using DeskBox.Contracts;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    private readonly ISearchFeatureSettings _searchFeatureSettings;

    private void OnSearchFeatureChanged()
    {
        if (!_isDisposed) OnPropertyChanged(nameof(FeatureWidgetEntries));
    }

    private void TrackSearchFeatureAction(Task action) => _ = CompleteSearchFeatureActionAsync(action);

    private async Task CompleteSearchFeatureActionAsync(Task action)
    {
        try { await action; }
        catch (OperationCanceledException) when (_isDisposed) { }
        catch (Exception ex) { App.Log($"[Search] Feature enablement failed: {ex}"); }
        finally { OnSearchFeatureChanged(); }
    }
}
