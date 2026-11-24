using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Views;

namespace DeskBox.Tests;

public sealed class Feedback1240RegressionTests : IDisposable
{
    private readonly string _temporaryRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBoxFeedback1240Tests",
        Guid.NewGuid().ToString("N"));

    public Feedback1240RegressionTests()
    {
        CallbackDepth.ResetForTests();
        InteractionBreadcrumbRecorder.ResetForTests();
    }

    public void Dispose()
    {
        CallbackDepth.ResetForTests();
        InteractionBreadcrumbRecorder.ResetForTests();
        if (Directory.Exists(_temporaryRoot))
        {
            Directory.Delete(_temporaryRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("routed-keyboard-input", true)]
    [InlineData("keyboard-focus", true)]
    [InlineData("popup-keyboard-focus", true)]
    [InlineData("explicit-keyboard-input", true)]
    [InlineData("quick-capture-inline-composer", true)]
    [InlineData("quick-capture-new-detail", true)]
    [InlineData("quick-capture-detail-edit", true)]
    [InlineData("quick-capture-search", true)]
    [InlineData("search-widget-input", true)]
    [InlineData("search-widget-focus", true)]
    [InlineData("markdown-editor-proportional-focus", true)]
    [InlineData("todo-quick-add", true)]
    [InlineData("todo-detail-editor", true)]
    [InlineData("routed-pointer", false)]
    [InlineData("routed-pointer-suppressed", false)]
    public void DesktopPinnedKeyboardActivation_RecognizesQuickCaptureAndTodoEditorReasons(
        string reason,
        bool expected)
    {
        Assert.Equal(expected, WidgetWindowBase.IsDesktopPinnedKeyboardActivationReason(reason));
    }

    [Fact]
    public void QuickCaptureSurfaceContent_ActivationFocusPolicy_PreservesActiveEditorFocus()
    {
        Assert.False(QuickCaptureSurfaceContent.ShouldApplyPendingFocusOnActivation(
            pendingFocusTarget: null,
            isInlineComposerOpen: false,
            isDetailEditing: false,
            isSearchExpanded: false));
        Assert.False(QuickCaptureSurfaceContent.ShouldApplyPendingFocusOnActivation(
            pendingFocusTarget: null,
            isInlineComposerOpen: true,
            isDetailEditing: false,
            isSearchExpanded: false));
        Assert.False(QuickCaptureSurfaceContent.ShouldApplyPendingFocusOnActivation(
            pendingFocusTarget: "Items",
            isInlineComposerOpen: true,
            isDetailEditing: false,
            isSearchExpanded: false));
        Assert.False(QuickCaptureSurfaceContent.ShouldApplyPendingFocusOnActivation(
            pendingFocusTarget: "Root",
            isInlineComposerOpen: false,
            isDetailEditing: true,
            isSearchExpanded: false));

        Assert.True(QuickCaptureSurfaceContent.ShouldApplyPendingFocusOnActivation(
            pendingFocusTarget: "InlineComposer",
            isInlineComposerOpen: true,
            isDetailEditing: false,
            isSearchExpanded: false));
        Assert.True(QuickCaptureSurfaceContent.ShouldApplyPendingFocusOnActivation(
            pendingFocusTarget: "DetailEditor",
            isInlineComposerOpen: false,
            isDetailEditing: true,
            isSearchExpanded: false));
        Assert.True(QuickCaptureSurfaceContent.ShouldApplyPendingFocusOnActivation(
            pendingFocusTarget: "Items",
            isInlineComposerOpen: false,
            isDetailEditing: false,
            isSearchExpanded: false));

        Assert.Equal("InlineComposer", QuickCaptureSurfaceContent.ResolveFocusTargetName(
            isInsideInlineComposer: true,
            isInsideDetailEditor: false,
            isInsideSearchBox: false,
            isItemsList: false));
        Assert.Equal("DetailEditor", QuickCaptureSurfaceContent.ResolveFocusTargetName(
            isInsideInlineComposer: false,
            isInsideDetailEditor: true,
            isInsideSearchBox: false,
            isItemsList: false));
    }

    [Fact]
    public void WidgetTrayBatchAnimationDriver_StalledRenderClock_CompletesViaDeadlineAndReleasesIdleTask()
    {
        long timestamp = 1_000_000;
        Action? frameCallback = null;
        Action? scheduledDeadline = null;
        var deadlineDisposed = false;
        var registrationDisposed = false;
        var committedBatches = new List<WidgetTrayWindowPosition[]>();
        var completedCount = 0;

        var driver = new WidgetTrayBatchAnimationDriver(
            (callback, _) =>
            {
                frameCallback = callback;
                return new TestDisposable(() => registrationDisposed = true);
            },
            _ => 1000d / 60,
            positions =>
            {
                WidgetTrayWindowPosition[] snapshot = positions.ToArray();
                committedBatches.Add(snapshot);
                return Enumerable.Repeat(true, snapshot.Length).ToArray();
            },
            () => timestamp,
            log: _ => { },
            scheduleDeadline: (_, onExpired) =>
            {
                scheduledDeadline = onExpired;
                return new TestDisposable(() => deadlineDisposed = true);
            });

        var entries = new[]
        {
            new WidgetTrayBatchAnimationEntry
            {
                WindowHandle = new IntPtr(0x101),
                BaseX = 100,
                BaseY = 200,
                FromOffsetX = 0,
                FromOffsetY = 0,
                ToOffsetX = -40,
                ToOffsetY = 0,
                RefreshRateHz = 60,
                IsValid = () => true,
                Completed = () => completedCount++
            },
            new WidgetTrayBatchAnimationEntry
            {
                WindowHandle = new IntPtr(0x102),
                BaseX = 300,
                BaseY = 400,
                FromOffsetX = 0,
                FromOffsetY = 0,
                ToOffsetX = 0,
                ToOffsetY = -20,
                RefreshRateHz = 60,
                IsValid = () => true,
                Completed = () => completedCount++
            }
        };

        driver.Start(entries, durationMs: 180, easingIntensity: "standard", isShowing: false, startDelayFrames: 0);
        Task idleTask = driver.WaitForIdleAsync();

        Assert.True(driver.IsRunning);
        Assert.True(driver.IsHiding);
        Assert.False(idleTask.IsCompleted);
        Assert.NotNull(scheduledDeadline);

        // Simulate frame 0 followed by a stalled render clock.
        frameCallback?.Invoke();
        Assert.True(driver.IsRunning);
        Assert.False(idleTask.IsCompleted);

        scheduledDeadline!();

        Assert.True(idleTask.IsCompletedSuccessfully);
        Assert.False(driver.IsRunning);
        Assert.False(driver.IsHiding);
        Assert.True(registrationDisposed);
        Assert.True(deadlineDisposed);
        Assert.Equal(2, completedCount);
        Assert.NotEmpty(committedBatches);
        WidgetTrayWindowPosition[] finalBatch = committedBatches[^1];
        Assert.Contains(finalBatch, pos => pos.WindowHandle == new IntPtr(0x101) && pos.X == 60 && pos.Y == 200);
        Assert.Contains(finalBatch, pos => pos.WindowHandle == new IntPtr(0x102) && pos.X == 300 && pos.Y == 380);
    }

    [Fact]
    public void TrayLeftClickAndMenuClose_DeferShowWindowOutOfNativeCallbacks()
    {
        MethodInfo? onTrayLeftClick = typeof(App).GetMethod(
            "OnTrayLeftClickRequested",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(onTrayLeftClick);
        List<MethodBase> leftClickCalls = ReadDirectIlCalls(onTrayLeftClick!);
        Assert.Contains(leftClickCalls, static m =>
            m.DeclaringType == typeof(UiDispatch) &&
            m.Name == nameof(UiDispatch.PostAfterCallback));
        Assert.DoesNotContain(leftClickCalls, static m =>
            m.DeclaringType == typeof(TrayContextMenuWindow) &&
            m.Name == nameof(TrayContextMenuWindow.DismissActiveIfOpen));

        MethodInfo? closeMenu = typeof(TrayContextMenuWindow).GetMethod(
            "CloseMenu",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(closeMenu);
        List<MethodBase> closeMenuCalls = ReadDirectIlCalls(closeMenu!);
        Assert.Contains(closeMenuCalls, static m =>
            m.DeclaringType == typeof(CallbackDepth) &&
            m.Name == "get_Depth");
        Assert.Contains(closeMenuCalls, static m =>
            m.DeclaringType == typeof(UiDispatch) &&
            m.Name == nameof(UiDispatch.PostAfterCallback));

        CallbackDepth.Enter();
        try
        {
            _ = Win32Helper.SetWindowLong(IntPtr.Zero, Win32Helper.GWL_EXSTYLE, 0);
        }
        finally
        {
            CallbackDepth.Exit();
        }

        IReadOnlyDictionary<string, long> counts = CallbackDepth.SnapshotWindowOperationCounts();
        Assert.True(counts.TryGetValue(nameof(Win32Helper.SetWindowLong), out long count) && count >= 1);
    }

    [Fact]
    public async Task InteractionBreadcrumbRecorder_RingBufferCapsAndSurvivesDiagnosticsSerialization()
    {
        for (int i = 0; i < InteractionBreadcrumbRecorder.Capacity + 6; i++)
        {
            InteractionBreadcrumbRecorder.Record("step", $"n={i}");
        }

        IReadOnlyList<string> breadcrumbs = InteractionBreadcrumbRecorder.Snapshot();
        Assert.Equal(InteractionBreadcrumbRecorder.Capacity, breadcrumbs.Count);
        Assert.Contains("n=6", breadcrumbs[0], StringComparison.Ordinal);
        Assert.Contains($"n={InteractionBreadcrumbRecorder.Capacity + 5}", breadcrumbs[^1], StringComparison.Ordinal);
        Assert.Equal(breadcrumbs[^1], InteractionBreadcrumbRecorder.GetLatest());

        Directory.CreateDirectory(_temporaryRoot);
        string logPath = Path.Combine(_temporaryRoot, "DeskBox.log");
        await File.WriteAllTextAsync(logPath, "[Test] sample\n");

        var snapshot = new DeskBoxDiagnosticSnapshot(
            SchemaVersion: 10,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            AppVersion: "1.6.0",
            DistributionChannel: "Direct",
            IsPackaged: false,
            OperatingSystem: "Windows",
            ProcessArchitecture: "X64",
            UiCulture: "zh-CN",
            Hotkeys: new DeskBoxHotkeyDiagnostic(false, false, 0, 0, 0, 0, 0, false, 0, 0, 0, 0, 0, false, false, false, 0, 0),
            Settings: new DeskBoxSettingsDiagnostic(SettingsLoadRecoveryState.Primary, false, null, null),
            RecoveryOffer: new DeskBoxRecoveryOfferDiagnostic(false, null, null, null, 0, "NotOffered"),
            ShortcutNative: new DeskBoxShortcutNativeDiagnostic("Native", "deskbox_native.dll", true, "X64", null, false, "Loaded", null, null),
            RuntimeHealth: null,
            WidgetManager: new DeskBoxWidgetManagerDiagnostic(
                false,
                "DesktopResting",
                false,
                1,
                1,
                DeskBoxWidgetManagerDiagnostic.Empty.FileHosts,
                DeskBoxWidgetManagerDiagnostic.Empty.ToggleQueue,
                [
                    new DeskBoxWidgetHostDiagnostic(
                        1,
                        WidgetKind.QuickCapture,
                        "QuickCapture",
                        false,
                        true,
                        false,
                        false,
                        false,
                        null,
                        new DeskBoxDiagnosticRect(0, 0, 280, 400),
                        new DeskBoxDiagnosticRect(0, 0, 280, 400),
                        InputSuppressed: true)
                ]),
            Displays: [],
            RecentInteractionBreadcrumbs: breadcrumbs);

        string archivePath = await new DeskBoxDiagnosticsBundleService().ExportAsync(
            _temporaryRoot,
            snapshot,
            logPath);

        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        ZipArchiveEntry? entry = archive.GetEntry("diagnostics.json");
        Assert.NotNull(entry);
        await using Stream stream = entry!.Open();
        using var reader = new StreamReader(stream);
        string diagnosticsJson = await reader.ReadToEndAsync();

        Assert.Contains("\"schemaVersion\": 10", diagnosticsJson, StringComparison.Ordinal);
        Assert.Contains("\"inputSuppressed\": true", diagnosticsJson, StringComparison.Ordinal);
        Assert.Contains("\"recentInteractionBreadcrumbs\"", diagnosticsJson, StringComparison.Ordinal);
        Assert.Contains($"n={InteractionBreadcrumbRecorder.Capacity + 5}", diagnosticsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopPinnedActivationRestorePolicy_PreservesTransientImeAndRecentKeyboardRequests()
    {
        // Deactivated due to IME candidate / Emoji / TextCommandBarFlyout must keep text focus un-restored.
        Assert.True(WidgetWindowBase.ShouldDeferDesktopPinnedRestoreOnActivation(
            isActivating: false,
            hasDeferredRestoreForTextInput: false,
            elapsedSinceKeyboardRequestMs: 1000,
            hasFocusedTextInputWithTransientForeground: true));

        // Deactivated to an unrelated foreign window must still restore bottom state even right after a keyboard request.
        Assert.False(WidgetWindowBase.ShouldDeferDesktopPinnedRestoreOnActivation(
            isActivating: false,
            hasDeferredRestoreForTextInput: true,
            elapsedSinceKeyboardRequestMs: 10,
            hasFocusedTextInputWithTransientForeground: false));

        // Async CodeActivated/PointerActivated right after TryActivateOwnerForKeyboardInput while the textbox
        // is becoming visible must honor the recent keyboard request window.
        Assert.True(WidgetWindowBase.ShouldDeferDesktopPinnedRestoreOnActivation(
            isActivating: true,
            hasDeferredRestoreForTextInput: true,
            elapsedSinceKeyboardRequestMs: 40,
            hasFocusedTextInputWithTransientForeground: false));

        // Once past the 150ms grace window with no active text focus, CodeActivated restores normally.
        Assert.False(WidgetWindowBase.ShouldDeferDesktopPinnedRestoreOnActivation(
            isActivating: true,
            hasDeferredRestoreForTextInput: true,
            elapsedSinceKeyboardRequestMs: 150,
            hasFocusedTextInputWithTransientForeground: false));
    }

    [Fact]
    public async Task TrayToggleRequestQueue_NotifiesBusyCallbackOnlyForNonSuppressedQueuedToggles()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int busyNotified = 0;

        var queue = new TrayToggleRequestQueue(
            async _ =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            },
            trayIconDoubleClickWindow: TimeSpan.FromSeconds(10),
            onAcceptedWhileBusy: () => Interlocked.Increment(ref busyNotified));

        Task first = queue.EnqueueAsync(TrayToggleRequestQueue.TrayIconSource);
        await firstStarted.Task;
        Assert.Equal(0, Volatile.Read(ref busyNotified));

        // A second tray-icon click inside the double-click window is suppressed and must not notify busy.
        await queue.EnqueueAsync(TrayToggleRequestQueue.TrayIconSource);
        Assert.Equal(0, Volatile.Read(ref busyNotified));

        // A genuine queued toggle (e.g. global hotkey) while the worker is busy notifies immediately.
        Task hotkey = queue.EnqueueAsync("global-hotkey");
        Assert.Equal(1, Volatile.Read(ref busyNotified));

        releaseFirst.TrySetResult();
        await Task.WhenAll(first, hotkey);
    }

    [Fact]
    public void AnimationClockHardening_DefersWatchdogOutOfPreArmAndAddsSingleWidgetDeadline()
    {
        string coordinatorSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/WidgetCompactAnimationCoordinator.cs"));
        string startFrameClock = SliceBetween(
            coordinatorSource,
            "private static void StartFrameClock()",
            "private static void StartWindows10DwmFlushClock()");
        string registerCore = SliceBetween(
            coordinatorSource,
            "private static IDisposable RegisterCore(",
            "private static void EnsureWin11StallWatchdogRunning()");
        string scheduleDeadline = SliceBetween(
            coordinatorSource,
            "internal static IDisposable ScheduleDeadline(",
            "private sealed class DeadlineRegistration(");

        Assert.DoesNotContain("StartWin11StallWatchdog", startFrameClock, StringComparison.Ordinal);
        Assert.Contains("EnsureWin11StallWatchdogRunning()", registerCore, StringComparison.Ordinal);
        Assert.Contains("CallbackDepth.Depth > 0", scheduleDeadline, StringComparison.Ordinal);
        Assert.Contains("UiDispatch.PostAfterCallback", scheduleDeadline, StringComparison.Ordinal);

        string controllerSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/WidgetTrayAnimationController.cs"));
        string playAfterContentReady = SliceBetween(
            controllerSource,
            "public void PlayAfterContentReady(",
            "public async Task WaitForContentReadyAsync(");

        Assert.Contains("WidgetCompactAnimationCoordinator.ScheduleDeadline", controllerSource, StringComparison.Ordinal);
        Assert.Contains("OnSingleAnimationDeadlineExpired", controllerSource, StringComparison.Ordinal);
        Assert.Contains("CallbackDepth.Depth > 0", playAfterContentReady, StringComparison.Ordinal);
        Assert.Contains("UiDispatch.PostAfterCallback", playAfterContentReady, StringComparison.Ordinal);
    }

    private static string SliceBetween(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker not found: {endMarker}");
        return source[start..end];
    }

    private sealed class TestDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private static readonly (OpCode[] SingleByte, OpCode[] TwoByte) s_opCodeTables = BuildOpCodeTables();

    private static (OpCode[] SingleByte, OpCode[] TwoByte) BuildOpCodeTables()
    {
        var singleByte = new OpCode[256];
        var twoByte = new OpCode[256];
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(OpCode))
            {
                continue;
            }

            var op = (OpCode)field.GetValue(null)!;
            ushort value = unchecked((ushort)op.Value);
            if ((value & 0xFF00) == 0xFE00)
            {
                twoByte[value & 0xFF] = op;
            }
            else if (value < 0x100)
            {
                singleByte[value] = op;
            }
        }

        return (singleByte, twoByte);
    }

    private static List<MethodBase> ReadDirectIlCalls(MethodBase method)
    {
        var calls = new List<MethodBase>();
        MethodBody? body = method.GetMethodBody();
        byte[]? il = body?.GetILAsByteArray();
        if (il is not { Length: > 0 })
        {
            return calls;
        }

        Type? declaringType = method.DeclaringType;
        Type[] genericTypeArgs = declaringType is { IsGenericType: true }
            ? declaringType.GetGenericArguments()
            : Type.EmptyTypes;
        Type[] genericMethodArgs = method is MethodInfo { IsGenericMethod: true } mi
            ? mi.GetGenericArguments()
            : Type.EmptyTypes;

        int offset = 0;
        while (offset < il.Length)
        {
            byte b = il[offset++];
            OpCode op = b == 0xFE && offset < il.Length
                ? s_opCodeTables.TwoByte[il[offset++]]
                : s_opCodeTables.SingleByte[b];

            switch (op.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    offset += 1;
                    break;
                case OperandType.InlineVar:
                    offset += 2;
                    break;
                case OperandType.InlineMethod:
                    if ((op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj) &&
                        offset + 4 <= il.Length)
                    {
                        int metadataToken = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4));
                        try
                        {
                            if (method.Module.ResolveMethod(metadataToken, genericTypeArgs, genericMethodArgs) is { } target)
                            {
                                calls.Add(target);
                            }
                        }
                        catch
                        {
                        }
                    }

                    offset += 4;
                    break;
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR:
                    offset += 4;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    offset += 8;
                    break;
                case OperandType.InlineSwitch:
                    if (offset + 4 > il.Length)
                    {
                        return calls;
                    }

                    int targetCount = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4));
                    offset += 4 + (targetCount * 4);
                    break;
                default:
                    return calls;
            }
        }

        return calls;
    }
}
