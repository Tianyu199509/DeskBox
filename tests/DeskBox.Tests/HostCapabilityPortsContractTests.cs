namespace DeskBox.Tests;

/// <summary>
/// Pins the stage 3a host-internal capability ports (pluginization roadmap
/// stage 3, src/DeskBox/Contracts) after the fourth review round's semantic
/// corrections. Source-scan style on purpose: the ports have no callers
/// until the stage 3b wiring lands, so behavioral tests are impossible -
/// what matters is that the contract semantics (result shape, naming,
/// boundary statement) do not silently drift before the wiring.
/// </summary>
public sealed class HostCapabilityPortsContractTests
{
    private static string ContractsRoot => TestPaths.FromRepository(
        "src/DeskBox/Contracts");

    private static string ReadPort(string fileName) => File.ReadAllText(
        Path.Combine(ContractsRoot, fileName));

    [Fact]
    public void TodoPresenter_KeepsTargetPresentedAsTheSuccessSignal()
    {
        string source = ReadPort("ITodoReminderPresenter.cs");

        // The adapter must keep the end-to-end success signal (window
        // visible + committed surface + item revealed) separate from "the
        // reveal call was made" - the AOT-era work distinguished those
        // deliberately, and the original draft collapsed them.
        Assert.Contains("bool ItemPresented", source, StringComparison.Ordinal);
        Assert.Contains("bool TargetPresented", source, StringComparison.Ordinal);

        // Current host behavior creates a widget when none matches; the
        // earlier "returns null when no matching live widget exists" doc
        // had drifted from it and must not come back.
        Assert.Contains("creating a Todo widget when no", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Returns null when no matching live widget exists",
            source,
            StringComparison.Ordinal);

        Assert.Contains(
            "Host-internal port, NOT the future public extension capability API",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FileImportTarget_IsRenamedWithCancellationAndFolderSemantics()
    {
        Assert.True(
            File.Exists(Path.Combine(ContractsRoot, "IFileWidgetImportTarget.cs")),
            "IFileWidgetImportTarget.cs is missing.");
        Assert.False(
            File.Exists(Path.Combine(ContractsRoot, "IFileDropTarget.cs")),
            "The old drag-and-drop-flavored name must not come back.");

        string source = ReadPort("IFileWidgetImportTarget.cs");

        // It is an import/sink surface, not drag-and-drop; cancellation is
        // cheapest to add while the port still has zero callers.
        Assert.Contains("CancellationToken cancellationToken = default", source, StringComparison.Ordinal);
        Assert.Contains("writable", source, StringComparison.Ordinal);
        Assert.Contains("host-managed", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FeatureStateEvents_IsRenamedAndCarriesStableFeatureIds()
    {
        Assert.True(
            File.Exists(Path.Combine(ContractsRoot, "IFeatureStateEvents.cs")),
            "IFeatureStateEvents.cs is missing.");
        Assert.False(
            File.Exists(Path.Combine(ContractsRoot, "IFeatureLifecycleEvents.cs")),
            "The lifecycle-flavored name collides with the three pinned lifecycles.");

        string source = ReadPort("IFeatureStateEvents.cs");

        // Event contract the 3d hub must implement (threading, subscriber
        // isolation, unsubscribe ownership) stays pinned in the port docs.
        Assert.Contains("UI thread", source, StringComparison.Ordinal);
        Assert.Contains("subscriber", source, StringComparison.Ordinal);

        // The enum-to-stable-id bridge exists with the six built-in ids.
        Assert.Contains("readonly record struct FeatureId", source, StringComparison.Ordinal);
        foreach (string value in new[]
                 {
                     "todo", "search", "quick-capture", "music", "weather", "glance"
                 })
        {
            Assert.Contains($"new(\"{value}\")", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Contracts_Readme_PinsTheBoundaryAndWiringOrder()
    {
        string readme = File.ReadAllText(Path.Combine(ContractsRoot, "README.md"));

        Assert.Contains("不是未来的插件公开能力 API", readme, StringComparison.Ordinal);
        Assert.Contains("先接线", readme, StringComparison.Ordinal);
        Assert.Contains("零变化", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage3B_CallersDependOnPortsNotManagerInternals()
    {
        string app = File.ReadAllText(TestPaths.SourceFile("src/DeskBox/App.xaml.cs"));
        string menus = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox/Views/QuickCaptureWidgetWindow.Menus.cs"));
        string featureWidgets = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox/Services/WidgetManager.FeatureWidgets.cs"));
        string manager = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox/Services/WidgetManager.cs"));
        string quickCaptureService = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox/Services/QuickCaptureService.cs"));

        // Cut 1 - Todo presenter: App goes through the port; the rich
        // HWND/Visible/XamlRoot diagnostics log lives in the implementation.
        Assert.DoesNotContain("ShowTodoReminderTargetAsync", app, StringComparison.Ordinal);
        Assert.Contains("TodoReminderPresenter", app, StringComparison.Ordinal);
        Assert.Contains("PresentReminderTargetAsync", app, StringComparison.Ordinal);
        Assert.Contains("[Notification] Todo target presentation", featureWidgets, StringComparison.Ordinal);

        // Cut 2 - file import: the import surface goes through the port (the
        // menus keep other, unrelated manager uses). The pre-wiring manager
        // method names must not come back; the producer-side item
        // translation lives in QuickCaptureService, not in widget-manager
        // internals.
        Assert.DoesNotContain("GetQuickCaptureFileWidgetTargets", menus, StringComparison.Ordinal);
        Assert.DoesNotContain("GetLastQuickCaptureFileWidgetTarget", menus, StringComparison.Ordinal);
        Assert.Contains("App.Current.FileWidgetImport", menus, StringComparison.Ordinal);
        Assert.Contains("TryImportFileAsync", menus, StringComparison.Ordinal);
        Assert.Contains("TryImportTextAsync", menus, StringComparison.Ordinal);
        Assert.Contains("BuildFileImportPlan", menus, StringComparison.Ordinal);
        Assert.Contains("BuildQuickCaptureContentFileName", quickCaptureService, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildQuickCaptureContentFileName", featureWidgets, StringComparison.Ordinal);
        Assert.Contains("TryResolveImportTarget", featureWidgets, StringComparison.Ordinal);

        // Cut 3 - feature state events: the App.Current service switch is
        // gone from the manager; the App subscribes through the port event
        // and owns its three service refreshes.
        Assert.DoesNotContain("App.Current.SetSearchFeatureEnabled", featureWidgets, StringComparison.Ordinal);
        Assert.DoesNotContain("App.Current.RefreshQuickCaptureClipboardService", featureWidgets, StringComparison.Ordinal);
        Assert.DoesNotContain("App.Current.RefreshTodoReminderService", featureWidgets, StringComparison.Ordinal);
        Assert.Contains("RaiseFeatureStateChanged", featureWidgets, StringComparison.Ordinal);
        Assert.Contains("FeatureStateChanged += OnFeatureStateChanged;", app, StringComparison.Ordinal);
        Assert.Contains("private void OnFeatureStateChanged(FeatureStateChangedEventArgs e)", app, StringComparison.Ordinal);

        // The manager declares all three ports; the import-target record is
        // port vocabulary now.
        Assert.Contains("ITodoReminderPresenter,", manager, StringComparison.Ordinal);
        Assert.Contains("IFileWidgetImportTarget,", manager, StringComparison.Ordinal);
        Assert.Contains("IFeatureStateEvents", manager, StringComparison.Ordinal);
        Assert.DoesNotContain("QuickCaptureFileWidgetTarget", manager, StringComparison.Ordinal);
    }
}
