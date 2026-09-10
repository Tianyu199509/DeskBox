using System.Text.Json;
using DeskBox.WeatherPackage.Models;
using DeskBox.WeatherPackage.Services;
using DeskBox.WeatherPackage.ViewModels;
using Microsoft.UI.Xaml;

namespace DeskBox.WeatherPackage.Rendering;

internal sealed class WeatherWidgetController : IDisposable
{
    private readonly WeatherPackageSession _session;
    private readonly string _instanceRoot;
    private readonly string _instanceId;
    private readonly WeatherSettingsContext _settings;
    private readonly WeatherInstanceConfig _instance;
    private string _lastSnapshot = string.Empty;
    private bool _disposed;
    private bool _visible;
    private bool _revealed;
    internal WeatherWidgetContent View { get; }
    internal WeatherWidgetViewModel ViewModel { get; }

    internal WeatherWidgetController(WeatherPackageSession session, string instanceId, string instanceRoot)
    {
        _session = session;
        _instanceId = instanceId;
        _instanceRoot = instanceRoot;
        var initial = ReadSnapshot();
        PackageEnvironment.Apply(initial.Environment);
        _instance = initial.Instance;
        _instance.Id = instanceId;
        _settings = new WeatherSettingsContext(initial.Settings, json => session.Host.WritePatch(instanceId, json));
        ViewModel = new WeatherWidgetViewModel(_instance, session.Weather, session.Localization, _settings);
        View = new WeatherWidgetContent(ViewModel, session.PackageRoot);
        View.Loaded += Loaded;
        View.Unloaded += Unloaded;
        AddSettingsMenu();
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try { await ViewModel.InitializeAsync(); }
        catch (Exception error) { PackageLogger.Log("[WeatherPackage] initialize: " + error); }
    }

    private WeatherConfigSnapshot ReadSnapshot()
    {
        string path = Path.Combine(_instanceRoot, "weather-config.json");
        foreach (string candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                string json = File.ReadAllText(candidate);
                var value = JsonSerializer.Deserialize(json, WeatherConfigJsonContext.Default.WeatherConfigSnapshot);
                if (value is null || value.Version != 1 || value.Settings is null || value.Instance is null) continue;
                WeatherSettingsContext.Normalize(value.Settings);
                value.Instance.Metadata ??= [];
                _lastSnapshot = json;
                return value;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            { PackageLogger.Log("[WeatherPackage] configuration fallback: " + error.Message); }
        }
        return new WeatherConfigSnapshot();
    }

    internal void RefreshConfig()
    {
        if (_disposed) return;
        string previous = _lastSnapshot;
        var current = ReadSnapshot();
        if (previous != _lastSnapshot)
        {
            _instance.Name = current.Instance.Name;
            _instance.IsDefaultTitle = current.Instance.IsDefaultTitle;
            _instance.Metadata = current.Instance.Metadata;
            PackageEnvironment.Apply(current.Environment);
            _settings.LastRequestId = current.LastRequestId;
            _settings.LastWriteSucceeded = current.LastWriteSucceeded;
            _settings.ReconcileViewWrite(current.LastRequestId);
            _settings.Apply(current.Settings);
            ViewModel.ApplyInstanceViewSelection();
        }
        View.ApplyEnvironment();
    }

    internal void OnEvent(uint kind, double width, double height, uint flags)
    {
        if (_disposed) return;
        switch (kind)
        {
            case 1: RefreshConfig(); _ = ViewModel.RefreshAsync(userTriggered: true); break;
            case 2: RefreshConfig(); ViewModel.ApplyAppearance(); break;
            case 3: ViewModel.OnActivated(); break;
            case 4: ViewModel.OnDeactivated(); break;
            case 5:
                _visible = (flags & 1) != 0;
                if (!_visible) _revealed = false;
                ViewModel.OnWindowVisibilityChanged(_visible);
                break;
            case 6: _revealed = true; ViewModel.OnWindowRevealCompleted(); break;
            case 7: _visible = false; _revealed = false; ViewModel.OnWindowVisibilityChanged(false); break;
            case 8: break; // Visibility/viewport, not focus, govern forecast work.
            case 9: ViewModel.UpdateAvailableSize(width, height); break;
            case 10: RefreshConfig(); View.ApplyEnvironment(); break;
            case 11: case 13: ViewModel.BeginResponsiveLayoutTransition(width, height, (flags & 1) != 0); break;
            case 12: case 14: ViewModel.CompleteResponsiveLayoutTransition(width, height); break;
            case 15: ViewModel.CancelResponsiveLayoutTransition(); break;
        }
    }

    private void Loaded(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        ViewModel.OnWindowVisibilityChanged(_visible);
        if (_revealed) ViewModel.OnWindowRevealCompleted();
    }
    private void Unloaded(object sender, RoutedEventArgs e) => ViewModel.OnWindowVisibilityChanged(false);

    private void AddSettingsMenu()
    {
        var menu = new Microsoft.UI.Xaml.Controls.MenuFlyout();
        var item = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = _session.Localization.T("Common.Settings") };
        item.Click += (_, _) => WeatherSettingsPanel.Show(View, _settings, _session.Cities, _session.Localization);
        menu.Items.Add(item);
        View.ContextFlyout = menu;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        View.Loaded -= Loaded;
        View.Unloaded -= Unloaded;
        ViewModel.Dispose();
        View.Dispose();
        View.ContextFlyout = null;
    }
}
