using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SimpleVsManager.Cloud;
using VintageStoryModManager.Models;

namespace VintageStoryModManager.Services;

internal sealed class FirebaseModlistMigrationService
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly JsonSerializerOptions _modlistJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public async Task<bool> TryMigrateAsync(
        string? playerUid,
        string? playerName,
        UserConfigurationService? userConfiguration,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(playerUid)) return false;

        var stateFilePath = FirebaseAnonymousAuthenticator.GetStateFilePath();
        if (string.IsNullOrWhiteSpace(stateFilePath) || !File.Exists(stateFilePath)) return false;

        var projectId = TryReadProjectId(stateFilePath);
        if (IsNewProject(projectId)) return false;
        if (!IsLegacyProject(projectId)) return false;

        StatusLogService.AppendStatus("Migrating cloud modlists to the new Firebase project...", false);

        var legacyAuthenticator = new FirebaseAnonymousAuthenticator(DevConfig.FirebaseLegacyApiKey);
        var legacyStore = new FirebaseModlistStore(DevConfig.FirebaseLegacyModlistDbUrl, legacyAuthenticator);
        legacyStore.SetPlayerIdentity(playerUid, playerName);

        var modlists = await DownloadLegacyModlistsAsync(legacyStore, ct).ConfigureAwait(false);
        var preparedModlists = modlists.Select(EncodeLegacyModlist).ToList();

        var legacyBackupPath = TryRotateExistingBackup(userConfiguration);
        var backupPath = TryBackupLegacyAuthFile(stateFilePath);

        var newStore = new FirebaseModlistStore();
        newStore.SetPlayerIdentity(playerUid, playerName);

        await newStore.Authenticator.GetSessionAsync(ct).ConfigureAwait(false);
        FirebaseAnonymousAuthenticator.EnsureStartupBackup(userConfiguration);

        foreach (var modlist in preparedModlists)
            await newStore.SaveAsync(modlist.SlotKey, modlist.ContentJson, ct).ConfigureAwait(false);

        StatusLogService.AppendStatus(
            "Cloud modlists migrated to the new Firebase project successfully." +
            BuildBackupMessageSuffix(legacyBackupPath ?? backupPath),
            false);

        return true;
    }

    private string BuildBackupMessageSuffix(string? backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath)) return string.Empty;

        return $" Legacy auth saved to {backupPath} for reference.";
    }

    private static bool IsNewProject(string? projectId)
    {
        return string.Equals(projectId, DevConfig.FirebaseNewProjectId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLegacyProject(string? projectId)
    {
        return string.Equals(projectId, DevConfig.FirebaseLegacyProjectId, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<LegacyModlist>> DownloadLegacyModlistsAsync(FirebaseModlistStore legacyStore,
        CancellationToken ct)
    {
        var result = new List<LegacyModlist>();
        var slots = await legacyStore.ListSlotsAsync(ct).ConfigureAwait(false);

        foreach (var slot in slots)
        {
            try
            {
                var content = await legacyStore.LoadAsync(slot, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(content)) continue;

                result.Add(new LegacyModlist(slot, content));
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException
                                           or TaskCanceledException)
            {
                StatusLogService.AppendStatus($"Failed to read legacy modlist for {slot}: {ex.Message}", true);
            }
        }

        return result;
    }

    private string? TryBackupLegacyAuthFile(string stateFilePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(stateFilePath);
            var fileName = Path.GetFileName(stateFilePath);

            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
                return null;

            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            var backupName = $"{fileName}.legacy.{timestamp}";
            var backupPath = Path.Combine(directory, backupName);

            File.Move(stateFilePath, backupPath, true);

            return backupPath;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (SecurityException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private string? TryRotateExistingBackup(UserConfigurationService? userConfiguration)
    {
        try
        {
            var backupPath = FirebaseAnonymousAuthenticator.GetBackupFilePath();
            if (string.IsNullOrWhiteSpace(backupPath)) return null;

            if (!File.Exists(backupPath))
            {
                ResetBackupFlag(userConfiguration);
                return null;
            }

            var directory = Path.GetDirectoryName(backupPath);
            var fileName = Path.GetFileName(backupPath);

            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
                return null;

            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            var legacyBackupPath = Path.Combine(directory, $"{fileName}.legacy.{timestamp}");

            File.Move(backupPath, legacyBackupPath, true);

            ResetBackupFlag(userConfiguration);

            return legacyBackupPath;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (SecurityException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static void ResetBackupFlag(UserConfigurationService? userConfiguration)
    {
        try
        {
            userConfiguration?.ResetFirebaseAuthBackupFlag();
        }
        catch
        {
        }
    }

    private string? TryReadProjectId(string stateFilePath)
    {
        try
        {
            var json = File.ReadAllText(stateFilePath);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var model = JsonSerializer.Deserialize<AuthStateModel>(json, _jsonOptions);
            return TryExtractProjectId(model?.IdToken);
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

    private static string? TryExtractProjectId(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return null;

        var parts = idToken.Split('.');
        if (parts.Length < 2) return null;

        try
        {
            var payload = parts[1];
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            payload = payload.Replace('-', '+').Replace('_', '/');

            var bytes = Convert.FromBase64String(payload);
            using var document = JsonDocument.Parse(bytes);

            if (document.RootElement.TryGetProperty("iss", out var issElement)
                && issElement.ValueKind == JsonValueKind.String)
            {
                var issuer = issElement.GetString();
                if (!string.IsNullOrWhiteSpace(issuer))
                {
                    var idx = issuer!.LastIndexOf("/", StringComparison.Ordinal);
                    return idx >= 0 && idx < issuer.Length - 1 ? issuer[(idx + 1)..] : issuer;
                }
            }

            if (document.RootElement.TryGetProperty("aud", out var audElement)
                && audElement.ValueKind == JsonValueKind.String)
                return audElement.GetString();
        }
        catch
        {
            return null;
        }

        return null;
    }

    private sealed record LegacyModlist(string SlotKey, string ContentJson);

    private LegacyModlist EncodeLegacyModlist(LegacyModlist legacyModlist)
    {
        var encoded = TryEncodeModlistContent(legacyModlist.ContentJson);
        return new LegacyModlist(legacyModlist.SlotKey, encoded ?? legacyModlist.ContentJson);
    }

    private string? TryEncodeModlistContent(string contentJson)
    {
        try
        {
            var preset = JsonSerializer.Deserialize<SerializablePreset>(contentJson, _modlistJsonOptions);
            if (preset is null) return null;

            EncodePresetConfigurationContent(preset);

            return JsonSerializer.Serialize(preset, _modlistJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void EncodePresetConfigurationContent(SerializablePreset preset)
    {
        if (preset.Mods is null) return;

        foreach (var mod in preset.Mods)
        {
            if (mod?.ConfigurationContent is null) continue;

            mod.ConfigurationContent = ModConfigurationEncoding.Encode(mod.ConfigurationContent);
        }
    }

    private sealed class AuthStateModel
    {
        public string? IdToken { get; set; }
    }
}
