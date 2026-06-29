using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetSessionManagerTests
{
    [Fact]
    public void MarkRaisedSession_RecordsRaisedState()
    {
        var manager = new WidgetSessionManager();

        manager.MarkRaisedSession("test");

        Assert.Equal(WidgetSessionState.RaisedSession, manager.State);
        Assert.True(manager.IsRaised);
        Assert.False(manager.IsInteractionActive);
    }

    [Fact]
    public void Interaction_ReturnsToPreviousDesktopState()
    {
        var manager = new WidgetSessionManager();

        manager.BeginInteraction("start");
        manager.EndInteraction("end");

        Assert.Equal(WidgetSessionState.DesktopResting, manager.State);
        Assert.False(manager.IsInteractionActive);
    }

    [Fact]
    public void Interaction_ReturnsToPreviousRaisedState()
    {
        var manager = new WidgetSessionManager();
        manager.MarkRaisedSession("raised");

        manager.BeginInteraction("start");
        manager.EndInteraction("end");

        Assert.Equal(WidgetSessionState.RaisedSession, manager.State);
        Assert.True(manager.IsRaised);
        Assert.False(manager.IsInteractionActive);
    }

    [Fact]
    public void NestedInteraction_RemainsActiveUntilAllInteractionsEnd()
    {
        var manager = new WidgetSessionManager();
        manager.MarkRaisedSession("raised");

        manager.BeginInteraction("first");
        manager.BeginInteraction("second");
        manager.EndInteraction("second-ended");

        Assert.Equal(WidgetSessionState.InteractionActive, manager.State);
        Assert.True(manager.IsInteractionActive);

        manager.EndInteraction("first-ended");

        Assert.Equal(WidgetSessionState.RaisedSession, manager.State);
        Assert.False(manager.IsInteractionActive);
    }

    [Fact]
    public void MarkHidden_ClearsInteractionState()
    {
        var manager = new WidgetSessionManager();
        manager.BeginInteraction("start");

        manager.MarkHidden("hidden");

        Assert.Equal(WidgetSessionState.Hidden, manager.State);
        Assert.False(manager.IsRaised);
        Assert.False(manager.IsInteractionActive);
    }

    [Fact]
    public void EndInteraction_WithoutActiveInteraction_DoesNotChangeState()
    {
        var manager = new WidgetSessionManager();
        manager.MarkRaisedSession("raised");

        manager.EndInteraction("stray-end");

        Assert.Equal(WidgetSessionState.RaisedSession, manager.State);
        Assert.False(manager.IsInteractionActive);
    }
}
