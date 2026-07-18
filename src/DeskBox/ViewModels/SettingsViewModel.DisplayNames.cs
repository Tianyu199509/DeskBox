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
    public string GetThemeDisplayName(string theme)
    {
        return theme switch
        {
            ThemeLight => _localizationService.T("Settings.Theme.Light"),
            ThemeDark => _localizationService.T("Settings.Theme.Dark"),
            _ => _localizationService.T("Settings.Theme.System")
        };
    }

    public string GetTrayIconStyleDisplayName(string style)
    {
        return style switch
        {
            TrayIconStyleColorful => _localizationService.T("Settings.TrayIcon.Colorful"),
            TrayIconStyleBlack => _localizationService.T("Settings.TrayIcon.Black"),
            TrayIconStyleWhite => _localizationService.T("Settings.TrayIcon.White"),
            _ => _localizationService.T("Settings.TrayIcon.System")
        };
    }

    public string GetCornerDisplayName(string corner)
    {
        return corner switch
        {
            CornerDefault => _localizationService.T("Settings.Corner.Default"),
            CornerSquare => _localizationService.T("Settings.Corner.Square"),
            CornerRound => _localizationService.T("Settings.Corner.Round"),
            _ => _localizationService.T("Settings.Corner.Small")
        };
    }

    public string GetMaterialTypeDisplayName(string material)
    {
        return material switch
        {
            MaterialMica => _localizationService.T("Settings.Material.Mica"),
            MaterialMicaAlt => _localizationService.T("Settings.Material.MicaAlt"),
            MaterialAcrylicBase => _localizationService.T("Settings.Material.AcrylicBase"),
            MaterialSolid => _localizationService.T("Settings.Material.Solid"),
            _ => _localizationService.T("Settings.Material.Acrylic")
        };
    }

    public string GetBorderColorModeDisplayName(string mode)
    {
        return mode switch
        {
            BorderColorAccent => _localizationService.T("Settings.BorderColor.Accent"),
            BorderColorNone => _localizationService.T("Settings.BorderColor.None"),
            _ => _localizationService.T("Settings.BorderColor.Neutral")
        };
    }

    public string GetBorderStyleDisplayName(string style)
    {
        return style switch
        {
            BorderNone => _localizationService.T("Settings.Border.None"),
            BorderMedium => _localizationService.T("Settings.Border.Medium"),
            BorderThick => _localizationService.T("Settings.Border.Thick"),
            _ => _localizationService.T("Settings.Border.Thin")
        };
    }

    public string GetWidgetCollapseBehaviorDisplayName(string behavior)
    {
        return WidgetCollapseBehaviorNames.Normalize(behavior) switch
        {
            WidgetCollapseBehavior.Expanded => _localizationService.T("Settings.CollapseBehavior.Expanded"),
            WidgetCollapseBehavior.Smart => _localizationService.T("Settings.CollapseBehavior.Smart"),
            _ => _localizationService.T("Settings.CollapseBehavior.Click")
        };
    }

    public string GetWidgetCompactWidthModeDisplayName(string mode)
    {
        return SettingsService.NormalizeWidgetCompactWidthMode(mode) ==
            SettingsService.WidgetCompactWidthModeIndependent
                ? _localizationService.T("Settings.Capsule.WidthMode.Independent")
                : _localizationService.T("Settings.Capsule.WidthMode.Aligned");
    }

    public string GetWidgetCompactContentModeDisplayName(string mode)
    {
        return SettingsService.NormalizeWidgetCompactContentMode(mode) switch
        {
            SettingsService.WidgetCompactContentModeMinimal => _localizationService.T("Settings.CompactContent.Minimal"),
            SettingsService.WidgetCompactContentModeSummary => _localizationService.T("Settings.CompactContent.Summary"),
            _ => _localizationService.T("Settings.CompactContent.Smart")
        };
    }

    public string GetLayoutDensityDisplayName(string density)
    {
        return density switch
        {
            SettingsService.LayoutDensityCompact => _localizationService.T("Settings.Density.Compact"),
            SettingsService.LayoutDensityRelaxed => _localizationService.T("Settings.Density.Relaxed"),
            SettingsService.LayoutDensityCustom => _localizationService.T("Settings.Density.Custom"),
            _ => _localizationService.T("Settings.Density.Standard")
        };
    }

    public string GetAnimationPresetDisplayName(string preset)
    {
        return preset switch
        {
            AnimationPresetNone => _localizationService.T("Settings.Animation.Preset.None"),
            AnimationPresetGentle => _localizationService.T("Settings.Animation.Preset.Gentle"),
            AnimationPresetEmphasized => _localizationService.T("Settings.Animation.Preset.Emphasized"),
            AnimationPresetCustom => _localizationService.T("Settings.Animation.Preset.Custom"),
            _ => _localizationService.T("Settings.Animation.Preset.Standard")
        };
    }

    public string GetWidgetAnimationEffectDisplayName(string effect)
    {
        return NormalizeWidgetAnimationEffect(effect) switch
        {
            SettingsService.WidgetAnimationEffectNone => _localizationService.T("Settings.Animation.Effect.None"),
            SettingsService.WidgetAnimationEffectFade => _localizationService.T("Settings.Animation.Effect.Fade"),
            SettingsService.WidgetAnimationEffectSlideRight => _localizationService.T("Settings.Animation.Effect.SlideRight"),
            SettingsService.WidgetAnimationEffectSlideLeft => _localizationService.T("Settings.Animation.Effect.SlideLeft"),
            SettingsService.WidgetAnimationEffectSlideUp => _localizationService.T("Settings.Animation.Effect.SlideUp"),
            SettingsService.WidgetAnimationEffectSlideDown => _localizationService.T("Settings.Animation.Effect.SlideDown"),
            SettingsService.WidgetAnimationEffectScaleFade => _localizationService.T("Settings.Animation.Effect.ScaleFade"),
            SettingsService.WidgetAnimationEffectZoom => _localizationService.T("Settings.Animation.Effect.Zoom"),
            SettingsService.WidgetAnimationEffectSlideUpFade => _localizationService.T("Settings.Animation.Effect.SlideUpFade"),
            SettingsService.WidgetAnimationEffectSlideDownFade => _localizationService.T("Settings.Animation.Effect.SlideDownFade"),
            SettingsService.WidgetAnimationEffectSlideLeftFade => _localizationService.T("Settings.Animation.Effect.SlideLeftFade"),
            SettingsService.WidgetAnimationEffectSlideRightFade => _localizationService.T("Settings.Animation.Effect.SlideRightFade"),
            SettingsService.WidgetAnimationEffectScaleSlide => _localizationService.T("Settings.Animation.Effect.ScaleSlide"),
            _ => _localizationService.T("Settings.Animation.Effect.SlideFade")
        };
    }

    public string GetWidgetAnimationSpeedDisplayName(string speed)
    {
        return NormalizeWidgetAnimationSpeed(speed) switch
        {
            SettingsService.WidgetAnimationSpeedVeryFast => _localizationService.T("Settings.Animation.Speed.VeryFast"),
            SettingsService.WidgetAnimationSpeedFast => _localizationService.T("Settings.Animation.Speed.Fast"),
            SettingsService.WidgetAnimationSpeedRelaxed => _localizationService.T("Settings.Animation.Speed.Relaxed"),
            SettingsService.WidgetAnimationSpeedSlow => _localizationService.T("Settings.Animation.Speed.Slow"),
            _ => _localizationService.T("Settings.Animation.Speed.Standard")
        };
    }

    public string GetWidgetAnimationSlideDirectionDisplayName(string direction)
    {
        return NormalizeWidgetAnimationSlideDirection(direction) switch
        {
            SettingsService.WidgetAnimationSlideDirectionLeft => _localizationService.T("Settings.Animation.Direction.Left"),
            SettingsService.WidgetAnimationSlideDirectionRight => _localizationService.T("Settings.Animation.Direction.Right"),
            SettingsService.WidgetAnimationSlideDirectionUp => _localizationService.T("Settings.Animation.Direction.Up"),
            SettingsService.WidgetAnimationSlideDirectionDown => _localizationService.T("Settings.Animation.Direction.Down"),
            _ => _localizationService.T("Settings.Animation.Direction.None")
        };
    }

    public string GetWidgetAnimationEasingIntensityDisplayName(string intensity)
    {
        return NormalizeWidgetAnimationEasingIntensity(intensity) switch
        {
            SettingsService.WidgetAnimationEasingLight => _localizationService.T("Settings.Animation.Easing.Light"),
            SettingsService.WidgetAnimationEasingStandard => _localizationService.T("Settings.Animation.Easing.Standard"),
            SettingsService.WidgetAnimationEasingStrong => _localizationService.T("Settings.Animation.Easing.Strong"),
            _ => _localizationService.T("Settings.Animation.Easing.None")
        };
    }

    public string GetWidgetChromeModeDisplayName(string mode)
    {
        return NormalizeWidgetChromeModeSetting(mode, WidgetChromeMode.Standard) switch
        {
            SettingsService.WidgetChromeModeCompact => _localizationService.T("Settings.WidgetChrome.Compact"),
            SettingsService.WidgetChromeModeOverlay => _localizationService.T("Settings.WidgetChrome.Overlay"),
            SettingsService.WidgetChromeModeHidden => _localizationService.T("Settings.WidgetChrome.Hidden"),
            _ => _localizationService.T("Settings.WidgetChrome.Standard")
        };
    }

    public string GetWidgetTitleIconModeDisplayName(string mode)
    {
        return NormalizeWidgetTitleIconModeSetting(mode) switch
        {
            SettingsService.WidgetTitleIconModeLineMono => _localizationService.T("Settings.WidgetTitleIcon.LineMono"),
            SettingsService.WidgetTitleIconModeColor => _localizationService.T("Settings.WidgetTitleIcon.Color"),
            SettingsService.WidgetTitleIconModeHidden => _localizationService.T("Settings.WidgetTitleIcon.Hidden"),
            SettingsService.WidgetTitleIconModeTextLabel => _localizationService.T("Settings.WidgetTitleIcon.TextLabel"),
            _ => _localizationService.T("Settings.WidgetTitleIcon.FilledMono")
        };
    }

    public string GetHoverButtonActionDisplayName(string action)
    {
        return action switch
        {
            SettingsService.WidgetHoverActionLockPosition => _localizationService.T("Settings.HoverButtonActions.LockPosition"),
            SettingsService.WidgetHoverActionLockSize => _localizationService.T("Settings.HoverButtonActions.LockSize"),
            SettingsService.WidgetHoverActionAdd => _localizationService.T("Settings.HoverButtonActions.Add"),
            SettingsService.WidgetHoverActionMore => _localizationService.T("Settings.HoverButtonActions.More"),
            SettingsService.WidgetHoverActionDelete => _localizationService.T("Settings.HoverButtonActions.Delete"),
            _ => action
        };
    }

    public string GetWidgetLayerModeDisplayName(string mode)
    {
        return SettingsService.NormalizeWidgetLayerModeSetting(mode) switch
        {
            SettingsService.WidgetLayerModeDesktopPinned => _localizationService.T("Settings.WidgetLayerMode.DesktopPinned"),
            _ => _localizationService.T("Settings.WidgetLayerMode.Dynamic")
        };
    }

    public string GetQuickCaptureDefaultViewDisplayName(string view)
    {
        return NormalizeQuickCaptureDefaultView(view) switch
        {
            SettingsService.QuickCaptureDefaultViewPinned => _localizationService.T("Settings.QuickCapture.DefaultView.Pinned"),
            SettingsService.QuickCaptureDefaultViewRecent => _localizationService.T("Settings.QuickCapture.DefaultView.Recent"),
            _ => _localizationService.T("Settings.QuickCapture.DefaultView.Records")
        };
    }

    public string GetWidgetTabStyleDisplayName(string style)
    {
        return SettingsService.NormalizeWidgetTabStyle(style) switch
        {
            SettingsService.WidgetTabStyleButton => _localizationService.T("Settings.WidgetTabStyle.Button"),
            _ => _localizationService.T("Settings.WidgetTabStyle.Pivot")
        };
    }

    public string GetTodoNewTaskPositionDisplayName(string position)
    {
        return NormalizeTodoNewTaskPosition(position) switch
        {
            SettingsService.TodoNewTaskPositionBottom => _localizationService.T("Settings.Todo.NewTaskPosition.Bottom"),
            _ => _localizationService.T("Settings.Todo.NewTaskPosition.Top")
        };
    }

    public string GetTodoDefaultFilterDisplayName(string filter)
    {
        return NormalizeTodoDefaultFilter(filter) switch
        {
            SettingsService.TodoDefaultFilterActive => _localizationService.T("Settings.Todo.DefaultFilter.Active"),
            SettingsService.TodoDefaultFilterToday => _localizationService.T("Settings.Todo.DefaultFilter.Today"),
            SettingsService.TodoDefaultFilterThisWeek => _localizationService.T("Settings.Todo.DefaultFilter.ThisWeek"),
            SettingsService.TodoDefaultFilterThisMonth => _localizationService.T("Settings.Todo.DefaultFilter.ThisMonth"),
            SettingsService.TodoDefaultFilterImportant => _localizationService.T("Settings.Todo.DefaultFilter.Important"),
            SettingsService.TodoDefaultFilterCompleted => _localizationService.T("Settings.Todo.DefaultFilter.Completed"),
            _ => _localizationService.T("Settings.Todo.DefaultFilter.All")
        };
    }

    public string GetTodoReminderOffsetDisplayName(int minutes)
    {
        return SettingsService.NormalizeTodoReminderOffsetMinutes(minutes) switch
        {
            0 => _localizationService.T("Settings.Todo.ReminderOffset.AtDueTime"),
            60 => _localizationService.T("Settings.Todo.ReminderOffset.OneHour"),
            1440 => _localizationService.T("Settings.Todo.ReminderOffset.OneDay"),
            var value => _localizationService.Format("Settings.Todo.ReminderOffset.Minutes", value)
        };
    }

    public string GetMusicDisplayModeDisplayName(string mode)
    {
        return SettingsService.NormalizeMusicDisplayMode(mode) switch
        {
            SettingsService.MusicDisplayModeCover => _localizationService.T("Settings.Music.DisplayMode.Cover"),
            SettingsService.MusicDisplayModeControls => _localizationService.T("Settings.Music.DisplayMode.Controls"),
            _ => _localizationService.T("Settings.Music.DisplayMode.Auto")
        };
    }

    public string GetLanguageDisplayName(string language)
    {
        return _localizationService.GetLanguageDisplayName(language);
    }
}
