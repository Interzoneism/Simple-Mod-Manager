using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VintageStoryModManager.Models;
using VintageStoryModManager.Services;

namespace SimpleVsManager.Cloud
{
    /// <summary>
    /// Firebase RTDB access that relies solely on the player's Vintage Story identity.
    /// </summary>
    public sealed class FirebaseModlistStore
    {
        private static readonly HttpClient HttpClient = new();

        private readonly string _dbUrl;
        private readonly FirebaseAnonymousAuthenticator _authenticator;
        private readonly SemaphoreSlim _ownershipClaimLock = new(1, 1);

        private string? _playerUid;
        private string? _playerName;
        private string? _ownershipClaimedForUid;

        private const string DefaultDbUrl =
            "https://simple-vs-manager-default-rtdb.europe-west1.firebasedatabase.app";

        public FirebaseModlistStore()
            : this(DefaultDbUrl, new FirebaseAnonymousAuthenticator()) { }

        public FirebaseModlistStore(string dbUrl, FirebaseAnonymousAuthenticator authenticator)
        {
            _dbUrl = (dbUrl ?? throw new ArgumentNullException(nameof(dbUrl))).TrimEnd('/');
            _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        }


        internal FirebaseAnonymousAuthenticator Authenticator => _authenticator;

        // ---------- New slot node model (adds registryId) ----------
        private struct SlotNode
        {
            [JsonPropertyName("registryId")]
            public string? RegistryId { get; set; }

            [JsonPropertyName("content")]
            public JsonElement Content { get; set; }

