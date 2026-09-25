using DeskBox.Services;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    public string QuickCaptureListTextSizeValueText => $"{QuickCaptureListTextSize:0.#}pt";
    public string QuickCaptureContentTextSizeValueText => $"{QuickCaptureContentTextSize:0.#}pt";
    public string TodoListTextSizeValueText => $"{TodoListTextSize:0.#}pt";
    public string TodoContentTextSizeValueText => $"{TodoContentTextSize:0.#}pt";

    partial void OnQuickCaptureListTextSizeChanged(double value) =>
        PersistOwnedTextSize(
            value,
            normalized => QuickCaptureListTextSize = normalized,
            TrySetQuickCaptureListTextSize,
            nameof(QuickCaptureListTextSizeValueText));

    partial void OnQuickCaptureContentTextSizeChanged(double value) =>
        PersistOwnedTextSize(
            value,
            normalized => QuickCaptureContentTextSize = normalized,
            TrySetQuickCaptureContentTextSize,
            nameof(QuickCaptureContentTextSizeValueText));

    partial void OnTodoListTextSizeChanged(double value) =>
        PersistOwnedTextSize(
            value,
            normalized => TodoListTextSize = normalized,
            normalized => _todoSettings.TrySetListTextSize(
                normalized,
                scheduleSave: false),
            nameof(TodoListTextSizeValueText));

    partial void OnTodoContentTextSizeChanged(double value) =>
        PersistOwnedTextSize(
            value,
            normalized => TodoContentTextSize = normalized,
            normalized => _todoSettings.TrySetContentTextSize(
                normalized,
                scheduleSave: false),
            nameof(TodoContentTextSizeValueText));

    private void PersistOwnedTextSize(
        double value,
        Action<double> setViewModelValue,
        Func<double, bool> setStoredValue,
        string valueTextPropertyName)
    {
        OnPropertyChanged(valueTextPropertyName);
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot) return;

        if (!double.IsFinite(value))
        {
            setViewModelValue(SettingsService.NormalizeTextSize(
                _settingsService.Settings.TextSize));
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

        if (!setStoredValue(normalized)) return;
        SaveAppearanceChange();
        OnPropertyChanged(valueTextPropertyName);
    }

}
