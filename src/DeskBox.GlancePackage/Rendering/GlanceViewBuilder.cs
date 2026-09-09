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
/// from the host config channel, the month view is the current month, and
/// the presentation re-builds (debounced) when the host reports viewport
/// changes.
/// </summary>
internal static class GlanceViewBuilder
{
    public static FrameworkElement Create(
        string packageRoot, string contributionId, string instanceId, string instanceDataRoot,
        out Action<double, double>? onViewportChanged)
    {
        CultureInfo culture = HostConfig.TryGetCulture() ?? CultureInfo.CurrentUICulture;
        PackageStrings.Configure(culture, packageRoot);

        // Build the month data through production services.
        var state = GlancePackageState.LoadOrCreate(instanceDataRoot);
        double width = GlanceMonthPipeline.DefaultWidth;
        double height = GlanceMonthPipeline.DefaultHeight;
        (GlanceCalendarMonth month, bool isCompact, double panelHeight, double panelWidth, double dayItemHeight, bool showSecondary) =
            GlanceMonthPipeline.Build(state.ShowTraditional, state.ShowFestivals, culture, width, height);

        var presentation = GlanceMonthPipeline.CreatePresentation(month, isCompact, panelHeight, panelWidth, culture, width, height);
        FrameworkElement content = (FrameworkElement)XamlReader.Load(
            File.ReadAllText(Path.Combine(packageRoot, "glance.xaml")));
        content.DataContext = presentation;

        // Calendar day decoration (single subscription, mutable state).
        var calendarView = content.FindName("NativeCalendarView").As<CalendarView>();
        var decoration = new CalendarDecorationState(month, dayItemHeight, state.ShowTraditional, state.ShowFestivals);
        SubscribeDayDecoration(calendarView, decoration, culture);

        // Background image rotation from package-local backgrounds/ folder.
        string[] images = LoadImages(Path.Combine(packageRoot, "backgrounds"));
        var backgroundA = content.FindName("BackgroundA").As<Border>();
        var backgroundB = content.FindName("BackgroundB").As<Border>();
        bool showingA = true;
        void Show(int index)
        {
            if (images.Length == 0) return;
            state.ImageIndex = ((index % images.Length) + images.Length) % images.Length;
            var brush = new ImageBrush { ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(images[state.ImageIndex])), Stretch = Stretch.UniformToFill };
            Border next = showingA ? backgroundB : backgroundA;
            Border fadeOut = showingA ? backgroundA : backgroundB;
            next.Background = brush;
            next.Opacity = 1;
            fadeOut.Opacity = 0;
            showingA = !showingA;
        }
        Show(state.ImageIndex);

        var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(3);
        timer.Tick += (_, _) => { if (!state.Paused) Show(state.ImageIndex + 1); };
        if (!state.Paused && images.Length > 1) timer.Start();

        // Action bar.
        var pauseButton = content.FindName("PauseButton").As<Button>();
        void TogglePause()
        {
            state.Paused = !state.Paused;
            if (state.Paused) timer.Stop();
            else if (images.Length > 1) timer.Start();
        }
        pauseButton.Click += (_, _) => TogglePause();
        var nextButton = content.FindName("NextButton").As<Button>();
        nextButton.Click += (_, _) => Show(state.ImageIndex + 1);

        // Settings panel (in-namescope for host-side probes).
        var settingsLayer = content.FindName("SettingsLayer").As<FrameworkElement>();
        var festivalToggle = content.FindName("FestivalToggle").As<ToggleSwitch>();
        var traditionalToggle = content.FindName("TraditionalToggle").As<ToggleSwitch>();
        festivalToggle.IsOn = state.ShowFestivals;
        traditionalToggle.IsOn = state.ShowTraditional;
        festivalToggle.Toggled += (_, _) =>
        {
            state.ShowFestivals = festivalToggle.IsOn;
            RebuildMonth(decoration, state, culture, width, height, content);
            state.Save(instanceDataRoot);
        };
        traditionalToggle.Toggled += (_, _) =>
        {
            state.ShowTraditional = traditionalToggle.IsOn;
            RebuildMonth(decoration, state, culture, width, height, content);
            state.Save(instanceDataRoot);
        };

        // Debounced responsive rebuild: the host can report viewport changes
        // continuously during a resize; the pipeline (month data + panel
        // sizing + presentation) re-runs once per settled size.
        var resizeTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        resizeTimer.Interval = TimeSpan.FromMilliseconds(120);
        resizeTimer.IsRepeating = false;
        resizeTimer.Tick += (_, _) => RebuildMonth(decoration, state, culture, width, height, content);
        onViewportChanged = (newWidth, newHeight) =>
        {
            if (Math.Abs(newWidth - width) < 1 && Math.Abs(newHeight - height) < 1) return;
            width = newWidth;
            height = newHeight;
            resizeTimer.Stop();
            resizeTimer.Start();
        };
        content.Unloaded += (_, _) => { timer.Stop(); resizeTimer.Stop(); };

        // Right-click menu (code-built: runtime XAML cannot wire handlers).
        var menu = new MenuFlyout();
        var nextItem = new MenuFlyoutItem { Text = PackageStrings.Get("menuNextBackground", "下一张背景") };
        nextItem.Click += (_, _) => Show(state.ImageIndex + 1);
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
        CalendarDecorationState decoration, GlancePackageState state, CultureInfo culture,
        double width, double height, FrameworkElement content)
    {
        (GlanceCalendarMonth rebuilt, bool isCompact, double panelHeight, double panelWidth, double itemHeight, _) =
            GlanceMonthPipeline.Build(state.ShowTraditional, state.ShowFestivals, culture, width, height);
        decoration.Update(rebuilt, itemHeight, state.ShowTraditional, state.ShowFestivals);
        content.DataContext = GlanceMonthPipeline.CreatePresentation(rebuilt, isCompact, panelHeight, panelWidth, culture, width, height);
    }

    private static string[] LoadImages(string backgroundsDirectory) =>
        Directory.Exists(backgroundsDirectory)
            ? Directory.GetFiles(backgroundsDirectory, "*.png")
                .Concat(Directory.GetFiles(backgroundsDirectory, "*.jpg"))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

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

/// <summary>Per-instance persisted state.</summary>
internal sealed class GlancePackageState
{
    public int ImageIndex;
    public bool Paused;
    public bool ShowFestivals = true;
    public bool ShowTraditional = true;

    public static GlancePackageState LoadOrCreate(string instanceDataRoot)
    {
        string path = Path.Combine(instanceDataRoot, "glance-state.json");
        if (!File.Exists(path)) return new();
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            var state = new GlancePackageState();
            if (document.RootElement.TryGetProperty("showFestivals", out var f)) state.ShowFestivals = f.GetBoolean();
            if (document.RootElement.TryGetProperty("showTraditional", out var t)) state.ShowTraditional = t.GetBoolean();
            if (document.RootElement.TryGetProperty("paused", out var p)) state.Paused = p.GetBoolean();
            if (document.RootElement.TryGetProperty("imageIndex", out var i)) state.ImageIndex = i.GetInt32();
            return state;
        }
        catch { return new(); }
    }

    public void Save(string instanceDataRoot)
    {
        Directory.CreateDirectory(instanceDataRoot);
        using var stream = File.Create(Path.Combine(instanceDataRoot, "glance-state.json"));
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteBoolean("showFestivals", ShowFestivals);
        writer.WriteBoolean("showTraditional", ShowTraditional);
        writer.WriteBoolean("paused", Paused);
        writer.WriteNumber("imageIndex", ImageIndex);
        writer.WriteEndObject();
    }
}
