using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class ContentWindowRegistrationTests
{
    [Fact]
    public void WindowWithoutHwnd_CannotClaimAnIdOrBlockAnotherRegistration()
    {
        var (registration, byId, handles) = Create();
        Assert.Throws<InvalidOperationException>(() =>
            registration.Register("widget-a", new FakeWindow(IntPtr.Zero)));
        Assert.Empty(byId);
        Assert.Empty(handles);

        var ready = new FakeWindow((IntPtr)90);
        registration.Register("widget-a", ready);
        Assert.Same(ready, byId["widget-a"]);
        Assert.Equal((IntPtr)90, Assert.Single(handles));
    }

    [Fact]
    public void DuplicateIdOrHandle_RejectsCandidateWithoutDisturbingActiveWindow()
    {
        var (registration, byId, handles) = Create();
        var active = new FakeWindow((IntPtr)100);
        registration.Register("widget-a", active);
        registration.Register("widget-a", active); // idempotent replay

        var sameId = new FakeWindow((IntPtr)101);
        var sameHandle = new FakeWindow((IntPtr)100);
        Assert.Throws<InvalidOperationException>(() => registration.Register("widget-a", sameId));
        Assert.Throws<InvalidOperationException>(() => registration.Register("widget-b", sameHandle));
        registration.Unregister(sameId); // candidate cleanup after failed create
        registration.Unregister(sameHandle);

        Assert.Same(active, Assert.Single(byId).Value);
        Assert.Equal((IntPtr)100, Assert.Single(handles));
    }

    [Fact]
    public void StaleCloseCannotRemoveReplacementEvenWhenTheHwndWasReused()
    {
        var (registration, byId, handles) = Create();
        var old = new FakeWindow((IntPtr)220);
        var replacement = new FakeWindow((IntPtr)220);
        registration.Register("widget-a", old);
        Assert.Equal(["widget-a"], registration.Unregister(old));
        registration.Register("widget-a", replacement);

        Assert.False(registration.UnregisterIfMatch("widget-a", old));
        Assert.Empty(registration.Unregister(old));
        Assert.Same(replacement, byId["widget-a"]);
        Assert.Equal((IntPtr)220, Assert.Single(handles));
    }

    [Fact]
    public void InPlaceGroupSwitchRebindsIdWithoutCreatingAnotherHandle()
    {
        var (registration, byId, handles) = Create();
        var persistent = new FakeWindow((IntPtr)330);
        registration.Register("previous", persistent);
        Assert.True(registration.CanRebind("next", persistent));
        registration.Rebind("next", persistent);

        Assert.False(byId.ContainsKey("previous"));
        Assert.Same(persistent, byId["next"]);
        Assert.Equal((IntPtr)330, Assert.Single(handles));
        Assert.Equal(["next"], registration.Unregister(persistent));
        Assert.Empty(byId);
        Assert.Empty(handles);
    }

    [Fact]
    public void GroupTargetConflictLeavesBothRegistrationsUntouched()
    {
        var (registration, byId, handles) = Create();
        var persistent = new FakeWindow((IntPtr)440);
        var target = new FakeWindow((IntPtr)441);
        registration.Register("previous", persistent);
        registration.Register("next", target);

        Assert.False(registration.CanRebind("next", persistent));
        Assert.Throws<InvalidOperationException>(() => registration.Rebind("next", persistent));
        Assert.Same(persistent, byId["previous"]);
        Assert.Same(target, byId["next"]);
        Assert.Equal(2, handles.Count);
        registration.Clear();
        registration.Clear();
        Assert.Empty(byId);
        Assert.Empty(handles);
    }

    private static (ContentWindowRegistration<FakeWindow> Registration,
        Dictionary<string, FakeWindow> ById, HashSet<IntPtr> Handles) Create()
    {
        var byId = new Dictionary<string, FakeWindow>(StringComparer.Ordinal);
        var handles = new HashSet<IntPtr>();
        return (new(byId, handles, window => window.Handle), byId, handles);
    }

    private sealed class FakeWindow(IntPtr handle)
    {
        internal IntPtr Handle { get; } = handle;
    }
}
