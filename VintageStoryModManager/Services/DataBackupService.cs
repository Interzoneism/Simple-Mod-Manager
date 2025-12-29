using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VintageStoryModManager.Services;

public sealed class DataBackupService
{
    private const string DataDirectoryName = "Data";
    private const string SavesFolderName = "Saves";
    private const string CacheFolderName = "Cache";
    private const string DirectoryStoreSuffix = ".dir";
    private const string FileStoreSuffix = ".file";

    private readonly string _backupRootDirectory;
    private readonly string _savesStoreDirectory;

    public DataBackupService(string configurationDirectory, string? customBackupLocation = null)
    {
        if (string.IsNullOrWhiteSpace(configurationDirectory))
            throw new ArgumentException("Configuration directory is required.", nameof(configurationDirectory));

        if (!string.IsNullOrWhiteSpace(customBackupLocation))
        {
            _backupRootDirectory = customBackupLocation;
        }
        else
        {
            _backupRootDirectory = Path.Combine(configurationDirectory, DevConfig.DataFolderBackupDirectoryName);
        }
        _savesStoreDirectory = Path.Combine(_backupRootDirectory, DevConfig.DataFolderBackupSaveStoreName);
    }

    public Task<DataBackupResult> CreateBackupAsync(
        string dataDirectory,
        string? vintageStoryVersion,
        IProgress<DataBackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => CreateBackupInternal(dataDirectory, vintageStoryVersion, progress, cancellationToken),
            cancellationToken);
    }

    public Task RestoreBackupAsync(
        DataBackupSummary summary,
        string dataDirectory,
        IProgress<DataBackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (summary is null) throw new ArgumentNullException(nameof(summary));
        return Task.Run(() => RestoreBackupInternal(summary, dataDirectory, progress, cancellationToken), cancellationToken);
    }

    public IReadOnlyList<DataBackupSummary> GetAvailableBackups()
    {
        if (!Directory.Exists(_backupRootDirectory)) return Array.Empty<DataBackupSummary>();

        var summaries = new List<DataBackupSummary>();

        foreach (var directory in Directory.EnumerateDirectories(_backupRootDirectory))
        {
            var manifest = TryLoadManifest(directory);
            if (manifest is null || manifest.CreatedOnUtc == default) continue;

            summaries.Add(new DataBackupSummary(
                manifest.Id ?? Path.GetFileName(directory),
                manifest.CreatedOnUtc,
                directory,
                manifest.SourceDataDirectory,
                manifest.VintageStoryVersion));
        }

        return summaries
            .OrderByDescending(summary => summary.CreatedOnUtc)
            .ToArray();
    }

    public int DeleteBackups(string dataDirectory, string vintageStoryVersion)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("A data directory must be provided.", nameof(dataDirectory));

        if (string.IsNullOrWhiteSpace(vintageStoryVersion))
            throw new ArgumentException("Vintage Story version is required.", nameof(vintageStoryVersion));

        var normalizedSource = NormalizeDirectoryPath(dataDirectory)
                               ?? throw new ArgumentException("A valid data directory must be provided.", nameof(dataDirectory));
        var normalizedVersion = VersionStringUtility.Normalize(vintageStoryVersion);
        if (string.IsNullOrWhiteSpace(normalizedVersion))
            throw new ArgumentException("A valid Vintage Story version must be provided.", nameof(vintageStoryVersion));

        if (!Directory.Exists(_backupRootDirectory)) return 0;

        var deleted = 0;
        foreach (var directory in Directory.EnumerateDirectories(_backupRootDirectory))
        {
            var manifest = TryLoadManifest(directory);
            if (manifest is null) continue;
            if (!DirectoriesMatch(manifest.SourceDataDirectory, normalizedSource)) continue;
            if (!VersionsMatch(manifest.VintageStoryVersion, normalizedVersion)) continue;

            TryDeleteDirectory(directory);
            deleted++;
        }

        if (deleted > 0) CleanupSaveStore();
        return deleted;
    }

    public string GetBackupRootDirectory()
    {
        return _backupRootDirectory;
    }

    private DataBackupResult CreateBackupInternal(
        string dataDirectory,
        string? vintageStoryVersion,
        IProgress<DataBackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("A data directory must be provided.", nameof(dataDirectory));

        if (!Directory.Exists(dataDirectory))
            throw new DirectoryNotFoundException($"The data directory {dataDirectory} does not exist.");

        Directory.CreateDirectory(_backupRootDirectory);
        Directory.CreateDirectory(_savesStoreDirectory);

        var timestampUtc = DateTime.UtcNow;
        var identifier = GenerateBackupIdentifier(timestampUtc);
        var backupDirectory = EnsureUniqueDirectory(Path.Combine(_backupRootDirectory, identifier));
        var dataTargetDirectory = Path.Combine(backupDirectory, DataDirectoryName);

        progress?.Report(new DataBackupProgress(0, "Scanning VintagestoryData..."));
        var plan = BuildBackupPlan(dataDirectory, dataTargetDirectory, cancellationToken);

        var normalizedSourceDirectory = NormalizeDirectoryPath(dataDirectory);
        var normalizedVersion = VersionStringUtility.Normalize(vintageStoryVersion);

        var manifest = new BackupManifest
        {
            Id = identifier,
            CreatedOnUtc = timestampUtc,
            SourceDataDirectory = normalizedSourceDirectory ?? dataDirectory,
            VintageStoryVersion = normalizedVersion ?? vintageStoryVersion
        };

        try
        {
            ExecuteBackupPlan(plan, manifest, progress, cancellationToken);
            WriteManifest(Path.Combine(backupDirectory, DevConfig.DataFolderBackupManifestFileName), manifest);
            CleanupSaveStore();
            progress?.Report(new DataBackupProgress(100, "Backup completed."));
            return new DataBackupResult(identifier, timestampUtc, backupDirectory);
        }
        catch
        {
            TryDeleteDirectory(backupDirectory);
            throw;
        }
    }

    private void RestoreBackupInternal(
        DataBackupSummary summary,
        string dataDirectory,
        IProgress<DataBackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("A destination data directory must be provided.", nameof(dataDirectory));

        if (!Directory.Exists(summary.DirectoryPath))
            throw new DirectoryNotFoundException($"The backup directory {summary.DirectoryPath} could not be found.");

        var manifest = TryLoadManifest(summary.DirectoryPath)
                       ?? throw new InvalidOperationException("The selected backup is missing required metadata.");

        var backupDataDirectory = Path.Combine(summary.DirectoryPath, DataDirectoryName);
        if (!Directory.Exists(backupDataDirectory))
            throw new InvalidOperationException("The selected backup does not include any data to restore.");

        Directory.CreateDirectory(dataDirectory);

        progress?.Report(new DataBackupProgress(0, "Preparing to restore backup..."));

        RemoveExistingData(dataDirectory);

        var savesBytes = manifest.Saves.Sum(save => Math.Max(1L, save.Size));
        var totalBytes = CalculateDirectorySize(backupDataDirectory) + savesBytes;
        if (totalBytes <= 0) totalBytes = 1;
        long restoredBytes = 0;

        CopyDirectory(
            backupDataDirectory,
            dataDirectory,
            cancellationToken,
            (relativePath, length) =>
            {
                restoredBytes += Math.Max(1L, length);
                var percent = restoredBytes * 100d / totalBytes;
                progress?.Report(new DataBackupProgress(percent, $"Restoring {relativePath}"));
            });

        if (manifest.Saves.Count > 0)
        {
            var savesTargetDirectory = Path.Combine(dataDirectory, SavesFolderName);
            foreach (var save in manifest.Saves)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var storePath = GetSaveStorePath(save.Hash, save.IsDirectory);
                if (save.IsDirectory)
                {
                    if (!Directory.Exists(storePath))
                        throw new InvalidOperationException($"The save archive for {save.RelativePath} is missing.");

                    CopyDirectory(
                        storePath,
                        Path.Combine(savesTargetDirectory, save.RelativePath),
                        cancellationToken,
                        (_, length) => { restoredBytes += Math.Max(1L, length); });
                }
                else
                {
                    if (!File.Exists(storePath))
                        throw new InvalidOperationException($"The save archive for {save.RelativePath} is missing.");

                    CopyFile(storePath, Path.Combine(savesTargetDirectory, save.RelativePath), cancellationToken);
                    restoredBytes += Math.Max(1L, save.Size);
                }

                var percent = restoredBytes * 100d / totalBytes;
                progress?.Report(new DataBackupProgress(percent, $"Restoring save {save.RelativePath}"));
            }
        }

        RemoveCacheDirectory(dataDirectory);
        progress?.Report(new DataBackupProgress(100, "Restore completed."));
    }

    private static void RemoveExistingData(string dataDirectory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(dataDirectory))
        {
            var name = Path.GetFileName(entry);
            if (string.Equals(name, CacheFolderName, StringComparison.OrdinalIgnoreCase))
            {
                RemoveCacheDirectory(dataDirectory);
                continue;
            }

            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, true);
            }
            else if (File.Exists(entry))
            {
                File.Delete(entry);
            }
        }
    }

    private static void RemoveCacheDirectory(string dataDirectory)
    {
        var cachePath = Path.Combine(dataDirectory, CacheFolderName);
        if (!Directory.Exists(cachePath)) return;
        Directory.Delete(cachePath, true);
    }

    private void ExecuteBackupPlan(
        BackupPlan plan,
        BackupManifest manifest,
        IProgress<DataBackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var directory in plan.DirectoriesToCreate)
        {
            Directory.CreateDirectory(directory);
        }

        var totalBytes = Math.Max(plan.TotalBytes, 1L);
        long copiedBytes = 0;

        foreach (var file in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyFile(file.SourcePath, file.DestinationPath, cancellationToken);
            copiedBytes += Math.Max(1L, file.Length);
            var percent = copiedBytes * 100d / totalBytes;
            progress?.Report(new DataBackupProgress(percent, $"Copying {Path.GetFileName(file.SourcePath)}"));
        }

        if (plan.Saves.Count > 0)
        {
            Directory.CreateDirectory(Path.Combine(plan.DataTargetDirectory, SavesFolderName));
        }

        foreach (var save in plan.Saves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var storePath = GetSaveStorePath(save.Hash, save.IsDirectory);
            if (!save.ExistsInStore)
            {
                if (save.IsDirectory)
                {
                    CopyDirectory(save.SourcePath, storePath, cancellationToken);
                }
                else
                {
                    CopyFile(save.SourcePath, storePath, cancellationToken);
                }

                copiedBytes += Math.Max(1L, save.Size);
            }

            manifest.Saves.Add(new SaveReference
            {
                RelativePath = save.RelativePath,
                Hash = save.Hash,
                IsDirectory = save.IsDirectory,
                Size = save.Size
            });

            var percent = copiedBytes * 100d / totalBytes;
            progress?.Report(new DataBackupProgress(percent, $"Archiving save {save.RelativePath}"));
        }
    }

    private BackupPlan BuildBackupPlan(string sourceDirectory, string targetDirectory, CancellationToken cancellationToken)
    {
        var plan = new BackupPlan(targetDirectory);
        plan.DirectoriesToCreate.Add(targetDirectory);

        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entry);
            if (string.Equals(name, CacheFolderName, StringComparison.OrdinalIgnoreCase)) continue;

            if (Directory.Exists(entry) && string.Equals(name, SavesFolderName, StringComparison.OrdinalIgnoreCase))
            {
                BuildSavesPlan(entry, Path.Combine(targetDirectory, SavesFolderName), plan, cancellationToken);
                continue;
            }

            BuildGeneralPlan(entry, Path.Combine(targetDirectory, name), plan, cancellationToken);
        }

        return plan;
    }

    private void BuildSavesPlan(string sourceDirectory, string targetDirectory, BackupPlan plan, CancellationToken cancellationToken)
    {
        plan.DirectoriesToCreate.Add(targetDirectory);
        if (!Directory.Exists(sourceDirectory)) return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceDirectory, entry);
            var isDirectory = Directory.Exists(entry);
            var hash = ComputeSaveHash(entry, isDirectory, cancellationToken);
            var storePath = GetSaveStorePath(hash, isDirectory);
            var existsInStore = isDirectory ? Directory.Exists(storePath) : File.Exists(storePath);
            var size = isDirectory ? CalculateDirectorySize(entry) : GetFileLength(entry);
            if (!existsInStore) plan.TotalBytes += Math.Max(1L, size);
            plan.Saves.Add(new SavePlanEntry(entry, relativePath, hash, isDirectory, existsInStore, size));
        }
    }

    private void BuildGeneralPlan(string sourcePath, string targetPath, BackupPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(sourcePath))
        {
            plan.DirectoriesToCreate.Add(targetPath);
            foreach (var entry in Directory.EnumerateFileSystemEntries(sourcePath))
            {
                var childName = Path.GetFileName(entry);
                BuildGeneralPlan(entry, Path.Combine(targetPath, childName), plan, cancellationToken);
            }
        }
        else if (File.Exists(sourcePath))
        {
            var length = GetFileLength(sourcePath);
            plan.Files.Add(new FileCopyPlanEntry(sourcePath, targetPath, length));
            plan.TotalBytes += Math.Max(1L, length);
        }
    }

    private static long CalculateDirectorySize(string directory)
    {
        if (!Directory.Exists(directory)) return 0;

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            total += GetFileLength(file);
        }

        return total;
    }

    private static string ComputeSaveHash(string path, bool isDirectory, CancellationToken cancellationToken)
    {
        if (!isDirectory)
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash);
        }

        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                         .OrderBy(dir => dir, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = NormalizeRelativePath(Path.GetRelativePath(path, directory));
                AppendText(incremental, $"D:{relative}");
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                         .OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = NormalizeRelativePath(Path.GetRelativePath(path, file));
                AppendText(incremental, $"F:{relative}");

                using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    incremental.AppendData(buffer, 0, read);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexString(incremental.GetHashAndReset());
    }

    private static void AppendText(IncrementalHash hash, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        hash.AppendData(bytes);
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static void CopyFile(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        using var input = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = File.Open(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void CopyDirectory(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken,
        Action<string, long>? onFileCopied = null)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var destinationPath = Path.Combine(destinationDirectory, relative);
            CopyFile(file, destinationPath, cancellationToken);
            onFileCopied?.Invoke(relative, GetFileLength(file));
        }
    }

    private static long GetFileLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Length;
        }
        catch
        {
            return 0;
        }
    }

    private string GenerateBackupIdentifier(DateTime timestampUtc)
    {
        var localTimestamp = timestampUtc.ToLocalTime();
        var dateStamp = localTimestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var prefix = $"{dateStamp}-";
        var highestSuffix = 0;

        if (Directory.Exists(_backupRootDirectory))
        {
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(_backupRootDirectory, $"{prefix}*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(directory);
                    if (string.IsNullOrWhiteSpace(name) || name.Length <= prefix.Length) continue;

                    var suffix = name[prefix.Length..];
                    var underscoreIndex = suffix.IndexOf('_');
                    if (underscoreIndex >= 0) suffix = suffix[..underscoreIndex];

                    if (!int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) continue;

                    if (value > highestSuffix) highestSuffix = value;
                }
            }
            catch
            {
                // If enumeration fails, fall back to the base identifier.
            }
        }

        var nextSuffix = highestSuffix + 1;
        return $"{prefix}{nextSuffix:000}";
    }

    private static string EnsureUniqueDirectory(string candidate)
    {
        if (!Directory.Exists(candidate)) return candidate;

        var counter = 1;
        string path;
        do
        {
            path = $"{candidate}_{counter++}";
        } while (Directory.Exists(path));

        return path;
    }

    private static void WriteManifest(string manifestPath, BackupManifest manifest)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        var directory = Path.GetDirectoryName(manifestPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, options));
    }

    private BackupManifest? TryLoadManifest(string directory)
    {
        try
        {
            var manifestPath = Path.Combine(directory, DevConfig.DataFolderBackupManifestFileName);
            if (!File.Exists(manifestPath)) return null;
            var json = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<BackupManifest>(json);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void CleanupSaveStore()
    {
        if (!Directory.Exists(_savesStoreDirectory)) return;

        var usedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(_backupRootDirectory))
        {
            foreach (var directory in Directory.EnumerateDirectories(_backupRootDirectory))
            {
                var manifest = TryLoadManifest(directory);
                if (manifest is null) continue;
                foreach (var save in manifest.Saves)
                {
                    if (!string.IsNullOrWhiteSpace(save.Hash)) usedHashes.Add(save.Hash);
                }
            }
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(_savesStoreDirectory))
        {
            var hash = ExtractHashFromStoreEntry(entry);
            if (hash is null) continue;
            if (usedHashes.Contains(hash)) continue;
            TryDeleteEntry(entry);
        }
    }

    private static string? ExtractHashFromStoreEntry(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.EndsWith(DirectoryStoreSuffix, StringComparison.OrdinalIgnoreCase))
            return fileName[..^DirectoryStoreSuffix.Length];
        if (fileName.EndsWith(FileStoreSuffix, StringComparison.OrdinalIgnoreCase))
            return fileName[..^FileStoreSuffix.Length];
        return null;
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (IOException ex)
        {
            Trace.TraceWarning("Failed to delete directory {0}: {1}", path, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            Trace.TraceWarning("Failed to delete directory {0}: {1}", path, ex.Message);
        }
    }

    private static void TryDeleteEntry(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            Trace.TraceWarning("Failed to delete backup store entry {0}: {1}", path, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            Trace.TraceWarning("Failed to delete backup store entry {0}: {1}", path, ex.Message);
        }
    }

    private string GetSaveStorePath(string hash, bool isDirectory)
    {
        var suffix = isDirectory ? DirectoryStoreSuffix : FileStoreSuffix;
        return Path.Combine(_savesStoreDirectory, hash + suffix);
    }

    private static string? NormalizeDirectoryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return null;
        }
    }

    private static bool DirectoriesMatch(string? candidate, string normalizedTarget)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        var normalizedCandidate = NormalizeDirectoryPath(candidate);
        if (normalizedCandidate is null) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(normalizedCandidate, normalizedTarget, comparison);
    }

    private static bool VersionsMatch(string? candidate, string normalizedTarget)
    {
        var normalizedCandidate = VersionStringUtility.Normalize(candidate);
        if (string.IsNullOrWhiteSpace(normalizedCandidate)) return false;
        return string.Equals(normalizedCandidate, normalizedTarget, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class BackupPlan
    {
        public BackupPlan(string dataTargetDirectory)
        {
            DataTargetDirectory = dataTargetDirectory;
        }

        public string DataTargetDirectory { get; }
        public List<string> DirectoriesToCreate { get; } = new();
        public List<FileCopyPlanEntry> Files { get; } = new();
        public List<SavePlanEntry> Saves { get; } = new();
        public long TotalBytes { get; set; }
    }

    private sealed record FileCopyPlanEntry(string SourcePath, string DestinationPath, long Length);

    private sealed record SavePlanEntry(
        string SourcePath,
        string RelativePath,
        string Hash,
        bool IsDirectory,
        bool ExistsInStore,
        long Size);

    private sealed class BackupManifest
    {
        public string? Id { get; set; }
        public DateTime CreatedOnUtc { get; set; }
        public string? SourceDataDirectory { get; set; }
        public string? VintageStoryVersion { get; set; }
        public List<SaveReference> Saves { get; set; } = new();
    }

    private sealed class SaveReference
    {
        public string RelativePath { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
    }
}

public readonly record struct DataBackupProgress(double Percent, string Status);

public sealed record DataBackupSummary(
    string Id,
    DateTime CreatedOnUtc,
    string DirectoryPath,
    string? SourceDataDirectory,
    string? VintageStoryVersion);

public sealed record DataBackupResult(string Id, DateTime CreatedOnUtc, string DirectoryPath);
