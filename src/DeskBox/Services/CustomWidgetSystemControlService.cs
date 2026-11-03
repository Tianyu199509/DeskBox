using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using DeskBox.Platform;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Media.Control;
using Windows.Media.Devices;
using Windows.Storage.Streams;
using Launcher = Windows.System.Launcher;

namespace DeskBox.Services;

public sealed record CustomWidgetDeviceStateSnapshot
{
    public static CustomWidgetDeviceStateSnapshot Empty { get; } = new();

    public int VolumePercent { get; init; } = 50;
    public bool IsMuted { get; init; }
    public int BrightnessPercent { get; init; } = 80;
    public bool BrightnessSupported { get; init; }
    public string AudioDeviceName { get; init; } = string.Empty;
    public bool WifiAvailable { get; init; }
    public bool WifiEnabled { get; init; }
    public bool BluetoothAvailable { get; init; }
    public bool BluetoothEnabled { get; init; }
    public bool NetworkConnected { get; init; } = true;
    public bool KeepAwakeActive { get; init; }
    public bool IsDarkTheme { get; init; } = true;
    public bool MediaPlaying { get; init; }
    public string MediaTitle { get; init; } = string.Empty;
    public string MediaArtist { get; init; } = string.Empty;
    public string MediaCoverPath { get; init; } = string.Empty;
    public long RecycleBinItems { get; init; }

    public string ResolveToken(string path, string? cultureName = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string culture = cultureName ?? LocalizationService.ResolveAmbientCultureName();

        return path.Trim() switch
        {
            "volume" or "volumePercent" => VolumePercent.ToString(CultureInfo.InvariantCulture),
            "volumeText" => IsMuted
                ? LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.Muted")
                : $"{VolumePercent}%",
            "muted" or "isMuted" => IsMuted ? "true" : "false",
            "brightness" or "brightnessPercent" => BrightnessPercent.ToString(CultureInfo.InvariantCulture),
            "brightnessText" => BrightnessSupported
                ? $"{BrightnessPercent}%"
                : LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.BrightnessOpenSettings"),
            "audioDevice" or "audioDeviceName" => string.IsNullOrWhiteSpace(AudioDeviceName)
                ? LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.AudioDeviceDefault")
                : AudioDeviceName,
            "wifiEnabled" or "wifi" => WifiEnabled ? "true" : "false",
            "wifiStatus" => !WifiAvailable
                ? LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.WifiOpenSettings")
                : WifiEnabled
                    ? (NetworkConnected
                        ? LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.Connected")
                        : LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.On"))
                    : LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.Off"),
            "bluetoothEnabled" or "bluetooth" => BluetoothEnabled ? "true" : "false",
            "bluetoothStatus" => !BluetoothAvailable
                ? LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.BluetoothOpenSettings")
                : BluetoothEnabled
                    ? LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.On")
                    : LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.Off"),
            "networkConnected" or "online" => NetworkConnected ? "true" : "false",
            "keepAwake" or "caffeine" => KeepAwakeActive ? "true" : "false",
            "keepAwakeStatus" => KeepAwakeActive
                ? LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.KeepAwakeActive")
                : LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.KeepAwakeNormal"),
            "darkTheme" or "isDark" => IsDarkTheme ? "true" : "false",
            "themeStatus" => IsDarkTheme
                ? LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.ThemeDark")
                : LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.ThemeLight"),
            "mediaPlaying" => MediaPlaying ? "true" : "false",
            "mediaStatus" => MediaPlaying
                ? LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.MediaPlaying")
                : string.IsNullOrWhiteSpace(MediaTitle)
                    ? LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.MediaIdle")
                    : LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.MediaPaused"),
            "mediaToggleIcon" => MediaPlaying ? "\uE769" : "\uE768",
            "mediaCover" or "mediaCoverPath" => MediaCoverPath,
            "mediaTitle" => string.IsNullOrWhiteSpace(MediaTitle)
                ? LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.MediaNoTrack")
                : MediaTitle,
            "mediaArtist" => MediaArtist,
            "mediaSummary" => string.IsNullOrWhiteSpace(MediaTitle)
                ? LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.MediaNoActive")
                : (string.IsNullOrWhiteSpace(MediaArtist) ? MediaTitle : $"{MediaTitle} · {MediaArtist}"),
            "recycleBinCount" or "recycleBinItems" => RecycleBinItems.ToString(CultureInfo.InvariantCulture),
            "recycleBinStatus" => RecycleBinItems <= 0
                ? LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.RecycleBinEmpty")
                : LocalizationService.FormatForCulture(culture, "CustomWidget.SysCtrl.RecycleBinItemsFormat", RecycleBinItems),
            _ => string.Empty
        };
    }
}

public sealed record CustomWidgetSystemControlOutcome(
    bool Succeeded,
    bool UsedSettingsFallback,
    string? FeedbackMessage);

/// <summary>
/// Safe, NativeAOT-compatible system control &amp; live device state bridge for Custom Widgets
/// (<c>deviceState</c> source, <c>system.control</c>, and <c>system.openSetting</c> actions).
/// Automatically falls back to <c>ms-settings:</c> URIs when hardware/OS permissions block direct switching.
/// </summary>
public sealed class CustomWidgetSystemControlService
{
    public static CustomWidgetSystemControlService Shared { get; } = new();

