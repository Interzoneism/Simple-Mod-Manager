using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VintageStoryModManager.Models;

namespace VintageStoryModManager.Services;

public static class ModlistGroupService
{
    public const string FileExtension = ".svsmgroup";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static ModlistGroupDefinition Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var definition = JsonSerializer.Deserialize<ModlistGroupDefinition>(File.ReadAllText(filePath), JsonOptions)
                         ?? throw new InvalidDataException("The modlist group file was empty.");

        definition.Members ??= new List<string>();
        definition.ConflictResolutions = new Dictionary<string, string>(
            definition.ConflictResolutions ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        return definition;
    }

    public static void Save(string filePath, ModlistGroupDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(definition);

        definition.SchemaVersion = 1;
        definition.Members ??= new List<string>();
        definition.ConflictResolutions = new Dictionary<string, string>(
            definition.ConflictResolutions ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        File.WriteAllText(filePath, JsonSerializer.Serialize(definition, JsonOptions));
    }

    public static string CreateReference(string groupFilePath, string memberFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberFilePath);

        var groupDirectory = Path.GetDirectoryName(Path.GetFullPath(groupFilePath))
                             ?? throw new InvalidOperationException("The group file does not have a parent directory.");
        var fullMemberPath = Path.GetFullPath(memberFilePath);
        var relativePath = Path.GetRelativePath(groupDirectory, fullMemberPath);

        return relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               || string.Equals(relativePath, "..", StringComparison.Ordinal)
            ? fullMemberPath
            : relativePath;
    }

    public static string ResolveReference(string groupFilePath, string memberReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberReference);

        if (Path.IsPathRooted(memberReference)) return Path.GetFullPath(memberReference);

