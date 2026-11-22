using DeskBox.Services;
using DeskBox.ViewModels;
using System.Globalization;
using System.Xml.Linq;

namespace DeskBox.Tests;

public sealed class WeatherWidgetViewModelTests
{
    [Fact]
    public void WeatherTextBlocks_DoNotUseUnreadableFixedFontSizes()
    {
        XDocument document = XDocument.Load(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/WeatherWidgetContent.xaml"));

        XElement[] undersizedText = document.Descendants()
            .Where(element => element.Name.LocalName == "TextBlock")
            .Where(element =>
                double.TryParse(
                    (string?)element.Attribute("FontSize"),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double fontSize) &&
                fontSize < 12)
            .ToArray();

        Assert.Empty(undersizedText);
    }

    [Fact]
    public void HourlyTypography_UsesAotSafeTypedBindings()
    {
        XDocument document = XDocument.Load(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/WeatherWidgetContent.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement hourlyItems = document.Descendants()
            .Single(element =>
                element.Name.LocalName == "ItemsControl" &&
                (string?)element.Attribute(x + "Name") == "ExpandedHourlyItems");

        // When outer layout grids use x:Load, WinUI's XAML compiler emits a zero-initialized
        // FontSizeDeferredValue in DataTemplate x:Bind Connect() before DataContextChanged runs,
        // throwing E_INVALIDARG on TextBlock.FontSize = 0.0. Using {Binding ForecastHourTextSize}
        // backed by WeatherHourViewModel's GeneratedBindableCustomProperty avoids Connect(0.0)
        // while staying AOT-safe.
        Assert.Equal(
            2,
            hourlyItems.Descendants()
                .Count(element =>
                    (string?)element.Attribute("FontSize") ==
                    "{Binding ForecastHourTextSize}"));

        string aotBindableSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/WeatherViewModels.AotBindableProperties.cs"));
        Assert.Contains("nameof(ForecastHourTextSize)", aotBindableSource, StringComparison.Ordinal);
        Assert.Contains("public sealed partial class WeatherHourViewModel", aotBindableSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(150, 104)]
    [InlineData(178, 134)]
    public void DetermineLayoutMode_KeepsSmallWidgetsInMini(double width, double contentHeight)
    {
        string layout = WeatherWidgetViewModel.DetermineLayoutMode(width, contentHeight, "Mini");

        Assert.Equal("Mini", layout);
    }

    [Theory]
    [InlineData(190, 145)]
    [InlineData(200, 154)]
    public void DetermineLayoutMode_UsesCompactLayoutFromMediumWidgetSize(double width, double contentHeight)
    {
        string layout = WeatherWidgetViewModel.DetermineLayoutMode(width, contentHeight, "Mini");

        Assert.Equal("Compact", layout);
    }

    [Fact]
    public void DetermineLayoutMode_UsesHysteresisNearMediumBoundary()
    {
        string layout = WeatherWidgetViewModel.DetermineLayoutMode(184, 140, "Compact");

        Assert.Equal("Compact", layout);
    }

    [Fact]
    public void ResponsiveTransition_ExpandingPreselectsFinalLayoutAndIgnoresIntermediateSizes()
    {
        WeatherWidgetViewModel viewModel = CreateViewModel();

        viewModel.UpdateAvailableSize(150, 104);
        viewModel.BeginResponsiveLayoutTransition(320, 260, isCollapsing: false);
        viewModel.UpdateAvailableSize(190, 140);
        viewModel.UpdateAvailableSize(255, 180);

        Assert.Equal("Expanded", viewModel.LayoutMode);

        viewModel.CompleteResponsiveLayoutTransition(320, 260);
        Assert.Equal("Expanded", viewModel.LayoutMode);
    }

    [Fact]
    public void ResponsiveTransition_CollapsingKeepsExpandedLayoutUntilContentIsHidden()
    {
        WeatherWidgetViewModel viewModel = CreateViewModel();

        viewModel.UpdateAvailableSize(320, 260);
        viewModel.BeginResponsiveLayoutTransition(150, 104, isCollapsing: true);
        viewModel.UpdateAvailableSize(255, 180);
        viewModel.UpdateAvailableSize(190, 140);

        Assert.Equal("Expanded", viewModel.LayoutMode);

        viewModel.CompleteResponsiveLayoutTransition(150, 104);
        Assert.Equal("Mini", viewModel.LayoutMode);
    }

    [Theory]
    [InlineData(300, 260)]
    [InlineData(320, 260)]
    [InlineData(420, 360)]
    public void DetermineLayoutMode_UsesExpandedLayoutFromLargeWidgetSize(double width, double contentHeight)
    {
        string layout = WeatherWidgetViewModel.DetermineLayoutMode(width, contentHeight, "Compact");

        Assert.Equal("Expanded", layout);
    }

    [Fact]
    public void DetermineLayoutMode_UsesHysteresisNearExpandedBoundary()
    {
        // Between the downgrade (280x240) and upgrade (300x260) thresholds an
        // already-Expanded widget stays Expanded, avoiding layout flicker.
        string layout = WeatherWidgetViewModel.DetermineLayoutMode(290, 250, "Expanded");

        Assert.Equal("Expanded", layout);
    }

    [Fact]
    public void WeekForecastTemperatures_UseTheSameTextSizeAsDayLabels()
    {
        using WeatherWidgetViewModel viewModel = CreateViewModel();

        viewModel.UpdateAvailableSize(320, 260);

        Assert.Equal(viewModel.WeekDayLabelTextSize, viewModel.WeekTempMaxSize);
        Assert.Equal(viewModel.WeekDayLabelTextSize, viewModel.WeekTempMinSize);
    }

    [Theory]
    [InlineData(275, 250)]
    [InlineData(300, 235)]
    public void DetermineLayoutMode_DowngradesExpandedToCompactWhenShrunk(double width, double contentHeight)
    {
        string layout = WeatherWidgetViewModel.DetermineLayoutMode(width, contentHeight, "Expanded");

        Assert.Equal("Compact", layout);
    }

    [Theory]
    [InlineData(320, 360, 100)]
    [InlineData(320, 320, 88)]
    [InlineData(320, 300, 80)]
    public void ExpandedHourlyCardHeight_AdaptsToAvailableHeight(double width, double height, double expectedCardHeight)
    {
        WeatherWidgetViewModel viewModel = CreateViewModel();

        viewModel.UpdateAvailableSize(width, height);

        Assert.Equal("Expanded", viewModel.LayoutMode);
        Assert.Equal(expectedCardHeight, viewModel.ExpandedHourlyCardHeight);
    }

    [Fact]
    public void ExpandedSunriseVisibility_ShowsOnlyWhenTallEnough()
    {
        WeatherWidgetViewModel viewModel = CreateViewModel();

        viewModel.UpdateAvailableSize(420, 380);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, viewModel.ExpandedSunriseVisibility);

        // Resizing the expanded widget down to the hourly-content height must
        // not remove the middle sunrise/sunset track prematurely.
        viewModel.UpdateAvailableSize(420, 340);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, viewModel.ExpandedSunriseVisibility);

        viewModel.UpdateAvailableSize(420, 299);
        Assert.Equal(
            Microsoft.UI.Xaml.Visibility.Collapsed,
            viewModel.ExpandedSecondaryMetricsVisibility);
        Assert.Equal(
            Microsoft.UI.Xaml.Visibility.Visible,
            viewModel.ExpandedSunriseVisibility);

        viewModel.UpdateAvailableSize(420, 279);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, viewModel.ExpandedSunriseVisibility);
    }

