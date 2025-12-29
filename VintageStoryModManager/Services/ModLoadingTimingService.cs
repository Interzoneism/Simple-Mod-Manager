using System.Diagnostics;
using System.Globalization;

namespace VintageStoryModManager.Services;

/// <summary>
///     Tracks cumulative timing metrics for mod loading operations.
///     All times are accumulated regardless of whether they occur during initial load, refresh, or fast check.
/// </summary>
public sealed class ModLoadingTimingService
{
    private readonly object _lock = new();

    // Cumulative timing metrics (in milliseconds)
    private double _totalIconLoadingTimeMs;
    private double _totalTagsLoadingTimeMs;
    private double _totalUserReportsLoadingTimeMs;
    private double _totalDependencyChecksTimeMs;
    private double _totalUpdateCheckTimeMs;
    private double _totalChangelogLoadingTimeMs;
    private double _totalDatabaseInfoLoadingTimeMs;
    private double _totalDbCacheLoadingTimeMs;
    private double _totalDbNetworkLoadingTimeMs;
    private double _totalDbApplyInfoTimeMs;
    private double _totalDbOfflineInfoTimeMs;

    // Network loading sub-operations
    private double _totalDbNetworkHttpTimeMs;
    private double _totalDbNetworkParseTimeMs;
    private double _totalDbNetworkExtractTimeMs;
    private double _totalDbNetworkStoreTimeMs;

    // Apply info sub-operations
    private double _totalDbApplyDispatcherTimeMs;
    private double _totalDbApplyEntryUpdateTimeMs;
    private double _totalDbApplyViewModelUpdateTimeMs;
    private double _totalDbApplyUiHandlerTimeMs;
    private double _maxDbApplyUiHandlerTimeMs;

    // Count of operations for averaging
    private int _iconLoadCount;
    private int _tagsLoadCount;
    private int _userReportsLoadCount;
    private int _dependencyCheckCount;
    private int _updateCheckCount;
    private int _changelogLoadCount;
    private int _databaseInfoLoadCount;
    private int _dbCacheLoadCount;
    private int _dbNetworkLoadCount;
    private int _dbApplyInfoCount;
    private int _dbOfflineInfoCount;

    // Network loading sub-operation counts
    private int _dbNetworkHttpCount;
    private int _dbNetworkParseCount;
    private int _dbNetworkExtractCount;
    private int _dbNetworkStoreCount;

    // Apply info sub-operation counts
    private int _dbApplyDispatcherCount;
    private int _dbApplyEntryUpdateCount;
    private int _dbApplyViewModelUpdateCount;
    private int _dbApplyUiHandlerCount;
    private int _dbApplyUiHandlerSlowCount;
    private int _dbApplyUiHandlerUpdates;
    private int _dbApplyUiHandlerSmallBatchCount;

    /// <summary>
    ///     Records time spent loading an icon.
    /// </summary>
    public void RecordIconLoadTime(double milliseconds)
    {
        lock (_lock)
        {
            _totalIconLoadingTimeMs += milliseconds;
            _iconLoadCount++;
        }
    }

    /// <summary>
    ///     Records time spent loading tags.
    /// </summary>
    public void RecordTagsLoadTime(double milliseconds)
    {
        lock (_lock)
        {
            _totalTagsLoadingTimeMs += milliseconds;
            _tagsLoadCount++;
        }
    }

    /// <summary>
    ///     Records time spent loading user reports/votes.
    /// </summary>
    public void RecordUserReportsLoadTime(double milliseconds)
    {
        lock (_lock)
        {
            _totalUserReportsLoadingTimeMs += milliseconds;
            _userReportsLoadCount++;
        }
    }

    /// <summary>
    ///     Records time spent checking dependencies/errors/warnings.
    /// </summary>
    public void RecordDependencyCheckTime(double milliseconds)
    {
        lock (_lock)
        {
            _totalDependencyChecksTimeMs += milliseconds;
            _dependencyCheckCount++;
        }
    }

    /// <summary>
    ///     Records time spent checking for updates.
    /// </summary>
    public void RecordUpdateCheckTime(double milliseconds)
    {
        lock (_lock)
        {
            _totalUpdateCheckTimeMs += milliseconds;
            _updateCheckCount++;
        }
    }

