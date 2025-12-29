using System.IO;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VintageStoryModManager;
using VintageStoryModManager.Services;

namespace SimpleVsManager.Cloud;

/// <summary>
///     Handles Firebase anonymous authentication and persists refresh tokens for reuse.
/// </summary>
public sealed class FirebaseAnonymousAuthenticator : IDisposable
{
    private static readonly string SignInEndpoint = DevConfig.FirebaseSignInEndpoint;
    private static readonly string RefreshEndpoint = DevConfig.FirebaseRefreshEndpoint;
    private static readonly string DeleteEndpoint = DevConfig.FirebaseDeleteEndpoint;
    private static readonly string StateFileName = DevConfig.FirebaseAuthStateFileName;
    private static readonly HttpClient HttpClient = new();
    private static readonly TimeSpan ExpirationSkew = TimeSpan.FromMinutes(2);

    private static readonly string DefaultApiKey = DevConfig.FirebaseDefaultApiKey;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _stateFilePath;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly object _disposeLock = new();

    private FirebaseAuthState? _cachedState;
    private bool _disposed;

    public FirebaseAnonymousAuthenticator() : this(DefaultApiKey)
    {
    }

    public FirebaseAnonymousAuthenticator(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("A Firebase API key is required.", nameof(apiKey));

        ApiKey = apiKey.Trim();
        _stateFilePath = DetermineStateFilePath();
    }


    public string ApiKey { get; }

