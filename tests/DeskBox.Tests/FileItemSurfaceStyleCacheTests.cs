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

    [Theory]
    [InlineData(false, (byte)0)]
    [InlineData(true, (byte)255)]
    public void UnfocusedSelection_UsesMutedStateLayersBelowFocusedSelection(
        bool isDark,
        byte expectedChannel)
    {
        Windows.UI.Color focusedSelected =
            FileItemSurfaceStyleCache.GetNeutralStateLayer(
                isDark,
                FileItemSurfaceVisualState.Normal,
                isSelected: true,
                isWindowFocused: true);
        Windows.UI.Color unfocusedSelected =
            FileItemSurfaceStyleCache.GetNeutralStateLayer(
                isDark,
                FileItemSurfaceVisualState.Normal,
                isSelected: true,
                isWindowFocused: false);
        Windows.UI.Color focusedSelectedHover =
            FileItemSurfaceStyleCache.GetNeutralStateLayer(
                isDark,
                FileItemSurfaceVisualState.Hover,
                isSelected: true,
                isWindowFocused: true);
        Windows.UI.Color unfocusedSelectedHover =
            FileItemSurfaceStyleCache.GetNeutralStateLayer(
                isDark,
                FileItemSurfaceVisualState.Hover,
                isSelected: true,
                isWindowFocused: false);

        Assert.All(
            new[] { unfocusedSelected, unfocusedSelectedHover },
            color =>
            {
                Assert.Equal(expectedChannel, color.R);
                Assert.Equal(expectedChannel, color.G);
                Assert.Equal(expectedChannel, color.B);
            });
        Assert.True(unfocusedSelected.A > 0);
        Assert.True(unfocusedSelected.A < focusedSelected.A);
        Assert.True(unfocusedSelectedHover.A > unfocusedSelected.A);
        Assert.True(unfocusedSelectedHover.A < focusedSelectedHover.A);
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
        Assert.Contains("bool isWindowFocused = true", cache, StringComparison.Ordinal);
        Assert.Contains("_hoverSurfaceBrush", cache, StringComparison.Ordinal);
        Assert.Contains("_selectedHoverSurfaceBrush", cache, StringComparison.Ordinal);
        Assert.Contains("_unfocusedSelectedSurfaceBrush", cache, StringComparison.Ordinal);
        Assert.Contains("_unfocusedSelectedHoverSurfaceBrush", cache, StringComparison.Ordinal);
        Assert.DoesNotContain("_dropTargetSurfaceBrush", cache, StringComparison.Ordinal);
        Assert.DoesNotContain("_dropTargetBorderBrush", cache, StringComparison.Ordinal);
        // Selection draws a constant 1px neutral ring only when the surface
        // window holds keyboard focus; unfocused selection swaps to the
        // transparent border so desktop-pinned clicks never imply keyboard focus.
        Assert.Contains("SelectedBorderThickness = new(1)", cache, StringComparison.Ordinal);
        Assert.Contains(
            "border.BorderBrush = isSelected && isWindowFocused ? _selectedBorderBrush : _normalBorderBrush;",
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
        Assert.Contains(
            "isWindowFocused: IsSurfaceWindowKeyboardFocused()",
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