    /// <summary>
    ///     Records time spent loading changelogs.
    /// </summary>
    public void RecordChangelogLoadTime(double milliseconds)
    {
        lock (_lock)
        {
            _totalChangelogLoadingTimeMs += milliseconds;
            _changelogLoadCount++;
        }
    }

    /// <summary>
    ///     Records time spent loading database info (general metadata).
    /// </summary>
    public void RecordDatabaseInfoLoadTime(double milliseconds)
    {
        lock (_lock)
        {
            _totalDatabaseInfoLoadingTimeMs += milliseconds;
            _databaseInfoLoadCount++;
        }
    }

    /// <summary>
    ///     Records time spent loading database info from cache.
    /// </summary>
    public void RecordDbCacheLoadTime(double milliseconds)
    {
        lock (_lock)
        {
            _totalDbCacheLoadingTimeMs += milliseconds;
            _dbCacheLoadCount++;
        }
    }

    /// <summary>
    ///     Records time spent loading database info from network.
    /// </summary>
    public void RecordDbNetworkLoadTime(double milliseconds)
    {
        lock (_lock)
        {
            _totalDbNetworkLoadingTimeMs += milliseconds;
            _dbNetworkLoadCount++;
        }
    }

    /// <summary>
    ///     Records time spent applying database info to mod entry.
    /// </summary>
    public void RecordDbApplyInfoTime(double milliseconds)
    {
        lock (_lock)
        {
            _totalDbApplyInfoTimeMs += milliseconds;
            _dbApplyInfoCount++;
        }
    }

    /// <summary>
    ///     Records time spent populating offline database info.
    /// </summary>
    public void RecordDbOfflineInfoTime(double milliseconds)
    {
        lock (_lock)
        {
            _totalDbOfflineInfoTimeMs += milliseconds;
            _dbOfflineInfoCount++;
        }
    }

    /// <summary>
    ///     Records time spent on HTTP request/response during network loading.
    /// </summary>
    public void RecordDbNetworkHttpTime(double milliseconds)
    {
        lock (_lock)
        {
            _totalDbNetworkHttpTimeMs += milliseconds;
            _dbNetworkHttpCount++;
        }
    }

    /// <summary>
    ///     Records time spent parsing JSON during network loading.
    /// </summary>
    public void RecordDbNetworkParseTime(double milliseconds)
    {
        lock (_lock)
        {
            _totalDbNetworkParseTimeMs += milliseconds;
            _dbNetworkParseCount++;
        }
    }

    /// <summary>
    ///     Records time spent extracting/processing data during network loading.
    /// </summary>
    public void RecordDbNetworkExtractTime(double milliseconds)
    {
        lock (_lock)
        {
            _totalDbNetworkExtractTimeMs += milliseconds;
            _dbNetworkExtractCount++;
        }
    }

    /// <summary>
    ///     Records time spent storing to cache during network loading.
    /// </summary>
    public void RecordDbNetworkStoreTime(double milliseconds)
    {
        lock (_lock)
        {
            _totalDbNetworkStoreTimeMs += milliseconds;
            _dbNetworkStoreCount++;
        }
    }

    /// <summary>
    ///     Records time spent waiting for dispatcher during apply info.
    /// </summary>
    public void RecordDbApplyDispatcherTime(double milliseconds)
    {
        lock (_lock)
        {
            _totalDbApplyDispatcherTimeMs += milliseconds;
            _dbApplyDispatcherCount++;
        }
    }

    /// <summary>
    ///     Records time spent updating entry during apply info.
    /// </summary>
    public void RecordDbApplyEntryUpdateTime(double milliseconds)
    {
        lock (_lock)
        {
            _totalDbApplyEntryUpdateTimeMs += milliseconds;
            _dbApplyEntryUpdateCount++;
        }
    }

    /// <summary>
    ///     Records time spent updating view model during apply info.
    /// </summary>
    public void RecordDbApplyViewModelUpdateTime(double milliseconds)
    {
        lock (_lock)
        {
            _totalDbApplyViewModelUpdateTimeMs += milliseconds;
            _dbApplyViewModelUpdateCount++;
        }
    }

