using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class GlanceWidgetStoreTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBox.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LoadAsync_UsesPhaseOneDefaults()
    {
        var store = new GlanceWidgetStore(_tempRoot);

        GlanceWidgetData data = await store.LoadAsync();

        Assert.Equal(GlanceWidgetData.CurrentVersion, data.Version);
        Assert.True(data.ShowTime);
        Assert.True(data.ShowDate);
        Assert.False(data.ShowYear);
        Assert.True(data.ShowWeekday);
        Assert.False(data.ShowCalendar);
        Assert.Equal(GlanceLayoutMode.Centered, data.Layout);
        Assert.Equal(GlanceBackgroundSource.Bing, data.BackgroundSource);
        Assert.Equal(GlanceOnlineImageCategory.Featured, data.OnlineImageCategory);
        Assert.Equal(GlanceTimeFormatMode.FollowSystem, data.TimeFormat);
        Assert.Equal(30d, data.RotationIntervalMinutes);
        Assert.Equal(GlanceTransitionMode.CrossFade, data.Transition);
        Assert.Equal(0, data.BackgroundImageTransparency);
        Assert.Equal(GlanceCalendarMaterialMode.FollowSystem, data.CalendarMaterialMode);
        Assert.Equal(0.32, data.CalendarImageMaterialTransparency, precision: 2);
        Assert.Equal(GlanceTraditionalCalendarMode.None, data.TraditionalCalendarMode);
        Assert.True(data.ShowChineseFestivals);
        Assert.True(data.ShowPhotoControls);
    }

    [Fact]
    public async Task SaveAsync_PreservesYearOnlyWhenDateIsVisible()
    {
        var store = new GlanceWidgetStore(_tempRoot);
        await store.SaveAsync(new GlanceWidgetData
        {
            ShowDate = true,
            ShowYear = true
        });

        GlanceWidgetData withDate = await store.LoadAsync();
        Assert.True(withDate.ShowYear);

        withDate.ShowDate = false;
        await store.SaveAsync(withDate);

        GlanceWidgetData withoutDate = await store.LoadAsync();
        Assert.False(withoutDate.ShowDate);
        Assert.False(withoutDate.ShowYear);
    }

    [Fact]
    public async Task SaveAsync_NormalizesUnknownTimeFormat()
    {
        var store = new GlanceWidgetStore(_tempRoot);
        await store.SaveAsync(new GlanceWidgetData
        {
            TimeFormat = (GlanceTimeFormatMode)999
        });

        GlanceWidgetData reloaded = await store.LoadAsync();

        Assert.Equal(GlanceTimeFormatMode.FollowSystem, reloaded.TimeFormat);
    }

    [Fact]
    public async Task SaveAsync_PreservesPhotoOnlyModeAndNormalizesPaths()
    {
        var store = new GlanceWidgetStore(_tempRoot);
        await store.SaveAsync(new GlanceWidgetData
        {
            ShowTime = false,
            ShowDate = false,
            ShowWeekday = false,
            ShowCalendar = false,
            BackgroundSource = GlanceBackgroundSource.LocalFiles,
            OnlineImageCategory = GlanceOnlineImageCategory.Astronomy,
            LocalImagePaths = [" C:\\Pictures\\one.jpg ", "c:\\pictures\\ONE.jpg", ""],
            RotationIntervalMinutes = 17,
            TimeScale = 9,
            ShowPhotoControls = false
        });

        GlanceWidgetData reloaded = await new GlanceWidgetStore(_tempRoot).LoadAsync();

        Assert.False(reloaded.ShowTime);
        Assert.False(reloaded.ShowDate);
        Assert.False(reloaded.ShowWeekday);
        Assert.False(reloaded.ShowCalendar);
        Assert.Single(reloaded.LocalImagePaths);
        Assert.Equal(GlanceOnlineImageCategory.Astronomy, reloaded.OnlineImageCategory);
        Assert.Equal(@"C:\Pictures\one.jpg", reloaded.LocalImagePaths[0]);
        Assert.Equal(30d, reloaded.RotationIntervalMinutes);
        Assert.Equal(1.35, reloaded.TimeScale);
        Assert.False(reloaded.ShowPhotoControls);
    }

    [Fact]
    public async Task LoadAsync_ReturnsCopiesThatCannotMutateCachedState()
    {
        var store = new GlanceWidgetStore(_tempRoot);
        GlanceWidgetData first = await store.LoadAsync();
        first.ShowTime = false;

        GlanceWidgetData second = await store.LoadAsync();

        Assert.True(second.ShowTime);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(10d / 60d)]
    [InlineData(30d / 60d)]
    [InlineData(1d)]
    [InlineData(2d)]
    [InlineData(5d)]
    [InlineData(10d)]
    [InlineData(30d)]
    public async Task SaveAsync_PreservesSupportedShortRotationIntervals(double intervalMinutes)
    {
        var store = new GlanceWidgetStore(_tempRoot);
        await store.SaveAsync(new GlanceWidgetData
        {
            RotationIntervalMinutes = intervalMinutes
        });

        GlanceWidgetData reloaded = await new GlanceWidgetStore(_tempRoot).LoadAsync();

        Assert.Equal(intervalMinutes, reloaded.RotationIntervalMinutes, precision: 6);
    }

    [Fact]
    public async Task SaveAsync_PreservesImageMaterialAndClampsTransparency()
    {
        var store = new GlanceWidgetStore(_tempRoot);
        await store.SaveAsync(new GlanceWidgetData
        {
            CalendarMaterialMode = GlanceCalendarMaterialMode.FollowImage,
            BackgroundImageTransparency = 4,
            CalendarImageMaterialTransparency = 4,
            TraditionalCalendarMode = GlanceTraditionalCalendarMode.Hebrew,
            ShowChineseFestivals = false
        });

        GlanceWidgetData reloaded = await new GlanceWidgetStore(_tempRoot).LoadAsync();

        Assert.Equal(GlanceCalendarMaterialMode.FollowImage, reloaded.CalendarMaterialMode);
        Assert.Equal(1, reloaded.BackgroundImageTransparency);
        Assert.Equal(1, reloaded.CalendarImageMaterialTransparency);
        Assert.Equal(GlanceTraditionalCalendarMode.Hebrew, reloaded.TraditionalCalendarMode);
        Assert.False(reloaded.ShowChineseFestivals);
    }

    [Theory]
    [InlineData(-2, 0)]
    [InlineData(0, 0)]
    [InlineData(0.42, 0.42)]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    public async Task SaveAsync_ClampsBackgroundImageTransparency(
        double requested,
        double expected)
    {
        var store = new GlanceWidgetStore(_tempRoot);
        await store.SaveAsync(new GlanceWidgetData
        {
            BackgroundImageTransparency = requested
        });

        GlanceWidgetData reloaded = await new GlanceWidgetStore(_tempRoot).LoadAsync();

        Assert.Equal(expected, reloaded.BackgroundImageTransparency, precision: 6);
    }

    [Fact]
    public async Task UpdateAsync_ResetsNonFiniteBackgroundImageTransparency()
    {
        var store = new GlanceWidgetStore(_tempRoot);
        await store.UpdateAsync(settings =>
            settings.BackgroundImageTransparency = double.NaN);

        GlanceWidgetData reloaded = await new GlanceWidgetStore(_tempRoot).LoadAsync();

        Assert.Equal(0, reloaded.BackgroundImageTransparency);
    }

    [Fact]
    public async Task PerWidgetStores_KeepPreferencesIndependent()
    {
        var first = new GlanceWidgetStore(_tempRoot, "first-widget");
        var second = new GlanceWidgetStore(_tempRoot, "second-widget");

        await first.SaveAsync(new GlanceWidgetData
        {
            ShowTime = false,
            BackgroundSource = GlanceBackgroundSource.LocalFiles,
            LocalImagePaths = [@"C:\Pictures\first.jpg"],
            BackgroundImageTransparency = 0.64
        });
        await second.SaveAsync(new GlanceWidgetData
        {
            ShowTime = true,
            BackgroundSource = GlanceBackgroundSource.Bing
        });

        GlanceWidgetData firstReloaded = await new GlanceWidgetStore(
            _tempRoot,
            "first-widget").LoadAsync();
        GlanceWidgetData secondReloaded = await new GlanceWidgetStore(
            _tempRoot,
            "second-widget").LoadAsync();

        Assert.NotEqual(first.StorePath, second.StorePath);
        Assert.False(firstReloaded.ShowTime);
        Assert.Single(firstReloaded.LocalImagePaths);
        Assert.Equal(0.64, firstReloaded.BackgroundImageTransparency, precision: 2);
        Assert.True(secondReloaded.ShowTime);
        Assert.Empty(secondReloaded.LocalImagePaths);
        Assert.Equal(0, secondReloaded.BackgroundImageTransparency);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }
}