            [JsonPropertyName("dateAdded")]
            public string? DateAdded { get; set; }
        }

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
                string url = BuildAuthenticatedUrl(session.IdToken, null, "users", uid, slotKey);
                return HttpClient.GetAsync(url, ct);
            }, ct).ConfigureAwait(false);

            using HttpResponseMessage response = sendResult.Response;

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            await EnsureOk(response, "Read slot").ConfigureAwait(false);

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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

        private static string GenerateEntryId() => Guid.NewGuid().ToString("n");

        // Ensure uploader fields are present and correct
        // (kept from your code; unchanged signature)


        /// <summary>
        /// Gets the known slot keys used for storing modlists in Firebase.
        /// </summary>
        public static IReadOnlyList<string> SlotKeys => KnownSlots;

        /// <summary>
        /// Gets the identifier used for Firebase storage operations (player UID).
        /// </summary>
        public string? CurrentUserId => string.IsNullOrWhiteSpace(_playerUid) ? null : _playerUid;

        /// <summary>
        /// Applies the current player identity sourced from clientsettings.json.
        /// </summary>
        public void SetPlayerIdentity(string? playerUid, string? playerName)
        {
            _playerUid = Normalize(playerUid);
            _playerName = Normalize(playerName);

            if (!string.Equals(_ownershipClaimedForUid, _playerUid, StringComparison.Ordinal))
            {
                _ownershipClaimedForUid = null;
            }
        }

        /// <summary>Save or replace the JSON in the given slot (e.g., "slot1").</summary>
        public async Task SaveAsync(string slotKey, string modlistJson, CancellationToken ct = default)
        {
            InternetAccessManager.ThrowIfInternetAccessDisabled();
            ValidateSlotKey(slotKey);
            var identity = GetIdentityComponents();
            await EnsureOwnershipAsync(identity, ct).ConfigureAwait(false);

            using var document = JsonDocument.Parse(modlistJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("The modlist JSON must be an object.");

            // Normalize uploader fields inside the content
            string normalizedContent = ReplaceUploader(document.RootElement, identity);

            // 1) Read existing slot to see if it already has a registryId
            SlotNode? existing = await TryReadSlotNodeAsync(identity.Uid, slotKey, ct).ConfigureAwait(false);
            string registryId = existing.HasValue && !string.IsNullOrWhiteSpace(existing.Value.RegistryId)
                ? existing.Value.RegistryId!
                : GenerateEntryId();

            string dateAddedIso = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            // Build the user slot payload including registryId
            string userSlotJson = BuildSlotNodeJson(normalizedContent, registryId, dateAddedIso);

            // 2) Atomic multi-location PATCH:
            // - write /users/{uid}/{slot} (content + registryId)
            // - write /registryOwners/{registryId} = auth.uid (safe even if same value)
            // - write /registry/{registryId} (public content mirror)
            var saveResult = await SendWithAuthRetryAsync(session =>
            {
                string rootUrl = BuildAuthenticatedUrl(session.IdToken, null /* root */);

                // public registry stores only the content object, not registryId
                string registryNodeJson =
                    $"{{\"content\":{normalizedContent},\"dateAdded\":{JsonSerializer.Serialize(dateAddedIso)}}}";
                string registryOwnerJson = JsonSerializer.Serialize(session.UserId);

                string patchJson =
                    $"{{" +
                        $"\"/users/{identity.Uid}/{slotKey}\":{userSlotJson}," +
                        $"\"/registryOwners/{registryId}\":{registryOwnerJson}," +
                        $"\"/registry/{registryId}\":{registryNodeJson}" +
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
        }



        /// <summary>Load a JSON string from the slot. Returns null if missing.</summary>
        public async Task<string?> LoadAsync(string slotKey, CancellationToken ct = default)
        {
            InternetAccessManager.ThrowIfInternetAccessDisabled();
            ValidateSlotKey(slotKey);
            var identity = GetIdentityComponents();
            await EnsureOwnershipAsync(identity, ct).ConfigureAwait(false);

            var sendResult = await SendWithAuthRetryAsync(session =>
                {
                    string userUrl = BuildAuthenticatedUrl(session.IdToken, null, "users", identity.Uid, slotKey);
                    return HttpClient.GetAsync(userUrl, ct);
                }, ct).ConfigureAwait(false);

            using HttpResponseMessage response = sendResult.Response;

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            await EnsureOk(response, "Load").ConfigureAwait(false);

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return null;
            }

            ModlistNode? node = JsonSerializer.Deserialize<ModlistNode?>(bytes, JsonOpts);
            if (node is null || node.Value.Content.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return null;
            }

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
            SlotNode? node = await TryReadSlotNodeAsync(identity.Uid, slotKey, ct).ConfigureAwait(false);
            string? registryId = node?.RegistryId;

            var result = await SendWithAuthRetryAsync(session =>
            {
                string rootUrl = BuildAuthenticatedUrl(session.IdToken, null /* root */);

                var sb = new StringBuilder();
                sb.Append('{');
                sb.Append($"\"/users/{identity.Uid}/{slotKey}\":null");

                if (!string.IsNullOrWhiteSpace(registryId))
                {
                    sb.Append($",\"/registry/{registryId}\":null");
                    sb.Append($",\"/registryOwners/{registryId}\":null");
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

            IReadOnlyList<string> slots = await ListSlotsAsync(identity, ct).ConfigureAwait(false);
            foreach (string slot in slots)
            {
                await DeleteAsync(slot, ct).ConfigureAwait(false);
            }

            await DeleteOwnershipAsync(identity, ct).ConfigureAwait(false);
        }



        public async Task<IReadOnlyList<CloudModlistRegistryEntry>> GetRegistryEntriesAsync(CancellationToken ct = default)
        {
            InternetAccessManager.ThrowIfInternetAccessDisabled();
            var sendResult = await SendWithAuthRetryAsync(session =>
            {
                string registryUrl = BuildAuthenticatedUrl(session.IdToken, null, "registry");
                return HttpClient.GetAsync(registryUrl, ct);
            }, ct).ConfigureAwait(false);

            using HttpResponseMessage response = sendResult.Response;

            if (response.StatusCode == HttpStatusCode.NotFound)
                return Array.Empty<CloudModlistRegistryEntry>();

            await EnsureOk(response, "Fetch registry").ConfigureAwait(false);

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Array.Empty<CloudModlistRegistryEntry>();

            var results = new List<CloudModlistRegistryEntry>();

            // Public registry is now { entryId: { content: {...}, dateAdded: "..." }, ... }
            foreach (JsonProperty entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object)
                    continue;

                if (!entry.Value.TryGetProperty("content", out JsonElement contentElement))
                    continue;

                if (contentElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    continue;

                string json = contentElement.GetRawText();

                DateTimeOffset? dateAdded = null;
                if (entry.Value.TryGetProperty("dateAdded", out JsonElement dateElement)
                    && dateElement.ValueKind == JsonValueKind.String)
                {
                    string? dateValue = dateElement.GetString();
                    if (!string.IsNullOrWhiteSpace(dateValue)
                        && DateTimeOffset.TryParse(dateValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
                    {
                        dateAdded = parsed;
                    }
                }

                // NOTE: we no longer have (ownerId, slotKey) here. If your UI expects those,
                // you can pass entryId as "ownerId" and use "public" as a placeholder slotKey.
                results.Add(new CloudModlistRegistryEntry(entry.Name, "public", json, dateAdded));
            }

            return results;
        }


        /// <summary>Return the first available slot key (slot1..slot5), or null if full.</summary>
        public async Task<string?> GetFirstFreeSlotAsync(CancellationToken ct = default)
        {
            InternetAccessManager.ThrowIfInternetAccessDisabled();
            IReadOnlyList<string> existing = await ListSlotsAsync(ct);
            foreach (string slot in KnownSlots)
            {
                if (!existing.Contains(slot))
                {
                    return slot;
                }
            }

            return null;
        }
        private async Task<IReadOnlyList<string>> ListSlotsAsync((string Uid, string Name) identity, CancellationToken ct)
        {
            await EnsureOwnershipAsync(identity, ct).ConfigureAwait(false);

            var sendResult = await SendWithAuthRetryAsync(session =>
                {
                    string url = BuildAuthenticatedUrl(session.IdToken, "shallow=true", "users", identity.Uid);
                    return HttpClient.GetAsync(url, ct);
                }, ct).ConfigureAwait(false);

            using HttpResponseMessage response = sendResult.Response;

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Array.Empty<string>();
            }

            await EnsureOk(response, "List").ConfigureAwait(false);

            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text) || text == "null")
            {
                return Array.Empty<string>();
            }

            var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(text, JsonOpts);
            if (dict is null)
            {
                return Array.Empty<string>();
            }

            var list = new List<string>(KnownSlots.Length);
            foreach (string slot in KnownSlots)
            {
                if (dict.ContainsKey(slot))
                {
                    list.Add(slot);
                }
            }

            return list;
        }

        private (string Uid, string Name) GetIdentityComponents()
        {
            string? uid = _playerUid;
            string? name = _playerName;

            if (string.IsNullOrWhiteSpace(uid))
            {
                throw new InvalidOperationException("The Vintage Story clientsettings.json file does not contain a playeruid value. Start the game once to generate it.");
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("The Vintage Story clientsettings.json file does not contain a playername value. Set a player name in the game before using cloud modlists.");
            }

            uid = uid.Trim();
            name = name.Trim();

            return (uid, name);
        }

        private static async Task EnsureOk(HttpResponseMessage response, string operation)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                string message = $"{operation} failed: access was denied by the Firebase security rules. Ensure anonymous authentication is enabled and that the configured Firebase API key has access to the database.";
                LogRestFailure(message);
                throw new InvalidOperationException(message);
            }

            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            LogRestFailure($"{operation} failed: {(int)response.StatusCode} {response.ReasonPhrase} | {Truncate(body, 400)}");
            throw new InvalidOperationException($"{operation} failed: {(int)response.StatusCode} {response.ReasonPhrase} | {Truncate(body, 400)}");
        }

        private static void LogRestFailure(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            StatusLogService.AppendStatus(message, true);
        }

        private async Task<(HttpResponseMessage Response, FirebaseAnonymousAuthenticator.FirebaseAuthSession Session)> SendWithAuthRetryAsync(Func<FirebaseAnonymousAuthenticator.FirebaseAuthSession, Task<HttpResponseMessage>> operation, CancellationToken ct)
        {
            bool hasRetried = false;

            while (true)
            {
                InternetAccessManager.ThrowIfInternetAccessDisabled();
                FirebaseAnonymousAuthenticator.FirebaseAuthSession session = await _authenticator.GetSessionAsync(ct).ConfigureAwait(false);
                HttpResponseMessage response = await operation(session).ConfigureAwait(false);

                if (IsAuthError(response.StatusCode) && !hasRetried)
                {
                    hasRetried = true;
                    response.Dispose();
                    await _authenticator.MarkTokenAsExpiredAsync(ct).ConfigureAwait(false);
                    continue;
                }

                return (response, session);
            }
        }

        private async Task EnsureOwnershipAsync((string Uid, string Name) identity, CancellationToken ct)
        {
            InternetAccessManager.ThrowIfInternetAccessDisabled();
            if (string.Equals(_ownershipClaimedForUid, identity.Uid, StringComparison.Ordinal))
            {
                return;
            }

            await _ownershipClaimLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (string.Equals(_ownershipClaimedForUid, identity.Uid, StringComparison.Ordinal))
                {
                    return;
                }

                var readResult = await SendWithAuthRetryAsync(session =>
                    {
                        string ownersUrl = BuildAuthenticatedUrl(session.IdToken, null, "owners", identity.Uid);
                        return HttpClient.GetAsync(ownersUrl, ct);
                    }, ct).ConfigureAwait(false);

                using (readResult.Response)
                {
                    if (readResult.Response.IsSuccessStatusCode)
                    {
                        string body = await readResult.Response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        string? ownerId = ParseOwnerIdentifier(body);

                        if (string.IsNullOrWhiteSpace(ownerId))
                        {
                            // Not yet claimed. Fall through to claim request.
                        }
                        else if (string.Equals(ownerId, readResult.Session.UserId, StringComparison.Ordinal))
                        {
                            _ownershipClaimedForUid = identity.Uid;
                            return;
                        }
                        else
                        {
                            throw new InvalidOperationException("Cloud modlists for this Vintage Story UID are already bound to another Firebase anonymous account.");
                        }
                    }
                    else if (!IsAuthError(readResult.Response.StatusCode) && readResult.Response.StatusCode != HttpStatusCode.NotFound)
                    {
                        await EnsureOk(readResult.Response, "Check ownership").ConfigureAwait(false);
                    }
                }

                var claimResult = await SendWithAuthRetryAsync(session =>
                    {
                        string ownersUrl = BuildAuthenticatedUrl(session.IdToken, null, "owners", identity.Uid);
                        string payload = JsonSerializer.Serialize(session.UserId);
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
                        _ownershipClaimedForUid = identity.Uid;
                        return;
                    }

                    if (IsAuthError(claimResult.Response.StatusCode))
                    {
                        throw new InvalidOperationException("Cloud modlists for this Vintage Story UID are already bound to another Firebase anonymous account.");
                    }

                    await EnsureOk(claimResult.Response, "Claim ownership").ConfigureAwait(false);
                }
            }
            finally
            {
                _ownershipClaimLock.Release();
            }
        }

        private async Task DeleteOwnershipAsync((string Uid, string Name) identity, CancellationToken ct)
        {
            var deleteResult = await SendWithAuthRetryAsync(session =>
            {
                string ownersUrl = BuildAuthenticatedUrl(session.IdToken, null, "owners", identity.Uid);
                return HttpClient.DeleteAsync(ownersUrl, ct);
            }, ct).ConfigureAwait(false);

            using (deleteResult.Response)
            {
                if (!deleteResult.Response.IsSuccessStatusCode && deleteResult.Response.StatusCode != HttpStatusCode.NotFound)
                {
                    await EnsureOk(deleteResult.Response, "Delete ownership").ConfigureAwait(false);
                }
            }

            if (string.Equals(_ownershipClaimedForUid, identity.Uid, StringComparison.Ordinal))
            {
                _ownershipClaimedForUid = null;
            }
        }

        private static string? ParseOwnerIdentifier(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || string.Equals(json.Trim(), "null", StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                string? owner = JsonSerializer.Deserialize<string>(json, JsonOpts);
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

            for (int i = 0; i < segments.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append('/');
                }

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
            => statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

        private static string ReplaceUploader(JsonElement root, (string Uid, string Name) identity)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                bool hasUploader = false;
                bool hasUploaderName = false;
                bool hasUploaderId = false;

                foreach (JsonProperty property in root.EnumerateObject())
                {
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
                        writer.WriteString("uploaderId", identity.Uid);
                        hasUploaderId = true;
                    }
                    else
                    {
                        property.WriteTo(writer);
                    }
                }

                if (!hasUploader)
                {
                    writer.WriteString("uploader", identity.Name);
                }

                if (!hasUploaderName)
                {
                    writer.WriteString("uploaderName", identity.Name);
                }

                if (!hasUploaderId)
                {
                    writer.WriteString("uploaderId", identity.Uid);
                }

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
            {
                throw new ArgumentException("Slot must be one of: slot1, slot2, slot3, slot4, slot5.", nameof(slotKey));
            }
        }

        private static string? Normalize(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string Truncate(string value, int max)
            => value.Length <= max ? value : value.Substring(0, max) + "...";

        private static readonly string[] KnownSlots = { "slot1", "slot2", "slot3", "slot4", "slot5" };

        private static bool IsKnownSlot(string slotKey)
            => Array.IndexOf(KnownSlots, slotKey) >= 0;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private struct ModlistNode
        {
            [JsonPropertyName("content")]
            public JsonElement Content { get; set; }
        }
    }
}
