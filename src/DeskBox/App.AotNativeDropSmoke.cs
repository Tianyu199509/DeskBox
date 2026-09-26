#if DESKBOX_NATIVE_AOT
using System.Diagnostics;
using System.Security.Cryptography;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Services;
using DeskBox.Views;

namespace DeskBox;

public partial class App
{
    private const uint AotNativeDropControlKeyState = 0x0008;

    // The product delays the acrylic import card by 120 ms
    // (FileSurfaceContent.ImportProgress.ImportCardShowDelay) before showing
    // it, so a "card invisible" sample only proves the Shell delegation
    // suppressed the card once the busy window has outlived that delay plus
    // timer, dispatcher and polling slack. Sampling before this floor cannot
    // distinguish "suppressed" from "not due to show yet".
    private const int AotImportCardDelayProofFloorMilliseconds = 150;

    private static readonly string[] AotNativeDropBaselineSurfaceNames =
    [
        AotNativeDropFixture.TargetFolderName,
        "baseline"
    ];

    private static readonly string[] AotNativeDropMutationSurfaceNames =
    [
        AotNativeDropFixture.CopyFolderName,
        AotNativeDropFixture.MoveFolderName,
        AotNativeDropFixture.TargetFolderName,
        "baseline",
        "copy-large",
        "move-small"
    ];

    private async Task CaptureAotManagedUiNativeDropAsync(
        AotManagedUiSmokeResult result,
        string phase)
    {
        AotManagedUiNativeDropEvidence evidence = result.NativeDrop ??
            throw new InvalidOperationException(
                "The native-drop evidence container is unavailable.");
        AotNativeDropFixturePaths paths = AotNativeDropFixture.GetOwnedPaths(
            DeskBoxDataPathService.Current);
        WidgetManager manager = WidgetManager ??
            throw new InvalidOperationException("WidgetManager is unavailable.");
        AotNativeDropSurfaceHost host =
            await manager.GetAotNativeDropSurfaceHostAsync(
                AotNativeDropFixture.OwnedWidgetId);

        evidence.RunId = paths.RunId;
        evidence.WidgetRoot = paths.WidgetRoot;
        evidence.SourceRoot = paths.SourceRoot;
        evidence.HostWindowHandle = host.WindowHandle;
        evidence.HostHasXamlRoot = host.HasXamlRoot;
        evidence.HostVisible = host.Visible;
        evidence.SourceKind = "ProgrammaticGeneratedCcwHDrop";
        evidence.PhysicalExplorerMouseVerified = false;
        RequireAotManagedUi(
            result,
            host.WindowHandle != 0 &&
            host.HasXamlRoot &&
            host.Visible &&
            IsAotManagedUiPathEqual(
                host.ViewModel.MappedFolderPath ?? string.Empty,
                paths.WidgetRoot),
            "NativeDropHostReady",
            "The real owned File Widget HWND, XamlRoot or mapped root is unavailable.");

        bool expectMutationBefore = phase == "VerifyRestore";
        evidence.Before = await CaptureAotNativeDropStateAsync(
            host,
            paths,
            expectMutationBefore);
        RequireAotNativeDropState(
            result,
            evidence.Before,
            paths,
            expectMutationBefore,
            expectMutationBefore
                ? "NativeDropRestartMutationVerified"
                : "NativeDropBaselineVerified");

        switch (phase)
        {
            case "Mutate":
                await ApplyAotNativeDropMutationAsync(
                    result,
                    evidence,
                    host,
                    paths);
                evidence.After = await CaptureAotNativeDropStateAsync(
                    host,
                    paths,
                    expectMutation: true);
                RequireAotNativeDropState(
                    result,
                    evidence.After,
                    paths,
                    expectMutation: true,
                    "NativeDropMutationApplied");
                break;

            case "VerifyRestore":
                await RestoreAotNativeDropBaselineAsync(
                    result,
                    evidence,
                    host,
                    paths);
                evidence.After = await CaptureAotNativeDropStateAsync(
                    host,
                    paths,
                    expectMutation: false);
                RequireAotNativeDropState(
                    result,
                    evidence.After,
                    paths,
                    expectMutation: false,
                    "NativeDropBaselineRestored");
                break;

            case "Postflight":
                evidence.After = await CaptureAotNativeDropStateAsync(
                    host,
                    paths,
                    expectMutation: false);
                RequireAotNativeDropState(
                    result,
                    evidence.After,
                    paths,
                    expectMutation: false,
                    "NativeDropPostflightVerified");
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported native-drop phase '{phase}'.");
        }

        SettingsService.SaveDebounced(notifySubscribers: false);
        evidence.FlushSucceeded = await SettingsService.FlushPendingSaveAsync(
            notifySubscribers: false);
        RequireAotManagedUi(
            result,
            evidence.FlushSucceeded,
            "NativeDropPersistenceFlushed",
            "The native-drop phase did not flush settings successfully.");
    }

