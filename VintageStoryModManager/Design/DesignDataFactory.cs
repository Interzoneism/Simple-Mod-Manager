using System.Reflection;
using VintageStoryModManager.Models;
using VintageStoryModManager.ViewModels;

namespace VintageStoryModManager.Design;

/// <summary>
///     Creates design-time instances of view models.
/// </summary>
internal static class DesignDataFactory
{
    private static readonly byte[] SampleIcon = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9s8Z5n8AAAAASUVORK5CYII=");

    public static IReadOnlyList<ModListItemViewModel> CreateSampleMods()
    {
        var mods = new List<ModListItemViewModel>
        {
            CreateMod(
                "betterhud",
                "Better HUD",
                "1.4.2",
                "1.21.0",
                "https://example.com/betterhud",
                "Mods/betterhud_v1.4.2.zip",
                "Mods/betterhud_v1.4.2.zip",
                ModSourceKind.ZipArchive,
                new[] { "Aurora" },
                new[] { "Vega" },
                new[] { new ModDependencyInfo("game", "1.21.0") },
                "Adds additional HUD widgets and customization options.",
                "Client",
                true,
                false,
                true,
                null,
                null,
                null),
            CreateMod(
                "expandedstorage",
                "Expanded Storage",
                "2.0.0",
                "1.21.0",
                "https://mods.vintagestory.at/expandedstorage",
                "Mods/expandedstorage",
                "Mods/expandedstorage",
                ModSourceKind.Folder,
                new[] { "Epsilon" },
                Array.Empty<string>(),
                new[]
                {
                    new ModDependencyInfo("game", "1.21.0"),
                    new ModDependencyInfo("survival", "1.21.0")
                },
                "Improves and rebalances all storage-related blocks and recipes.",
                "Both",
                true,
                true,
                false,
                "Missing dependency: CarryCapacity ≥ 1.3.0",
                null,
                null),
            CreateMod(
                "utilityscripts",
                "Utility Scripts",
                "0.9.1",
                "1.21.0",
                null,
                "Mods/utilityscripts.dll",
                "Mods/utilityscripts.dll",
                ModSourceKind.Assembly,
                new[] { "Nova", "Rigel" },
                Array.Empty<string>(),
                new[] { new ModDependencyInfo("game", "1.21.0") },
                "Assortment of server utilities and chat commands.",
                "Server",
                false,
                true,
                true,
                null,
                "Unable to load mod. Requires dependency CarryCapacity v1.3.0",
                "Failed to enable scripts due to permission error."),
            CreateMod(
                "worldeditplus",
                "World Edit Plus",
                "3.5.0",
                "1.21.0",
                "https://example.com/worldeditplus",
                "Mods/worldeditplus_v3.5.0.zip",
                "Mods/worldeditplus_v3.5.0.zip",
                ModSourceKind.ZipArchive,
                new[] { "Lyra" },
                new[] { "Orion" },
                new[] { new ModDependencyInfo("game", "1.21.0") },
                "Advanced world editing tools with region presets and macros.",
                "Both",
                true,
                true,
                true,
                null,
                null,
                null)
        };

        return mods;
    }

    private static ModListItemViewModel CreateMod(
        string modId,
        string name,
        string? version,
        string? networkVersion,
        string? website,
        string sourcePath,
        string location,
        ModSourceKind sourceKind,
        IReadOnlyList<string> authors,
        IReadOnlyList<string> contributors,
        IReadOnlyList<ModDependencyInfo> dependencies,
        string? description,
        string? side,
        bool? requiredOnClient,
        bool? requiredOnServer,
        bool isActive,
        string? error,
        string? loadError,
        string? activationError,
        ModDatabaseInfo? databaseInfo = null)
    {
        var entry = new ModEntry
        {
            ModId = modId,
            Name = name,
            ManifestName = name,
            Version = version,
            NetworkVersion = networkVersion,
            Website = website,
            SourcePath = sourcePath,
            SourceKind = sourceKind,
            Authors = authors,
            Contributors = contributors,
            Dependencies = dependencies,
            Description = description,
            Side = side,
            RequiredOnClient = requiredOnClient,
            RequiredOnServer = requiredOnServer,
            IconBytes = SampleIcon,
            Error = error,
            LoadError = loadError,
            DatabaseInfo = databaseInfo
        };

        var viewModel = new ModListItemViewModel(entry, isActive, location,
            (_, _) => Task.FromResult(new ActivationResult(true, null)));

        if (!string.IsNullOrWhiteSpace(activationError)) SetActivationError(viewModel, activationError);

        return viewModel;
    }

    private static void SetActivationError(ModListItemViewModel viewModel, string error)
    {
        var setter = typeof(ModListItemViewModel)
            .GetProperty(nameof(ModListItemViewModel.ActivationError),
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?
            .GetSetMethod(true);

        setter?.Invoke(viewModel, new object?[] { error });
    }
}