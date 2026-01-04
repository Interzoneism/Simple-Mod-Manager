using System.Windows;

namespace VintageStoryModManager.Helpers;

/// <summary>
/// Helper class for managing hover effects throughout the application.
/// </summary>
public static class HoverEffectHelper
{
    /// <summary>
    /// Attached property to disable hover effects on controls.
    /// This property is inheritable, so setting it on a parent will affect all children.
    /// </summary>
    public static readonly DependencyProperty DisableHoverEffectsProperty =
        DependencyProperty.RegisterAttached(
            "DisableHoverEffects",
            typeof(bool),
            typeof(HoverEffectHelper),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetDisableHoverEffects(DependencyObject obj)
    {
        return (bool)obj.GetValue(DisableHoverEffectsProperty);
    }

    public static void SetDisableHoverEffects(DependencyObject obj, bool value)
    {
        obj.SetValue(DisableHoverEffectsProperty, value);
    }
}