    private async Task ApplyAotNativeDropMutationAsync(
        AotManagedUiSmokeResult result,
        AotManagedUiNativeDropEvidence evidence,
        AotNativeDropSurfaceHost host,
        AotNativeDropFixturePaths paths)
    {
        string[] copyPaths =
        [
            paths.CopyLargeSourceFile,
            paths.CopySourceFolder
        ];
        string[] movePaths =
        [
            paths.MoveSourceFile,
            paths.MoveSourceFolder
        ];

        AotNativeDropHighlightProbe pointerProbe =
            host.Surface.PrimeAotNativeFolderHighlight(paths.TargetFolder);
        AotNativeDropCallbackResult pointerCallbacks =
            host.Window.InvokeAotNativeHDropCallbacks(
                copyPaths,
                pointerProbe.OutsideScreenX,
                pointerProbe.OutsideScreenY,
                AotNativeDropControlKeyState,
                leaveWithoutDrop: false,
                stopAfterDragOver: true);
        AotNativeDropHighlightState afterPointer =
            host.Surface.CaptureAotNativeFolderHighlightState(
                paths.TargetFolder);
        int pointerCleanupResult =
            host.Window.InvokeAotNativeDragLeaveCallback();
        evidence.NativePointerClear = MapAotNativeDropHighlightEvidence(
            pointerProbe,
            pointerCallbacks,
            afterPointer,
            pointerCleanupResult);
        RequireAotManagedUi(
            result,
            pointerProbe.HighlightActiveBeforeNativeCallback &&
            pointerProbe.FolderVisualStateBeforeNativeCallback == "DropTarget" &&
            pointerCallbacks.TargetRegistered &&
            pointerCallbacks.DragEnterHResult == 0 &&
            pointerCallbacks.DragOverHResult == 0 &&
            pointerCallbacks.DragEnterEffect == NativeDropEffectPolicy.Copy &&
            pointerCallbacks.DragOverEffect == NativeDropEffectPolicy.Copy &&
            !afterPointer.AnyChildHighlightActive &&
            afterPointer.FolderVisualState == "Normal" &&
            pointerCleanupResult == 0,
            "NativeDropScreenPointClearedStaleFolderHighlight",
            "The native DragOver screen-point fallback did not clear the stale folder target.");

        AotNativeDropHighlightProbe leaveProbe =
            host.Surface.PrimeAotNativeFolderHighlight(paths.TargetFolder);
        int insideFolderX =
            (leaveProbe.FolderBounds.Left + leaveProbe.FolderBounds.Right) / 2;
        int insideFolderY =
            (leaveProbe.FolderBounds.Top + leaveProbe.FolderBounds.Bottom) / 2;
        AotNativeDropCallbackResult leaveCallbacks =
            host.Window.InvokeAotNativeHDropCallbacks(
                copyPaths,
                insideFolderX,
                insideFolderY,
                AotNativeDropControlKeyState,
                leaveWithoutDrop: true);
        AotNativeDropHighlightState afterLeave =
            host.Surface.CaptureAotNativeFolderHighlightState(
                paths.TargetFolder);
        evidence.NativeLeaveClear = MapAotNativeDropHighlightEvidence(
            leaveProbe,
            leaveCallbacks,
            afterLeave,
            leaveCallbacks.DragLeaveHResult);
        RequireAotManagedUi(
            result,
            leaveProbe.HighlightActiveBeforeNativeCallback &&
            leaveCallbacks.DragLeaveHResult == 0 &&
            !afterLeave.AnyChildHighlightActive &&
            afterLeave.FolderVisualState == "Normal",
            "NativeDropLeaveClearedFolderHighlight",
            "The generated IDropTarget DragLeave callback left a folder highlighted.");

        evidence.CopyImport = await InvokeAotNativeDropImportAsync(
            result,
            host,
            copyPaths,
            pointerProbe.OutsideScreenX,
            pointerProbe.OutsideScreenY,
            AotNativeDropControlKeyState,
            expectedFeedbackEffect: NativeDropEffectPolicy.Copy,
            requireTransferBusyEvidence: true,
            stepPrefix: "NativeDropCopy");
        await host.Surface.WaitForAotLocalFileSurfaceAsync(
            paths.WidgetRoot,
            [
                AotNativeDropFixture.CopyFolderName,
                AotNativeDropFixture.TargetFolderName,
                "baseline",
                "copy-large"
            ],
            expectAtMappedRoot: true);

        evidence.MoveImport = await InvokeAotNativeDropImportAsync(
            result,
            host,
            movePaths,
            pointerProbe.OutsideScreenX,
            pointerProbe.OutsideScreenY,
            keyState: 0,
            expectedFeedbackEffect: NativeDropEffectPolicy.Move,
            requireTransferBusyEvidence: false,
            stepPrefix: "NativeDropMove");
        await host.Surface.WaitForAotLocalFileSurfaceAsync(
            paths.WidgetRoot,
            AotNativeDropMutationSurfaceNames,
            expectAtMappedRoot: true);

        // Runtime coverage for the acrylic card itself: the managed (non
        // shell-delegated) transfer engine is the path a browser file or URL
        // download drop takes, and its card MUST become visible while the
        // surface is busy. This is the counterpart of the suppression proof
        // above: one engine must show the card, the other must not.
        evidence.ManagedCardImport = await InvokeAotNativeDropManagedCardImportAsync(
            result,
            host,
            paths);

        bool copySemantics =
            File.Exists(paths.CopyLargeSourceFile) &&
            Directory.Exists(paths.CopySourceFolder) &&
            File.Exists(paths.CopyDestinationFile) &&
            Directory.Exists(paths.CopyDestinationFolder);
        bool moveSemantics =
            !File.Exists(paths.MoveSourceFile) &&
            !Directory.Exists(paths.MoveSourceFolder) &&
            File.Exists(paths.MoveDestinationFile) &&
            Directory.Exists(paths.MoveDestinationFolder);
        RequireAotManagedUi(
            result,
            copySemantics && moveSemantics,
            "NativeDropCopyMoveSemanticsVerified",
            "The native drop did not preserve copy sources or remove move sources as requested.");
    }

