extern alias GlancePkg;

using PackageDataFile = GlancePkg::DeskBox.GlancePackage.Rendering.GlanceDataFile;
using PackageData = GlancePkg::DeskBox.Models.GlanceWidgetData;
using PackageLayout = GlancePkg::DeskBox.Models.GlanceLayoutMode;

namespace DeskBox.Tests;

public sealed class NativeGlanceSettingsRefreshTests
{
    [Fact]
    public void SuppressedNativeDayTextHasARealBoundReplacementTemplate()
    {
        var xaml = System.Xml.Linq.XDocument.Load(TestPaths.SourceFile(
            "src/DeskBox.GlancePackage/Rendering/glance.xaml"));
        System.Xml.Linq.XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var calendar = Assert.Single(xaml.Descendants(ui + "CalendarView"));
        string style = (string)calendar.Attribute("CalendarViewDayItemStyle")!;
        Assert.NotNull(style);
        var template = Assert.Single(xaml.Descendants(ui + "Style").Where(s =>
            style == "{StaticResource " + (string?)s.Attribute(x + "Key") + "}"));
        Assert.Contains(template.Descendants(ui + "TextBlock"), t =>
            ((string?)t.Attribute("Text"))?.Contains("Tag.DayText", StringComparison.Ordinal) == true);
        Assert.Contains(template.Descendants(ui + "TextBlock"), t =>
            ((string?)t.Attribute("Text"))?.Contains("Tag.SecondaryText", StringComparison.Ordinal) == true);
        Assert.DoesNotContain("Converter=", template.ToString());
        Assert.Contains(xaml.Descendants(ui + "StackPanel"), p =>
            (string?)p.Attribute("Visibility") == "{Binding ExpandedCalendarHeaderVisibility}");
    }

    [Fact]
    public void DisplayChangesDoNotReshuffleTheBackgroundSequence()
    {
        var before = new PackageData { LocalImagePaths = [@"C:\a.png", @"C:\b.png"] };
        var after = new PackageData
        {
            LocalImagePaths = [@"c:\A.png", @"c:\B.png"],
            ShowTime = false,
            Layout = PackageLayout.Centered,
            RotationIntervalMinutes = 30,
            ShowChineseFestivals = false,
        };
        Assert.True(PackageDataFile.SameImageSources(before, after));
    }

    [Fact]
    public void SourceOrderAndRandomModeChangesRebuildTheImageSequence()
    {
        var before = new PackageData { LocalImagePaths = [@"C:\a.png", @"C:\b.png"] };
        var after = new PackageData { LocalImagePaths = [@"C:\b.png", @"C:\a.png"] };
        Assert.False(PackageDataFile.SameImageSources(before, after));
        after.LocalImagePaths = [.. before.LocalImagePaths];
        after.RandomOrder = !before.RandomOrder;
        Assert.False(PackageDataFile.SameImageSources(before, after));
        after.RandomOrder = before.RandomOrder;
        after.LocalFolderPath = @"C:\another-folder";
        Assert.False(PackageDataFile.SameImageSources(before, after));
    }
}
