namespace VintageStoryModManager.Views.Dialogs;

public sealed class ExperimentalModDebugLogLine
{
    private const int MaxLogLineLength = 300;

    private static readonly string[] HighlightKeywords =
    {
        "error",
        "warning",
        "exception",
        "failed ",
        "aborting",
        "missing"
    };

    private ExperimentalModDebugLogLine(string text, bool isHighlighted, string? modName = null,
        string? filePath = null, int lineNumber = 0)
    {
        Text = text;
        IsHighlighted = isHighlighted;
        ModName = modName;
        FilePath = filePath;
        LineNumber = lineNumber;
    }

    public string Text { get; }

    public bool IsHighlighted { get; }

    public string? ModName { get; }

    public string? FilePath { get; }

    public int LineNumber { get; }

    public static ExperimentalModDebugLogLine FromLogEntry(string rawText, string? modName = null,
        string? filePath = null, int lineNumber = 0)
    {
        ArgumentNullException.ThrowIfNull(rawText);

        var text = rawText.Length > MaxLogLineLength
            ? string.Concat(rawText.AsSpan(0, MaxLogLineLength), "... (log line too long to show)")
            : rawText;

        var isHighlighted = ContainsHighlightKeyword(rawText);
        return new ExperimentalModDebugLogLine(text, isHighlighted, modName, filePath, lineNumber);
    }

    public static ExperimentalModDebugLogLine FromPlainText(string text, string? modName = null,
        string? filePath = null, int lineNumber = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new ExperimentalModDebugLogLine(text, false, modName, filePath, lineNumber);
    }

    private static bool ContainsHighlightKeyword(string text)
    {
        foreach (var keyword in HighlightKeywords)
            if (text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

        return false;
    }
}