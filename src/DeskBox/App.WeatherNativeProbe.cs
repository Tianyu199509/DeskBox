#if DESKBOX_NATIVE_DEV_PILOT
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Services.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DeskBox;

public partial class App
{
    private void StartWeatherNativeProbe()
    {
        if (Environment.GetEnvironmentVariable("DESKBOX_WEATHER_NATIVE_PROBE") != "1" ||
            !DeskBoxDataPathService.Current.IsDevelopmentRoot) return;
        _ = RunWeatherNativeProbeAsync();
    }

    private async Task RunWeatherNativeProbeAsync()
    {
        string root = Path.Combine(DeskBoxDataPathService.Current.RootPath, "probe", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(root);
        Log("[WeatherNativeProbe] output=" + root);
        try
        {
            await Task.Delay(2500);
            var window = WidgetManager!.ContentWidgets.Values.Single(w => w.Config.WidgetKind == WidgetKind.Weather);
            string id = window.Config.Id;
            if (window.CurrentContent is not NativeWidgetPilotContent content) throw new InvalidOperationException("Weather fell back to the built-in provider.");
            var switcher = Descendants(content.View).OfType<RadioButtons>().Single();
            switcher.SelectedIndex = 0;
            await Task.Delay(800);
            await CaptureAsync(content.View, "initial");

            string originalTemperatureUnit =
                SettingsService.Settings.WeatherTemperatureUnit;
            try
            {
                SettingsService.Settings.WeatherTemperatureUnit =
                    SettingsService.WeatherTemperatureUnitFahrenheit;
                if (!await SettingsService.SaveCheckedAsync())
                {
                    throw new InvalidOperationException(
                        "Host Weather temperature-unit setting could not be saved.");
                }
                await WaitForVisibleTextAsync(content.View, "°F");
                await CaptureAsync(content.View, "fahrenheit");
            }
            finally
            {
                SettingsService.Settings.WeatherTemperatureUnit =
                    originalTemperatureUnit;
                await SettingsService.SaveCheckedAsync();
                await WaitForVisibleTextAsync(content.View, "°C");
            }

            switcher.SelectedIndex = 1;
            await Task.Delay(1000);
            if (!WeatherWidgetViewModeSettings.TryGetWeekView(SettingsService.Settings.Widgets.Single(w => w.Id == id), out bool week) || !week)
                throw new InvalidOperationException("Native view selection did not reach host persistence.");
            await CaptureAsync(content.View, "week");

            // Native package contract: a second instance can be destroyed while
            // the original stays live. It has no visible host window.
            var manager = new PluginPackageManager(Path.Combine(DeskBoxDataPathService.Current.DataDirectory, "plugins"));
            var handle = manager.TryCreateNativeHandle("deskbox.weather") ?? throw new InvalidOperationException("No verified Weather package handle.");
            if (!NativeWidgetRuntimeManager.TryCreateFromInstalled(handle, "weather", "weather-probe-secondary",
                    DeskBoxDataPathService.Current.DataDirectory, out var second)) throw new InvalidOperationException("Second native instance creation failed.");
            ((IDisposable)second!).Dispose();
            await CaptureAsync(content.View, "after-second-destroy");

            await WidgetManager.SetFeatureWidgetEnabledAsync(WidgetKind.Weather, enabled: false, reveal: false);
            await Task.Delay(250);
            await WidgetManager.SetFeatureWidgetEnabledAsync(WidgetKind.Weather, enabled: true, reveal: true);
            await Task.Delay(1200);
            var restored = WidgetManager.ContentWidgets.Values.Single(w => w.Config.WidgetKind == WidgetKind.Weather);
            if (restored.CurrentContent is not NativeWidgetPilotContent restoredContent || restored.Config.Id != id)
                throw new InvalidOperationException("Native instance identity was not restored.");
            await CaptureAsync(restoredContent.View, "restored");
            File.WriteAllText(
                Path.Combine(root, "result.txt"),
                "PASS: verified native view, host setting sync, today/week input, " +
                "persisted view mode, second-instance teardown, disable/re-enable recovery");
            Log("[WeatherNativeProbe] PASS");
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(root, "error.txt"), error.ToString());
            Log("[WeatherNativeProbe] FAIL " + error);
        }

        async Task CaptureAsync(FrameworkElement view, string name)
        {
            var nodes = Descendants(view).ToArray();
            var labels = nodes.OfType<TextBlock>().Where(t => !string.IsNullOrWhiteSpace(t.Text)).Select(t => t.Text).Distinct().ToArray();
            if (labels.Length < 3) throw new InvalidOperationException("The native XAML has no populated text content.");
            using (var stream = File.Create(Path.Combine(root, name + ".json")))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject(); writer.WriteNumber("width", view.ActualWidth); writer.WriteNumber("height", view.ActualHeight);
                writer.WritePropertyName("text"); writer.WriteStartArray(); foreach (string label in labels) writer.WriteStringValue(label); writer.WriteEndArray();
                writer.WritePropertyName("items"); writer.WriteStartArray();
                foreach (var control in nodes.OfType<ItemsControl>())
                {
                    writer.WriteStartObject(); writer.WriteString("name", control.Name); writer.WriteNumber("count", control.Items.Count);
                    writer.WriteNumber("width", control.ActualWidth); writer.WriteNumber("height", control.ActualHeight); writer.WriteString("visibility", control.Visibility.ToString()); writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            bool week = nodes.OfType<RadioButtons>().Single().SelectedIndex == 1;
            string expectedList = week
                ? "ExpandedDailyItems"
                : "ExpandedHourlyItems";
            var visibleItems = nodes
                .OfType<ItemsControl>()
                .SingleOrDefault(control => control.Name == expectedList);
            int expectedMinimum = week ? 7 : 1;
            if (visibleItems is null || visibleItems.Items.Count < expectedMinimum)
            {
                throw new InvalidOperationException(
                    $"Forecast data did not reach the visible WinUI list: " +
                    $"week={week}, name={expectedList}, count={visibleItems?.Items.Count}.");
            }
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(view);
            var pixels = await bitmap.GetPixelsAsync();
            using var reader = DataReader.FromBuffer(pixels);
            byte[] bytes = new byte[pixels.Length]; reader.ReadBytes(bytes);
            StorageFile file = await StorageFile.GetFileFromPathAsync(CreateImageFile(Path.Combine(root, name + ".png")));
            using var output = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
            await encoder.FlushAsync();
        }

        static async Task WaitForVisibleTextAsync(
            FrameworkElement view,
            string expected)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            do
            {
                bool found = Descendants(view)
                    .OfType<TextBlock>()
                    .Any(text => text.Visibility == Visibility.Visible &&
                        text.Text.Contains(expected, StringComparison.Ordinal));
                if (found)
                {
                    return;
                }
                await Task.Delay(100);
            }
            while (DateTime.UtcNow < deadline);

            throw new InvalidOperationException(
                $"Native Weather did not consume the saved host setting: {expected}.");
        }
    }

    private static string CreateImageFile(string path) { using var file = File.Create(path); return path; }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
#endif
