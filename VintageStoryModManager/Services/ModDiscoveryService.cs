using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using VintageStoryModManager.Models;

namespace VintageStoryModManager.Services;

/// <summary>
/// Discovers installed mods and reads their metadata in the same way as the game.
/// </summary>
public sealed class ModDiscoveryService
{
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

    private readonly ClientSettingsStore _settingsStore;
    private byte[]? _defaultIconBytes;
    private string? _defaultIconDescription;
    private bool _defaultIconResolved;
    private readonly object _defaultIconLock = new();

    public ModDiscoveryService(ClientSettingsStore settingsStore)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    public IReadOnlyList<ModEntry> LoadMods()
    {
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderedEntries = new List<(int Order, FileSystemInfo Entry)>();
        int order = 0;

        foreach (string searchPath in BuildSearchPaths())
        {
            if (!Directory.Exists(searchPath))
            {
                continue;
            }

            if (IsDefaultGameModsDirectory(searchPath))
            {
                continue;
            }

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(searchPath).EnumerateFileSystemInfos();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (FileSystemInfo entry in entries)
            {
                if (!seenSources.Add(entry.FullName))
                {
                    continue;
                }

                orderedEntries.Add((order++, entry));
            }
        }

        if (orderedEntries.Count == 0)
        {
            return Array.Empty<ModEntry>();
        }

        const int BatchSize = 16;
        int maxDegree = Math.Clamp(Environment.ProcessorCount, 1, 8);
        var collected = new ConcurrentBag<(int Order, ModEntry Entry)>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegree
        };

        Parallel.ForEach(orderedEntries.Chunk(BatchSize), options, batch =>
        {
            foreach (var (batchOrder, fileSystemInfo) in batch)
            {
                ModEntry? entry = ProcessEntry(fileSystemInfo);
                if (entry != null)
                {
                    collected.Add((batchOrder, entry));
                }
            }
        });

        var results = collected
            .OrderBy(tuple => tuple.Order)
            .Select(tuple => tuple.Entry)
            .ToList();

        ApplyLoadStatuses(results);
        return results;

        ModEntry? ProcessEntry(FileSystemInfo entry)
        {
            switch (entry)
            {
                case DirectoryInfo directory:
                    return TryLoadFromDirectory(directory);
                case FileInfo file when string.Equals(file.Extension, ".zip", StringComparison.OrdinalIgnoreCase):
                    return TryLoadFromZip(file);
                case FileInfo file when string.Equals(file.Extension, ".cs", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(file.Extension, ".dll", StringComparison.OrdinalIgnoreCase):
                    return CreateUnsupportedCodeModEntry(file);
                default:
                    return null;
            }
        }
    }

    public IReadOnlyList<string> GetSearchPaths()
    {
        return BuildSearchPaths().ToArray();
    }

