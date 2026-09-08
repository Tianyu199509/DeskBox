using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Markup;
using WinRT;

namespace DeskBox.Glance.NativePackage;

/// <summary>
/// Full Glance slice: background image rotation, calendar (production services),
/// action bar with real click handling, right-click menu and a settings flyout
/// persisted to the package directory. Online image sources are out of scope
/// (batch D migrates the production image service); this slice loads images
/// from &lt;package&gt;\backgrounds\*.
/// </summary>
internal static class FullGlanceView
{
    private sealed class FullState
    {
        public int ImageIndex;
        public bool Paused;
        public bool ShowFestivals = true;
        public bool ShowTraditional = true;
        public int Decorated;
        public int Revision;
    }

    public static FrameworkElement Create(string root)
    {
        var state = LoadState(root);
        FrameworkElement content = (FrameworkElement)XamlReader.Load(
            File.ReadAllText(Path.Combine(root, "full-glance.xaml")));

        (GlanceCalendarMonth month, bool isCompact, double panelHeight, double panelWidth, double dayItemHeight, _) =
            RealGlanceModel.Build(state.ShowTraditional, state.ShowFestivals);
        var presentation = RealGlanceModel.CreatePresentation(month, isCompact, panelHeight, panelWidth);
        presentation.PlayIconVisibility = state.Paused ? Visibility.Visible : Visibility.Collapsed;
        presentation.PauseIconVisibility = state.Paused ? Visibility.Collapsed : Visibility.Visible;
        content.DataContext = presentation;

        var calendarView = content.FindName("NativeCalendarView").As<CalendarView>();
        // Single subscription (audit round 11): settings changes update the
        // decoration state in place; re-subscribing per rebuild would stack
        // handlers that capture stale calendar state.
        var decoration = new CalendarDecorationState(month, dayItemHeight, state.ShowTraditional, state.ShowFestivals);
        SubscribeDayDecoration(calendarView, decoration);

        // Image pipeline: package-local folder, A/B cross-fade borders.
        string[] images = Directory.Exists(Path.Combine(root, "backgrounds"))
            ? Directory.GetFiles(Path.Combine(root, "backgrounds"), "*.png")
                .Concat(Directory.GetFiles(Path.Combine(root, "backgrounds"), "*.jpg"))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        var backgroundA = content.FindName("BackgroundA").As<Border>();
        var backgroundB = content.FindName("BackgroundB").As<Border>();
        bool showingA = true;
        void Show(int index)
        {
            if (images.Length == 0) return;
            state.ImageIndex = ((index % images.Length) + images.Length) % images.Length;
            var brush = new ImageBrush { ImageSource = new BitmapImage(new Uri(images[state.ImageIndex])), Stretch = Stretch.UniformToFill };
            Border next = showingA ? backgroundB : backgroundA;
            Border fadeOut = showingA ? backgroundA : backgroundB;
            next.Background = brush;
            next.Opacity = 1;
            fadeOut.Opacity = 0;
            showingA = !showingA;
            WriteSummary(root, images, state, month);
        }
        Show(state.ImageIndex);

        var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(3);
        timer.Tick += (_, _) =>
        {
            if (!state.Paused) Show(state.ImageIndex + 1);
        };
        if (!state.Paused && images.Length > 1) timer.Start();
        content.Unloaded += (_, _) => timer.Stop();

        var pauseButton = content.FindName("PauseButton").As<Button>();
        void TogglePause()
        {
            state.Paused = !state.Paused;
            if (state.Paused) timer.Stop();
            else if (images.Length > 1) timer.Start();
            presentation.PlayIconVisibility = state.Paused ? Visibility.Visible : Visibility.Collapsed;
            presentation.PauseIconVisibility = state.Paused ? Visibility.Collapsed : Visibility.Visible;
            WriteSummary(root, images, state, month);
        }
        pauseButton.Click += (_, _) => TogglePause();
        var nextButton = content.FindName("NextButton").As<Button>();
        nextButton.Click += (_, _) => Show(state.ImageIndex + 1);

        // Settings panel lives in the XAML namescope (host-side probes can drive
        // the same Toggled handlers); the menu item reveals it.
        var settingsLayer = content.FindName("SettingsLayer").As<FrameworkElement>();
        var festivalToggle = content.FindName("FestivalToggle").As<ToggleSwitch>();
        var traditionalToggle = content.FindName("TraditionalToggle").As<ToggleSwitch>();
        festivalToggle.IsOn = state.ShowFestivals;
        traditionalToggle.IsOn = state.ShowTraditional;
        festivalToggle.Toggled += (_, _) =>
        {
            state.ShowFestivals = festivalToggle.IsOn;
            Rebuild(root, state, calendarView, decoration, dayItemHeight);
            SaveState(root, state);
        };
        traditionalToggle.Toggled += (_, _) =>
        {
            state.ShowTraditional = traditionalToggle.IsOn;
            Rebuild(root, state, calendarView, decoration, dayItemHeight);
            SaveState(root, state);
        };

        // Right-click menu (code-built: runtime XAML cannot wire handlers).
        var menu = new MenuFlyout();
        var nextItem = new MenuFlyoutItem { Text = "下一张背景" };
        nextItem.Click += (_, _) => Show(state.ImageIndex + 1);
        var pauseItem = new MenuFlyoutItem { Text = "暂停轮播" };
        pauseItem.Click += (_, _) => TogglePause();
        var settingsItem = new MenuFlyoutItem { Text = "设置" };
        settingsItem.Click += (_, _) =>
        {
            settingsLayer.Visibility = settingsLayer.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        };
        var aboutItem = new MenuFlyoutItem { Text = "DeskBox.Glance.NativePackage 完整切片" };
        menu.Items.Add(nextItem);
        menu.Items.Add(pauseItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(aboutItem);
        content.ContextFlyout = menu;

        content.Loaded += (_, _) => WriteSummary(root, images, state, month);
        return content;
    }

    private static void Rebuild(string root, FullState state, CalendarView calendarView, CalendarDecorationState decoration, double dayItemHeight)
    {
        (GlanceCalendarMonth rebuilt, _, _, _, _, _) = RealGlanceModel.Build(state.ShowTraditional, state.ShowFestivals);
        decoration.Update(rebuilt, dayItemHeight, state.ShowTraditional, state.ShowFestivals);
        WriteSummary(root, [], state, rebuilt);
    }

    /// <summary>Mutable decoration state read by the single day-item handler.</summary>
    private sealed class CalendarDecorationState(
        GlanceCalendarMonth month,
        double dayItemHeight,
        bool showTraditional,
        bool showFestivals)
    {
        public GlanceCalendarMonth Month = month;
        public double DayItemHeight = dayItemHeight;
        public bool ShowTraditional = showTraditional;
        public bool ShowFestivals = showFestivals;

        public void Update(GlanceCalendarMonth rebuilt, double itemHeight, bool traditional, bool festivals)
        {
            Month = rebuilt;
            DayItemHeight = itemHeight;
            ShowTraditional = traditional;
            ShowFestivals = festivals;
        }
    }

    private static void SubscribeDayDecoration(CalendarView calendarView, CalendarDecorationState decoration)
    {
        System.Globalization.CultureInfo culture = RealGlanceModel.Culture;
        DateOnly pinned = new(RealGlanceModel.PinnedYear, RealGlanceModel.PinnedMonth, 1);
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
            bool isCurrentMonth = day?.IsCurrentMonth ?? date.Month == pinned.Month;
            item.MinHeight = decoration.DayItemHeight;
            item.Height = decoration.DayItemHeight;
            item.Tag = new RealGlanceDayDecoration(
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

    private static FullState LoadState(string root)
    {
        var state = new FullState();
        string path = Path.Combine(root, "glance-settings.json");
        if (!File.Exists(path)) return state;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.TryGetProperty("showFestivals", out JsonElement festivals))
            state.ShowFestivals = festivals.GetBoolean();
        if (document.RootElement.TryGetProperty("showTraditional", out JsonElement traditional))
            state.ShowTraditional = traditional.GetBoolean();
        if (document.RootElement.TryGetProperty("paused", out JsonElement paused))
            state.Paused = paused.GetBoolean();
        return state;
    }

    private static void SaveState(string root, FullState state)
    {
        using var stream = File.Create(Path.Combine(root, "glance-settings.json"));
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteBoolean("showFestivals", state.ShowFestivals);
        writer.WriteBoolean("showTraditional", state.ShowTraditional);
        writer.WriteBoolean("paused", state.Paused);
        writer.WriteEndObject();
    }

    private static void WriteSummary(string root, string[] images, FullState state, GlanceCalendarMonth month)
    {
        state.Revision++;
        File.AppendAllText(Path.Combine(root, "full-writes.log"),
            $"r{state.Revision} idx={state.ImageIndex} imgs={images.Length} fest={state.ShowFestivals} trad={state.ShowTraditional} paused={state.Paused} t={DateTime.Now:HH:mm:ss.fff}\n");
        using var stream = File.Create(Path.Combine(root, "full-summary.json"));
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteNumber("revision", state.Revision);
        writer.WriteString("variant", "full-glance");
        writer.WriteNumber("imageCount", images.Length);
        writer.WriteNumber("currentImageIndex", state.ImageIndex);
        writer.WriteBoolean("paused", state.Paused);
        writer.WriteBoolean("showFestivals", state.ShowFestivals);
        writer.WriteBoolean("showTraditional", state.ShowTraditional);
        writer.WriteNumber("festivalDayCount", month.Days.Count(day => day.HasFestival));
        writer.WriteNumber("traditionalTextDayCount", month.Days.Count(day => !string.IsNullOrWhiteSpace(day.TraditionalText)));
        writer.WriteString("traditionalTitle", month.TraditionalTitle);
        writer.WriteEndObject();
    }
}