    [Fact]
    public void ExpandedHourlyPrecipVisibility_ShowsOnlyWhenTallEnough()
    {
        WeatherWidgetViewModel viewModel = CreateViewModel();

        viewModel.UpdateAvailableSize(320, 310);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, viewModel.ExpandedHourlyPrecipVisibility);

        viewModel.UpdateAvailableSize(320, 300);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, viewModel.ExpandedHourlyPrecipVisibility);
    }

    [Fact]
    public void UpdateAvailableSize_DoesNotNotifyWhenResponsiveValuesAreUnchanged()
    {
        WeatherWidgetViewModel viewModel = CreateViewModel();
        viewModel.UpdateAvailableSize(420, 380);

        List<string?> changedProperties = [];
        viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        viewModel.UpdateAvailableSize(421, 381);

        Assert.Empty(changedProperties);
    }

    [Fact]
    public void UpdateAvailableSize_NotifiesOnlyHeightDerivedValuesThatActuallyChange()
    {
        WeatherWidgetViewModel viewModel = CreateViewModel();
        viewModel.UpdateAvailableSize(320, 360);

        List<string?> changedProperties = [];
        viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        viewModel.UpdateAvailableSize(320, 340);

        Assert.DoesNotContain(nameof(WeatherWidgetViewModel.ExpandedSunriseVisibility), changedProperties);
        Assert.DoesNotContain(nameof(WeatherWidgetViewModel.ExpandedHourlyPrecipVisibility), changedProperties);
        Assert.Contains(nameof(WeatherWidgetViewModel.ExpandedHourlyCardHeight), changedProperties);
    }

    [Fact]
    public void ExpandedHourlyDivider_HidesAdjacentDuplicateWhenSupplementaryMetricsAreHidden()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new SettingsService(root);
            settings.Settings.WeatherShowSunrise = false;
            settings.Settings.WeatherShowUvIndex = false;
            settings.Settings.WeatherShowPressure = false;

            using var viewModel = new WeatherWidgetViewModel(
                CreateConfig(),
                new WeatherService(),
                TestServices.CreateLocalizationService(),
                settings);

            viewModel.UpdateAvailableSize(320, 300);

            Assert.Equal(
                Microsoft.UI.Xaml.Visibility.Visible,
                viewModel.PrimaryMetricsVisibility);
            Assert.Equal(
                Microsoft.UI.Xaml.Visibility.Collapsed,
                viewModel.ExpandedSecondaryMetricsVisibility);
            Assert.Equal(
                Microsoft.UI.Xaml.Visibility.Collapsed,
                viewModel.ExpandedSunriseVisibility);
            Assert.Equal(
                Microsoft.UI.Xaml.Visibility.Collapsed,
                viewModel.ExpandedHourlyDividerVisibility);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ExpandedHourlyDivider_RemainsWhenPrimaryMetricsAreHidden()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new SettingsService(root);
            settings.Settings.WeatherShowHumidity = false;
            settings.Settings.WeatherShowWind = false;
            settings.Settings.WeatherShowPrecipitation = false;
            settings.Settings.WeatherShowSunrise = false;
            settings.Settings.WeatherShowUvIndex = false;
            settings.Settings.WeatherShowPressure = false;

            using var viewModel = new WeatherWidgetViewModel(
                CreateConfig(),
                new WeatherService(),
                TestServices.CreateLocalizationService(),
                settings);

            viewModel.UpdateAvailableSize(320, 300);

            Assert.Equal(
                Microsoft.UI.Xaml.Visibility.Collapsed,
                viewModel.PrimaryMetricsVisibility);
            Assert.Equal(
                Microsoft.UI.Xaml.Visibility.Visible,
                viewModel.ExpandedHourlyDividerVisibility);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(10, 9, 10, 11, 28)]
    [InlineData(11.5, 10.5, 11.5, 12.5, 29.5)]
    [InlineData(16, 15, 16, 17, 32)]
    public void Typography_UsesAppearanceTextSizeAsTheBaseline(
        double appearanceTextSize,
        double expectedCaption,
        double expectedBody,
        double expectedTitle,
        double expectedExpandedTemperature)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new SettingsService(root);
            settings.Settings.TextSize = appearanceTextSize;
            using var viewModel = new WeatherWidgetViewModel(
                CreateConfig(),
                new WeatherService(),
                TestServices.CreateLocalizationService(),
                settings);

            viewModel.ApplyAppearance();
            // TemperatureTextSize is layout-dependent; use a comfortably
            // expanded surface so this contract verifies the expanded weather
            // presentation shown in the user report rather than the compact
            // constructor fallback size.
            viewModel.UpdateAvailableSize(420, 360);

            Assert.Equal(appearanceTextSize, viewModel.TextSize);
            Assert.Equal(expectedCaption, viewModel.CaptionTextSize);
            Assert.Equal(expectedBody, viewModel.BodyTextSize);
            Assert.Equal(expectedTitle, viewModel.TitleTextSize);
            Assert.Equal(
                expectedExpandedTemperature,
                viewModel.TemperatureTextSize);
            Assert.Equal(
                viewModel.CaptionTextSize,
                viewModel.ForecastHourTextSize);
            Assert.Equal(
                viewModel.BodyTextSize,
                viewModel.ForecastTempTextSize);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void SystemTextScale_DowngradesLayoutBeforeTypographyCanBeClipped()
    {
        using WeatherWidgetViewModel viewModel = CreateViewModel();
        viewModel.UpdateAvailableSize(320, 260);
        Assert.Equal("Expanded", viewModel.LayoutMode);

        viewModel.UpdateSystemTextScaleFactor(2.25);

        Assert.Equal("Compact", viewModel.LayoutMode);
        Assert.True(viewModel.ExpandedHourlyCardHeight > 88);

        viewModel.UpdateAvailableSize(150, 104);
        Assert.Equal("Mini", viewModel.LayoutMode);
        Assert.Equal(
            Microsoft.UI.Xaml.Visibility.Collapsed,
            viewModel.MiniHeaderVisibility);
    }

    [Fact]
    public void CompactLayout_HidesSecondaryMetricsWhenHeightIsTight()
    {
        using WeatherWidgetViewModel viewModel = CreateViewModel();

        viewModel.UpdateAvailableSize(200, 154);
        Assert.Equal("Compact", viewModel.LayoutMode);
        Assert.Equal(
            Microsoft.UI.Xaml.Visibility.Collapsed,
            viewModel.PrimaryMetricsVisibility);

        viewModel.UpdateAvailableSize(220, 180);
        Assert.Equal(
            Microsoft.UI.Xaml.Visibility.Visible,
            viewModel.PrimaryMetricsVisibility);
    }

    [Fact]
    public void ViewMode_UsesPersistedWidgetOverride()
    {
        var config = CreateConfig();
        config.Metadata[DeskBox.Services.WeatherWidgetViewModeSettings.MetadataKey] =
            DeskBox.Services.WeatherWidgetViewModeSettings.WeekValue;

        using var viewModel = CreateViewModel(config);

        Assert.True(viewModel.IsWeekView);
    }

    [Fact]
    public void ViewModeChange_IsStoredInWidgetMetadata()
    {
        var config = CreateConfig();
        using (var viewModel = CreateViewModel(config))
        {
            viewModel.SetViewMode(useWeekView: true);
        }

        using var restored = CreateViewModel(config);
        Assert.True(restored.IsWeekView);
        Assert.Equal(
            DeskBox.Services.WeatherWidgetViewModeSettings.WeekValue,
            config.Metadata[
                DeskBox.Services.WeatherWidgetViewModeSettings.MetadataKey]);
    }

    [Fact]
    public async Task ExplicitViewSelection_IsPersistedWhenItMatchesTheGlobalDefault()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var config = CreateConfig();
            var settings = new DeskBox.Services.SettingsService(root);
            settings.Settings.WeatherDefaultView =
                DeskBox.Services.SettingsService.WeatherDefaultViewWeek;
            settings.UpdateWidget(config, notifySubscribers: false);

            using (var viewModel = new WeatherWidgetViewModel(
                       config,
                       new DeskBox.Services.WeatherService(),
                       TestServices.CreateLocalizationService(),
                       settings))
            {
                Assert.True(viewModel.IsWeekView);
                Assert.DoesNotContain(
                    DeskBox.Services.WeatherWidgetViewModeSettings.MetadataKey,
                    config.Metadata);

                viewModel.SetViewMode(useWeekView: true);
            }

            Assert.Equal(
                DeskBox.Services.WeatherWidgetViewModeSettings.WeekValue,
                config.Metadata[
                    DeskBox.Services.WeatherWidgetViewModeSettings.MetadataKey]);
            Assert.True(await settings.FlushPendingSaveAsync());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ViewModeChange_SurvivesSettingsReload()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new DeskBox.Services.SettingsService(root);
            var config = CreateConfig();
            settings.UpdateWidget(config, notifySubscribers: false);
            using (var viewModel = new WeatherWidgetViewModel(
                       config,
                       new DeskBox.Services.WeatherService(),
                       TestServices.CreateLocalizationService(),
                       settings))
            {
                viewModel.SetViewMode(useWeekView: true);
            }

            Assert.True(await settings.FlushPendingSaveAsync());

            var reloaded = new DeskBox.Services.SettingsService(root);
            await reloaded.LoadAsync();
            DeskBox.Models.WidgetConfig restoredConfig = Assert.Single(
                reloaded.Settings.Widgets,
                widget => widget.Id == config.Id);
            using var restored = new WeatherWidgetViewModel(
                restoredConfig,
                new DeskBox.Services.WeatherService(),
                TestServices.CreateLocalizationService(),
                reloaded);

            Assert.True(restored.IsWeekView);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static WeatherWidgetViewModel CreateViewModel()
    {
        return CreateViewModel(CreateConfig());
    }

    private static WeatherWidgetViewModel CreateViewModel(
        DeskBox.Models.WidgetConfig config)
    {
        return new WeatherWidgetViewModel(
            config,
            new DeskBox.Services.WeatherService(),
            TestServices.CreateLocalizationService());
    }

    private static DeskBox.Models.WidgetConfig CreateConfig()
    {
        return new DeskBox.Models.WidgetConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Weather",
            WidgetKind = DeskBox.Models.WidgetKind.Weather
        };
    }
}
