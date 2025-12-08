# Mod Installation Flow - Complete Function List

This document lists all functions used when the install button on a mod card in the mod database card view is clicked until the mod is installed, including any special cases like if the mod is already installed.

## Entry Point

### 1. InstallModButton_OnClick
**File:** `/VintageStoryModManager/Views/MainWindow.xaml.cs` (Line 4610)

```csharp
private async void InstallModButton_OnClick(object sender, RoutedEventArgs e)
```

**Purpose:** Event handler triggered when the install button is clicked on a mod card in the mod database view.

**Flow:**
1. Checks if a mod update is already in progress (`_isModUpdateInProgress`)
2. Verifies we're in mod database search mode (`_viewModel?.SearchModDatabase != true`)
3. Validates the sender has a ModListItemViewModel data context
4. Checks if the mod has downloadable releases
5. Selects the release to install
6. Gets the target installation path
7. Creates an automatic backup
8. Performs the actual installation via ModUpdateService
9. Refreshes the mods list
10. Removes the mod from selection and search results

**Special Cases:**
- Returns early if mod update is already in progress
- Returns early if not in mod database search mode
- Shows error if no downloadable releases available
- Shows error if target path cannot be determined

---

## Supporting Functions

### 2. SelectReleaseForInstall
**File:** `/VintageStoryModManager/Views/MainWindow.xaml.cs` (Line 4854)

```csharp
private static ModReleaseInfo? SelectReleaseForInstall(ModListItemViewModel mod)
```

**Purpose:** Determines which release version to install based on compatibility.

**Logic:**
1. If latest release is compatible with installed game, return it
2. Otherwise, if there's a latest compatible release, return it
3. Fall back to the latest release regardless of compatibility

**Returns:** `ModReleaseInfo` object or null

---

### 3. TryGetInstallTargetPath
**File:** `/VintageStoryModManager/Views/MainWindow.xaml.cs` (Line 7524)

```csharp
private bool TryGetInstallTargetPath(ModListItemViewModel mod, ModReleaseInfo release, 
    out string fullPath, out string? errorMessage)
```

**Purpose:** Determines the file system path where the mod should be installed.

**Logic:**
1. Validates data directory is available
2. Constructs path to Mods directory (`{DataDirectory}/Mods`)
3. Creates Mods directory if it doesn't exist
4. Determines filename from release info or generates fallback
5. Sanitizes filename and ensures .zip extension
6. Ensures unique file path to avoid conflicts

**Returns:** `true` if successful, `false` if error occurred

**Error Cases:**
- Data directory not configured
- Mods folder cannot be accessed
- IO exceptions

---

### 4. CreateAutomaticBackupAsync
**File:** `/VintageStoryModManager/Views/MainWindow.xaml.cs` (Not fully shown in the view, referenced at line 4650)

```csharp
await CreateAutomaticBackupAsync("ModsUpdated").ConfigureAwait(true);
```

**Purpose:** Creates a backup before making changes to mods.

**Parameters:** Backup reason string ("ModsUpdated")

---

### 5. ModUpdateService.UpdateAsync
**File:** `/VintageStoryModManager/Services/ModUpdateService.cs` (Line 15)

```csharp
public async Task<ModUpdateResult> UpdateAsync(
    ModUpdateDescriptor descriptor,
    bool cacheDownloads,
    IProgress<ModUpdateProgress>? progress = null,
    CancellationToken cancellationToken = default)
```

**Purpose:** Main service method that handles downloading and installing the mod.

**Flow:**
1. Downloads the mod archive
2. Validates the archive
3. Caches the download if configured
4. Installs the mod to the target location

**Special Cases:**
- Handles operation cancellation
- Handles internet access disabled
- Handles various IO exceptions
- Uses cache if available

---

### 6. ModUpdateService.DownloadAsync
**File:** `/VintageStoryModManager/Services/ModUpdateService.cs` (Line 75)

```csharp
private static async Task<DownloadResult> DownloadAsync(
    ModUpdateDescriptor descriptor,
    CancellationToken cancellationToken)
```

**Purpose:** Downloads the mod file from either cache or network.

**Flow:**
1. Determines cache path for the mod
2. Checks if mod is already cached
3. If cached, returns cached file path
4. If not cached:
   - Creates temporary directory
   - Downloads from network or copies from local file
   - Returns path to downloaded file

**Cache Behavior:**
- Promotes legacy cache files to new structure
- Uses ModCacheLocator to find cached files
- Returns cache hit status

---

