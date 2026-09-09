using System.Globalization;
using System.Text.Json;
using DeskBox.GlancePackage.Services;
using DeskBox.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.GlancePackage.Rendering;

/// <summary>
/// Static construction helpers for the glance widget view; the live
/// instance state (timers, lifecycle, settings) lives in
/// GlanceWidgetController (audit rounds 18-19).
/// </summary>
internal static class GlanceViewBuilder
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];

    internal static string[] LoadImages(GlanceWidgetData settings, string packageRoot)
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

    internal static void ShowGradientFallback(Border background)
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

    internal static void SubscribeDayDecoration(CalendarView calendarView, CalendarDecorationState decoration)
    {
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
            // Regression pass: "today" and the culture are read PER CALL, not
            // captured - a captured snapshot left the yesterday highlight
            // marked as today after any midnight rollover, and kept the old
            // locale's digit shaping after a live language switch.
            DateOnly today = DateOnly.FromDateTime(DateTime.Today);
            item.MinHeight = decoration.DayItemHeight;
            item.Height = decoration.DayItemHeight;
            item.Tag = new GlanceDayDecoration(
                day?.DayText ?? date.Day.ToString(decoration.Culture),
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

/// <summary>Mutable decoration state shared with the day-item callback.</summary>
internal sealed class CalendarDecorationState(
    GlanceCalendarMonth month, double dayItemHeight, bool showTraditional, bool showFestivals, bool showSecondary,
    CultureInfo culture)
{
    public GlanceCalendarMonth Month = month;
    public double DayItemHeight = dayItemHeight;
    public bool ShowTraditional = showTraditional;
    public bool ShowFestivals = showFestivals;
    public bool ShowSecondary = showSecondary;
    public CultureInfo Culture = culture;

    public void Update(GlanceCalendarMonth rebuilt, double itemHeight, bool traditional, bool festivals, bool secondary, CultureInfo culture)
    {
        Month = rebuilt; DayItemHeight = itemHeight; ShowTraditional = traditional; ShowFestivals = festivals; ShowSecondary = secondary; Culture = culture;
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

    /// <summary>
    /// Total no-throw variant (audit round 20): ephemeral runtime state must
    /// never turn into an ABI destroy failure, so a failed save is logged
    /// and reported as false instead of propagating.
    /// </summary>
    public static bool TrySave(GlanceRuntimeState state, string instanceDataRoot)
    {
        try
        {
            Save(state, instanceDataRoot);
            return true;
        }
        catch (Exception error)
        {
            PackageLogger.LogVerbose($"[GlancePackage] failed to save runtime state: {error.Message}");
            return false;
        }
    }
}
