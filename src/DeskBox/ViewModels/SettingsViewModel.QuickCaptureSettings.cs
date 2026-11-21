using DeskBox.Contracts;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    private void OnQuickCaptureSettingsChanged()
    {
        if (_isDisposed) return;
        // The section's whole presentation (feature/recording switches incl.
        // the coordinator's enablement chaining, tabs, editor preferences,
        // capacity and text sizes) lives on the Quick Capture editor; the
        // coordinator broadcast re-projects it there.
        _quickCaptureSettingsEditor.SyncPresentation();
        OnPropertyChanged(nameof(FeatureWidgetEntries));
    }

    // The editor commits text-size writes with scheduleSave:false (the old
    // shell facade path); answer with the shared appearance save pass so the
    // preview orchestration and the slider-drag suppression flags still own
    // persistence.
    private void OnQuickCaptureListTextSizeCommitted() => SaveAppearanceChange();

    private void OnQuickCaptureContentTextSizeCommitted() => SaveAppearanceChange();

    // Todo editors commit text-size writes under the same contract (raw
    // override persisted with scheduleSave:false); the shared appearance
    // save pass owns the preview orchestration and debounced persistence.
    private void OnTodoListTextSizeCommitted() => SaveAppearanceChange();

    private void OnTodoContentTextSizeCommitted() => SaveAppearanceChange();
}
