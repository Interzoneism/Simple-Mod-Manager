using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using VintageStoryModManager.Models;
using Application = System.Windows.Application;

namespace VintageStoryModManager.Services;

/// <summary>
///     Discovers installed mods and reads their metadata in the same way as the game.
/// </summary>
public sealed class ModDiscoveryService
{
    private static readonly int DiscoveryBatchSize = DevConfig.ModDiscoveryBatchSize;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly char[] PathSeparators = new[]
    {
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar
    };

    private static readonly string[] DefaultIconRelativePaths =
    {
        Path.Combine("assets", "game", "textures", "gui", "3rdpartymodicon.png"),
        Path.Combine("game", "textures", "gui", "3rdpartymodicon.png")
    };

    private static readonly string GeneralLoadErrorMessage = DevConfig.ModDiscoveryGeneralLoadErrorMessage;
    private static readonly string DependencyErrorMessage = DevConfig.ModDiscoveryDependencyErrorMessage;
    private readonly object _defaultIconLock = new();
    private readonly object _directoryCacheLock = new();

    private readonly Dictionary<string, CachedDirectoryEntry> _directoryManifestCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ClientSettingsStore _settingsStore;
    private byte[]? _defaultIconBytes;
    private bool _defaultIconResolved;

    public ModDiscoveryService(ClientSettingsStore settingsStore)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    public IReadOnlyList<ModEntry> LoadMods()
    {
        var orderedEntries = CollectModSources();

        if (orderedEntries.Count == 0) return Array.Empty<ModEntry>();

        // Scale parallelism based on CPU cores: 1 for single-core, up to 16 for many-core systems
        var maxDegree = Math.Clamp(Environment.ProcessorCount, 1, 16);
        var collected = new ConcurrentBag<(int Order, ModEntry Entry)>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegree
        };

        Parallel.ForEach(orderedEntries.Chunk(DiscoveryBatchSize), options, batch =>
        {
            foreach (var (batchOrder, fileSystemInfo) in batch)
            {
                var entry = ProcessEntry(fileSystemInfo);
                if (entry != null) collected.Add((batchOrder, entry));
            }
        });

        var results = collected
            .OrderBy(tuple => tuple.Order)
            .Select(tuple => tuple.Entry)
            .ToList();

