using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Feedback 327: display-topology restore, the deferred startup bounds pass,
/// and tray reveal re-resolve every window independently, which can land two
/// capsule bar members on the same slot with no later event to heal the
/// overlap (diagnostics showed members #9/#11 fully coincident until the user
/// dragged any capsule). The consistency check must (a) coalesce bursts into
/// one forced bar pass, (b) only ever constrain bar members — free capsules
/// are user placements — and (c) be wired behind each restore/reveal
/// completion point.
/// </summary>
public sealed class WidgetManagerCapsuleArrangementConsistencyTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    private sealed record Harness(SettingsService Settings, WidgetManager Manager);

    private Harness Create()
    {
        string desktopPath = Directory.CreateDirectory(
            Path.Combine(_root, "desktop")).FullName;
        var settingsService = new SettingsService(Path.Combine(_root, "settings"));
        var fileService = new FileService();
        var manager = new WidgetManager(
            settingsService,
            fileService,
            TestOrganizerServices.Create(settingsService, fileService),
            new ThemeService(settingsService),
            new QuickCaptureService(
                new QuickCaptureStore(Path.Combine(_root, "quick-capture"))),
            () => desktopPath,
            recycleManagedFolderDeletes: false);
        return new Harness(settingsService, manager);
    }

    [Fact]
    public async Task ConsistencyCheck_CoalescesBurstsIntoASingleBarPass()
    {
        Harness harness = Create();
        harness.Settings.Settings.WidgetCapsuleArrangementMode =
            SettingsService.WidgetCapsuleArrangementBar;

        // A topology restore burst: the restore, its verification re-run, and
        // a stray reveal completion all land within the debounce window.
        harness.Manager.ScheduleCapsuleArrangementConsistencyCheck("display-topology-restored");
        harness.Manager.ScheduleCapsuleArrangementConsistencyCheck("display-topology-restored");
        harness.Manager.ScheduleCapsuleArrangementConsistencyCheck("tray-reveal-completed");
        Assert.Equal(0, harness.Manager.CapsuleArrangementConsistencyRunCount);

        await Task.Delay(WidgetManager.CapsuleArrangementConsistencyDelay + TimeSpan.FromMilliseconds(1500));

        Assert.Equal(1, harness.Manager.CapsuleArrangementConsistencyRunCount);

        // A later completion still gets its own pass.
        harness.Manager.ScheduleCapsuleArrangementConsistencyCheck("tray-reveal-completed");
        await Task.Delay(WidgetManager.CapsuleArrangementConsistencyDelay + TimeSpan.FromMilliseconds(1500));
        Assert.Equal(2, harness.Manager.CapsuleArrangementConsistencyRunCount);
    }

    [Fact]
    public async Task ConsistencyCheck_LeavesFreeArrangementModeUntouched()
    {
        Harness harness = Create();
        harness.Settings.Settings.WidgetCapsuleArrangementMode =
            SettingsService.WidgetCapsuleArrangementFree;

        harness.Manager.ScheduleCapsuleArrangementConsistencyCheck("display-topology-restored");
        await Task.Delay(WidgetManager.CapsuleArrangementConsistencyDelay + TimeSpan.FromMilliseconds(1500));

        // Free capsules are user placements: no forced arrangement may run,
        // even when free-placement backups linger in settings.
        Assert.Equal(0, harness.Manager.CapsuleArrangementConsistencyRunCount);
    }

    [Fact]
    public void ConsistencyCheck_IsScheduledAfterEveryRestoreCompletionPoint()
    {
        string managerSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.cs"));

        string topologyRestore = ExtractSection(
            managerSource,
            "public async Task<bool> RestoreWidgetPositionsAsync(",
            "private static bool HasUsableWorkArea()");
        Assert.Contains(
            "ScheduleCapsuleArrangementConsistencyCheck(\"display-topology-restored\");",
            topologyRestore,
            StringComparison.Ordinal);
        // The check must run after the per-window restore loop itself, not
        // before it.
        int lastRestoreIndex = topologyRestore.LastIndexOf(
            "window.EndDisplayTopologyTransition(generation);",
            StringComparison.Ordinal);
        int scheduleIndex = topologyRestore.IndexOf(
            "ScheduleCapsuleArrangementConsistencyCheck(",
            StringComparison.Ordinal);
        Assert.True(lastRestoreIndex >= 0 && scheduleIndex > lastRestoreIndex);

        string deferredStartup = ExtractSection(
            managerSource,
            "private void QueueDeferredStartupWidgetBoundsReconciliation()",
            "private void QueueVisibleGroupedFileIconRecoveryAfterStartup()");
        Assert.Contains(
            "ScheduleCapsuleArrangementConsistencyCheck(\"startup-bounds-reconciled\");",
            deferredStartup,
            StringComparison.Ordinal);
        Assert.Contains("RestoreLoadedWidgetBoundsAfterStartup();", deferredStartup, StringComparison.Ordinal);

        string reveal = ExtractSection(
            managerSource,
            "private async Task SetAllWidgetsVisibleCoreAsync(bool visible)",
            "private static bool HasUsableWorkArea()");
        Assert.Contains(
            "ScheduleCapsuleArrangementConsistencyCheck(\"tray-reveal-completed\");",
            reveal,
            StringComparison.Ordinal);
        Assert.Contains("await _trayBatchAnimationDriver.WaitForIdleAsync();", reveal, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsistencyCheck_IsBarModeGatedBeforeForcingTheArrangement()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.CapsuleArrangement.cs"));
        string method = ExtractSection(
            source,
            "private void ApplyCapsuleArrangementConsistencyCheck(",
            "private void ApplyCapsuleArrangementIfChanged(");

        Assert.Contains(
            "ResolveEffectiveCapsuleArrangementMode() != SettingsService.WidgetCapsuleArrangementBar",
            method,
            StringComparison.Ordinal);
        Assert.Contains("ApplyCapsuleArrangementIfChanged(force: true);", method, StringComparison.Ordinal);
        // Debounce: only the newest request's pass survives.
        Assert.Contains("ticket != _capsuleArrangementConsistencyTicket", method, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
    }

    private static string ExtractSection(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }
}
