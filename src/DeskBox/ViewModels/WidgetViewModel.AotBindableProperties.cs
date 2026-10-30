#if DESKBOX_NATIVE_AOT
namespace DeskBox.ViewModels;

// FileSurfaceContent keeps its established runtime Binding surface. Expose
// only the root and layout properties consumed by the real File Widget XAML.
[WinRT.GeneratedBindableCustomProperty([
    nameof(ContentPadding),
    nameof(CurrentFolderDisplayName),
    nameof(CurrentFolderRelativePath),
    nameof(EmptyStateGlyph),
    nameof(EmptyStateTitle),
    nameof(FolderNavigationVisibility),
    nameof(IconCellHeight),
    nameof(IconCellWidth),
    nameof(IconContentSpacing),
    nameof(IconImageSize),
    nameof(IconLabelFontSize),
    nameof(IconLabelMaxLines),
    nameof(IconLabelMaxWidth),
    nameof(IconLabelVisibility),
    nameof(IconTileHeight),
    nameof(IconTileMargin),
    nameof(IconTileWidth),
    nameof(IconViewVisibility),
    nameof(IsLoading),
    nameof(ListIconSize),
    nameof(ListItemDetailFontSize),
    nameof(ListItemDetailVisibility),
    nameof(ListLabelFontSize),
    nameof(ListViewVisibility),
    nameof(LoadingVisibility),
    nameof(ScrollBarsEnabled),
    nameof(ShowFileItemPathTooltips),
    nameof(VisibleItems),
    nameof(RenderedItems)
], [])]
public partial class WidgetViewModel
{
}
#endif
