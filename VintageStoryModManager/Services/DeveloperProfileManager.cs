using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VintageStoryModManager.Services;

/// <summary>
/// Provides helpers for switching between developer-only player identities without
/// touching the real user's data or Firebase authentication state.
/// </summary>
public static class DeveloperProfileManager
{
    /// <summary>
    /// Enables the developer identity switching helpers. Set to false before publishing.
    /// </summary>
    public static bool DevDebug { get; set; } = false;

    private static readonly DeveloperProfileDefinition[] FakeProfiles =
    {
        new("ember-rowan", "Fake: Ember Rowan", "EmberRowan", "c8f8d0a5-1f54-4c0b-93de-314ce521f9db"),
        new("juniper-vale", "Fake: Juniper Vale", "JuniperVale", "67f1f0be-18e6-4d1a-86ac-c04dfb75d991"),
        new("orion-ashwood", "Fake: Orion Ashwood", "OrionAshwood", "b74cb56b-1af4-4c2c-8a8e-bd13c9bc44b1"),
        new("mira-slate", "Fake: Mira Slate", "MiraSlate", "4e902c46-b6c7-4f6a-88a4-425a7980c6b3"),
        new("sylas-storm", "Fake: Sylas Storm", "SylasStorm", "df0c1975-3bc4-4f59-9d7f-f2c6c6df2056")
    };

    private static readonly object SyncRoot = new();

    private static List<DeveloperProfile> _profiles = new();
    private static DeveloperProfile? _originalProfile;
    private static DeveloperProfile? _currentProfile;
    private static string? _profilesRoot;

    public static event EventHandler<DeveloperProfileChangedEventArgs>? CurrentProfileChanged;

    public static DeveloperProfile? CurrentProfile
    {
        get
        {
            if (!DevDebug)
            {
                return null;
            }

            lock (SyncRoot)
            {
                return _currentProfile;
            }
        }
    }

    public static IReadOnlyList<DeveloperProfile> GetProfiles()
    {
        if (!DevDebug)
        {
            return Array.Empty<DeveloperProfile>();
        }

        lock (SyncRoot)
        {
            EnsureProfilesReadyLocked();
            return _profiles.ToArray();
        }
    }

    public static bool TrySetCurrentProfile(string profileId)
    {
        if (!DevDebug)
        {
            return false;
        }

        DeveloperProfile? target;
        bool changed = false;

        lock (SyncRoot)
        {
            EnsureProfilesReadyLocked();

            target = _profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                return false;
            }

            if (_currentProfile is null || !string.Equals(_currentProfile.Id, target.Id, StringComparison.OrdinalIgnoreCase))
            {
                _currentProfile = target;
                changed = true;
            }
        }

        if (changed && target is not null)
        {
            OnCurrentProfileChanged(target, definitionsUpdated: false);
        }

