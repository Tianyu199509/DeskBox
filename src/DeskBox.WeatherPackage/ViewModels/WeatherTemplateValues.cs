using Microsoft.UI.Xaml;
namespace DeskBox.WeatherPackage.ViewModels;

public sealed partial class WeatherDayViewModel
{
    public Thickness TempBarMargin => new(TempBarOffset * 72, 0, Math.Max(0, 1 - TempBarOffset - TempBarWidth) * 72, 0);
}
public sealed partial class WeatherHourViewModel
{
    public Visibility CurrentHourVisibility => IsCurrentHour ? Visibility.Visible : Visibility.Collapsed;
}