        ApplyLoadStatuses(results);
        return results;
    }

    public async IAsyncEnumerable<IReadOnlyList<ModEntry>> LoadModsIncrementallyAsync(
        int batchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0) batchSize = DiscoveryBatchSize;

        var orderedEntries = CollectModSources();
        if (orderedEntries.Count == 0) yield break;

        // Use a bounded channel to avoid unbounded buffering while keeping producers busy.
        // BoundedChannelFullMode.Wait ensures producers naturally throttle when the consumer
        // falls behind, which prevents excessive memory growth when thousands of mods are loaded.
        var maxDegree = Math.Clamp(Environment.ProcessorCount, 1, 16);
        var channelOptions = new BoundedChannelOptions(maxDegree * 2)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        };
        var channel = Channel.CreateBounded<(int Order, ModEntry? Entry)>(channelOptions);

        var producer = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(
                        orderedEntries.Chunk(DiscoveryBatchSize),
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = maxDegree,
                            CancellationToken = cancellationToken
                        },
                        async (batch, token) =>
                        {
                            foreach (var (order, source) in batch)
                            {
                                token.ThrowIfCancellationRequested();
                                var entry = ProcessEntry(source);
                                await channel.Writer.WriteAsync((order, entry), token).ConfigureAwait(false);
                            }
                        })
                    .ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, cancellationToken);

        var pending = new SortedDictionary<int, ModEntry?>();
        var nextOrder = 0;
        var batchBuffer = new List<ModEntry>(batchSize);

        try
        {
            await foreach (var (order, entry) in channel.Reader.ReadAllAsync(cancellationToken))
            {
                pending[order] = entry;

                while (pending.TryGetValue(nextOrder, out var next))
                {
                    pending.Remove(nextOrder);
                    if (next != null)
                    {
                        batchBuffer.Add(next);
                        if (batchBuffer.Count >= batchSize)
                        {
                            yield return batchBuffer.ToArray();
                            batchBuffer.Clear();
                        }
                    }

                    nextOrder++;
                }
            }

            if (batchBuffer.Count > 0) yield return batchBuffer.ToArray();
        }
        finally
        {
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    public IReadOnlyList<string> GetSearchPaths()
    {
        return BuildSearchPaths().ToArray();
    }

    public ModEntry? LoadModFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return null;
        }

        ModEntry? modEntry = null;

        if (Directory.Exists(fullPath))
        {
            modEntry = TryLoadFromDirectory(new DirectoryInfo(fullPath));
        }
        else if (File.Exists(fullPath))
        {
            var fileInfo = new FileInfo(fullPath);
            if (string.Equals(fileInfo.Extension, ".zip", StringComparison.OrdinalIgnoreCase))
                modEntry = TryLoadFromZip(fileInfo);
            else if (string.Equals(fileInfo.Extension, ".cs", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(fileInfo.Extension, ".dll", StringComparison.OrdinalIgnoreCase))
                modEntry = CreateUnsupportedCodeModEntry(fileInfo);
        }

        if (modEntry != null && IsBaseGameMod(modEntry.ModId)) return null;

        return modEntry;
    }

    private List<(int Order, FileSystemInfo Entry)> CollectModSources()
    {
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderedEntries = new List<(int Order, FileSystemInfo Entry)>();
        var order = 0;

        foreach (var searchPath in BuildSearchPaths())
        {
            if (!Directory.Exists(searchPath)) continue;

            if (IsDefaultGameModsDirectory(searchPath)) continue;

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(searchPath).EnumerateFileSystemInfos();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (!seenSources.Add(entry.FullName)) continue;

                orderedEntries.Add((order++, entry));
            }
        }

        return orderedEntries;
    }

    private ModEntry? ProcessEntry(FileSystemInfo entry)
    {
        var modEntry = entry switch
        {
            DirectoryInfo directory => TryLoadFromDirectory(directory),
            FileInfo file when string.Equals(file.Extension, ".zip", StringComparison.OrdinalIgnoreCase)
                => TryLoadFromZip(file),
            FileInfo file when string.Equals(file.Extension, ".cs", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(file.Extension, ".dll", StringComparison.OrdinalIgnoreCase)
                => CreateUnsupportedCodeModEntry(file),
            _ => null
        };

        if (modEntry != null && IsBaseGameMod(modEntry.ModId)) return null;

        return modEntry;
    }

    private static bool IsBaseGameMod(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId)) return false;

        return string.Equals(modId, "VSCreativeMod", StringComparison.OrdinalIgnoreCase)
               || string.Equals(modId, "VSEssentials", StringComparison.OrdinalIgnoreCase)
               || string.Equals(modId, "VSSurvivalMod", StringComparison.OrdinalIgnoreCase);
    }

    public void ApplyLoadStatuses(IList<ModEntry> mods)
    {
        ApplyLoadStatusesInternal(mods, mods, null);
    }

    public IReadOnlyCollection<ModEntry> ApplyLoadStatusesIncremental(
        IList<ModEntry> mods,
        ICollection<ModEntry>? changedMods,
        ICollection<string>? removedModIds = null)
    {
        return ApplyLoadStatusesInternal(mods, changedMods, removedModIds);
    }

    private IReadOnlyCollection<ModEntry> ApplyLoadStatusesInternal(
        IList<ModEntry> mods,
        ICollection<ModEntry>? changedMods,
        ICollection<string>? removedModIds)
    {
        if (mods == null || mods.Count == 0) return Array.Empty<ModEntry>();

        var hasChanges = (changedMods != null && changedMods.Count > 0)
                         || (removedModIds != null && removedModIds.Count > 0);

        if (!hasChanges) changedMods = mods;

        var modsById = new Dictionary<string, List<ModEntry>>(StringComparer.OrdinalIgnoreCase);
        var dependentsById = new Dictionary<string, HashSet<ModEntry>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            if (!string.IsNullOrWhiteSpace(mod.ModId))
            {
                if (!modsById.TryGetValue(mod.ModId, out var list))
                {
                    list = new List<ModEntry>();
                    modsById[mod.ModId] = list;
                }

                list.Add(mod);
            }

            if (mod.Dependencies.Count == 0) continue;

            foreach (var dependency in mod.Dependencies)
            {
                if (dependency.IsGameOrCoreDependency || string.IsNullOrWhiteSpace(dependency.ModId)) continue;

                if (!dependentsById.TryGetValue(dependency.ModId, out var dependents))
                {
                    dependents = new HashSet<ModEntry>();
                    dependentsById[dependency.ModId] = dependents;
                }

                dependents.Add(mod);
            }
        }

        if (modsById.Count == 0) return Array.Empty<ModEntry>();

        var availableMods = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in modsById)
        {
            ModEntry? primary = null;
            foreach (var candidate in pair.Value)
                if (primary == null && !candidate.HasErrors)
                    primary = candidate;

            primary ??= pair.Value[0];
            availableMods[pair.Key] = primary;
        }

        var affected = new HashSet<ModEntry>();

        if (changedMods != null)
            foreach (var mod in changedMods)
            {
                if (mod == null || string.IsNullOrWhiteSpace(mod.ModId)) continue;

                if (!modsById.TryGetValue(mod.ModId, out var sameId)) continue;

                foreach (var entry in sameId) affected.Add(entry);
            }

        if (removedModIds is { Count: > 0 })
            foreach (var modId in removedModIds)
            {
                if (string.IsNullOrWhiteSpace(modId)) continue;

                if (!modsById.TryGetValue(modId, out var sameId)) continue;

                foreach (var entry in sameId) affected.Add(entry);
            }

        var queue = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (changedMods != null)
            foreach (var mod in changedMods)
                if (!string.IsNullOrWhiteSpace(mod?.ModId))
                    queue.Enqueue(mod.ModId);

        if (removedModIds is { Count: > 0 })
            foreach (var modId in removedModIds)
                if (!string.IsNullOrWhiteSpace(modId))
                    queue.Enqueue(modId);

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            if (!seen.Add(currentId)) continue;

            if (!dependentsById.TryGetValue(currentId, out var dependents)) continue;

            foreach (var dependent in dependents)
                if (affected.Add(dependent) && !string.IsNullOrWhiteSpace(dependent.ModId))
                {
                    queue.Enqueue(dependent.ModId);

                    if (modsById.TryGetValue(dependent.ModId, out var sameId))
                        foreach (var entry in sameId)
                            affected.Add(entry);
                }
        }

        if (affected.Count == 0) return Array.Empty<ModEntry>();

        foreach (var mod in affected)
        {
            mod.LoadError = null;
            mod.DependencyHasErrors = false;
            mod.MissingDependencies = Array.Empty<ModDependencyInfo>();
        }

        foreach (var pair in modsById)
        {
            var primary = availableMods[pair.Key];
            foreach (var candidate in pair.Value)
            {
                if (!affected.Contains(candidate)) continue;

                if (!ReferenceEquals(candidate, primary)) candidate.LoadError = GeneralLoadErrorMessage;
            }
        }

        foreach (var mod in affected)
        {
            if (mod.HasErrors || mod.Dependencies.Count == 0) continue;

            var dependencyHasError = false;
            List<ModDependencyInfo>? missingDependencies = null;

            foreach (var dependency in mod.Dependencies)
            {
                if (dependency.IsGameOrCoreDependency) continue;

                if (!availableMods.TryGetValue(dependency.ModId, out var provider))
                {
                    missingDependencies ??= new List<ModDependencyInfo>();
                    missingDependencies.Add(dependency);
                    continue;
                }

                if (provider.HasErrors || provider.HasLoadError ||
                    _settingsStore.IsDisabled(provider.ModId, provider.Version))
                {
                    dependencyHasError = true;
                    break;
                }

                if (!VersionStringUtility.SatisfiesMinimumVersion(dependency.Version, provider.Version))
                {
                    missingDependencies ??= new List<ModDependencyInfo>();
                    missingDependencies.Add(dependency);
                }
            }

            if (dependencyHasError)
            {
                mod.DependencyHasErrors = true;
                mod.LoadError = DependencyErrorMessage;
            }
            else if (missingDependencies is { Count: > 0 })
            {
                var missing = missingDependencies.ToArray();
                mod.MissingDependencies = missing;
                mod.LoadError = BuildMissingDependencyMessage(missing);
            }
        }

        return affected.ToArray();
    }

    private static string BuildMissingDependencyMessage(IReadOnlyList<ModDependencyInfo> dependencies)
    {
        static string Format(ModDependencyInfo dependency)
        {
            var version = dependency.Version?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(version) || version == "*") return dependency.ModId;

            return $"{dependency.ModId} v{version}";
        }

        if (dependencies.Count == 1) return $"Unable to load mod. Requires dependency {Format(dependencies[0])}";

        return $"Unable to load mod. Requires dependencies {string.Join(", ", dependencies.Select(Format))}";
    }

    public string GetModsStateFingerprint()
    {
        using var sha256 = SHA256.Create();
        var builder = new StringBuilder();

        foreach (var searchPath in BuildSearchPaths())
        {
            builder.AppendLine(searchPath);

            if (!Directory.Exists(searchPath))
            {
                builder.AppendLine("<missing>");
                continue;
            }

            try
            {
                var entries = new DirectoryInfo(searchPath)
                    .EnumerateFileSystemInfos()
                    .OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase);

                foreach (var entry in entries) AppendEntrySignature(builder, entry);
            }
            catch (Exception ex)
            {
                builder.Append("<error>|")
                    .Append(ex.GetType().FullName)
                    .Append('|')
                    .Append(ex.Message)
                    .AppendLine();
            }
        }

        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash);
    }

    private static void AppendEntrySignature(StringBuilder builder, FileSystemInfo entry)
    {
        builder.Append(entry.Name)
            .Append('|')
            .Append(entry is DirectoryInfo ? 'D' : 'F')
            .Append('|')
            .Append(entry.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));

        if (entry is FileInfo fileInfo)
            builder.Append('|')
                .Append(fileInfo.Length.ToString(CultureInfo.InvariantCulture));

        builder.AppendLine();
    }

    private IEnumerable<string> BuildSearchPaths()
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddPath(string candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate)) ordered.Add(candidate);
        }

        AddPath(Path.Combine(_settingsStore.DataDirectory, "Mods"));
        AddPath(Path.Combine(_settingsStore.DataDirectory, "ModsByServer"));

        foreach (var path in _settingsStore.ModPaths)
            foreach (var candidate in ResolvePathCandidates(path))
                AddPath(candidate);

        foreach (var loggedPath in LoadPathsFromLog()) AddPath(loggedPath);

        return ordered;
    }

    private static bool IsDefaultGameModsDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var normalized = fullPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar);
            var marker = Path.DirectorySeparatorChar + "Vintagestory" + Path.DirectorySeparatorChar + "Mods";
            return normalized.Contains(marker, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private IEnumerable<string> LoadPathsFromLog()
    {
        var logsDirectory = Path.Combine(_settingsStore.DataDirectory, "Logs");
        if (!Directory.Exists(logsDirectory)) yield break;

        FileInfo? logFile = null;
        try
        {
            logFile = new DirectoryInfo(logsDirectory)
                .EnumerateFiles("client-main*.txt", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();

            logFile ??= new DirectoryInfo(logsDirectory)
                .EnumerateFiles("*.txt", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            yield break;
        }

        if (logFile == null) yield break;

        var collecting = false;
        List<string> lines;
        try
        {
            lines = File.ReadLines(logFile.FullName).ToList();
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var rawLine in lines)
        {
            var message = ExtractLogMessage(rawLine);
            if (!collecting)
            {
                if (message.IndexOf("Will search the following paths for mods:", StringComparison.OrdinalIgnoreCase) >=
                    0) collecting = true;

                continue;
            }

            if (string.IsNullOrWhiteSpace(message)) break;

            var trimmed = message.Trim();
            if (trimmed.EndsWith("(Not found?)", StringComparison.Ordinal))
                trimmed = trimmed.Substring(0, trimmed.Length - "(Not found?)".Length).TrimEnd();

            if (LooksLikePath(trimmed))
            {
                yield return trimmed;
                continue;
            }

            break;
        }
    }

    private static string ExtractLogMessage(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return string.Empty;

        var bracketIndex = line.IndexOf(']');
        if (bracketIndex >= 0 && bracketIndex + 1 < line.Length) return line[(bracketIndex + 1)..].TrimStart();

        return line.TrimStart();
    }

    private static bool LooksLikePath(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (text.IndexOfAny(PathSeparators) >= 0) return true;

        if (text.Length >= 2 && char.IsLetter(text[0]) && text[1] == ':') return true;

        return false;
    }

    private IEnumerable<string> ResolvePathCandidates(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) yield break;

        if (Path.IsPathRooted(rawPath))
        {
            yield return Path.GetFullPath(rawPath);
            yield break;
        }

        foreach (var basePath in _settingsStore.SearchBaseCandidates)
        {
            var combined = Path.GetFullPath(Path.Combine(basePath, rawPath));
            yield return combined;
        }
    }

    private ModEntry? TryLoadFromDirectory(DirectoryInfo directory)
    {
        var modInfoPath = Path.Combine(directory.FullName, "modinfo.json");
        if (!File.Exists(modInfoPath))
        {
            InvalidateDirectoryCache(directory.FullName);
            return null;
        }

        DateTime manifestLastWriteUtc;
        long manifestLength;

        try
        {
            var manifestFile = new FileInfo(modInfoPath);
            manifestLastWriteUtc = manifestFile.LastWriteTimeUtc;
            manifestLength = manifestFile.Length;
        }
        catch (Exception)
        {
            manifestLastWriteUtc = DateTime.MinValue;
            manifestLength = -1L;
        }

        if (TryGetCachedDirectoryEntry(directory.FullName, manifestLastWriteUtc, manifestLength, out var cached))
        {
            var iconBytes = cached.IconBytes ?? LoadDefaultIcon();
            return CreateEntry(cached.Info, directory.FullName, ModSourceKind.Folder, iconBytes);
        }

        try
        {
            var manifestJson = File.ReadAllText(modInfoPath);
            using var document = JsonDocument.Parse(manifestJson, DocumentOptions);
            var info = ParseModInfo(document.RootElement, directory.Name);

            var icon = LoadDirectoryIcon(directory, info.IconPath);
            var iconBytes = icon.Bytes;
            iconBytes ??= LoadDefaultIcon();

            CacheDirectoryEntry(directory.FullName, info, manifestLastWriteUtc, manifestLength, icon);

            return CreateEntry(info, directory.FullName, ModSourceKind.Folder, iconBytes);
        }
        catch (Exception ex)
        {
            InvalidateDirectoryCache(directory.FullName);
            return CreateErrorEntry(directory.Name, directory.FullName, ModSourceKind.Folder, ex.Message);
        }
    }

    private ModEntry? TryLoadFromZip(FileInfo archiveFile)
    {
        var lastWriteTimeUtc = DateTime.MinValue;
        var length = 0L;

        try
        {
            lastWriteTimeUtc = archiveFile.LastWriteTimeUtc;
            length = archiveFile.Length;
        }
        catch (Exception)
        {
            // Ignore failures when probing file metadata; the cache lookup will simply miss.
        }

        if (ModManifestCacheService.TryGetManifest(archiveFile.FullName, lastWriteTimeUtc, length, out var manifestJson,
                out var cachedIconBytes))
            try
            {
                using var cachedDocument = JsonDocument.Parse(manifestJson, DocumentOptions);
                var cachedInfo = ParseModInfo(cachedDocument.RootElement,
                    Path.GetFileNameWithoutExtension(archiveFile.Name));

                var iconBytes = cachedIconBytes;
                iconBytes ??= LoadDefaultIcon();

                return CreateEntry(cachedInfo, archiveFile.FullName, ModSourceKind.ZipArchive, iconBytes);
            }
            catch (Exception)
            {
                ModManifestCacheService.Invalidate(archiveFile.FullName);
            }

        try
        {
            using var archive = ZipFile.OpenRead(archiveFile.FullName);
            var lookup = BuildArchiveLookup(archive);
            var infoEntry = FindEntry(lookup, "modinfo.json");
            if (infoEntry == null)
                return CreateErrorEntry(Path.GetFileNameWithoutExtension(archiveFile.Name), archiveFile.FullName,
                    ModSourceKind.ZipArchive, "Missing modinfo.json");

            string manifestContent;
            using (var infoStream = infoEntry.Open())
            using (var reader = new StreamReader(infoStream, Encoding.UTF8, true))
            {
                manifestContent = reader.ReadToEnd();
            }

            using var document = JsonDocument.Parse(manifestContent, DocumentOptions);
            var info = ParseModInfo(document.RootElement, Path.GetFileNameWithoutExtension(archiveFile.Name));

            var archiveIconBytes = LoadIconFromArchive(lookup, info.IconPath);
            var iconBytes = archiveIconBytes;
            iconBytes ??= LoadDefaultIcon();

            ModManifestCacheService.StoreManifest(
                archiveFile.FullName,
                lastWriteTimeUtc,
                length,
                info.ModId,
                info.Version,
                manifestContent,
                archiveIconBytes);

            return CreateEntry(info, archiveFile.FullName, ModSourceKind.ZipArchive, iconBytes);
        }
        catch (Exception ex)
        {
            return CreateErrorEntry(Path.GetFileNameWithoutExtension(archiveFile.Name), archiveFile.FullName,
                ModSourceKind.ZipArchive, ex.Message);
        }
    }

    private static ZipArchiveLookup BuildArchiveLookup(ZipArchive archive)
    {
        var byPath = new Dictionary<string, ZipArchiveEntry?>(StringComparer.OrdinalIgnoreCase);
        var byFileName = new Dictionary<string, ZipArchiveEntry?>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.Entries)
        {
            var normalizedPath = NormalizeZipEntryPath(entry.FullName);
            if (!byPath.ContainsKey(normalizedPath)) byPath[normalizedPath] = entry;

            var fileName = NormalizeZipEntryPath(Path.GetFileName(entry.FullName));
            if (!string.IsNullOrEmpty(fileName) && !byFileName.ContainsKey(fileName)) byFileName[fileName] = entry;
        }

        return new ZipArchiveLookup(byPath, byFileName);
    }

    private static ZipArchiveEntry? FindEntry(ZipArchiveLookup lookup, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName)) return null;

        var normalizedTarget = NormalizeZipEntryPath(entryName);
        if (lookup.TryGetEntry(normalizedTarget, out var exact)) return exact;

        var normalizedFileName = NormalizeZipEntryPath(Path.GetFileName(entryName));
        if (lookup.TryGetEntryByFileName(normalizedFileName, out var byName)) return byName;

        return null;
    }

    private ModEntry CreateUnsupportedCodeModEntry(FileInfo file)
    {
        var id = ToModId(Path.GetFileNameWithoutExtension(file.Name));
        return new ModEntry
        {
            ModId = id,
            Name = Path.GetFileNameWithoutExtension(file.Name),
            ManifestName = null,
            Version = null,
            NetworkVersion = null,
            Description =
                "This code mod is not packaged with a modinfo.json file. Pack the mod into a zip or folder with modinfo.json so it can be managed.",
            Website = null,
            Authors = Array.Empty<string>(),
            Contributors = Array.Empty<string>(),
            Dependencies = Array.Empty<ModDependencyInfo>(),
            SourcePath = file.FullName,
            SourceKind = string.Equals(file.Extension, ".cs", StringComparison.OrdinalIgnoreCase)
                ? ModSourceKind.SourceCode
                : ModSourceKind.Assembly,
            IconBytes = null,
            Error = "Metadata unavailable for code-only mods.",
            LoadError = null,
            Side = null,
            RequiredOnClient = null,
            RequiredOnServer = null
        };
    }

    private ModEntry CreateEntry(RawModInfo info, string sourcePath, ModSourceKind kind, byte[]? iconBytes)
    {
        return new ModEntry
        {
            ModId = info.ModId,
            Name = info.Name,
            ManifestName = info.ManifestName,
            Version = info.Version,
            NetworkVersion = info.NetworkVersion,
            Description = info.Description,
            Website = info.Website,
            Authors = info.Authors,
            Contributors = info.Contributors,
            Dependencies = info.Dependencies,
            SourcePath = sourcePath,
            SourceKind = kind,
            IconBytes = iconBytes,
            Error = null,
            LoadError = null,
            Side = info.Side,
            RequiredOnClient = info.RequiredOnClient,
            RequiredOnServer = info.RequiredOnServer
        };
    }

    private ModEntry CreateErrorEntry(string hintName, string sourcePath, ModSourceKind kind, string message)
    {
        var name = string.IsNullOrWhiteSpace(hintName) ? Path.GetFileName(sourcePath) : hintName;
        return new ModEntry
        {
            ModId = ToModId(name),
            Name = name,
            ManifestName = null,
            Version = null,
            NetworkVersion = null,
            Description = null,
            Website = null,
            Authors = Array.Empty<string>(),
            Contributors = Array.Empty<string>(),
            Dependencies = Array.Empty<ModDependencyInfo>(),
            SourcePath = sourcePath,
            SourceKind = kind,
            IconBytes = null,
            Error = message,
            LoadError = null,
            Side = null,
            RequiredOnClient = null,
            RequiredOnServer = null
        };
    }

    private static RawModInfo ParseModInfo(JsonElement root, string fallbackName)
    {
        var modId = GetString(root, "modid") ?? GetString(root, "modID") ?? string.Empty;
        fallbackName = string.IsNullOrWhiteSpace(fallbackName) ? string.Empty : fallbackName.Trim();
        var manifestName = GetString(root, "name");
        manifestName = string.IsNullOrWhiteSpace(manifestName) ? null : manifestName.Trim();
        var nameCandidate = manifestName ?? fallbackName;
        if (string.IsNullOrWhiteSpace(modId)) modId = ToModId(nameCandidate);

        var version = GetString(root, "version");
        version ??= TryResolveVersionFromMap(root);

        var authors = GetStringList(root, "authors");
        var contributors = GetStringList(root, "contributors");
        var dependencies = GetDependencies(root);

        var displayName = string.IsNullOrWhiteSpace(nameCandidate) ? modId : nameCandidate;

        return new RawModInfo
        {
            ModId = modId,
            Name = displayName,
            ManifestName = manifestName,
            Version = string.IsNullOrWhiteSpace(version) ? null : version,
            NetworkVersion = GetString(root, "networkversion") ?? GetString(root, "networkVersion"),
            Description = GetString(root, "description"),
            Website = GetString(root, "website"),
            Authors = authors,
            Contributors = contributors,
            Dependencies = dependencies,
            IconPath = GetString(root, "iconpath") ?? GetString(root, "iconPath"),
            Side = GetString(root, "side"),
            RequiredOnClient = GetNullableBool(root, "requiredonclient"),
            RequiredOnServer = GetNullableBool(root, "requiredonserver")
        };
    }

    private static IReadOnlyList<ModDependencyInfo> GetDependencies(JsonElement root)
    {
        if (!TryGetProperty(root, "dependencies", out var dependencies) ||
            dependencies.ValueKind != JsonValueKind.Object) return Array.Empty<ModDependencyInfo>();

        var list = new List<ModDependencyInfo>();
        foreach (var property in dependencies.EnumerateObject())
        {
            var version = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : string.Empty;
            list.Add(new ModDependencyInfo(property.Name, version));
        }

        return list.Count == 0 ? Array.Empty<ModDependencyInfo>() : list.ToArray();
    }

    private static string? TryResolveVersionFromMap(JsonElement root)
    {
        if (!TryGetProperty(root, "versionmap", out var map) &&
            !TryGetProperty(root, "VersionMap", out map)) return null;

        if (map.ValueKind != JsonValueKind.Object) return null;

        string? preferred = null;
        string? fallback = null;
        foreach (var property in map.EnumerateObject())
        {
            var version = property.Value.GetString();
            if (version == null) continue;

            fallback = version;
            if (property.Name.Contains("1.21", StringComparison.OrdinalIgnoreCase)) preferred = version;
        }

        return preferred ?? fallback;
    }

    private DirectoryIconCache LoadDirectoryIcon(DirectoryInfo directory, string? iconPath)
    {
        var candidate = ResolveSafePath(directory.FullName, iconPath);
        if (candidate == null)
        {
            var fallback = Path.Combine(directory.FullName, "modicon.png");
            if (File.Exists(fallback)) candidate = fallback;
        }

        if (candidate != null)
            try
            {
                var fileInfo = new FileInfo(candidate);
                if (fileInfo.Exists)
                {
                    var bytes = File.ReadAllBytes(candidate);
                    return new DirectoryIconCache
                    {
                        Bytes = bytes,
                        Metadata = new DirectoryIconMetadata
                        {
                            IconPath = candidate,
                            LastWriteTimeUtc = fileInfo.LastWriteTimeUtc,
                            Length = fileInfo.Length,
                            WasMissing = false
                        }
                    };
                }

                return new DirectoryIconCache
                {
                    Bytes = null,
                    Metadata = new DirectoryIconMetadata
                    {
                        IconPath = candidate,
                        WasMissing = true
                    }
                };
            }
            catch (Exception)
            {
                return new DirectoryIconCache
                {
                    Bytes = null,
                    Metadata = new DirectoryIconMetadata
                    {
                        IconPath = candidate,
                        WasMissing = true
                    }
                };
            }

        return new DirectoryIconCache
        {
            Bytes = null,
            Metadata = null
        };
    }

    private bool TryGetCachedDirectoryEntry(
        string directoryPath,
        DateTime manifestLastWriteUtc,
        long manifestLength,
        out CachedDirectoryEntry entry)
    {
        lock (_directoryCacheLock)
        {
            if (_directoryManifestCache.TryGetValue(directoryPath, out var cached))
            {
                if (cached.ManifestLength == manifestLength
                    && cached.ManifestLastWriteTimeUtc == manifestLastWriteUtc
                    && IconMetadataMatches(cached.IconMetadata))
                {
                    entry = cached;
                    return true;
                }

                _directoryManifestCache.Remove(directoryPath);
            }
        }

        entry = null!;
        return false;
    }

    private void CacheDirectoryEntry(
        string directoryPath,
        RawModInfo info,
        DateTime manifestLastWriteUtc,
        long manifestLength,
        DirectoryIconCache icon)
    {
        var cached = new CachedDirectoryEntry
        {
            Info = info,
            ManifestLastWriteTimeUtc = manifestLastWriteUtc,
            ManifestLength = manifestLength,
            IconBytes = icon.Bytes,
            IconMetadata = icon.Metadata
        };

        lock (_directoryCacheLock)
        {
            _directoryManifestCache[directoryPath] = cached;
        }
    }

    private void InvalidateDirectoryCache(string directoryPath)
    {
        lock (_directoryCacheLock)
        {
            _directoryManifestCache.Remove(directoryPath);
        }
    }

    private static bool IconMetadataMatches(DirectoryIconMetadata? metadata)
    {
        if (metadata == null || string.IsNullOrWhiteSpace(metadata.IconPath)) return true;

        try
        {
            var fileInfo = new FileInfo(metadata.IconPath);
            if (!fileInfo.Exists) return metadata.WasMissing;

            if (metadata.WasMissing) return false;

            return fileInfo.LastWriteTimeUtc == metadata.LastWriteTimeUtc
                   && fileInfo.Length == metadata.Length;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static byte[]? LoadIconFromArchive(ZipArchiveLookup lookup, string? iconPath)
    {
        ZipArchiveEntry? entry = null;
        if (!string.IsNullOrWhiteSpace(iconPath)) entry = FindEntry(lookup, iconPath);

        entry ??= FindEntry(lookup, "modicon.png");
        if (entry == null) return null;

        using var buffer = new MemoryStream();
        using var iconStream = entry.Open();
        iconStream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private byte[]? LoadDefaultIcon()
    {
        lock (_defaultIconLock)
        {
            if (!_defaultIconResolved)
            {
                _defaultIconBytes = TryLoadBundledDefaultIcon()
                                    ?? TryLoadBundledIconFromBaseDirectory();

                if (_defaultIconBytes == null)
                    foreach (var candidate in EnumerateDefaultIconCandidates())
                        try
                        {
                            if (!File.Exists(candidate)) continue;

                            _defaultIconBytes = File.ReadAllBytes(candidate);
                            break;
                        }
                        catch (IOException)
                        {
                            // Ignore IO failures and continue with other candidates.
                        }
                        catch (UnauthorizedAccessException)
                        {
                            // Ignore permission issues and continue with other candidates.
                        }
                        catch (NotSupportedException)
                        {
                            // Ignore invalid path formats and continue with other candidates.
                        }

                _defaultIconResolved = true;
            }

            return _defaultIconBytes;
        }
    }

    private static byte[]? TryLoadBundledDefaultIcon()
    {
        const string resourcePath = "Resources/mod-default.png";
        try
        {
            var resourceUri = new Uri(resourcePath, UriKind.Relative);
            var resource = Application.GetResourceStream(resourceUri);
            if (resource?.Stream != null)
            {
                using var stream = resource.Stream;
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return memory.ToArray();
            }
        }
        catch (IOException)
        {
        }
        catch (NotSupportedException)
        {
        }
        catch (SecurityException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (UriFormatException)
        {
        }

        return null;
    }

    private static byte[]? TryLoadBundledIconFromBaseDirectory()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "mod-default.png");
        try
        {
            if (File.Exists(candidate)) return File.ReadAllBytes(candidate);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (NotSupportedException)
        {
        }

        return null;
    }

    private IEnumerable<string> EnumerateDefaultIconCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var basePath in _settingsStore.SearchBaseCandidates)
        {
            if (string.IsNullOrWhiteSpace(basePath)) continue;

            string? current;
            try
            {
                current = Path.GetFullPath(basePath);
            }
            catch (Exception)
            {
                continue;
            }

            for (var depth = 0; depth < 4 && !string.IsNullOrEmpty(current); depth++)
            {
                foreach (var relative in DefaultIconRelativePaths)
                {
                    string candidate;
                    try
                    {
                        candidate = Path.Combine(current, relative);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (seen.Add(candidate)) yield return candidate;
                }

                try
                {
                    var parent = Directory.GetParent(current);
                    if (parent == null) break;

                    current = parent.FullName;
                }
                catch (Exception)
                {
                    break;
                }
            }
        }
    }

    private static string? ResolveSafePath(string baseDirectory, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;

        try
        {
            var combined = Path.GetFullPath(Path.Combine(baseDirectory, relative));
            if (combined.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase)) return combined;
        }
        catch (Exception)
        {
            // Ignore invalid paths.
        }

        return null;
    }

    private static string NormalizeZipEntryPath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');

        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];

        if (normalized.Length > 0 && normalized[0] == '/') normalized = normalized[1..];

        return normalized;
    }

    private static string ToModId(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "mod";

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
            if (char.IsLetter(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else if (char.IsDigit(ch))
            {
                if (builder.Length == 0) builder.Append('m');
                builder.Append(ch);
            }

        return builder.Length == 0 ? "mod" : builder.ToString();
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (TryGetProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();

        return null;
    }

    private static bool? GetNullableBool(JsonElement element, string propertyName)
    {
        if (TryGetProperty(element, propertyName, out var value))
        {
            if (value.ValueKind == JsonValueKind.True) return true;

            if (value.ValueKind == JsonValueKind.False) return false;
        }

        return null;
    }

    private static IReadOnlyList<string> GetStringList(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var list = new List<string>();
        foreach (var item in array.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String)
                list.Add(item.GetString() ?? string.Empty);

        return list.Count == 0 ? Array.Empty<string>() : list.ToArray();
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }

        value = default;
        return false;
    }

    private sealed class CachedDirectoryEntry
    {
        public required RawModInfo Info { get; init; }
        public required DateTime ManifestLastWriteTimeUtc { get; init; }
        public required long ManifestLength { get; init; }
        public byte[]? IconBytes { get; init; }
        public DirectoryIconMetadata? IconMetadata { get; init; }
    }

    private sealed class DirectoryIconCache
    {
        public byte[]? Bytes { get; init; }
        public DirectoryIconMetadata? Metadata { get; init; }
    }

    private sealed class DirectoryIconMetadata
    {
        public string? IconPath { get; init; }
        public DateTime? LastWriteTimeUtc { get; init; }
        public long? Length { get; init; }
        public bool WasMissing { get; init; }
    }

    private sealed class ZipArchiveLookup
    {
        private readonly Dictionary<string, ZipArchiveEntry?> _byFileName;
        private readonly Dictionary<string, ZipArchiveEntry?> _byPath;

        public ZipArchiveLookup(Dictionary<string, ZipArchiveEntry?> byPath,
            Dictionary<string, ZipArchiveEntry?> byFileName)
        {
            _byPath = byPath;
            _byFileName = byFileName;
        }

        public bool TryGetEntry(string normalizedPath, out ZipArchiveEntry? entry)
        {
            if (_byPath.TryGetValue(normalizedPath, out var value) && value != null)
            {
                entry = value;
                return true;
            }

            entry = null;
            return false;
        }

        public bool TryGetEntryByFileName(string normalizedFileName, out ZipArchiveEntry? entry)
        {
            if (_byFileName.TryGetValue(normalizedFileName, out var value) && value != null)
            {
                entry = value;
                return true;
            }

            entry = null;
            return false;
        }
    }

    private sealed class RawModInfo
    {
        public required string ModId { get; init; }
        public required string Name { get; init; }
        public string? ManifestName { get; init; }
        public string? Version { get; init; }
        public string? NetworkVersion { get; init; }
        public string? Description { get; init; }
        public string? Website { get; init; }
        public IReadOnlyList<string> Authors { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Contributors { get; init; } = Array.Empty<string>();
        public IReadOnlyList<ModDependencyInfo> Dependencies { get; init; } = Array.Empty<ModDependencyInfo>();
        public string? IconPath { get; init; }
        public string? Side { get; init; }
        public bool? RequiredOnClient { get; init; }
        public bool? RequiredOnServer { get; init; }
    }
}