    private const byte VkVolumeMute = 0xAD;
    private const byte VkMediaNextTrack = 0xB0;
    private const byte VkMediaPrevTrack = 0xB1;
    private const byte VkMediaPlayPause = 0xB3;

    private static readonly IReadOnlyDictionary<string, string> SafeSystemSettingsUris =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wifi"] = "ms-settings:network-wifi",
            ["network"] = "ms-settings:network",
            ["bluetooth"] = "ms-settings:bluetooth",
            ["hotspot"] = "ms-settings:network-mobilehotspot",
            ["airplane"] = "ms-settings:network-airplanemode",
            ["vpn"] = "ms-settings:network-vpn",
            ["sound"] = "ms-settings:sound",
            ["volume"] = "ms-settings:apps-volume",
            ["display"] = "ms-settings:display",
            ["nightlight"] = "ms-settings:nightlight",
            ["power"] = "ms-settings:powersleep",
            ["battery"] = "ms-settings:batterysaver",
            ["storage"] = "ms-settings:storagesense",
            ["clipboard"] = "ms-settings:clipboard",
            ["notifications"] = "ms-settings:notifications",
            ["personalization"] = "ms-settings:personalization",
            ["colors"] = "ms-settings:colors",
            ["apps"] = "ms-settings:appsfeatures",
            ["windowsupdate"] = "ms-settings:windowsupdate"
        };

    private readonly MusicVolumeService _volumeService = new();
    private readonly SettingsService? _settingsService;
    private volatile bool _keepAwakeActive;
    private volatile bool _softwareMutedEstimate;
    private string _cachedAudioRenderId = string.Empty;
    private string _cachedAudioDeviceName = string.Empty;
    private string _cachedMediaTrackSignature = string.Empty;
    private string _cachedMediaCoverPath = string.Empty;
    private CustomWidgetDeviceStateSnapshot _latest = CustomWidgetDeviceStateSnapshot.Empty;

    public CustomWidgetSystemControlService(SettingsService? settingsService = null)
    {
        _settingsService = settingsService;
    }

    public CustomWidgetDeviceStateSnapshot Latest => _latest;

    public bool IsKeepAwakeActive => _keepAwakeActive;

    public static bool IsSupportedSettingTarget(string? key) =>
        !string.IsNullOrWhiteSpace(key) &&
        (SafeSystemSettingsUris.ContainsKey(key.Trim()) ||
         string.Equals(key.Trim(), "taskmgr", StringComparison.OrdinalIgnoreCase));