        var groupDirectory = Path.GetDirectoryName(Path.GetFullPath(groupFilePath))
                             ?? throw new InvalidOperationException("The group file does not have a parent directory.");
        return Path.GetFullPath(Path.Combine(groupDirectory, memberReference));
    }

    public static ModlistGroupBuildResult BuildMergedPreset(
        ModlistGroupDefinition definition,
        IReadOnlyList<ModlistGroupSource> sources,
        IReadOnlyList<string>? missingMembers = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(sources);

        var candidatesByModId = new Dictionary<string, List<ModCandidate>>(StringComparer.OrdinalIgnoreCase);
        var orderedModIds = new List<string>();
        var disabledEntries = new List<string>();
        var seenDisabledEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            if (source.Preset.DisabledEntries is not null)
                foreach (var disabledEntry in source.Preset.DisabledEntries)
                    if (!string.IsNullOrWhiteSpace(disabledEntry) && seenDisabledEntries.Add(disabledEntry.Trim()))
                        disabledEntries.Add(disabledEntry.Trim());

            if (source.Preset.Mods is null) continue;

            foreach (var mod in source.Preset.Mods)
            {
                if (mod is null || string.IsNullOrWhiteSpace(mod.ModId)) continue;

                var modId = mod.ModId.Trim();
                if (!candidatesByModId.TryGetValue(modId, out var candidates))
                {
                    candidates = new List<ModCandidate>();
                    candidatesByModId[modId] = candidates;
                    orderedModIds.Add(modId);
                }

                // A malformed modlist containing the same ID twice should not create
                // two identical choices for the same source.
                if (candidates.Any(candidate => string.Equals(candidate.Source.Reference, source.Reference,
                        StringComparison.OrdinalIgnoreCase)))
                    continue;

                candidates.Add(new ModCandidate(source, mod));
            }
        }

        var conflicts = new List<ModlistGroupConflict>();
        var selectedCandidates = new Dictionary<string, ModCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var modId in orderedModIds)
        {
            var candidates = candidatesByModId[modId];
            var distinctCandidates = candidates
                .GroupBy(BuildChoiceComparisonKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            if (distinctCandidates.Count == 1)
            {
                // Identical references do not require a user decision. The first
                // modlist wins deterministically, even if an old resolution exists.
                selectedCandidates[modId] = distinctCandidates[0];
                continue;
            }

            var selectedReference = definition.ConflictResolutions.TryGetValue(modId, out var resolution)
                ? resolution
                : null;
            var resolvedCandidate = candidates.FirstOrDefault(candidate => ReferencesEqual(
                candidate.Source.Reference,
                selectedReference));
            var selectedCandidate = resolvedCandidate is null
                ? SelectDefaultCandidate(distinctCandidates)
                : distinctCandidates.First(candidate => string.Equals(
                    BuildChoiceComparisonKey(candidate),
                    BuildChoiceComparisonKey(resolvedCandidate),
                    StringComparison.Ordinal));
            selectedCandidates[modId] = selectedCandidate;

            var choices = distinctCandidates.Select(candidate => new ModlistGroupConflictChoice(
                    candidate.Source.Reference,
                    candidate.Source.FilePath,
                    candidate.Source.DisplayName,
                    candidate.Mod.Version,
                    HasCustomConfiguration(candidate.Source.Preset, candidate.Mod)))
                .ToList();
            conflicts.Add(new ModlistGroupConflict(
                modId,
                choices,
                selectedCandidate.Source.Reference));
        }

        var selectedMods = orderedModIds
            .Select(modId => CloneModState(selectedCandidates[modId].Mod))
            .ToList();
        var configurations = new List<SerializableModConfiguration>();

        foreach (var modId in orderedModIds)
        {
            var selected = selectedCandidates[modId];
            if (selected.Source.Preset.Configurations is null) continue;

            foreach (var configuration in selected.Source.Preset.Configurations)
                if (configuration is not null
                    && string.Equals(configuration.ModId?.Trim(), modId, StringComparison.OrdinalIgnoreCase))
                    configurations.Add(CloneConfiguration(configuration));
        }

        var mergedPreset = new SerializablePreset
        {
            Name = string.IsNullOrWhiteSpace(definition.Name) ? "Unnamed Modlist Group" : definition.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(definition.Description) ? null : definition.Description.Trim(),
            Version = string.IsNullOrWhiteSpace(definition.Version) ? null : definition.Version.Trim(),
            GameVersion = string.IsNullOrWhiteSpace(definition.GameVersion) ? null : definition.GameVersion.Trim(),
            DisabledEntries = disabledEntries,
            Mods = selectedMods,
            Configurations = configurations.Count > 0 ? configurations : null,
            IncludeModStatus = sources.Any(source => source.Preset.IncludeModStatus == true
                                                           || source.Preset.Mods?.Any(mod => mod?.IsActive is not null) == true),
            IncludeModVersions = sources.Any(source => source.Preset.IncludeModVersions == true
                                                        || source.Preset.Mods?.Any(mod =>
                                                            !string.IsNullOrWhiteSpace(mod?.Version)) == true),
            Exclusive = true
        };

        return new ModlistGroupBuildResult(
            mergedPreset,
            conflicts,
            missingMembers is null ? Array.Empty<string>() : missingMembers.ToList());
    }

    private static bool ReferencesEqual(string left, string? right)
    {
        return !string.IsNullOrWhiteSpace(right)
               && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static ModCandidate SelectDefaultCandidate(IReadOnlyList<ModCandidate> candidates)
    {
        var selected = candidates[0];
        var selectedHasConfiguration = HasCustomConfiguration(selected.Source.Preset, selected.Mod);

        for (var index = 1; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var versionComparison = CompareVersions(candidate.Mod.Version, selected.Mod.Version);
            var candidateHasConfiguration = HasCustomConfiguration(candidate.Source.Preset, candidate.Mod);
            if (versionComparison > 0
                || versionComparison == 0 && candidateHasConfiguration && !selectedHasConfiguration)
            {
                selected = candidate;
                selectedHasConfiguration = candidateHasConfiguration;
            }
        }

        return selected;
    }

    private static int CompareVersions(string? left, string? right)
    {
        var normalizedLeft = VersionStringUtility.Normalize(left);
        var normalizedRight = VersionStringUtility.Normalize(right);
        if (normalizedLeft is not null && normalizedRight is not null)
        {
            var leftParts = normalizedLeft.Split('.').Select(int.Parse).ToArray();
            var rightParts = normalizedRight.Split('.').Select(int.Parse).ToArray();
            var partCount = Math.Max(leftParts.Length, rightParts.Length);
            for (var index = 0; index < partCount; index++)
            {
                var leftPart = index < leftParts.Length ? leftParts[index] : 0;
                var rightPart = index < rightParts.Length ? rightParts[index] : 0;
                var comparison = leftPart.CompareTo(rightPart);
                if (comparison != 0) return comparison;
            }

            return 0;
        }

        if (normalizedLeft is not null) return 1;
        if (normalizedRight is not null) return -1;

        return string.Compare(
            NormalizeOptional(left),
            NormalizeOptional(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasCustomConfiguration(SerializablePreset preset, SerializablePresetModState mod)
    {
        var matchingConfigurations = preset.Configurations?
                                         .Where(configuration => configuration is not null
                                                                 && string.Equals(
                                                                     configuration.ModId?.Trim(),
                                                                     mod.ModId?.Trim(),
                                                                     StringComparison.OrdinalIgnoreCase))
                                         .ToList()
                                     ?? [];

        return matchingConfigurations.Count > 0
            ? matchingConfigurations.Any(configuration => !string.IsNullOrEmpty(configuration.Content))
            : !string.IsNullOrEmpty(ModConfigurationEncoding.Decode(mod.ConfigurationContent));
    }

    private static string BuildChoiceComparisonKey(ModCandidate candidate)
    {
        var mod = candidate.Mod;
        var modId = mod.ModId?.Trim();
        var matchingConfigurations = candidate.Source.Preset.Configurations?
                                         .Where(configuration => configuration is not null
                                                                 && string.Equals(
                                                                     configuration.ModId?.Trim(),
                                                                     modId,
                                                                     StringComparison.OrdinalIgnoreCase))
                                         .ToList()
                                     ?? [];
        var configurations = matchingConfigurations
                                 .Where(configuration => !string.IsNullOrEmpty(configuration.Content))
                                 .Select(configuration => new
                                 {
                                     fileName = NormalizeOptional(configuration.FileName)?.ToUpperInvariant(),
                                     relativePath = NormalizeOptional(configuration.RelativePath)?.ToUpperInvariant(),
                                     Content = configuration.Content!
                                 })
                                 .OrderBy(configuration => configuration.fileName, StringComparer.Ordinal)
                                 .ThenBy(configuration => configuration.relativePath, StringComparer.Ordinal)
                                 .ThenBy(configuration => configuration.Content, StringComparer.Ordinal)
                                 .ToList()
                             ?? [];

        if (matchingConfigurations.Count == 0)
        {
            var inlineContent = ModConfigurationEncoding.Decode(mod.ConfigurationContent);
            if (!string.IsNullOrEmpty(inlineContent))
                configurations.Add(new
                {
                    fileName = NormalizeOptional(mod.ConfigurationFileName)?.ToUpperInvariant(),
                    relativePath = (string?)null,
                    Content = inlineContent
                });
        }

        return JsonSerializer.Serialize(new
        {
            version = NormalizeOptional(mod.Version),
            configurations
        });
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static SerializablePresetModState CloneModState(SerializablePresetModState source)
    {
        return new SerializablePresetModState
        {
            ModId = source.ModId,
            Version = source.Version,
            IsActive = source.IsActive,
            ConfigurationFileName = source.ConfigurationFileName,
            ConfigurationContent = source.ConfigurationContent
        };
    }

    private static SerializableModConfiguration CloneConfiguration(SerializableModConfiguration source)
    {
        return new SerializableModConfiguration
        {
            ModId = source.ModId,
            FileName = source.FileName,
            RelativePath = source.RelativePath,
            Content = source.Content
        };
    }

    private sealed record ModCandidate(ModlistGroupSource Source, SerializablePresetModState Mod);
}
