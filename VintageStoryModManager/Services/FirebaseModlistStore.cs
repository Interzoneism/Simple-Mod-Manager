using System.Buffers;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using VintageStoryModManager;
using VintageStoryModManager.Models;
using VintageStoryModManager.Services;

namespace SimpleVsManager.Cloud;

/// <summary>
///     Firebase RTDB access that relies solely on the player's Vintage Story identity.
/// </summary>
public sealed class FirebaseModlistStore : IDisposable
{
    private static readonly HttpClient HttpClient = new();

    private static readonly string DefaultDbUrl = DevConfig.FirebaseModlistDefaultDbUrl;

    private static readonly string[] KnownSlots = { "slot1", "slot2", "slot3", "slot4", "slot5" };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _dbUrl;
    private readonly SemaphoreSlim _ownershipClaimLock = new(1, 1);
    private readonly SemaphoreSlim _registryCacheLock = new(1, 1);
    private readonly object _disposeLock = new();
    private string? _ownershipClaimedForUid;
    private string? _playerName;

    private readonly Dictionary<string, RegistryCache> _registryCache =
        new(StringComparer.OrdinalIgnoreCase);

    private string? _playerUid; // Original player UID from Vintage Story
    private string? _sanitizedPlayerUid; // Firebase-compatible version of the player UID
    private bool _disposed;

    public FirebaseModlistStore()
        : this(DefaultDbUrl, new FirebaseAnonymousAuthenticator())
    {
    }

    public FirebaseModlistStore(string dbUrl, FirebaseAnonymousAuthenticator authenticator)
    {
        _dbUrl = (dbUrl ?? throw new ArgumentNullException(nameof(dbUrl))).TrimEnd('/');
        Authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
    }


    internal FirebaseAnonymousAuthenticator Authenticator { get; }

    // Ensure uploader fields are present and correct
    // (kept from your code; unchanged signature)


    /// <summary>
    ///     Gets the known slot keys used for storing modlists in Firebase.
    /// </summary>
    public static IReadOnlyList<string> SlotKeys => KnownSlots;

    /// <summary>
    ///     Gets the identifier used for Firebase storage operations (player UID).
    /// </summary>
    public string? CurrentUserId => string.IsNullOrWhiteSpace(_playerUid) ? null : _playerUid;

    // Build a user-slot JSON object with optional registryId and mandatory content
    private static string BuildSlotNodeJson(string contentJson, string? registryId, string dateAdded)
    {
        using var contentDoc = JsonDocument.Parse(contentJson);
        var node = new SlotNode
        {
            RegistryId = registryId,
            Content = contentDoc.RootElement.Clone(),
            DateAdded = dateAdded
        };
        return JsonSerializer.Serialize(node, JsonOpts);
    }

