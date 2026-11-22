using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using DeskBox.Platform;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Media.Devices;
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

[Flags]
internal enum DeviceStateQueryFields
{
    None = 0,
    Volume = 1 << 0,
    Brightness = 1 << 1,
    AudioDevice = 1 << 2,
    Radios = 1 << 3,
    Media = 1 << 4,
    RecycleBin = 1 << 5,
    Theme = 1 << 6,
    KeepAwake = 1 << 7,
    All = Volume | Brightness | AudioDevice | Radios | Media | RecycleBin | Theme | KeepAwake
}

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

    private const long RadiosCacheTtlMs = 15_000L;
    private const long EmptyMediaBackoffTtlMs = 15_000L;
    private const long RecycleBinFallbackCacheTtlMs = 60_000L;

    private readonly MusicVolumeService _volumeService = new();
    private SettingsService? _settingsService;
    private readonly object _subQueryCacheGate = new();
    private volatile bool _keepAwakeActive;
    private volatile bool _softwareMutedEstimate;
    private string _cachedAudioRenderId = string.Empty;
    private string _cachedAudioDeviceName = string.Empty;
    private (bool WifiAvail, bool WifiOn, bool BtAvail, bool BtOn) _cachedRadios;
    private long _cachedRadiosExpiresTick64;
    private long _emptyMediaBackoffUntilTick64;
    private long _cachedRecycleBinItems;
    private long _cachedRecycleBinExpiresTick64;
    private CustomWidgetDeviceStateSnapshot _latest = CustomWidgetDeviceStateSnapshot.Empty;

    public CustomWidgetSystemControlService(SettingsService? settingsService = null)
    {
        _settingsService = settingsService;
    }

    internal void AttachFallbackSettingsService(SettingsService? settingsService)
    {
        if (settingsService is not null)
        {
            Interlocked.CompareExchange(ref _settingsService, settingsService, null);
        }
    }

    public CustomWidgetDeviceStateSnapshot Latest => _latest;

    public bool IsKeepAwakeActive => _keepAwakeActive;

    public static bool IsSupportedSettingTarget(string? key) =>
        !string.IsNullOrWhiteSpace(key) &&
        (SafeSystemSettingsUris.ContainsKey(key.Trim()) ||
         string.Equals(key.Trim(), "taskmgr", StringComparison.OrdinalIgnoreCase));

    public Task<CustomWidgetDeviceStateSnapshot> RefreshSnapshotAsync(CancellationToken cancellationToken = default) =>
        RefreshSnapshotAsync(DeviceStateQueryFields.All, forceUncached: false, cancellationToken);

    internal async Task<CustomWidgetDeviceStateSnapshot> RefreshSnapshotAsync(
        DeviceStateQueryFields fields,
        bool forceUncached = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int volPct = 50;
        bool isMuted = false;
        if ((fields & DeviceStateQueryFields.Volume) != 0)
        {
            double volRatio = _latest.VolumePercent / 100.0;
            try
            {
                volRatio = await _volumeService.GetSystemMasterVolumeAsync().ConfigureAwait(false);
            }
            catch
            {
            }

            volPct = (int)Math.Clamp(Math.Round(volRatio * 100.0), 0, 100);
            isMuted = _softwareMutedEstimate || volPct == 0;
        }

        int queriedBrightness = 80;
        bool brightnessOk = false;
        if ((fields & DeviceStateQueryFields.Brightness) != 0)
        {
            (brightnessOk, queriedBrightness) = await Task.Run(() =>
            {
                bool ok = CustomWidgetSystemControlInterop.TryGetDisplayBrightnessPercent(
                    out int pct,
                    bypassCache: forceUncached);
                return (ok, pct);
            }, cancellationToken).ConfigureAwait(false);
        }

        bool netUp = true;
        bool wifiAvail = false;
        bool wifiEnabled = false;
        bool btAvail = false;
        bool btOn = false;
        if ((fields & DeviceStateQueryFields.Radios) != 0)
        {
            netUp = false;
            try
            {
                netUp = NetworkInterface.GetIsNetworkAvailable();
            }
            catch
            {
            }

            (wifiAvail, bool wifiOn, btAvail, btOn) = await QueryRadiosCachedAsync(forceUncached).ConfigureAwait(false);
            wifiEnabled = wifiAvail ? wifiOn : netUp;
        }

        string audioName = string.Empty;
        if ((fields & DeviceStateQueryFields.AudioDevice) != 0)
        {
            audioName = await QueryDefaultAudioDeviceNameSafeAsync().ConfigureAwait(false);
        }

        bool mediaPlaying = false;
        string mediaTitle = string.Empty;
        string mediaArtist = string.Empty;
        string mediaCover = string.Empty;
        if ((fields & DeviceStateQueryFields.Media) != 0)
        {
            (mediaPlaying, mediaTitle, mediaArtist, mediaCover) =
                await QueryMediaSessionSafeAsync(forceUncached).ConfigureAwait(false);
        }

        long binItems = 0;
        if ((fields & DeviceStateQueryFields.RecycleBin) != 0)
        {
            binItems = await QueryRecycleBinCountCachedAsync(forceUncached, cancellationToken).ConfigureAwait(false);
        }

        bool isDark = true;
        if ((fields & DeviceStateQueryFields.Theme) != 0)
        {
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
        }

        CustomWidgetDeviceStateSnapshot snapshot;
        lock (_subQueryCacheGate)
        {
            CustomWidgetDeviceStateSnapshot latestNow = _latest;
            snapshot = new CustomWidgetDeviceStateSnapshot
            {
                VolumePercent = (fields & DeviceStateQueryFields.Volume) != 0 ? volPct : latestNow.VolumePercent,
                IsMuted = (fields & DeviceStateQueryFields.Volume) != 0 ? isMuted : latestNow.IsMuted,
                BrightnessPercent = (fields & DeviceStateQueryFields.Brightness) != 0
                    ? (brightnessOk ? queriedBrightness : latestNow.BrightnessPercent)
                    : latestNow.BrightnessPercent,
                BrightnessSupported = (fields & DeviceStateQueryFields.Brightness) != 0
                    ? brightnessOk
                    : latestNow.BrightnessSupported,
                AudioDeviceName = (fields & DeviceStateQueryFields.AudioDevice) != 0 ? audioName : latestNow.AudioDeviceName,
                WifiAvailable = (fields & DeviceStateQueryFields.Radios) != 0 ? wifiAvail : latestNow.WifiAvailable,
                WifiEnabled = (fields & DeviceStateQueryFields.Radios) != 0 ? wifiEnabled : latestNow.WifiEnabled,
                BluetoothAvailable = (fields & DeviceStateQueryFields.Radios) != 0 ? btAvail : latestNow.BluetoothAvailable,
                BluetoothEnabled = (fields & DeviceStateQueryFields.Radios) != 0 ? btOn : latestNow.BluetoothEnabled,
                NetworkConnected = (fields & DeviceStateQueryFields.Radios) != 0 ? netUp : latestNow.NetworkConnected,
                KeepAwakeActive = _keepAwakeActive,
                IsDarkTheme = (fields & DeviceStateQueryFields.Theme) != 0 ? isDark : latestNow.IsDarkTheme,
                MediaPlaying = (fields & DeviceStateQueryFields.Media) != 0 ? mediaPlaying : latestNow.MediaPlaying,
                MediaTitle = (fields & DeviceStateQueryFields.Media) != 0 ? mediaTitle : latestNow.MediaTitle,
                MediaArtist = (fields & DeviceStateQueryFields.Media) != 0 ? mediaArtist : latestNow.MediaArtist,
                MediaCoverPath = (fields & DeviceStateQueryFields.Media) != 0 ? mediaCover : latestNow.MediaCoverPath,
                RecycleBinItems = (fields & DeviceStateQueryFields.RecycleBin) != 0
                    ? Math.Max(0, binItems)
                    : latestNow.RecycleBinItems
            };
            _latest = snapshot;
        }

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
                    await RefreshSnapshotAsync(DeviceStateQueryFields.Volume, forceUncached: true).ConfigureAwait(false);
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
                await RefreshSnapshotAsync(DeviceStateQueryFields.Volume, forceUncached: true).ConfigureAwait(false);
                return new(ok, false, $"{Math.Round(next * 100):0}%");
            }

            case "volume.mutetoggle":
            case "volume.mute":
            {
                SendVirtualKeyTap(VkVolumeMute);
                _softwareMutedEstimate = !_softwareMutedEstimate;
                await RefreshSnapshotAsync(DeviceStateQueryFields.Volume, forceUncached: true).ConfigureAwait(false);
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
                    bool applied = await Task.Run(() => CustomWidgetSystemControlInterop.TrySetDisplayBrightnessPercent(target)).ConfigureAwait(false);
                    if (applied)
                    {
                        await RefreshSnapshotAsync(DeviceStateQueryFields.Brightness).ConfigureAwait(false);
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
                int fallbackBrightness = _latest.BrightnessPercent;
                (bool applied, int target) = await Task.Run(() =>
                {
                    int cur = CustomWidgetSystemControlInterop.TryGetDisplayBrightnessPercent(out int queried, bypassCache: true)
                        ? queried
                        : fallbackBrightness;
                    int next = Math.Clamp(cur + delta, 0, 100);
                    return (CustomWidgetSystemControlInterop.TrySetDisplayBrightnessPercent(next), next);
                }).ConfigureAwait(false);
                if (applied)
                {
                    await RefreshSnapshotAsync(DeviceStateQueryFields.Brightness).ConfigureAwait(false);
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
                await RefreshSnapshotAsync(DeviceStateQueryFields.Media, forceUncached: true).ConfigureAwait(false);
                return new(true, false, null);
            }

            case "media.next":
            {
                if (!await TryControlSmtcAsync("next").ConfigureAwait(false))
                {
                    SendVirtualKeyTap(VkMediaNextTrack);
                }
                await Task.Delay(120).ConfigureAwait(false);
                await RefreshSnapshotAsync(DeviceStateQueryFields.Media, forceUncached: true).ConfigureAwait(false);
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
                await RefreshSnapshotAsync(DeviceStateQueryFields.Media, forceUncached: true).ConfigureAwait(false);
                return new(true, false, null);
            }

            case "power.keepawaketoggle":
            case "power.caffeine":
            {
                SetKeepAwake(!_keepAwakeActive);
                await RefreshSnapshotAsync(DeviceStateQueryFields.KeepAwake).ConfigureAwait(false);
                return new(
                    true,
                    false,
                    _keepAwakeActive
                        ? LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.ToastKeepAwakeEnabled")
                        : LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.ToastKeepAwakeRestored"));
            }

            case "power.keepawakeon":
                SetKeepAwake(true);
                await RefreshSnapshotAsync(DeviceStateQueryFields.KeepAwake).ConfigureAwait(false);
                return new(
                    true,
                    false,
                    LocalizationService.TranslateForCulture(culture, "CustomWidget.SysCtrl.ToastKeepAwakeEnabled"));

            case "power.keepawakeoff":
                SetKeepAwake(false);
                await RefreshSnapshotAsync(DeviceStateQueryFields.KeepAwake).ConfigureAwait(false);
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
                CustomWidgetRecycleBinService.Shared.NotifyShellChanged();
                await RefreshSnapshotAsync(DeviceStateQueryFields.RecycleBin, forceUncached: true).ConfigureAwait(false);
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
                using Process? proc = Process.Start(new ProcessStartInfo("taskmgr.exe")
                {
                    UseShellExecute = true,
                    ErrorDialog = false
                });
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
                        await RefreshSnapshotAsync(DeviceStateQueryFields.Radios, forceUncached: true).ConfigureAwait(false);
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

    private async Task<(bool WifiAvail, bool WifiOn, bool BtAvail, bool BtOn)> QueryRadiosCachedAsync(bool forceUncached)
    {
        long nowTick = Environment.TickCount64;
        if (!forceUncached)
        {
            lock (_subQueryCacheGate)
            {
                if (nowTick < _cachedRadiosExpiresTick64)
                {
                    return _cachedRadios;
                }
            }
        }

        (bool WifiAvail, bool WifiOn, bool BtAvail, bool BtOn) queried = await QueryRadiosSafeAsync().ConfigureAwait(false);
        lock (_subQueryCacheGate)
        {
            _cachedRadios = queried;
            _cachedRadiosExpiresTick64 = Environment.TickCount64 + RadiosCacheTtlMs;
        }

        return queried;
    }

    private async Task<long> QueryRecycleBinCountCachedAsync(bool forceUncached, CancellationToken cancellationToken)
    {
        if (!forceUncached && CustomWidgetRecycleBinService.Shared.TryGetLiveSubscribedItemCount(out long liveCount))
        {
            lock (_subQueryCacheGate)
            {
                _cachedRecycleBinItems = liveCount;
                _cachedRecycleBinExpiresTick64 = Environment.TickCount64 + RecycleBinFallbackCacheTtlMs;
            }

            return liveCount;
        }

        long nowTick = Environment.TickCount64;
        if (!forceUncached)
        {
            lock (_subQueryCacheGate)
            {
                if (nowTick < _cachedRecycleBinExpiresTick64)
                {
                    return _cachedRecycleBinItems;
                }
            }
        }

        long queried = await Task.Run(() =>
        {
            long count = 0;
            ShellRecycleBinInterop.TryQueryTotalItemCount(out count);
            return Math.Max(0, count);
        }, cancellationToken).ConfigureAwait(false);

        lock (_subQueryCacheGate)
        {
            _cachedRecycleBinItems = queried;
            _cachedRecycleBinExpiresTick64 = Environment.TickCount64 + RecycleBinFallbackCacheTtlMs;
        }

        return queried;
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

    private async Task<(bool Playing, string Title, string Artist, string CoverPath)> QueryMediaSessionSafeAsync(bool forceUncached)
    {
        try
        {
            bool hasLiveSessionOwner = CustomWidgetHostSnapshotService.HasActiveSharedMusicSession ||
                                       MusicSessionService.SharedManagerReferenceCount > 0;
            if (!forceUncached && !hasLiveSessionOwner)
            {
                lock (_subQueryCacheGate)
                {
                    if (Environment.TickCount64 < _emptyMediaBackoffUntilTick64)
                    {
                        return (false, string.Empty, string.Empty, string.Empty);
                    }
                }
            }

            CustomWidgetMusicSnapshot snap =
                await CustomWidgetHostSnapshotService.CaptureMusicSnapshotAsync().ConfigureAwait(false);
            if (!snap.HasSession)
            {
                lock (_subQueryCacheGate)
                {
                    _emptyMediaBackoffUntilTick64 = Environment.TickCount64 + EmptyMediaBackoffTtlMs;
                }

                return (false, string.Empty, string.Empty, string.Empty);
            }

            lock (_subQueryCacheGate)
            {
                _emptyMediaBackoffUntilTick64 = 0;
            }

            return (snap.IsPlaying, snap.Title, snap.Artist, snap.CoverPath);
        }
        catch
        {
            return (false, string.Empty, string.Empty, string.Empty);
        }
    }

    private static async Task<bool> TryControlSmtcAsync(string action)
    {
        try
        {
            using var sessionService = new MusicSessionService();
            await sessionService.InitializeAsync().ConfigureAwait(false);
            return action switch
            {
                "playpause" => await sessionService.TryTogglePlayPauseAsync(null).ConfigureAwait(false),
                "next" => await sessionService.TryNextAsync(null).ConfigureAwait(false),
                "prev" => await sessionService.TryPreviousAsync(null).ConfigureAwait(false),
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }
}
