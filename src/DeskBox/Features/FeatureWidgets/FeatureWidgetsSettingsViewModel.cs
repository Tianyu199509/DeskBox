using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Features.FeatureWidgets;

/// <summary>
/// Feature-section editor seam. The legacy settings shell keeps every
/// XAML/AOT binding, the summary projections, the feature-card list and the
/// host-side WidgetManager sync chains; this editor is the forwarding target
/// over <see cref="IFeatureWidgetsSettings"/> and owns no duplicated state.
/// All persistence rules (normalization through the shared normalizers,
/// unchanged-write skip, the debounced save) live in the coordinator.
/// </summary>
public sealed class FeatureWidgetsSettingsViewModel
{
    private readonly IFeatureWidgetsSettings _settings;

    public FeatureWidgetsSettingsViewModel(IFeatureWidgetsSettings settings)
    {
        _settings = settings;
    }

    public void SetFeatureWidgetEnabled(WidgetKind kind, bool enabled) =>
        _settings.SetFeatureWidgetEnabled(kind, enabled);

    public bool SetMusicDisplayMode(string? mode) =>
        _settings.SetMusicDisplayMode(mode);

    public bool SetMusicUseArtworkBackdrop(bool value) =>
        _settings.SetMusicUseArtworkBackdrop(value);

    public bool SetMusicEnableCoverHoverMotion(bool value) =>
        _settings.SetMusicEnableCoverHoverMotion(value);

    public void ResetMusicPresentationPreferences(bool scheduleSave = true) =>
        _settings.ResetMusicPresentationPreferences(scheduleSave);

    public bool SetWeatherTemperatureUnit(string? unit) =>
        _settings.SetWeatherTemperatureUnit(unit);

    public bool SetWeatherWindSpeedUnit(string? unit) =>
        _settings.SetWeatherWindSpeedUnit(unit);

    public bool SetWeatherDefaultView(string? view) =>
        _settings.SetWeatherDefaultView(view);

    public bool SetWeatherSkin(string? skin) =>
        _settings.SetWeatherSkin(skin);

    public bool SetWeatherDataSource(string? source) =>
        _settings.SetWeatherDataSource(source);

    public bool SetWeatherRefreshInterval(int minutes) =>
        _settings.SetWeatherRefreshInterval(minutes);

    public bool SetWeatherAutoLocation(bool enabled) =>
        _settings.SetWeatherAutoLocation(enabled);

    public bool TrySetWeatherManualLocation(
        string cityName,
        double latitude,
        double longitude) =>
        _settings.TrySetWeatherManualLocation(cityName, latitude, longitude);

    public bool SetWeatherDisplayOption(string option, bool enabled) =>
        _settings.SetWeatherDisplayOption(option, enabled);

    public void ResetWeatherPreferences(bool scheduleSave = true) =>
        _settings.ResetWeatherPreferences(scheduleSave);

    public bool SetAttachmentStorageMode(string? mode) =>
        _settings.SetAttachmentStorageMode(mode);

    public bool SetManagedDropAction(string? action) =>
        _settings.SetManagedDropAction(action);

    public bool SetFileWidgetFolderOpenBehavior(string? behavior) =>
        _settings.SetFileWidgetFolderOpenBehavior(behavior);
}
