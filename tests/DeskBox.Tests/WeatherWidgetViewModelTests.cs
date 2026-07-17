using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class WeatherWidgetViewModelTests
{
    [Theory]
    [InlineData(150, 104)]
    [InlineData(168, 116)]
    public void DetermineLayoutMode_KeepsSmallWidgetsInMini(double width, double contentHeight)
    {
        string layout = WeatherWidgetViewModel.DetermineLayoutMode(width, contentHeight, "Mini");

        Assert.Equal("Mini", layout);
    }

    [Theory]
    [InlineData(180, 134)]
    [InlineData(200, 154)]
    public void DetermineLayoutMode_UsesCompactLayoutFromMediumWidgetSize(double width, double contentHeight)
    {
        string layout = WeatherWidgetViewModel.DetermineLayoutMode(width, contentHeight, "Mini");

        Assert.Equal("Compact", layout);
    }

    [Fact]
    public void DetermineLayoutMode_UsesHysteresisNearMediumBoundary()
    {
        string layout = WeatherWidgetViewModel.DetermineLayoutMode(174, 122, "Compact");

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

        Assert.Equal("Detailed", viewModel.LayoutMode);

        viewModel.CompleteResponsiveLayoutTransition(320, 260);
        Assert.Equal("Detailed", viewModel.LayoutMode);
    }

    [Fact]
    public void ResponsiveTransition_CollapsingKeepsExpandedLayoutUntilContentIsHidden()
    {
        WeatherWidgetViewModel viewModel = CreateViewModel();

        viewModel.UpdateAvailableSize(320, 260);
        viewModel.BeginResponsiveLayoutTransition(150, 104, isCollapsing: true);
        viewModel.UpdateAvailableSize(255, 180);
        viewModel.UpdateAvailableSize(190, 140);

        Assert.Equal("Detailed", viewModel.LayoutMode);

        viewModel.CompleteResponsiveLayoutTransition(150, 104);
        Assert.Equal("Mini", viewModel.LayoutMode);
    }

    private static WeatherWidgetViewModel CreateViewModel()
    {
        return new WeatherWidgetViewModel(
            new DeskBox.Models.WidgetConfig
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Weather",
                WidgetKind = DeskBox.Models.WidgetKind.Weather
            },
            new DeskBox.Services.WeatherService(),
            TestServices.CreateLocalizationService());
    }
}
