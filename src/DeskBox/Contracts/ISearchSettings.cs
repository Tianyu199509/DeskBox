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
    int IconAnimation);

public sealed record SearchPreferenceChange(
    bool? EverythingEnabled = null,
    bool? AdvancedSyntax = null,
    bool? IncludeDeskBoxContent = null,
    bool? ShowRecommendations = null,
    string? DefaultTab = null,
    int? IconAnimation = null);

public sealed record SearchHotkeyState(
    bool Available,
    bool Enabled,
    bool Registered,
    GlobalHotkeyGesture Gesture,
    string DisplayText);

public sealed record SearchSettingsSnapshot(
    bool FeatureEnabled,
    SearchPreferences Preferences,
    SearchHotkeyState Hotkey);

public readonly record struct SearchHotkeyUpdateResult(bool Succeeded, string? Error = null);

/// <summary>Settings operations; the caller owns its requests, never the shared search runtime.</summary>
public interface ISearchSettings
{
    SearchSettingsSnapshot Read();
    string FormatHotkey(GlobalHotkeyGesture gesture);
    EverythingConnectionSnapshot Connection { get; }
    event Action? StateChanged;
    void UpdatePreferences(SearchPreferenceChange change);
    SearchHotkeyUpdateResult SetHotkeyEnabled(bool enabled);
    SearchHotkeyUpdateResult ApplyHotkey(GlobalHotkeyGesture gesture);
    Task<EverythingConnectionSnapshot> RefreshConnectionAsync(CancellationToken cancellationToken);
    Task<EverythingConnectionSnapshot> DetectAutomaticallyAsync(CancellationToken cancellationToken);
    Task<bool> SelectExecutableAsync(string path, CancellationToken cancellationToken);
    Task<bool> LaunchEverythingAsync(CancellationToken cancellationToken);
}

/// <summary>One writer for the Search master switch and its global runtime transition.</summary>
public interface ISearchFeatureSettings
{
    bool Enabled { get; }
    event Action? FeatureChanged;
    Task SetEnabledAsync(bool enabled, bool reveal = true, CancellationToken cancellationToken = default);
    Task CommitEnabledStateAsync(bool enabled, CancellationToken cancellationToken = default);
    Task StopAsync();
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
    void SetEnabled(bool enabled);
    bool TryApplyGesture(GlobalHotkeyGesture gesture, out string? error);
}
