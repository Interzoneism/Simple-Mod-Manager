using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace VintageStoryModManager.Services;

/// <summary>
/// Validates public, user-authored online modlist text against the packaged blocked-word list.
/// </summary>
public static class OnlineModlistContentValidator
{
    private const string BlockedWordsResourceSuffix = ".swearWords.xml";
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
    private static readonly Lazy<IReadOnlyList<BlockedTerm>> BlockedTerms = new(LoadBlockedTerms);

    public static bool TryFindBlockedWord(
        IEnumerable<string?> textValues,
        out string blockedWord)
    {
        ArgumentNullException.ThrowIfNull(textValues);

        foreach (var text in textValues)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;

            foreach (var blockedTerm in BlockedTerms.Value)
                if (blockedTerm.Pattern.IsMatch(text))
                {
                    blockedWord = blockedTerm.Text;
                    return true;
                }
        }

        blockedWord = string.Empty;
        return false;
    }

    private static IReadOnlyList<BlockedTerm> LoadBlockedTerms()
    {
        var assembly = typeof(OnlineModlistContentValidator).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(BlockedWordsResourceSuffix, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
            throw new InvalidOperationException("The embedded online modlist blocked-word list could not be found.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException(
                               "The embedded online modlist blocked-word list could not be opened.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });

        var document = XDocument.Load(reader, LoadOptions.None);
        var terms = document.Root?
            .Elements("word")
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(CreateBlockedTerm)
            .ToList();

        return terms is { Count: > 0 }
            ? terms
            : throw new InvalidDataException("The online modlist blocked-word list is empty.");
    }

    private static BlockedTerm CreateBlockedTerm(string text)
    {
        var escapedParts = Regex.Split(text, @"\s+")
            .Where(part => part.Length > 0)
            .Select(Regex.Escape);
        var phrasePattern = string.Join(@"\s+", escapedParts);
        var pattern = $@"(?<![\p{{L}}\p{{N}}]){phrasePattern}(?![\p{{L}}\p{{N}}])";

        return new BlockedTerm(
            text,
            new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout));
    }

    private sealed record BlockedTerm(string Text, Regex Pattern);
}
