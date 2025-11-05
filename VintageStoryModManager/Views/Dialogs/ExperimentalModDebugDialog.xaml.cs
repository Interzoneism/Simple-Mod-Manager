using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace VintageStoryModManager.Views.Dialogs;

public partial class ExperimentalModDebugDialog : Window, INotifyPropertyChanged
{
    private bool _showOnlyHighlighted;
    private ICollectionView? _filteredLogLines;

    public ExperimentalModDebugDialog(string modId, IReadOnlyList<ExperimentalModDebugLogLine> logLines)
        : this(
            $"Log entries mentioning '{modId}'",
            $"No log entries referencing '{modId}' were found.",
            logLines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ModId = modId;
    }

    public ExperimentalModDebugDialog(
        string headerText,
        string emptyMessage,
        IReadOnlyList<ExperimentalModDebugLogLine> logLines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerText);
        ArgumentException.ThrowIfNullOrWhiteSpace(emptyMessage);
        ArgumentNullException.ThrowIfNull(logLines);

        // Initialize ModId to null to prevent crashes when accessing it
        ModId = null;

        InitializeComponent();

        HeaderText = headerText;
        EmptyMessage = emptyMessage;
        LogLines = logLines;

        // Initialize the filtered view
        _filteredLogLines = CollectionViewSource.GetDefaultView(LogLines);
        _filteredLogLines.Filter = FilterLogLine;

        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? ModId { get; private set; }

    public IReadOnlyList<ExperimentalModDebugLogLine> LogLines { get; }

    public ICollectionView FilteredLogLines => _filteredLogLines ?? CollectionViewSource.GetDefaultView(LogLines);

    public string HeaderText { get; }

    public string EmptyMessage { get; }

    public bool HasLogLines => LogLines.Count > 0;

    public bool ShowOnlyHighlighted
    {
        get => _showOnlyHighlighted;
        set
        {
            if (_showOnlyHighlighted != value)
            {
                _showOnlyHighlighted = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowOnlyHighlighted)));
                _filteredLogLines?.Refresh();
            }
        }
    }

    private bool FilterLogLine(object obj)
    {
        if (obj is not ExperimentalModDebugLogLine logLine)
        {
            return true;
        }

        if (!ShowOnlyHighlighted)
        {
            return true;
        }

        return logLine.IsHighlighted;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void DataGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid dataGrid)
        {
            return;
        }

        if (dataGrid.SelectedItem is not ExperimentalModDebugLogLine selectedLine)
        {
            return;
        }

        // Check if the line has a file path and line number
        if (string.IsNullOrWhiteSpace(selectedLine.FilePath) || selectedLine.LineNumber <= 0)
        {
            return;
        }

        // Verify the file exists
        if (!File.Exists(selectedLine.FilePath))
        {
            System.Windows.MessageBox.Show(
                $"The log file could not be found:\n\n{selectedLine.FilePath}",
                "File Not Found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        OpenFileAtLine(selectedLine.FilePath, selectedLine.LineNumber);
    }

    private static void OpenFileAtLine(string filePath, int lineNumber)
    {
        // Validate file path to prevent security issues
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        // Ensure the file path is absolute and doesn't contain potentially malicious patterns
        try
        {
            filePath = Path.GetFullPath(filePath);
        }
        catch (Exception)
        {
            // Invalid path
            return;
        }

        // Try to open with common text editors that support line navigation
        // VS Code, Notepad++, Sublime Text, and others support similar command-line arguments
        
        var editorAttempts = new List<(string executable, string arguments)>
        {
            // VS Code - most common modern editor
            ("code", $"-g \"{filePath}\":{lineNumber}"),
            
            // Notepad++ - popular on Windows
            ("notepad++", $"\"{filePath}\" -n{lineNumber}"),
            
            // Sublime Text
            ("subl", $"\"{filePath}\":{lineNumber}"),
            
            // Vim/GVim
            ("gvim", $"+{lineNumber} \"{filePath}\""),
            
            // Fallback to default text editor without line navigation
            (string.Empty, $"\"{filePath}\"")
        };

        foreach (var (executable, arguments) in editorAttempts)
        {
            try
            {
                var startInfo = new ProcessStartInfo();
                
                if (string.IsNullOrEmpty(executable))
                {
                    // Use default application
                    startInfo.FileName = filePath;
                    startInfo.UseShellExecute = true;
                }
                else
                {
                    startInfo.FileName = executable;
                    startInfo.Arguments = arguments;
                    startInfo.UseShellExecute = false;
                }

                Process.Start(startInfo);
                return; // Success - don't try other editors
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Editor not found or not installed - try next one
                continue;
            }
            catch (FileNotFoundException)
            {
                // Editor executable not found - try next one
                continue;
            }
            catch (Exception)
            {
                // Unexpected error with this editor - try next one
                // We silently continue to try other editors rather than showing an error
                continue;
            }
        }
    }
}
