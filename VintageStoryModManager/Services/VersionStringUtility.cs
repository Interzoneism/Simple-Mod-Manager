using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VintageStoryModManager.Services;

internal static class VersionStringUtility
{
    public static string? Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        const int MaxNormalizedParts = 4;

        var parts = new List<string>(MaxNormalizedParts);
        var currentPart = new StringBuilder();
        bool hasCapturedDigits = false;

        foreach (char c in version)
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

                if (parts.Count >= MaxNormalizedParts)
                {
                    break;
                }
            }

            if (char.IsWhiteSpace(c) && hasCapturedDigits)
            {
                break;
            }
        }

        if (currentPart.Length > 0 && parts.Count < MaxNormalizedParts)
        {
            parts.Add(currentPart.ToString());
        }

        if (parts.Count == 0)
        {
            return null;
        }

        return string.Join('.', parts);
    }

    public static bool IsCandidateVersionNewer(string? candidateVersion, string? currentVersion)
    {
        if (string.IsNullOrWhiteSpace(candidateVersion))
        {
            return false;
        }

        string? normalizedCandidate = Normalize(candidateVersion);
        string? normalizedCurrent = Normalize(currentVersion);

        if (normalizedCandidate is null)
        {
            if (string.IsNullOrWhiteSpace(currentVersion))
            {
                return false;
            }

            return !string.Equals(candidateVersion.Trim(), currentVersion.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        if (normalizedCurrent is null)
        {
            return true;
        }

        if (TryParseVersionParts(normalizedCandidate, out var candidateParts)
            && TryParseVersionParts(normalizedCurrent, out var currentParts))
        {
            int length = Math.Max(candidateParts.Length, currentParts.Length);
            for (int i = 0; i < length; i++)
            {
                int candidatePart = i < candidateParts.Length ? candidateParts[i] : 0;
                int currentPart = i < currentParts.Length ? currentParts[i] : 0;

                if (candidatePart > currentPart)
                {
                    return true;
                }

                if (candidatePart < currentPart)
                {
                    return false;
                }
            }

            return false;
        }

        return !string.Equals(normalizedCandidate, normalizedCurrent, StringComparison.OrdinalIgnoreCase);
    }

    public static bool MatchesVersionOrPrefix(string? candidateVersion, string? targetVersion)
    {
        if (string.IsNullOrWhiteSpace(candidateVersion) || string.IsNullOrWhiteSpace(targetVersion))
        {
            return false;
        }

        string? normalizedCandidate = Normalize(candidateVersion);
        string? normalizedTarget = Normalize(targetVersion);

        if (normalizedCandidate is null || normalizedTarget is null)
        {
            return false;
        }

        if (string.Equals(normalizedCandidate, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!TryParseVersionParts(normalizedCandidate, out var candidateParts)
            || !TryParseVersionParts(normalizedTarget, out var targetParts))
        {
            return false;
        }

        if (candidateParts.Length == 0 || candidateParts.Length > targetParts.Length)
        {
            return false;
        }

        for (int i = 0; i < candidateParts.Length; i++)
        {
            if (candidateParts[i] != targetParts[i])
            {
                return false;
            }
        }

        return true;
    }

    public static bool SatisfiesMinimumVersion(string? requestedVersion, string? providedVersion)
    {
        if (string.IsNullOrWhiteSpace(requestedVersion)
            || string.Equals(requestedVersion.Trim(), "*", StringComparison.Ordinal))
        {
            return true;
        }

        string? normalizedRequested = Normalize(requestedVersion);
        if (normalizedRequested is null)
        {
            return true;
        }

        string? normalizedProvided = Normalize(providedVersion);
        if (normalizedProvided is null)
        {
            return false;
        }

        if (!TryParseVersionParts(normalizedRequested, out var requestedParts))
        {
            return true;
        }

        if (!TryParseVersionParts(normalizedProvided, out var providedParts))
        {
            return false;
        }

        int length = Math.Max(requestedParts.Length, providedParts.Length);
        for (int i = 0; i < length; i++)
        {
            int requestedPart = i < requestedParts.Length ? requestedParts[i] : 0;
            int providedPart = i < providedParts.Length ? providedParts[i] : 0;

            if (providedPart > requestedPart)
            {
                return true;
            }

            if (providedPart < requestedPart)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseVersionParts(string normalizedVersion, out int[] parts)
    {
        string[] tokens = normalizedVersion.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        parts = new int[tokens.Length];

        for (int i = 0; i < tokens.Length; i++)
        {
            if (!int.TryParse(tokens[i], out parts[i]))
            {
                parts = Array.Empty<int>();
                return false;
            }
        }

        return true;
    }
}
