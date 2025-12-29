using System.Collections.Concurrent;
using System.Text;

namespace VintageStoryModManager.Services;

/// <summary>
///     Provides utilities for normalizing, parsing, and comparing version strings.
/// </summary>
internal static class VersionStringUtility
{
    // Cache for normalized version strings to avoid re-parsing
    private static readonly ConcurrentDictionary<string, string?> NormalizedVersionCache = new();

    // Cache for parsed version parts to avoid repeated parsing
    // Successful parses cache the array directly; failed parses store Array.Empty<int>()
    private static readonly ConcurrentDictionary<string, int[]> ParsedVersionPartsCache = new();
    /// <summary>
    ///     Normalizes a version string by extracting up to four numeric parts separated by dots.
    /// </summary>
    /// <param name="version">The raw version string.</param>
    /// <returns>A normalized version string (e.g., "1.21.5.0"), or null if no valid numeric parts are found.</returns>
    public static string? Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

        // Check cache first
        if (NormalizedVersionCache.TryGetValue(version, out var cached))
            return cached;

        const int MaxNormalizedParts = 4;

        var parts = new List<string>(MaxNormalizedParts);
        var currentPart = new StringBuilder();
        var hasCapturedDigits = false;

        foreach (var c in version)
        {
            if (char.IsDigit(c))
            {
                currentPart.Append(c);
                hasCapturedDigits = true;
                continue;
            }

            if (currentPart.Length > 0)
            {
                parts.Add(currentPart.ToString());
                currentPart.Clear();

                if (parts.Count >= MaxNormalizedParts) break;
            }

            if (char.IsWhiteSpace(c) && hasCapturedDigits) break;
        }

        if (currentPart.Length > 0 && parts.Count < MaxNormalizedParts) parts.Add(currentPart.ToString());

        var result = parts.Count == 0 ? null : string.Join('.', parts);

        // Cache the result
        NormalizedVersionCache.TryAdd(version, result);