    /// <summary>
    ///     Records time spent executing the UI thread handler that applies database info.
    ///     Also tracks how many updates were processed to identify high-frequency dispatcher postings.
    /// </summary>
    public void RecordDbApplyUiHandlerTime(double milliseconds, int updatesApplied)
    {
        lock (_lock)
        {
            _totalDbApplyUiHandlerTimeMs += milliseconds;
            _dbApplyUiHandlerCount++;
            _dbApplyUiHandlerUpdates += Math.Max(0, updatesApplied);
            _maxDbApplyUiHandlerTimeMs = Math.Max(_maxDbApplyUiHandlerTimeMs, milliseconds);

            if (milliseconds >= 50)
                _dbApplyUiHandlerSlowCount++;

            if (updatesApplied <= 2)
                _dbApplyUiHandlerSmallBatchCount++;
        }

        if (milliseconds >= 50 || updatesApplied <= 2)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Timing] UI handler applied {updatesApplied} database update(s) in {milliseconds:F2}ms " +
                "(slow handler or small batch detected).");
        }
    }

    /// <summary>
    ///     Gets a formatted summary of all timing metrics for logging.
    /// </summary>
    public string GetTimingSummary()
    {
        lock (_lock)
        {
            var lines = new List<string>
            {
                "=== Mod Loading Performance Metrics ===",
                "",
                FormatMetric("Icon Loading", _totalIconLoadingTimeMs, _iconLoadCount),
                FormatMetric("Tags Loading", _totalTagsLoadingTimeMs, _tagsLoadCount),
                FormatMetric("User Reports/Votes Loading", _totalUserReportsLoadingTimeMs, _userReportsLoadCount),
                FormatMetric("Dependency/Error/Warning Checks", _totalDependencyChecksTimeMs, _dependencyCheckCount),
                FormatMetric("Update Checks", _totalUpdateCheckTimeMs, _updateCheckCount),
                FormatMetric("Changelog Loading", _totalChangelogLoadingTimeMs, _changelogLoadCount),
                FormatMetric("Database Info Loading", _totalDatabaseInfoLoadingTimeMs, _databaseInfoLoadCount),
            };

            // Add detailed breakdown of Database Info Loading if we have any sub-operations
            if (_dbCacheLoadCount > 0 || _dbNetworkLoadCount > 0 || _dbApplyInfoCount > 0 || _dbOfflineInfoCount > 0)
            {
                lines.Add("");
                lines.Add("  Database Info Loading Breakdown:");
                lines.Add($"  {FormatMetric("Cache Loading", _totalDbCacheLoadingTimeMs, _dbCacheLoadCount)}");
                lines.Add($"  {FormatMetric("Network Loading", _totalDbNetworkLoadingTimeMs, _dbNetworkLoadCount)}");

                // Add Network Loading sub-breakdown if we have data
                if (_dbNetworkHttpCount > 0 || _dbNetworkParseCount > 0 || _dbNetworkExtractCount > 0 || _dbNetworkStoreCount > 0)
                {
                    lines.Add("");
                    lines.Add("    Network Loading Breakdown:");
                    lines.Add($"    {FormatMetric("HTTP Request/Response", _totalDbNetworkHttpTimeMs, _dbNetworkHttpCount)}");
                    lines.Add($"    {FormatMetric("JSON Parsing", _totalDbNetworkParseTimeMs, _dbNetworkParseCount)}");
                    lines.Add($"    {FormatMetric("Data Extraction", _totalDbNetworkExtractTimeMs, _dbNetworkExtractCount)}");
                    lines.Add($"    {FormatMetric("Cache Storage", _totalDbNetworkStoreTimeMs, _dbNetworkStoreCount)}");
                }

                lines.Add($"  {FormatMetric("Applying Info", _totalDbApplyInfoTimeMs, _dbApplyInfoCount)}");

                // Add Applying Info sub-breakdown if we have data
                if (_dbApplyDispatcherCount > 0 || _dbApplyEntryUpdateCount > 0 || _dbApplyViewModelUpdateCount > 0
                    || _dbApplyUiHandlerCount > 0)
                {
                    lines.Add("");
                    lines.Add("    Applying Info Breakdown:");
                    lines.Add($"    {FormatMetric("Dispatcher Wait", _totalDbApplyDispatcherTimeMs, _dbApplyDispatcherCount)}");
                    lines.Add($"    {FormatMetric("Entry Update", _totalDbApplyEntryUpdateTimeMs, _dbApplyEntryUpdateCount)}");
                    lines.Add($"    {FormatMetric("ViewModel Update", _totalDbApplyViewModelUpdateTimeMs, _dbApplyViewModelUpdateCount)}");
                    lines.Add($"    {FormatUiHandlerMetric()}");
                }

                lines.Add($"  {FormatMetric("Offline Info Population", _totalDbOfflineInfoTimeMs, _dbOfflineInfoCount)}");
            }

            lines.Add("");
            lines.Add($"Total Time Across All Operations: {FormatTime(_totalIconLoadingTimeMs + _totalTagsLoadingTimeMs + _totalUserReportsLoadingTimeMs + _totalDependencyChecksTimeMs + _totalUpdateCheckTimeMs + _totalChangelogLoadingTimeMs + _totalDatabaseInfoLoadingTimeMs)}");
            lines.Add("=======================================");

            return string.Join(Environment.NewLine, lines);
        }
    }

    private static string FormatMetric(string label, double totalMs, int count)
    {
        if (count == 0)
        {
            return $"{label}: No operations recorded";
        }

        var avgMs = totalMs / count;
        return $"{label}: {FormatTime(totalMs)} total ({count} ops, avg {FormatTime(avgMs)}/op)";
    }

    private static string FormatTime(double milliseconds)
    {
        if (milliseconds < 1000)
        {
            return $"{milliseconds:F2}ms";
        }

        var seconds = milliseconds / 1000.0;
        return $"{seconds:F2}s";
    }

    private string FormatUiHandlerMetric()
    {
        if (_dbApplyUiHandlerCount == 0)
            return "UI Handler: No operations recorded";

        var baseMetric = FormatMetric("UI Handler", _totalDbApplyUiHandlerTimeMs, _dbApplyUiHandlerCount);
        var avgUpdatesPerDispatch = _dbApplyUiHandlerCount == 0
            ? "n/a"
            : $"{(double)_dbApplyUiHandlerUpdates / _dbApplyUiHandlerCount:F1} updates/dispatch";
        var slowPart = _dbApplyUiHandlerSlowCount > 0
            ? $", {_dbApplyUiHandlerSlowCount} slow (>=50ms)"
            : string.Empty;
        var smallBatchPart = _dbApplyUiHandlerSmallBatchCount > 0
            ? $", {_dbApplyUiHandlerSmallBatchCount} small batches (<=2 updates)"
            : string.Empty;

        return $"{baseMetric} (avg {avgUpdatesPerDispatch}, max {FormatTime(_maxDbApplyUiHandlerTimeMs)}{slowPart}{smallBatchPart})";
    }

    /// <summary>
    ///     Starts a timing operation and returns a disposable that records the elapsed time when disposed.
    /// </summary>
    public IDisposable MeasureIconLoad()
    {
        return new TimingScope(this, RecordIconLoadTime);
    }

    /// <summary>
    ///     Starts a timing operation and returns a disposable that records the elapsed time when disposed.
    /// </summary>
    public IDisposable MeasureTagsLoad()
    {
        return new TimingScope(this, RecordTagsLoadTime);
    }

    /// <summary>
    ///     Starts a timing operation and returns a disposable that records the elapsed time when disposed.
    /// </summary>
    public IDisposable MeasureUserReportsLoad()
    {
        return new TimingScope(this, RecordUserReportsLoadTime);
    }

    /// <summary>
    ///     Starts a timing operation and returns a disposable that records the elapsed time when disposed.
    /// </summary>
    public IDisposable MeasureDependencyCheck()
    {
        return new TimingScope(this, RecordDependencyCheckTime);
    }

    /// <summary>
    ///     Starts a timing operation and returns a disposable that records the elapsed time when disposed.
    /// </summary>
    public IDisposable MeasureUpdateCheck()
    {
        return new TimingScope(this, RecordUpdateCheckTime);
    }

    /// <summary>
    ///     Starts a timing operation and returns a disposable that records the elapsed time when disposed.
    /// </summary>
    public IDisposable MeasureChangelogLoad()
    {
        return new TimingScope(this, RecordChangelogLoadTime);
    }

    /// <summary>
    ///     Starts a timing operation and returns a disposable that records the elapsed time when disposed.
    /// </summary>
    public IDisposable MeasureDatabaseInfoLoad()
    {
        return new TimingScope(this, RecordDatabaseInfoLoadTime);
    }

    /// <summary>
    ///     Starts a timing operation and returns a disposable that records the elapsed time when disposed.
    /// </summary>
    public IDisposable MeasureDbCacheLoad()
    {
        return new TimingScope(this, RecordDbCacheLoadTime);
    }

    /// <summary>
    ///     Starts a timing operation and returns a disposable that records the elapsed time when disposed.
    /// </summary>
    public IDisposable MeasureDbNetworkLoad()
    {
        return new TimingScope(this, RecordDbNetworkLoadTime);
    }

    /// <summary>
    ///     Starts a timing operation and returns a disposable that records the elapsed time when disposed.
    /// </summary>
    public IDisposable MeasureDbApplyInfo()
    {
        return new TimingScope(this, RecordDbApplyInfoTime);
    }

    /// <summary>
    ///     Starts a timing operation and returns a disposable that records the elapsed time when disposed.
    /// </summary>
    public IDisposable MeasureDbOfflineInfo()
    {
        return new TimingScope(this, RecordDbOfflineInfoTime);
    }

    /// <summary>
    ///     Starts a timing operation and returns a disposable that records the elapsed time when disposed.
    /// </summary>
    public IDisposable MeasureDbNetworkHttp()
    {
        return new TimingScope(this, RecordDbNetworkHttpTime);
    }

    /// <summary>
    ///     Starts a timing operation and returns a disposable that records the elapsed time when disposed.
    /// </summary>
    public IDisposable MeasureDbNetworkParse()
    {
        return new TimingScope(this, RecordDbNetworkParseTime);
    }

    /// <summary>
    ///     Starts a timing operation and returns a disposable that records the elapsed time when disposed.
    /// </summary>
    public IDisposable MeasureDbNetworkExtract()
    {
        return new TimingScope(this, RecordDbNetworkExtractTime);
    }

    /// <summary>
    ///     Starts a timing operation and returns a disposable that records the elapsed time when disposed.
    /// </summary>
    public IDisposable MeasureDbNetworkStore()
    {
        return new TimingScope(this, RecordDbNetworkStoreTime);
    }

    /// <summary>
    ///     Starts a timing operation and returns a disposable that records the elapsed time when disposed.
    /// </summary>
    public IDisposable MeasureDbApplyDispatcher()
    {
        return new TimingScope(this, RecordDbApplyDispatcherTime);
    }

    /// <summary>
    ///     Starts a timing operation and returns a disposable that records the elapsed time when disposed.
    /// </summary>
    public IDisposable MeasureDbApplyEntryUpdate()
    {
        return new TimingScope(this, RecordDbApplyEntryUpdateTime);
    }

    /// <summary>
    ///     Starts a timing operation and returns a disposable that records the elapsed time when disposed.
    /// </summary>
    public IDisposable MeasureDbApplyViewModelUpdate()
    {
        return new TimingScope(this, RecordDbApplyViewModelUpdateTime);
    }

    /// <summary>
    ///     Starts a timing operation for the UI handler that applies database info on the dispatcher thread.
    ///     The supplied updatesApplied value is recorded with the elapsed time to diagnose high-frequency postings.
    /// </summary>
    public IDisposable MeasureDbApplyUiHandler(int updatesApplied)
    {
        return new CountedTimingScope(this, updatesApplied, RecordDbApplyUiHandlerTime);
    }

    private sealed class TimingScope : IDisposable
    {
        private readonly ModLoadingTimingService _service;
        private readonly Action<double> _recordAction;
        private readonly Stopwatch _stopwatch;
        private bool _disposed;

        public TimingScope(ModLoadingTimingService service, Action<double> recordAction)
        {
            _service = service;
            _recordAction = recordAction;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _stopwatch.Stop();
            _recordAction(_stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private sealed class CountedTimingScope : IDisposable
    {
        private readonly ModLoadingTimingService _service;
        private readonly Action<double, int> _recordAction;
        private readonly int _updatesApplied;
        private readonly Stopwatch _stopwatch;
        private bool _disposed;

        public CountedTimingScope(ModLoadingTimingService service, int updatesApplied, Action<double, int> recordAction)
        {
            _service = service;
            _updatesApplied = updatesApplied;
            _recordAction = recordAction;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _stopwatch.Stop();
            _recordAction(_stopwatch.Elapsed.TotalMilliseconds, _updatesApplied);
        }
    }
}
