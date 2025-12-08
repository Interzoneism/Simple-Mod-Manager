# Mod Installation Flow - Complete Function List (ZIP Files Only)

This document lists all functions used when the install button on a mod card in the mod database card view is clicked until the mod is installed. **This flow assumes all mods are installed as ZIP files** - no folder/directory extraction is used.

## Entry Point

### 1. InstallModButton_OnClick
**File:** `VintageStoryModManager/Views/MainWindow.xaml.cs` (Line 4610)

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
6. Gets the target installation path (always a .zip file)
7. Creates an automatic backup
8. Performs the actual installation via ModUpdateService (always as zip file)
9. Refreshes the mods list
10. Removes the mod from selection and search results

**Special Cases:**
- Returns early if mod update is already in progress
- Returns early if not in mod database search mode
- Shows error if no downloadable releases available
- Shows error if target path cannot be determined

**Important:** When calling `ModUpdateService.UpdateAsync`, the `descriptor.TargetIsDirectory` parameter is always set to `false` for zip-only installation.

---

## Supporting Functions

### 2. SelectReleaseForInstall
**File:** `VintageStoryModManager/Views/MainWindow.xaml.cs` (Line 4854)

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
**File:** `VintageStoryModManager/Views/MainWindow.xaml.cs` (Line 7524)

```csharp
private bool TryGetInstallTargetPath(ModListItemViewModel mod, ModReleaseInfo release, 
    out string fullPath, out string? errorMessage)
```

**Purpose:** Determines the file system path where the mod ZIP file should be installed.

**Logic:**
1. Validates data directory is available
2. Constructs path to Mods directory (`{DataDirectory}/Mods`)
3. Creates Mods directory if it doesn't exist
4. Determines filename from release info or generates fallback
5. Sanitizes filename and ensures .zip extension
6. Ensures unique file path to avoid conflicts

**Returns:** `true` if successful, `false` if error occurred

**Important:** Always returns a path to a .zip file, never a directory.

**Error Cases:**
- Data directory not configured
- Mods folder cannot be accessed
- IO exceptions

---

### 4. CreateAutomaticBackupAsync
**File:** `VintageStoryModManager/Views/MainWindow.xaml.cs` (Referenced at line 4650)

```csharp
await CreateAutomaticBackupAsync("ModsUpdated").ConfigureAwait(true);
```

**Purpose:** Creates a backup before making changes to mods.

**Parameters:** Backup reason string ("ModsUpdated")

---

### 5. ModUpdateService.UpdateAsync
**File:** `VintageStoryModManager/Services/ModUpdateService.cs` (Line 15)

```csharp
public async Task<ModUpdateResult> UpdateAsync(
    ModUpdateDescriptor descriptor,
    bool cacheDownloads,
    IProgress<ModUpdateProgress>? progress = null,
    CancellationToken cancellationToken = default)
```

**Purpose:** Main service method that handles downloading and installing the mod as a ZIP file.

**Flow:**
1. Downloads the mod archive via `DownloadAsync`
2. Validates the archive via `ValidateArchive`
3. Caches the download if configured via `TryCacheDownload`
4. Installs the mod to the target location via `InstallAsync` (always as zip file)

**Important:** For zip-only installation, `descriptor.TargetIsDirectory` must be `false`.

**Special Cases:**
- Handles operation cancellation
- Handles internet access disabled
- Handles various IO exceptions
- Uses cache if available

---

### 6. ModUpdateService.DownloadAsync
**File:** `VintageStoryModManager/Services/ModUpdateService.cs` (Line 75)

```csharp
private static async Task<DownloadResult> DownloadAsync(
    ModUpdateDescriptor descriptor,
    CancellationToken cancellationToken)
```

**Purpose:** Downloads the mod file from either cache or network.

**Flow:**
1. Determines cache path for the mod via `ModCacheLocator.GetModCachePath`
2. Checks if mod is already cached via `ModCacheLocator.TryLocateCachedModFile`
3. If cached, returns cached file path
4. If not cached:
   - Creates temporary directory via `CreateTemporaryDirectory`
   - Downloads from network (HTTP) or copies from local file
   - Returns path to downloaded file

