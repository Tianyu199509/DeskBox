using DeskBox.Controls.WidgetContents;

namespace DeskBox.Tests;

/// <summary>
/// Pins the pure gating rules behind the Windows-desktop-style selected-name
/// expansion (trimmed label grows downward on sole selection) and the
/// slow-click rename entry (clicking the label of the already sole-selected
/// item). Hosts keep the state; these tests keep the policy from drifting.
/// </summary>
public sealed class SelectedNameExpansionPolicyTests
{
    [Fact]
    public void ExpansionRequiresSoleSelectionInDoubleClickMode()
    {
        Assert.True(SelectedNameExpansionPolicy.ShouldExpandSelectedItemName(
            isDoubleClickToOpen: true,
            selectedItemCount: 1,
            isBoxSelecting: false,
            isDragActive: false));
    }

    [Fact]
    public void ExpansionDisabledWhenSurfaceWindowIsUnfocused()
    {
        Assert.False(SelectedNameExpansionPolicy.ShouldExpandSelectedItemName(
            isDoubleClickToOpen: true,
            selectedItemCount: 1,
            isBoxSelecting: false,
            isDragActive: false,
            isWindowFocused: false));
    }

    [Fact]
    public void ExpansionRejectsMultiSelection()
    {
        Assert.False(SelectedNameExpansionPolicy.ShouldExpandSelectedItemName(
            isDoubleClickToOpen: true,
            selectedItemCount: 3,
            isBoxSelecting: false,
            isDragActive: false));
    }

    [Fact]
    public void ExpansionRejectsMarqueeAndDrag()
    {
        // A marquee passes through momentary single-selection states while
        // sweeping; it must not flash an expansion on the way.
        Assert.False(SelectedNameExpansionPolicy.ShouldExpandSelectedItemName(
            isDoubleClickToOpen: true,
            selectedItemCount: 1,
            isBoxSelecting: true,
            isDragActive: false));
        Assert.False(SelectedNameExpansionPolicy.ShouldExpandSelectedItemName(
            isDoubleClickToOpen: true,
            selectedItemCount: 1,
            isBoxSelecting: false,
            isDragActive: true));
    }

    [Fact]
    public void ExpansionDisabledInSingleClickOpenMode()
    {
        // In single-click mode a click opens the item; there is no
        // "selected, then pause" state for an expanded label to act on.
        Assert.False(SelectedNameExpansionPolicy.ShouldExpandSelectedItemName(
            isDoubleClickToOpen: false,
            selectedItemCount: 1,
            isBoxSelecting: false,
            isDragActive: false));
    }

    [Fact]
    public void SlowClickRenameRequiresLabelHitOnSoleSelectedItem()
    {
        Assert.True(SelectedNameExpansionPolicy.ShouldStartRenameFromItemClick(
            isDoubleClickToOpen: true,
            hasModifierKey: false,
            wasSoleSelectedItemBeforeClick: true,
            pointerPressedOnLabel: true));
    }

    [Fact]
    public void SlowClickRenameDisabledWhenSurfaceWindowIsUnfocused()
    {
        Assert.False(SelectedNameExpansionPolicy.ShouldStartRenameFromItemClick(
            isDoubleClickToOpen: true,
            hasModifierKey: false,
            wasSoleSelectedItemBeforeClick: true,
            pointerPressedOnLabel: true,
            isWindowFocused: false));
    }

    [Fact]
    public void SlowClickRenameRejectsModifiersIconPressesAndFreshSelections()
    {
        // Ctrl/Shift clicks change the selection, not the name.
        Assert.False(SelectedNameExpansionPolicy.ShouldStartRenameFromItemClick(
            isDoubleClickToOpen: true,
            hasModifierKey: true,
            wasSoleSelectedItemBeforeClick: true,
            pointerPressedOnLabel: true));
        // Pressing the icon stages a drag far more often than it renames.
        Assert.False(SelectedNameExpansionPolicy.ShouldStartRenameFromItemClick(
            isDoubleClickToOpen: true,
            hasModifierKey: false,
            wasSoleSelectedItemBeforeClick: true,
            pointerPressedOnLabel: false));
        // First click only selects; a click that changed the selection never
        // renames (this is the pre-click snapshot the ItemClick handler reads).
        Assert.False(SelectedNameExpansionPolicy.ShouldStartRenameFromItemClick(
            isDoubleClickToOpen: true,
            hasModifierKey: false,
            wasSoleSelectedItemBeforeClick: false,
            pointerPressedOnLabel: true));
        // Single-click mode opens on click; slow click has no meaning.
        Assert.False(SelectedNameExpansionPolicy.ShouldStartRenameFromItemClick(
            isDoubleClickToOpen: false,
            hasModifierKey: false,
            wasSoleSelectedItemBeforeClick: true,
            pointerPressedOnLabel: true));
    }

    [Fact]
    public void RenameEditorHeightGrowsToFullNameAndNeverShrinksBelowTile()
    {
        Assert.Equal(
            240,
            SelectedNameExpansionPolicy.ClampRenameEditorHeight(
                measuredNameHeight: 240,
                tileHeight: 100,
                availableHeight: 400,
                minimumHeight: 20));
        Assert.Equal(
            100,
            SelectedNameExpansionPolicy.ClampRenameEditorHeight(
                measuredNameHeight: 40,
                tileHeight: 100,
                availableHeight: 400,
                minimumHeight: 20));
    }

    [Fact]
    public void RenameEditorHeightClampsToAvailableSurface()
    {
        Assert.Equal(
            180,
            SelectedNameExpansionPolicy.ClampRenameEditorHeight(
                measuredNameHeight: 320,
                tileHeight: 100,
                availableHeight: 180,
                minimumHeight: 20));
        // A surface sliver still yields the minimum so the editor never
        // collapses to nothing.
        Assert.Equal(
            20,
            SelectedNameExpansionPolicy.ClampRenameEditorHeight(
                measuredNameHeight: 320,
                tileHeight: 100,
                availableHeight: 5,
                minimumHeight: 20));
    }
}