### 7. ModUpdateService.ValidateArchive
**File:** `/VintageStoryModManager/Services/ModUpdateService.cs` (Line 167)

```csharp
private static void ValidateArchive(string downloadPath)
```

**Purpose:** Validates that the downloaded file is a valid Vintage Story mod archive.

**Validation:**
1. Opens the zip archive
2. Checks for presence of `modinfo.json` file
3. Throws `InvalidDataException` if validation fails

**Error Cases:**
- Invalid zip file format
- Missing modinfo.json file

---

### 8. ModUpdateService.InstallAsync
**File:** `/VintageStoryModManager/Services/ModUpdateService.cs` (Line 184)

```csharp
private static Task<ModUpdateResult> InstallAsync(
    ModUpdateDescriptor descriptor, 
    string downloadPath,
    bool treatAsDirectory, 
    IProgress<ModUpdateProgress>? progress, 
    CancellationToken cancellationToken)
```

**Purpose:** Installs the mod to the target location.

**Logic:**
1. Checks if target already exists
2. Determines if this is a fresh install or update
3. Routes to either directory or file installation based on `treatAsDirectory`

**Two Installation Paths:**
- **Directory mode:** Extracts zip to directory (calls `InstallToDirectory`)
- **File mode:** Copies zip file as-is (calls `InstallToFile`)

---

### 9. ModUpdateService.InstallToFile
**File:** `/VintageStoryModManager/Services/ModUpdateService.cs` (Line 271)

```csharp
private static void InstallToFile(ModUpdateDescriptor descriptor, string downloadPath)
```

**Purpose:** Installs mod as a zip file to the target path.

**Flow:**
1. Creates target directory if needed
2. Creates backup of existing file if present
3. Attempts to cache the backup
4. Copies downloaded file to target path
5. Cleans up old file if path changed
6. On failure, restores from backup

**Special Cases:**
- Handles version updates (replaces existing file)
- Handles fresh installs
- Rollback support via backups

---

### 10. ModUpdateService.InstallToDirectory
**File:** `/VintageStoryModManager/Services/ModUpdateService.cs` (Line 213)

```csharp
private static ModUpdateResult InstallToDirectory(
    ModUpdateDescriptor descriptor, 
    string targetDirectory,
    string downloadPath, 
    string completionMessage, 
    IProgress<ModUpdateProgress>? progress,
    CancellationToken cancellationToken)
```

**Purpose:** Installs mod by extracting zip contents to a directory.

**Flow:**
1. Creates backup path for existing directory
2. Creates temporary extraction directory
3. Moves existing directory to backup if present
4. Extracts zip file to temporary directory
5. Determines payload root (handles zip with/without wrapper folder)
6. Copies extracted files to target directory
7. On success, deletes backup
8. On failure, restores from backup

**Special Cases:**
- Handles cancellation with rollback
- Handles extraction errors with rollback
- Caches directory backup as zip file

---

### 11. ModUpdateService.DeterminePayloadRoot
**File:** `/VintageStoryModManager/Services/ModUpdateService.cs` (Line 345)

```csharp
private static string DeterminePayloadRoot(string extractDirectory)
```

**Purpose:** Determines the root directory containing mod files after extraction.

**Logic:**
- If there's exactly 1 subdirectory and no files, returns that subdirectory
- Otherwise returns the extraction directory itself

**Reason:** Some mods wrap all content in a single folder, others don't.

---

### 12. ModUpdateService.CopyDirectory
**File:** `/VintageStoryModManager/Services/ModUpdateService.cs` (Line 472)

```csharp
private static void CopyDirectory(
    string sourceDirectory, 
    string destinationDirectory,
    CancellationToken cancellationToken)
```

**Purpose:** Recursively copies all files and subdirectories from source to destination.

**Implementation:**
- Uses stack-based iteration (not recursion)
- Supports cancellation
- Preserves directory structure

---

### 13. RefreshModsAsync
**File:** `/VintageStoryModManager/Views/MainWindow.xaml.cs` (Line 3333)

```csharp
private async Task RefreshModsAsync(bool allowModDetailsRefresh = false)
```

**Purpose:** Refreshes the mod list after installation to show the newly installed mod.

**Flow:**
1. Saves current scroll position
2. Saves selected mod source paths
3. Executes refresh command on view model
4. Restores selection based on source paths
5. Restores scroll position

**Special Cases:**
- Only preserves selection if not in mod database search mode
- Can force loading of mod details

---

