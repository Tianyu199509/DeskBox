using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;

namespace DeskBox.Tests;

public sealed class Win10BackdropDisposedCompositorCrashRegressionTests
{
    [Fact]
    public void MaskedBackdropTargetProxy_InteractionTintOverride_SurvivesControllerCompositorClosureAcrossFiveTargets()
    {
        RunOnStaWithCompositor(() =>
        {
            var targets = Enumerable.Range(0, 5).Select(_ => new FakeBackdropTarget()).ToArray();
            var proxies = targets
                .Select(static target => new MaskedBackdropTargetProxy(target, cornerRadiusDip: 12.0, rasterizationScale: 1.0))
                .ToArray();

            // Simulate WinAppSDK DesktopAcrylicController's internal Compositor feeding
            // all 5 widget targets before batch hide.
            var controllerCompositor = new Windows.UI.Composition.Compositor();
            var controllerBrush = controllerCompositor.CreateColorBrush(Colors.CornflowerBlue);
            foreach (MaskedBackdropTargetProxy proxy in proxies)
            {
                proxy.SystemBackdrop = controllerBrush;
                Assert.NotNull(proxy.SystemBackdrop);
            }

            Assert.All(targets, static t => Assert.IsType<Windows.UI.Composition.CompositionMaskBrush>(t.SystemBackdrop));

            // Simulate SimplifyBackdropForInteraction() across all 5 widgets followed by
            // DetachAcrylicControllerTarget(): when the 5th (last) target detaches,
            // DesktopAcrylicController closes its internal Compositor.
            var simplifiedTint = Windows.UI.Color.FromArgb(220, 32, 34, 38);
            for (int i = 0; i < proxies.Length; i++)
            {
                Assert.True(proxies[i].SetInteractionTintOverride(simplifiedTint));
                proxies[i].SystemBackdrop = null;
            }

            controllerBrush.Dispose();
            controllerCompositor.Dispose();

            // Every widget's live interaction tint mask must still be alive on DeskBox's
            // persistent thread compositor rather than closed with controllerCompositor.
            foreach (FakeBackdropTarget target in targets)
            {
                var appliedMask = Assert.IsType<Windows.UI.Composition.CompositionMaskBrush>(target.SystemBackdrop);
                Assert.NotNull(appliedMask.Source);
                Assert.NotNull(appliedMask.Mask);
            }

            // Simulate RestoreBackdropAfterInteraction() after batch hide completes:
            // clearing the interaction override must never throw ObjectDisposedException
            // or leave _overrideBrush wedged for subsequent ApplyBackdropPreference / Dispose calls.
            foreach (MaskedBackdropTargetProxy proxy in proxies)
            {
                Assert.True(proxy.SetInteractionTintOverride(null));
            }

            Assert.All(targets, static t => Assert.Null(t.SystemBackdrop));

            // Re-attaching a new controller compositor on next reveal and later closing
            // the widget must also succeed cleanly without throwing.
            using var nextControllerCompositor = new Windows.UI.Composition.Compositor();
            using var nextControllerBrush = nextControllerCompositor.CreateColorBrush(Colors.CadetBlue);
            foreach (MaskedBackdropTargetProxy proxy in proxies)
            {
                proxy.SystemBackdrop = nextControllerBrush;
            }

            Assert.All(targets, static t => Assert.IsType<Windows.UI.Composition.CompositionMaskBrush>(t.SystemBackdrop));

            foreach (MaskedBackdropTargetProxy proxy in proxies)
            {
                proxy.ReleaseSuspendedResources();
                proxy.Dispose();
            }

            Assert.All(targets, static t => Assert.Null(t.SystemBackdrop));
            RoundedBackdropMaskService.TrimIdle();
        });
    }