**Cache Behavior:**
- Promotes legacy cache files to new structure
- Uses ModCacheLocator to find cached files
- Returns cache hit status in DownloadResult

---

### 7. ModUpdateService.ValidateArchive
**File:** `VintageStoryModManager/Services/ModUpdateService.cs` (Line 167)

```csharp
private static void ValidateArchive(string downloadPath)
```

**Purpose:** Validates that the downloaded file is a valid Vintage Story mod archive.

**Validation:**
1. Opens the zip archive using `ZipFile.OpenRead`
2. Checks for presence of `modinfo.json` file in any entry
3. Throws `InvalidDataException` if validation fails

**Error Cases:**
- Invalid zip file format
- Missing modinfo.json file

---

### 8. ModUpdateService.InstallAsync
**File:** `VintageStoryModManager/Services/ModUpdateService.cs` (Line 184)

```csharp
private static Task<ModUpdateResult> InstallAsync(
    ModUpdateDescriptor descriptor, 
    string downloadPath,
    bool treatAsDirectory, 
    IProgress<ModUpdateProgress>? progress, 
    CancellationToken cancellationToken)
```

**Purpose:** Installs the mod to the target location.

**Logic (ZIP-only mode):**
1. Checks if target already exists
2. Determines if this is a fresh install or update
3. Routes to file installation (since `treatAsDirectory` is always `false`)

**Important:** For zip-only installation, this always calls `InstallToFile`, never `InstallToDirectory`.

---

### 9. ModUpdateService.InstallToFile
**File:** `VintageStoryModManager/Services/ModUpdateService.cs` (Line 271)

```csharp
private static void InstallToFile(ModUpdateDescriptor descriptor, string downloadPath)
```

**Purpose:** Installs mod as a zip file to the target path. **This is the only installation method used in zip-only mode.**

**Flow:**
1. Creates target directory if needed via `Directory.CreateDirectory`
2. Creates backup of existing file if present via `File.Move`
3. Attempts to cache the backup via `TryMoveBackupToCache`
4. Copies downloaded file to target path via `File.Copy`
5. Cleans up old file if path changed via `TryDelete`
6. On failure, restores from backup

**Special Cases:**
- Handles version updates (replaces existing file)
- Handles fresh installs
- Rollback support via backups
- Caches old versions if configured

**Important:** This function handles all mod installations when working with zip files only.

---

### 10. RefreshModsAsync
**File:** `VintageStoryModManager/Views/MainWindow.xaml.cs` (Line 3333)

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

### 11. UpdateSelectedModButtons
**File:** `VintageStoryModManager/Views/MainWindow.xaml.cs` (Line 12186)

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

### 12. RemoveFromSelection
**File:** `VintageStoryModManager/Views/MainWindow.xaml.cs` (Line 11995)

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

### 13. MainViewModel.RemoveSearchResult
**File:** `VintageStoryModManager/ViewModels/MainViewModel.cs`

```csharp
_viewModel?.RemoveSearchResult(mod);
```

**Purpose:** Removes the installed mod from the search results since it's now installed.

---

## Supporting Data Structures

### ModUpdateDescriptor
**File:** `VintageStoryModManager/Services/ModUpdateService.cs` (Line 570)

```csharp
public sealed record ModUpdateDescriptor(
    string ModId,
    string DisplayName,
    Uri DownloadUri,
    string TargetPath,
    bool TargetIsDirectory,  // Always false for zip-only installation
    string? ReleaseFileName,
    string? ReleaseVersion,
    string? InstalledVersion)
{
    public string? ExistingPath { get; init; }
}
```

**Purpose:** Contains all information needed to download and install a mod.

**Important for ZIP-only:** Set `TargetIsDirectory = false`

---

### ModUpdateResult
**File:** `VintageStoryModManager/Services/ModUpdateService.cs` (Line 583)