### 14. UpdateSelectedModButtons
**File:** `/VintageStoryModManager/Views/MainWindow.xaml.cs` (Line 12186)

```csharp
private void UpdateSelectedModButtons()
```

**Purpose:** Updates the enabled/disabled state of mod action buttons in the UI.

**Updates:**
- Install button
- Update button
- Delete button
- Edit config button
- Fix button
- Database page button
- Copy for server button

---

### 15. RemoveFromSelection
**File:** `/VintageStoryModManager/Views/MainWindow.xaml.cs` (Line 11995)

```csharp
private void RemoveFromSelection(ModListItemViewModel mod)
```

**Purpose:** Removes a mod from the currently selected mods.

**Actions:**
- Removes from `_selectedMods` list
- Sets `IsSelected` property to false
- Unsubscribes from property change events
- Updates button states

---

### 16. MainViewModel.RemoveSearchResult
**File:** Referenced at line 4694, likely in `/VintageStoryModManager/ViewModels/MainViewModel.cs`

```csharp
_viewModel?.RemoveSearchResult(mod);
```

**Purpose:** Removes the installed mod from the search results since it's now installed.

---

## Supporting Data Structures

### ModUpdateDescriptor
**File:** `/VintageStoryModManager/Services/ModUpdateService.cs` (Line 570)

```csharp
public sealed record ModUpdateDescriptor(
    string ModId,
    string DisplayName,
    Uri DownloadUri,
    string TargetPath,
    bool TargetIsDirectory,
    string? ReleaseFileName,
    string? ReleaseVersion,
    string? InstalledVersion)
{
    public string? ExistingPath { get; init; }
}
```

**Purpose:** Contains all information needed to download and install a mod.

---

### ModUpdateResult
**File:** `/VintageStoryModManager/Services/ModUpdateService.cs` (Line 583)

```csharp
public sealed record ModUpdateResult(bool Success, string? ErrorMessage);
```

**Purpose:** Result of an installation operation.

---

### ModUpdateProgress
**File:** `/VintageStoryModManager/Services/ModUpdateService.cs` (Line 585)

```csharp
public readonly record struct ModUpdateProgress(ModUpdateStage Stage, string Message);
```

**Purpose:** Progress information reported during installation.

---

### ModUpdateStage
**File:** `/VintageStoryModManager/Services/ModUpdateService.cs` (Line 587)

```csharp
public enum ModUpdateStage
{
    Downloading,
    Validating,
    Preparing,
    Replacing,
    Completed
}
```

**Purpose:** Enumeration of installation stages.

---

## Utility Functions

### 17. TryCacheDownload
**File:** `/VintageStoryModManager/Services/ModUpdateService.cs` (Line 515)

```csharp
private static void TryCacheDownload(string sourcePath, string cachePath)
```

**Purpose:** Copies downloaded mod file to cache for future use.

---

### 18. TryMoveBackupToCache
**File:** `/VintageStoryModManager/Services/ModUpdateService.cs` (Line 355)

```csharp
private static string? TryMoveBackupToCache(ModUpdateDescriptor descriptor, string backupPath)
```

**Purpose:** Moves the old version of a mod being updated to the cache.

---

### 19. TryCacheDirectoryBackup
**File:** `/VintageStoryModManager/Services/ModUpdateService.cs` (Line 416)

```csharp
private static void TryCacheDirectoryBackup(ModUpdateDescriptor descriptor, string backupPath)
```

**Purpose:** Caches a directory-based mod by zipping it and storing in cache.

---

### 20. CreateTemporaryDirectory
**File:** `/VintageStoryModManager/Services/ModUpdateService.cs` (Line 499)

```csharp
private static string CreateTemporaryDirectory()
```

**Purpose:** Creates a unique temporary directory for extraction/processing.

**Location:** `%TEMP%/IMM/{GUID}`

---

### 21. TryDelete
**File:** `/VintageStoryModManager/Services/ModUpdateService.cs` (Line 532)

```csharp
private static void TryDelete(string path)
```

**Purpose:** Attempts to delete a file or directory, logging warnings on failure.

---

### 22. TryRestoreDirectoryBackup
**File:** `/VintageStoryModManager/Services/ModUpdateService.cs` (Line 546)

```csharp
private static void TryRestoreDirectoryBackup(string backupPath, string targetPath)
```

**Purpose:** Restores a directory from backup on installation failure.

---

### 23. ReportProgress
**File:** `/VintageStoryModManager/Services/ModUpdateService.cs` (Line 562)

