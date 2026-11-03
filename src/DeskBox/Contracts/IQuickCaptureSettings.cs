namespace DeskBox.Contracts;

public sealed record QuickCaptureSettingsSnapshot(bool Enabled);

public readonly record struct QuickCaptureTabSettings(
    string DefaultView,
    bool ShowTabBar,
    bool ShowRecordsTab,
    bool ShowPinnedTab);

public readonly record struct QuickCapturePresentationSettings(
    string TabStyle,
    bool ShowCreatedTime,
    int PreviewLineCount);

/// <summary>Effective sizes; stored zero inherits the global appearance size.</summary>
public readonly record struct QuickCaptureTextSizeSettings(
    double ListTextSize,
    double ContentTextSize);

/// <summary>
/// Effective editor preferences; stored raw values normalize exactly like
/// the load pipeline does.
/// </summary>
public readonly record struct QuickCaptureEditorSettings(
    string EnterBehavior,
    string DefaultFormat,
    string WideLayout,
    string WideOpenMode,
    bool AllowRemoteImages);

/// <summary>One owner for Quick Capture enablement and presentation preferences.</summary>
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
    QuickCaptureTextSizeSettings ReadTextSizes();
    bool TrySetListTextSize(double size, bool scheduleSave = true);
    bool TrySetContentTextSize(double size, bool scheduleSave = true);
    QuickCaptureEditorSettings ReadEditorSettings();
    void SetEditorEnterBehavior(string? behavior);
    void SetEditorFormat(string? format);
    void SetWideLayout(string? layout);
    void SetWideOpenMode(string? mode);
    void SetAllowRemoteImages(bool allowed);
    /// <summary>
    /// Writes the fresh-install editor preferences and clears the remembered
    /// last Quick Capture file widget. Used by the feature-card reset flow's
    /// single explicit save; normal edits use the individual setters.
    /// </summary>
    void ResetEditorPreferences(bool scheduleSave = true);
    Task SetEnabledAsync(bool enabled, bool reveal = true, CancellationToken cancellationToken = default);
    void CommitEnabledState(bool enabled);
    void RefreshFromSettings();
    event Action? Changed;
    Task StopAsync();
}