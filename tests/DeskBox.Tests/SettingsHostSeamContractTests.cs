namespace DeskBox.Tests;


/// <summary>
/// Ratchet for the settings-process seam (1.6.0 batch 2): the settings UI
/// tree (window shell, view models, sections) must reach host services only
/// through the ISettings*Actions facades. App.Current grabs of process-bound
/// services would crash the second-process settings host at runtime — this
/// test keeps them from creeping back. Directly allowed by design:
/// App.Current.SettingsService / App.Current.LocalizationService (each
/// process owns instances) and pure statics on GlobalHotkeyService
/// (normalization/formatting/preset gestures).
/// </summary>
public sealed class SettingsHostSeamContractTests
{
    private static readonly string[] BannedPatterns =
    [
        "App.Current.WidgetManager",
        "App.Current?.WidgetManager",
        "App.Current.GlobalHotkeyService",
        "App.Current?.GlobalHotkeyService",
        "App.Current.SearchHotkeyService",
        "App.Current?.SearchHotkeyService",
        "App.Current.DesktopDoubleClickActivationService",
        "App.Current.DataBackupService",
        "App.Current.AttachmentHealthService",
        "App.Current.QuickCaptureService",
        "App.Current?.QuickCaptureService",
        "App.Current.OrganizerService",
        "App.Current.ManagedStorageDesktopShortcutService",
        "App.Current.DiagnosticsService",
        "App.Current.EverythingSearchService",
        "App.Current.ResizeGuideOverlay",
        "App.Current.SettingsWindowInstance",
        "App.Current.ShutdownForRestartAsync",
        "App.Current.ShutdownForUpdateAsync",
        "App.Current.ShowOnboarding",
        "App.Current.ShowDesktopOrganizationWindow",
        "App.Current.OpenAppReleaseNotesWindow",
        "App.Current.UpdateTrayIcon",
        "App.Current.AppUpdateService",
        "App.Current.CreateDiagnosticSnapshot",
        "App.Current.DiagnosticsBundleService",
        "App.Current.ForceExternalStateRecoveryAsync",
        "DragDropPermissionService.Diagnose",
        "DragDropPermissionService.Repair",
        "DragDropPermissionService.TryRelaunchAsExplorerUser",
        "AppRelaunchService.ScheduleAfterCurrentProcessExit",
        "App.ScheduleLightMemoryCleanup",
        "App.ScheduleBackgroundMemoryCleanup",
    ];

    [Fact]
    public void SettingsTree_ReachesHostServicesOnlyThroughFacades()
    {
        string root = FindRepositoryRoot();
        var settingsFiles = Directory.EnumerateFiles(
                Path.Combine(root, "src/DeskBox/Views"),
                "SettingsWindow*.cs",
                SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Contains("AotSmoke", StringComparison.Ordinal) &&
                           !Path.GetFileName(path).Contains("AotDeepSmoke", StringComparison.Ordinal))
            .Concat(Directory.EnumerateFiles(
                Path.Combine(root, "src/DeskBox/Views/SettingsSections"),
                "*.cs",
                SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(
                Path.Combine(root, "src/DeskBox/ViewModels"),
                "SettingsViewModel*.cs",
                SearchOption.AllDirectories))
            .Where(path => !Path.GetFileName(path).Contains("AotBindableProperties", StringComparison.Ordinal));

        var violations = new List<string>();
        foreach (string path in settingsFiles)
        {
            string source = File.ReadAllText(path);
            foreach (string pattern in BannedPatterns)
            {
                if (source.Contains(pattern, StringComparison.Ordinal))
                {
                    violations.Add($"{Path.GetRelativePath(root, path)}: {pattern}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "The settings tree must reach host services through the " +
            "ISettings*Actions facades (a settings host running in its own " +
            "process has none of these App members):\n" +
            string.Join('\n', violations));
    }

    [Fact]
    public void FacadeImplementations_StayInProcessNeutralServices()
    {
        string root = FindRepositoryRoot();
        foreach (string facade in new[]
                 {
                     "InProcessSettingsHotkeyActions",
                     "InProcessSettingsWidgetActions",
                     "InProcessSettingsRestoreActions",
                     "InProcessSettingsAppActions",
                     "InProcessSettingsDataActions",
                 })
        {
            Assert.True(
                File.Exists(Path.Combine(root, $"src/DeskBox/Services/{facade}.cs")),
                $"Missing in-process facade implementation: {facade}");
        }

        // The App facade must hold the App instance it was given, never a
        // global lookup — the services ratchet (LegacyBusinessGlobalAccess)
        // does not scan for bare App references, so pin it here.
        string appFacade = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Services/InProcessSettingsAppActions.cs"));
        Assert.DoesNotContain("App.Current", appFacade, StringComparison.Ordinal);
        Assert.Contains("private readonly App _app", appFacade, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        string directory = AppContext.BaseDirectory;
        while (directory is not null &&
               !File.Exists(Path.Combine(directory, "DeskBox.sln")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.False(directory is null, "Repository root (DeskBox.sln) not found.");
        return directory;
    }
}
