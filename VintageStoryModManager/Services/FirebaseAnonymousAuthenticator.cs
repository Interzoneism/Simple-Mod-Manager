using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VintageStoryModManager.Services;

namespace SimpleVsManager.Cloud;

/// <summary>
/// Handles Firebase anonymous authentication and persists refresh tokens for reuse.
/// </summary>
public sealed class FirebaseAnonymousAuthenticator
{
    private const string SignInEndpoint = "https://identitytoolkit.googleapis.com/v1/accounts:signUp";
    private const string RefreshEndpoint = "https://securetoken.googleapis.com/v1/token";
    private const string DeleteEndpoint = "https://identitytoolkit.googleapis.com/v1/accounts:delete";
    private const string StateFileName = "firebase-auth.json";
    private static readonly HttpClient HttpClient = new();
    private static readonly TimeSpan ExpirationSkew = TimeSpan.FromMinutes(2);

    private readonly string _apiKey;
    private readonly string _stateFilePath;
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    private FirebaseAuthState? _cachedState;

    private const string DefaultApiKey = "AIzaSyCmDJ9yC1ccUEUf41fC-SI8fuXFJzWWlHY";

    public FirebaseAnonymousAuthenticator() : this(DefaultApiKey) { }

    public FirebaseAnonymousAuthenticator(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("A Firebase API key is required.", nameof(apiKey));
        }

