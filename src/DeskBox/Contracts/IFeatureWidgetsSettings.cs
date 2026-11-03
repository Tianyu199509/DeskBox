using DeskBox.Models;

namespace DeskBox.Contracts;

/// <summary>
/// One owner for the feature-section settings-page writes: the music and
/// weather presentation options, the weather location policy, the feature-card
/// enable states and the section's misc presentation picks (attachment
/// storage, managed-drop action, folder-open behavior). Every write follows
/// the original section semantics — normalize through the existing shared
/// normalizers (including <c>WeatherSettingsPolicy</c>), skip unchanged
/// writes, store, and schedule one debounced save. Widget creation, hiding
/// and runtime start/stop stay on the existing WidgetManager /
/// SettingsChanged chains owned by the host; the enable port only writes the
/// persisted flag, exactly like the shell did before the migration.
/// </summary>
/// <summary>
/// Canonical music display-mode values, owned here so the feature editor can
/// build its option list without referencing the settings adapter.
/// <see cref="Services.SettingsService"/> keeps its historical constants as
/// aliases of these.
/// </summary>
public static class MusicDisplayModes
{
    public const string Auto = "Auto";
    public const string Cover = "Cover";
    public const string Controls = "Controls";
    public const string RecordVertical = "RecordVertical";
    public const string RecordHorizontal = "RecordHorizontal";
}

/// <summary>
/// Immutable read snapshot of the music presentation preferences, mirroring
/// the Quick Capture editor's read-port shape. The music settings editor
/// binds its XAML surface to a projection of this snapshot; external refresh
/// paths (settings broadcasts, default restores) re-read it. The display
/// mode arrives already normalized.
/// </summary>
public sealed record MusicPresentationSettings(
    bool UseArtworkBackdrop,
    bool EnableCoverHoverMotion,
    string DisplayMode,
    bool HideTimelineWhenUnavailable,
    bool ShowLyrics = true,
    bool ShowCapsuleLyrics = true,
    string LocalLyricsFolder = "");

/// <summary>
/// Immutable read snapshot of the weather presentation preferences, mirroring
/// the music read-port shape. The weather settings editor binds its XAML
/// surface to a projection of this snapshot (values arrive normalized with
/// the old shell-constructor semantics); external refresh paths re-read it.
/// </summary>
public sealed record WeatherPresentationSettings(
    bool AutoLocation,
    string CityName,
    string TemperatureUnit,
    string WindSpeedUnit,
    string DefaultView,
    string Skin,
    string IconStyle,
    string DataSource,
    int RefreshIntervalMinutes,
    bool ShowForecast,
    bool ShowSunrise,
    bool ShowUvIndex,
    bool ShowPrecipitation,
    bool ShowHumidity,
    bool ShowWind,
    bool ShowPressure);

public interface IFeatureWidgetsSettings
{
    /// <summary>Reads the current music presentation snapshot (raw stored values).</summary>
    MusicPresentationSettings ReadMusicPresentation();

    /// <summary>
    /// Reads the current weather presentation snapshot, normalized with the
    /// old shell-constructor read semantics (units/view/skin/data source fall
    /// back to their defaults on unknown values, the refresh interval is
    /// clamped to its bounds).
    /// </summary>
    WeatherPresentationSettings ReadWeatherPresentation();

    /// <summary>
    /// Writes one feature card's persisted enable state (Music / Weather /
    /// Glance / later feature kinds). Does not save: the caller's existing
    /// WidgetManager sync chain owns persistence for this path, unchanged.
    /// </summary>
    void SetFeatureWidgetEnabled(WidgetKind kind, bool enabled);

    bool SetMusicDisplayMode(string? mode);

    bool SetMusicUseArtworkBackdrop(bool value);

    bool SetMusicEnableCoverHoverMotion(bool value);

    /// <summary>
    /// Writes the "collapse the progress bar and time labels when the player
    /// publishes no timeline" preference. Skips unchanged writes and schedules
    /// one debounced save, exactly like the other music presentation setters.
    /// </summary>
    bool SetMusicHideTimelineWhenUnavailable(bool value);

    bool SetMusicShowLyrics(bool value);

    bool SetMusicShowCapsuleLyrics(bool value);

    bool SetMusicLocalLyricsFolder(string? folderPath);

    /// <summary>
    /// Writes the fresh-install music presentation defaults. Used by the
    /// feature-card reset flow, which applies every feature's defaults and
    /// then performs one explicit save.
    /// </summary>
    void ResetMusicPresentationPreferences(bool scheduleSave = true);

    bool SetWeatherTemperatureUnit(string? unit);

    bool SetWeatherWindSpeedUnit(string? unit);

    bool SetWeatherDefaultView(string? view);

    bool SetWeatherSkin(string? skin);

    bool SetWeatherIconStyle(string? style);

    bool SetWeatherDataSource(string? source);

    bool SetWeatherRefreshInterval(int minutes);

    bool SetWeatherAutoLocation(bool enabled);

    /// <summary>
    /// Persists a manually selected city. Returns false when the coordinates
    /// are rejected by the shared weather policy, exactly like the previous
    /// in-shell call.
    /// </summary>
    bool TrySetWeatherManualLocation(
        string cityName,
        double latitude,
        double longitude);

    /// <summary>
    /// Writes one weather display option toggle. The option key is the same
    /// string the settings page projects ("Forecast", "Sunrise", "UvIndex",
    /// "Precipitation", "Humidity", "Wind", "Pressure").
    /// </summary>
    bool SetWeatherDisplayOption(string option, bool enabled);

    /// <summary>
    /// Writes the fresh-install weather defaults (location, units, view,
    /// skin, all display toggles and the refresh interval). Used by the
    /// feature-card reset flow's single explicit save.
    /// </summary>
    void ResetWeatherPreferences(bool scheduleSave = true);

    bool SetAttachmentStorageMode(string? mode);

    bool SetManagedDropAction(string? action);

    bool SetFileWidgetFolderOpenBehavior(string? behavior);

    /// <summary>
    /// Reads the persisted global folder-open behavior, normalized through
    /// <see cref="FileWidgetFolderOpenBehaviors"/> (batch 45 read port for
    /// the file-widget overview combo).
    /// </summary>
    string ReadFileWidgetFolderOpenBehavior();

    /// <summary>
    /// Reads the persisted attachment storage mode, normalized through
    /// <see cref="AttachmentStorageModes"/> (batch 50 read port for the
    /// General section's attachment-storage combo, whose binding surface
    /// lives on the feature-widgets editor).
    /// </summary>
    string ReadAttachmentStorageMode();
}
