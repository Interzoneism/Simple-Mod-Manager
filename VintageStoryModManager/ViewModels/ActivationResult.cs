namespace VintageStoryModManager.ViewModels;

/// <summary>
/// Represents the outcome of attempting to change a mod's activation state.
/// </summary>
public readonly record struct ActivationResult(bool Success, string? ErrorMessage);