```csharp
public sealed record ModUpdateResult(bool Success, string? ErrorMessage);
```

**Purpose:** Result of an installation operation.

---

### ModUpdateProgress
**File:** `VintageStoryModManager/Services/ModUpdateService.cs` (Line 585)

```csharp
public readonly record struct ModUpdateProgress(ModUpdateStage Stage, string Message);
```

**Purpose:** Progress information reported during installation.

---

### ModUpdateStage
**File:** `VintageStoryModManager/Services/ModUpdateService.cs` (Line 587)

```csharp
public enum ModUpdateStage
{
    Downloading,
    Validating,
    Preparing,    // Not used in zip-only mode
    Replacing,
    Completed
}
```

**Purpose:** Enumeration of installation stages.

**Note:** `Preparing` stage is only used for directory extraction, not in zip-only mode.

---

## Utility Functions

### 14. TryCacheDownload
**File:** `VintageStoryModManager/Services/ModUpdateService.cs` (Line 515)

```csharp
private static void TryCacheDownload(string sourcePath, string cachePath)
```

**Purpose:** Copies downloaded mod file to cache for future use.

**Flow:**
1. Ensures source and cache paths are different
2. Creates cache directory if needed
3. Copies file to cache location
4. Logs warning on failure but doesn't throw

---

### 15. TryMoveBackupToCache
**File:** `VintageStoryModManager/Services/ModUpdateService.cs` (Line 355)

```csharp
private static string? TryMoveBackupToCache(ModUpdateDescriptor descriptor, string backupPath)
```

**Purpose:** Moves the old version of a mod being updated to the cache.

**Flow:**
1. Validates installed version is known
2. Determines cache path for old version
3. Checks if cache already exists
4. Moves backup file to cache
5. Returns cache path on success, null on failure

**Benefit:** Allows quick rollback to previous version from cache.

---

### 16. CreateTemporaryDirectory
**File:** `VintageStoryModManager/Services/ModUpdateService.cs` (Line 499)

```csharp
private static string CreateTemporaryDirectory()
```

**Purpose:** Creates a unique temporary directory for download operations.

**Location:** `%TEMP%/IMM/{GUID}`

**Returns:** Full path to created directory

---

### 17. TryDelete
**File:** `VintageStoryModManager/Services/ModUpdateService.cs` (Line 532)

```csharp
private static void TryDelete(string path)
```

**Purpose:** Attempts to delete a file or directory, logging warnings on failure.

**Logic:**
- Checks if path is file or directory
- Deletes accordingly
- Catches and logs IO exceptions without throwing

---

### 18. ReportProgress
**File:** `VintageStoryModManager/Services/ModUpdateService.cs` (Line 562)

```csharp
private static void ReportProgress(IProgress<ModUpdateProgress>? progress, 
    ModUpdateStage stage, string message)
```

**Purpose:** Reports progress updates to the UI during installation.

**Parameters:**
- `progress`: Optional progress reporter
- `stage`: Current installation stage
- `message`: Human-readable status message

---

## Special Case: Mod Already Installed

If a mod is already installed:

1. **In InstallModButton_OnClick:**
   - The button visibility is controlled by XAML binding to `IsInstalled` property
   - Install button is hidden/collapsed when `IsInstalled` is true

2. **XAML Binding:**
   ```xml
   <Style.Triggers>
       <DataTrigger Binding="{Binding IsInstalled}" Value="True">
           <Setter Property="Visibility" Value="Collapsed" />
       </DataTrigger>
   </Style.Triggers>
   ```

3. **User Experience:**
   - The install button simply doesn't appear for installed mods
   - Instead, update/edit/delete buttons are available
   - The card shows "Installed" badge overlay

---

## Installation Flow Diagram (ZIP Files Only)

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
TryGetInstallTargetPath → Determine where to install (.zip file)
    ↓
CreateAutomaticBackupAsync → Backup existing mods
    ↓