    [Fact]
    public void MaskedBackdropTargetProxy_AndMaskCache_SelfHealWhenUnderlyingCompositorAlreadyClosed()
    {
        RunOnStaWithCompositor(() =>
        {
            var target = new FakeBackdropTarget();
            var proxy = new MaskedBackdropTargetProxy(target, cornerRadiusDip: 14.0, rasterizationScale: 1.25);

            var deadCompositor = new Windows.UI.Composition.Compositor();
            var deadSourceBrush = deadCompositor.CreateColorBrush(Colors.SlateBlue);
            proxy.SystemBackdrop = deadSourceBrush;
            Assert.IsType<Windows.UI.Composition.CompositionMaskBrush>(target.SystemBackdrop);

            // Force-close the compositor while _maskBrush and the cached NineGridBrush
            // entry are still referenced by the proxy and CompositorMaskCache.
            deadCompositor.Dispose();

            // Clearing override, updating parameters, suspending, and disposing must never
            // propagate RO_E_CLOSED / ObjectDisposedException.
            var exception = Record.Exception(() =>
            {
                proxy.UpdateParameters(cornerRadiusDip: 16.0, rasterizationScale: 1.5);
                proxy.SetInteractionTintOverride(null);
                proxy.ReleaseSuspendedResources();
                proxy.Dispose();
            });
            Assert.Null(exception);
            Assert.Null(target.SystemBackdrop);

            // Acquiring a mask on a live compositor with the same key must succeed cleanly,
            // and if a compositor with a cached entry is later closed, TryGetReusableEntry
            // must evict the dead entry rather than returning a closed NineGridBrush.
            var ephemeralCacheCompositor = new Windows.UI.Composition.Compositor();
            var lease = RoundedBackdropMaskService.AcquireNineGridMask(
                ephemeralCacheCompositor,
                radiusDip: 12.0,
                rasterizationScale: 1.0);
            Assert.NotNull(lease);
            lease!.Dispose();

            ephemeralCacheCompositor.Dispose();

            // Acquiring against a closed compositor returns null cleanly without throwing.
            var afterCloseException = Record.Exception(() =>
            {
                var closedLease = RoundedBackdropMaskService.AcquireNineGridMask(
                    ephemeralCacheCompositor,
                    radiusDip: 12.0,
                    rasterizationScale: 1.0);
                Assert.Null(closedLease);
                RoundedBackdropMaskService.TrimIdle();
            });
            Assert.Null(afterCloseException);
        });
    }

    [Fact]
    public void ElementBackdropTargetBridge_FaultIsolation_AlwaysDisposesTargetLinkEvenWhenProxyOrCallbackThrows()
    {
        ThrowingDisposable? proxyField = new();
        bool callbackInvoked = false;
        var targetLink = new TrackingDisposableTarget();

        var exception = Record.Exception(() =>
        {
            ElementBackdropTargetBridge.NotifyAndDisposeMaskedProxySafely(
                ref proxyField,
                _ =>
                {
                    callbackInvoked = true;
                    throw new ObjectDisposedException("Simulated disconnecting callback failure");
                },
                "[TestBridge]");

            bool disposedLink = ElementBackdropTargetBridge.TryDisposeElementTargetLink(
                targetLink,
                "[TestBridge]");
            Assert.True(disposedLink);
        });

        Assert.Null(exception);
        Assert.True(callbackInvoked);
        Assert.Null(proxyField);
        Assert.Equal(1, targetLink.DisposeCount);
    }

