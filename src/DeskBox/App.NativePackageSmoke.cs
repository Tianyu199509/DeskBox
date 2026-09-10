#if DESKBOX_NATIVE_DEV_PILOT
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Services.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DeskBox;

public partial class App
{
    private List<Window>? _nativeMusicVisualWindows;
    private List<NativeWidgetLease>? _nativeMusicVisualLeases;
    private MediaPlayer? _nativeMusicVisualPlayer;

    private void StartNativePackageSmokeIfRequested()
    {
        if (Environment.GetEnvironmentVariable("DESKBOX_DEV_MUSIC_SMOKE") == "1" && DeskBoxDataPathService.Current.IsDevelopmentRoot)
            _ = RunNativeMusicPackageSmokeAsync();
    }

    private async Task RunNativeMusicPackageSmokeAsync()
    {
        var checks = new List<string>();
        var windows = new List<Window>();
        var leases = new List<NativeWidgetLease>();
        MediaPlayer? player = null;
        string root = DeskBoxDataPathService.Current.RootPath;
        bool success = false;
        string? failure = null;
        const string title = "DeskBox Native Music Fixture";
        int playRequests = 0, pauseRequests = 0, nextRequests = 0, previousRequests = 0;
        try
        {
            string wave = Path.Combine(root, "silent-fixture.wav");
            using (var stream = File.Create(wave))
            using (var writer = new BinaryWriter(stream))
            {
                int bytes = 48000 * 2 * 30;
                writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVEfmt "u8);
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(48000);
                writer.Write(96000); writer.Write((short)2); writer.Write((short)16);
                writer.Write("data"u8); writer.Write(bytes); writer.Write(new byte[bytes]);
            }
            player = new MediaPlayer { IsLoopingEnabled = true, IsMuted = true };
            player.CommandManager.IsEnabled = false;
            player.Source = MediaSource.CreateFromStorageFile(await StorageFile.GetFileFromPathAsync(wave));
            var controls = player.SystemMediaTransportControls;
            controls.IsEnabled = true;
            controls.IsPlayEnabled = controls.IsPauseEnabled = controls.IsNextEnabled = controls.IsPreviousEnabled = true;
            controls.DisplayUpdater.Type = MediaPlaybackType.Music;
            controls.DisplayUpdater.MusicProperties.Title = title;
            controls.DisplayUpdater.MusicProperties.Artist = "Owned test session";
            string cover = Path.Combine(AppContext.BaseDirectory, "Assets", "Store", "Square150x150Logo.png");
            if (File.Exists(cover)) controls.DisplayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(cover));
            controls.DisplayUpdater.Update();
            var ownedPlayer = player;
            controls.ButtonPressed += (_, args) => UiDispatcherQueue?.TryEnqueue(() =>
            {
                switch (args.Button)
                {
                    case SystemMediaTransportControlsButton.Play: playRequests++; ownedPlayer.Play(); controls.PlaybackStatus = MediaPlaybackStatus.Playing; break;
                    case SystemMediaTransportControlsButton.Pause: pauseRequests++; ownedPlayer.Pause(); controls.PlaybackStatus = MediaPlaybackStatus.Paused; break;
                    case SystemMediaTransportControlsButton.Next: nextRequests++; break;
                    case SystemMediaTransportControlsButton.Previous: previousRequests++; break;
                }
            });
            player.Play();
            controls.PlaybackStatus = MediaPlaybackStatus.Playing;
            await Task.Delay(1200);
            var manager = new PluginPackageManager(Path.Combine(DeskBoxDataPathService.Current.DataDirectory, "plugins"));
            var installed = manager.TryCreateNativeHandle(MusicInstanceMigration.PackageId)
                ?? throw new InvalidOperationException("No verified installed Music package.");
            await MusicSettingsStore.Current.SaveAsync(new MusicWidgetSettings { DisplayMode = "Controls" });
            NativeWidgetLease Create(string id)
            {
                if (!SettingsService.Settings.Widgets.Any(w => w.Id == id))
                    SettingsService.Settings.Widgets.Add(new WidgetConfig { Id = id, WidgetKind = WidgetKind.Music, Name = id });
                NativeWidgetDataMigration.TrySync(MusicInstanceMigration.Instance, installed.Record.PublisherFingerprint,
                    installed.Record.PackageId, id, DeskBoxDataPathService.Current.DataDirectory);
                if (!NativeWidgetRuntimeManager.TryCreateFromInstalled(installed, "music", id,
                    DeskBoxDataPathService.Current.DataDirectory, out var lease))
                    throw new InvalidOperationException("Native create failed for " + id);
                var window = new Window { Title = "Music native probe " + id, Content = lease!.View };
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(400, 360));
                windows.Add(window); leases.Add(lease); window.Activate();
                lease.InvokeWidgetEvent(WidgetLifecycleEventKind.VisibilityChanged, 0, 0, 1);
                lease.InvokeWidgetEvent(WidgetLifecycleEventKind.RevealCompleted, 0, 0, 0);
                return lease;
            }
            NativeWidgetLease a = Create("music-probe-a");
            NativeWidgetLease b = Create("music-probe-b");
            await UntilAsync(() => FindMusicElement<TextBlock>(a.View, "TitleStaticText")?.Text == title &&
                FindMusicElement<TextBlock>(b.View, "TitleStaticText")?.Text == title, "real media title in both native views");
            checks.Add("native-smct-title-two-instances");
            await UntilAsync(() => FindMusicElement<Image>(a.View, "AlbumArtworkImage")?.Source is not null &&
                FindMusicElement<Image>(b.View, "AlbumArtworkImage")?.Source is not null, "native cover binding");
            checks.Add("native-cover-two-instances");
            InvokeMusicButton(a.View, "PlayPauseButton");
            await UntilAsync(() => pauseRequests > 0, "pause command reached owned SMTC"); checks.Add("pause-command");
            await UntilAsync(() => ToolTipService.GetToolTip(FindMusicElement<Button>(b.View, "PlayPauseButton")!) as string ==
                LocalizationService.T("Music.Control.Play"), "pause state reflected in second native view");
            InvokeMusicButton(b.View, "PlayPauseButton");
            await UntilAsync(() => playRequests > 0, "play command reached owned SMTC"); checks.Add("play-command");
            InvokeMusicButton(a.View, "NextButton");
            InvokeMusicButton(a.View, "PreviousButton");
            await UntilAsync(() => nextRequests > 0 && previousRequests > 0, "next/previous commands"); checks.Add("next-previous-commands");
            InvokeMusicButton(a.View, "VolumeButton");
            await UntilAsync(() => FindMusicElement<Border>(a.View, "InlineVolumePanel")?.Visibility == Visibility.Visible, "native volume panel");
            Border volumePanel = FindMusicElement<Border>(a.View, "InlineVolumePanel")!;
            if (CountMusicElements<Slider>(volumePanel) != 1)
                throw new InvalidOperationException("Music volume panel must contain exactly one system-volume slider.");
            checks.Add("single-system-volume-slider");
            await UntilAsync(() => File.ReadAllText(DeskBoxDataPathService.Current.LogFilePath).Contains("[MusicPackage] system-volume-read success"), "real Rust system-volume read");
            checks.Add("volume-panel-rust-read");
            ThemeService.SetTheme("Light");
            await UntilAsync(() => a.View.RequestedTheme == ElementTheme.Light && b.View.RequestedTheme == ElementTheme.Light, "light theme push");
            ThemeService.SetTheme("Dark");
            await UntilAsync(() => a.View.RequestedTheme == ElementTheme.Dark && b.View.RequestedTheme == ElementTheme.Dark, "dark theme push");
            checks.Add("live-light-dark-theme");
            LocalizationService.SetLanguage("en-US");
            await UntilAsync(() => ToolTipService.GetToolTip(FindMusicElement<Button>(a.View, "NextButton")!) as string ==
                LocalizationService.T("Music.Control.Next"), "English package tooltip");
            LocalizationService.SetLanguage("zh-CN");
            await UntilAsync(() => ToolTipService.GetToolTip(FindMusicElement<Button>(a.View, "NextButton")!) as string ==
                LocalizationService.T("Music.Control.Next"), "Chinese package tooltip");
            checks.Add("live-language-switch");
            foreach (var pair in new[] { ("Auto", "ContentGrid"), ("Cover", "MinimalLayout"), ("Controls", "ContentGrid"),
                ("RecordVertical", "RecordLayout"), ("RecordHorizontal", "RecordHorizontalLayout") })
            {
                await MusicSettingsStore.Current.SaveAsync(new MusicWidgetSettings { DisplayMode = pair.Item1 });
                await UntilAsync(() => FindMusicElement<Grid>(a.View, pair.Item2)?.Visibility == Visibility.Visible &&
                    FindMusicElement<Grid>(b.View, pair.Item2)?.Visibility == Visibility.Visible, "shared layout " + pair.Item1);
                checks.Add("shared-layout-" + pair.Item1);
            }
            ((IDisposable)a).Dispose(); leases.Remove(a); windows[0].Close(); windows.RemoveAt(0);
            b.InvokeWidgetEvent(WidgetLifecycleEventKind.RefreshRequested, 0, 0, 0);
            await Task.Delay(300);
            if (FindMusicElement<TextBlock>(b.View, "TitleStaticText")?.Text != title) throw new InvalidOperationException("Second instance lost media after first destroy.");
            checks.Add("destroy-a-keeps-b");
            var recreated = Create("music-probe-a");
            await UntilAsync(() => FindMusicElement<TextBlock>(recreated.View, "TitleStaticText")?.Text == title, "recreated view");
            checks.Add("destroy-recreate");
            foreach (var lease in leases) ((IDisposable)lease).Dispose();
            leases.Clear();
            foreach (var window in windows) window.Close();
            windows.Clear();
            await WidgetManager!.SetFeatureWidgetEnabledAsync(WidgetKind.Music, false, reveal: false);
            await UntilAsync(() => NativeHostApiBridge.RegisteredConfigChangedHandlerCount == 0,
                "last native instance shuts down and detaches callback");
            checks.Add("last-instance-shutdown");
            var reactivated = Create("music-probe-reactivated");
            await UntilAsync(() => FindMusicElement<TextBlock>(reactivated.View, "TitleStaticText")?.Text == title, "reactivation after shutdown");
            checks.Add("same-module-reactivation");
            success = true;
        }
        catch (Exception error) { failure = error.ToString(); Log("[MusicPackageSmoke] " + failure); }
        finally
        {
            bool keepVisual = success && Environment.GetEnvironmentVariable("DESKBOX_DEV_MUSIC_VISUAL") == "1";
            if (keepVisual)
            {
                _nativeMusicVisualWindows = windows;
                _nativeMusicVisualLeases = leases;
                _nativeMusicVisualPlayer = player;
                windows = [];
                leases = [];
                player = null;
                Log("[MusicPackageSmoke] visual probe retained for inspection");
            }
            foreach (var lease in leases) try { ((IDisposable)lease).Dispose(); } catch { }
            foreach (var window in windows) try { window.Close(); } catch { }
            if (player is not null) { player.SystemMediaTransportControls.IsEnabled = false; player.Dispose(); }
            using var stream = File.Create(Path.Combine(root, "music-native-smoke.json"));
            using var writer = new System.Text.Json.Utf8JsonWriter(stream, new() { Indented = true });
            writer.WriteStartObject(); writer.WriteBoolean("passed", success); writer.WriteString("failure", failure);
            writer.WriteNumber("playRequests", playRequests); writer.WriteNumber("pauseRequests", pauseRequests);
            writer.WriteNumber("nextRequests", nextRequests); writer.WriteNumber("previousRequests", previousRequests);
            writer.WriteStartArray("checks"); foreach (string check in checks) writer.WriteStringValue(check); writer.WriteEndArray();
            writer.WriteEndObject();
            Log($"[MusicPackageSmoke] completed passed={success} checks={checks.Count}");
        }
    }

    private void DisposeNativeMusicVisualPreview()
    {
        if (_nativeMusicVisualLeases is not null)
            foreach (var lease in _nativeMusicVisualLeases) try { ((IDisposable)lease).Dispose(); } catch { }
        if (_nativeMusicVisualWindows is not null)
            foreach (var window in _nativeMusicVisualWindows) try { window.Close(); } catch { }
        if (_nativeMusicVisualPlayer is not null)
        {
            _nativeMusicVisualPlayer.SystemMediaTransportControls.IsEnabled = false;
            _nativeMusicVisualPlayer.Dispose();
        }
        _nativeMusicVisualLeases = null;
        _nativeMusicVisualWindows = null;
        _nativeMusicVisualPlayer = null;
    }
    private static async Task UntilAsync(Func<bool> condition, string description)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline) { if (condition()) return; await Task.Delay(100); }
        throw new TimeoutException(description);
    }
    private static T? FindMusicElement<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T element && element.Name == name) return element;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindMusicElement<T>(VisualTreeHelper.GetChild(root, i), name) is { } found) return found;
        return null;
    }
    private static int CountMusicElements<T>(DependencyObject root) where T : DependencyObject
    {
        int count = root is T ? 1 : 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            count += CountMusicElements<T>(VisualTreeHelper.GetChild(root, i));
        return count;
    }
    private static void InvokeMusicButton(FrameworkElement root, string name)
    {
        var button = FindMusicElement<Button>(root, name) ?? throw new InvalidOperationException("Missing button: " + name);
        if (!button.IsEnabled) throw new InvalidOperationException("Disabled button: " + name);
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    }
}
#endif
