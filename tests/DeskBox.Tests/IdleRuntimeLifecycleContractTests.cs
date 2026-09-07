namespace DeskBox.Tests;

public sealed class IdleRuntimeLifecycleContractTests
{
    [Fact]
    public void UiWatchdog_IsOptInForNormalRuntime()
    {
        string source = Read("src/DeskBox/Services/AppDiagnosticsService.cs");
        string startAll = Slice(
            source,
            "public void StartAll()",
            "public int LifecycleEventCount");

        Assert.Contains("ScheduleMemoryDiagnostics();", startAll, StringComparison.Ordinal);
        Assert.Contains(
            "PerformanceLogger.IsEnabled || App.IsVerboseLoggingEnabled",
            startAll,
            StringComparison.Ordinal);
        Assert.Contains("StartUiThreadWatchdog();", startAll, StringComparison.Ordinal);
        Assert.Contains("[Watchdog] Disabled", startAll, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryMeasurement_CapturesWindowInventoryForGpuSurfaceAudits()
    {
        string script = Read("scripts/measure-deskbox-memory.ps1");

        Assert.Contains("SchemaVersion = 2", script, StringComparison.Ordinal);
        Assert.Contains("CaptureTopLevelWindows", script, StringComparison.Ordinal);
        Assert.Contains("WindowInventory = @(", script, StringComparison.Ordinal);
        Assert.Contains("ClassName", script, StringComparison.Ordinal);
        Assert.Contains("OwnerHandle", script, StringComparison.Ordinal);
        Assert.Contains("IsVisible", script, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalClipboardAndReminderServices_ExistOnlyWhileTheirFeaturesAreActive()
    {
        string app = Read("src/DeskBox/App.xaml.cs");
        string launch = Slice(
            app,
            "protected override async void OnLaunched(LaunchActivatedEventArgs args)",
            "private void OnDisplaysChanged()");
        string clipboard = Slice(
            app,
            "internal void RefreshQuickCaptureClipboardService(bool captureCurrent = false)",
            "internal TodoReminderService? RefreshTodoReminderService");
        string reminder = Slice(
            app,
            "internal TodoReminderService? RefreshTodoReminderService(bool checkNow = false)",
            "private void StartTodoReminderService()");
        string manager = Read("src/DeskBox/Services/WidgetManager.FeatureWidgets.cs");
        // Stage 3b wiring: the manager raises the feature-state event; the
        // App-side subscriber owns the service refreshes (same behavior as
        // the old App.Current switch).
        string stateHandler = Slice(
            app,
            "private void OnFeatureStateChanged(FeatureStateChangedEventArgs e)",
            "internal void SetSearchFeatureEnabled");

        Assert.Contains("RefreshQuickCaptureClipboardService();", launch, StringComparison.Ordinal);
        Assert.Contains("RefreshTodoReminderService();", launch, StringComparison.Ordinal);
        Assert.DoesNotContain("new QuickCaptureClipboardService", launch, StringComparison.Ordinal);
        Assert.DoesNotContain("StartTodoReminderService();", launch, StringComparison.Ordinal);

        Assert.Contains("settings.QuickCaptureClipboardEnabled", clipboard, StringComparison.Ordinal);
        Assert.Contains("FeatureWidgetSettings.IsEnabled(settings, WidgetKind.QuickCapture)", clipboard, StringComparison.Ordinal);
        Assert.Contains("QuickCaptureClipboardService.Dispose();", clipboard, StringComparison.Ordinal);
        Assert.Contains("QuickCaptureClipboardService = null;", clipboard, StringComparison.Ordinal);

        Assert.Contains("settings.TodoReminderEnabled", reminder, StringComparison.Ordinal);
        Assert.Contains("FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Todo)", reminder, StringComparison.Ordinal);
        Assert.Contains("_todoReminderService.Dispose();", reminder, StringComparison.Ordinal);
        Assert.Contains("_todoReminderService = null;", reminder, StringComparison.Ordinal);

        Assert.Contains("RaiseFeatureStateChanged(featureId, enabled);", manager, StringComparison.Ordinal);
        Assert.Contains("SetSearchFeatureEnabled(e.Enabled);", stateHandler, StringComparison.Ordinal);
        Assert.Contains("RefreshQuickCaptureClipboardService();", stateHandler, StringComparison.Ordinal);
        Assert.Contains("RefreshTodoReminderService();", stateHandler, StringComparison.Ordinal);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker not found: {endMarker}");
        return source[start..end];
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.SourceFile(relativePath));
}
