extern alias GlancePkg;
using Viewport = GlancePkg::DeskBox.GlancePackage.Rendering.GlanceViewportCoordinator;

namespace DeskBox.Tests;

public sealed class NativeGlanceViewportCoordinatorTests
{
    [Fact]
    public void TransitionKeepsTargetViewportUntilCompletion()
    {
        var viewport = new Viewport(440, 560);
        Assert.True(viewport.Begin(240, 100));
        Assert.False(viewport.Resize(410, 420));
        Assert.Equal(240, viewport.Width);
        Assert.Equal(100, viewport.Height);
        viewport.Complete(240, 100);
        Assert.False(viewport.IsTransitionActive);
        Assert.True(viewport.Resize(260, 150));
    }

    [Fact]
    public void CancelRestoresOriginalViewportEvenIfTargetChanged()
    {
        var viewport = new Viewport(440, 560);
        viewport.Begin(240, 100);
        viewport.Begin(260, 120);
        Assert.True(viewport.Cancel());
        Assert.Equal(440, viewport.Width);
        Assert.Equal(560, viewport.Height);
        Assert.False(viewport.IsTransitionActive);
        Assert.False(viewport.Cancel());
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, -1)]
    [InlineData(double.NaN, 100)]
    [InlineData(100, double.PositiveInfinity)]
    public void InvalidTransitionGeometryDoesNotTrapTheView(double width, double height)
    {
        var viewport = new Viewport(440, 560);
        Assert.False(viewport.Begin(width, height));
        Assert.False(viewport.IsTransitionActive);
        Assert.Equal(440, viewport.Width);
    }

    [Fact]
    public void UnchangedBeginDoesNotActivateTransition()
    {
        var viewport = new Viewport(440, 560);
        Assert.False(viewport.Begin(440, 560));
        Assert.False(viewport.IsTransitionActive);
        Assert.True(viewport.Resize(420, 520));
    }

    [Fact]
    public void InvalidCompletionKeepsRollbackAvailable()
    {
        var viewport = new Viewport(440, 560);
        Assert.True(viewport.Begin(240, 100));
        Assert.False(viewport.Complete(double.NaN, 100));
        Assert.True(viewport.IsTransitionActive);
        Assert.True(viewport.Cancel());
        Assert.Equal(440, viewport.Width);
        Assert.Equal(560, viewport.Height);
    }
}
