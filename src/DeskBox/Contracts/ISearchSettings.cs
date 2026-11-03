using DeskBox.Models;

namespace DeskBox.Contracts;

public sealed record EverythingConnectionSnapshot(
    EverythingConnectionState State,
    string? ExecutablePath,
    string? Version,
    bool IsRunning,
    bool UsesManualPath,
    string? DiagnosticCode)
{
    public static EverythingConnectionSnapshot Unknown { get; } =
        new(EverythingConnectionState.Unknown, null, null, false, false, null);
}

public sealed record SearchPreferences(
    bool EverythingEnabled,
    bool AdvancedSyntax,
    bool IncludeDeskBoxContent,
    bool ShowRecommendations,
    string DefaultTab,
    int IconAnimation,
    string CategoryBarMode = "auto",
    bool ShowSortHeader = false,
    string DefaultSortColumn = "relevance",
    bool IncludeClipboard = true,
    bool IncludeCalculator = true,
    bool ShowDetailsPane = true,
    bool IncludeWebFallback = true,
    string DefaultWebEngine = "bing",
    string CustomWebUrl = "",
    bool QuickCreateTodoEnabled = true,
    bool QuickCreateNoteEnabled = true,
    string QuickCreateDisplayMode = "always",
    string QuickCreateDefaultTarget = "todo",
    bool QuickCreateShowHud = true);

public sealed record SearchPreferenceChange(
    bool? EverythingEnabled = null,
    bool? AdvancedSyntax = null,
    bool? IncludeDeskBoxContent = null,
    bool? ShowRecommendations = null,
    string? DefaultTab = null,
    int? IconAnimation = null,
    string? CategoryBarMode = null,
    bool? ShowSortHeader = null,
    string? DefaultSortColumn = null,
    bool? IncludeClipboard = null,
    bool? IncludeCalculator = null,
    bool? ShowDetailsPane = null,
    bool? IncludeWebFallback = null,
    string? DefaultWebEngine = null,
    string? CustomWebUrl = null,
    bool? QuickCreateTodoEnabled = null,
    bool? QuickCreateNoteEnabled = null,
    string? QuickCreateDisplayMode = null,
    string? QuickCreateDefaultTarget = null,
    bool? QuickCreateShowHud = null);

public sealed record SearchHotkeyState(
    bool Available,
    bool Enabled,
    bool Registered,
    GlobalHotkeyGesture Gesture,
    string DisplayText,
    HotkeyActivationKind ActivationKind = HotkeyActivationKind.Chord)
{
    public GlobalHotkeyActivation EffectiveActivation =>
        new(ActivationKind, Gesture);
}

public sealed record SearchSettingsSnapshot(
    bool FeatureEnabled,
    SearchPreferences Preferences,
    SearchHotkeyState Hotkey,
    bool TodoFeatureEnabled = true,
    bool QuickCaptureFeatureEnabled = true);

public readonly record struct SearchHotkeyUpdateResult(bool Succeeded, string? Error = null);

/// <summary>Settings operations; the caller owns its requests, never the shared search runtime.</summary>
public interface ISearchSettings
{
    SearchSettingsSnapshot Read();
    string FormatHotkey(GlobalHotkeyGesture gesture);
    string FormatHotkeyActivation(GlobalHotkeyActivation activation) => FormatHotkey(activation.Gesture);
    EverythingConnectionSnapshot Connection { get; }
    event Action? StateChanged;
    void UpdatePreferences(SearchPreferenceChange change);
    SearchHotkeyUpdateResult SetHotkeyEnabled(bool enabled);
    SearchHotkeyUpdateResult ApplyHotkey(GlobalHotkeyGesture gesture);
    SearchHotkeyUpdateResult ApplyHotkeyActivation(GlobalHotkeyActivation activation) =>
        ApplyHotkey(activation.Gesture);
    bool IsGestureOwnedByMainHotkey(GlobalHotkeyGesture gesture);
    bool IsActivationOwnedByMainHotkey(GlobalHotkeyActivation activation) =>
        activation.Kind == HotkeyActivationKind.Chord && IsGestureOwnedByMainHotkey(activation.Gesture);
    bool IsActivationOwnedByClipboardHotkey(GlobalHotkeyActivation activation) => false;
    Task<EverythingConnectionSnapshot> RefreshConnectionAsync(CancellationToken cancellationToken);
    Task<EverythingConnectionSnapshot> DetectAutomaticallyAsync(CancellationToken cancellationToken);
    Task<bool> SelectExecutableAsync(string path, CancellationToken cancellationToken);
    Task<bool> LaunchEverythingAsync(CancellationToken cancellationToken);
}

/// <summary>Borrowed connection capability. The search engine owns its disposal.</summary>
public interface ISearchConnectionClient
{
    EverythingConnectionSnapshot CurrentSnapshot { get; }
    event Action<EverythingConnectionSnapshot>? ConnectionChanged;
    Task<EverythingConnectionSnapshot> RefreshConnectionAsync(bool allowIpcProbe, CancellationToken cancellationToken);
    Task UseAutomaticDetectionAsync(CancellationToken cancellationToken);
    Task<bool> SetExecutablePathAsync(string path, CancellationToken cancellationToken);
    Task<bool> LaunchEverythingAsync(CancellationToken cancellationToken);
}

public interface ISearchHotkeyController
{
    bool IsRegistered { get; }
    GlobalHotkeyGesture CurrentGesture { get; }
    GlobalHotkeyActivation CurrentActivation => GlobalHotkeyActivation.FromChord(CurrentGesture);
    void SetEnabled(bool enabled);
    bool TryApplyGesture(GlobalHotkeyGesture gesture, out string? error);
    bool TryApplyActivation(GlobalHotkeyActivation activation, out string? error) =>
        TryApplyGesture(activation.Gesture, out error);
    bool IsGestureOwnedByMainHotkey(GlobalHotkeyGesture gesture);
    bool IsActivationOwnedByMainHotkey(GlobalHotkeyActivation activation) =>
        activation.Kind == HotkeyActivationKind.Chord && IsGestureOwnedByMainHotkey(activation.Gesture);
}
