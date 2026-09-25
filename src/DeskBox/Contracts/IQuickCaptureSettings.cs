using DeskBox.Models;

namespace DeskBox.Contracts;

public sealed record QuickCaptureSettingsSnapshot(bool Enabled, bool ClipboardEnabled, bool ImageEnabled);

public readonly record struct QuickCaptureTabSettings(
    string DefaultView,
    bool ShowTabBar,
    bool ShowRecordsTab,
    bool ShowPinnedTab,
    bool ShowRecentTab);

public readonly record struct QuickCapturePresentationSettings(
    string TabStyle,
    bool ShowCreatedTime,
    int PreviewLineCount);

/// <summary>Effective sizes; stored zero inherits the global appearance size.</summary>
public readonly record struct QuickCaptureTextSizeSettings(
    double ListTextSize,
    double ContentTextSize);

/// <summary>One owner for Quick Capture enablement, recording choices, and their host work.</summary>
public interface IQuickCaptureSettings
{
    QuickCaptureSettingsSnapshot Read();
    QuickCaptureTabSettings ReadTabs();
    void SetDefaultView(string? view);
    void SetTabVisible(string? view, bool visible);
    void SetTabBarVisible(bool visible);
    void ResetTabPreferences(bool scheduleSave = true);
    QuickCapturePresentationSettings ReadPresentation();
    void SetTabStyle(string? style);
    void SetShowCreatedTime(bool visible);
    void SetPreviewLineCount(int lineCount);
    void ResetPresentationPreferences(bool scheduleSave = true);
    int ReadRecentLimit();
    void SetRecentLimit(int limit);
    void ResetRecentLimit(bool scheduleSave = true);
    QuickCaptureTextSizeSettings ReadTextSizes();
    bool TrySetListTextSize(double size, bool scheduleSave = true);
    bool TrySetContentTextSize(double size, bool scheduleSave = true);
    Task SetEnabledAsync(bool enabled, bool reveal = true, CancellationToken cancellationToken = default);
    Task SetClipboardEnabledAsync(bool enabled, bool captureCurrent = true, CancellationToken cancellationToken = default);
    Task SetImageEnabledAsync(bool enabled, bool captureCurrent = true, CancellationToken cancellationToken = default);
    Task ResetRecordingAsync();
    void CommitEnabledState(bool enabled);
    void RefreshFromSettings(bool captureCurrent = false);
    QuickCaptureClipboardDiagnostics? ClipboardDiagnostics { get; }
    event Action? Changed;
    event Action? DiagnosticsChanged;
    Task StopAsync();
}

/// <summary>The host adapter for one clipboard subscription and its in-flight capture.</summary>
public interface IQuickCaptureClipboardSession : IDisposable
{
    event Action? DiagnosticsChanged;
    QuickCaptureClipboardDiagnostics GetDiagnostics();
    void Refresh();
    void CaptureCurrent();
    Task StopAsync();
}