    [Fact]
    public void WindowAndPopoverTeardown_KeepSystemBackdropElementRootedUntilXamlDisconnectsAndIsolateCleanupBase()
    {
        string widgetBase = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.cs"));
        string widgetBackdrop = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Backdrop.cs"));
        string editorPopover = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetEditorPopoverWindow.cs"));
        string materialBackdrop = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetMaterialSystemBackdrop.cs"));

        // CleanupBase must isolate backdrop disposal exceptions so WidgetLayerService.ReleaseWindow
        // and TrackWindowClosedForDiagnostics always execute on window close.
        int cleanupStart = widgetBase.IndexOf("protected void CleanupBase()", StringComparison.Ordinal);
        int cleanupEnd = widgetBase.IndexOf("private void QueueInteractiveResizePointer", cleanupStart, StringComparison.Ordinal);
        Assert.True(cleanupStart >= 0 && cleanupEnd > cleanupStart);
        string cleanupBody = widgetBase[cleanupStart..cleanupEnd];
        Assert.Contains("try { DisposeMaterialBackdropBridge(); }", cleanupBody, StringComparison.Ordinal);
        int bridgeDisposeIdx = cleanupBody.IndexOf("try { DisposeMaterialBackdropBridge(); }", StringComparison.Ordinal);
        int releaseWindowIdx = cleanupBody.IndexOf("WidgetLayerService.ReleaseWindow(HWnd);", StringComparison.Ordinal);
        int trackClosedIdx = cleanupBody.IndexOf("TrackWindowClosedForDiagnostics();", StringComparison.Ordinal);
        Assert.True(bridgeDisposeIdx >= 0 && releaseWindowIdx > bridgeDisposeIdx && trackClosedIdx > releaseWindowIdx);

        // DisposeMaterialBackdropBridge and SuspendBackdropTargetsForHiddenWindow must never
        // clear MaterialSurface.SystemBackdrop = null (which strands the thread-affine native
        // target link on the GC finalizer thread if XAML has collapsed/detached the element).
        int disposeBridgeStart = widgetBackdrop.IndexOf("private void DisposeMaterialBackdropBridge()", StringComparison.Ordinal);
        int disposeBridgeEnd = widgetBackdrop.IndexOf("private bool ApplyTransparentHostBackdrop()", disposeBridgeStart, StringComparison.Ordinal);
        Assert.True(disposeBridgeStart >= 0 && disposeBridgeEnd > disposeBridgeStart);
        string disposeBridgeBody = widgetBackdrop[disposeBridgeStart..disposeBridgeEnd];
        Assert.DoesNotContain("MaterialSurface.SystemBackdrop = null", disposeBridgeBody, StringComparison.Ordinal);
        Assert.Contains("bridge.Dispose();", disposeBridgeBody, StringComparison.Ordinal);

        int suspendHiddenStart = widgetBackdrop.IndexOf("protected void SuspendBackdropTargetsForHiddenWindow()", StringComparison.Ordinal);
        int suspendHiddenEnd = widgetBackdrop.IndexOf("protected void DetachMicaControllerTarget()", suspendHiddenStart, StringComparison.Ordinal);
        Assert.True(suspendHiddenStart >= 0 && suspendHiddenEnd > suspendHiddenStart);
        string suspendHiddenBody = widgetBackdrop[suspendHiddenStart..suspendHiddenEnd];
        Assert.Contains("_materialBackdropBridge?.Suspend();", suspendHiddenBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DisposeMaterialBackdropBridge();", suspendHiddenBody, StringComparison.Ordinal);

        // WidgetEditorPopoverWindow must likewise suspend & keep backdropEl.SystemBackdrop rooted.
        int releasePopoverStart = editorPopover.IndexOf("private void ReleaseAndDisposeBackdrops()", StringComparison.Ordinal);
        int releasePopoverEnd = editorPopover.IndexOf("private void DisposeMaterialBackdrop()", releasePopoverStart, StringComparison.Ordinal);
        Assert.True(releasePopoverStart >= 0 && releasePopoverEnd > releasePopoverStart);
        string releasePopoverBody = editorPopover[releasePopoverStart..releasePopoverEnd];
        Assert.DoesNotContain("backdropEl.SystemBackdrop = null", releasePopoverBody, StringComparison.Ordinal);
        Assert.Contains("_materialBackdrop?.Suspend();", releasePopoverBody, StringComparison.Ordinal);

        // Both OnTargetDisconnected implementations must isolate proxy disposal before closing the native link.
        Assert.Contains("NotifyAndDisposeMaskedProxySafely(", materialBackdrop, StringComparison.Ordinal);
        Assert.Contains("TryDisposeElementTargetLink(", materialBackdrop, StringComparison.Ordinal);
    }

    private static void RunOnStaWithCompositor(Action action)
    {
        Exception? staError = null;
        var staThread = new Thread(() =>
        {
            try
            {
                Windows.UI.Composition.Compositor? threadCompositor =
                    SolidTintSystemBackdrop.TryEnsureThreadCompositor();
                Assert.NotNull(threadCompositor);
                action();
            }
            catch (Exception ex)
            {
                staError = ex;
            }
        })
        {
            IsBackground = true,
            Name = nameof(Win10BackdropDisposedCompositorCrashRegressionTests)
        };
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        Assert.True(staThread.Join(TimeSpan.FromSeconds(15)), "STA composition test timed out.");
        if (staError is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(staError).Throw();
        }
    }

    private sealed class FakeBackdropTarget : ICompositionSupportsSystemBackdrop
    {
        public Windows.UI.Composition.CompositionBrush? SystemBackdrop { get; set; }
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        public void Dispose() =>
            throw new ObjectDisposedException("Simulated closed CompositionMaskBrush");
    }

    private sealed class TrackingDisposableTarget : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }
}
