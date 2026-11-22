#if DESKBOX_NATIVE_AOT
namespace DeskBox.Features.Todo;

// The Todo settings section keeps its runtime {Binding} surface and binds
// through a section-level DataContext switch. Expose only the properties
// used by that XAML surface in NativeAOT builds, mirroring the
// Glance/Music/FileStack/QuickCapture editor bridge pattern.
[WinRT.GeneratedBindableCustomProperty([
    nameof(AllDayReminderTimeOfDay),
    nameof(AllDayReminderTimeText),
    nameof(AllDayRemindersEnabled),
    nameof(AllDefaultFilterOptionItems),
    nameof(AllDefaultFilterOptions),
    nameof(AvailableEnterBehaviorOptions),
    nameof(AvailableLayoutOptions),
    nameof(AvailableNewTaskPositionOptions),
    nameof(AvailablePreviewLineOptions),
    nameof(AvailableReminderOffsetOptions),
    nameof(AutoSelectFirstInWideLayout),
    nameof(CanEditAllDayReminderTime),
    nameof(ContentSummaryText),
    nameof(ContentTextSize),
    nameof(ContentTextSizeValueText),
    nameof(DefaultFilter),
    nameof(DefaultOffsetMinutes),
    nameof(EditorEnterBehavior),
    nameof(Enabled),
    nameof(FooterDisplaySummaryText),
    nameof(HasCustomTextSizeOverride),
    nameof(ItemPreviewLineCount),
    nameof(LayoutMode),
    nameof(LayoutSummaryText),
    nameof(ListTextSize),
    nameof(ListTextSizeValueText),
    nameof(NewTaskPosition),
    nameof(ReminderSummaryText),
    nameof(RemindersEnabled),
    nameof(SelectedDefaultFilter),
    nameof(ShowCompletedTasks),
    nameof(ShowTabBar),
    nameof(ShowWideOptions),
    nameof(TabStyleIndex),
    nameof(TabsSummaryText),
    nameof(TextSizeSummaryText),
    nameof(VisibleDefaultFilterOptions),
    nameof(VisibleTabsText),
    nameof(WideLayoutSummaryText)
], [])]
public sealed partial class TodoSettingsViewModel
{
}
#endif