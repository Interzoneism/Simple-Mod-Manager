using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace VintageStoryModManager.Services;

/// <summary>
/// Writes status updates from the UI to a log file so that the recent history can be reviewed.
/// </summary>
public static class StatusLogService
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";
    private const int MaxModNameLength = 10;
    private const int MaxLogLines = 5000;
    private static readonly object SyncRoot = new();
    private static readonly string LogFilePath = InitializeLogFilePath();
    private static volatile bool _isLoggingEnabled;

    public static bool IsLoggingEnabled
    {
        get => _isLoggingEnabled;
        set => _isLoggingEnabled = value;
    }

    private static string InitializeLogFilePath()
    {
        try
        {
            string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrEmpty(documentsPath))
            {
                string directory = Path.Combine(documentsPath, "Simple VS Manager");
                return Path.Combine(directory, "SimpleVSManagerStatus.log");
            }
        }
        catch (Exception)
        {
            // Ignore failures when determining the documents directory and fall back to the app directory.
        }

        return Path.Combine(AppContext.BaseDirectory, "SimpleVSManagerStatus.log");
    }

    /// <summary>
    /// Appends a status entry to the log file using a timestamp and severity marker.
    /// </summary>
    /// <param name="message">The message to record.</param>
    /// <param name="isError">Whether the status represents an error.</param>
    public static void AppendStatus(string message, bool isError)
    {
        if (!_isLoggingEnabled || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        string timestamp = DateTime.Now.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        string severity = isError ? "ERROR" : "INFO";
        string line = $"[{timestamp}] [{severity}] {message}{Environment.NewLine}";

        _ = Task.Run(() => WriteLogEntry(line));
    }

    public static DebugLogScope? BeginDebugScope(string? modName, string? modId, string process)
    {
        if (!_isLoggingEnabled)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(process))
        {
            return null;
        }

        return new DebugLogScope(FormatModName(modName), NormalizeIdentifier(modId), process.Trim());
    }

    private static string FormatModName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        string trimmed = name.Trim();
        if (trimmed.Length <= MaxModNameLength)
        {
            return trimmed;
        }

        return trimmed.Substring(0, MaxModNameLength);
    }

    private static string NormalizeIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return string.Empty;
        }

        return identifier.Trim();
    }

    private static void WriteLogEntry(string line)
    {
        try
        {
            lock (SyncRoot)
            {
                EnsureLogDirectory();
                using FileStream stream = new(LogFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                stream.Seek(0, SeekOrigin.End);
                using (var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(line);
                    writer.Flush();
                }

                TrimLogFileIfNecessary(stream);
            }
        }
        catch (IOException)
        {
            // Ignore logging failures so that the UI does not crash when the log cannot be written.
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore logging failures so that the UI does not crash when the log cannot be written.
        }
    }

    private static void EnsureLogDirectory()
    {
        string? directory = Path.GetDirectoryName(LogFilePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
    }

    private static void TrimLogFileIfNecessary(FileStream stream)
    {
        try
        {
            stream.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);

            var lines = new Queue<string>(MaxLogLines + 1);
            bool trimmed = false;
            while (!reader.EndOfStream)
            {
                string? existingLine = reader.ReadLine();
                if (existingLine is null)
                {
                    continue;
                }

                lines.Enqueue(existingLine);
                if (lines.Count > MaxLogLines)
                {
                    lines.Dequeue();
                    trimmed = true;
                }
            }

            if (!trimmed)
            {
                stream.Seek(0, SeekOrigin.End);
                return;
            }

            stream.SetLength(0);
            stream.Seek(0, SeekOrigin.Begin);
            using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
            foreach (string existingLine in lines)
            {
                writer.WriteLine(existingLine);
            }

            writer.Flush();
        }
        catch (IOException)
        {
            // Ignore trimming failures so that logging does not crash the UI.
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore trimming failures so that logging does not crash the UI.
        }
        finally
        {
            stream.Seek(0, SeekOrigin.End);
        }
    }
}

public sealed class DebugLogScope : IDisposable
{
    private readonly string _modDisplay;
    private readonly string _process;
    private readonly Stopwatch _stopwatch;
    private readonly Dictionary<string, string> _details = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    internal DebugLogScope(string modName, string modId, string process)
    {
        _modDisplay = BuildModDisplay(modName, modId);
        _process = process;
        _stopwatch = Stopwatch.StartNew();
    }

    public void SetCacheStatus(bool cacheHit)
    {
        SetDetail("cache", cacheHit ? "hit" : "miss");
    }

    public void SetDetail(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        string normalizedKey = key.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            _details.Remove(normalizedKey);
            return;
        }

        _details[normalizedKey] = value.Trim();
    }

    public void SetDetail(string key, int value)
    {
        SetDetail(key, value.ToString(CultureInfo.InvariantCulture));
    }

    public void SetDetail(string key, long value)
    {
        SetDetail(key, value.ToString(CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopwatch.Stop();

        var builder = new StringBuilder();
        builder.Append("[Debug]");
        if (!string.IsNullOrEmpty(_modDisplay))
        {
            builder.Append('[').Append(_modDisplay).Append(']');
        }

        builder.Append(' ').Append(_process).Append(' ')
            .Append(_stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture))
            .Append("ms");

        foreach (var pair in _details)
        {
            builder.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
        }

        StatusLogService.AppendStatus(builder.ToString(), false);
    }

    private static string BuildModDisplay(string modName, string modId)
    {
        bool hasName = !string.IsNullOrWhiteSpace(modName);
        bool hasId = !string.IsNullOrWhiteSpace(modId);

        if (!hasName && !hasId)
        {
            return string.Empty;
        }

        if (hasName && hasId)
        {
            return $"{modName}|{modId}";
        }

        return hasName ? modName : modId;
    }
}