        return changed;
    }

    public static void UpdateOriginalProfile(string dataDirectory)
    {
        if (!DevDebug || string.IsNullOrWhiteSpace(dataDirectory))
        {
            return;
        }

        DeveloperProfile? profileToNotify = null;
        bool definitionsUpdated = false;

        lock (SyncRoot)
        {
            string normalized;
            try
            {
                normalized = Path.GetFullPath(dataDirectory);
            }
            catch (Exception)
            {
                return;
            }

            if (_originalProfile is null || !string.Equals(_originalProfile.DataDirectory, normalized, StringComparison.OrdinalIgnoreCase))
            {
                _originalProfile = new DeveloperProfile("original", "My Profile (Original)", normalized, firebaseStateFilePath: null, playerName: null, playerUid: null, isOriginal: true);
                definitionsUpdated = true;
            }

            if (_profilesRoot is null)
            {
                _profilesRoot = EnsureProfilesRoot();
                definitionsUpdated = true;
            }

            if (definitionsUpdated || _profiles.Count == 0)
            {
                RebuildProfilesLocked();
                definitionsUpdated = true;
            }

            if (_currentProfile is null || _currentProfile.IsOriginal)
            {
                _currentProfile = _originalProfile;
                profileToNotify = _currentProfile;
            }
            else if (definitionsUpdated)
            {
                profileToNotify = _currentProfile;
            }
            else if (_originalProfile is not null && _currentProfile is null)
            {
                profileToNotify = _originalProfile;
            }

            if (!definitionsUpdated && profileToNotify is null)
            {
                return;
            }

            profileToNotify ??= _currentProfile ?? _originalProfile;
        }

        if (profileToNotify is not null)
        {
            OnCurrentProfileChanged(profileToNotify, definitionsUpdated);
        }
    }

    public static string? GetFirebaseStateFilePathOverride()
    {
        if (!DevDebug)
        {
            return null;
        }

        lock (SyncRoot)
        {
            string? path = _currentProfile?.FirebaseStateFilePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }
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

            return path;
        }
    }

    private static void EnsureProfilesReadyLocked()
    {
        if (_originalProfile is null)
        {
            _profiles = new List<DeveloperProfile>();
            return;
        }

        if (_profilesRoot is null)
        {
            _profilesRoot = EnsureProfilesRoot();
        }

        if (_profiles.Count == 0)
        {
            RebuildProfilesLocked();
        }

        if (_currentProfile is null)
        {
            _currentProfile = _originalProfile;
        }
    }

    private static void RebuildProfilesLocked()
    {
        var profiles = new List<DeveloperProfile>();

        if (_originalProfile is not null)
        {
            profiles.Add(_originalProfile);
        }

        if (_profilesRoot is null)
        {
            _profiles = profiles;
            return;
        }

        foreach (DeveloperProfileDefinition definition in FakeProfiles)
        {
            string profileRoot = Path.Combine(_profilesRoot, definition.Id);
            string dataDirectory = Path.Combine(profileRoot, "Data");
            string firebaseDirectory = Path.Combine(profileRoot, "Firebase");

            EnsureFakeProfileData(profileRoot, dataDirectory, firebaseDirectory, definition);

            string firebaseState = Path.Combine(firebaseDirectory, "firebase-auth.json");
            profiles.Add(new DeveloperProfile(definition.Id, definition.DisplayName, dataDirectory, firebaseState, definition.PlayerName, definition.PlayerUid, isOriginal: false));
        }

        _profiles = profiles;
    }

    private static string EnsureProfilesRoot()
    {
        string? managerDirectory = ModCacheLocator.GetManagerDataDirectory();
        string root = string.IsNullOrWhiteSpace(managerDirectory)
            ? Path.Combine(Path.GetTempPath(), "SimpleVSManager", "DeveloperProfiles")
            : Path.Combine(managerDirectory!, "DeveloperProfiles");

        Directory.CreateDirectory(root);
        return root;
    }

    private static void EnsureFakeProfileData(string profileRoot, string dataDirectory, string firebaseDirectory, DeveloperProfileDefinition definition)
    {
        try
        {
            Directory.CreateDirectory(profileRoot);
            Directory.CreateDirectory(dataDirectory);
            Directory.CreateDirectory(firebaseDirectory);
            Directory.CreateDirectory(Path.Combine(dataDirectory, "Mods"));
            Directory.CreateDirectory(Path.Combine(dataDirectory, "ModConfig"));

            var root = new JsonObject
            {
                ["stringSettings"] = new JsonObject
                {
                    ["playeruid"] = definition.PlayerUid,
                    ["playername"] = definition.PlayerName
                },
                ["stringListSettings"] = new JsonObject()
            };

            string settingsPath = Path.Combine(dataDirectory, "clientsettings.json");
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
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
    }

    private static void OnCurrentProfileChanged(DeveloperProfile profile, bool definitionsUpdated)
    {
        CurrentProfileChanged?.Invoke(null, new DeveloperProfileChangedEventArgs(profile, definitionsUpdated));
    }

    private readonly record struct DeveloperProfileDefinition(string Id, string DisplayName, string PlayerName, string PlayerUid);
}

public sealed class DeveloperProfile
{
    internal DeveloperProfile(string id, string displayName, string dataDirectory, string? firebaseStateFilePath, string? playerName, string? playerUid, bool isOriginal)
    {
        Id = id;
        DisplayName = displayName;
        DataDirectory = dataDirectory;
        FirebaseStateFilePath = firebaseStateFilePath;
        PlayerName = playerName;
        PlayerUid = playerUid;
        IsOriginal = isOriginal;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string DataDirectory { get; }

    public string? FirebaseStateFilePath { get; }

    public string? PlayerName { get; }

    public string? PlayerUid { get; }

    public bool IsOriginal { get; }
}

public sealed class DeveloperProfileChangedEventArgs : EventArgs
{
    internal DeveloperProfileChangedEventArgs(DeveloperProfile profile, bool profilesUpdated)
    {
        Profile = profile;
        ProfilesUpdated = profilesUpdated;
    }

    public DeveloperProfile Profile { get; }

    public bool ProfilesUpdated { get; }
}

