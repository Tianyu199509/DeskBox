#if DESKBOX_NATIVE_AOT
namespace DeskBox.ViewModels;

// WeatherWidgetContent keeps its established runtime Binding surface. Expose
// only the properties consumed by that surface in NativeAOT builds.
[WinRT.GeneratedBindableCustomProperty([
    nameof(ApparentTemperatureText),
    nameof(BodyTextSize),
    nameof(CaptionTextSize),
    nameof(CompactLayoutVisibility),
    nameof(CurrentDescription),
    nameof(CurrentEmoji),
    nameof(CurrentEmojiSize),
    nameof(CurrentTemperatureText),
    nameof(DailyForecastItemsSource),
    nameof(DayViewText),
    nameof(ExpandedHourlyCardHeight),
    nameof(ExpandedHourlyPrecipVisibility),
    nameof(ExpandedLayoutVisibility),
    nameof(ExpandedSecondaryMetricsVisibility),
    nameof(ExpandedSunriseVisibility),
    nameof(ForecastEmojiSize),
    nameof(ForecastTempTextSize),
    nameof(ForecastVisibility),
    nameof(HourlyCardWidth),
    nameof(HourlyForecastItemsSource),
    nameof(HumidityLabel),
    nameof(HumidityText),
    nameof(HumidityValueText),
    nameof(HumidityVisibility),
    nameof(LoadingText),
    nameof(LoadingVisibility),
    nameof(LocationDisplay),
    nameof(LocationFallbackTooltip),
    nameof(LocationFallbackVisibility),
    nameof(MiniDescriptionVisibility),
    nameof(MiniLayoutVisibility),
    nameof(MiniLocationVisibility),
    nameof(PrecipitationLabel),
    nameof(PrecipitationText),
    nameof(PrecipitationValueText),
    nameof(PrecipitationVisibility),
    nameof(PressureLabel),
    nameof(PressureValueText),
    nameof(PressureVisibility),
    nameof(PrimaryMetricsVisibility),
    nameof(RefreshStatusText),
    nameof(RefreshTooltip),
    nameof(RichBackdropBottomColor),
    nameof(RichBackdropTopColor),
    nameof(RichSkinVisibility),
    nameof(SunriseLabel),
    nameof(SunriseText),
    nameof(SunsetLabel),
    nameof(SunsetText),
    nameof(TemperatureTextSize),
    nameof(TitleTextSize),
    nameof(TodayViewText),
    nameof(UvIndexLabel),
    nameof(UvIndexValueText),
    nameof(UvIndexVisibility),
    nameof(ViewSwitchVisibility),
    nameof(WeekDayLabelTextSize),
    nameof(WeekEmojiSize),
    nameof(WeekForecastVisibility),
    nameof(WeekTempMaxSize),
    nameof(WeekTempMinSize),
    nameof(WeekViewShortText),
    nameof(WeekViewText),
    nameof(WidgetCornerRadius),
    nameof(WindLabel),
    nameof(WindText),
    nameof(WindValueText),
    nameof(WindVisibility)
], [])]
public sealed partial class WeatherWidgetViewModel
{
}

[WinRT.GeneratedBindableCustomProperty([
    nameof(DayLabel),
    nameof(Description),
    nameof(Emoji),
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
    nameof(Emoji),
    nameof(HourLabel),
    nameof(IconGlyph),
    nameof(IsCurrentHour),
    nameof(PrecipitationText),
    nameof(TemperatureText)
], [])]
public sealed partial class WeatherHourViewModel
{
}
#endif
