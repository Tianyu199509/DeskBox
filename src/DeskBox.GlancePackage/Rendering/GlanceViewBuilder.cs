using System.Globalization;
using System.Text.Json;
using DeskBox.GlancePackage.Services;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using WinRT;

namespace DeskBox.GlancePackage.Rendering;

/// <summary>
/// Builds the Glance widget view from runtime text XAML, driven by the
/// package-owned production services. D3 data-ownership hardening (audit 18):
/// settings live in the lossless GlanceData document, rotation 0 means
/// disabled (never a 6-second timer), the traditional-calendar mode travels
/// as the real enum (toggle off/on restores the user's calendar, it does not
/// overwrite Hebrew with Chinese), and image loading mirrors the built-in
/// semantics (user order for explicit files, full extension set, enumeration
/// failures never take the widget down).
/// </summary>
internal static class GlanceViewBuilder
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];

    public static FrameworkElement Create(
        string packageRoot, string contributionId, string instanceId, string instanceDataRoot,
        out Action<double, double>? onViewportChanged)
    {
        CultureInfo culture = HostConfig.TryGetCulture() ?? CultureInfo.CurrentUICulture;
        PackageStrings.Configure(culture, packageRoot);

        GlanceData data = GlanceDataFile.Load(instanceDataRoot) ?? new GlanceData(new GlanceWidgetData(), default);
        GlanceWidgetData settings = data.Settings;
        bool showFestivals = settings.ShowChineseFestivals;
        var runtimeState = GlanceRuntimeState.LoadOrCreate(instanceDataRoot);

        double width = GlanceMonthPipeline.DefaultWidth;
        double height = GlanceMonthPipeline.DefaultHeight;
        (GlanceCalendarMonth month, bool isCompact, double panelHeight, double panelWidth, double dayItemHeight, bool showSecondary, GlanceTraditionalCalendarMode effectiveMode) =
            GlanceMonthPipeline.Build(settings.ShowChineseFestivals, settings.TraditionalCalendarMode, culture, width, height);
        bool showTraditional = effectiveMode != GlanceTraditionalCalendarMode.None;

        var presentation = GlanceMonthPipeline.CreatePresentation(month, isCompact, panelHeight, panelWidth, culture, width, height);
        FrameworkElement content = (FrameworkElement)XamlReader.Load(
            File.ReadAllText(Path.Combine(packageRoot, "glance.xaml")));
        content.DataContext = presentation;

        // Calendar day decoration (single subscription, mutable state).
        var calendarView = content.FindName("NativeCalendarView").As<CalendarView>();
        var decoration = new CalendarDecorationState(month, dayItemHeight, showTraditional, settings.ShowChineseFestivals, showSecondary);
        SubscribeDayDecoration(calendarView, decoration, culture);

        // Background rotation. Image set follows GlanceWidgetData: explicit
        // local files (user order preserved), a local folder (full extension
        // set, enumeration failures degrade to empty), or a gradient surface
        // when the configured source needs a capability the package does not
        // have yet (Online/Bing) or nothing resolves.
        string[] images = LoadImages(settings, packageRoot);
        Stretch imageStretch = settings.ImageFit == GlanceImageFitMode.Fit ? Stretch.Uniform : Stretch.UniformToFill;
        var backgroundA = content.FindName("BackgroundA").As<Border>();
        var backgroundB = content.FindName("BackgroundB").As<Border>();
        bool showingA = true;
        void Show(int index)
        {
            if (images.Length == 0) return;
            runtimeState.ImageIndex = ((index % images.Length) + images.Length) % images.Length;
            var brush = new ImageBrush { ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(images[runtimeState.ImageIndex])), Stretch = imageStretch };
            Border next = showingA ? backgroundB : backgroundA;
            Border fadeOut = showingA ? backgroundA : backgroundB;
            next.Background = brush;
            next.Opacity = 1;
            fadeOut.Opacity = 0;
            showingA = !showingA;
        }
        if (images.Length == 0)
        {
            ShowGradientFallback(backgroundA);
            showingA = false;
        }
        else
        {
            Show(runtimeState.ImageIndex);
        }

        // Built-in parity: RotationIntervalMinutes <= 0 means rotation is
        // DISABLED - it must never clamp up into a fast timer (audit 18).
        var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        if (settings.RotationIntervalMinutes > 0)
        {
            timer.Interval = TimeSpan.FromMinutes(Math.Clamp(settings.RotationIntervalMinutes, 0.1, 1440));
        }
        timer.Tick += (_, _) => { if (!runtimeState.Paused) Show(runtimeState.ImageIndex + 1); };
        if (settings.RotationIntervalMinutes > 0 && !runtimeState.Paused && images.Length > 1) timer.Start();

        // Action bar (hidden when the user turned photo controls off).
        var pauseButton = content.FindName("PauseButton").As<Button>();
        var nextButton = content.FindName("NextButton").As<Button>();
        if (!settings.ShowPhotoControls)
        {
            pauseButton.Visibility = Visibility.Collapsed;
            nextButton.Visibility = Visibility.Collapsed;
        }
        void TogglePause()
        {
            runtimeState.Paused = !runtimeState.Paused;
            if (runtimeState.Paused) timer.Stop();
            else if (settings.RotationIntervalMinutes > 0 && images.Length > 1) timer.Start();
        }
        pauseButton.Click += (_, _) => TogglePause();
        nextButton.Click += (_, _) => Show(runtimeState.ImageIndex + 1);

        // Settings panel (in-namescope for host-side probes). Toggles mutate
        // the lossless document and persist it; the traditional toggle
        // restores the user's real calendar on re-enable instead of
        // overwriting it with Chinese lunar.
        var settingsLayer = content.FindName("SettingsLayer").As<FrameworkElement>();
        var festivalToggle = content.FindName("FestivalToggle").As<ToggleSwitch>();
        var traditionalToggle = content.FindName("TraditionalToggle").As<ToggleSwitch>();
        festivalToggle.IsOn = showFestivals;
        traditionalToggle.IsOn = showTraditional;
        GlanceTraditionalCalendarMode restoreMode = effectiveMode != GlanceTraditionalCalendarMode.None
            ? effectiveMode
            : GlanceTraditionalCalendarMode.ChineseLunar;
        // Settings are HOST-AUTHORITATIVE (audit round 19): a toggle mutation
        // commits through the write-through channel into the authoritative
        // store. Without the channel (older host) or on failure the toggle
        // reverts - never a silent local save that the next sync overwrites.
        bool applying = false;
        bool CommitSettings()
        {
            if (HostConfig.TryPushInstanceConfig(instanceId, GlanceDataFile.BuildOwnedPatch(settings)))
            {
                // Local cache for continuity until the next host sync; the
                // authoritative copy lives in the built-in store.
                GlanceDataFile.Save(data, instanceDataRoot);
                return true;
            }
            PackageLogger.LogVerbose("[GlancePackage] settings write-through unavailable; reverting toggle");
            return false;
        }
        festivalToggle.Toggled += (_, _) =>
        {
            if (applying) return;
            settings.ShowChineseFestivals = festivalToggle.IsOn;
            if (CommitSettings())
            {
                RebuildMonth(decoration, data, culture, width, height, content);
                return;
            }
            applying = true;
            festivalToggle.IsOn = !festivalToggle.IsOn;
            applying = false;
            settings.ShowChineseFestivals = festivalToggle.IsOn;
        };
        traditionalToggle.Toggled += (_, _) =>
        {
            if (applying) return;
            settings.TraditionalCalendarMode = traditionalToggle.IsOn
                ? restoreMode
                : GlanceTraditionalCalendarMode.None;
            if (CommitSettings())
            {
                RebuildMonth(decoration, data, culture, width, height, content);
                return;
            }
            applying = true;
            traditionalToggle.IsOn = !traditionalToggle.IsOn;
            applying = false;
            settings.TraditionalCalendarMode = traditionalToggle.IsOn
                ? restoreMode
                : GlanceTraditionalCalendarMode.None;
        };

        // Debounced responsive rebuild: the host can report viewport changes
        // continuously during a resize; the pipeline (month data + panel
        // sizing + presentation) re-runs once per settled size.
        var resizeTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        resizeTimer.Interval = TimeSpan.FromMilliseconds(120);
        resizeTimer.IsRepeating = false;
        resizeTimer.Tick += (_, _) => RebuildMonth(decoration, data, culture, width, height, content);
        onViewportChanged = (newWidth, newHeight) =>
        {
            if (Math.Abs(newWidth - width) < 1 && Math.Abs(newHeight - height) < 1) return;
            width = newWidth;
            height = newHeight;
            resizeTimer.Stop();
            resizeTimer.Start();
        };
        content.Unloaded += (_, _) =>
        {
            timer.Stop();
            resizeTimer.Stop();
            GlanceRuntimeState.Save(runtimeState, instanceDataRoot);
        };

        // Right-click menu (code-built: runtime XAML cannot wire handlers).
        var menu = new MenuFlyout();
        var nextItem = new MenuFlyoutItem { Text = PackageStrings.Get("menuNextBackground", "下一张背景") };
        nextItem.Click += (_, _) => Show(runtimeState.ImageIndex + 1);
        var pauseItem = new MenuFlyoutItem { Text = PackageStrings.Get("menuPauseRotation", "暂停轮播") };
        pauseItem.Click += (_, _) => TogglePause();
        var settingsItem = new MenuFlyoutItem { Text = PackageStrings.Get("menuSettings", "设置") };
        settingsItem.Click += (_, _) =>
        {
            settingsLayer.Visibility = settingsLayer.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        };
        menu.Items.Add(nextItem);
        menu.Items.Add(pauseItem);
        menu.Items.Add(settingsItem);
        content.ContextFlyout = menu;

        return content;
    }

    private static void RebuildMonth(
        CalendarDecorationState decoration, GlanceData data, CultureInfo culture,
        double width, double height, FrameworkElement content)
    {
        (GlanceCalendarMonth rebuilt, bool isCompact, double panelHeight, double panelWidth, double itemHeight, bool secondary, GlanceTraditionalCalendarMode mode) =
            GlanceMonthPipeline.Build(data.Settings.ShowChineseFestivals, data.Settings.TraditionalCalendarMode, culture, width, height);
        decoration.Update(rebuilt, itemHeight, mode != GlanceTraditionalCalendarMode.None, data.Settings.ShowChineseFestivals, secondary);
        content.DataContext = GlanceMonthPipeline.CreatePresentation(rebuilt, isCompact, panelHeight, panelWidth, culture, width, height);
    }

    private static string[] LoadImages(GlanceWidgetData settings, string packageRoot)
    {
        IEnumerable<string> files = settings.BackgroundSource switch
        {
            // Explicit file list: user order is meaningful, never re-sort.
            GlanceBackgroundSource.LocalFiles => settings.LocalImagePaths.Where(File.Exists),
            GlanceBackgroundSource.LocalFolder when !string.IsNullOrWhiteSpace(settings.LocalFolderPath) &&
                                                    Directory.Exists(settings.LocalFolderPath)
                => EnumerateImageFiles(settings.LocalFolderPath),
            _ => BundledBackgrounds(packageRoot, settings.BackgroundSource),
        };
        string[] images = files.ToArray();
        if (settings.RandomOrder && images.Length > 1)
        {
            // Approximate random rotation with a per-load shuffle.
            for (int index = images.Length - 1; index > 0; index--)
            {
                int swap = Random.Shared.Next(index + 1);
                (images[swap], images[index]) = (images[index], images[swap]);
            }
        }
        return images;
    }

    private static IEnumerable<string> BundledBackgrounds(string packageRoot, GlanceBackgroundSource configured)
    {
        if (configured is not (GlanceBackgroundSource.Online or GlanceBackgroundSource.Bing))
        {
            yield break;
        }
        // Online/Bing sources need a network path the package does not have
        // yet; fall back to the bundled backgrounds until that batch lands.
        PackageLogger.LogVerbose(
            $"[GlancePackage] background source {configured} is not available natively yet; using bundled backgrounds");
        foreach (string file in EnumerateImageFiles(Path.Combine(packageRoot, "backgrounds")))
        {
            yield return file;
        }
    }

    private static IEnumerable<string> EnumerateImageFiles(string directory)
    {
        // Built-in parity: the same extension set, and a failing folder
        // (network share hiccup, permissions) yields nothing instead of
        // throwing the whole widget away.
        foreach (string extension in ImageExtensions)
        {
            string[]? files = null;
            try
            {
                files = Directory.GetFiles(directory, "*" + extension);
            }
            catch (Exception error)
            {
                PackageLogger.LogVerbose($"[GlancePackage] failed to enumerate {directory}: {error.Message}");
                yield break;
            }
            foreach (string file in files)
            {
                yield return file;
            }
        }
    }

    private static void ShowGradientFallback(Border background)
    {
        // No resolvable image set: an explicit gradient surface instead of a
        // dead black widget (the official package ships no bundled images
        // yet; the online-source batch will replace this).
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
        };
        gradient.GradientStops.Add(new GradientStop { Color = Microsoft.UI.ColorHelper.FromArgb(255, 0x33, 0x3D, 0x4D), Offset = 0 });
        gradient.GradientStops.Add(new GradientStop { Color = Microsoft.UI.ColorHelper.FromArgb(255, 0x14, 0x14, 0x14), Offset = 1 });
        background.Background = gradient;
        background.Opacity = 1;
    }

    private sealed class CalendarDecorationState(
        GlanceCalendarMonth month, double dayItemHeight, bool showTraditional, bool showFestivals, bool showSecondary)
    {
        public GlanceCalendarMonth Month = month;
        public double DayItemHeight = dayItemHeight;
        public bool ShowTraditional = showTraditional;
        public bool ShowFestivals = showFestivals;
        public bool ShowSecondary = showSecondary;

        public void Update(GlanceCalendarMonth rebuilt, double itemHeight, bool traditional, bool festivals, bool secondary)
        {
            Month = rebuilt; DayItemHeight = itemHeight; ShowTraditional = traditional; ShowFestivals = festivals; ShowSecondary = secondary;
        }
    }

    private static void SubscribeDayDecoration(CalendarView calendarView, CalendarDecorationState decoration, CultureInfo culture)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        calendarView.CalendarViewDayItemChanging += (_, args) =>
        {
            CalendarViewDayItem item = args.Item;
            if (args.InRecycleQueue) { item.Tag = null; return; }
            DateOnly date = DateOnly.FromDateTime(item.Date.DateTime);
            GlanceCalendarDay? day = null;
            foreach (GlanceCalendarDay candidate in decoration.Month.Days)
            {
                if (candidate.Date == date) { day = candidate; break; }
            }
            // Built-in parity: the secondary line only renders when the
            // responsive layout says there is room for it (audit round 19).
            string secondaryText = !decoration.ShowSecondary || !decoration.ShowTraditional ? string.Empty
                : decoration.ShowFestivals && !string.IsNullOrWhiteSpace(day?.FestivalText) ? day.FestivalText
                : day?.TraditionalText ?? string.Empty;
            bool hasSecondaryText = !string.IsNullOrWhiteSpace(secondaryText);
            bool isFestival = hasSecondaryText && day?.HasFestival == true;
            bool isCurrentMonth = day?.IsCurrentMonth ?? date.Month == decoration.Month.Month.Month;
            item.MinHeight = decoration.DayItemHeight;
            item.Height = decoration.DayItemHeight;
            item.Tag = new GlanceDayDecoration(
                day?.DayText ?? date.Day.ToString(culture),
                secondaryText,
                hasSecondaryText ? Visibility.Visible : Visibility.Collapsed,
                date == today ? Visibility.Visible : Visibility.Collapsed,
                date == today ? Visibility.Collapsed : Visibility.Visible,
                isFestival ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                isCurrentMonth ? 1.0 : 0.42,
                !isCurrentMonth ? 0.34 : isFestival ? 0.88 : 0.62);
        };
    }
}

