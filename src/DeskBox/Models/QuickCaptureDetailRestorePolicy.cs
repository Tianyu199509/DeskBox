using DeskBox.Services;

namespace DeskBox.Models;

/// <summary>
/// Keeps a remembered list selection from being mistaken for an open detail
/// page when Quick Capture is hosted in a single-pane widget group.
/// </summary>
public static class QuickCaptureDetailRestorePolicy
{
    public static bool ShouldCaptureDetail(
        bool isDualPane,
        bool isDetailVisibleInSinglePane,
        bool hasDetail) =>
        hasDetail && (isDualPane || isDetailVisibleInSinglePane);

    public static bool ShouldRestoreDetail(
        bool isDualPane,
        bool wasDetailVisibleInSinglePane) =>
        isDualPane || wasDetailVisibleInSinglePane;
}

internal sealed record QuickCaptureExtendedTransientState(
    QuickCaptureWidgetTransientState BaseState,
    bool WasDualPane = false,
    bool IsCreatingDetail = false,
    QuickCaptureAppearancePreset DetailAppearance = QuickCaptureAppearancePreset.Default,
    TextContentFormat DetailContentFormat = TextContentFormat.Markdown,
    DroppedFilePath[]? PendingDetailAttachments = null,
    bool HasAppearanceOverride = false);