    /// <summary>
    ///     Retrieves a valid ID token, refreshing or signing in anonymously as required.
    /// </summary>
    public async Task<string> GetIdTokenAsync(CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FirebaseAnonymousAuthenticator));
        InternetAccessManager.ThrowIfInternetAccessDisabled();
        var session = await GetSessionAsync(ct).ConfigureAwait(false);
        return session.IdToken;
    }

    public async Task<FirebaseAuthSession> GetSessionAsync(CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FirebaseAnonymousAuthenticator));
        InternetAccessManager.ThrowIfInternetAccessDisabled();
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _cachedState ??= LoadStateFromDisk();

            if (_cachedState is { } cachedWithoutUser && string.IsNullOrWhiteSpace(cachedWithoutUser.UserId))
                _cachedState = null;

            if (_cachedState is { } existing && !existing.IsExpired)
                return new FirebaseAuthSession(existing.IdToken, existing.UserId);

            FirebaseAuthState? refreshed = null;
            if (_cachedState is { } stateWithRefresh && !string.IsNullOrWhiteSpace(stateWithRefresh.RefreshToken))
                refreshed = await TryRefreshAsync(stateWithRefresh, ct).ConfigureAwait(false);

            if (refreshed is not null)
            {
                _cachedState = refreshed;
                SaveStateToDisk(refreshed);
                return new FirebaseAuthSession(refreshed.IdToken, refreshed.UserId);
            }

            var newState = await SignInAsync(ct).ConfigureAwait(false);
            _cachedState = newState;
            SaveStateToDisk(newState);
            return new FirebaseAuthSession(newState.IdToken, newState.UserId);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<FirebaseAuthSession?> TryGetExistingSessionAsync(CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FirebaseAnonymousAuthenticator));
        if (InternetAccessManager.IsInternetAccessDisabled) return null;

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _cachedState ??= LoadStateFromDisk();

            if (_cachedState is null) return null;

            if (string.IsNullOrWhiteSpace(_cachedState.UserId))
            {
                _cachedState = null;
                DeleteStateFile();
                return null;
            }

            var currentState = _cachedState!;

            if (!currentState.IsExpired) return new FirebaseAuthSession(currentState.IdToken, currentState.UserId);

            if (string.IsNullOrWhiteSpace(currentState.RefreshToken)) return null;

            FirebaseAuthState? refreshed;
            try
            {
                refreshed = await TryRefreshAsync(currentState, ct).ConfigureAwait(false);
            }
            catch (InternetAccessDisabledException)
            {
                return null;
            }

            if (refreshed is null || string.IsNullOrWhiteSpace(refreshed.UserId))
            {
                _cachedState = null;
                DeleteStateFile();
                return null;
            }

            _cachedState = refreshed;
            SaveStateToDisk(refreshed);
            return new FirebaseAuthSession(refreshed.IdToken, refreshed.UserId);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    ///     Marks the current token as expired so the next call will refresh it.
    /// </summary>
    public async Task MarkTokenAsExpiredAsync(CancellationToken ct)
    {
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = _cachedState ?? LoadStateFromDisk();
            if (state is null) return;

            var expired = state.WithExpiration(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5));
            _cachedState = expired;
            SaveStateToDisk(expired);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task DeleteAccountAsync(CancellationToken ct)
    {
        InternetAccessManager.ThrowIfInternetAccessDisabled();

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = _cachedState ?? LoadStateFromDisk();
            if (state is null)
            {
                _cachedState = null;
                DeleteStateFile();
                return;
            }

            var currentState = state;
            if (currentState.IsExpired)
            {
                var refreshed = await TryRefreshAsync(currentState, ct).ConfigureAwait(false);
                if (refreshed is not null)
                {
                    currentState = refreshed;
                    _cachedState = refreshed;
                    SaveStateToDisk(refreshed);
                }
            }

            await DeleteAccountInternalAsync(currentState.IdToken, ct).ConfigureAwait(false);

            _cachedState = null;
            DeleteStateFile();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    internal static bool HasPersistedState()
    {
        var stateFilePath = GetStateFilePath();
        if (string.IsNullOrWhiteSpace(stateFilePath) || !File.Exists(stateFilePath)) return false;

        try
        {
            var json = File.ReadAllText(stateFilePath);
            if (string.IsNullOrWhiteSpace(json)) return false;

            var model = JsonSerializer.Deserialize<AuthStateModel>(json, JsonOptions);
            if (model is null
                || string.IsNullOrWhiteSpace(model.IdToken)
                || string.IsNullOrWhiteSpace(model.RefreshToken)
                || string.IsNullOrWhiteSpace(model.UserId))
                return false;

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (SecurityException)
        {
            return false;
        }
    }

    internal static string GetStateFilePath()
    {
        var developerOverride = DeveloperProfileManager.GetFirebaseStateFilePathOverride();
        if (!string.IsNullOrWhiteSpace(developerOverride)) return developerOverride!;

        var secureDirectory = TryGetSecureBaseDirectory();

        if (!string.IsNullOrWhiteSpace(secureDirectory)) return Path.Combine(secureDirectory!, StateFileName);

        return GetDefaultStateFilePath();
    }

    private static string DetermineStateFilePath()
    {
        var stateFilePath = GetStateFilePath();
        var directory = Path.GetDirectoryName(stateFilePath);

        if (!string.IsNullOrWhiteSpace(directory))
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (NotSupportedException)
            {
            }
            catch (SecurityException)
            {
            }

        return stateFilePath;
    }

    private static string? TryGetSecureBaseDirectory()
    {
        // First check if there's a custom config folder configured
        var customFolder = CustomConfigFolderManager.GetCustomConfigFolder();
        if (!string.IsNullOrWhiteSpace(customFolder) && TryEnsureDirectory(customFolder))
            return customFolder;

        // Fall back to default locations
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.LocalApplicationData,
                     Environment.SpecialFolder.ApplicationData
                 })
        {
            var root = TryGetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(root)) continue;

            var candidate = Path.Combine(root!, "Simple VS Manager");
            if (TryEnsureDirectory(candidate)) return candidate;
        }

        return null;
    }

    private static string GetDefaultStateFilePath()
    {
        var baseDirectory = ModCacheLocator.GetManagerDataDirectory();
        if (!string.IsNullOrWhiteSpace(baseDirectory)) return Path.Combine(baseDirectory, StateFileName);

        var documents = TryGetFolderPath(Environment.SpecialFolder.MyDocuments)
                        ?? TryGetFolderPath(Environment.SpecialFolder.Personal);

        var root = string.IsNullOrWhiteSpace(documents)
            ? Environment.CurrentDirectory
            : documents!;

        return Path.Combine(root, "Simple VS Manager", StateFileName);
    }

    private static string? TryGetFolderPath(Environment.SpecialFolder folder)
    {
        try
        {
            var path = Environment.GetFolderPath(folder);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception ex) when
            (ex is PlatformNotSupportedException or InvalidOperationException or SecurityException)
        {
            return null;
        }
    }

    private static bool TryEnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or SecurityException)
        {
            return false;
        }
    }

    private FirebaseAuthState? LoadStateFromDisk()
    {
        try
        {
            if (!File.Exists(_stateFilePath)) return null;

            var json = File.ReadAllText(_stateFilePath);
            if (string.IsNullOrWhiteSpace(json)) return null;

            // Try the current format first
            var state = TryDeserializeState(json);
            if (state is not null) return state;

            // Attempt to migrate legacy formats before giving up
            var legacyState = TryDeserializeLegacyState(json);
            return legacyState;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (SecurityException)
        {
            return null;
        }
    }

    private static FirebaseAuthState? TryDeserializeState(string json)
    {
        var model = JsonSerializer.Deserialize<AuthStateModel>(json, JsonOptions);
        if (model is null ||
            string.IsNullOrWhiteSpace(model.IdToken) ||
            string.IsNullOrWhiteSpace(model.RefreshToken) ||
            string.IsNullOrWhiteSpace(model.UserId))
            return null;

        var expiration = model.ExpirationUtc == default
            ? DateTimeOffset.MinValue
            : model.ExpirationUtc;

        return new FirebaseAuthState(model.IdToken.Trim(), model.RefreshToken.Trim(), expiration,
            model.UserId.Trim());
    }

    private static FirebaseAuthState? TryDeserializeLegacyState(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var idToken = TryGetString(root, "idToken");
        var refreshToken = TryGetString(root, "refreshToken");
        var userId = TryGetString(root, "userId")
                     ?? TryGetString(root, "uid")
                     ?? TryGetString(root, "localId");

        if (string.IsNullOrWhiteSpace(idToken) ||
            string.IsNullOrWhiteSpace(refreshToken) ||
            string.IsNullOrWhiteSpace(userId))
            return null;

        var expirationRaw = TryGetString(root, "expirationUtc")
                            ?? TryGetString(root, "expiration")
                            ?? TryGetString(root, "expiresAt");

        var expiration = DateTimeOffset.TryParse(expirationRaw, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

        return new FirebaseAuthState(idToken.Trim(), refreshToken.Trim(), expiration, userId.Trim());
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out var number) ? number.ToString() : value.ToString(),
            _ => null
        };
    }

    private void SaveStateToDisk(FirebaseAuthState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var model = new AuthStateModel
            {
                IdToken = state.IdToken,
                RefreshToken = state.RefreshToken,
                ExpirationUtc = state.Expiration,
                UserId = state.UserId
            };

            var json = JsonSerializer.Serialize(model, JsonOptions);
            File.WriteAllText(_stateFilePath, json);

            // Try to create a backup after successfully saving the auth file
            TryCreateBackup();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (SecurityException)
        {
        }
    }

    private void TryCreateBackup()
    {
        try
        {
            // Check if the original auth file exists
            if (!File.Exists(_stateFilePath)) return;

            // Try to get user configuration to check if backup has been created
            var config = TryGetUserConfiguration();
            if (config is not null && config.FirebaseAuthBackupCreated)
                // Backup flag is already set, no need to check further
                return;

            // Determine backup path: AppData/Local/SVSM Backup/firebase-auth.json
            var backupPath = GetBackupFilePath();
            if (string.IsNullOrWhiteSpace(backupPath)) return;

            // If backup file already exists, don't overwrite it
            if (File.Exists(backupPath))
            {
                // Backup exists, just update the config flag
                MarkBackupCreated(config);
                return;
            }

            // Create backup directory if it doesn't exist
            var backupDirectory = Path.GetDirectoryName(backupPath);
            if (!string.IsNullOrWhiteSpace(backupDirectory)) Directory.CreateDirectory(backupDirectory);

            // Copy the auth file to backup location
            File.Copy(_stateFilePath, backupPath, false);

            // Mark backup as created in configuration
            MarkBackupCreated(config);
        }
        catch (IOException)
        {
            // Silently ignore backup failures
        }
        catch (UnauthorizedAccessException)
        {
            // Silently ignore backup failures
        }
        catch (SecurityException)
        {
            // Silently ignore backup failures
        }
        catch (NotSupportedException)
        {
            // Silently ignore backup failures
        }
    }

    private static void MarkBackupCreated(UserConfigurationService? config)
    {
        if (config is not null)
        {
            config.EnablePersistence();
            config.SetFirebaseAuthBackupCreated();
        }
    }

    private static UserConfigurationService? TryGetUserConfiguration()
    {
        try
        {
            // Create a new instance to read the current configuration
            return new UserConfigurationService();
        }
        catch
        {
            return null;
        }
    }

    internal static string? GetBackupFilePath()
    {
        try
        {
            // Always use the default location in LocalApplicationData for the backup
            // The SVSM Backup folder should remain in its original location
            // even if the Simple VS Manager folder is moved to a custom location
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData)) return null;

            var backupDirectory = Path.Combine(localAppData, DevConfig.FirebaseAuthBackupDirectoryName);
            return Path.Combine(backupDirectory, StateFileName);
        }
        catch
        {
            return null;
        }
    }

    private void DeleteStateFile()
    {
        try
        {
            if (File.Exists(_stateFilePath)) File.Delete(_stateFilePath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (SecurityException)
        {
        }
    }

    private async Task<FirebaseAuthState> SignInAsync(CancellationToken ct)
    {
        InternetAccessManager.ThrowIfInternetAccessDisabled();
        var requestUri = $"{SignInEndpoint}?key={Uri.EscapeDataString(ApiKey)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent("{\"returnSecureToken\":true}", Encoding.UTF8, "application/json")
        };

        using var response = await HttpClient.SendAsync(request, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Firebase anonymous sign-in failed: {(int)response.StatusCode} {response.ReasonPhrase} | {Truncate(json, 200)}");

        var payload = JsonSerializer.Deserialize<SignInResponse>(json, JsonOptions);
        if (payload is null ||
            string.IsNullOrWhiteSpace(payload.IdToken) ||
            string.IsNullOrWhiteSpace(payload.RefreshToken) ||
            string.IsNullOrWhiteSpace(payload.ExpiresIn))
            throw new InvalidOperationException("Firebase anonymous sign-in returned an unexpected response.");

        var expiration = DateTimeOffset.UtcNow.AddSeconds(ParseExpirationSeconds(payload.ExpiresIn));
        if (string.IsNullOrWhiteSpace(payload.LocalId))
            throw new InvalidOperationException(
                "Firebase anonymous sign-in returned a response without a user identifier.");

        return new FirebaseAuthState(payload.IdToken.Trim(), payload.RefreshToken.Trim(), expiration,
            payload.LocalId.Trim());
    }

    private async Task DeleteAccountInternalAsync(string idToken, CancellationToken ct)
    {
        InternetAccessManager.ThrowIfInternetAccessDisabled();
        var requestUri = $"{DeleteEndpoint}?key={Uri.EscapeDataString(ApiKey)}";
        var payload = JsonSerializer.Serialize(new { idToken });

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var response = await HttpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return;

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound) return;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new InvalidOperationException(
            $"Firebase account deletion failed: {(int)response.StatusCode} {response.ReasonPhrase} | {Truncate(body, 200)}");
    }

    private async Task<FirebaseAuthState?> TryRefreshAsync(FirebaseAuthState existingState, CancellationToken ct)
    {
        InternetAccessManager.ThrowIfInternetAccessDisabled();
        var requestUri = $"{RefreshEndpoint}?key={Uri.EscapeDataString(ApiKey)}";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = existingState.RefreshToken
        });

        using var response = await HttpClient.PostAsync(requestUri, content, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) return null;

        var payload = JsonSerializer.Deserialize<RefreshResponse>(json, JsonOptions);
        if (payload is null ||
            string.IsNullOrWhiteSpace(payload.IdToken) ||
            string.IsNullOrWhiteSpace(payload.RefreshToken) ||
            string.IsNullOrWhiteSpace(payload.ExpiresIn))
            return null;

        var expiration = DateTimeOffset.UtcNow.AddSeconds(ParseExpirationSeconds(payload.ExpiresIn));
        var userId = !string.IsNullOrWhiteSpace(payload.UserId)
            ? payload.UserId.Trim()
            : existingState.UserId;

        if (string.IsNullOrWhiteSpace(userId)) return null;

        return new FirebaseAuthState(payload.IdToken.Trim(), payload.RefreshToken.Trim(), expiration, userId);
    }

    private static double ParseExpirationSeconds(string expiresIn)
    {
        if (double.TryParse(expiresIn, out var seconds) && seconds > 0) return seconds;

        return 3600; // Default to one hour if parsing fails.
    }

    private static string Truncate(string value, int length)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= length) return value;

        return value.Substring(0, length) + "...";
    }

    /// <summary>
    ///     Checks if Firebase auth backup should be created and attempts to create it on app startup.
    ///     This ensures the firebase-auth.json file is backed up to AppData/Local/SVSM Backup/ if it exists
    ///     and hasn't been backed up yet.
    /// </summary>
    /// <param name="config">User configuration service to check and update backup status.</param>
    public static void EnsureStartupBackup(UserConfigurationService? config)
    {
        try
        {
            // If config is null or backup already created, nothing to do
            if (config is null || config.FirebaseAuthBackupCreated) return;

            // Get the path to the main firebase-auth.json file
            var stateFilePath = GetStateFilePath();
            if (string.IsNullOrWhiteSpace(stateFilePath) || !File.Exists(stateFilePath))
                // No auth file exists, nothing to backup
                return;

            // Get the backup file path
            var backupPath = GetBackupFilePath();
            if (string.IsNullOrWhiteSpace(backupPath)) return;

            // If backup file already exists, just mark it as created
            if (File.Exists(backupPath))
            {
                MarkBackupCreated(config);
                return;
            }

            // Create backup directory if it doesn't exist
            var backupDirectory = Path.GetDirectoryName(backupPath);
            if (!string.IsNullOrWhiteSpace(backupDirectory)) Directory.CreateDirectory(backupDirectory);

            // Copy the auth file to backup location
            File.Copy(stateFilePath, backupPath, false);

            // Mark backup as created in configuration
            MarkBackupCreated(config);
        }
        catch (IOException)
        {
            // Silently ignore backup failures
        }
        catch (UnauthorizedAccessException)
        {
            // Silently ignore backup failures
        }
        catch (SecurityException)
        {
            // Silently ignore backup failures
        }
        catch (NotSupportedException)
        {
            // Silently ignore backup failures
        }
    }

    private sealed class AuthStateModel
    {
        public string? IdToken { get; set; }

        public string? RefreshToken { get; set; }

        public DateTimeOffset ExpirationUtc { get; set; }

        public string? UserId { get; set; }
    }

    private sealed class SignInResponse
    {
        [JsonPropertyName("idToken")] public string? IdToken { get; set; }

        [JsonPropertyName("refreshToken")] public string? RefreshToken { get; set; }

        [JsonPropertyName("expiresIn")] public string? ExpiresIn { get; set; }

        [JsonPropertyName("localId")] public string? LocalId { get; set; }
    }

    private sealed class RefreshResponse
    {
        [JsonPropertyName("id_token")] public string? IdToken { get; set; }

        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")] public string? ExpiresIn { get; set; }

        [JsonPropertyName("user_id")] public string? UserId { get; set; }
    }

    private sealed class FirebaseAuthState
    {
        public FirebaseAuthState(string idToken, string refreshToken, DateTimeOffset expiration, string userId)
        {
            IdToken = idToken;
            RefreshToken = refreshToken;
            Expiration = expiration;
            UserId = userId;
        }

        public string IdToken { get; }

        public string RefreshToken { get; }

        public DateTimeOffset Expiration { get; }

        public string UserId { get; }

        public bool IsExpired => DateTimeOffset.UtcNow >= Expiration - ExpirationSkew;

        public FirebaseAuthState WithExpiration(DateTimeOffset expiration)
        {
            return new FirebaseAuthState(IdToken, RefreshToken, expiration, UserId);
        }
    }

    public readonly struct FirebaseAuthSession
    {
        public FirebaseAuthSession(string idToken, string userId)
        {
            IdToken = idToken;
            UserId = userId;
        }

        public string IdToken { get; }

        public string UserId { get; }
    }

    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        // Dispose semaphore outside the lock to avoid potential deadlocks.
        // The _disposed flag (set atomically above) prevents new operations.
        _stateLock.Dispose();
    }
}