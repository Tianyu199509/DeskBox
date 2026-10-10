using DeskBox.Controls;

namespace DeskBox.Tests;

public sealed class FileItemSurfaceStyleCacheTests
{
    [Theory]
    [InlineData(false, (byte)0)]
    [InlineData(true, (byte)255)]
    public void NeutralStateLayers_UseThemeAdaptiveMonochrome(
        bool isDark,
        byte expectedChannel)
    {
        Windows.UI.Color hover =
            FileItemSurfaceStyleCache.GetNeutralStateLayer(
                isDark,
                FileItemSurfaceVisualState.Hover,
                isSelected: false);
        Windows.UI.Color selected =
            FileItemSurfaceStyleCache.GetNeutralStateLayer(
                isDark,
                FileItemSurfaceVisualState.Normal,
                isSelected: true);
        Windows.UI.Color selectedHover =
            FileItemSurfaceStyleCache.GetNeutralStateLayer(
                isDark,
                FileItemSurfaceVisualState.Hover,
                isSelected: true);

        Assert.All(
            new[] { hover, selected, selectedHover },
            color =>
            {
                Assert.Equal(expectedChannel, color.R);
                Assert.Equal(expectedChannel, color.G);
                Assert.Equal(expectedChannel, color.B);
            });
        Assert.True(selected.A > hover.A);
        Assert.True(selectedHover.A > selected.A);
    }

    [Fact]
    public void DropTargets_UseTheNeutralHoverSurfaceWithoutAnAccentBorder()
    {
        // Folder import, stack add and shortcut launch are one visual family:
        // the shared cache renders each as the neutral hover surface, so the
        // accent color never enters a drop visual and no border is drawn.
        string root = FindRepositoryRoot();
        string cache = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/FileItemSurfaceStyleCache.cs"));
        string visuals = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ItemVisuals.cs"));

        Assert.Contains("bool isDropTarget = false", cache, StringComparison.Ordinal);
        Assert.Contains("_hoverSurfaceBrush", cache, StringComparison.Ordinal);
        Assert.Contains("_selectedHoverSurfaceBrush", cache, StringComparison.Ordinal);
        Assert.DoesNotContain("_dropTargetSurfaceBrush", cache, StringComparison.Ordinal);
        Assert.DoesNotContain("_dropTargetBorderBrush", cache, StringComparison.Ordinal);
        // Selection now draws a constant 1px neutral ring (DeskBoxNeutralLine
        // values); thickness never changes per state so batch selections do
        // not re-measure. Drop targets still swap to the transparent border,
        // and accent must not enter any border.
        Assert.Contains("SelectedBorderThickness = new(1)", cache, StringComparison.Ordinal);
        Assert.Contains(
            "border.BorderBrush = isSelected ? _selectedBorderBrush : _normalBorderBrush;",
            cache,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Accent", cache, StringComparison.Ordinal);
        // The stack drop highlight must not rebuild an accent border or
        // background either.
        Assert.DoesNotContain("_stackDropBorderBrush", visuals, StringComparison.Ordinal);
        Assert.DoesNotContain("_stackDropBrushesInitialized", visuals, StringComparison.Ordinal);
        Assert.Contains(
            "isDropTarget: state == FileItemSurfaceVisualState.DropTarget",
            visuals,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "src",
                    "DeskBox",
                    "DeskBox.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "DeskBox repository root was not found.");
    }
}