ModUpdateService.UpdateAsync
    ↓
    ├─→ DownloadAsync
    │   ├─→ Check cache (ModCacheLocator)
    │   │   └─→ [Cache Hit] → Use cached file
    │   └─→ [Cache Miss] → Download from network
    │       └─→ CreateTemporaryDirectory
    │           └─→ HTTP download or file copy
    ↓
    ├─→ ValidateArchive
    │   └─→ Check for modinfo.json in zip
    ↓
    ├─→ TryCacheDownload (if caching enabled)
    ↓
    └─→ InstallAsync
        └─→ InstallToFile (ALWAYS for zip-only)
            ├─→ Create target directory if needed
            ├─→ Backup existing file
            ├─→ TryMoveBackupToCache (old version)
            ├─→ Copy new zip file to target
            └─→ [On Error] → Restore from backup
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

**Cache Locations:** Managed by `ModCacheLocator` service

**Cache Benefits:**
- Faster re-installation of previously downloaded mods
- Ability to roll back to previous versions
- Offline installation of cached mods

---

## Notes for Integration with External Mod Browser

When integrating this flow into another mod browser:

### Required Services
- `ModUpdateService` for download and installation
- `ModCacheLocator` for cache management (optional but recommended)
- Progress reporting mechanism (via `IProgress<ModUpdateProgress>`)

### Required Data
- **Mod ID** (string)
- **Download URI** (Uri)
- **Target path** (string) - usually `{DataDirectory}/Mods/{filename}.zip`
- **Release version** (string)
- **Release filename** (string)
- **Installed version** (string, optional - for updates)

### Key Settings
```csharp
var descriptor = new ModUpdateDescriptor(
    ModId: "modid",
    DisplayName: "Mod Name",
    DownloadUri: new Uri("https://..."),
    TargetPath: Path.Combine(dataDir, "Mods", "modfile.zip"),
    TargetIsDirectory: false,  // ALWAYS false for zip-only
    ReleaseFileName: "modfile.zip",
    ReleaseVersion: "1.0.0",
    InstalledVersion: null  // or current version if updating
);

var result = await modUpdateService.UpdateAsync(
    descriptor, 
    cacheDownloads: true,  // recommended
    progress: progressReporter
);
```

### Dependencies
- Vintage Story data directory path (must exist)
- Network access (unless using cached mods)
- Write permissions to target directory
- .NET ZIP file handling (`System.IO.Compression`)

### UI Considerations
- Show progress during download/installation (5 stages)
- Handle long-running operations asynchronously
- Provide clear feedback on success/failure
- Refresh mod list after installation
- Update button states appropriately

### Important Constraints
- **Always** set `TargetIsDirectory = false`
- **Always** provide a .zip file path as `TargetPath`
- **Never** call directory extraction functions
- The service will validate the zip contains `modinfo.json`

---

## Function Summary

### Core Flow (13 functions)
1. `InstallModButton_OnClick` - Entry point
2. `SelectReleaseForInstall` - Version selection
3. `TryGetInstallTargetPath` - Path determination
4. `CreateAutomaticBackupAsync` - Pre-install backup
5. `ModUpdateService.UpdateAsync` - Main orchestrator
6. `DownloadAsync` - Download/cache retrieval
7. `ValidateArchive` - Zip validation
8. `InstallAsync` - Installation router
9. `InstallToFile` - **Actual installation (zip-only)**
10. `RefreshModsAsync` - UI refresh
11. `UpdateSelectedModButtons` - Button state update
12. `RemoveFromSelection` - Selection management
13. `RemoveSearchResult` - Search result cleanup

### Utility Functions (5 functions)
14. `TryCacheDownload` - Cache new download
15. `TryMoveBackupToCache` - Cache old version
16. `CreateTemporaryDirectory` - Temp directory creation
17. `TryDelete` - Safe file/directory deletion
18. `ReportProgress` - Progress reporting

### Data Structures (4 types)
- `ModUpdateDescriptor` - Installation parameters
- `ModUpdateResult` - Installation result
- `ModUpdateProgress` - Progress information
- `ModUpdateStage` - Stage enumeration

**Total: 18 functions + 4 data structures**
