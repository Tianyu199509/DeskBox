using System.Xml.Linq;
using DeskBox.Helpers;
using Windows.UI;

namespace DeskBox.Tests;

/// <summary>
/// The compositor text shadow route: every TextBlock opts in through an
/// app-level implicit style (the only scope template-expanded text can see)
/// and casts a shadow only inside windows marked WidgetTextShadow.Scope.
/// An empty host is inserted into the
/// nearest overlay panel right before the text's branch, and carries a
/// DropShadow masked by the text's glyph alpha whose Offset/Size/Opacity are
/// bound to the hand-out visuals in between through ExpressionAnimations, so
/// scrolling and marquee transforms move the shadow inside the compositor.
///
/// Two earlier routes are pinned out. The 1.5.1 composition route tracked
/// every TextBlock from one window-level layer on the UI thread (LayoutUpdated
/// reconciliation, full tree walks, TransformToVisual) and froze widgets. The
/// 1.5.6 dual-layer route drew a second, margin-offset TextBlock copy that
/// wrapped and trimmed independently of the real text and only covered the
/// handful of places that had been hand-wired.
/// </summary>
public sealed class WidgetTextShadowContractTests
{
    [Fact]
    public void Shadows_FollowTheirTextInsideTheCompositor()
    {
        string helper = ReadHelper();
        Assert.Contains("GetAlphaMask", helper, StringComparison.Ordinal);
        // The returned mask belongs to the TextBlock; disposing it closes the
        // element's cached brush and poisons every later re-activation.
        Assert.DoesNotContain("Mask.Dispose", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("Mask?.Dispose", helper, StringComparison.Ordinal);
        Assert.Contains("CreateDropShadow", helper, StringComparison.Ordinal);
        Assert.Contains("SetElementChildVisual", helper, StringComparison.Ordinal);
        Assert.Contains("CreateExpressionAnimation", helper, StringComparison.Ordinal);
        Assert.Contains("\" - host.Offset\"", helper, StringComparison.Ordinal);
        Assert.Contains("\"c0.Size\"", helper, StringComparison.Ordinal);
        // The host sits directly beneath the text's branch, inside a panel
        // where an extra 0x0 child cannot move anything.
        Assert.Contains("panel.Children.Insert(anchorIndex, host)", helper, StringComparison.Ordinal);
        Assert.Contains("panel is Grid or Canvas or RelativePanel", helper, StringComparison.Ordinal);
        Assert.Contains("!panel.IsItemsHost", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUiThreadTrackingRoute_StaysOutOfTheTree()
    {
        string helper = ReadHelper();
        foreach (string forbidden in new[]
        {
            "LayoutUpdated",
            "SizeChanged",
            "GetChildrenCount",
            "TransformToVisual",
            "CompositionTarget",
            "DispatcherQueueTimer"
        })
        {
            Assert.DoesNotContain(forbidden, helper, StringComparison.Ordinal);
        }

        // The only tree walk is a bounded climb from a text when it loads.
        Assert.Contains("MaxPassThroughDepth = 4", helper, StringComparison.Ordinal);

        Assert.False(File.Exists(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetTextShadowManager.cs")));
        Assert.False(File.Exists(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.TextEdge.cs")));
    }

    [Fact]
    public void EveryWidgetTextBlock_OptsInThroughTheAppImplicitStyle()
    {
        // Template-expanded text only sees implicit styles from the
        // application resources, so the opt-in style has to live there; the
        // scope marker on each widget window's root keeps every other
        // window's text out.
        string app = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/App.xaml"));
        const string implicitStyle = "<Style TargetType=\"TextBlock\">";
        int setter = app.IndexOf(
            "<Setter Property=\"helpers:WidgetTextShadow.Cast\" Value=\"True\" />",
            StringComparison.Ordinal);
        Assert.True(setter > 0);
        Assert.Equal(
            app.IndexOf(implicitStyle, StringComparison.Ordinal),
            app.LastIndexOf(implicitStyle, setter, StringComparison.Ordinal));

        string window = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.xaml"));
        Assert.Contains("helpers:WidgetTextShadow.Scope=\"True\"", window, StringComparison.Ordinal);

        string helper = ReadHelper();
        Assert.Contains("text.XamlRoot?.Content is UIElement root && GetScope(root)", helper, StringComparison.Ordinal);

        string shell = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml"));

        // Explicitly styled widget text bypasses the implicit style and opts
        // in locally; icon glyphs drawn through TextBlocks opt out.
        string todo = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml"));
        Assert.Equal(7, CountOccurrences(todo, "helpers:WidgetTextShadow.Cast=\"True\""));
        string titleIcon = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetTitleIcon.xaml"));
        Assert.Equal(2, CountOccurrences(titleIcon, "helpers:WidgetTextShadow.Cast=\"False\""));

        // No hand-wired hosts and no second TextBlock copies that could wrap
        // or trim differently.
        string files = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/FileItemSurface.xaml"));
        foreach (string xaml in new[] { shell, files })
        {
            Assert.DoesNotContain("WidgetTextShadow.Layer", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("WidgetTextShadow.Source", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("TextShadow\"", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("CloneShadow\"", xaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TemplateTextBlocks_DeclareTheirCastExplicitly()
    {
        // Implicit styles do not cross the DataTemplate/ControlTemplate
        // boundary for non-Control elements, so text realized by item
        // templates must carry a local Cast value: True to shadow, False
        // where it must not (dialog surfaces, glyphs).
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        foreach (string file in Directory.GetFiles(
                     TestPaths.FromRepository("src/DeskBox/Controls"),
                     "*.xaml", SearchOption.AllDirectories))
        {
            XDocument doc = XDocument.Load(file);
            foreach (XElement text in doc.Descendants(p + "TextBlock"))
            {
                bool inTemplate = text.Ancestors().Any(static a =>
                    a.Name.LocalName is "DataTemplate" or "ControlTemplate");
                if (!inTemplate)
                {
                    continue;
                }

                Assert.True(
                    text.Attributes().Any(static a =>
                        a.Name.LocalName == "WidgetTextShadow.Cast"),
                    $"{Path.GetFileName(file)}: a template TextBlock lacks WidgetTextShadow.Cast");
            }
        }
    }

    [Fact]
    public void MarqueeCode_LeavesShadowPlacementToTheCompositor()
    {
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml.cs"));
        Assert.DoesNotContain("ResolveMarqueeCloneShadow", code, StringComparison.Ordinal);
        Assert.DoesNotContain("cloneShadow", code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0xF7, 0xF7, 0xF7, true)]   // light palette
    [InlineData(0x1A, 0x1A, 0x1A, false)]  // dark palette
    [InlineData(0xFF, 0xD7, 0x00, true)]   // custom gold
    [InlineData(0x8B, 0x00, 0x00, false)]  // custom dark red
    [InlineData(0x00, 0x00, 0xFF, false)]  // custom pure blue
    [InlineData(0x77, 0x77, 0x77, true)]   // just above the crossover
    [InlineData(0x75, 0x75, 0x75, false)]  // just below the crossover
    public void EdgePolarity_FollowsTheTextColor(byte r, byte g, byte b, bool expectsDarkShadow)
    {
        TextShadowEdge edge = TextShadowPalette.Resolve(Color.FromArgb(0xFF, r, g, b));
        Assert.Equal(
            expectsDarkShadow ? TextShadowPalette.DarkShadow : TextShadowPalette.LightHalo,
            edge);
    }

    [Fact]
    public void EdgePolarity_FlipsAtTheWcagContrastCrossover()
    {
        // contrast(L, black) == contrast(L, white)  =>  L = sqrt(1.05 * 0.05) - 0.05.
        double crossover = Math.Sqrt(1.05 * 0.05) - 0.05;
        Assert.Equal(TextShadowPalette.PolarityLuminanceThreshold, crossover, 3);

        // A dark shadow drops below light text; a light halo surrounds dark
        // text without a direction, so it never reads as an engraved copy.
        Assert.Equal(0, TextShadowPalette.DarkShadow.Color.R);
        Assert.True(TextShadowPalette.DarkShadow.OffsetY > 0);
        Assert.Equal(0xFF, TextShadowPalette.LightHalo.Color.R);
        Assert.Equal(0f, TextShadowPalette.LightHalo.OffsetY);
    }

    [Fact]
    public void Setting_TogglesThroughTheSchemaAndBothWindows()
    {
        Assert.Contains(
            "CurrentSchemaVersion = 13",
            File.ReadAllText(TestPaths.FromRepository(
                "src/DeskBox/Services/SettingsMigrationService.cs")),
            StringComparison.Ordinal);
        Assert.Contains(
            "Migration_10_To_11",
            File.ReadAllText(TestPaths.FromRepository(
                "src/DeskBox/Services/SettingsMigrationService.cs")),
            StringComparison.Ordinal);

        Assert.Contains(
            "WidgetTextShadow.SetEnabled",
            File.ReadAllText(TestPaths.FromRepository(
                "src/DeskBox/Views/ContentWidgetWindow.xaml.cs")),
            StringComparison.Ordinal);

        string settingsXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsWindow.xaml"));
        Assert.Contains(
            "{Binding WidgetTextShadowEnabled, Mode=TwoWay}",
            settingsXaml,
            StringComparison.Ordinal);

        string bridge = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Features/Appearance/AppearanceSettingsViewModel.AotBindableProperties.cs"));
        Assert.Contains("nameof(WidgetTextShadowEnabled)", bridge, StringComparison.Ordinal);
    }

    private static string ReadHelper() => File.ReadAllText(TestPaths.FromRepository(
        "src/DeskBox/Helpers/WidgetTextShadow.cs"));

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