/// <summary>Pre-shaped bindable decoration (no converters in runtime XAML).</summary>
[WinRT.GeneratedBindableCustomProperty]
public sealed partial record GlanceDayDecoration(
    string DayText,
    string SecondaryText,
    Visibility SecondaryVisibility,
    Visibility TodayVisibility,
    Visibility NonTodayVisibility,
    Windows.UI.Text.FontWeight SecondaryFontWeight,
    double PrimaryOpacity,
    double SecondaryOpacity);

/// <summary>
/// Per-instance runtime state (paused flag, current image index). Settings
/// live in the lossless GlanceWidgetData document; only ephemeral runtime
/// bits persist here, atomically with a backup kept.
/// </summary>
internal sealed class GlanceRuntimeState
{
    public int ImageIndex;
    public bool Paused;

    public static GlanceRuntimeState LoadOrCreate(string instanceDataRoot)
    {
        string? content = PackageFileStore.TryReadText(Path.Combine(instanceDataRoot, "glance-state.json"));
        if (content is null) return new();
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            var state = new GlanceRuntimeState();
            if (document.RootElement.TryGetProperty("paused", out var p)) state.Paused = p.GetBoolean();
            if (document.RootElement.TryGetProperty("imageIndex", out var i)) state.ImageIndex = i.GetInt32();
            return state;
        }
        catch { return new(); }
    }

    public static void Save(GlanceRuntimeState state, string instanceDataRoot) =>
        PackageFileStore.WriteAtomically(
            Path.Combine(instanceDataRoot, "glance-state.json"),
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("paused", state.Paused);
                writer.WriteNumber("imageIndex", state.ImageIndex);
                writer.WriteEndObject();
            });
}
