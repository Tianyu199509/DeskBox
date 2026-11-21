#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using DeskBox.Controls.WidgetContents;
using DeskBox.ViewModels;
using DeskBox.Views;

namespace DeskBox.Services;

public sealed partial class WidgetManager
{
    internal async Task<AotWeatherSurfaceHost> GetAotWeatherSurfaceHostAsync()
    {
        if (!_contentWidgets.TryGetValue(
                AotWeatherSurfaceFixture.OwnedWidgetId,
                out ContentWidgetWindow? window))
        {
            throw new InvalidOperationException(
                "The owned Weather surface host is unavailable.");
        }

        await window.ContentReadyTask;
        if (window.CurrentContent is WeatherWidgetContentAdapter adapter &&
            adapter.View is WeatherWidgetContent surface)
        {
            return new AotWeatherSurfaceHost(
                window,
                surface,
                adapter.ViewModel,
                window.WindowHandle.ToInt64(),
                window.WindowContentRoot?.XamlRoot is not null,
                window.Visible);
        }

        throw new InvalidOperationException(
            "The owned Weather surface host has the wrong content.");
    }

    internal async Task<AotWeatherCompactSurfaceSnapshot>
        CaptureAotWeatherCompactSurfaceAsync(
            AotWeatherSurfaceHost host,
            bool expectWeekView,
            string expectedTemperatureText,
            string expectedWindValueText,
            bool expectRichSkin)
    {
        AotPersistenceSmokePhysicalBounds baseline =
            host.Window.CaptureAotPersistenceSmokeBounds();
        if (host.Surface.ActualWidth <= 0)
        {
            throw new InvalidOperationException(
                "The real Weather surface width is unavailable.");
        }

        const double compactLogicalWidth = 205;
        double physicalScale = baseline.Width / host.Surface.ActualWidth;
        int compactPhysicalWidth = Math.Max(
            (int)SettingsService.MinWidgetWidth,
            (int)Math.Round(compactLogicalWidth * physicalScale));
        var compactBounds = new AotPersistenceSmokePhysicalBounds(
            baseline.X,
            baseline.Y,
            compactPhysicalWidth,
            baseline.Height);

        try
        {
            host.Window.ApplyAotPersistenceSmokeBounds(compactBounds);
            return await host.Surface.WaitForAotWeatherCompactSurfaceAsync(
                expectedTemperatureText,
                expectedWindValueText,
                expectRichSkin);
        }
        finally
        {
            host.Window.ApplyAotPersistenceSmokeBounds(baseline);
            await host.Surface.WaitForAotWeatherSurfaceAsync(
                expectWeekView,
                expectedTemperatureText,
                expectedWindValueText,
                expectRichSkin);

            AotPersistenceSmokePhysicalBounds restored =
                host.Window.CaptureAotPersistenceSmokeBounds();
            if (restored != baseline)
            {
                throw new InvalidOperationException(
                    $"The Weather compact probe did not restore its window bounds. " +
                    $"Expected={baseline}, Actual={restored}");
            }
        }
    }
}

internal sealed record AotWeatherSurfaceHost(
    ContentWidgetWindow Window,
    WeatherWidgetContent Surface,
    WeatherWidgetViewModel ViewModel,
    long WindowHandle,
    bool HasXamlRoot,
    bool Visible);
#endif