    private async Task<AotManagedUiNativeDropImportEvidence>
        InvokeAotNativeDropImportAsync(
            AotManagedUiSmokeResult result,
            AotNativeDropSurfaceHost host,
            IReadOnlyList<string> paths,
            int screenX,
            int screenY,
            uint keyState,
            uint expectedFeedbackEffect,
            bool requireTransferBusyEvidence,
            string stepPrefix)
    {
        var busyStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var busyEnded = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnImportBusyChanged(bool busy)
        {
            if (busy)
            {
                busyStarted.TrySetResult(true);
            }
            else
            {
                busyEnded.TrySetResult(true);
            }
        }

        host.Surface.ImportBusyChanged += OnImportBusyChanged;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            AotNativeDropCallbackResult callbacks =
                host.Window.InvokeAotNativeHDropCallbacks(
                    paths,
                    screenX,
                    screenY,
                    keyState,
                    leaveWithoutDrop: false);
            stopwatch.Stop();
            AotNativeDropProgressSnapshot immediatelyAfterCallback =
                host.Surface.CaptureAotNativeDropProgress();

            RequireAotManagedUi(
                result,
                callbacks.TargetRegistered &&
                callbacks.DragEnterHResult == 0 &&
                callbacks.DragOverHResult == 0 &&
                callbacks.DropHResult == 0 &&
                callbacks.DragEnterEffect == expectedFeedbackEffect &&
                callbacks.DragOverEffect == expectedFeedbackEffect &&
                callbacks.CompletionEffect == NativeDropEffectPolicy.Copy &&
                !immediatelyAfterCallback.IsImportBusy &&
                !immediatelyAfterCallback.CardVisible,
                stepPrefix + "OleCallbackReleasedBeforeProgress",
                "The generated OLE callback did not return before the asynchronous product import began.");

            await busyStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            AotNativeDropProgressSnapshot duringImport;
            if (requireTransferBusyEvidence)
            {
                // Physical-file copies delegate to the Windows Shell engine,
                // which owns the native progress window. The widget surface
                // must stay busy for the transfer without painting a second,
                // competing progress card over the Shell's own progress. The
                // card's own show delay is 120 ms, so the busy sample is only
                // taken once the window has outlived that delay: an earlier
                // sample could not tell suppression from "not due yet".
                duringImport = await WaitForAotImportBusyPastCardDelayAsync(
                    host,
                    TimeSpan.FromSeconds(5));
                RequireAotManagedUi(
                    result,
                    duringImport.IsImportBusy &&
                    !duringImport.CardVisible &&
                    duringImport.BusyElapsedMilliseconds >=
                        AotImportCardDelayProofFloorMilliseconds,
                    stepPrefix + "ProgressDeferredToShellTransfer",
                    "The delegated shell transfer did not keep the surface " +
                    "busy without a competing widget progress card after the " +
                    "import-card delay elapsed.");
            }
            else
            {
                duringImport = host.Surface.CaptureAotNativeDropProgress();
            }

            await busyEnded.Task.WaitAsync(TimeSpan.FromSeconds(180));
            AotNativeDropProgressSnapshot afterImport =
                host.Surface.CaptureAotNativeDropProgress();
            RequireAotManagedUi(
                result,
                !afterImport.IsImportBusy && !afterImport.CardVisible,
                stepPrefix + "ImportCompleted",
                "The native-drop import did not settle and hide its progress card.");

            return new AotManagedUiNativeDropImportEvidence
            {
                CallbackElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                Callback = MapAotNativeDropCallback(callbacks),
                ImmediatelyAfterCallback = MapAotNativeDropProgress(
                    immediatelyAfterCallback),
                DuringImport = MapAotNativeDropProgress(duringImport),
                AfterImport = MapAotNativeDropProgress(afterImport)
            };
        }
        finally
        {
            host.Surface.ImportBusyChanged -= OnImportBusyChanged;
        }
    }

    private async Task<AotManagedUiNativeDropManagedCardEvidence>
        InvokeAotNativeDropManagedCardImportAsync(
            AotManagedUiSmokeResult result,
            AotNativeDropSurfaceHost host,
            AotNativeDropFixturePaths paths)
    {
        AotNativeDropProgressSnapshot settledBeforeProbe =
            host.Surface.CaptureAotNativeDropProgress();
        RequireAotManagedUi(
            result,
            !settledBeforeProbe.IsImportBusy &&
            !settledBeforeProbe.CardVisible,
            "NativeDropManagedCardImportStartedIdle",
            "The managed-card probe requires an idle surface with no visible progress card.");

        string[] widgetRootFilesBefore = await Task.Run(() =>
            Directory.GetFiles(paths.WidgetRoot));
        var busyStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnImportBusyChanged(bool busy)
        {
            if (busy)
            {
                busyStarted.TrySetResult(true);
            }
        }

        host.Surface.ImportBusyChanged += OnImportBusyChanged;
        try
        {
            // containsTemporaryFiles routes every path through the product's
            // ForceManagedCopy leg (the exact path a browser file or URL
            // download drop takes): the managed chunked engine runs with
            // useShellProgress: false while the surface tracks busy state and
            // delays the acrylic card, so once the busy window outlives the
            // 120 ms delay the card must be visible.
            Task<bool> importTask = host.Surface.ImportNativeDroppedFilesAsync(
                [paths.CopyLargeSourceFile],
                containsTemporaryFiles: true,
                copyWhenMapped: true);
            await busyStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            AotNativeDropProgressSnapshot cardShown =
                await WaitForAotImportCardVisibleSnapshotAsync(
                    host,
                    TimeSpan.FromSeconds(30));
            RequireAotManagedUi(
                result,
                cardShown.IsImportBusy &&
                cardShown.CardVisible &&
                cardShown.BackgroundIsAcrylicBrush &&
                cardShown.CanvasZIndex == 1000,
                "NativeDropManagedImportShowedAcrylicCard",
                "The managed (non-shell) transfer did not show the top-layer acrylic import card while busy.");

            bool importAccepted = await importTask;
            AotNativeDropProgressSnapshot afterImport =
                host.Surface.CaptureAotNativeDropProgress();
            RequireAotManagedUi(
                result,
                importAccepted &&
                !afterImport.IsImportBusy &&
                !afterImport.CardVisible,
                "NativeDropManagedImportSettled",
                "The managed-card import did not complete and collapse its progress card.");

            string destinationFile = await Task.Run(() =>
                Directory.GetFiles(paths.WidgetRoot)
                    .Except(
                        widgetRootFilesBefore,
                        StringComparer.OrdinalIgnoreCase)
                    .Single());
            await Task.Run(() => File.Delete(destinationFile));
            AotLocalFileSurfaceSnapshot restoredSurface =
                await host.Surface.WaitForAotLocalFileSurfaceAsync(
                    paths.WidgetRoot,
                    AotNativeDropMutationSurfaceNames,
                    expectAtMappedRoot: true);

            var evidence = new AotManagedUiNativeDropManagedCardEvidence
            {
                CardShown = MapAotNativeDropProgress(cardShown),
                AfterImport = MapAotNativeDropProgress(afterImport),
                DestinationFile = destinationFile,
                DestinationRemovedAfterProbe =
                    !File.Exists(destinationFile) &&
                    restoredSurface.ProjectedItemCount ==
                        AotNativeDropMutationSurfaceNames.Length
            };
            RequireAotManagedUi(
                result,
                evidence.DestinationRemovedAfterProbe,
                "NativeDropManagedCardProbeCleanedUp",
                "The managed-card probe did not return the owned widget root to its mutation baseline.");
            return evidence;
        }
        finally
        {
            host.Surface.ImportBusyChanged -= OnImportBusyChanged;
        }
    }

    private static async Task<AotNativeDropProgressSnapshot>
        WaitForAotImportBusyPastCardDelayAsync(
            AotNativeDropSurfaceHost host,
            TimeSpan limit)
    {
        DateTime deadline = DateTime.UtcNow + limit;
        bool sawBusy = false;
        long longestBusyElapsedMilliseconds = 0;
        while (DateTime.UtcNow < deadline)
        {
            AotNativeDropProgressSnapshot snapshot =
                host.Surface.CaptureAotNativeDropProgress();
            if (snapshot.IsImportBusy)
            {
                sawBusy = true;
                longestBusyElapsedMilliseconds = Math.Max(
                    longestBusyElapsedMilliseconds,
                    snapshot.BusyElapsedMilliseconds ?? 0);
                if (snapshot.BusyElapsedMilliseconds >=
                    AotImportCardDelayProofFloorMilliseconds)
                {
                    return snapshot;
                }
            }

            await Task.Delay(10);
        }

        // An honest failure: a busy window shorter than the card show delay
        // cannot prove suppression either way, so the run must fail instead
        // of silently reporting an unprovable pass.
        throw new InvalidOperationException(
            sawBusy
                ? $"The native-drop import busy window (longest observed " +
                  $"{longestBusyElapsedMilliseconds} ms) never outlived the " +
                  $"{AotImportCardDelayProofFloorMilliseconds} ms import-card " +
                  "delay proof floor, so shell progress suppression could not " +
                  "be proven on this run."
                : "The native-drop import never entered its busy state.");
    }

    private static async Task<AotNativeDropProgressSnapshot>
        WaitForAotImportCardVisibleSnapshotAsync(
            AotNativeDropSurfaceHost host,
            TimeSpan limit)
    {
        DateTime deadline = DateTime.UtcNow + limit;
        bool sawBusy = false;
        long longestBusyElapsedMilliseconds = 0;
        while (DateTime.UtcNow < deadline)
        {
            AotNativeDropProgressSnapshot snapshot =
                host.Surface.CaptureAotNativeDropProgress();
            if (snapshot.IsImportBusy && snapshot.CardVisible)
            {
                return snapshot;
            }

            if (snapshot.IsImportBusy)
            {
                sawBusy = true;
                longestBusyElapsedMilliseconds = Math.Max(
                    longestBusyElapsedMilliseconds,
                    snapshot.BusyElapsedMilliseconds ?? 0);
            }
            else if (sawBusy)
            {
                // Busy ended without the card ever being observed visible.
                break;
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException(
            !sawBusy
                ? "The managed import busy window ended before the first " +
                  "poll could observe it, so managed card display could not " +
                  "be proven on this run."
                : longestBusyElapsedMilliseconds >=
                        AotImportCardDelayProofFloorMilliseconds
                    ? $"The managed import stayed busy for " +
                      $"{longestBusyElapsedMilliseconds} ms (past the " +
                      $"{AotImportCardDelayProofFloorMilliseconds} ms card " +
                      "delay proof floor) without ever showing its acrylic " +
                      "progress card."
                    : $"The managed import busy window (longest observed " +
                      $"{longestBusyElapsedMilliseconds} ms) never outlived " +
                      $"the {AotImportCardDelayProofFloorMilliseconds} ms " +
                      "import-card delay proof floor, so managed card display " +
                      "could not be proven on this run.");
    }

    private async Task RestoreAotNativeDropBaselineAsync(
        AotManagedUiSmokeResult result,
        AotManagedUiNativeDropEvidence evidence,
        AotNativeDropSurfaceHost host,
        AotNativeDropFixturePaths paths)
    {
        await Task.Run(() =>
        {
            if (File.Exists(paths.CopyDestinationFile))
            {
                File.Delete(paths.CopyDestinationFile);
            }
            if (Directory.Exists(paths.CopyDestinationFolder))
            {
                Directory.Delete(paths.CopyDestinationFolder, recursive: true);
            }
            if (File.Exists(paths.MoveDestinationFile))
            {
                File.Move(paths.MoveDestinationFile, paths.MoveSourceFile);
            }
            if (Directory.Exists(paths.MoveDestinationFolder))
            {
                Directory.Move(
                    paths.MoveDestinationFolder,
                    paths.MoveSourceFolder);
            }
        });

        AotLocalFileSurfaceSnapshot restored =
            await host.Surface.WaitForAotLocalFileSurfaceAsync(
                paths.WidgetRoot,
                AotNativeDropBaselineSurfaceNames,
                expectAtMappedRoot: true);
        evidence.RestoreObservedByWatcher =
            restored.ProjectedItemCount == AotNativeDropBaselineSurfaceNames.Length;
        RequireAotManagedUi(
            result,
            evidence.RestoreObservedByWatcher &&
            File.Exists(paths.MoveSourceFile) &&
            Directory.Exists(paths.MoveSourceFolder) &&
            !File.Exists(paths.CopyDestinationFile) &&
            !Directory.Exists(paths.CopyDestinationFolder),
            "NativeDropOwnedBaselineRestored",
            "The owned native-drop fixture or File Widget watcher did not return to baseline.");
    }

    private async Task<AotManagedUiNativeDropStateEvidence>
        CaptureAotNativeDropStateAsync(
            AotNativeDropSurfaceHost host,
            AotNativeDropFixturePaths paths,
            bool expectMutation)
    {
        string[] expectedNames = expectMutation
            ? AotNativeDropMutationSurfaceNames
            : AotNativeDropBaselineSurfaceNames;
        AotLocalFileSurfaceSnapshot surface =
            await host.Surface.WaitForAotLocalFileSurfaceAsync(
                paths.WidgetRoot,
                expectedNames,
                expectAtMappedRoot: true);
        Dictionary<string, AotManagedUiNativeDropFileEvidence> files =
            await Task.Run(() => CaptureAotNativeDropFiles(paths));

        return new AotManagedUiNativeDropStateEvidence
        {
            ExpectMutation = expectMutation,
            Surface = MapAotLocalFileSurface(surface),
            Files = files
        };
    }

    private static Dictionary<string, AotManagedUiNativeDropFileEvidence>
        CaptureAotNativeDropFiles(AotNativeDropFixturePaths paths)
    {
        string[] candidates =
        [
            paths.BaselineFile,
            paths.CopyLargeSourceFile,
            paths.CopySourceNestedFile,
            paths.MoveSourceFile,
            paths.MoveSourceNestedFile,
            paths.CopyDestinationFile,
            paths.CopyDestinationNestedFile,
            paths.MoveDestinationFile,
            paths.MoveDestinationNestedFile
        ];
        return candidates.ToDictionary(
            path => path,
            path =>
            {
                if (!File.Exists(path))
                {
                    return new AotManagedUiNativeDropFileEvidence
                    {
                        Exists = false
                    };
                }

                using FileStream stream = File.OpenRead(path);
                return new AotManagedUiNativeDropFileEvidence
                {
                    Exists = true,
                    Length = stream.Length,
                    Sha256 = Convert.ToHexString(SHA256.HashData(stream))
                };
            },
            StringComparer.OrdinalIgnoreCase);
    }

    private static void RequireAotNativeDropState(
        AotManagedUiSmokeResult result,
        AotManagedUiNativeDropStateEvidence state,
        AotNativeDropFixturePaths paths,
        bool expectMutation,
        string step)
    {
        string[] expectedNames = expectMutation
            ? AotNativeDropMutationSurfaceNames
            : AotNativeDropBaselineSurfaceNames;
        string[] actualNames = state.Surface.Items
            .Select(item => item.Name)
            .ToArray();
        bool surfaceValid =
            state.ExpectMutation == expectMutation &&
            state.Surface.IsLoaded &&
            state.Surface.HasXamlRoot &&
            state.Surface.DataContextMatchesViewModel &&
            state.Surface.ViewModelInitialized &&
            state.Surface.IsAtMappedRoot &&
            state.Surface.ProjectedItemCount == expectedNames.Length &&
            state.Surface.RealizedContainerCount == expectedNames.Length &&
            actualNames.SequenceEqual(
                expectedNames,
                StringComparer.OrdinalIgnoreCase);
        bool baselineFile = state.Files[paths.BaselineFile].Exists;
        AotManagedUiNativeDropFileEvidence copySource =
            state.Files[paths.CopyLargeSourceFile];
        AotManagedUiNativeDropFileEvidence copySourceNested =
            state.Files[paths.CopySourceNestedFile];
        AotManagedUiNativeDropFileEvidence moveSource =
            state.Files[paths.MoveSourceFile];
        AotManagedUiNativeDropFileEvidence moveSourceNested =
            state.Files[paths.MoveSourceNestedFile];
        AotManagedUiNativeDropFileEvidence copyDestination =
            state.Files[paths.CopyDestinationFile];
        AotManagedUiNativeDropFileEvidence copyDestinationNested =
            state.Files[paths.CopyDestinationNestedFile];
        AotManagedUiNativeDropFileEvidence moveDestination =
            state.Files[paths.MoveDestinationFile];
        AotManagedUiNativeDropFileEvidence moveDestinationNested =
            state.Files[paths.MoveDestinationNestedFile];
        bool diskValid = baselineFile &&
            copySource.Exists &&
            copySourceNested.Exists &&
            (expectMutation
                ? !moveSource.Exists &&
                  !moveSourceNested.Exists &&
                  copyDestination.Exists &&
                  copyDestinationNested.Exists &&
                  moveDestination.Exists &&
                  moveDestinationNested.Exists &&
                  copySource.Length == copyDestination.Length &&
                  copySource.Sha256 == copyDestination.Sha256 &&
                  copySourceNested.Sha256 == copyDestinationNested.Sha256
                : moveSource.Exists &&
                  moveSourceNested.Exists &&
                  !copyDestination.Exists &&
                  !copyDestinationNested.Exists &&
                  !moveDestination.Exists &&
                  !moveDestinationNested.Exists);

        RequireAotManagedUi(
            result,
            surfaceValid && diskValid,
            step,
            expectMutation
                ? "The real File Widget or owned disk tree did not retain the native-drop mutation."
                : "The real File Widget or owned disk tree did not match the native-drop baseline.");
    }

    private static AotManagedUiNativeDropHighlightEvidence
        MapAotNativeDropHighlightEvidence(
            AotNativeDropHighlightProbe probe,
            AotNativeDropCallbackResult callbacks,
            AotNativeDropHighlightState after,
            int cleanupHResult)
    {
        return new AotManagedUiNativeDropHighlightEvidence
        {
            HighlightActiveBefore = probe.HighlightActiveBeforeNativeCallback,
            FolderVisualStateBefore = probe.FolderVisualStateBeforeNativeCallback,
            OutsideScreenX = probe.OutsideScreenX,
            OutsideScreenY = probe.OutsideScreenY,
            Callback = MapAotNativeDropCallback(callbacks),
            HighlightActiveAfter = after.AnyChildHighlightActive,
            FolderVisualStateAfter = after.FolderVisualState,
            CleanupHResult = cleanupHResult
        };
    }

    private static AotManagedUiNativeDropCallbackEvidence
        MapAotNativeDropCallback(AotNativeDropCallbackResult result)
    {
        return new AotManagedUiNativeDropCallbackEvidence
        {
            TargetRegistered = result.TargetRegistered,
            Paths = result.Paths.ToList(),
            ScreenX = result.ScreenX,
            ScreenY = result.ScreenY,
            KeyState = result.KeyState,
            LeaveWithoutDrop = result.LeaveWithoutDrop,
            StoppedAfterDragOver = result.StoppedAfterDragOver,
            DragEnterHResult = result.DragEnterHResult,
            DragOverHResult = result.DragOverHResult,
            DragLeaveHResult = result.DragLeaveHResult,
            DropHResult = result.DropHResult,
            DragEnterEffect = result.DragEnterEffect,
            DragOverEffect = result.DragOverEffect,
            CompletionEffect = result.CompletionEffect
        };
    }

    private static AotManagedUiNativeDropProgressEvidence
        MapAotNativeDropProgress(AotNativeDropProgressSnapshot snapshot)
    {
        return new AotManagedUiNativeDropProgressEvidence
        {
            IsImportBusy = snapshot.IsImportBusy,
            BusyElapsedMilliseconds = snapshot.BusyElapsedMilliseconds,
            CardVisible = snapshot.CardVisible,
            CardVisibility = snapshot.CardVisibility,
            CanvasZIndex = snapshot.CanvasZIndex,
            TranslationZ = snapshot.TranslationZ,
            BackgroundType = snapshot.BackgroundType,
            BackgroundIsAcrylicBrush = snapshot.BackgroundIsAcrylicBrush,
            ProgressIndeterminate = snapshot.ProgressIndeterminate,
            ProgressValue = snapshot.ProgressValue,
            PercentText = snapshot.PercentText,
            TitleText = snapshot.TitleText,
            DescriptionText = snapshot.DescriptionText
        };
    }
}

internal sealed class AotManagedUiNativeDropEvidence
{
    public string Phase { get; set; } = string.Empty;
    public bool NormalShutdownRequested { get; set; }
    public bool FlushSucceeded { get; set; }
    public string RunId { get; set; } = string.Empty;
    public string WidgetRoot { get; set; } = string.Empty;
    public string SourceRoot { get; set; } = string.Empty;
    public long HostWindowHandle { get; set; }
    public bool HostHasXamlRoot { get; set; }
    public bool HostVisible { get; set; }
    public string SourceKind { get; set; } = string.Empty;
    public bool PhysicalExplorerMouseVerified { get; set; }
    public bool RestoreObservedByWatcher { get; set; }
    public AotManagedUiNativeDropStateEvidence Before { get; set; } = new();
    public AotManagedUiNativeDropStateEvidence After { get; set; } = new();
    public AotManagedUiNativeDropHighlightEvidence NativePointerClear { get; set; } = new();
    public AotManagedUiNativeDropHighlightEvidence NativeLeaveClear { get; set; } = new();
    public AotManagedUiNativeDropImportEvidence CopyImport { get; set; } = new();
    public AotManagedUiNativeDropImportEvidence MoveImport { get; set; } = new();
    public AotManagedUiNativeDropManagedCardEvidence ManagedCardImport { get; set; } = new();
}

internal sealed class AotManagedUiNativeDropStateEvidence
{
    public bool ExpectMutation { get; set; }
    public AotManagedUiLocalFileSurfaceEvidence Surface { get; set; } = new();
    public Dictionary<string, AotManagedUiNativeDropFileEvidence> Files { get; set; } = [];
}

internal sealed class AotManagedUiNativeDropFileEvidence
{
    public bool Exists { get; set; }
    public long Length { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

internal sealed class AotManagedUiNativeDropHighlightEvidence
{
    public bool HighlightActiveBefore { get; set; }
    public string FolderVisualStateBefore { get; set; } = string.Empty;
    public int OutsideScreenX { get; set; }
    public int OutsideScreenY { get; set; }
    public AotManagedUiNativeDropCallbackEvidence Callback { get; set; } = new();
    public bool HighlightActiveAfter { get; set; }
    public string FolderVisualStateAfter { get; set; } = string.Empty;
    public int CleanupHResult { get; set; }
}

internal sealed class AotManagedUiNativeDropImportEvidence
{
    public long CallbackElapsedMilliseconds { get; set; }
    public AotManagedUiNativeDropCallbackEvidence Callback { get; set; } = new();
    public AotManagedUiNativeDropProgressEvidence ImmediatelyAfterCallback { get; set; } = new();
    public AotManagedUiNativeDropProgressEvidence DuringImport { get; set; } = new();
    public AotManagedUiNativeDropProgressEvidence AfterImport { get; set; } = new();
}

internal sealed class AotManagedUiNativeDropManagedCardEvidence
{
    public AotManagedUiNativeDropProgressEvidence CardShown { get; set; } = new();
    public AotManagedUiNativeDropProgressEvidence AfterImport { get; set; } = new();
    public string DestinationFile { get; set; } = string.Empty;
    public bool DestinationRemovedAfterProbe { get; set; }
}

internal sealed class AotManagedUiNativeDropCallbackEvidence
{
    public bool TargetRegistered { get; set; }
    public List<string> Paths { get; set; } = [];
    public int ScreenX { get; set; }
    public int ScreenY { get; set; }
    public uint KeyState { get; set; }
    public bool LeaveWithoutDrop { get; set; }
    public bool StoppedAfterDragOver { get; set; }
    public int DragEnterHResult { get; set; }
    public int DragOverHResult { get; set; }
    public int DragLeaveHResult { get; set; }
    public int DropHResult { get; set; }
    public uint DragEnterEffect { get; set; }
    public uint DragOverEffect { get; set; }
    public uint CompletionEffect { get; set; }
}

internal sealed class AotManagedUiNativeDropProgressEvidence
{
    public bool IsImportBusy { get; set; }
    public long? BusyElapsedMilliseconds { get; set; }
    public bool CardVisible { get; set; }
    public string CardVisibility { get; set; } = string.Empty;
    public int CanvasZIndex { get; set; }
    public float TranslationZ { get; set; }
    public string BackgroundType { get; set; } = string.Empty;
    public bool BackgroundIsAcrylicBrush { get; set; }
    public bool ProgressIndeterminate { get; set; }
    public double ProgressValue { get; set; }
    public string PercentText { get; set; } = string.Empty;
    public string TitleText { get; set; } = string.Empty;
    public string DescriptionText { get; set; } = string.Empty;
}
#endif