    public async Task<CustomWidgetDeviceStateSnapshot> RefreshSnapshotAsync(CancellationToken cancellationToken = default)
    {
        double volRatio = 0.5;
        try
        {
            volRatio = await _volumeService.GetSystemMasterVolumeAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        int volPct = (int)Math.Clamp(Math.Round(volRatio * 100.0), 0, 100);
        bool brightnessOk = CustomWidgetSystemControlInterop.TryGetDisplayBrightnessPercent(out int brightnessPct);

        bool netUp = false;
        try
        {
            netUp = NetworkInterface.GetIsNetworkAvailable();
        }
        catch
        {
        }

        string audioName = await QueryDefaultAudioDeviceNameSafeAsync().ConfigureAwait(false);
        (bool wifiAvail, bool wifiOn, bool btAvail, bool btOn) = await QueryRadiosSafeAsync().ConfigureAwait(false);
        (bool mediaPlaying, string mediaTitle, string mediaArtist, string mediaCover) =
            await QueryMediaSessionSafeAsync().ConfigureAwait(false);

        long binItems = 0;
        ShellRecycleBinInterop.TryQueryTotalItemCount(out binItems);

        bool isDark = true;
        try
        {
            string? themePref = _settingsService?.Settings.Core.Theme;
            isDark = themePref switch
            {
                "Light" => false,
                "Dark" => true,
                _ => Win32Helper.IsSystemDarkMode()
            };
        }
        catch
        {
        }

        var snapshot = new CustomWidgetDeviceStateSnapshot
        {
            VolumePercent = volPct,
            IsMuted = _softwareMutedEstimate || volPct == 0,
            BrightnessPercent = brightnessOk ? brightnessPct : _latest.BrightnessPercent,
            BrightnessSupported = brightnessOk,
            AudioDeviceName = audioName,
            WifiAvailable = wifiAvail,
            WifiEnabled = wifiAvail ? wifiOn : netUp,
            BluetoothAvailable = btAvail,
            BluetoothEnabled = btOn,
            NetworkConnected = netUp,
            KeepAwakeActive = _keepAwakeActive,
            IsDarkTheme = isDark,
            MediaPlaying = mediaPlaying,
            MediaTitle = mediaTitle,
            MediaArtist = mediaArtist,
            MediaCoverPath = mediaCover,
            RecycleBinItems = Math.Max(0, binItems)
        };

        _latest = snapshot;
        return snapshot;
    }

    public async Task<CustomWidgetSystemControlOutcome> ExecuteControlAsync(
        string? command,
        string? rawValue,
        IntPtr ownerHwnd,
        string? cultureName = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return new(false, false, null);
        }

        string culture = cultureName ?? LocalizationService.ResolveAmbientCultureName();
        string cmd = command.Trim().ToLowerInvariant();

        switch (cmd)
        {
            case "volume.set":
            {
                if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
                {
                    double clamped = Math.Clamp(pct, 0, 100) / 100.0;
                    bool ok = await _volumeService.TrySetSystemMasterVolumeAsync(clamped).ConfigureAwait(false);
                    if (clamped > 0) _softwareMutedEstimate = false;
                    await RefreshSnapshotAsync().ConfigureAwait(false);
                    return new(ok, false, null);
                }
                return new(false, false, null);
            }

            case "volume.step":
            {
                double deltaPct = double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                    ? d
                    : 10;
                double cur = await _volumeService.GetSystemMasterVolumeAsync().ConfigureAwait(false);
                double next = Math.Clamp(cur + (deltaPct / 100.0), 0.0, 1.0);
                bool ok = await _volumeService.TrySetSystemMasterVolumeAsync(next).ConfigureAwait(false);
                if (next > 0) _softwareMutedEstimate = false;
                await RefreshSnapshotAsync().ConfigureAwait(false);
                return new(ok, false, $"{Math.Round(next * 100):0}%");
            }

            case "volume.mutetoggle":
            case "volume.mute":
            {
                SendVirtualKeyTap(VkVolumeMute);
                _softwareMutedEstimate = !_softwareMutedEstimate;
                await RefreshSnapshotAsync().ConfigureAwait(false);
                return new(
                    true,
                    false,
                    _softwareMutedEstimate
                        ? LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.Muted")
                        : LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.Unmuted"));
            }

            case "brightness.set":
            {
                if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
                {
                    int target = (int)Math.Clamp(Math.Round(pct), 0, 100);
                    if (CustomWidgetSystemControlInterop.TrySetDisplayBrightnessPercent(target))
                    {
                        await RefreshSnapshotAsync().ConfigureAwait(false);
                        return new(true, false, null);
                    }

                    await OpenSettingAsync("display").ConfigureAwait(false);
                    return new(
                        true,
                        true,
                        LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.ToastDisplaySettingsExternal"));
                }
                return new(false, false, null);
            }

            case "brightness.step":
            {
                int delta = int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d)
                    ? d
                    : 10;
                int cur = CustomWidgetSystemControlInterop.TryGetDisplayBrightnessPercent(out int queried)
                    ? queried
                    : _latest.BrightnessPercent;
                int target = Math.Clamp(cur + delta, 0, 100);
                if (CustomWidgetSystemControlInterop.TrySetDisplayBrightnessPercent(target))
                {
                    await RefreshSnapshotAsync().ConfigureAwait(false);
                    return new(true, false, $"{target}%");
                }

                await OpenSettingAsync("display").ConfigureAwait(false);
                return new(
                    true,
                    true,
                    LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.ToastDisplaySettingsBrightness"));
            }

            case "audio.output":
            case "audio.settings":
            case "sound.output":
                await OpenSettingAsync("sound").ConfigureAwait(false);
                return new(
                    true,
                    true,
                    LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.ToastSoundSettings"));

            case "media.playpause":
            case "media.toggle":
            {
                if (!await TryControlSmtcAsync("playpause").ConfigureAwait(false))
                {
                    SendVirtualKeyTap(VkMediaPlayPause);
                }
                await Task.Delay(120).ConfigureAwait(false);
                await RefreshSnapshotAsync().ConfigureAwait(false);
                return new(true, false, null);
            }

            case "media.next":
            {
                if (!await TryControlSmtcAsync("next").ConfigureAwait(false))
                {
                    SendVirtualKeyTap(VkMediaNextTrack);
                }
                await Task.Delay(120).ConfigureAwait(false);
                await RefreshSnapshotAsync().ConfigureAwait(false);
                return new(true, false, null);
            }

            case "media.previous":
            case "media.prev":
            {
                if (!await TryControlSmtcAsync("prev").ConfigureAwait(false))
                {
                    SendVirtualKeyTap(VkMediaPrevTrack);
                }
                await Task.Delay(120).ConfigureAwait(false);
                await RefreshSnapshotAsync().ConfigureAwait(false);
                return new(true, false, null);
            }

            case "power.keepawaketoggle":
            case "power.caffeine":
            {
                SetKeepAwake(!_keepAwakeActive);
                await RefreshSnapshotAsync().ConfigureAwait(false);
                return new(
                    true,
                    false,
                    _keepAwakeActive
                        ? LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.ToastKeepAwakeEnabled")
                        : LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.ToastKeepAwakeRestored"));
            }

            case "power.keepawakeon":
                SetKeepAwake(true);
                await RefreshSnapshotAsync().ConfigureAwait(false);
                return new(
                    true,
                    false,
                    LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.ToastKeepAwakeEnabled"));

            case "power.keepawakeoff":
                SetKeepAwake(false);
                await RefreshSnapshotAsync().ConfigureAwait(false);
                return new(
                    true,
                    false,
                    LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.ToastKeepAwakeDisabled"));

            case "power.lock":
            case "lock":
                CustomWidgetSystemControlInterop.LockDesktopWorkstation();
                return new(true, false, null);

            case "recyclebin.empty":
            {
                bool ok = ShellRecycleBinInterop.TryEmptyAllBins(ownerHwnd);
                await RefreshSnapshotAsync().ConfigureAwait(false);
                return new(ok, false, null);
            }

            case "radio.wifitoggle":
            case "wifi.toggle":
                return await ToggleRadioWithFallbackAsync(RadioKind.WiFi, "ms-settings:network-wifi", culture).ConfigureAwait(false);

            case "radio.bluetoothtoggle":
            case "bluetooth.toggle":
                return await ToggleRadioWithFallbackAsync(RadioKind.Bluetooth, "ms-settings:bluetooth", culture).ConfigureAwait(false);

            case "theme.toggle":
                await OpenSettingAsync("colors").ConfigureAwait(false);
                return new(
                    true,
                    true,
                    LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.ToastColorSettings"));

            default:
                if (IsSupportedSettingTarget(cmd))
                {
                    bool opened = await OpenSettingAsync(cmd).ConfigureAwait(false);
                    return new(opened, true, null);
                }
                return new(
                    false,
                    false,
                    LocalizationService.FormatForCulture(culture, "CustomWidget.SysCtrl.ToastUnsupportedCommandFormat", command));
        }
    }

    public async Task<bool> OpenSettingAsync(string? targetKey)
    {
        if (string.IsNullOrWhiteSpace(targetKey))
        {
            return false;
        }

        string clean = targetKey.Trim();
        if (clean.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            string suffix = clean["ms-settings:".Length..];
            if (SafeSystemSettingsUris.Values.Contains(clean, StringComparer.OrdinalIgnoreCase) ||
                SafeSystemSettingsUris.ContainsKey(suffix))
            {
                return await Launcher.LaunchUriAsync(new Uri(clean)).AsTask().ConfigureAwait(false);
            }
            return false;
        }

        if (string.Equals(clean, "taskmgr", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (SafeSystemSettingsUris.TryGetValue(clean, out string? uri))
        {
            return await Launcher.LaunchUriAsync(new Uri(uri)).AsTask().ConfigureAwait(false);
        }

        return false;
    }

    private void SetKeepAwake(bool enable)
    {
        _keepAwakeActive = enable;
        try
        {
            CustomWidgetSystemControlInterop.SetKeepAwakeExecutionState(enable);
        }
        catch
        {
        }
    }

    private static void SendVirtualKeyTap(byte vk)
    {
        try
        {
            CustomWidgetSystemControlInterop.SendVirtualKeyTap(vk);
        }
        catch
        {
        }
    }

    private async Task<string> QueryDefaultAudioDeviceNameSafeAsync()
    {
        try
        {
            string? id = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
            if (string.IsNullOrWhiteSpace(id))
            {
                return string.Empty;
            }

            if (string.Equals(id, _cachedAudioRenderId, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(_cachedAudioDeviceName))
            {
                return _cachedAudioDeviceName;
            }

            DeviceInformation info = await DeviceInformation.CreateFromIdAsync(id).AsTask().ConfigureAwait(false);
            string name = info?.Name?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(name))
            {
                _cachedAudioRenderId = id;
                _cachedAudioDeviceName = name;
            }
            return name;
        }
        catch
        {
            return _cachedAudioDeviceName;
        }
    }

    private async Task<CustomWidgetSystemControlOutcome> ToggleRadioWithFallbackAsync(
        RadioKind kind,
        string fallbackSettingsUri,
        string culture)
    {
        try
        {
            RadioAccessStatus access = await Radio.RequestAccessAsync().AsTask().ConfigureAwait(false);
            if (access == RadioAccessStatus.Allowed)
            {
                IReadOnlyList<Radio> radios = await Radio.GetRadiosAsync().AsTask().ConfigureAwait(false);
                Radio? target = radios.FirstOrDefault(r => r.Kind == kind);
                if (target is not null)
                {
                    RadioState desired = target.State == RadioState.On ? RadioState.Off : RadioState.On;
                    RadioAccessStatus setStatus = await target.SetStateAsync(desired).AsTask().ConfigureAwait(false);
                    if (setStatus == RadioAccessStatus.Allowed)
                    {
                        await RefreshSnapshotAsync().ConfigureAwait(false);
                        string label = kind == RadioKind.WiFi ? "Wi-Fi" : LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.Bluetooth");
                        string stateWord = desired == RadioState.On
                            ? LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.On")
                            : LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.Off");
                        return new(true, false, $"{label} {stateWord}");
                    }
                }
            }
        }
        catch
        {
        }

        try
        {
            await Launcher.LaunchUriAsync(new Uri(fallbackSettingsUri)).AsTask().ConfigureAwait(false);
        }
        catch
        {
        }

        return new(
            true,
            true,
            LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.ToastHardwareSwitch"));
    }

    private static async Task<(bool WifiAvail, bool WifiOn, bool BtAvail, bool BtOn)> QueryRadiosSafeAsync()
    {
        try
        {
            IReadOnlyList<Radio> radios = await Radio.GetRadiosAsync().AsTask().ConfigureAwait(false);
            Radio? wifi = radios.FirstOrDefault(static r => r.Kind == RadioKind.WiFi);
            Radio? bt = radios.FirstOrDefault(static r => r.Kind == RadioKind.Bluetooth);
            return (
                wifi is not null,
                wifi?.State == RadioState.On,
                bt is not null,
                bt?.State == RadioState.On);
        }
        catch
        {
            return (false, false, false, false);
        }
    }

    private async Task<(bool Playing, string Title, string Artist, string CoverPath)> QueryMediaSessionSafeAsync()
    {
        try
        {
            GlobalSystemMediaTransportControlsSessionManager? mgr =
                await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().ConfigureAwait(false);
            GlobalSystemMediaTransportControlsSession? session = mgr?.GetCurrentSession();
            if (session is null)
            {
                return (false, string.Empty, string.Empty, string.Empty);
            }

            GlobalSystemMediaTransportControlsSessionPlaybackInfo? pb = session.GetPlaybackInfo();
            bool playing = pb?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            GlobalSystemMediaTransportControlsSessionMediaProperties? props =
                await session.TryGetMediaPropertiesAsync().AsTask().ConfigureAwait(false);

            string title = props?.Title ?? string.Empty;
            string artist = props?.Artist ?? string.Empty;
            string signature = $"{title}|{artist}";

            string coverPath = string.Empty;
            if (!string.IsNullOrWhiteSpace(title))
            {
                if (string.Equals(signature, _cachedMediaTrackSignature, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(_cachedMediaCoverPath) &&
                    File.Exists(_cachedMediaCoverPath))
                {
                    coverPath = _cachedMediaCoverPath;
                }
                else if (props?.Thumbnail is not null)
                {
                    coverPath = await TryCacheSmtcCoverAsync(props.Thumbnail, signature).ConfigureAwait(false);
                    _cachedMediaTrackSignature = signature;
                    _cachedMediaCoverPath = coverPath;
                }
                else
                {
                    _cachedMediaTrackSignature = signature;
                    _cachedMediaCoverPath = string.Empty;
                }
            }

            return (playing, title, artist, coverPath);
        }
        catch
        {
            return (false, string.Empty, string.Empty, string.Empty);
        }
    }

    private static Task<string> TryCacheSmtcCoverAsync(
        IRandomAccessStreamReference thumbnailRef,
        string signature) =>
        CustomWidgetHostSnapshotService.CacheSmtcCoverStreamAsync(thumbnailRef, signature);

    private static async Task<bool> TryControlSmtcAsync(string action)
    {
        try
        {
            GlobalSystemMediaTransportControlsSessionManager? mgr =
                await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().ConfigureAwait(false);
            GlobalSystemMediaTransportControlsSession? session = mgr?.GetCurrentSession();
            if (session is null)
            {
                return false;
            }

            return action switch
            {
                "playpause" => await session.TryTogglePlayPauseAsync().AsTask().ConfigureAwait(false),
                "next" => await session.TrySkipNextAsync().AsTask().ConfigureAwait(false),
                "prev" => await session.TrySkipPreviousAsync().AsTask().ConfigureAwait(false),
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }
}