    public ModEntry? LoadModFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return null;
        }

        if (Directory.Exists(fullPath))
        {
            return TryLoadFromDirectory(new DirectoryInfo(fullPath));
        }

        if (!File.Exists(fullPath))
        {
            return null;
        }

        var fileInfo = new FileInfo(fullPath);
        if (string.Equals(fileInfo.Extension, ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return TryLoadFromZip(fileInfo);
        }

        if (string.Equals(fileInfo.Extension, ".cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileInfo.Extension, ".dll", StringComparison.OrdinalIgnoreCase))
        {
            return CreateUnsupportedCodeModEntry(fileInfo);
        }

        return null;
    }

    public void ApplyLoadStatuses(IList<ModEntry> mods)
    {
        if (mods == null || mods.Count == 0)
        {
            return;
        }

        var availableMods = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in mods.Where(mod => !string.IsNullOrWhiteSpace(mod.ModId))
            .GroupBy(mod => mod.ModId, StringComparer.OrdinalIgnoreCase))
        {
            ModEntry? primary = null;
            foreach (var candidate in group)
            {
                if (primary == null && !candidate.HasErrors)
                {
                    primary = candidate;
                }
            }

            primary ??= group.First();
            availableMods[primary.ModId] = primary;

            foreach (var candidate in group)
            {
                if (!ReferenceEquals(candidate, primary))
                {
                    candidate.LoadError = GeneralLoadErrorMessage;
                }
            }
        }

        foreach (var mod in mods)
        {
            mod.MissingDependencies = Array.Empty<ModDependencyInfo>();
            mod.DependencyHasErrors = false;

            if (mod.HasErrors || mod.HasLoadError || mod.Dependencies.Count == 0)
            {
                continue;
            }

            bool dependencyHasError = false;
            List<ModDependencyInfo>? missingDependencies = null;

            foreach (var dependency in mod.Dependencies)
            {
                if (dependency.IsGameOrCoreDependency)
                {
                    continue;
                }

                if (!availableMods.TryGetValue(dependency.ModId, out ModEntry? provider))
                {
                    missingDependencies ??= new List<ModDependencyInfo>();
                    missingDependencies.Add(dependency);
                    continue;
                }

                if (provider.HasErrors || provider.HasLoadError || _settingsStore.IsDisabled(provider.ModId, provider.Version))
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
                ModDependencyInfo[] missing = missingDependencies.ToArray();
                mod.MissingDependencies = missing;
                mod.LoadError = BuildMissingDependencyMessage(missing);
            }
        }
    }

    private static string BuildMissingDependencyMessage(IReadOnlyList<ModDependencyInfo> dependencies)
    {
        static string Format(ModDependencyInfo dependency)
        {
            string version = dependency.Version?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(version) || version == "*")
            {
                return dependency.ModId;
            }

            return $"{dependency.ModId} v{version}";
        }

        if (dependencies.Count == 1)
        {
            return $"Unable to load mod. Requires dependency {Format(dependencies[0])}";
        }

        return $"Unable to load mod. Requires dependencies {string.Join(", ", dependencies.Select(Format))}";
    }

    private const string GeneralLoadErrorMessage = "Unable to load mod. Check log files.";
    private const string DependencyErrorMessage = "Unable to load mod. A dependency has an error. Make sure they all load correctly.";

    public string GetModsStateFingerprint()
    {
        using var sha256 = SHA256.Create();
        var builder = new StringBuilder();

        foreach (string searchPath in BuildSearchPaths())
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

                foreach (var entry in entries)
                {
                    AppendEntrySignature(builder, entry);
                }
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

        byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
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
        {
            builder.Append('|')
                .Append(fileInfo.Length.ToString(CultureInfo.InvariantCulture));
        }

        builder.AppendLine();
    }

    private IEnumerable<string> BuildSearchPaths()
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddPath(string candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
            {
                ordered.Add(candidate);
            }
        }

        AddPath(Path.Combine(_settingsStore.DataDirectory, "Mods"));
        AddPath(Path.Combine(_settingsStore.DataDirectory, "ModsByServer"));

        foreach (var path in _settingsStore.ModPaths)
        {
            foreach (var candidate in ResolvePathCandidates(path))
            {
                AddPath(candidate);
            }
        }

        foreach (var loggedPath in LoadPathsFromLog())
        {
            AddPath(loggedPath);
        }

        return ordered;
    }

    private static bool IsDefaultGameModsDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            string normalized = fullPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar);
            string marker = Path.DirectorySeparatorChar + "Vintagestory" + Path.DirectorySeparatorChar + "Mods";
            return normalized.Contains(marker, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private IEnumerable<string> LoadPathsFromLog()
    {
        string logsDirectory = Path.Combine(_settingsStore.DataDirectory, "Logs");
        if (!Directory.Exists(logsDirectory))
        {
            yield break;
        }

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

        if (logFile == null)
        {
            yield break;
        }

        bool collecting = false;
        List<string> lines;
        try
        {
            lines = File.ReadLines(logFile.FullName).ToList();
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (string rawLine in lines)
        {
            string message = ExtractLogMessage(rawLine);
            if (!collecting)
            {
                if (message.IndexOf("Will search the following paths for mods:", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    collecting = true;
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                break;
            }

            string trimmed = message.Trim();
            if (trimmed.EndsWith("(Not found?)", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - "(Not found?)".Length).TrimEnd();
            }

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
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        int bracketIndex = line.IndexOf(']');
        if (bracketIndex >= 0 && bracketIndex + 1 < line.Length)
        {
            return line[(bracketIndex + 1)..].TrimStart();
        }

        return line.TrimStart();
    }

    private static bool LooksLikePath(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.IndexOfAny(PathSeparators) >= 0)
        {
            return true;
        }

        if (text.Length >= 2 && char.IsLetter(text[0]) && text[1] == ':')
        {
            return true;
        }

        return false;
    }

    private IEnumerable<string> ResolvePathCandidates(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            yield break;
        }

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
        string modInfoPath = Path.Combine(directory.FullName, "modinfo.json");
        if (!File.Exists(modInfoPath))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(modInfoPath), DocumentOptions);
            var info = ParseModInfo(document.RootElement, directory.Name);
            byte[]? iconBytes = LoadIconFromDirectory(directory, info.IconPath, out string? iconDescription);
            if (iconBytes == null)
            {
                iconBytes = LoadDefaultIcon(out iconDescription);
            }

            return CreateEntry(info, directory.FullName, ModSourceKind.Folder, iconBytes, iconDescription);
        }
        catch (Exception ex)
        {
            return CreateErrorEntry(directory.Name, directory.FullName, ModSourceKind.Folder, ex.Message);
        }
    }

    private ModEntry? TryLoadFromZip(FileInfo archiveFile)
    {
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(archiveFile.FullName);
            ZipArchiveEntry? infoEntry = FindEntry(archive, "modinfo.json");
            if (infoEntry == null)
            {
                return CreateErrorEntry(Path.GetFileNameWithoutExtension(archiveFile.Name), archiveFile.FullName, ModSourceKind.ZipArchive, "Missing modinfo.json");
            }

            using var infoStream = infoEntry.Open();
            using JsonDocument document = JsonDocument.Parse(infoStream, DocumentOptions);
            var info = ParseModInfo(document.RootElement, Path.GetFileNameWithoutExtension(archiveFile.Name));

            byte[]? iconBytes = LoadIconFromArchive(archive, info.IconPath, out string? iconDescription);
            if (iconBytes == null)
            {
                iconBytes = LoadDefaultIcon(out iconDescription);
            }
            return CreateEntry(info, archiveFile.FullName, ModSourceKind.ZipArchive, iconBytes, iconDescription);
        }
        catch (Exception ex)
        {
            return CreateErrorEntry(Path.GetFileNameWithoutExtension(archiveFile.Name), archiveFile.FullName, ModSourceKind.ZipArchive, ex.Message);
        }
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string entryName)
    {
        foreach (var entry in archive.Entries)
        {
            if (string.Equals(entry.FullName, entryName, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return archive.Entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.FullName), entryName, StringComparison.OrdinalIgnoreCase));
    }

    private ModEntry CreateUnsupportedCodeModEntry(FileInfo file)
    {
        string id = ToModId(Path.GetFileNameWithoutExtension(file.Name));
        return new ModEntry
        {
            ModId = id,
            Name = Path.GetFileNameWithoutExtension(file.Name),
            Version = null,
            NetworkVersion = null,
            Description = "This code mod is not packaged with a modinfo.json file. Pack the mod into a zip or folder with modinfo.json so it can be managed.",
            Website = null,
            Authors = Array.Empty<string>(),
            Contributors = Array.Empty<string>(),
            Dependencies = Array.Empty<ModDependencyInfo>(),
            SourcePath = file.FullName,
            SourceKind = string.Equals(file.Extension, ".cs", StringComparison.OrdinalIgnoreCase) ? ModSourceKind.SourceCode : ModSourceKind.Assembly,
            IconBytes = null,
            IconDescription = null,
            Error = "Metadata unavailable for code-only mods.",
            LoadError = null,
            Side = null,
            RequiredOnClient = null,
            RequiredOnServer = null
        };
    }

    private ModEntry CreateEntry(RawModInfo info, string sourcePath, ModSourceKind kind, byte[]? iconBytes, string? iconDescription)
    {
        return new ModEntry
        {
            ModId = info.ModId,
            Name = info.Name,
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
            IconDescription = iconDescription,
            Error = null,
            LoadError = null,
            Side = info.Side,
            RequiredOnClient = info.RequiredOnClient,
            RequiredOnServer = info.RequiredOnServer
        };
    }

    private ModEntry CreateErrorEntry(string hintName, string sourcePath, ModSourceKind kind, string message)
    {
        string name = string.IsNullOrWhiteSpace(hintName) ? Path.GetFileName(sourcePath) : hintName;
        return new ModEntry
        {
            ModId = ToModId(name),
            Name = name,
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
            IconDescription = null,
            Error = message,
            LoadError = null,
            Side = null,
            RequiredOnClient = null,
            RequiredOnServer = null
        };
    }

    private static RawModInfo ParseModInfo(JsonElement root, string fallbackName)
    {
        string modId = GetString(root, "modid") ?? GetString(root, "modID") ?? string.Empty;
        string name = GetString(root, "name") ?? fallbackName;
        if (string.IsNullOrWhiteSpace(modId))
        {
            modId = ToModId(name);
        }

        string? version = GetString(root, "version");
        version ??= TryResolveVersionFromMap(root);

        var authors = GetStringList(root, "authors");
        var contributors = GetStringList(root, "contributors");
        var dependencies = GetDependencies(root);

        return new RawModInfo
        {
            ModId = modId,
            Name = string.IsNullOrWhiteSpace(name) ? modId : name,
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
        if (!TryGetProperty(root, "dependencies", out JsonElement dependencies) || dependencies.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<ModDependencyInfo>();
        }

        var list = new List<ModDependencyInfo>();
        foreach (var property in dependencies.EnumerateObject())
        {
            string version = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : string.Empty;
            list.Add(new ModDependencyInfo(property.Name, version));
        }

        return list.Count == 0 ? Array.Empty<ModDependencyInfo>() : list.ToArray();
    }

    private static string? TryResolveVersionFromMap(JsonElement root)
    {
        if (!TryGetProperty(root, "versionmap", out JsonElement map) && !TryGetProperty(root, "VersionMap", out map))
        {
            return null;
        }

        if (map.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? preferred = null;
        string? fallback = null;
        foreach (var property in map.EnumerateObject())
        {
            string? version = property.Value.GetString();
            if (version == null)
            {
                continue;
            }

            fallback = version;
            if (property.Name.Contains("1.21", StringComparison.OrdinalIgnoreCase))
            {
                preferred = version;
            }
        }

        return preferred ?? fallback;
    }

    private static byte[]? LoadIconFromDirectory(DirectoryInfo directory, string? iconPath, out string? description)
    {
        string? candidate = ResolveSafePath(directory.FullName, iconPath);
        if (candidate == null)
        {
            string fallback = Path.Combine(directory.FullName, "modicon.png");
            if (File.Exists(fallback))
            {
                candidate = fallback;
            }
        }

        if (candidate != null && File.Exists(candidate))
        {
            description = candidate;
            return File.ReadAllBytes(candidate);
        }

        description = null;
        return null;
    }

    private static byte[]? LoadIconFromArchive(ZipArchive archive, string? iconPath, out string? description)
    {
        ZipArchiveEntry? entry = null;
        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            entry = FindEntry(archive, iconPath);
        }

        entry ??= FindEntry(archive, "modicon.png");
        if (entry == null)
        {
            description = null;
            return null;
        }

        using MemoryStream buffer = new MemoryStream();
        using Stream iconStream = entry.Open();
        iconStream.CopyTo(buffer);
        description = entry.FullName;
        return buffer.ToArray();
    }

    private byte[]? LoadDefaultIcon(out string? description)
    {
        lock (_defaultIconLock)
        {
            if (!_defaultIconResolved)
            {
                foreach (string candidate in EnumerateDefaultIconCandidates())
                {
                    try
                    {
                        if (!File.Exists(candidate))
                        {
                            continue;
                        }

                        _defaultIconBytes = File.ReadAllBytes(candidate);
                        _defaultIconDescription = candidate;
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
                }

                _defaultIconResolved = true;
            }

            description = _defaultIconDescription;
            return _defaultIconBytes;
        }
    }

    private IEnumerable<string> EnumerateDefaultIconCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string basePath in _settingsStore.SearchBaseCandidates)
        {
            if (string.IsNullOrWhiteSpace(basePath))
            {
                continue;
            }

            string? current;
            try
            {
                current = Path.GetFullPath(basePath);
            }
            catch (Exception)
            {
                continue;
            }

            for (int depth = 0; depth < 4 && !string.IsNullOrEmpty(current); depth++)
            {
                foreach (string relative in DefaultIconRelativePaths)
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

                    if (seen.Add(candidate))
                    {
                        yield return candidate;
                    }
                }

                try
                {
                    DirectoryInfo? parent = Directory.GetParent(current);
                    if (parent == null)
                    {
                        break;
                    }

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
        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }

        try
        {
            string combined = Path.GetFullPath(Path.Combine(baseDirectory, relative));
            if (combined.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return combined;
            }
        }
        catch (Exception)
        {
            // Ignore invalid paths.
        }

        return null;
    }

    private static string ToModId(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "mod";
        }

        var builder = new StringBuilder(name.Length);
        foreach (char ch in name)
        {
            if (char.IsLetter(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else if (char.IsDigit(ch))
            {
                if (builder.Length == 0)
                {
                    builder.Append('m');
                }
                builder.Append(ch);
            }
        }

        return builder.Length == 0 ? "mod" : builder.ToString();
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (TryGetProperty(element, propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static bool? GetNullableBool(JsonElement element, string propertyName)
    {
        if (TryGetProperty(element, propertyName, out JsonElement value))
        {
            if (value.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> GetStringList(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                list.Add(item.GetString() ?? string.Empty);
            }
        }

        return list.Count == 0 ? Array.Empty<string>() : list.ToArray();
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private sealed class RawModInfo
    {
        public required string ModId { get; init; }
        public required string Name { get; init; }
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
