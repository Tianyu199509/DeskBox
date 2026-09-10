using System.Text;
using System.Text.Json;
using DeskBox.WeatherPackage.Models;
using DeskBox.WeatherPackage.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.WeatherPackage.Rendering;

internal static class WeatherSettingsPanel
{
    internal static void Show(FrameworkElement target, WeatherSettingsContext context, CitySearchService cities, PackageLocalization text)
    {
        var panel = new StackPanel { Spacing = 12, Width = 300 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var flyout = new Flyout { Content = new ScrollViewer { Content = panel, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        bool synchronizing = false;
        bool closed = false;
        string pendingRequest = string.Empty;
        var refreshers = new List<Action>();
        CancellationTokenSource? query = null;

        bool Send(Action<Utf8JsonWriter> write)
        {
            if (synchronizing || closed) return false;
            using var buffer = new MemoryStream();
            pendingRequest = Guid.NewGuid().ToString("N");
            using (var writer = new Utf8JsonWriter(buffer)) { writer.WriteStartObject(); writer.WriteString("requestId", pendingRequest); write(writer); writer.WriteEndObject(); }
            bool accepted = context.RequestPatch(Encoding.UTF8.GetString(buffer.ToArray()));
            status.Text = text.T(accepted ? "Weather.Package.Saving" : "Weather.Package.SaveFailed");
            return accepted;
        }

        void Refresh()
        {
            if (closed) return;
            synchronizing = true;
            try { foreach (var refresh in refreshers) refresh(); }
            finally { synchronizing = false; }
            if (pendingRequest.Length > 0 && context.LastRequestId == pendingRequest)
                status.Text = text.T(context.LastWriteSucceeded ? "Weather.Package.Saved" : "Weather.Package.SaveFailed");
        }

        void Toggle(string label, string key, Func<WeatherPreferences, bool> read)
        {
            var control = new ToggleSwitch { Header = text.T(label), IsOn = read(context.Settings) };
            control.Toggled += (_, _) =>
            {
                if (!synchronizing && !Send(w => w.WriteBoolean(key, control.IsOn)))
                {
                    Refresh();
                }
            };
            refreshers.Add(() => control.IsOn = read(context.Settings));
            panel.Children.Add(control);
        }
        void Choice(string label, string key, string[] values, Func<WeatherPreferences, string> read)
        {
            var control = new ComboBox { Header = text.T(label), HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (string value in values) control.Items.Add(new ComboBoxItem { Content = value, Tag = value });
            void Update() => control.SelectedIndex = Array.IndexOf(values, read(context.Settings));
            Update();
            control.SelectionChanged += (_, _) =>
            {
                if (!synchronizing &&
                    control.SelectedItem is ComboBoxItem { Tag: string value } &&
                    !Send(w => w.WriteString(key, value)))
                {
                    Refresh();
                }
            };
            refreshers.Add(Update);
            panel.Children.Add(control);
        }
        Toggle("Settings.Weather.AutoLocation.Title", "weatherAutoLocation", s => s.WeatherAutoLocation);
        var city = new AutoSuggestBox { Header = text.T("Settings.Weather.CityName.Title"), Text = context.Settings.WeatherCityName, DisplayMemberPath = "DisplayName" };
        city.TextChanged += async (_, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput || closed) return;
            query?.Cancel();
            var pending = new CancellationTokenSource();
            query = pending;
            try
            {
                await Task.Delay(180, pending.Token);
                var results = await cities.SearchAsync(city.Text, text.CurrentCultureName, cancellationToken: pending.Token);
                if (!closed && !pending.IsCancellationRequested) city.ItemsSource = results;
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { PackageLogger.Log("[WeatherPackage] city search: " + error.Message); }
            finally { if (ReferenceEquals(query, pending)) query = null; pending.Dispose(); }
        };
        city.SuggestionChosen += (_, e) =>
        {
            if (e.SelectedItem is not WeatherCitySearchResult selected) return;
            Send(w => { w.WriteBoolean("weatherAutoLocation", false); w.WriteString("weatherCityName", selected.Name); w.WriteNumber("weatherLatitude", selected.Latitude); w.WriteNumber("weatherLongitude", selected.Longitude); });
        };
        panel.Children.Add(city);
        Choice("Settings.Weather.DataSource.Title", "weatherDataSource", ["MSN", "OpenMeteo"], s => s.WeatherDataSource);
        Choice("Settings.Weather.TemperatureUnit.Title", "weatherTemperatureUnit", ["Celsius", "Fahrenheit"], s => s.WeatherTemperatureUnit);
        Choice("Settings.Weather.WindSpeedUnit.Title", "weatherWindSpeedUnit", ["kmh", "ms", "mph"], s => s.WeatherWindSpeedUnit);
        Choice("Settings.Weather.DefaultView.Title", "weatherDefaultView", ["Today", "Week"], s => s.WeatherDefaultView);
        Choice("Settings.Weather.Skin.Title", "weatherSkin", ["Standard", "Rich"], s => s.WeatherSkin);
        var interval = new NumberBox { Header = text.T("Settings.Weather.RefreshInterval.Title"), Minimum = 15, Maximum = 180, Value = context.Settings.WeatherRefreshIntervalMinutes, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        interval.ValueChanged += (_, e) =>
        {
            if (!synchronizing &&
                double.IsFinite(e.NewValue) &&
                !Send(w => w.WriteNumber(
                    "weatherRefreshIntervalMinutes",
                    (int)Math.Clamp(e.NewValue, 15, 180))))
            {
                Refresh();
            }
        };
        refreshers.Add(() => interval.Value = context.Settings.WeatherRefreshIntervalMinutes);
        panel.Children.Add(interval);
        Toggle("Settings.Weather.ShowForecast.Title", "weatherShowForecast", s => s.WeatherShowForecast);
        Toggle("Settings.Weather.ShowSunrise.Title", "weatherShowSunrise", s => s.WeatherShowSunrise);
        Toggle("Settings.Weather.ShowUvIndex.Title", "weatherShowUvIndex", s => s.WeatherShowUvIndex);
        Toggle("Settings.Weather.ShowPrecipitation.Title", "weatherShowPrecipitation", s => s.WeatherShowPrecipitation);
        Toggle("Settings.Weather.ShowHumidity.Title", "weatherShowHumidity", s => s.WeatherShowHumidity);
        Toggle("Settings.Weather.ShowWind.Title", "weatherShowWind", s => s.WeatherShowWind);
        Toggle("Settings.Weather.ShowPressure.Title", "weatherShowPressure", s => s.WeatherShowPressure);
        panel.Children.Add(status);
        context.SettingsChanged += Refresh;
        flyout.Closed += (_, _) => { closed = true; query?.Cancel(); context.SettingsChanged -= Refresh; };
        flyout.ShowAt(target);
    }
}
