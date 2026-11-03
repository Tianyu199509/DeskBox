#if DESKBOX_NATIVE_AOT
namespace DeskBox.Features.FileDisplay;

// The file-display settings section keeps its runtime {Binding} surface and
// binds through a section-level DataContext switch. Expose only the
// properties used by that XAML surface in NativeAOT builds, mirroring the
// GlanceWidgetViewModel bridge pattern.
[WinRT.GeneratedBindableCustomProperty([
    nameof(HideShortcutArrowOverlay),
    nameof(HideShortcutExtensionWhenShowingFileExtensions),
    nameof(ShowFileExtensions),
    nameof(ShowFileItemPathTooltips),
    nameof(ShowImageFilesAsIcons),
    nameof(ShowImageThumbnails),
    nameof(ShowListItemDetails)
], [])]
public sealed partial class FileDisplaySettingsViewModel
{
}
#endif
