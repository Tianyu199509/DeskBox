using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Regression coverage for the Glance "background stays blank until clicked"
/// bug: a decode completing after the window hid used to re-mark the image as
/// shown although its brush had been dropped, so the next reveal skipped the
/// reload. The presenter state machine now owns those rules.
/// </summary>
public sealed class GlanceBackgroundPresenterStateTests
{
    private const string First = @"C:\images\first.jpg";
    private const string Second = @"C:\images\second.jpg";

    [Fact]
    public void StaleDecodeAfterSuspend_CannotPublishAndRevealReloads()
    {
        var state = new GlanceBackgroundPresenterState();
        state.Activate();
        GlanceBackgroundLoadTicket ticket = state.Request(First, 1280, allowTransition: false)!.Value;

        // Window hides while the decode is in flight; nothing was shown yet.
        state.Suspend();
        state.ReleaseFrame();
        Assert.False(state.TryAcceptOpened(ticket.Token));
        Assert.False(state.HasFrame);

        // Reveal must issue a fresh decode instead of trusting stale state.
        state.Activate();
        GlanceBackgroundLoadTicket? reload = state.Request(First, 1280, allowTransition: false);
        Assert.NotNull(reload);
        Assert.NotEqual(ticket.Token, reload!.Value.Token);
        Assert.True(state.TryAcceptOpened(reload.Value.Token));
        Assert.True(state.HasFrame);
    }

    [Fact]
    public void QuickHideShow_WithRetainedFrame_DoesNotRedecode()
    {
        var state = ShowFrame(First, 1280);

        state.Suspend();
        Assert.True(state.HasFrame);
        state.Activate();

        Assert.Null(state.Request(First, 1280, allowTransition: false));
        Assert.True(state.HasFrame);
    }

    [Fact]
    public void ReleasedFrame_RevealDecodesAgain()
    {
        var state = ShowFrame(First, 1280);

        state.Suspend();
        state.ReleaseFrame();
        state.Activate();

        Assert.NotNull(state.Request(First, 1280, allowTransition: false));
    }

    [Fact]
    public void InactiveSurface_IssuesNoDecodes()
    {
        var state = new GlanceBackgroundPresenterState();
        Assert.Null(state.Request(First, 1280, allowTransition: true));

        state.Activate();
        state.Suspend();
        Assert.Null(state.Request(First, 1280, allowTransition: true));
    }

    [Fact]
    public void SupersedingRequest_InvalidatesEarlierDecode()
    {
        var state = new GlanceBackgroundPresenterState();
        state.Activate();
        GlanceBackgroundLoadTicket first = state.Request(First, 1280, true)!.Value;
        GlanceBackgroundLoadTicket second = state.Request(Second, 1280, true)!.Value;

        Assert.False(state.TryAcceptOpened(first.Token));
        Assert.True(state.TryAcceptOpened(second.Token));
        Assert.Equal(Second, state.FramePath);
    }

    [Fact]
    public void FlippingBackToDisplayedImage_CancelsPendingDecode()
    {
        var state = ShowFrame(First, 1280);
        GlanceBackgroundLoadTicket pending = state.Request(Second, 1280, true)!.Value;

        Assert.Null(state.Request(First, 1280, true));
        Assert.False(state.HasPending);
        Assert.False(state.TryAcceptOpened(pending.Token));
        Assert.Equal(First, state.FramePath);
    }

    [Fact]
    public void DuplicateRequestWhileDecoding_IsDeduplicated()
    {
        var state = new GlanceBackgroundPresenterState();
        state.Activate();
        Assert.NotNull(state.Request(First, 1280, true));
        Assert.Null(state.Request(First, 1280, true));
    }

    [Fact]
    public void LargerSurface_RequestsSharperDecode()
    {
        var state = ShowFrame(First, 768);

        GlanceBackgroundLoadTicket? sharper = state.Request(First, 1536, false);
        Assert.NotNull(sharper);
        Assert.Equal(1536, sharper!.Value.DecodePixelWidth);
        Assert.Equal(1536, state.GetKnownDecodePixelWidth(First));
    }

    [Fact]
    public void FailedDecode_RetriesWithBoundedAttempts()
    {
        var state = new GlanceBackgroundPresenterState();
        state.Activate();
        GlanceBackgroundLoadTicket ticket = state.Request(First, 1280, true)!.Value;

        for (int attempt = 2; attempt <= GlanceBackgroundPresenterState.MaxDecodeAttempts; attempt++)
        {
            GlanceBackgroundLoadTicket? retry = state.OnFailed(ticket.Token);
            Assert.NotNull(retry);
            Assert.Equal(attempt, retry!.Value.Attempt);
            Assert.True(state.IsCurrent(retry.Value.Token));
            ticket = retry.Value;
        }

        Assert.Null(state.OnFailed(ticket.Token));
        Assert.False(state.HasPending);
    }

    [Fact]
    public void StaleFailure_IsIgnored()
    {
        var state = new GlanceBackgroundPresenterState();
        state.Activate();
        GlanceBackgroundLoadTicket first = state.Request(First, 1280, true)!.Value;
        GlanceBackgroundLoadTicket second = state.Request(Second, 1280, true)!.Value;

        Assert.Null(state.OnFailed(first.Token));
        Assert.True(state.IsCurrent(second.Token));
    }

    [Fact]
    public void FailureWhileSuspended_DoesNotRetry()
    {
        var state = new GlanceBackgroundPresenterState();
        state.Activate();
        GlanceBackgroundLoadTicket ticket = state.Request(First, 1280, true)!.Value;
        state.Suspend();

        Assert.Null(state.OnFailed(ticket.Token));
    }

    [Fact]
    public void ViewSurface_RoutesEveryResidencyChangeThroughThePresenter()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.xaml.cs"));

        Assert.Contains("_background.TryAcceptOpened(token)", source, StringComparison.Ordinal);
        Assert.Contains("_background.ReleaseFrame();", source, StringComparison.Ordinal);
        Assert.Contains("_background.Suspend();", source, StringComparison.Ordinal);
        // The loose version/path bookkeeping that caused the blank reveal must not return.
        Assert.DoesNotContain("_imageLoadVersion", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_decodedImagePath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_requestedImagePath", source, StringComparison.Ordinal);
    }

    private static GlanceBackgroundPresenterState ShowFrame(string path, int width)
    {
        var state = new GlanceBackgroundPresenterState();
        state.Activate();
        GlanceBackgroundLoadTicket ticket = state.Request(path, width, false)!.Value;
        Assert.True(state.TryAcceptOpened(ticket.Token));
        return state;
    }
}
