#if DESKBOX_NATIVE_AOT
namespace DeskBox.Features.QuickCapture;

// The Quick Capture settings section keeps its runtime {Binding} surface
// and binds through a section-level DataContext switch. Expose only the
// properties used by that XAML surface in NativeAOT builds, mirroring the
// Glance/Music/FileStack editor bridge pattern.
[WinRT.GeneratedBindableCustomProperty([
    nameof(AllowRemoteImages),
    nameof(AvailableEnterBehaviorOptions),
    nameof(AvailableFormatOptions),
    nameof(AvailablePreviewLineOptions),
    nameof(AvailableWideLayoutOptions),
    nameof(AvailableWideOpenModeOptions),
    nameof(CanClearImageCache),
    nameof(ContentSummaryText),
    nameof(ContentTextSize),
    nameof(ContentTextSizeValueText),
    nameof(EditorEnterBehavior),
    nameof(EditorFormat),
    nameof(Enabled),
    nameof(ImageCacheText),
    nameof(ItemPreviewLineCount),
    nameof(LayoutSummaryText),
    nameof(ListTextSize),
    nameof(ListTextSizeValueText),
    nameof(SelectedDefaultView),
    nameof(ShowCreatedTime),
    nameof(ShowRemoteImages),
    nameof(ShowTabBar),
    nameof(ShowWideOptions),
    nameof(StatusText),
    nameof(TabStyleIndex),
    nameof(TabsSummaryText),
    nameof(VisibleDefaultViewOptions),
    nameof(VisibleTabsText),
    nameof(WideLayout),
    nameof(WideOpenMode)
], [])]
public sealed partial class QuickCaptureSettingsViewModel
{
}
#endif