    // Read a full slot node (to get registryId + content)
    private async Task<SlotNode?> TryReadSlotNodeAsync(string uid, string slotKey, CancellationToken ct)
    {
        var sendResult = await SendWithAuthRetryAsync(session =>
        {
            var url = BuildAuthenticatedUrl(session.IdToken, null, "users", uid, slotKey);
            return HttpClient.GetAsync(url, ct);
        }, ct).ConfigureAwait(false);

        using var response = sendResult.Response;

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureOk(response, "Read slot").ConfigureAwait(false);

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json) || json == "null")
            return null;

        try
        {
            return JsonSerializer.Deserialize<SlotNode?>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Registers the player identity in the admin registry for troubleshooting purposes.
    ///     This is called automatically during save operations.
    ///     Stores both the original and sanitized UIDs for reference.
    /// </summary>
    private async Task RegisterPlayerIdentityAsync(PlayerIdentity identity, string firebaseUid, CancellationToken ct)
    {
        try
        {
            var sendResult = await SendWithAuthRetryAsync(session =>
            {
                var adminUrl = BuildAuthenticatedUrl(session.IdToken, null, "adminRegistry", session.UserId);

                var registryEntry = new
                {
                    playerUid = identity.OriginalUid,
                    sanitizedPlayerUid = identity.SanitizedUid,
                    playerName = identity.Name,
                    lastUpdated = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                };

                var payload = JsonSerializer.Serialize(registryEntry, JsonOpts);
                var request = new HttpRequestMessage(HttpMethod.Put, adminUrl)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                return HttpClient.SendAsync(request, ct);
            }, ct).ConfigureAwait(false);

            using (sendResult.Response)
            {
                // Silently ignore failures - this is a best-effort logging mechanism
                if (!sendResult.Response.IsSuccessStatusCode)
                {
                    // Don't throw - we don't want to block saves if admin registry fails
                }
            }
        }
        catch
        {
            // Silently ignore - admin registry is optional
        }
    }

    private static string GenerateEntryId()
    {
        return Guid.NewGuid().ToString("n");
    }

    /// <summary>
    ///     Applies the current player identity sourced from clientsettings.json.
    /// </summary>
    public void SetPlayerIdentity(string? playerUid, string? playerName)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FirebaseModlistStore));
        _playerUid = Normalize(playerUid);
        _playerName = Normalize(playerName);

        // Sanitize the player UID for Firebase compatibility
        _sanitizedPlayerUid = string.IsNullOrWhiteSpace(_playerUid)
            ? null
            : SanitizePlayerUidForFirebase(_playerUid);

        if (!string.Equals(_ownershipClaimedForUid, _sanitizedPlayerUid, StringComparison.Ordinal))
            _ownershipClaimedForUid = null;
    }

    /// <summary>Save or replace the JSON in the given slot (e.g., "slot1").</summary>
    public async Task SaveAsync(string slotKey, string modlistJson, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FirebaseModlistStore));
        InternetAccessManager.ThrowIfInternetAccessDisabled();
        ValidateSlotKey(slotKey);
        var identity = GetIdentityComponents();
        await EnsureOwnershipAsync(identity, ct).ConfigureAwait(false);

        using var document = JsonDocument.Parse(modlistJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The modlist JSON must be an object.");

        // Normalize uploader fields inside the content
        var normalizedContent = ReplaceUploader(document.RootElement, identity);

        // 1) Read existing slot to see if it already has a registryId
        var existing = await TryReadSlotNodeAsync(identity.SanitizedUid, slotKey, ct).ConfigureAwait(false);
        var registryId = existing.HasValue && !string.IsNullOrWhiteSpace(existing.Value.RegistryId)
            ? existing.Value.RegistryId!
            : GenerateEntryId();

        var dateAddedIso = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        var summaryJson = BuildSummaryJson(normalizedContent, dateAddedIso);

        // Build the user slot payload including registryId
        var userSlotJson = BuildSlotNodeJson(normalizedContent, registryId, dateAddedIso);

        // 2) Atomic multi-location PATCH:
        // - write /users/{uid}/{slot} (content + registryId)
        // - write /registryOwners/{registryId} = auth.uid (safe even if same value)
        // - write /registry/{registryId} (public content mirror)
        // - write /registrySummaries/{registryId} (public summary mirror)
        var saveResult = await SendWithAuthRetryAsync(session =>
        {
            var rootUrl = BuildAuthenticatedUrl(session.IdToken, null /* root */);

            var registryNodeJson =
                $"{{\"content\":{normalizedContent},\"dateAdded\":{JsonSerializer.Serialize(dateAddedIso)}}}";
            var registryOwnerJson = JsonSerializer.Serialize(session.UserId);

            var patchJson =
                $"{{" +
                $"\"/users/{identity.SanitizedUid}/{slotKey}\":{userSlotJson}," +
                $"\"/registryOwners/{registryId}\":{registryOwnerJson}," +
                $"\"/registry/{registryId}\":{registryNodeJson}," +
                $"\"/registrySummaries/{registryId}\":{summaryJson}" +
                $"}}";

            var req = new HttpRequestMessage(new HttpMethod("PATCH"), rootUrl)
            {
                Content = new StringContent(patchJson, Encoding.UTF8, "application/json")
            };
            return HttpClient.SendAsync(req, ct);
        }, ct).ConfigureAwait(false);

        using (saveResult.Response)
        {
            await EnsureOk(saveResult.Response, "Save (user + registry)").ConfigureAwait(false);
        }

        // Register player identity in admin registry (best-effort, non-blocking)
        await RegisterPlayerIdentityAsync(identity, saveResult.Session.UserId, ct).ConfigureAwait(false);
    }


    /// <summary>Load a JSON string from the slot. Returns null if missing.</summary>
    public async Task<string?> LoadAsync(string slotKey, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FirebaseModlistStore));
        InternetAccessManager.ThrowIfInternetAccessDisabled();
        ValidateSlotKey(slotKey);
        var identity = GetIdentityComponents();
        await EnsureOwnershipAsync(identity, ct).ConfigureAwait(false);

        var sendResult = await SendWithAuthRetryAsync(session =>
        {
            var userUrl = BuildAuthenticatedUrl(session.IdToken, null, "users", identity.SanitizedUid, slotKey);
            return HttpClient.GetAsync(userUrl, ct);
        }, ct).ConfigureAwait(false);

        using var response = sendResult.Response;

        if (response.StatusCode == HttpStatusCode.NotFound) return null;

        await EnsureOk(response, "Load").ConfigureAwait(false);

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length == 0) return null;

        var node = JsonSerializer.Deserialize<ModlistNode?>(bytes, JsonOpts);
        if (node is null || node.Value.Content.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;

        return node.Value.Content.GetRawText();
    }

    public async Task<IReadOnlyList<string>> ListSlotsAsync(CancellationToken ct = default)
    {
        InternetAccessManager.ThrowIfInternetAccessDisabled();
        var identity = GetIdentityComponents();
        await EnsureOwnershipAsync(identity, ct).ConfigureAwait(false); // NEW
        return await ListSlotsAsync(identity, ct);
    }


    /// <summary>Delete a slot if present (idempotent).</summary>
    public async Task DeleteAsync(string slotKey, CancellationToken ct = default)
    {
        InternetAccessManager.ThrowIfInternetAccessDisabled();
        ValidateSlotKey(slotKey);
        var identity = GetIdentityComponents();
        await EnsureOwnershipAsync(identity, ct).ConfigureAwait(false);

        // Read the slot to discover its registryId (if any)
        var node = await TryReadSlotNodeAsync(identity.SanitizedUid, slotKey, ct).ConfigureAwait(false);
        var registryId = node?.RegistryId;

        var result = await SendWithAuthRetryAsync(session =>
        {
            var rootUrl = BuildAuthenticatedUrl(session.IdToken, null /* root */);

            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append($"\"/users/{identity.SanitizedUid}/{slotKey}\":null");

            if (!string.IsNullOrWhiteSpace(registryId))
            {
                sb.Append($",\"/registry/{registryId}\":null");
                sb.Append($",\"/registryOwners/{registryId}\":null");
                sb.Append($",\"/registrySummaries/{registryId}\":null");
            }

            sb.Append('}');

            var req = new HttpRequestMessage(new HttpMethod("PATCH"), rootUrl)
            {
                Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json")
            };
            return HttpClient.SendAsync(req, ct);
        }, ct).ConfigureAwait(false);

        using (result.Response)
        {
            if (!result.Response.IsSuccessStatusCode && result.Response.StatusCode != HttpStatusCode.NotFound)
                await EnsureOk(result.Response, "Delete (user + registry)").ConfigureAwait(false);
        }
    }


    public async Task DeleteAllUserDataAsync(CancellationToken ct = default)
    {
        InternetAccessManager.ThrowIfInternetAccessDisabled();
        var identity = GetIdentityComponents();

        var slots = await ListSlotsAsync(identity, ct).ConfigureAwait(false);
        foreach (var slot in slots) await DeleteAsync(slot, ct).ConfigureAwait(false);

        await DeleteOwnershipAsync(identity, ct).ConfigureAwait(false);
    }


    public async Task<IReadOnlyList<CloudModlistRegistryEntry>> GetRegistryEntriesAsync(CancellationToken ct = default)
    {
        InternetAccessManager.ThrowIfInternetAccessDisabled();

        var summaries = await TryFetchRegistryEntriesAsync("registrySummaries", false, ct).ConfigureAwait(false);
        if (summaries.Count > 0) return summaries;

        // Fall back to the full registry for older databases that don't have summaries yet
        return await TryFetchRegistryEntriesAsync("registry", true, ct).ConfigureAwait(false);
    }

    public async Task<CloudModlistRegistryEntry?> GetRegistryEntryAsync(string registryId, CancellationToken ct = default)
    {
        InternetAccessManager.ThrowIfInternetAccessDisabled();

        if (string.IsNullOrWhiteSpace(registryId))
            throw new ArgumentException("Registry ID cannot be null or whitespace.", nameof(registryId));

        var cachePath = GetRegistryEntryCachePath(registryId);
        var cachedEntry = await TryReadRegistryEntryCacheAsync(cachePath, ct).ConfigureAwait(false);

        var sendResult = await SendWithAuthRetryAsync(session =>
        {
            var registryUrl = BuildAuthenticatedUrl(session.IdToken, null, "registry", registryId);
            var request = new HttpRequestMessage(HttpMethod.Get, registryUrl);
            request.Headers.TryAddWithoutValidation("X-Firebase-ETag", "true");
            if (!string.IsNullOrWhiteSpace(cachedEntry?.ETag))
                request.Headers.TryAddWithoutValidation("If-None-Match", cachedEntry.ETag);

            return HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }, ct).ConfigureAwait(false);

        using var response = sendResult.Response;

        var responseEtag = response.Headers.ETag?.Tag;

        if (response.StatusCode == HttpStatusCode.NotModified && cachedEntry?.Entry is not null)
            return cachedEntry.Entry;

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            await DeleteRegistryEntryCacheAsync(cachePath).ConfigureAwait(false);
            return null;
        }

        await EnsureOk(response, "Fetch registry entry").ConfigureAwait(false);

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var entry = ParseRegistryEntry(registryId, json);
        if (entry is null) return null;

        await WriteRegistryEntryCacheAsync(cachePath, entry, responseEtag, ct).ConfigureAwait(false);
        return entry;
    }


    /// <summary>Return the first available slot key (slot1..slot5), or null if full.</summary>
    public async Task<string?> GetFirstFreeSlotAsync(CancellationToken ct = default)
    {
        InternetAccessManager.ThrowIfInternetAccessDisabled();
        var existing = await ListSlotsAsync(ct);
        foreach (var slot in KnownSlots)
            if (!existing.Contains(slot))
                return slot;

        return null;
    }

    private async Task<IReadOnlyList<string>> ListSlotsAsync(PlayerIdentity identity, CancellationToken ct)
    {
        await EnsureOwnershipAsync(identity, ct).ConfigureAwait(false);

        var sendResult = await SendWithAuthRetryAsync(session =>
        {
            var url = BuildAuthenticatedUrl(session.IdToken, "shallow=true", "users", identity.SanitizedUid);
            return HttpClient.GetAsync(url, ct);
        }, ct).ConfigureAwait(false);

        using var response = sendResult.Response;

        if (response.StatusCode == HttpStatusCode.NotFound) return Array.Empty<string>();

        await EnsureOk(response, "List").ConfigureAwait(false);

        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text) || text == "null") return Array.Empty<string>();

        var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(text, JsonOpts);
        if (dict is null) return Array.Empty<string>();

        var list = new List<string>(KnownSlots.Length);
        foreach (var slot in KnownSlots)
            if (dict.ContainsKey(slot))
                list.Add(slot);

        return list;
    }

    private async Task<List<CloudModlistRegistryEntry>> TryFetchRegistryEntriesAsync(
        string path,
        bool isContentComplete,
        CancellationToken ct)
    {
        await _registryCacheLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cachePath = GetRegistryCachePath(path);
            var cached = await TryReadRegistryCacheAsync(cachePath, ct).ConfigureAwait(false);

            var sendResult = await SendWithAuthRetryAsync(session =>
            {
                var registryUrl = BuildAuthenticatedUrl(session.IdToken, null, path);
                var request = new HttpRequestMessage(HttpMethod.Get, registryUrl);
                request.Headers.TryAddWithoutValidation("X-Firebase-ETag", "true");
                if (!string.IsNullOrWhiteSpace(cached?.ETag))
                    request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);

                return HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }, ct).ConfigureAwait(false);

            using var response = sendResult.Response;

            var responseEtag = response.Headers.ETag?.Tag;

            if (response.StatusCode == HttpStatusCode.NotModified && cached?.Entries is { })
            {
                var cachedEntries = cached.Entries.ToList();
                _registryCache[cachePath] = new RegistryCache(cachedEntries, cached.ETag, isContentComplete);
                return cachedEntries;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                await WriteRegistryCacheAsync(cachePath, Array.Empty<CloudModlistRegistryEntry>(), responseEtag,
                        isContentComplete, ct)
                    .ConfigureAwait(false);
                _registryCache[cachePath] = new RegistryCache(new List<CloudModlistRegistryEntry>(), responseEtag,
                    isContentComplete);
                return new List<CloudModlistRegistryEntry>();
            }

            await EnsureOk(response, $"Fetch {path}").ConfigureAwait(false);

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                await WriteRegistryCacheAsync(cachePath, Array.Empty<CloudModlistRegistryEntry>(), responseEtag,
                        isContentComplete, ct)
                    .ConfigureAwait(false);
                return new List<CloudModlistRegistryEntry>();
            }

            var results = ParseRegistryEntries(document, isContentComplete);
            await WriteRegistryCacheAsync(cachePath, results, responseEtag, isContentComplete, ct)
                .ConfigureAwait(false);
            _registryCache[cachePath] = new RegistryCache(results, responseEtag, isContentComplete);
            return results;
        }
        finally
        {
            _registryCacheLock.Release();
        }
    }

    private static CloudModlistSummary BuildSummary(string contentJson)
    {
        using var document = JsonDocument.Parse(contentJson);
        return CloudModlistSummary.FromJsonElement(document.RootElement);
    }

    private static string BuildSummaryJson(string contentJson, string dateAddedIso)
    {
        var summary = BuildSummary(contentJson);
        var payload = summary.ToFirebasePayload(dateAddedIso);
        return JsonSerializer.Serialize(payload, JsonOpts);
    }

    private static string GetRegistryCachePath(string path)
    {
        var directory = DevConfig.FirebaseBackupDirectory;
        Directory.CreateDirectory(directory);
        var safePath = path.Replace('/', '_');
        return Path.Combine(directory, $"registry-{safePath}-cache.json");
    }

    private static string GetRegistryEntryCachePath(string registryId)
    {
        var directory = Path.Combine(DevConfig.FirebaseBackupDirectory, "registry-entries");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{registryId}.json");
    }

    private static List<CloudModlistRegistryEntry> ParseRegistryEntries(JsonDocument document, bool isContentComplete)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object) return new List<CloudModlistRegistryEntry>();

        var results = new List<CloudModlistRegistryEntry>();

        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
                continue;

            if (!entry.Value.TryGetProperty("content", out var contentElement))
                continue;

            if (contentElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;

            var json = contentElement.GetRawText();

            DateTimeOffset? dateAdded = null;
            if (entry.Value.TryGetProperty("dateAdded", out var dateElement)
                && dateElement.ValueKind == JsonValueKind.String)
            {
                var dateValue = dateElement.GetString();
                if (!string.IsNullOrWhiteSpace(dateValue)
                    && DateTimeOffset.TryParse(dateValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                        out var parsed))
                    dateAdded = parsed;
            }

            results.Add(new CloudModlistRegistryEntry(entry.Name, "public", json, dateAdded, isContentComplete));
        }

        return results;
    }

    private async Task<RegistryCache?> TryReadRegistryCacheAsync(string cachePath, CancellationToken ct)
    {
        if (_registryCache.TryGetValue(cachePath, out var cached)) return cached;

        try
        {
            if (!File.Exists(cachePath)) return null;

            await using var stream = File.OpenRead(cachePath);
            var cache = await JsonSerializer
                .DeserializeAsync<RegistryCachePayload>(stream, cancellationToken: ct)
                .ConfigureAwait(false);

            if (cache?.Entries is null) return null;

            var entries = cache.Entries
                .Select(e => new CloudModlistRegistryEntry(
                    e.OwnerId,
                    e.SlotKey,
                    e.ContentJson,
                    e.DateAdded,
                    cache.IsContentComplete))
                .ToList();

            var registryCache = new RegistryCache(entries, cache.ETag, cache.IsContentComplete);
            _registryCache[cachePath] = registryCache;
            return registryCache;
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteRegistryCacheAsync(
        string cachePath,
        IReadOnlyCollection<CloudModlistRegistryEntry> entries,
        string? eTag,
        bool isContentComplete,
        CancellationToken ct)
    {
        var payload = new RegistryCachePayload
        {
            ETag = eTag,
            IsContentComplete = isContentComplete,
            Entries = entries
                .Select(entry => new RegistryCacheEntry(
                    entry.OwnerId,
                    entry.SlotKey,
                    entry.ContentJson,
                    entry.DateAdded))
                .ToList()
        };

        var directory = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        await using var stream = File.Create(cachePath);
        await JsonSerializer.SerializeAsync(stream, payload, cancellationToken: ct).ConfigureAwait(false);
    }

    private static CloudModlistRegistryEntry? ParseRegistryEntry(string registryId, string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null") return null;

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return null;

        if (!document.RootElement.TryGetProperty("content", out var contentElement))
            return null;

        if (contentElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        DateTimeOffset? dateAdded = null;
        if (document.RootElement.TryGetProperty("dateAdded", out var dateElement)
            && dateElement.ValueKind == JsonValueKind.String)
        {
            var dateValue = dateElement.GetString();
            if (!string.IsNullOrWhiteSpace(dateValue)
                && DateTimeOffset.TryParse(dateValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                    out var parsed))
                dateAdded = parsed;
        }

        var contentJson = contentElement.GetRawText();
        return new CloudModlistRegistryEntry(registryId, "public", contentJson, dateAdded);
    }

    private static async Task<RegistryEntryCache?> TryReadRegistryEntryCacheAsync(string cachePath, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(cachePath)) return null;

            await using var stream = File.OpenRead(cachePath);
            return await JsonSerializer
                .DeserializeAsync<RegistryEntryCache>(stream, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteRegistryEntryCacheAsync(
        string cachePath,
        CloudModlistRegistryEntry entry,
        string? eTag,
        CancellationToken ct)
    {
        var payload = new RegistryEntryCache(entry, eTag);

        var directory = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        await using var stream = File.Create(cachePath);
        await JsonSerializer.SerializeAsync(stream, payload, cancellationToken: ct).ConfigureAwait(false);
    }

    private static async Task DeleteRegistryEntryCacheAsync(string cachePath)
    {
        try
        {
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
        catch
        {
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private PlayerIdentity GetIdentityComponents()
    {
        var uid = _playerUid;
        var sanitizedUid = _sanitizedPlayerUid;
        var name = _playerName;

        if (string.IsNullOrWhiteSpace(uid))
            throw new InvalidOperationException(
                "The Vintage Story clientsettings.json file does not contain a playeruid value. Start the game once to generate it.");

        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException(
                "The Vintage Story clientsettings.json file does not contain a playername value. Set a player name in the game before using cloud modlists.");

        uid = uid.Trim();
        name = name.Trim();

        // Sanitized UID should already be set by SetPlayerIdentity, but create it here as a fallback
        sanitizedUid = string.IsNullOrWhiteSpace(sanitizedUid)
            ? SanitizePlayerUidForFirebase(uid)
            : sanitizedUid;

        return new PlayerIdentity(uid, sanitizedUid, name);
    }

    private static async Task EnsureOk(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode) return;

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var message =
                $"{operation} failed: access was denied by the Firebase security rules. Ensure anonymous authentication is enabled and that the configured Firebase API key has access to the database.";
            LogRestFailure(message);
            throw new InvalidOperationException(message);
        }

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        LogRestFailure(
            $"{operation} failed: {(int)response.StatusCode} {response.ReasonPhrase} | {Truncate(body, 400)}");
        throw new InvalidOperationException(
            $"{operation} failed: {(int)response.StatusCode} {response.ReasonPhrase} | {Truncate(body, 400)}");
    }

    private static void LogRestFailure(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        StatusLogService.AppendStatus(message, true);
    }

    private async Task<(HttpResponseMessage Response, FirebaseAnonymousAuthenticator.FirebaseAuthSession Session)>
        SendWithAuthRetryAsync(
            Func<FirebaseAnonymousAuthenticator.FirebaseAuthSession, Task<HttpResponseMessage>> operation,
            CancellationToken ct)
    {
        var hasRetried = false;

        while (true)
        {
            InternetAccessManager.ThrowIfInternetAccessDisabled();
            var session = await Authenticator.GetSessionAsync(ct).ConfigureAwait(false);
            var response = await operation(session).ConfigureAwait(false);

            if (IsAuthError(response.StatusCode) && !hasRetried)
            {
                hasRetried = true;
                response.Dispose();
                await Authenticator.MarkTokenAsExpiredAsync(ct).ConfigureAwait(false);
                continue;
            }

            return (response, session);
        }
    }

    private async Task EnsureOwnershipAsync(PlayerIdentity identity, CancellationToken ct)
    {
        InternetAccessManager.ThrowIfInternetAccessDisabled();
        if (string.Equals(_ownershipClaimedForUid, identity.SanitizedUid, StringComparison.Ordinal)) return;

        await _ownershipClaimLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (string.Equals(_ownershipClaimedForUid, identity.SanitizedUid, StringComparison.Ordinal)) return;

            var readResult = await SendWithAuthRetryAsync(session =>
            {
                var ownersUrl = BuildAuthenticatedUrl(session.IdToken, null, "owners", identity.SanitizedUid);
                return HttpClient.GetAsync(ownersUrl, ct);
            }, ct).ConfigureAwait(false);

            using (readResult.Response)
            {
                if (readResult.Response.IsSuccessStatusCode)
                {
                    var body = await readResult.Response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var ownerId = ParseOwnerIdentifier(body);

                    if (string.IsNullOrWhiteSpace(ownerId))
                    {
                        // Not yet claimed. Fall through to claim request.
                    }
                    else if (string.Equals(ownerId, readResult.Session.UserId, StringComparison.Ordinal))
                    {
                        _ownershipClaimedForUid = identity.SanitizedUid;
                        return;
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "Cloud modlists for this Vintage Story UID are already bound to another Firebase anonymous account.");
                    }
                }
                else if (!IsAuthError(readResult.Response.StatusCode) &&
                         readResult.Response.StatusCode != HttpStatusCode.NotFound)
                {
                    await EnsureOk(readResult.Response, "Check ownership").ConfigureAwait(false);
                }
            }

            var claimResult = await SendWithAuthRetryAsync(session =>
            {
                var ownersUrl = BuildAuthenticatedUrl(session.IdToken, null, "owners", identity.SanitizedUid);
                var payload = JsonSerializer.Serialize(session.UserId);
                var request = new HttpRequestMessage(HttpMethod.Put, ownersUrl)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                return HttpClient.SendAsync(request, ct);
            }, ct).ConfigureAwait(false);

            using (claimResult.Response)
            {
                if (claimResult.Response.IsSuccessStatusCode)
                {
                    _ownershipClaimedForUid = identity.SanitizedUid;
                    return;
                }

                if (IsAuthError(claimResult.Response.StatusCode))
                    throw new InvalidOperationException(
                        "Cloud modlists for this Vintage Story UID are already bound to another Firebase anonymous account.");

                await EnsureOk(claimResult.Response, "Claim ownership").ConfigureAwait(false);
            }
        }
        finally
        {
            _ownershipClaimLock.Release();
        }
    }

    private async Task DeleteOwnershipAsync(PlayerIdentity identity, CancellationToken ct)
    {
        var deleteResult = await SendWithAuthRetryAsync(session =>
        {
            var ownersUrl = BuildAuthenticatedUrl(session.IdToken, null, "owners", identity.SanitizedUid);
            return HttpClient.DeleteAsync(ownersUrl, ct);
        }, ct).ConfigureAwait(false);

        using (deleteResult.Response)
        {
            if (!deleteResult.Response.IsSuccessStatusCode &&
                deleteResult.Response.StatusCode != HttpStatusCode.NotFound)
                await EnsureOk(deleteResult.Response, "Delete ownership").ConfigureAwait(false);
        }

        if (string.Equals(_ownershipClaimedForUid, identity.SanitizedUid, StringComparison.Ordinal))
            _ownershipClaimedForUid = null;
    }

    private static string? ParseOwnerIdentifier(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || string.Equals(json.Trim(), "null", StringComparison.Ordinal))
            return null;

        try
        {
            var owner = JsonSerializer.Deserialize<string>(json, JsonOpts);
            return string.IsNullOrWhiteSpace(owner) ? null : owner.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string BuildAuthenticatedUrl(string authToken, string? query, params string[] segments)
    {
        var builder = new StringBuilder(_dbUrl.Length + 64);
        builder.Append(_dbUrl);
        builder.Append('/');

        for (var i = 0; i < segments.Length; i++)
        {
            if (i > 0) builder.Append('/');

            builder.Append(Uri.EscapeDataString(segments[i]));
        }

        builder.Append(".json?auth=");
        builder.Append(Uri.EscapeDataString(authToken));

        if (!string.IsNullOrWhiteSpace(query))
        {
            builder.Append('&');
            builder.Append(query);
        }

        return builder.ToString();
    }

    private static bool IsAuthError(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
    }

    private static string ReplaceUploader(JsonElement root, PlayerIdentity identity)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            var hasUploader = false;
            var hasUploaderName = false;
            var hasUploaderId = false;

            foreach (var property in root.EnumerateObject())
                if (property.NameEquals("uploader"))
                {
                    writer.WriteString("uploader", identity.Name);
                    hasUploader = true;
                }
                else if (property.NameEquals("uploaderName"))
                {
                    writer.WriteString("uploaderName", identity.Name);
                    hasUploaderName = true;
                }
                else if (property.NameEquals("uploaderId"))
                {
                    // Use the sanitized UID for uploaderId to match the /owners/{sanitizedUid} path
                    // The Firebase security rules validate uploaderId against the owners path
                    writer.WriteString("uploaderId", identity.SanitizedUid);
                    hasUploaderId = true;
                }
                else
                {
                    property.WriteTo(writer);
                }

            if (!hasUploader) writer.WriteString("uploader", identity.Name);

            if (!hasUploaderName) writer.WriteString("uploaderName", identity.Name);

            if (!hasUploaderId)
                // Use the sanitized UID for uploaderId to match the /owners/{sanitizedUid} path
                // The Firebase security rules validate uploaderId against the owners path
                writer.WriteString("uploaderId", identity.SanitizedUid);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string BuildNodeJson(string contentJson)
    {
        using var doc = JsonDocument.Parse(contentJson);
        var node = new ModlistNode { Content = doc.RootElement.Clone() };
        return JsonSerializer.Serialize(node, JsonOpts);
    }

    private static void ValidateSlotKey(string slotKey)
    {
        if (Array.IndexOf(KnownSlots, slotKey) < 0)
            throw new ArgumentException("Slot must be one of: slot1, slot2, slot3, slot4, slot5.", nameof(slotKey));
    }

    private sealed record RegistryCache(
        IReadOnlyList<CloudModlistRegistryEntry> Entries,
        string? ETag,
        bool IsContentComplete);

    private sealed class RegistryCachePayload
    {
        public string? ETag { get; set; }

        public bool IsContentComplete { get; set; }

        public List<RegistryCacheEntry>? Entries { get; set; }
    }

    private sealed record RegistryCacheEntry(
        string OwnerId,
        string SlotKey,
        string ContentJson,
        DateTimeOffset? DateAdded);

    private sealed record RegistryEntryCache(CloudModlistRegistryEntry? Entry, string? ETag);

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    ///     Sanitizes a player UID to be compatible with Firebase Realtime Database path restrictions.
    ///     Firebase keys cannot contain: . $ # [ ] / and ASCII control characters (0-31 and 127).
    ///     We replace these with underscores to maintain readability while ensuring compatibility.
    /// </summary>
    private static string SanitizePlayerUidForFirebase(string playerUid)
    {
        if (string.IsNullOrWhiteSpace(playerUid))
            throw new ArgumentException("Player UID cannot be null or whitespace.", nameof(playerUid));

        var sb = new StringBuilder(playerUid.Length);
        foreach (var c in playerUid)
            // Replace Firebase-incompatible characters with underscore
            // Forbidden: . $ # [ ] / and control characters (0-31, 127)
            if (c == '.' || c == '$' || c == '#' || c == '[' || c == ']' || c == '/' || c < 32 || c == 127)
                sb.Append('_');
            else
                sb.Append(c);

        return sb.ToString();
    }

    private static string Truncate(string value, int max)
    {
        return value.Length <= max ? value : value.Substring(0, max) + "...";
    }

    private static bool IsKnownSlot(string slotKey)
    {
        return Array.IndexOf(KnownSlots, slotKey) >= 0;
    }

    // ---------- New slot node model (adds registryId) ----------
    private struct SlotNode
    {
        [JsonPropertyName("registryId")] public string? RegistryId { get; set; }

        [JsonPropertyName("content")] public JsonElement Content { get; set; }

        [JsonPropertyName("dateAdded")] public string? DateAdded { get; set; }
    }

    /// <summary>
    ///     Holds player identity information with both original and Firebase-sanitized UIDs.
    /// </summary>
    private readonly struct PlayerIdentity
    {
        public PlayerIdentity(string originalUid, string sanitizedUid, string name)
        {
            OriginalUid = originalUid;
            SanitizedUid = sanitizedUid;
            Name = name;
        }

        /// <summary>The original player UID from Vintage Story (may contain Firebase-incompatible characters)</summary>
        public string OriginalUid { get; }

        /// <summary>The Firebase-compatible version of the player UID (used in Firebase paths)</summary>
        public string SanitizedUid { get; }

        /// <summary>The player name</summary>
        public string Name { get; }
    }

    private struct ModlistNode
    {
        [JsonPropertyName("content")] public JsonElement Content { get; set; }
    }

    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        // Dispose semaphores outside the lock to avoid potential deadlocks.
        // The _disposed flag (set atomically above) prevents new operations.
        _ownershipClaimLock.Dispose();
        _registryCacheLock.Dispose();
    }
}