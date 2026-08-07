using System.Collections.Generic;
using System.Collections.ObjectModel;
using VintageStoryModManager.Models;

namespace VintageStoryModManager.Design;

/// <summary>
/// Provides design-time data for the <see cref="VintageStoryModManager.Views.ModBrowserView" />.
/// </summary>
public sealed class ModBrowserViewDesignData
{
    public ModBrowserViewDesignData()
    {
        // Create sample mods for design-time viewing
        ModsList = new ObservableCollection<DownloadableModOnList>
        {
            new()
            {
                ModId = 1,
                AssetId = 101,
                Name = "Better HUD",
                Summary = "Adds additional HUD widgets and customization options for an improved user experience.",
                Author = "Aurora",
                Downloads = 15234,
                Follows = 1823,
                Comments = 142,
                Side = "Client",
                Type = "Code Mod",
                LogoFileDatabase = "https://moddbcdn.vintagestory.at/playercorpse_86bd2916a59eeddb4b02df4d78a66304.png",
                Logo = "https://moddbcdn.vintagestory.at/playercorpse_86bd2916a59eeddb4b02df4d78a66304.png",
                Tags = new List<string> { "UI", "Client" },
                LastReleased = "2024-11-15",
                UserReportDisplay = "Fully functional (12)",
                UserReportTooltip = "Fully functional (12)\nNo issues noticed (4)\nSome issues but works (2)\nNot functional (1)\nCrashes/Freezes game (0)",
                ShowUserReportBadge = true,
                IsInstalled = true
            },
            new()
            {
                ModId = 2,
                AssetId = 102,
                Name = "Expanded Storage",
                Summary = "Improves and rebalances all storage-related blocks and recipes. Adds new storage types and sizes.",
                Author = "Epsilon",
                Downloads = 28901,
                Follows = 3456,
                Comments = 278,
                Side = "Both",
                Type = "Code Mod",
                LogoFileDatabase = "https://moddbcdn.vintagestory.at/modicon1_c4dff1d57426dd7a9e6fec18057f1fc7.png",
                Logo = "https://moddbcdn.vintagestory.at/modicon1_c4dff1d57426dd7a9e6fec18057f1fc7.png",
                Tags = new List<string> { "Storage", "Gameplay" },
                LastReleased = "2024-11-10",
                UserReportDisplay = "No issues noticed (8)",
                UserReportTooltip = "Fully functional (6)\nNo issues noticed (8)\nSome issues but works (1)\nNot functional (0)\nCrashes/Freezes game (0)",
                ShowUserReportBadge = true,
                IsInstalled = true
            },
            new()
            {
                ModId = 3,
                AssetId = 103,
                Name = "World Edit Plus",
                Summary = "Advanced world editing tools with region presets, macros and powerful selection options.",
                Author = "Lyra",
                Downloads = 45678,
                Follows = 5234,
                Comments = 389,
                Side = "Both",
                Type = "Code Mod",
                LogoFileDatabase = "https://moddbcdn.vintagestory.at/modicon_910cef72d741fec6c488e5c5cf99f84f.png",
                Logo = "https://moddbcdn.vintagestory.at/modicon_910cef72d741fec6c488e5c5cf99f84f.png",
                Tags = new List<string> { "Tools", "Admin" },
                LastReleased = "2024-11-20",
                UserReportDisplay = "Mixed (6)",
                UserReportTooltip = "Fully functional (3)\nNo issues noticed (3)\nSome issues but works (3)\nNot functional (1)\nCrashes/Freezes game (0)",
                ShowUserReportBadge = true,
                IsInstalled = true
            },
            new()
            {
                ModId = 4,
                AssetId = 104,
                Name = "Carry Capacity",
                Summary = "Realistic inventory weight system that adds depth to the survival experience.",
                Author = "Nova",
                Downloads = 12456,
                Follows = 1567,
                Comments = 98,
                Side = "Both",
                Type = "Code Mod",
                LogoFileDatabase = "https://moddbcdn.vintagestory.at/thumbnail2_594aaea088bc90b76bef20fedb95b70a.png",
                Tags = new List<string> { "Gameplay", "Survival" },
                LastReleased = "2024-10-30",
                UserReportDisplay = "Some issues but works (3)",
                UserReportTooltip = "Fully functional (1)\nNo issues noticed (0)\nSome issues but works (3)\nNot functional (0)\nCrashes/Freezes game (0)",
                ShowUserReportBadge = true,
                IsInstalled = false
            },
            new()
            {
                ModId = 5,
                AssetId = 105,
                Name = "Medieval Expansion",
                Summary = "Adds hundreds of new medieval-themed blocks, items, and decorative elements to the game.",
                Author = "Rigel",
                Downloads = 67890,
                Follows = 8912,
                Comments = 523,
                Side = "Both",
                Type = "Content Mod",
                LogoFileDatabase = "https://moddbcdn.vintagestory.at/logo3_fbd3936aa3bd17ad6e69eb38b6bb80c8.jpg",
                Logo = "https://moddbcdn.vintagestory.at/logo3_fbd3936aa3bd17ad6e69eb38b6bb80c8.jpg",
                Tags = new List<string> { "Content", "Building", "Medieval" },
                LastReleased = "2024-11-18",
                UserReportDisplay = "No votes",
                UserReportTooltip = "Fully functional (0)\nNo issues noticed (0)\nSome issues but works (0)\nNot functional (0)\nCrashes/Freezes game (0)"
            },
            new()
            {
                ModId = 6,
                AssetId = 106,
                Name = "Primitive Survival",
                Summary = "Enhanced survival mechanics focused on primitive technology and stone age gameplay.",
                Author = "Vega",
                Downloads = 34567,
                Follows = 4123,
                Comments = 267,
                Side = "Both",
                Type = "Code Mod",
                Logo = "https://mods.vintagestory.at/web/img/mod-default.png",
                Tags = new List<string> { "Survival", "Gameplay", "Hardcore" },
                LastReleased = "2024-11-12",
                UserReportDisplay = "Crashes/Freezes game (2)",
                UserReportTooltip = "Fully functional (0)\nNo issues noticed (1)\nSome issues but works (0)\nNot functional (0)\nCrashes/Freezes game (2)",
                ShowUserReportBadge = true,
                IsInstalled = false
            }
        };

        // Initialize other collections
        FavoriteMods = new ObservableCollection<int> { 2, 5 };
        InstalledMods = new ObservableCollection<int> { 1, 2, 3 };
        AvailableVersions = new ObservableCollection<GameVersion>();
        AvailableTags = new ObservableCollection<ModTag>();
        SelectedVersions = new ObservableCollection<GameVersion>();
        SelectedTags = new ObservableCollection<ModTag>();
    }

    public ObservableCollection<DownloadableModOnList> ModsList { get; }
    public IEnumerable<DownloadableModOnList> VisibleMods => ModsList;
    public ObservableCollection<int> FavoriteMods { get; }
    public ObservableCollection<int> InstalledMods { get; }
    public ObservableCollection<GameVersion> AvailableVersions { get; }
    public ObservableCollection<ModTag> AvailableTags { get; }
    public ObservableCollection<GameVersion> SelectedVersions { get; set; }
    public ObservableCollection<ModTag> SelectedTags { get; set; }
    public bool IsSearching => false;
}
