#if DESKBOX_NATIVE_AOT
namespace DeskBox.ViewModels;

// Top-level WeatherWidgetContent bindings use compiled {x:Bind}. Retain only
// the properties consumed by forecast DataTemplates and shared runtime bindings.
[WinRT.GeneratedBindableCustomProperty([
    nameof(ErrorBody),
    nameof(ErrorRetryText),
    nameof(ErrorTitle),
    nameof(ErrorVisibility),
    nameof(ExpandedHourlyCardHeight),
    nameof(ExpandedHourlyPrecipVisibility),
    nameof(ForecastEmojiSize),
    nameof(ForecastHourTextSize),
    nameof(ForecastTempTextSize),
    nameof(HourlyCardWidth),
    nameof(LoadingVisibility),
    nameof(PrecipitationText),
    nameof(TitleTextSize),
    nameof(WeekDayLabelTextSize),
    nameof(WeekEmojiSize),
    nameof(WeekTempMaxSize),
    nameof(WeekTempMinSize),
    nameof(WidgetCornerRadius)
], [])]
public sealed partial class WeatherWidgetViewModel
{
}

[WinRT.GeneratedBindableCustomProperty([
    nameof(DayLabel),
    nameof(Description),
    nameof(Icon),
    nameof(IconGlyph),
    nameof(PrecipitationText),
    nameof(TempBarOffset),
    nameof(TempBarWidth),
    nameof(TempMaxText),
    nameof(TempMinText)
], [])]
public sealed partial class WeatherDayViewModel
{
}

[WinRT.GeneratedBindableCustomProperty([
    nameof(ForecastHourTextSize),
    nameof(HourLabel),
    nameof(Icon),
    nameof(IconGlyph),
    nameof(IsCurrentHour),
    nameof(PrecipitationText),
    nameof(TemperatureText)
], [])]
public sealed partial class WeatherHourViewModel
{
}
#endif
