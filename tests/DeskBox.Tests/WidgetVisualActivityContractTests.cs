using DeskBox.Controls;

namespace DeskBox.Tests;

public sealed class WidgetVisualActivityContractTests
{
    [Fact]
    public void CompositionAnimationParameters_PreserveExistingPeriods()
    {
        Assert.Equal(
            1.0 / 0.875,
            WidgetShell.CompactLiveIndeterminateDurationSeconds,
            precision: 8);
        Assert.Equal(
            Math.PI * 2 / 0.8,
            WidgetShell.EdgeGlowPulseDurationSeconds,
            precision: 8);
    }

    [Fact]
    public void MusicTitleMarquee_UsesCompositionWithOriginalDelaySpeedAndGap()
    {
        string source = Read("src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml.cs");

        Assert.Contains("TitleMarqueeGap = 32.0", source, StringComparison.Ordinal);
        Assert.Contains("TitleMarqueeStartDelayMs = 900.0", source, StringComparison.Ordinal);
        Assert.Contains("TitleMarqueeSpeedPixelsPerSecond = 50.0", source, StringComparison.Ordinal);
        Assert.Contains("CreateScalarKeyFrameAnimation()", source, StringComparison.Ordinal);
        Assert.Contains("visual.StartAnimation(\"Translation.X\", animation);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_titleMarqueeTimer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeSpan.FromMilliseconds(33)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void VisibleIdleMaintenance_PreservesInteractiveCachesAndDecoration()
    {
        string app = Read("src/DeskBox/App.xaml.cs");
        string shell = Read("src/DeskBox/Controls/WidgetShell.xaml.cs");
        string manager = Read("src/DeskBox/Services/WidgetManager.cs");
        string visibleIdleMaintenance = ExtractSection(
            app,
            "private void VisibleIdleMemoryMaintenanceTimer_Tick(",
            "private void StopVisibleIdleMemoryMaintenance()");

        Assert.Contains(
            "_visibleIdleMemoryTracker.CommitMaintenance",
            visibleIdleMaintenance,
            StringComparison.Ordinal);
        Assert.Contains(
            "Localized.PruneDeadTargets();",
            visibleIdleMaintenance,
            StringComparison.Ordinal);
        Assert.Contains(
            "interactiveCacheTrim={cacheRelease.ReleasedAnything}",
            visibleIdleMaintenance,
            StringComparison.Ordinal);
        Assert.Contains(
            "warmCachePreserved=true",
            visibleIdleMaintenance,
            StringComparison.Ordinal);
        Assert.Contains(
            "IconHelper.ReleaseIdleCaches(",
            visibleIdleMaintenance,
            StringComparison.Ordinal);
        Assert.DoesNotContain("GC.Collect(", app, StringComparison.Ordinal);
        Assert.DoesNotContain("TryTrimCurrentProcessWorkingSet", app, StringComparison.Ordinal);
        Assert.DoesNotContain("_fileMetaService?.Clear()", app, StringComparison.Ordinal);
        Assert.DoesNotContain("FileService.ClearShellKindCache()", app, StringComparison.Ordinal);
        Assert.DoesNotContain("SuspendVisibleIdleVisualActivity", app, StringComparison.Ordinal);

        string shellGate = ExtractSection(
            shell,
            "internal bool HasActiveVisualWork =>",
            "public bool HasWidgetGroup");
        Assert.DoesNotContain("_isCompactVinylRotating", shellGate, StringComparison.Ordinal);
        Assert.DoesNotContain("_compactMarqueeStoryboard", shellGate, StringComparison.Ordinal);
        Assert.DoesNotContain("ActiveMusicTimerCount", manager, StringComparison.Ordinal);
        Assert.Contains("UpdateCompactVinylRotation", shell, StringComparison.Ordinal);
        Assert.Contains("QueueCompactMarquee", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void HiddenMemoryCleanup_UsesConditionalDeepFinalizerWithoutTrimOrRebuild()
    {
        string app = Read("src/DeskBox/App.xaml.cs");
        string schedule = ExtractSection(
            app,
            "internal static void ScheduleBackgroundMemoryCleanup(",
            "private bool CanRunBackgroundMemoryCleanup()");

        Assert.Contains("RunBackgroundMemoryCleanupScheduleAsync", schedule, StringComparison.Ordinal);
        Assert.Contains("conditional-deep-finalizer", schedule, StringComparison.Ordinal);
        Assert.DoesNotContain("RunBackgroundCacheCleanupScheduleAsync", schedule, StringComparison.Ordinal);
        Assert.DoesNotContain("RunHiddenWorkingSetTrimScheduleAsync", schedule, StringComparison.Ordinal);

        string coordinator = ExtractSection(
            app,
            "private async Task RunBackgroundMemoryCleanupScheduleAsync(",
            "private bool CanArmBackgroundMemoryCleanup(");
        int softCleanupIndex = coordinator.IndexOf(
            "await RunBackgroundSoftMemoryCleanupAsync(",
            StringComparison.Ordinal);
        int maintenanceIndex = coordinator.IndexOf(
            "RunLongHiddenNoRebuildMaintenance()",
            StringComparison.Ordinal);
        int deepReclaimIndex = coordinator.IndexOf(
            "RunBackgroundDeepMemoryCleanupAsync(",
            StringComparison.Ordinal);
        Assert.True(
            softCleanupIndex >= 0 &&
            softCleanupIndex < deepReclaimIndex &&
            deepReclaimIndex < maintenanceIndex,
            "Cache cleanup and finalization must run before long-hidden maintenance.");
        Assert.Contains(
            "GetCappedBackgroundRetryDelay",
            coordinator,
            StringComparison.Ordinal);
        Assert.Contains(
            "Task.Delay(nextDelay.Value, cancellationToken)",
            coordinator,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "for (int retry = 0; retry < 12; retry++)",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain("WorkingSetTrim", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("GC.Collect(", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void WidgetVisibilityTransition_CentrallyArmsAndCancelsHiddenCleanup()
    {
        string manager = Read("src/DeskBox/Services/WidgetManager.cs");
        string app = Read("src/DeskBox/App.xaml.cs");
        string baseWindow = Read("src/DeskBox/Views/WidgetWindowBase.Bounds.cs");
        string surfaces = Read("src/DeskBox/Services/WidgetManager.Surfaces.cs");

        string snapshot = ExtractSection(
            manager,
            "CaptureMemoryCleanupVisibilitySnapshot(",
            "internal void ReconcileBackgroundMemoryCleanupForWidgetVisibility(");
        Assert.Contains("Win32Helper.IsWindowVisible", snapshot, StringComparison.Ordinal);
        Assert.Contains("logicalVisibleCount", snapshot, StringComparison.Ordinal);

        string reconciliation = ExtractSection(
            manager,
            "internal void ReconcileBackgroundMemoryCleanupForWidgetVisibility(",
            "public bool IsWidgetWindow(");
        Assert.Contains("CaptureMemoryCleanupVisibilitySnapshot", reconciliation, StringComparison.Ordinal);
        Assert.Contains("App.CancelBackgroundMemoryCleanup", reconciliation, StringComparison.Ordinal);
        Assert.Contains("App.ScheduleBackgroundMemoryCleanup", reconciliation, StringComparison.Ordinal);

        string canArm = ExtractSection(
            app,
            "private bool CanArmBackgroundMemoryCleanup(",
            "private bool CanRunBackgroundMemoryCleanup()");
        Assert.Contains("CaptureMemoryCleanupVisibilitySnapshot", canArm, StringComparison.Ordinal);
        Assert.DoesNotContain("WidgetManager.HasVisibleWidgets", canArm, StringComparison.Ordinal);

        string activity = ExtractSection(
            app,
            "private MemoryCleanupActivitySnapshot CaptureMemoryCleanupActivity()",
            "private static void CancelBackgroundMemoryCleanupDelay()");
        Assert.Contains("visibility.HasNativeVisibleWidgets", activity, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.IsWindowVisible(foregroundWindow)", activity, StringComparison.Ordinal);
        Assert.Contains(
            "ReconcileBackgroundMemoryCleanupForWidgetVisibility(",
            baseWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface-unregistered",
            surfaces,
            StringComparison.Ordinal);
        Assert.Contains(
            "forceScheduleWhenHidden: true",
            manager,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BackgroundMemoryCleanup_EmitsOneStandardOutcomeFromCoordinatorFinally()
    {
        string app = Read("src/DeskBox/App.xaml.cs");
        string outcomeLogger = ExtractSection(
            app,
            "private static void LogBackgroundMemoryCleanupOutcome(",
            "internal static void ScheduleBackgroundMemoryCleanup(");
        Assert.Contains("[Memory] Background cleanup outcome", outcomeLogger, StringComparison.Ordinal);
        Assert.Contains("workingSetBeforeMB", outcomeLogger, StringComparison.Ordinal);
        Assert.Contains("privateAfterMB", outcomeLogger, StringComparison.Ordinal);
        Assert.Contains("logicalVisibleWidgets", outcomeLogger, StringComparison.Ordinal);
        Assert.Contains("nativeVisibleWidgets", outcomeLogger, StringComparison.Ordinal);
        Assert.Contains("deepReclaimResult={state.DeepReclaimResult}", outcomeLogger, StringComparison.Ordinal);
        Assert.Contains("forcedCollection={state.DeepReclaimExecuted}", outcomeLogger, StringComparison.Ordinal);
        Assert.Contains("workingSetTrimmed=false", outcomeLogger, StringComparison.Ordinal);
        Assert.Contains("fullViewRebuilds=0", outcomeLogger, StringComparison.Ordinal);
        Assert.Contains("shortcutCacheBefore", outcomeLogger, StringComparison.Ordinal);
        Assert.Contains(
            "privateMinusGcHeapBeforeEstimateMB",
            outcomeLogger,
            StringComparison.Ordinal);
        Assert.Contains(
            "privateMinusGcHeapAfterEstimateMB",
            outcomeLogger,
            StringComparison.Ordinal);

        string coordinator = ExtractSection(
            app,
            "private async Task RunBackgroundMemoryCleanupScheduleAsync(",
            "private bool CanArmBackgroundMemoryCleanup(");
        string finallySection = ExtractSection(
            coordinator,
            "finally",
            "cancellationSource.Dispose();");
        Assert.Equal(
            1,
            CountOccurrences(
                finallySection,
                "LogBackgroundMemoryCleanupOutcome("));
    }

    [Fact]
    public void CacheDiagnostics_AggregateLookupsAndLoadLatencyWithoutPerLookupLogs()
    {
        string logger = Read("src/DeskBox/Services/PerformanceLogger.cs");
        string iconHelper = Read("src/DeskBox/Helpers/IconHelper.cs");
        string thumbnailLookup = ExtractSection(
            logger,
            "internal static void RecordThumbnailCacheLookup(bool hit)",
            "internal static void RecordThumbnailCacheLoad(");
        string decodedBitmapLookup = ExtractSection(
            logger,
            "internal static void RecordDecodedBitmapCacheLookup(bool hit)",
            "internal static void RecordDecodedBitmapCacheLoad(");

        Assert.Contains("if (!IsEnabled)", thumbnailLookup, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Increment", thumbnailLookup, StringComparison.Ordinal);
        Assert.DoesNotContain("App.Log", thumbnailLookup, StringComparison.Ordinal);
        Assert.Contains("if (!IsEnabled)", decodedBitmapLookup, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Increment", decodedBitmapLookup, StringComparison.Ordinal);
        Assert.DoesNotContain("App.Log", decodedBitmapLookup, StringComparison.Ordinal);
        Assert.Contains(
            "CreateThumbnailWithDiagnosticsAsync(createThumbnail)",
            iconHelper,
            StringComparison.Ordinal);
        Assert.Contains(
            "LoadBitmapImageWithDiagnosticsAsync(",
            iconHelper,
            StringComparison.Ordinal);
        Assert.Contains("thumbHitRatePercent", logger, StringComparison.Ordinal);
        Assert.Contains("thumbLoadAvgMs", logger, StringComparison.Ordinal);
        Assert.Contains("decodedBitmapHitRatePercent", logger, StringComparison.Ordinal);
        Assert.Contains("decodedBitmapLoadAvgMs", logger, StringComparison.Ordinal);
        Assert.Contains("privateMinusGcHeapEstimateMB", logger, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomaticCleanup_HasNoForcedGcWorkingSetTrimOrHeapCompaction()
    {
        string helper = Read("src/DeskBox/Helpers/Win32Helper.cs");
        string app = Read("src/DeskBox/App.xaml.cs");
        string reclaimer = Read("src/DeskBox/Services/MemoryReclaimer.cs");

        Assert.DoesNotContain("EmptyWorkingSet", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("TryTrimCurrentProcessWorkingSet", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("GC.Collect(", app, StringComparison.Ordinal);
        Assert.DoesNotContain("GC.WaitForPendingFinalizers", app, StringComparison.Ordinal);
        Assert.DoesNotContain("LargeObjectHeapCompactionMode", app, StringComparison.Ordinal);
        Assert.DoesNotContain("HeapSetInformation", app, StringComparison.Ordinal);
        Assert.Contains("GC.Collect(", reclaimer, StringComparison.Ordinal);
        Assert.Contains("GC.WaitForPendingFinalizers", reclaimer, StringComparison.Ordinal);
        Assert.Contains("MinimumCooldownSeconds", reclaimer, StringComparison.Ordinal);
    }

    [Fact]
    public void DelayedCleanup_PrunesDeadTargetsWithoutCreatingAStopTheWorldPause()
    {
        string app = Read("src/DeskBox/App.xaml.cs");
        string delayedCleanup = ExtractSection(
            app,
            "internal static void ScheduleLightMemoryCleanup(",
            "public async Task ShutdownForUpdateAsync()");

        Assert.Contains("bool enqueued = dispatcherQueue?.TryEnqueue", delayedCleanup, StringComparison.Ordinal);
        Assert.Contains("if (!enqueued)", delayedCleanup, StringComparison.Ordinal);
        Assert.Contains("Localized.PruneDeadTargets();", delayedCleanup, StringComparison.Ordinal);
        Assert.Contains("deepReclaimRequested={completedHeavyOperation}", delayedCleanup, StringComparison.Ordinal);
        Assert.Contains("RunHeavyOperationDeepMemoryCleanupAsync", delayedCleanup, StringComparison.Ordinal);
        Assert.Contains(
            "reason=widgets-restored",
            delayedCleanup,
            StringComparison.Ordinal);
        Assert.DoesNotContain("GC.Collect(", delayedCleanup, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run(", delayedCleanup, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsWindow_UnregistersNativeCallbacksBeforeDetachingItsTree()
    {
        string window = Read("src/DeskBox/Views/SettingsWindow.xaml.cs");
        string navigation = Read("src/DeskBox/Views/SettingsWindow.Navigation.cs");
        string app = Read("src/DeskBox/App.xaml.cs");

        Assert.Contains(
            "ClearFeatureSettingsExpanderCallbacks();",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "UnregisterPropertyChangedCallback",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "_settingsSearchResults = Array.Empty<SettingsSearchResult>();",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "SettingsWindow_ClosedForApp",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_settingsWindow.Closed += (_, _)",
            app,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LongHiddenMaintenance_PreservesViewsWatchersAndSearchMaterial()
    {
        string app = Read("src/DeskBox/App.xaml.cs");
        string contentSwitching = Read(
            "src/DeskBox/Views/ContentWidgetWindow.ContentSwitching.cs");
        string fileSurface = Read(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs");
        string fileViewModel = Read("src/DeskBox/ViewModels/WidgetViewModel.cs");
        string searchPopup = Read("src/DeskBox/Views/SearchPopupWindow.xaml.cs");
        string settings = Read("src/DeskBox/Views/SettingsWindow.xaml");
        string iconHelper = Read("src/DeskBox/Helpers/IconHelper.cs");

        string longHidden = ExtractSection(
            contentSwitching,
            "internal void RunLongHiddenNoRebuildMaintenance()",
            "internal int ReleaseLongHiddenContentResources()");
        Assert.Contains("_contentHost.CurrentContent?.OnWindowLongHidden();", longHidden, StringComparison.Ordinal);
        Assert.DoesNotContain("DisposeCachedGroupContents", longHidden, StringComparison.Ordinal);
        string longHiddenRelease = ExtractSection(
            contentSwitching,
            "internal int ReleaseLongHiddenContentResources()",
            "private void DisposeCachedGroupContents()");
        Assert.Contains("DisposeCachedGroupContents();", longHiddenRelease, StringComparison.Ordinal);
        Assert.Contains("if (Visible || IsClosing)", longHiddenRelease, StringComparison.Ordinal);
        Assert.DoesNotContain("OnWindowLongHidden()", fileSurface, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseBackgroundActivityForLongHide", fileViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("ResumeBackgroundActivityAsync", fileViewModel, StringComparison.Ordinal);

        string searchHide = ExtractSection(
            searchPopup,
            "private void OnPopupHideCompleted(",
            "public void TogglePopup()");
        Assert.Contains("_appWindow?.Hide();", searchHide, StringComparison.Ordinal);
        Assert.DoesNotContain("DisposeAcrylicController", searchHide, StringComparison.Ordinal);
        Assert.DoesNotContain("DisposeMicaController", searchHide, StringComparison.Ordinal);
        Assert.DoesNotContain("DisableAccentPolicy", searchHide, StringComparison.Ordinal);

        Assert.DoesNotContain("ScheduleTransientWindowRelease", app, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedTransientWindowReleaseDelaySeconds", settings, StringComparison.Ordinal);

        string idleCacheTrim = ExtractSection(
            iconHelper,
            "internal static IdleIconCacheReleaseResult ReleaseIdleCaches()",
            "internal static IdleIconCacheReleaseResult ReleaseHiddenCaches()");
        Assert.Contains("MaxThumbnailCacheEntries / 2", idleCacheTrim, StringComparison.Ordinal);
        Assert.Contains("MaxDecodedBitmapCacheEntries / 2", idleCacheTrim, StringComparison.Ordinal);
        Assert.Contains("MaxIconCacheEntries / 2", idleCacheTrim, StringComparison.Ordinal);
        Assert.DoesNotContain(".Clear();", idleCacheTrim, StringComparison.Ordinal);
        string hiddenCacheTrim = ExtractSection(
            iconHelper,
            "internal static IdleIconCacheReleaseResult ReleaseHiddenCaches()",
            "internal static string CurrentPerformanceCacheBudget");
        Assert.Contains("IsCompleted", hiddenCacheTrim, StringComparison.Ordinal);
        Assert.Contains("ShellThumbnailProxy.ClearTransientFailures", hiddenCacheTrim, StringComparison.Ordinal);
    }

    [Fact]
    public void CacheEviction_UsesLruAndProtectsRecentOrInflightEntries()
    {
        string iconHelper = Read("src/DeskBox/Helpers/IconHelper.cs");
        string shortcutHelper = Read("src/DeskBox/Helpers/ShortcutHelper.cs");
        string fileMeta = Read("src/DeskBox/Services/FileMetaService.cs");

        string idleCacheTrim = ExtractSection(
            iconHelper,
            "internal static IdleIconCacheReleaseResult ReleaseIdleCaches()",
            "internal static IdleIconCacheReleaseResult ReleaseHiddenCaches()");
        Assert.Contains("IdleCacheMinimumAge", iconHelper, StringComparison.Ordinal);
        Assert.Contains("coldOnly: true", idleCacheTrim, StringComparison.Ordinal);
        Assert.Contains(
            "FindOldestEvictableThumbnailNode",
            idleCacheTrim,
            StringComparison.Ordinal);
        Assert.Contains(
            "FindOldestEvictableDecodedBitmapNode",
            idleCacheTrim,
            StringComparison.Ordinal);
        Assert.Contains("if (!task.IsCompleted)", iconHelper, StringComparison.Ordinal);
        Assert.Contains("s_iconBytesLastAccess", iconHelper, StringComparison.Ordinal);

        Assert.Contains("s_storedMetadataLru", shortcutHelper, StringComparison.Ordinal);
        Assert.Contains(
            "TouchStoredMetadataCacheEntry",
            shortcutHelper,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReleaseHiddenMetadataCache",
            shortcutHelper,
            StringComparison.Ordinal);
        Assert.Contains("s_storedMetadataCache.Clear()", shortcutHelper, StringComparison.Ordinal);

        Assert.Contains("_iconCacheLru", fileMeta, StringComparison.Ordinal);
        Assert.Contains("task.IsCompleted", fileMeta, StringComparison.Ordinal);
        Assert.DoesNotContain("_iconCache.Keys.Take", fileMeta, StringComparison.Ordinal);
        Assert.Contains("ReleaseHiddenCaches", fileMeta, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactLiveAndEdgeGlow_UseCompositionAndKeepReducedMotionStaticPath()
    {
        string source = Read("src/DeskBox/Controls/WidgetShell.xaml.cs");
        string compactLive = ExtractSection(
            source,
            "private void StartCompactLiveIndeterminate(bool isFullBleed)",
            "private DispatcherQueueTimer? _compactLiveBreathingTimer");
        string edgeGlow = ExtractSection(
            source,
            "private ScalarKeyFrameAnimation? _edgeGlowPulseAnimation;",
            "// ── Particles (rain / snow)");

        Assert.Contains("isFullBleed ? 0.22 : 0.30", compactLive, StringComparison.Ordinal);
        Assert.Contains("midpoint: 0.6f, amplitude: 0.2f", compactLive, StringComparison.Ordinal);
        Assert.Contains("!SystemAnimationsEnabled()", compactLive, StringComparison.Ordinal);
        Assert.Contains("CompactLiveProgressTransform.ScaleX = 1", compactLive, StringComparison.Ordinal);
        Assert.Contains("WidgetAnimationTemplate.CompactLiveTranslation", compactLive, StringComparison.Ordinal);
        Assert.Contains("WidgetAnimationTemplate.CompactLiveOpacity", compactLive, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateTimer()", compactLive, StringComparison.Ordinal);

        Assert.Contains("midpoint: 0.48f, amplitude: 0.1f", edgeGlow, StringComparison.Ordinal);
        Assert.Contains("EdgeGlowPulseDurationSeconds", edgeGlow, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateTimer()", edgeGlow, StringComparison.Ordinal);
    }

    [Fact]
    public void WeatherParticles_UseScopedCompositionBatchesInsteadOfUiTicks()
    {
        string source = Read("src/DeskBox/Controls/WidgetShell.xaml.cs");
        string particles = ExtractSection(
            source,
            "// ── Particles (rain / snow)",
            "// ── Bottom glow (music playback)");

        Assert.Contains("for (int i = 0; i < 10; i++)", particles, StringComparison.Ordinal);
        Assert.Contains("CreateVector3KeyFrameAnimation()", particles, StringComparison.Ordinal);
        Assert.Contains("CreateScopedBatch(CompositionBatchTypes.Animation)", particles, StringComparison.Ordinal);
        Assert.Contains("ParticleAnimationBatch_Completed", particles, StringComparison.Ordinal);
        Assert.Contains("!SystemAnimationsEnabled()", particles, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateTimer()", particles, StringComparison.Ordinal);
        Assert.DoesNotContain("ParticleTimer_Tick", particles, StringComparison.Ordinal);
    }

    [Fact]
    public void TodoSegmentedRestore_DetachesRenderingWhenTheViewCannotRender()
    {
        string source = Read(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs");
        string restore = ExtractSection(
            source,
            "private void QueueTodoSegmentedRestore()",
            "private void ViewModel_PropertyChanged");

        Assert.Contains("if (!IsLoaded ||", restore, StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel?.TabBarVisibility != Visibility.Visible",
            restore,
            StringComparison.Ordinal);
        Assert.Contains(
            "ListHeaderArea.Visibility != Visibility.Visible",
            restore,
            StringComparison.Ordinal);
        Assert.Contains("CancelTodoSegmentedRestore();", restore, StringComparison.Ordinal);
        Assert.Contains(
            "CompositionTarget.Rendering -= _todoSegmentedRenderingHandler;",
            restore,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "src/DeskBox/Views/ContentWidgetWindow.TrayAnimations.cs",
        "AppWindow.Hide();")]
    [InlineData(
        "src/DeskBox/Views/QuickCaptureWidgetWindow.xaml.cs",
        "_appWindow.Hide();")]
    public void VisualActivity_SuspendsOnlyAfterNativeWindowHide(
        string relativePath,
        string hideCall)
    {
        string source = Read(relativePath);
        int hideIndex = source.LastIndexOf(hideCall, StringComparison.Ordinal);
        int suspendIndex = source.IndexOf(
            "WidgetShellControl.SuspendVisualActivity();",
            hideIndex,
            StringComparison.Ordinal);

        Assert.True(hideIndex >= 0 && hideIndex < suspendIndex);
    }

    [Fact]
    public void QuickCaptureShow_ResumesVisualsBeforeRemovingCloak()
    {
        string source = Read("src/DeskBox/Views/QuickCaptureWidgetWindow.xaml.cs");
        string show = ExtractSection(
            source,
            "public void ShowPreparedRaisedFromTray(bool persistVisibility = true)",
            "public void EnsureRaisedFromTrayTopMost()");

        int resumeIndex = show.IndexOf(
            "NotifyCompactHostVisibilityChanged(true);",
            StringComparison.Ordinal);
        int revealIndex = show.IndexOf(
            "_trayAnimation.RevealWindowForTrayShow();",
            StringComparison.Ordinal);
        Assert.True(resumeIndex >= 0 && resumeIndex < revealIndex);
    }

    [Theory]
    [InlineData(
        "src/DeskBox/Views/ContentWidgetWindow.WindowInteraction.cs",
        "generation != _contentVisibilityGeneration")]
    [InlineData(
        "src/DeskBox/Views/QuickCaptureWidgetWindow.xaml.cs",
        "generation != _visibleContentResumeGeneration")]
    public void RevealCompletedBackgroundWork_IsDelayedAndGenerationCancelled(
        string relativePath,
        string generationGuard)
    {
        string source = Read(relativePath);

        Assert.Contains(
            "Task.Delay(RevealCompletedBackgroundDelayMs)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(generationGuard, source, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));

    private static int CountOccurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    private static string ExtractSection(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }
}
