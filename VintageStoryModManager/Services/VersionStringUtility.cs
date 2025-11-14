using System.Text;

namespace VintageStoryModManager.Services;

internal static class VersionStringUtility
{
    public static string? Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

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

        if (parts.Count == 0) return null;

        return string.Join('.', parts);
    }

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

    private static bool TryParseVersionParts(string normalizedVersion, out int[] parts)
    {
        var tokens =
            normalizedVersion.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        parts = new int[tokens.Length];

        for (var i = 0; i < tokens.Length; i++)
            if (!int.TryParse(tokens[i], out parts[i]))
            {
                parts = Array.Empty<int>();
                return false;
            }

        return true;
    }
}