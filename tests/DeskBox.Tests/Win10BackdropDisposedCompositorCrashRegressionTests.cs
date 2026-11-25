using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;

namespace DeskBox.Tests;

public sealed class Win10BackdropDisposedCompositorCrashRegressionTests
{
    [Fact]
    public void MaskedBackdropTargetProxy_NeverAppliesForeignCompositorBrush_AndReleasesMaskBeforeControllerCompositorClosure()
    {
        RunOnStaWithCompositor(threadCompositor =>
        {
            // Simulate WinAppSDK DesktopAcrylicController's internal Compositor feeding
            // 5 SystemBackdropElement (ContentExternalBackdropLink) widget targets.
            // ContentExternalBackdropLink fail-fasts natively (0xc000027b) if a brush from
            // a foreign Compositor (such as DeskBox's standalone threadCompositor) is ever assigned.
            var controllerCompositor = new Windows.UI.Composition.Compositor();
            Assert.NotSame(threadCompositor, controllerCompositor);

            var targets = Enumerable.Range(0, 5)
                .Select(_ => new StrictElementCompositorBackdropTarget(expectedCompositor: controllerCompositor))
                .ToArray();
            var proxies = targets
                .Select(static target => new MaskedBackdropTargetProxy(target, cornerRadiusDip: 12.0, rasterizationScale: 1.0))
                .ToArray();

            var controllerBrush = controllerCompositor.CreateColorBrush(Colors.CornflowerBlue);
            foreach (MaskedBackdropTargetProxy proxy in proxies)
            {
                proxy.SystemBackdrop = controllerBrush;
                Assert.NotNull(proxy.SystemBackdrop);
            }

            foreach (StrictElementCompositorBackdropTarget target in targets)
            {
                var appliedMask = Assert.IsType<Windows.UI.Composition.CompositionMaskBrush>(target.SystemBackdrop);
                Assert.Same(controllerCompositor, appliedMask.Compositor);
                Assert.Same(controllerBrush, appliedMask.Source);
                Assert.NotNull(appliedMask.Mask);
            }

            // Simulate DetachAcrylicControllerTarget() across all 5 widgets:
            // RemoveSystemBackdropTarget clears proxy.SystemBackdrop = null synchronously
            // BEFORE closing controllerCompositor when the 5th (last) target detaches.
            foreach (MaskedBackdropTargetProxy proxy in proxies)
            {
                proxy.SystemBackdrop = null;
            }

            Assert.All(targets, static t => Assert.Null(t.SystemBackdrop));

            // Now the last target has detached and DesktopAcrylicController closes its internal Compositor.
            controllerBrush.Dispose();
            controllerCompositor.Dispose();

            // Subsequent parameter updates, TrimIdle, re-attachment to a newly created controller
            // compositor on next reveal, and disposal must never throw ObjectDisposedException
            // or assign a cross-compositor brush.
            foreach (MaskedBackdropTargetProxy proxy in proxies)
            {
                proxy.UpdateParameters(cornerRadiusDip: 14.0, rasterizationScale: 1.0);
            }

            RoundedBackdropMaskService.TrimIdle();

            using var nextControllerCompositor = new Windows.UI.Composition.Compositor();
            using var nextControllerBrush = nextControllerCompositor.CreateColorBrush(Colors.CadetBlue);
            for (int i = 0; i < proxies.Length; i++)
            {
                targets[i].AllowCompositor(nextControllerCompositor);
                proxies[i].SystemBackdrop = nextControllerBrush;
                var reattachedMask = Assert.IsType<Windows.UI.Composition.CompositionMaskBrush>(targets[i].SystemBackdrop);
                Assert.Same(nextControllerCompositor, reattachedMask.Compositor);
            }

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
        RunOnStaWithCompositor(_ =>
        {
            var deadCompositor = new Windows.UI.Composition.Compositor();
            var target = new StrictElementCompositorBackdropTarget(expectedCompositor: deadCompositor);
            var proxy = new MaskedBackdropTargetProxy(target, cornerRadiusDip: 14.0, rasterizationScale: 1.25);

            var deadSourceBrush = deadCompositor.CreateColorBrush(Colors.SlateBlue);
            proxy.SystemBackdrop = deadSourceBrush;
            Assert.IsType<Windows.UI.Composition.CompositionMaskBrush>(target.SystemBackdrop);

            // Force-close the compositor while _maskBrush and the cached NineGridBrush
            // entry are still referenced by the proxy and CompositorMaskCache.
            deadCompositor.Dispose();

            // Clearing source, updating parameters, suspending, and disposing must never
            // propagate RO_E_CLOSED / ObjectDisposedException.
            var exception = Record.Exception(() =>
            {
                proxy.SystemBackdrop = null;
                proxy.UpdateParameters(cornerRadiusDip: 16.0, rasterizationScale: 1.5);
                proxy.ReleaseSuspendedResources();
                proxy.Dispose();
            });
            Assert.Null(exception);
            Assert.Null(target.SystemBackdrop);

            // Acquiring against a closed compositor returns null cleanly without throwing.
            var ephemeralCacheCompositor = new Windows.UI.Composition.Compositor();
            var lease = RoundedBackdropMaskService.AcquireNineGridMask(
                ephemeralCacheCompositor,
                radiusDip: 12.0,
                rasterizationScale: 1.0);
            Assert.NotNull(lease);
            lease!.Dispose();

            ephemeralCacheCompositor.Dispose();

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
    public void Win10InteractionSimplification_PreservesDesktopAcrylicController_AndNeverInjectsCrossCompositorOverride()
    {
        string widgetBackdrop = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Backdrop.cs"));
        string maskService = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/RoundedBackdropMaskService.cs"));
        string materialBackdrop = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetMaterialSystemBackdrop.cs"));

        // SystemBackdropElement (ContentExternalBackdropLink) targets fail-fast natively (0xc000027b)
        // in Windows 10 VMs if a brush created on SolidTintSystemBackdrop's standalone thread
        // Compositor is assigned to them. Ensure MaskedBackdropTargetProxy never touches
        // SolidTintSystemBackdrop or exposes cross-compositor override brushes.
        Assert.DoesNotContain("SolidTintSystemBackdrop", maskService, StringComparison.Ordinal);
        Assert.DoesNotContain("SetInteractionTintOverride", maskService, StringComparison.Ordinal);
        Assert.DoesNotContain("SetInteractionTintOverride", materialBackdrop, StringComparison.Ordinal);
        Assert.DoesNotContain("SetInteractionTintOverride", widgetBackdrop, StringComparison.Ordinal);

        // SimplifyBackdropForInteraction must only target legacy Win32 SWCA_ACCENT_POLICY
        // (!LegacyAccentBackdropActive -> return) and never detach DesktopAcrylicController
        // mid-animation (which strips frosted/fallback backing at frame 0 and causes a visible
        // semi-transparent pop before slide/hide animations).
        int simplifyStart = widgetBackdrop.IndexOf("public void SimplifyBackdropForInteraction()", StringComparison.Ordinal);
        int restoreStart = widgetBackdrop.IndexOf("public void RestoreBackdropAfterInteraction()", simplifyStart, StringComparison.Ordinal);
        Assert.True(simplifyStart >= 0 && restoreStart > simplifyStart);
        string simplifyBody = widgetBackdrop[simplifyStart..restoreStart];
        Assert.Contains("!LegacyAccentBackdropActive", simplifyBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DetachAcrylicControllerTarget", simplifyBody, StringComparison.Ordinal);
        Assert.DoesNotContain("AcrylicControllerAttached", simplifyBody, StringComparison.Ordinal);

        int canReuseStart = widgetBackdrop.IndexOf("private bool CanReuseAppliedBackdrop", restoreStart, StringComparison.Ordinal);
        Assert.True(canReuseStart > restoreStart);
        string restoreBody = widgetBackdrop[restoreStart..canReuseStart];
        Assert.DoesNotContain("ApplyAcrylicController", restoreBody, StringComparison.Ordinal);
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

    private static void RunOnStaWithCompositor(Action<Windows.UI.Composition.Compositor> action)
    {
        Exception? staError = null;
        var staThread = new Thread(() =>
        {
            try
            {
                Windows.UI.Composition.Compositor? threadCompositor =
                    SolidTintSystemBackdrop.TryEnsureThreadCompositor();
                Assert.NotNull(threadCompositor);
                action(threadCompositor!);
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

    private sealed class StrictElementCompositorBackdropTarget(
        Windows.UI.Composition.Compositor expectedCompositor) : ICompositionSupportsSystemBackdrop
    {
        private Windows.UI.Composition.Compositor _expectedCompositor = expectedCompositor;
        private Windows.UI.Composition.CompositionBrush? _systemBackdrop;

        public void AllowCompositor(Windows.UI.Composition.Compositor compositor) =>
            _expectedCompositor = compositor;

        public Windows.UI.Composition.CompositionBrush? SystemBackdrop
        {
            get => _systemBackdrop;
            set
            {
                if (value is not null && !ReferenceEquals(value.Compositor, _expectedCompositor))
                {
                    throw new InvalidOperationException(
                        "Cross-compositor brush assigned to SystemBackdropElement (ContentExternalBackdropLink) — native FailFast 0xc000027b!");
                }

                _systemBackdrop = value;
            }
        }
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
