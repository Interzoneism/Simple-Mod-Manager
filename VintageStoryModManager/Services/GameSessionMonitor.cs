using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace VintageStoryModManager.Services;

/// <summary>
///     Monitors Vintage Story log files to detect long-running game sessions.
/// </summary>
public sealed class GameSessionMonitor : IDisposable
{
    private static readonly TimeSpan MinimumSessionDuration = DevConfig.MinimumSessionDuration;

    private static readonly Regex VintageStoryTimestampRegex =
        new(@"^(\d{1,2})\.(\d{1,2})\.(\d{4})\s+(\d{1,2}):(\d{2}):(\d{2})", RegexOptions.Compiled);

    private static readonly Regex Iso8601TimestampRegex =
        new(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})", RegexOptions.Compiled);

    private static readonly string[] ClientStartMarkers =
    {
        "Received level finalize"
    };

    private static readonly string[] ClientEndMarkers =
    {
        "Exiting current game"
    };

    private static readonly string[] ServerEndMarkers =
    {
        "server shutting down"
    };

    private readonly Func<IReadOnlyList<ModUsageTrackingEntry>> _activeModProvider;
    private readonly UserConfigurationService _configuration;
    private readonly Dispatcher _dispatcher;

    private readonly string _logsDirectory;
    private readonly ConcurrentDictionary<string, LogTailState> _logStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sessionLock = new();

    private FileSystemWatcher? _clientWatcher;
    private bool _disposed;
    private bool _lastPromptState;
    private FileSystemWatcher? _serverWatcher;
    private bool _sessionActive;
    private List<ModUsageTrackingEntry> _sessionModEntries = new();
    private DateTimeOffset _sessionStartUtc;

    public GameSessionMonitor(
        string logsDirectory,
        Dispatcher dispatcher,
        UserConfigurationService configuration,
        Func<IReadOnlyList<ModUsageTrackingEntry>> activeModProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _activeModProvider = activeModProvider ?? throw new ArgumentNullException(nameof(activeModProvider));

        _logsDirectory = Path.GetFullPath(logsDirectory);

        try
        {
            Directory.CreateDirectory(_logsDirectory);
            InitializeWatchers();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusLogService.AppendStatus(
                string.Format(CultureInfo.CurrentCulture, "Failed to initialize log monitor: {0}", ex.Message),
                true);
        }

        _lastPromptState = _configuration.HasPendingModUsagePrompt;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _clientWatcher?.Dispose();
        _clientWatcher = null;
        _serverWatcher?.Dispose();
        _serverWatcher = null;
    }

    public event EventHandler? PromptRequired;

    public void RefreshPromptState()
    {
        _lastPromptState = _configuration.HasPendingModUsagePrompt;
    }

    private void InitializeWatchers()
    {
        _clientWatcher = CreateWatcher("client-main*");
        if (_clientWatcher is not null)
        {
            _clientWatcher.Changed += (_, e) => HandleLogEvent(e.FullPath, LogCategory.Client);
            _clientWatcher.Created += (_, e) => HandleLogEvent(e.FullPath, LogCategory.Client);
            _clientWatcher.Renamed += (_, e) => HandleLogEvent(e.FullPath, LogCategory.Client);
            _clientWatcher.Renamed += (_, e) => RemoveLogState(e.OldFullPath);
            _clientWatcher.Deleted += (_, e) => RemoveLogState(e.FullPath);
            _clientWatcher.EnableRaisingEvents = true;
            InitializeExistingLogs(LogCategory.Client);
        }

        _serverWatcher = CreateWatcher("server-main*");
        if (_serverWatcher is not null)
        {
            _serverWatcher.Changed += (_, e) => HandleLogEvent(e.FullPath, LogCategory.Server);
            _serverWatcher.Created += (_, e) => HandleLogEvent(e.FullPath, LogCategory.Server);
            _serverWatcher.Renamed += (_, e) => HandleLogEvent(e.FullPath, LogCategory.Server);
            _serverWatcher.Renamed += (_, e) => RemoveLogState(e.OldFullPath);
            _serverWatcher.Deleted += (_, e) => RemoveLogState(e.FullPath);
            _serverWatcher.EnableRaisingEvents = true;
            InitializeExistingLogs(LogCategory.Server);
        }
    }

    private FileSystemWatcher? CreateWatcher(string filter)
    {
        try
        {
            return new FileSystemWatcher(_logsDirectory, filter)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = false
            };
        }
        catch (ArgumentException)
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

    private void InitializeExistingLogs(LogCategory category)
    {
        try
        {
            foreach (var path in EnumerateCategoryFiles(category)) EnsureLogState(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusLogService.AppendStatus(
                string.Format(CultureInfo.CurrentCulture, "Failed to enumerate existing logs: {0}", ex.Message),
                true);
        }
    }

    private IEnumerable<string> EnumerateCategoryFiles(LogCategory category)
    {
        var prefix = category == LogCategory.Client ? "client-main" : "server-main";

        IEnumerable<string> Enumerate(string pattern)
        {
            return Directory.EnumerateFiles(_logsDirectory, pattern, SearchOption.TopDirectoryOnly);
        }

        foreach (var file in Enumerate(prefix + "*.log")) yield return file;

        foreach (var file in Enumerate(prefix + "*.txt")) yield return file;

        foreach (var file in Enumerate(prefix + "*.json")) yield return file;
    }

    private void HandleLogEvent(string? path, LogCategory category)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path)) return;

        _ = Task.Run(() => ProcessLogChangesAsync(category, path));
    }

    private void RemoveLogState(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        _logStates.TryRemove(Path.GetFullPath(path), out _);
    }

    private async Task ProcessLogChangesAsync(LogCategory category, string path)
    {
        if (_disposed) return;

        var fullPath = Path.GetFullPath(path);
        if (!IsSupportedLog(fullPath)) return;

        var state = EnsureLogState(fullPath);
        var currentFirstLine = TryReadFirstLine(fullPath);

        if (currentFirstLine is not null)
            lock (state.SyncRoot)
            {
                if (!string.Equals(state.FirstLineReference, currentFirstLine, StringComparison.Ordinal))
                {
                    state.FirstLineReference = currentFirstLine;
                    state.HasProcessedReference = false;
                    state.Position = 0;
                }
                else if (state.HasProcessedReference)
                {
                    return;
                }
            }

        var newLines = new List<string>();
        try
        {
            using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            lock (state.SyncRoot)
            {
                if (state.Position > stream.Length) state.Position = 0;

                if (stream.Length == state.Position) return;

                stream.Seek(state.Position, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true);
                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    if (line is null) break;

                    if (line.Length > 0) newLines.Add(line);
                }

                state.Position = stream.Position;
            }
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var line in newLines)
        {
            var recorded = await HandleLogLineAsync(category, line, state).ConfigureAwait(false);
            if (recorded) break;
        }
    }

    private static bool IsSupportedLog(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return false;

        var fileName = Path.GetFileName(path);
        return fileName.Contains("client-main", StringComparison.OrdinalIgnoreCase)
               || fileName.Contains("server-main", StringComparison.OrdinalIgnoreCase);
    }

    private LogTailState EnsureLogState(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return _logStates.GetOrAdd(fullPath, CreateInitialLogState);
    }

    private LogTailState CreateInitialLogState(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return new LogTailState(info.Exists ? info.Length : 0);
        }
        catch (IOException)
        {
            return new LogTailState(0);
        }
        catch (UnauthorizedAccessException)
        {
            return new LogTailState(0);
        }
    }

    private static string? TryReadFirstLine(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (line is null) break;

                if (line.Length == 0) continue;

                return line;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static bool TryExtractTimestamp(string line, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (string.IsNullOrWhiteSpace(line)) return false;

        var trimmed = line.Trim();
        if (trimmed.Length == 0) return false;

        if (trimmed.StartsWith("{", StringComparison.Ordinal))
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (TryGetJsonTimestamp(document.RootElement, out timestamp)) return true;
            }
            catch (JsonException)
            {
            }

        // Try Vintage Story log format: DD.MM.YYYY HH:MM:SS
        var vsMatch = VintageStoryTimestampRegex.Match(trimmed);
        if (vsMatch.Success)
            try
            {
                var day = int.Parse(vsMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                var month = int.Parse(vsMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                var year = int.Parse(vsMatch.Groups[3].Value, CultureInfo.InvariantCulture);
                var hour = int.Parse(vsMatch.Groups[4].Value, CultureInfo.InvariantCulture);
                var minute = int.Parse(vsMatch.Groups[5].Value, CultureInfo.InvariantCulture);
                var second = int.Parse(vsMatch.Groups[6].Value, CultureInfo.InvariantCulture);

                var dateTime = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
                var offset = TimeZoneInfo.Local.GetUtcOffset(dateTime);
                timestamp = new DateTimeOffset(dateTime, offset);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                // Invalid date/time values
            }
            catch (FormatException)
            {
                // Failed to parse numbers
            }

        // Try ISO 8601 format: YYYY-MM-DDTHH:MM:SS
        var match = Iso8601TimestampRegex.Match(trimmed);
        if (match.Success && DateTimeOffset.TryParse(match.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out timestamp)) return true;

        return false;
    }

    private static bool TryGetJsonTimestamp(JsonElement element, out DateTimeOffset timestamp)
    {
        if (TryReadJsonString(element, "@t", out var value)
            || TryReadJsonString(element, "time", out value)
            || TryReadJsonString(element, "timestamp", out value)
            || TryReadJsonString(element, "Time", out value)
            || TryReadJsonString(element, "Timestamp", out value))
            if (!string.IsNullOrEmpty(value)
                && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                    out timestamp))
                return true;

        timestamp = default;
        return false;
    }

    private static bool TryReadJsonString(JsonElement element, string propertyName, out string? value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var property))
            if (property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString();
                return !string.IsNullOrEmpty(value);
            }

        value = null;
        return false;
    }

    private async Task<bool> HandleLogLineAsync(LogCategory category, string line, LogTailState state)
    {
        DateTimeOffset? timestamp = TryExtractTimestamp(line, out var parsedTimestamp)
            ? parsedTimestamp
            : null;

        if (category == LogCategory.Client)
        {
            if (ContainsMarker(line, ClientStartMarkers))
            {
                await StartSessionAsync(timestamp).ConfigureAwait(false);
                return false;
            }

            if (ContainsMarker(line, ClientEndMarkers))
            {
                await CompleteSessionAsync("client-main", timestamp).ConfigureAwait(false);
                lock (state.SyncRoot)
                {
                    state.HasProcessedReference = true;
                }

                return true;
            }
        }
        else if (category == LogCategory.Server)
        {
            if (ContainsMarker(line, ServerEndMarkers))
            {
                await CompleteSessionAsync("server-main", timestamp).ConfigureAwait(false);
                lock (state.SyncRoot)
                {
                    state.HasProcessedReference = true;
                }

                return true;
            }
        }

        return false;
    }

    private static bool ContainsMarker(string line, IReadOnlyList<string> markers)
    {
        foreach (var marker in markers)
            if (line.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

        return false;
    }

    private async Task StartSessionAsync(DateTimeOffset? logTimestamp)
    {
        IReadOnlyList<ModUsageTrackingEntry>? activeMods = null;
        try
        {
            activeMods = await _dispatcher.InvokeAsync(_activeModProvider);
        }
        catch (TaskCanceledException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        lock (_sessionLock)
        {
            if (_sessionActive && _sessionStartUtc != default) return;

            _sessionActive = true;
            _sessionStartUtc = logTimestamp ?? DateTimeOffset.UtcNow;
            _sessionModEntries = NormalizeEntries(activeMods);
        }

        StatusLogService.AppendStatus("Detected Vintage Story game session start from logs.", false);
    }

    private async Task<bool> CompleteSessionAsync(string source, DateTimeOffset? logTimestamp)
    {
        List<ModUsageTrackingEntry> modEntries;
        DateTimeOffset start;
        lock (_sessionLock)
        {
            if (!_sessionActive) return false;

            _sessionActive = false;
            start = _sessionStartUtc;
            modEntries = _sessionModEntries;
            _sessionStartUtc = default;
            _sessionModEntries = new List<ModUsageTrackingEntry>();
        }

        if (start == default) return false;

        var end = logTimestamp ?? DateTimeOffset.UtcNow;
        if (end < start) end = start;

        var duration = end - start;
        if (duration < MinimumSessionDuration)
        {
            StatusLogService.AppendStatus(
                string.Format(
                    CultureInfo.CurrentCulture,
                    "Game session ended after {0:F1} minutes (source: {1}); below {2:F0} minute threshold.",
                    duration.TotalMinutes,
                    source),
                false);
            return false;
        }

        var recordedUsage = false;
        var shouldPrompt = false;
        try
        {
            shouldPrompt = await _dispatcher.InvokeAsync(() =>
            {
                var prompt = _configuration.RecordLongRunningSession(modEntries, out var recorded);
                recordedUsage = recorded;
                return prompt;
            });
        }
        catch (TaskCanceledException)
        {
            return recordedUsage;
        }

        StatusLogService.AppendStatus(
            string.Format(
                CultureInfo.CurrentCulture,
                "Game session lasted {0:F1} minutes; recorded for mod usage tracking (source: {1}).",
                duration.TotalMinutes,
                source),
            false);

        var isPending = _configuration.HasPendingModUsagePrompt;
        if (shouldPrompt && !_lastPromptState)
        {
            _lastPromptState = true;
            PromptRequired?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _lastPromptState = isPending;
        }

        return recordedUsage;
    }

    private static List<ModUsageTrackingEntry> NormalizeEntries(IReadOnlyList<ModUsageTrackingEntry>? entries)
    {
        if (entries is null || entries.Count == 0) return new List<ModUsageTrackingEntry>();

        var distinct = new HashSet<ModUsageTrackingKey>();
        var normalized = new List<ModUsageTrackingEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.ModId)
                || string.IsNullOrEmpty(entry.ModVersion)
                || string.IsNullOrEmpty(entry.GameVersion))
                continue;

            if (!entry.CanSubmitVote || entry.HasUserVote) continue;

            var key = new ModUsageTrackingKey(entry.ModId, entry.ModVersion, entry.GameVersion);
            if (!distinct.Add(key)) continue;

            normalized.Add(new ModUsageTrackingEntry(
                entry.ModId,
                entry.ModVersion,
                entry.GameVersion,
                entry.CanSubmitVote,
                entry.HasUserVote));
        }

        return normalized;
    }

    private enum LogCategory
    {
        Client,
        Server
    }

    private sealed class LogTailState
    {
        public LogTailState(long initialPosition)
        {
            Position = initialPosition < 0 ? 0 : initialPosition;
        }

        public long Position { get; set; }

        public object SyncRoot { get; } = new();

        public string? FirstLineReference { get; set; }

        public bool HasProcessedReference { get; set; }
    }
}