        _apiKey = apiKey.Trim();
        _stateFilePath = DetermineStateFilePath();
    }


    public string ApiKey => _apiKey;

    /// <summary>
    /// Retrieves a valid ID token, refreshing or signing in anonymously as required.
    /// </summary>
    public async Task<string> GetIdTokenAsync(CancellationToken ct)
    {
        InternetAccessManager.ThrowIfInternetAccessDisabled();
        FirebaseAuthSession session = await GetSessionAsync(ct).ConfigureAwait(false);
        return session.IdToken;
    }

    public async Task<FirebaseAuthSession> GetSessionAsync(CancellationToken ct)
    {
        InternetAccessManager.ThrowIfInternetAccessDisabled();
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _cachedState ??= LoadStateFromDisk();

            if (_cachedState is { } cachedWithoutUser && string.IsNullOrWhiteSpace(cachedWithoutUser.UserId))
            {
                _cachedState = null;
            }

            if (_cachedState is { } existing && !existing.IsExpired)
            {
                return new FirebaseAuthSession(existing.IdToken, existing.UserId);
            }

            FirebaseAuthState? refreshed = null;
            if (_cachedState is { } stateWithRefresh && !string.IsNullOrWhiteSpace(stateWithRefresh.RefreshToken))
            {
                refreshed = await TryRefreshAsync(stateWithRefresh, ct).ConfigureAwait(false);
            }

            if (refreshed is not null)
            {
                _cachedState = refreshed;
                SaveStateToDisk(refreshed);
                return new FirebaseAuthSession(refreshed.IdToken, refreshed.UserId);
            }

            FirebaseAuthState newState = await SignInAsync(ct).ConfigureAwait(false);
            _cachedState = newState;
            SaveStateToDisk(newState);
            return new FirebaseAuthSession(newState.IdToken, newState.UserId);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Marks the current token as expired so the next call will refresh it.
    /// </summary>
    public async Task MarkTokenAsExpiredAsync(CancellationToken ct)
    {
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            FirebaseAuthState? state = _cachedState ?? LoadStateFromDisk();
            if (state is null)
            {
                return;
            }

            FirebaseAuthState expired = state.WithExpiration(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5));
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
            FirebaseAuthState? state = _cachedState ?? LoadStateFromDisk();
            if (state is null)
            {
                _cachedState = null;
                DeleteStateFile();
                return;
            }

            FirebaseAuthState currentState = state;
            if (currentState.IsExpired)
            {
                FirebaseAuthState? refreshed = await TryRefreshAsync(currentState, ct).ConfigureAwait(false);
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

    internal static string GetStateFilePath()
    {
        string? secureDirectory = TryGetSecureBaseDirectory();

        if (!string.IsNullOrWhiteSpace(secureDirectory))
        {
            return Path.Combine(secureDirectory!, StateFileName);
        }

        return GetDefaultStateFilePath();
    }

    private static string DetermineStateFilePath()
    {
        string stateFilePath = GetStateFilePath();
        string? directory = Path.GetDirectoryName(stateFilePath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
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
        }

        return stateFilePath;
    }

    private static string? TryGetSecureBaseDirectory()
    {
        foreach (Environment.SpecialFolder folder in new[]
                 {
                     Environment.SpecialFolder.LocalApplicationData,
                     Environment.SpecialFolder.ApplicationData
                 })
        {
            string? root = TryGetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string candidate = Path.Combine(root!, "Simple VS Manager");
            if (TryEnsureDirectory(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string GetDefaultStateFilePath()
    {
        string? baseDirectory = ModCacheLocator.GetManagerDataDirectory();
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            return Path.Combine(baseDirectory, StateFileName);
        }

        string? documents = TryGetFolderPath(Environment.SpecialFolder.MyDocuments)
            ?? TryGetFolderPath(Environment.SpecialFolder.Personal);

        string root = string.IsNullOrWhiteSpace(documents)
            ? Environment.CurrentDirectory
            : documents!;

        return Path.Combine(root, "Simple VS Manager", StateFileName);
    }

    private static string? TryGetFolderPath(Environment.SpecialFolder folder)
    {
        try
        {
            string path = Environment.GetFolderPath(folder);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or SecurityException)
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private FirebaseAuthState? LoadStateFromDisk()
    {
        try
        {
            if (!File.Exists(_stateFilePath))
            {
                return null;
            }

            string json = File.ReadAllText(_stateFilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            AuthStateModel? model = JsonSerializer.Deserialize<AuthStateModel>(json, JsonOptions);
            if (model is null ||
                string.IsNullOrWhiteSpace(model.IdToken) ||
                string.IsNullOrWhiteSpace(model.RefreshToken) ||
                string.IsNullOrWhiteSpace(model.UserId))
            {
                return null;
            }

            DateTimeOffset expiration = model.ExpirationUtc == default
                ? DateTimeOffset.MinValue
                : model.ExpirationUtc;

            return new FirebaseAuthState(model.IdToken.Trim(), model.RefreshToken.Trim(), expiration, model.UserId.Trim());
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

    private void SaveStateToDisk(FirebaseAuthState state)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var model = new AuthStateModel
            {
                IdToken = state.IdToken,
                RefreshToken = state.RefreshToken,
                ExpirationUtc = state.Expiration,
                UserId = state.UserId
            };

            string json = JsonSerializer.Serialize(model, JsonOptions);
            File.WriteAllText(_stateFilePath, json);
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

    private void DeleteStateFile()
    {
        try
        {
            if (File.Exists(_stateFilePath))
            {
                File.Delete(_stateFilePath);
            }
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
        string requestUri = $"{SignInEndpoint}?key={Uri.EscapeDataString(_apiKey)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent("{\"returnSecureToken\":true}", Encoding.UTF8, "application/json")
        };

        using HttpResponseMessage response = await HttpClient.SendAsync(request, ct).ConfigureAwait(false);
        string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Firebase anonymous sign-in failed: {(int)response.StatusCode} {response.ReasonPhrase} | {Truncate(json, 200)}");
        }

        SignInResponse? payload = JsonSerializer.Deserialize<SignInResponse>(json, JsonOptions);
        if (payload is null ||
            string.IsNullOrWhiteSpace(payload.IdToken) ||
            string.IsNullOrWhiteSpace(payload.RefreshToken) ||
            string.IsNullOrWhiteSpace(payload.ExpiresIn))
        {
            throw new InvalidOperationException("Firebase anonymous sign-in returned an unexpected response.");
        }

        DateTimeOffset expiration = DateTimeOffset.UtcNow.AddSeconds(ParseExpirationSeconds(payload.ExpiresIn));
        if (string.IsNullOrWhiteSpace(payload.LocalId))
        {
            throw new InvalidOperationException("Firebase anonymous sign-in returned a response without a user identifier.");
        }

        return new FirebaseAuthState(payload.IdToken.Trim(), payload.RefreshToken.Trim(), expiration, payload.LocalId.Trim());
    }

    private async Task DeleteAccountInternalAsync(string idToken, CancellationToken ct)
    {
        InternetAccessManager.ThrowIfInternetAccessDisabled();
        string requestUri = $"{DeleteEndpoint}?key={Uri.EscapeDataString(_apiKey)}";
        string payload = JsonSerializer.Serialize(new { idToken });

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using HttpResponseMessage response = await HttpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new InvalidOperationException($"Firebase account deletion failed: {(int)response.StatusCode} {response.ReasonPhrase} | {Truncate(body, 200)}");
    }

    private async Task<FirebaseAuthState?> TryRefreshAsync(FirebaseAuthState existingState, CancellationToken ct)
    {
        InternetAccessManager.ThrowIfInternetAccessDisabled();
        string requestUri = $"{RefreshEndpoint}?key={Uri.EscapeDataString(_apiKey)}";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = existingState.RefreshToken
        });

        using HttpResponseMessage response = await HttpClient.PostAsync(requestUri, content, ct).ConfigureAwait(false);
        string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        RefreshResponse? payload = JsonSerializer.Deserialize<RefreshResponse>(json, JsonOptions);
        if (payload is null ||
            string.IsNullOrWhiteSpace(payload.IdToken) ||
            string.IsNullOrWhiteSpace(payload.RefreshToken) ||
            string.IsNullOrWhiteSpace(payload.ExpiresIn))
        {
            return null;
        }

        DateTimeOffset expiration = DateTimeOffset.UtcNow.AddSeconds(ParseExpirationSeconds(payload.ExpiresIn));
        string userId = !string.IsNullOrWhiteSpace(payload.UserId)
            ? payload.UserId.Trim()
            : existingState.UserId;

        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        return new FirebaseAuthState(payload.IdToken.Trim(), payload.RefreshToken.Trim(), expiration, userId);
    }

    private static double ParseExpirationSeconds(string expiresIn)
    {
        if (double.TryParse(expiresIn, out double seconds) && seconds > 0)
        {
            return seconds;
        }

        return 3600; // Default to one hour if parsing fails.
    }

    private static string Truncate(string value, int length)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= length)
        {
            return value;
        }

        return value.Substring(0, length) + "...";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class AuthStateModel
    {
        public string? IdToken { get; set; }

        public string? RefreshToken { get; set; }

        public DateTimeOffset ExpirationUtc { get; set; }

        public string? UserId { get; set; }
    }

    private sealed class SignInResponse
    {
        [JsonPropertyName("idToken")]
        public string? IdToken { get; set; }

        [JsonPropertyName("refreshToken")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expiresIn")]
        public string? ExpiresIn { get; set; }

        [JsonPropertyName("localId")]
        public string? LocalId { get; set; }
    }

    private sealed class RefreshResponse
    {
        [JsonPropertyName("id_token")]
        public string? IdToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public string? ExpiresIn { get; set; }

        [JsonPropertyName("user_id")]
        public string? UserId { get; set; }
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
            => new(IdToken, RefreshToken, expiration, UserId);
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
}
