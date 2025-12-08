// Extracted functions from MainWindow.xaml.cs for mod installation (ZIP files only)
// Original file: VintageStoryModManager/Views/MainWindow.xaml.cs
// 
// IMPORTANT: This extract contains only the core installation functions.
// You will need the full MainWindow.xaml.cs file for complete context including:
// - Field declarations (_isModUpdateInProgress, _viewModel, _dataDirectory, etc.)
// - Helper methods (SanitizeFileName, EnsureUniqueFilePath, etc.)
// - Supporting types (ModListItemViewModel, ModReleaseInfo, WpfButton, etc.)

using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using VintageStoryModManager.Services;
using VintageStoryModManager.ViewModels;

namespace VintageStoryModManager.Views
{
    public partial class MainWindow
    {
        // =================================================================
        // MAIN INSTALL FUNCTION - Entry point when install button is clicked
        // =================================================================
        
        /// <summary>
        /// Event handler triggered when the install button is clicked on a mod card.
        /// This is the entry point for the entire installation flow.
        /// </summary>
        private async void InstallModButton_OnClick(object sender, RoutedEventArgs e)
        {
            // 1. Check if an installation is already in progress
            if (_isModUpdateInProgress) return;

            // 2. Verify we're in mod database search mode
            if (_viewModel?.SearchModDatabase != true) return;

            // 3. Validate the sender has a ModListItemViewModel data context
            if (sender is not WpfButton { DataContext: ModListItemViewModel mod }) return;

            e.Handled = true;

            // 4. Check if the mod has downloadable releases
            if (!mod.HasDownloadableRelease)
            {
                WpfMessageBox.Show("No downloadable releases are available for this mod.",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // 5. Select the best release version to install
            var release = SelectReleaseForInstall(mod);
            if (release is null)
            {
                WpfMessageBox.Show("No downloadable releases are available for this mod.",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // 6. Get the target installation path (always a .zip file)
            if (!TryGetInstallTargetPath(mod, release, out var targetPath, out var errorMessage))
            {
                if (!string.IsNullOrWhiteSpace(errorMessage))
                    WpfMessageBox.Show(errorMessage!,
                        "Simple VS Manager",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);

                return;
            }

            // 7. Create an automatic backup
            await CreateAutomaticBackupAsync("ModsUpdated").ConfigureAwait(true);

            // 8. Set flag and update UI
            _isModUpdateInProgress = true;
            UpdateSelectedModButtons();

            try
            {
                // 9. Create the descriptor for the installation
                // IMPORTANT: TargetIsDirectory is set to FALSE for zip-only installation
                var descriptor = new ModUpdateDescriptor(
                    mod.ModId,
                    mod.DisplayName,
                    release.DownloadUri,
                    targetPath,
                    false,  // TargetIsDirectory = false (ZIP file installation)
                    release.FileName,
                    release.Version,
                    mod.Version);

                // 10. Create progress reporter for UI updates
                var progress = new Progress<ModUpdateProgress>(p =>
                    _viewModel?.ReportStatus($"{mod.DisplayName}: {p.Message}"));

                // 11. Perform the actual installation via ModUpdateService
                var result = await _modUpdateService
                    .UpdateAsync(descriptor, _userConfiguration.CacheAllVersionsLocally, progress)
                    .ConfigureAwait(true);

                // 12. Handle installation failure
                if (!result.Success)
                {
                    var message = string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? "The installation failed."
                        : result.ErrorMessage!;
                    _viewModel?.ReportStatus($"Failed to install {mod.DisplayName}: {message}", true);
                    WpfMessageBox.Show($"Failed to install {mod.DisplayName}:{Environment.NewLine}{message}",
                        "Simple VS Manager",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                // 13. Report success
                var versionText = string.IsNullOrWhiteSpace(release.Version) ? string.Empty : $" {release.Version}";
                _viewModel?.ReportStatus($"Installed {mod.DisplayName}{versionText}.");

                // 14. Refresh the mods list to show the newly installed mod
                await RefreshModsAsync().ConfigureAwait(true);

                // 15. Remove the mod from selection
                if (mod.IsSelected) RemoveFromSelection(mod);

                // 16. Remove from search results (since it's now installed)
                _viewModel?.RemoveSearchResult(mod);
            }
            catch (OperationCanceledException)
            {
                _viewModel?.ReportStatus("Installation cancelled.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _viewModel?.ReportStatus($"Failed to install {mod.DisplayName}: {ex.Message}", true);
                WpfMessageBox.Show($"Failed to install {mod.DisplayName}:{Environment.NewLine}{ex.Message}",
                    "Simple VS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                // 17. Reset flag and update UI
                _isModUpdateInProgress = false;
                UpdateSelectedModButtons();
            }
        }

        // =================================================================
        // SUPPORTING FUNCTIONS
        // =================================================================

        /// <summary>
        /// Determines which release version to install based on compatibility.
        /// Priority: Compatible latest > Latest compatible > Latest regardless
        /// </summary>
        private static ModReleaseInfo? SelectReleaseForInstall(ModListItemViewModel mod)
        {
            // First choice: Latest release if it's compatible
            if (mod.LatestRelease?.IsCompatibleWithInstalledGame == true) 
                return mod.LatestRelease;

            // Second choice: Latest compatible release (even if not the newest)
            if (mod.LatestCompatibleRelease != null) 
                return mod.LatestCompatibleRelease;

            // Fallback: Latest release regardless of compatibility
            return mod.LatestRelease;
        }

        /// <summary>
        /// Determines the file system path where the mod ZIP file should be installed.
        /// IMPORTANT: Always returns a path to a .zip file, never a directory.
        /// </summary>
        private bool TryGetInstallTargetPath(
            ModListItemViewModel mod, 
            ModReleaseInfo release, 
            out string fullPath,
            out string? errorMessage)
        {
            fullPath = string.Empty;
            errorMessage = null;

            // 1. Validate data directory is available
            if (_dataDirectory is null)
            {
                errorMessage =
                    "The VintagestoryData folder is not available. Please verify it from File > Set Data Folder.";
                return false;
            }

            // 2. Construct path to Mods directory
            var modsDirectory = Path.Combine(_dataDirectory, "Mods");

            // 3. Create Mods directory if it doesn't exist
            try
            {
                Directory.CreateDirectory(modsDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                           or NotSupportedException)
            {
                errorMessage = $"The Mods folder could not be accessed:{Environment.NewLine}{ex.Message}";
                return false;
            }

            // 4. Determine filename from release info or generate fallback
            var defaultName = string.IsNullOrWhiteSpace(mod.ModId) ? "mod" : mod.ModId;
            var versionPart = string.IsNullOrWhiteSpace(release.Version) ? "latest" : release.Version!;
            var fallbackFileName = $"{defaultName}-{versionPart}.zip";

            var releaseFileName = release.FileName;
            if (!string.IsNullOrWhiteSpace(releaseFileName)) 
                releaseFileName = Path.GetFileName(releaseFileName);

            // 5. Sanitize filename and ensure .zip extension
            var sanitizedFileName = SanitizeFileName(releaseFileName, fallbackFileName);
            if (string.IsNullOrWhiteSpace(Path.GetExtension(sanitizedFileName))) 
                sanitizedFileName += ".zip";

            // 6. Ensure unique file path to avoid conflicts
            var candidatePath = Path.Combine(modsDirectory, sanitizedFileName);
            fullPath = EnsureUniqueFilePath(candidatePath);
            return true;
        }

        /// <summary>
        /// Refreshes the mod list after installation to show the newly installed mod.
        /// Preserves scroll position and selection state.
        /// </summary>
        private async Task RefreshModsAsync(bool allowModDetailsRefresh = false)
        {
            if (_viewModel?.RefreshCommand == null) return;

            // 1. Save current scroll position
            var scrollViewer = GetModsScrollViewer();
            var targetOffset = scrollViewer?.VerticalOffset;

            // 2. Save selected mod source paths for restoration
            List<string>? selectedSourcePaths = null;
            string? anchorSourcePath = null;

            if (!_viewModel.SearchModDatabase && _selectedMods.Count > 0)
            {
                var dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                selectedSourcePaths = new List<string>(_selectedMods.Count);

                foreach (var selected in _selectedMods)
                {
                    var sourcePath = selected.SourcePath;
                    if (string.IsNullOrWhiteSpace(sourcePath)) continue;

                    if (dedup.Add(sourcePath)) selectedSourcePaths.Add(sourcePath);
                }

                if (selectedSourcePaths.Count > 0 && _selectionAnchor is { } anchor) 
                    anchorSourcePath = anchor.SourcePath;
            }

            // 3. Force loading of mod details if requested
            if (allowModDetailsRefresh) 
                _viewModel.ForceNextRefreshToLoadDetails();

            // 4. Execute refresh command on view model
            await _viewModel.RefreshCommand.ExecuteAsync(null);

            // 5. Restore selection based on source paths
            if (selectedSourcePaths is { Count: > 0 })
                RestoreSelectionFromSourcePaths(selectedSourcePaths, anchorSourcePath);

            // 6. Restore scroll position
            if (scrollViewer != null && targetOffset.HasValue)
                await Dispatcher.InvokeAsync(() =>
                {
                    scrollViewer.UpdateLayout();
                    var clampedOffset = Math.Max(0, Math.Min(targetOffset.Value, scrollViewer.ScrollableHeight));
                    scrollViewer.ScrollToVerticalOffset(clampedOffset);
                }, DispatcherPriority.Background);
        }

        // =================================================================
        // NOTES FOR INTEGRATION
        // =================================================================
        
        /*
         * Key Points for External Integration:
         * 
         * 1. ALWAYS set ModUpdateDescriptor.TargetIsDirectory = false
         * 2. Ensure the target path points to a .zip file
         * 3. The ModUpdateService handles all download, validation, and installation
         * 4. Progress reporting is optional but recommended for user feedback
         * 5. Cache usage is controlled by the cacheDownloads parameter
         * 
         * Minimal Integration Example:
         * 
         * var descriptor = new ModUpdateDescriptor(
         *     modId,
         *     displayName,
         *     downloadUri,
         *     Path.Combine(dataDirectory, "Mods", $"{modId}.zip"),
         *     false,  // TargetIsDirectory = false for ZIP installation
         *     releaseFileName,
         *     releaseVersion,
         *     null    // installedVersion (null for new install)
         * );
         * 
         * var modUpdateService = new ModUpdateService();
         * var result = await modUpdateService.UpdateAsync(descriptor, true);
         * 
         * if (result.Success)
         * {
         *     // Refresh your mod list
         *     // Update UI
         * }
         * else
         * {
         *     // Show error: result.ErrorMessage
         * }
         */
    }
}
