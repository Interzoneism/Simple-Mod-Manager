using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace VintageStoryModManager.Services;

/// <summary>
///     Downloads and installs mod updates retrieved from the official mod database.
/// </summary>
public sealed class ModUpdateService
{
    private static readonly HttpClient HttpClient = new();

    public async Task<ModUpdateResult> UpdateAsync(
        ModUpdateDescriptor descriptor,
        bool cacheDownloads,
        IProgress<ModUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (descriptor is null) throw new ArgumentNullException(nameof(descriptor));

        try
        {
            ReportProgress(progress, ModUpdateStage.Downloading, "Downloading update package...");
            var download = await DownloadAsync(descriptor, cancellationToken).ConfigureAwait(false);

            try
            {
                ReportProgress(progress, ModUpdateStage.Validating, "Validating archive...");
                ValidateArchive(download.Path);

                if (cacheDownloads && !download.IsCacheHit && download.CachePath != null)
                    TryCacheDownload(download.Path, download.CachePath);

                var treatAsDirectory = descriptor.TargetIsDirectory;
                using var installScope =
                    StatusLogService.BeginDebugScope(descriptor.DisplayName, descriptor.ModId, "install");
                if (installScope != null)
                {
                    installScope.SetDetail("mode", treatAsDirectory ? "dir" : "file");
                    installScope.SetCacheStatus(download.IsCacheHit);
                }

                var result =
                    await InstallAsync(descriptor, download.Path, treatAsDirectory, progress, cancellationToken)
                        .ConfigureAwait(false);
                installScope?.SetDetail("success", result.Success ? "y" : "n");
                return result;
            }
            finally
            {
                if (download.IsTemporary) TryDelete(download.Path);
            }
        }
        catch (OperationCanceledException)
        {
            Trace.TraceWarning("Mod update cancelled for {0}", descriptor.TargetPath);
            throw;
        }
        catch (InternetAccessDisabledException ex)
        {
            Trace.TraceWarning("Mod update blocked for {0}: {1}", descriptor.TargetPath, ex.Message);
            return new ModUpdateResult(false,
                "Internet access is disabled. Enable Internet Access in the File menu to download updates.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or HttpRequestException or NotSupportedException)
        {
            Trace.TraceError("Mod update failed for {0}: {1}", descriptor.TargetPath, ex);
            return new ModUpdateResult(false, ex.Message);
        }
    }

    private static async Task<DownloadResult> DownloadAsync(ModUpdateDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        using var logScope = StatusLogService.BeginDebugScope(descriptor.DisplayName, descriptor.ModId, "download");
        var fileName = descriptor.ReleaseFileName;
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = descriptor.TargetIsDirectory
                ? descriptor.ModId + ".zip"
                : Path.GetFileName(descriptor.TargetPath);

        if (string.IsNullOrWhiteSpace(fileName)) fileName = descriptor.ModId + ".zip";

        var cachePath = ModCacheLocator.GetModCachePath(descriptor.ModId, descriptor.ReleaseVersion, fileName);
        if (cachePath != null)
            ModCacheLocator.TryPromoteLegacyCacheFile(descriptor.ModId, descriptor.ReleaseVersion, fileName, cachePath);

        if (ModCacheLocator.TryLocateCachedModFile(descriptor.ModId, descriptor.ReleaseVersion, fileName,
                out var existingCachePath)
            && existingCachePath is not null)
        {
            if (logScope != null)
            {
                logScope.SetCacheStatus(true);
                logScope.SetDetail("src", "cache");
                try
                {
                    var bytes = new FileInfo(existingCachePath).Length;
                    logScope.SetDetail("bytes", bytes);
                }
                catch (IOException)
                {
                    // Ignore file size failures.
                }
                catch (UnauthorizedAccessException)
                {
                    // Ignore file size failures.
                }
            }

            return new DownloadResult(
                existingCachePath,
                false,
                cachePath ?? existingCachePath,
                true);
        }

        var tempDirectory = CreateTemporaryDirectory();
        var downloadPath = Path.Combine(tempDirectory, fileName);

        logScope?.SetCacheStatus(false);

        if (descriptor.DownloadUri.IsFile)
        {
            var sourcePath = descriptor.DownloadUri.LocalPath;
            await using var sourceStream = File.OpenRead(sourcePath);
            await using var destination = File.Create(downloadPath);
            await sourceStream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            logScope?.SetDetail("src", "file");
        }
        else
        {
            InternetAccessManager.ThrowIfInternetAccessDisabled();

            using HttpRequestMessage request = new(HttpMethod.Get, descriptor.DownloadUri);
            using var response = await HttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = File.Create(downloadPath);
            await stream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            logScope?.SetDetail("src", "net");
        }

        if (logScope != null)
            try
            {
                var bytes = new FileInfo(downloadPath).Length;
                logScope.SetDetail("bytes", bytes);
            }
            catch (IOException)
            {
                // Ignore file size failures.
            }
            catch (UnauthorizedAccessException)
            {
                // Ignore file size failures.
            }

        return new DownloadResult(downloadPath, true, cachePath, false);
    }

    private static void ValidateArchive(string downloadPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(downloadPath);
            foreach (var entry in archive.Entries)
                if (string.Equals(Path.GetFileName(entry.FullName), "modinfo.json", StringComparison.OrdinalIgnoreCase))
                    return;
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("The downloaded file is not a valid Vintage Story mod archive.", ex);
        }

        throw new InvalidDataException("The downloaded file does not contain a modinfo.json manifest.");
    }

    private static Task<ModUpdateResult> InstallAsync(ModUpdateDescriptor descriptor, string downloadPath,
        bool treatAsDirectory, IProgress<ModUpdateProgress>? progress, CancellationToken cancellationToken)
    {
        var targetPath = descriptor.TargetPath;

        var targetExists = treatAsDirectory ? Directory.Exists(targetPath) : File.Exists(targetPath);
        var isFreshInstall = string.IsNullOrWhiteSpace(descriptor.InstalledVersion) || !targetExists;
        var completionMessage = isFreshInstall ? "Mod installed." : "Update installed.";

        if (treatAsDirectory)
        {
            ReportProgress(progress, ModUpdateStage.Preparing, "Preparing extracted files...");
            var result = InstallToDirectory(
                descriptor,
                targetPath,
                downloadPath,
                completionMessage,
                progress,
                cancellationToken);
            return Task.FromResult(result);
        }

        var replaceMessage = isFreshInstall ? "Installing mod archive..." : "Replacing mod archive...";
        ReportProgress(progress, ModUpdateStage.Replacing, replaceMessage);
        InstallToFile(descriptor, downloadPath);
        ReportProgress(progress, ModUpdateStage.Completed, completionMessage);
        return Task.FromResult(new ModUpdateResult(true, null));
    }

    private static ModUpdateResult InstallToDirectory(ModUpdateDescriptor descriptor, string targetDirectory,
        string downloadPath, string completionMessage, IProgress<ModUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var backupPath = CreateUniquePath(targetDirectory, ".immbackup");
        var extractDirectory = CreateTemporaryDirectory();

        var backupMoved = false;

        try
        {
            if (Directory.Exists(targetDirectory))
            {
                if (Directory.Exists(backupPath)) Directory.Delete(backupPath, true);

                Directory.Move(targetDirectory, backupPath);
                backupMoved = true;

                TryCacheDirectoryBackup(descriptor, backupPath);
            }

            ZipFile.ExtractToDirectory(downloadPath, extractDirectory);

            var payloadRoot = DeterminePayloadRoot(extractDirectory);
            CopyDirectory(payloadRoot, targetDirectory, cancellationToken);
            ReportProgress(progress, ModUpdateStage.Completed, completionMessage);

            TryDelete(backupPath);
            return new ModUpdateResult(true, null);
        }
        catch (OperationCanceledException)
        {
            TryDelete(targetDirectory);
            if (backupMoved) TryRestoreDirectoryBackup(backupPath, targetDirectory);

            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or NotSupportedException)
        {
            TryDelete(targetDirectory);
            if (backupMoved) TryRestoreDirectoryBackup(backupPath, targetDirectory);

            return new ModUpdateResult(false, ex.Message);
        }
        catch
        {
            TryDelete(targetDirectory);
            if (backupMoved) TryRestoreDirectoryBackup(backupPath, targetDirectory);

            throw;
        }
        finally
        {
            TryDelete(extractDirectory);
        }
    }

    private static void InstallToFile(ModUpdateDescriptor descriptor, string downloadPath)
    {
        var targetPath = descriptor.TargetPath;
        var existingPath = string.IsNullOrWhiteSpace(descriptor.ExistingPath)
            ? targetPath
            : descriptor.ExistingPath!;

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var backupPath = CreateUniquePath(existingPath, ".immbackup");
        string? cachedBackupPath = null;

        if (File.Exists(existingPath))
        {
            if (File.Exists(backupPath)) File.Delete(backupPath);

            File.Move(existingPath, backupPath);

            cachedBackupPath = TryMoveBackupToCache(descriptor, backupPath);
        }

        try
        {
            if (File.Exists(targetPath)) File.Delete(targetPath);

            File.Copy(downloadPath, targetPath, true);

            if (!string.Equals(existingPath, targetPath, StringComparison.OrdinalIgnoreCase)) TryDelete(existingPath);

            if (cachedBackupPath is null) TryDelete(backupPath);
        }
        catch (Exception)
        {
            TryDelete(targetPath);

            if (!string.Equals(existingPath, targetPath, StringComparison.OrdinalIgnoreCase)) TryDelete(existingPath);

            if (cachedBackupPath is not null && File.Exists(cachedBackupPath))
            {
                try
                {
                    File.Copy(cachedBackupPath, existingPath, true);
                }
                catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException
                                                   or NotSupportedException)
                {
                    Trace.TraceWarning("Failed to restore mod from cache {0}: {1}", cachedBackupPath, copyEx.Message);
                    try
                    {
                        File.Move(cachedBackupPath, existingPath);
                        cachedBackupPath = null;
                    }
                    catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException
                                                       or NotSupportedException)
                    {
                        Trace.TraceWarning("Failed to move cached mod back to {0}: {1}", existingPath, moveEx.Message);
                    }
                }
            }
            else if (File.Exists(backupPath))
            {
                TryDelete(existingPath);
                File.Move(backupPath, existingPath);
            }

            throw;
        }
        finally
        {
            if (cachedBackupPath is null) TryDelete(backupPath);
        }
    }

    private static string DeterminePayloadRoot(string extractDirectory)
    {
        var directories = Directory.GetDirectories(extractDirectory, "*", SearchOption.TopDirectoryOnly);
        var files = Directory.GetFiles(extractDirectory, "*", SearchOption.TopDirectoryOnly);

        if (directories.Length == 1 && files.Length == 0) return directories[0];

        return extractDirectory;
    }

    private static string? TryMoveBackupToCache(ModUpdateDescriptor descriptor, string backupPath)
    {
        if (string.IsNullOrWhiteSpace(descriptor.InstalledVersion)) return null;

        if (!File.Exists(backupPath)) return null;

        var targetFileName = descriptor.ExistingPath is not null
            ? Path.GetFileName(descriptor.ExistingPath)
            : null;
        if (string.IsNullOrWhiteSpace(targetFileName)) targetFileName = Path.GetFileName(descriptor.TargetPath);
        if (string.IsNullOrWhiteSpace(targetFileName)) targetFileName = descriptor.ReleaseFileName;

        var cachePath = ModCacheLocator.GetModCachePath(descriptor.ModId, descriptor.InstalledVersion, targetFileName);
        if (cachePath is null) return null;

        if (ModCacheLocator.TryPromoteLegacyCacheFile(descriptor.ModId, descriptor.InstalledVersion, targetFileName,
                cachePath)
            && File.Exists(cachePath))
            return null;

        if (File.Exists(cachePath)) return null;

        if (ModCacheLocator.TryLocateCachedModFile(descriptor.ModId, descriptor.InstalledVersion, targetFileName,
                out var existingCacheFile)
            && existingCacheFile is not null)
        {
            var existingDirectory = Path.GetDirectoryName(existingCacheFile);
            var cacheDirectory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(existingDirectory)
                && !string.IsNullOrWhiteSpace(cacheDirectory)
                && string.Equals(existingDirectory, cacheDirectory, StringComparison.OrdinalIgnoreCase))
                return null;
        }

        try
        {
            var cacheDirectory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(cacheDirectory)) Directory.CreateDirectory(cacheDirectory);

            File.Move(backupPath, cachePath);
            return cachePath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Trace.TraceWarning("Failed to cache existing mod version {0}: {1}", cachePath, ex.Message);

            try
            {
                if (!File.Exists(backupPath) && File.Exists(cachePath)) File.Move(cachePath, backupPath);
            }
            catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException
                                                  or NotSupportedException)
            {
                Trace.TraceWarning("Failed to restore backup after cache failure {0}: {1}", cachePath,
                    restoreEx.Message);
            }

            return null;
        }
    }

    private static void TryCacheDirectoryBackup(ModUpdateDescriptor descriptor, string backupPath)
    {
        if (string.IsNullOrWhiteSpace(descriptor.InstalledVersion)) return;

        if (!Directory.Exists(backupPath)) return;

        var cacheFileName = descriptor.ReleaseFileName ?? Path.GetFileName(descriptor.TargetPath);
        var cachePath = ModCacheLocator.GetModCachePath(descriptor.ModId, descriptor.InstalledVersion, cacheFileName);

        if (cachePath is null) return;

        if (ModCacheLocator.TryPromoteLegacyCacheFile(descriptor.ModId, descriptor.InstalledVersion, cacheFileName,
                cachePath)
            && File.Exists(cachePath))
            return;

        if (File.Exists(cachePath)) return;

        if (ModCacheLocator.TryLocateCachedModFile(descriptor.ModId, descriptor.InstalledVersion, cacheFileName,
                out var existingCacheFile)
            && existingCacheFile is not null)
        {
            var existingDirectory = Path.GetDirectoryName(existingCacheFile);
            var cacheDirectory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(existingDirectory)
                && !string.IsNullOrWhiteSpace(cacheDirectory)
                && string.Equals(existingDirectory, cacheDirectory, StringComparison.OrdinalIgnoreCase))
                return;
        }

        var tempDirectory = CreateTemporaryDirectory();
        var tempArchive = Path.Combine(tempDirectory, Path.GetFileName(cachePath));

        try
        {
            var cacheDirectory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(cacheDirectory)) Directory.CreateDirectory(cacheDirectory);

            ZipFile.CreateFromDirectory(backupPath, tempArchive, CompressionLevel.Optimal, false);

            if (File.Exists(cachePath)) File.Delete(cachePath);

            File.Move(tempArchive, cachePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or InvalidDataException)
        {
            Trace.TraceWarning("Failed to cache directory mod backup {0}: {1}", backupPath, ex.Message);
            TryDelete(tempArchive);
        }
        finally
        {
            TryDelete(tempDirectory);
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(sourceDirectory);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            var relative = Path.GetRelativePath(sourceDirectory, current);
            var target = relative == "." ? destinationDirectory : Path.Combine(destinationDirectory, relative);

            Directory.CreateDirectory(target);

            foreach (var file in Directory.GetFiles(current))
            {
                var fileRelative = Path.GetRelativePath(sourceDirectory, file);
                var targetFile = Path.Combine(destinationDirectory, fileRelative);
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                File.Copy(file, targetFile, true);
            }

            foreach (var directory in Directory.GetDirectories(current)) pending.Push(directory);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "IMM", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateUniquePath(string basePath, string suffix)
    {
        var candidate = basePath + suffix;
        var counter = 1;
        while (File.Exists(candidate) || Directory.Exists(candidate)) candidate = basePath + suffix + counter++;

        return candidate;
    }

    private static void TryCacheDownload(string sourcePath, string cachePath)
    {
        try
        {
            if (string.Equals(sourcePath, cachePath, StringComparison.OrdinalIgnoreCase)) return;

            var directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            File.Copy(sourcePath, cachePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Trace.TraceWarning("Failed to cache mod archive {0}: {1}", cachePath, ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Failed to delete temporary resource {0}: {1}", path, ex.Message);
        }
    }

    private static void TryRestoreDirectoryBackup(string backupPath, string targetPath)
    {
        if (!Directory.Exists(backupPath)) return;

        try
        {
            if (Directory.Exists(targetPath)) Directory.Delete(targetPath, true);

            Directory.Move(backupPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Failed to restore mod directory backup {0}: {1}", backupPath, ex.Message);
        }
    }

    private static void ReportProgress(IProgress<ModUpdateProgress>? progress, ModUpdateStage stage, string message)
    {
        progress?.Report(new ModUpdateProgress(stage, message));
    }

    private sealed record DownloadResult(string Path, bool IsTemporary, string? CachePath, bool IsCacheHit);
}

public sealed record ModUpdateDescriptor(
    string ModId,
    string DisplayName,
    Uri DownloadUri,
    string TargetPath,
    bool TargetIsDirectory,
    string? ReleaseFileName,
    string? ReleaseVersion,
    string? InstalledVersion)
{
    public string? ExistingPath { get; init; }
}

public sealed record ModUpdateResult(bool Success, string? ErrorMessage);

public readonly record struct ModUpdateProgress(ModUpdateStage Stage, string Message);

public enum ModUpdateStage
{
    Downloading,
    Validating,
    Preparing,
    Replacing,
    Completed
}