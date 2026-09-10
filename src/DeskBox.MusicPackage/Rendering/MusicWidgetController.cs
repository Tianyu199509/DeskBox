using System.ComponentModel;
using System.Text.Json;
using DeskBox.MusicPackage.Models;
using DeskBox.MusicPackage.Services;
using DeskBox.MusicPackage.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.MusicPackage.Rendering;

internal sealed class MusicWidgetController : IDisposable
{
    private readonly string _packageRoot, _dataRoot;
    private readonly MusicEnvironmentContext _environment = new();
    private readonly MusicStrings _strings = new();
    private readonly MusicWidgetViewModel _model;
    private readonly MusicWidgetContent _content;
    private bool _disposed, _visible;
    internal string InstanceId { get; }
    internal FrameworkElement View => _content;
    internal MusicWidgetController(string packageRoot, string instanceId, string dataRoot)
    {
        _packageRoot = packageRoot; _dataRoot = dataRoot; InstanceId = instanceId;
        ReloadEnvironment();
        _model = new(new MusicInstance { Id = instanceId }, new MusicSessionService(), _strings, _environment);
        try { _content = new(packageRoot, _model); }
        catch { _model.Dispose(); throw; }
        _content.Loaded += OnLoaded;
        _content.Unloaded += OnUnloaded;
        _model.PropertyChanged += OnPropertyChanged;
        ApplyVisuals();
    }
    internal void Start() => _ = InitializeAsync();
    private async Task InitializeAsync()
    {
        try
        {
            await _model.InitializeAsync();
            if (_disposed) return;
            string? saved = PackageFileStore.TryReadText(Path.Combine(_dataRoot, "music-state.json"));
            if (saved is not null)
            {
                using var document = JsonDocument.Parse(saved);
                if (document.RootElement.TryGetProperty("preferredSessionId", out var v) && v.ValueKind == JsonValueKind.String)
                    await _model.SelectSessionAsync(v.GetString());
            }
            if (!_disposed) PackageLog.Write($"[MusicPackage] media ready instance={InstanceId} sessions={_model.SessionIds.Count}");
        }
        catch (Exception error) { PackageLog.Write($"[MusicPackage] media initialization: {error}"); }
    }
    private void ReloadEnvironment()
    {
        string? json = PackageFileStore.TryReadText(Path.Combine(_dataRoot, "music-settings.json"));
        if (json is not null)
            try { _environment.Apply(MusicEnvironmentContext.Parse(json)); }
            catch (Exception error) { PackageLog.Write($"[MusicPackage] settings rejected: {error.Message}"); }
        string locale = HostConfig.ReadLocale() ?? _environment.Settings.Locale;
        _strings.Configure(locale, _packageRoot);
    }
    internal void ReloadSettings()
    {
        if (_disposed) return;
        ReloadEnvironment();
        ApplyVisuals();
        PackageLog.Write($"[MusicPackage] settings applied instance={InstanceId} mode={_environment.Settings.Music.DisplayMode}");
    }
    private void ApplyVisuals()
    {
        _content.RequestedTheme = _environment.Settings.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        _model.ApplyAppearance();
        _content.ApplyPerformanceSettings();
    }
    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        using var ui = MusicUiContext.Enter();
        if (_disposed) return;
        _model.OnWindowVisibilityChanged(_visible);
        _content.OnWindowVisibilityChanged(_visible);
        if (_visible) _model.OnWindowRevealCompleted();
    }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        using var ui = MusicUiContext.Enter();
        _model.OnWindowVisibilityChanged(false);
        _content.OnWindowVisibilityChanged(false);
    }
    internal void OnLifecycleEvent(uint kind, double width, double height, uint flags)
    {
        if (_disposed) return;
        switch (kind)
        {
            case 1: ReloadSettings(); _ = _model.RefreshAsync(); break;
            case 2: case 10: ReloadSettings(); break;
            case 3: _model.OnActivated(); break;
            case 4: _model.OnDeactivated(); break;
            case 5:
                _visible = (flags & 1) != 0;
                _model.OnWindowVisibilityChanged(_visible); _content.OnWindowVisibilityChanged(_visible); break;
            case 6: _model.OnWindowRevealCompleted(); break;
            case 7: _model.OnWindowVisibilityChanged(false); _content.OnWindowVisibilityChanged(false); break;
            case 8:
                bool compact = (flags & 1) != 0;
                _model.OnCompactStateChanged(compact); _content.OnCompactStateChanged(compact); break;
            case 9: break; // actual WinUI SizeChanged drives the preserved responsive layouts
            case 11: case 13: _content.BeginResponsiveLayoutTransition(width, height, (flags & 1) != 0); break;
            case 12: case 14: _content.CompleteResponsiveLayoutTransition(width, height); break;
            case 15: _content.CancelResponsiveLayoutTransition(); break;
        }
    }
    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MusicWidgetViewModel.PreferredSessionId) || _disposed) return;
        try
        {
            PackageFileStore.WriteAtomically(Path.Combine(_dataRoot, "music-state.json"), writer =>
            {
                writer.WriteStartObject(); writer.WriteString("preferredSessionId", _model.PreferredSessionId); writer.WriteEndObject();
            });
        }
        catch (Exception error) { PackageLog.Write($"[MusicPackage] state save: {error.Message}"); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _content.Loaded -= OnLoaded; _content.Unloaded -= OnUnloaded;
        _model.PropertyChanged -= OnPropertyChanged;
        try { _model.Dispose(); } catch (Exception error) { PackageLog.Write(error.ToString()); }
        try { _content.Dispose(); } catch (Exception error) { PackageLog.Write(error.ToString()); }
    }
}
