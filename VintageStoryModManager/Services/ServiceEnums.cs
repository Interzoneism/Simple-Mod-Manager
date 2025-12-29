namespace VintageStoryModManager.Services;

/// <summary>
///     Defines which modlists tab is currently selected.
/// </summary>
public enum ModlistsTabSelection
{
    /// <summary>
    ///     The local modlists tab, showing modlists stored on disk.
    /// </summary>
    Local,

    /// <summary>
    ///     The online modlists tab, showing cloud-stored modlists.
    /// </summary>
    Online
}

/// <summary>
///     Defines how the application should handle automatic modlist loading.
/// </summary>
public enum ModlistAutoLoadBehavior
{
    /// <summary>
    ///     Prompt the user before loading the modlist.
    /// </summary>
    Prompt,

    /// <summary>
    ///     Automatically replace the current mod configuration with the modlist.
    /// </summary>
    Replace,

    /// <summary>
    ///     Automatically add the modlist to the current mod configuration.
    /// </summary>
    Add
}

/// <summary>
///     Defines the available color themes for the application UI.
/// </summary>
public enum ColorTheme
{
    /// <summary>
    ///     The default Vintage Story game theme with warm, earthy tones.
    /// </summary>
    VintageStory,

    /// <summary>
    ///     A modern dark theme with high contrast.
    /// </summary>
    Dark,

    /// <summary>
    ///     A modern light theme with softer colors.
    /// </summary>
    Light,

    /// <summary>
    ///     Randomly selects a theme on startup.
    /// </summary>
    SurpriseMe,

    /// <summary>
    ///     A user-defined custom theme.
    /// </summary>
    Custom
}
