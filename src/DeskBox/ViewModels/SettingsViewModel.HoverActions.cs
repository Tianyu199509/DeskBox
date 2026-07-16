using System.Globalization;
using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    private bool CanToggleHoverButtonAction(bool isSelected)
    {
        int selectedCount = GetSelectedHoverButtonActionCount();
        return isSelected
            ? selectedCount > 1
            : selectedCount < 3;
    }

    public bool IsHoverButtonActionSelected(string action)
    {
        return action switch
        {
            SettingsService.WidgetHoverActionLockPosition => ShowHoverActionLockPosition,
            SettingsService.WidgetHoverActionLockSize => ShowHoverActionLockSize,
            SettingsService.WidgetHoverActionAdd => ShowHoverActionAdd,
            SettingsService.WidgetHoverActionMore => ShowHoverActionMore,
            SettingsService.WidgetHoverActionDelete => ShowHoverActionDelete,
            _ => false
        };
    }

    public bool CanToggleHoverButtonAction(string action)
    {
        return CanToggleHoverButtonAction(IsHoverButtonActionSelected(action));
    }

    public void ToggleHoverButtonAction(string action)
    {
        switch (action)
        {
            case SettingsService.WidgetHoverActionLockPosition:
                ShowHoverActionLockPosition = !ShowHoverActionLockPosition;
                break;
            case SettingsService.WidgetHoverActionLockSize:
                ShowHoverActionLockSize = !ShowHoverActionLockSize;
                break;
            case SettingsService.WidgetHoverActionAdd:
                ShowHoverActionAdd = !ShowHoverActionAdd;
                break;
            case SettingsService.WidgetHoverActionMore:
                ShowHoverActionMore = !ShowHoverActionMore;
                break;
            case SettingsService.WidgetHoverActionDelete:
                ShowHoverActionDelete = !ShowHoverActionDelete;
                break;
        }
    }

    private void ApplyHoverButtonActionSelection(string? value)
    {
        var selected = SettingsService.ParseWidgetHoverButtonActions(value);
        bool previousUpdating = _isUpdatingHoverButtonActionSelection;
        _isUpdatingHoverButtonActionSelection = true;
        try
        {
            ShowHoverActionLockPosition = selected.Contains(SettingsService.WidgetHoverActionLockPosition);
            ShowHoverActionLockSize = selected.Contains(SettingsService.WidgetHoverActionLockSize);
            ShowHoverActionAdd = selected.Contains(SettingsService.WidgetHoverActionAdd);
            ShowHoverActionMore = selected.Contains(SettingsService.WidgetHoverActionMore);
            ShowHoverActionDelete = selected.Contains(SettingsService.WidgetHoverActionDelete);
        }
        finally
        {
            _isUpdatingHoverButtonActionSelection = previousUpdating;
        }

        NotifyHoverButtonActionPropertiesChanged();
    }

    private void OnHoverButtonActionSelectionChanged(string action, bool value)
    {
        if (_isUpdatingHoverButtonActionSelection)
        {
            return;
        }

        int selectedCount = GetSelectedHoverButtonActionCount();
        if ((!value && selectedCount == 0) || (value && selectedCount > 3))
        {
            RevertHoverButtonActionSelection(action, !value);
            return;
        }

        NotifyHoverButtonActionPropertiesChanged();

        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.WidgetHoverButtonActions = BuildHoverButtonActionSettingValue();
        SaveAppearanceChange();
    }

    private void RevertHoverButtonActionSelection(string action, bool value)
    {
        bool previousUpdating = _isUpdatingHoverButtonActionSelection;
        _isUpdatingHoverButtonActionSelection = true;
        try
        {
            switch (action)
            {
                case SettingsService.WidgetHoverActionLockPosition:
                    ShowHoverActionLockPosition = value;
                    break;
                case SettingsService.WidgetHoverActionLockSize:
                    ShowHoverActionLockSize = value;
                    break;
                case SettingsService.WidgetHoverActionAdd:
                    ShowHoverActionAdd = value;
                    break;
                case SettingsService.WidgetHoverActionMore:
                    ShowHoverActionMore = value;
                    break;
                case SettingsService.WidgetHoverActionDelete:
                    ShowHoverActionDelete = value;
                    break;
            }
        }
        finally
        {
            _isUpdatingHoverButtonActionSelection = previousUpdating;
        }

        NotifyHoverButtonActionPropertiesChanged();
    }

    private int GetSelectedHoverButtonActionCount()
    {
        int count = 0;
        if (ShowHoverActionLockPosition)
        {
            count++;
        }

        if (ShowHoverActionLockSize)
        {
            count++;
        }

        if (ShowHoverActionAdd)
        {
            count++;
        }

        if (ShowHoverActionMore)
        {
            count++;
        }

        if (ShowHoverActionDelete)
        {
            count++;
        }

        return count;
    }

    private string BuildHoverButtonActionSettingValue()
    {
        var selected = new List<string>(capacity: 3);
        AddHoverButtonActionIfSelected(selected, SettingsService.WidgetHoverActionLockPosition, ShowHoverActionLockPosition);
        AddHoverButtonActionIfSelected(selected, SettingsService.WidgetHoverActionLockSize, ShowHoverActionLockSize);
        AddHoverButtonActionIfSelected(selected, SettingsService.WidgetHoverActionAdd, ShowHoverActionAdd);
        AddHoverButtonActionIfSelected(selected, SettingsService.WidgetHoverActionMore, ShowHoverActionMore);
        AddHoverButtonActionIfSelected(selected, SettingsService.WidgetHoverActionDelete, ShowHoverActionDelete);
        return selected.Count == 0
            ? SettingsService.DefaultWidgetHoverButtonActions
            : string.Join(",", selected);
    }

    private static void AddHoverButtonActionIfSelected(ICollection<string> selected, string action, bool isSelected)
    {
        if (isSelected && selected.Count < 3)
        {
            selected.Add(action);
        }
    }

    private void NotifyHoverButtonActionPropertiesChanged()
    {
        OnPropertyChanged(nameof(CanToggleHoverActionLockPosition));
        OnPropertyChanged(nameof(CanToggleHoverActionLockSize));
        OnPropertyChanged(nameof(CanToggleHoverActionAdd));
        OnPropertyChanged(nameof(CanToggleHoverActionMore));
        OnPropertyChanged(nameof(CanToggleHoverActionDelete));
        OnPropertyChanged(nameof(HoverButtonActionsSummaryText));
    }

    private static string NormalizeWidgetAnimationEffect(string? effect)
    {
        return effect is
            SettingsService.WidgetAnimationEffectNone or
            SettingsService.WidgetAnimationEffectFade or
            SettingsService.WidgetAnimationEffectSlideRight or
            SettingsService.WidgetAnimationEffectSlideLeft or
            SettingsService.WidgetAnimationEffectSlideUp or
            SettingsService.WidgetAnimationEffectSlideDown or
            SettingsService.WidgetAnimationEffectScaleFade or
            SettingsService.WidgetAnimationEffectSlideFade or
            SettingsService.WidgetAnimationEffectZoom or
            SettingsService.WidgetAnimationEffectSlideUpFade or
            SettingsService.WidgetAnimationEffectSlideDownFade or
            SettingsService.WidgetAnimationEffectSlideLeftFade or
            SettingsService.WidgetAnimationEffectSlideRightFade or
            SettingsService.WidgetAnimationEffectScaleSlide
            ? effect
            : SettingsService.WidgetAnimationEffectSlideFade;
    }

    private static string NormalizeWidgetAnimationSpeed(string? speed)
    {
        return speed is
            SettingsService.WidgetAnimationSpeedVeryFast or
            SettingsService.WidgetAnimationSpeedFast or
            SettingsService.WidgetAnimationSpeedStandard or
            SettingsService.WidgetAnimationSpeedRelaxed or
            SettingsService.WidgetAnimationSpeedSlow
            ? speed
            : SettingsService.WidgetAnimationSpeedStandard;
    }

    private static string NormalizeWidgetAnimationSlideDirection(string? direction)
    {
        return direction is
            SettingsService.WidgetAnimationSlideDirectionNone or
            SettingsService.WidgetAnimationSlideDirectionLeft or
            SettingsService.WidgetAnimationSlideDirectionRight or
            SettingsService.WidgetAnimationSlideDirectionUp or
            SettingsService.WidgetAnimationSlideDirectionDown
            ? direction
            : SettingsService.WidgetAnimationSlideDirectionRight;
    }

    private static string NormalizeWidgetAnimationEasingIntensity(string? intensity)
    {
        return intensity is
            SettingsService.WidgetAnimationEasingNone or
            SettingsService.WidgetAnimationEasingLight or
            SettingsService.WidgetAnimationEasingStandard or
            SettingsService.WidgetAnimationEasingStrong
            ? intensity
            : SettingsService.WidgetAnimationEasingStandard;
    }

    private static string NormalizeWidgetChromeModeSetting(string? mode, WidgetChromeMode fallback)
    {
        return SettingsService.NormalizeWidgetChromeModeSetting(mode, fallback);
    }

    private static string NormalizeWidgetTitleIconModeSetting(string? mode)
    {
        return SettingsService.NormalizeWidgetTitleIconModeSetting(mode);
    }

    private static string NormalizeTodoNewTaskPosition(string? position)
    {
        return position == SettingsService.TodoNewTaskPositionBottom
            ? SettingsService.TodoNewTaskPositionBottom
            : SettingsService.TodoNewTaskPositionTop;
    }

    private static string NormalizeQuickCaptureDefaultView(string? view)
    {
        return view is
            SettingsService.QuickCaptureDefaultViewPinned or
            SettingsService.QuickCaptureDefaultViewRecent
            ? view
            : SettingsService.QuickCaptureDefaultViewRecords;
    }

    private static string NormalizeTodoDefaultFilter(string? filter)
    {
        return filter is
            SettingsService.TodoDefaultFilterToday or
            SettingsService.TodoDefaultFilterImportant or
            SettingsService.TodoDefaultFilterCompleted
            ? filter
            : SettingsService.TodoDefaultFilterAll;
    }
}