```csharp
private static void ReportProgress(IProgress<ModUpdateProgress>? progress, ModUpdateStage stage, string message)
```

**Purpose:** Reports progress updates to the UI during installation.

---

## Special Case: Mod Already Installed

If a mod is already installed:

1. **In InstallModButton_OnClick:**
   - The button visibility is controlled by XAML binding to `IsInstalled` property
   - Install button is hidden/collapsed when `IsInstalled` is true
   - See XAML line 3637-3639:
   ```xml
   <Style.Triggers>
       <DataTrigger Binding="{Binding IsInstalled}" Value="True">
           <Setter Property="Visibility" Value="Collapsed" />
       </DataTrigger>
   </Style.Triggers>
   ```

2. **User Experience:**
   - The install button simply doesn't appear for installed mods
   - Instead, update/edit/delete buttons are available
   - The card shows "Installed" badge overlay (XAML line 3418-3437)

---

## Installation Flow Diagram

```
User Clicks Install Button
    ↓
InstallModButton_OnClick (MainWindow.xaml.cs)
    ↓
Check if update in progress → [Yes] → Return
    ↓ [No]
Check if in mod database mode → [No] → Return
    ↓ [Yes]
SelectReleaseForInstall → Select best release version
    ↓
TryGetInstallTargetPath → Determine where to install
    ↓
CreateAutomaticBackupAsync → Backup existing mods
    ↓
ModUpdateService.UpdateAsync
    ↓
    ├─→ DownloadAsync
    │   ├─→ Check cache (ModCacheLocator)
    │   │   └─→ [Cache Hit] → Use cached file
    │   └─→ [Cache Miss] → Download from network
    │       └─→ Create temp directory
    │           └─→ HTTP download or file copy
    ↓
    ├─→ ValidateArchive
    │   └─→ Check for modinfo.json
    ↓
    ├─→ TryCacheDownload (if caching enabled)
    ↓
    └─→ InstallAsync
        ├─→ [File Mode] → InstallToFile
        │   ├─→ Backup existing file
        │   ├─→ TryMoveBackupToCache
        │   ├─→ Copy new file to target
        │   └─→ [On Error] → Restore from backup
        │
        └─→ [Directory Mode] → InstallToDirectory
            ├─→ Create backup of directory
            ├─→ Extract to temp directory
            ├─→ DeterminePayloadRoot
            ├─→ CopyDirectory (recursive)
            ├─→ TryCacheDirectoryBackup
            └─→ [On Error] → TryRestoreDirectoryBackup
    ↓
RefreshModsAsync → Reload mod list to show newly installed mod
    ↓
RemoveFromSelection → Deselect the mod card
    ↓
RemoveSearchResult → Remove from search results
    ↓
UpdateSelectedModButtons → Update UI button states
```

---

## Error Handling

Throughout the flow, errors are handled as follows:

1. **Network Errors:** Caught in `DownloadAsync`, returns error in `ModUpdateResult`
2. **IO Errors:** Caught at multiple levels, returns descriptive error messages
3. **Validation Errors:** Caught in `ValidateArchive`, throws `InvalidDataException`
4. **Cancellation:** Supported via `CancellationToken`, triggers rollback
5. **Internet Access Disabled:** Throws `InternetAccessDisabledException`, shows appropriate message

All errors result in:
- Status message reported to UI
- Error dialog shown to user
- `_isModUpdateInProgress` flag reset
- Buttons re-enabled

---

## Cache Management

The installation flow integrates with caching at multiple points:

1. **Before Download:** Check if mod version is cached
2. **After Download:** Cache the downloaded file
3. **Before Update:** Cache the old version being replaced
4. **Cache Locations:** Managed by `ModCacheLocator` service

**Cache Benefits:**
- Faster re-installation of previously downloaded mods
- Ability to roll back to previous versions
- Offline installation of cached mods

---

## Notes for Integration

When integrating this flow into another mod browser:

1. **Required Services:**
   - `ModUpdateService` for download and installation
   - `ModCacheLocator` for cache management (optional but recommended)
   - Progress reporting mechanism

2. **Required Data:**
   - Mod ID
   - Download URI
   - Target path (usually `{DataDirectory}/Mods/{filename}`)
   - Release version
   - Release filename

3. **Key Dependencies:**
   - Vintage Story data directory path
   - Network access (unless using cached mods)
   - Write permissions to target directory

4. **UI Considerations:**
   - Show progress during download/installation
   - Handle long-running operations asynchronously
   - Provide feedback on success/failure
   - Refresh mod list after installation
