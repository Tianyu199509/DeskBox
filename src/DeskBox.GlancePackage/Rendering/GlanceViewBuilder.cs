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
/// package-owned production services. D3 product migration: locale comes
/// from the host config channel, the month view is the current month,
/// settings come from the migrated GlanceWidgetData file (legacy store
/// handoff), and the presentation re-builds (debounced) when the host
/// reports viewport changes.
/// </summary>
internal static class GlanceViewBuilder
{
    public static FrameworkElement Create(
        string packageRoot, string contributionId, string instanceId, string instanceDataRoot,
        out Action<double, double>? onViewportChanged)
    {
        CultureInfo culture = HostConfig.TryGetCulture() ?? CultureInfo.CurrentUICulture;
        PackageStrings.Configure(culture, packageRoot);

        // Settings: migrated GlanceWidgetData (host legacy store handoff) or
        // model defaults; runtime bits (paused, image index) stay separate.
        GlanceWidgetData data = GlanceDataFile.Load(instanceDataRoot) ?? new GlanceWidgetData();
        var runtimeState = GlanceRuntimeState.LoadOrCreate(instanceDataRoot);
        bool showTraditional = data.TraditionalCalendarMode != GlanceTraditionalCalendarMode.None;
        bool showFestivals = data.ShowChineseFestivals;

        double width = GlanceMonthPipeline.DefaultWidth;
        double height = GlanceMonthPipeline.DefaultHeight;
        (GlanceCalendarMonth month, bool isCompact, double panelHeight, double panelWidth, double dayItemHeight, bool showSecondary) =
            GlanceMonthPipeline.Build(showTraditional, showFestivals, culture, width, height);

        var presentation = GlanceMonthPipeline.CreatePresentation(month, isCompact, panelHeight, panelWidth, culture, width, height);
        FrameworkElement content = (FrameworkElement)XamlReader.Load(
            File.ReadAllText(Path.Combine(packageRoot, "glance.xaml")));
        content.DataContext = presentation;

        // Calendar day decoration (single subscription, mutable state).
        var calendarView = content.FindName("NativeCalendarView").As<CalendarView>();
        var decoration = new CalendarDecorationState(month, dayItemHeight, showTraditional, showFestivals);
        SubscribeDayDecoration(calendarView, decoration, culture);

        // Background rotation. Image set follows GlanceWidgetData: explicit
        // local files, a local folder, or the bundled backgrounds when the
        // configured source needs a capability the package does not have yet.
        string[] images = LoadImages(data, packageRoot);
        Stretch imageStretch = data.ImageFit == GlanceImageFitMode.Fit ? Stretch.Uniform : Stretch.UniformToFill;
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
        Show(runtimeState.ImageIndex);

        var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMinutes(Math.Clamp(data.RotationIntervalMinutes, 0.1, 1440));
        timer.Tick += (_, _) => { if (!runtimeState.Paused) Show(runtimeState.ImageIndex + 1); };
        if (!runtimeState.Paused && images.Length > 1) timer.Start();

        // Action bar (hidden when the user turned photo controls off).
        var pauseButton = content.FindName("PauseButton").As<Button>();
        var nextButton = content.FindName("NextButton").As<Button>();
        if (!data.ShowPhotoControls)
        {
            pauseButton.Visibility = Visibility.Collapsed;
            nextButton.Visibility = Visibility.Collapsed;
        }
        void TogglePause()
        {
            runtimeState.Paused = !runtimeState.Paused;
            if (runtimeState.Paused) timer.Stop();
            else if (images.Length > 1) timer.Start();
        }
        pauseButton.Click += (_, _) => TogglePause();
        nextButton.Click += (_, _) => Show(runtimeState.ImageIndex + 1);

        // Settings panel (in-namescope for host-side probes). Toggles now
        // mutate the migrated data and persist it in the package's format.
        var settingsLayer = content.FindName("SettingsLayer").As<FrameworkElement>();
        var festivalToggle = content.FindName("FestivalToggle").As<ToggleSwitch>();
        var traditionalToggle = content.FindName("TraditionalToggle").As<ToggleSwitch>();
        festivalToggle.IsOn = showFestivals;
        traditionalToggle.IsOn = showTraditional;
        festivalToggle.Toggled += (_, _) =>
        {
            data.ShowChineseFestivals = festivalToggle.IsOn;
            RebuildMonth(decoration, data, culture, width, height, content);
            GlanceDataFile.Save(data, instanceDataRoot);
        };
        traditionalToggle.Toggled += (_, _) =>
        {
            data.TraditionalCalendarMode = traditionalToggle.IsOn
                ? GlanceTraditionalCalendarMode.ChineseLunar
                : GlanceTraditionalCalendarMode.None;
            RebuildMonth(decoration, data, culture, width, height, content);
            GlanceDataFile.Save(data, instanceDataRoot);
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
        CalendarDecorationState decoration, GlanceWidgetData data, CultureInfo culture,
        double width, double height, FrameworkElement content)
    {
        bool showTraditional = data.TraditionalCalendarMode != GlanceTraditionalCalendarMode.None;
        (GlanceCalendarMonth rebuilt, bool isCompact, double panelHeight, double panelWidth, double itemHeight, _) =
            GlanceMonthPipeline.Build(showTraditional, data.ShowChineseFestivals, culture, width, height);
        decoration.Update(rebuilt, itemHeight, showTraditional, data.ShowChineseFestivals);
        content.DataContext = GlanceMonthPipeline.CreatePresentation(rebuilt, isCompact, panelHeight, panelWidth, culture, width, height);
    }

    private static string[] LoadImages(GlanceWidgetData data, string packageRoot)
    {
        IEnumerable<string> files = data.BackgroundSource switch
        {
            GlanceBackgroundSource.LocalFiles => data.LocalImagePaths.Where(File.Exists),
            GlanceBackgroundSource.LocalFolder when !string.IsNullOrWhiteSpace(data.LocalFolderPath) &&
                                                    Directory.Exists(data.LocalFolderPath)
                => EnumerateImageFiles(data.LocalFolderPath),
            _ => BundledBackgrounds(packageRoot, data.BackgroundSource),
        };
        string[] images = files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (data.RandomOrder && images.Length > 1)
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
        if (!Directory.Exists(directory)) yield break;
        foreach (string file in Directory.GetFiles(directory, "*.png")) yield return file;
        foreach (string file in Directory.GetFiles(directory, "*.jpg")) yield return file;
    }

    private sealed class CalendarDecorationState(
        GlanceCalendarMonth month, double dayItemHeight, bool showTraditional, bool showFestivals)
    {
        public GlanceCalendarMonth Month = month;
        public double DayItemHeight = dayItemHeight;
        public bool ShowTraditional = showTraditional;
        public bool ShowFestivals = showFestivals;

        public void Update(GlanceCalendarMonth rebuilt, double itemHeight, bool traditional, bool festivals)
        {
            Month = rebuilt; DayItemHeight = itemHeight; ShowTraditional = traditional; ShowFestivals = festivals;
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
            string secondaryText = !decoration.ShowTraditional ? string.Empty
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
/// live in the migrated GlanceWidgetData file; only ephemeral runtime bits
/// persist here.
/// </summary>
internal sealed class GlanceRuntimeState
{
    public int ImageIndex;
    public bool Paused;

    public static GlanceRuntimeState LoadOrCreate(string instanceDataRoot)
    {
        string path = Path.Combine(instanceDataRoot, "glance-state.json");
        if (!File.Exists(path)) return new();
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            var state = new GlanceRuntimeState();
            if (document.RootElement.TryGetProperty("paused", out var p)) state.Paused = p.GetBoolean();
            if (document.RootElement.TryGetProperty("imageIndex", out var i)) state.ImageIndex = i.GetInt32();
            return state;
        }
        catch { return new(); }
    }

    public static void Save(GlanceRuntimeState state, string instanceDataRoot)
    {
        Directory.CreateDirectory(instanceDataRoot);
        using var stream = File.Create(Path.Combine(instanceDataRoot, "glance-state.json"));
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteBoolean("paused", state.Paused);
        writer.WriteNumber("imageIndex", state.ImageIndex);
        writer.WriteEndObject();
    }
}