        return result;
    }

    /// <summary>
    ///     Determines if a candidate version is newer than the current version.
    /// </summary>
    /// <param name="candidateVersion">The version to check.</param>
    /// <param name="currentVersion">The current version to compare against.</param>
    /// <returns>True if the candidate version is newer; otherwise, false.</returns>
    public static bool IsCandidateVersionNewer(string? candidateVersion, string? currentVersion)
    {
        if (string.IsNullOrWhiteSpace(candidateVersion)) return false;

        var normalizedCandidate = Normalize(candidateVersion);
        var normalizedCurrent = Normalize(currentVersion);

        if (normalizedCandidate is null)
        {
            if (string.IsNullOrWhiteSpace(currentVersion)) return false;

            return !string.Equals(candidateVersion.Trim(), currentVersion.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        if (normalizedCurrent is null) return true;

        if (TryParseVersionParts(normalizedCandidate, out var candidateParts)
            && TryParseVersionParts(normalizedCurrent, out var currentParts))
        {
            var length = Math.Max(candidateParts.Length, currentParts.Length);
            for (var i = 0; i < length; i++)
            {
                var candidatePart = i < candidateParts.Length ? candidateParts[i] : 0;
                var currentPart = i < currentParts.Length ? currentParts[i] : 0;

                if (candidatePart > currentPart) return true;

                if (candidatePart < currentPart) return false;
            }

            return false;
        }

        return !string.Equals(normalizedCandidate, normalizedCurrent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Checks if a candidate version matches the target version or is a valid prefix of it.
    ///     For example, "1.21" matches "1.21.5", and "1.21.5" matches "1.21.5" exactly.
    /// </summary>
    /// <param name="candidateVersion">The version prefix or exact match to check.</param>
    /// <param name="targetVersion">The target version to match against.</param>
    /// <returns>True if the candidate matches the target or is a valid prefix; otherwise, false.</returns>
    public static bool MatchesVersionOrPrefix(string? candidateVersion, string? targetVersion)
    {
        if (string.IsNullOrWhiteSpace(candidateVersion) || string.IsNullOrWhiteSpace(targetVersion)) return false;

        var normalizedCandidate = Normalize(candidateVersion);
        var normalizedTarget = Normalize(targetVersion);

        if (normalizedCandidate is null || normalizedTarget is null) return false;

        if (string.Equals(normalizedCandidate, normalizedTarget, StringComparison.OrdinalIgnoreCase)) return true;

        if (!TryParseVersionParts(normalizedCandidate, out var candidateParts)
            || !TryParseVersionParts(normalizedTarget, out var targetParts))
            return false;

        if (candidateParts.Length == 0 || candidateParts.Length > targetParts.Length) return false;

        for (var i = 0; i < candidateParts.Length; i++)
            if (candidateParts[i] != targetParts[i])
                return false;

        return true;
    }

    /// <summary>
    ///     Checks if a provided version satisfies a minimum version requirement.
    ///     Treats "*" as a wildcard that matches any version.
    /// </summary>
    /// <param name="requestedVersion">The minimum required version (or "*" for any version).</param>
    /// <param name="providedVersion">The version to check.</param>
    /// <returns>True if the provided version meets or exceeds the requested version; otherwise, false.</returns>
    public static bool SatisfiesMinimumVersion(string? requestedVersion, string? providedVersion)
    {
        if (string.IsNullOrWhiteSpace(requestedVersion)
            || string.Equals(requestedVersion.Trim(), "*", StringComparison.Ordinal))
            return true;

        var normalizedRequested = Normalize(requestedVersion);
        if (normalizedRequested is null) return true;

        var normalizedProvided = Normalize(providedVersion);
        if (normalizedProvided is null) return false;

        if (!TryParseVersionParts(normalizedRequested, out var requestedParts)) return true;

        if (!TryParseVersionParts(normalizedProvided, out var providedParts)) return false;

        var length = Math.Max(requestedParts.Length, providedParts.Length);
        for (var i = 0; i < length; i++)
        {
            var requestedPart = i < requestedParts.Length ? requestedParts[i] : 0;
            var providedPart = i < providedParts.Length ? providedParts[i] : 0;

            if (providedPart > requestedPart) return true;

            if (providedPart < requestedPart) return false;
        }

        return true;
    }

    /// <summary>
    ///     Checks if two versions have matching first three numeric parts (major.minor.patch).
    /// </summary>
    /// <param name="version1">The first version to compare.</param>
    /// <param name="version2">The second version to compare.</param>
    /// <returns>True if both versions have at least three parts and the first three match exactly; otherwise, false.</returns>
    public static bool MatchesFirstThreeDigits(string? version1, string? version2)
    {
        if (string.IsNullOrWhiteSpace(version1) || string.IsNullOrWhiteSpace(version2)) return false;

        var normalized1 = Normalize(version1);
        var normalized2 = Normalize(version2);

        if (normalized1 is null || normalized2 is null) return false;

        if (!TryParseVersionParts(normalized1, out var parts1)
            || !TryParseVersionParts(normalized2, out var parts2))
            return false;

        // Both versions must have at least 3 parts for exact comparison
        if (parts1.Length < 3 || parts2.Length < 3) return false;

        // Compare first three parts exactly
        for (var i = 0; i < 3; i++)
            if (parts1[i] != parts2[i])
                return false;

        return true;
    }

    /// <summary>
    ///     Checks if a candidate version tag supports a target version.
    ///     In exact mode, only exact matches or prefix matches are allowed (e.g., "1.21" or "1.21.5" match "1.21.5", but
    ///     "1.21.0" does not).
    ///     In relaxed mode, versions with the same major.minor version are considered compatible
    ///     regardless of patch version (e.g., 1.21.0 is compatible with 1.21.5).
    /// </summary>
    /// <param name="candidateTag">The version tag from mod metadata (e.g., "1.21.0" or "1.21")</param>
    /// <param name="targetVersion">The target game version to check against (e.g., "1.21.5")</param>
    /// <param name="requireExactMatch">If true, uses exact/prefix matching; if false, uses relaxed matching</param>
    /// <returns>True if the candidate tag supports the target version</returns>
    public static bool SupportsVersion(string? candidateTag, string? targetVersion, bool requireExactMatch)
    {
        if (string.IsNullOrWhiteSpace(candidateTag) || string.IsNullOrWhiteSpace(targetVersion)) return false;

        var trimmedTag = candidateTag.Trim();
        var trimmedTarget = targetVersion.Trim();

        // Exact string match always works
        if (string.Equals(trimmedTag, trimmedTarget, StringComparison.OrdinalIgnoreCase)) return true;

        // Check if tag is a prefix (e.g., "1.21" matches "1.21.5")
        if (MatchesVersionOrPrefix(trimmedTag, trimmedTarget)) return true;

        // In exact match mode, we're done - no match found
        if (requireExactMatch) return false;

        // In relaxed mode, check if major.minor versions match
        var normalizedTag = Normalize(trimmedTag);
        var normalizedTarget = Normalize(trimmedTarget);

        if (normalizedTag is null || normalizedTarget is null) return false;

        if (!TryParseVersionParts(normalizedTag, out var tagParts)
            || !TryParseVersionParts(normalizedTarget, out var targetParts))
            return false;

        // Both must have at least major.minor version parts
        if (tagParts.Length < 2 || targetParts.Length < 2) return false;

        // In relaxed mode, major.minor must match (e.g., 1.21.0 is compatible with 1.21.5)
        return tagParts[0] == targetParts[0] && tagParts[1] == targetParts[1];
    }

    /// <summary>
    ///     Parses a normalized version string into an array of integer parts.
    /// </summary>
    /// <param name="normalizedVersion">A normalized version string (e.g., "1.21.5").</param>
    /// <param name="parts">An array of integer version parts if parsing succeeds.</param>
    /// <returns>True if the version string was successfully parsed; otherwise, false.</returns>
    private static bool TryParseVersionParts(string normalizedVersion, out int[] parts)
    {
        // Check cache first - empty array indicates parse failure
        if (ParsedVersionPartsCache.TryGetValue(normalizedVersion, out var cached))
        {
            // Return a defensive copy for successful parses, or Array.Empty for failures
            parts = cached.Length == 0 ? Array.Empty<int>() : (int[])cached.Clone();
            return cached.Length > 0;
        }

        // Use Span-based parsing to avoid allocations
        var span = normalizedVersion.AsSpan();
        var partsList = new List<int>(4); // Most versions have 4 parts or less

        while (!span.IsEmpty)
        {
            var dotIndex = span.IndexOf('.');
            var part = dotIndex >= 0 ? span.Slice(0, dotIndex) : span;

            if (!int.TryParse(part, out var value))
            {
                // Cache the failure using empty array as sentinel
                // Array.Empty is safe to share as it's read-only in practice
                parts = Array.Empty<int>();
                ParsedVersionPartsCache.TryAdd(normalizedVersion, parts);
                return false;
            }

            partsList.Add(value);

            if (dotIndex < 0)
                break;

            span = span.Slice(dotIndex + 1);
        }

        parts = partsList.ToArray();

        // Cache a copy to prevent external modifications
        ParsedVersionPartsCache.TryAdd(normalizedVersion, (int[])parts.Clone());
        return true;
    }
}