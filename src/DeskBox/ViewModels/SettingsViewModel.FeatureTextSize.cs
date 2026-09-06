using DeskBox.Services;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    public string QuickCaptureListTextSizeValueText => $"{QuickCaptureListTextSize:0.#}pt";
    public string QuickCaptureContentTextSizeValueText => $"{QuickCaptureContentTextSize:0.#}pt";
    public string TodoListTextSizeValueText => $"{TodoListTextSize:0.#}pt";
    public string TodoContentTextSizeValueText => $"{TodoContentTextSize:0.#}pt";

    partial void OnQuickCaptureListTextSizeChanged(double value) =>
        PersistFeatureTextSize(
            value,
            normalized => QuickCaptureListTextSize = normalized,
            normalized => _settingsService.Settings.QuickCaptureListTextSize = normalized,
            nameof(QuickCaptureListTextSizeValueText));

    partial void OnQuickCaptureContentTextSizeChanged(double value) =>
        PersistFeatureTextSize(
            value,
            normalized => QuickCaptureContentTextSize = normalized,
            normalized => _settingsService.Settings.QuickCaptureContentTextSize = normalized,
            nameof(QuickCaptureContentTextSizeValueText));

    partial void OnTodoListTextSizeChanged(double value) =>
        PersistFeatureTextSize(
            value,
            normalized => TodoListTextSize = normalized,
            normalized => _settingsService.Settings.TodoListTextSize = normalized,
            nameof(TodoListTextSizeValueText));

    partial void OnTodoContentTextSizeChanged(double value) =>
        PersistFeatureTextSize(
            value,
            normalized => TodoContentTextSize = normalized,
            normalized => _settingsService.Settings.TodoContentTextSize = normalized,
            nameof(TodoContentTextSizeValueText));

    private void PersistFeatureTextSize(
        double value,
        Action<double> setViewModelValue,
        Action<double> setStoredValue,
        string valueTextPropertyName)
    {
        OnPropertyChanged(valueTextPropertyName);
        if (_isRestoringDefaults)
        {
            return;
        }

        if (!double.IsFinite(value))
        {
            setViewModelValue(SettingsService.NormalizeTextSize(_settingsService.Settings.TextSize));
            return;
        }

        double normalized = Math.Clamp(
            Math.Round(value * 2d, MidpointRounding.AwayFromZero) / 2d,
            SettingsService.MinTextSize,
            SettingsService.MaxTextSize);
        if (Math.Abs(normalized - value) > 0.0001)
        {
            setViewModelValue(normalized);
            return;
        }

        setStoredValue(normalized);
        SaveAppearanceChange();
        OnPropertyChanged(valueTextPropertyName);
    }
}
