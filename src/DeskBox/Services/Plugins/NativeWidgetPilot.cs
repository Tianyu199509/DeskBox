using DeskBox.Contracts;
using DeskBox.Models;
using Microsoft.UI.Xaml;

namespace DeskBox.Services.Plugins;

/// <summary>
/// Development pilot: widget content served by a native package. The
/// INSTALLED path is tried first regardless of any environment variable and
/// serves both production records and dev-installed records (isDevelopment)
/// gated by DESKBOX_ALLOW_UNTRUSTED_NATIVE_DEV=1, with the full B1
/// verification + identity-binding chain. DESKBOX_DEV_NATIVE_GLANCE only
/// controls the dev-time install bootstrap; the raw-directory fallback for
/// spike iteration exists exclusively in pilot builds (#if) so Release has
/// no raw-load entry at all.
/// </summary>
internal static class NativeWidgetPilot
{
    private const string TargetPackageId = "deskbox.glance";

    private static PluginPackageManager? _manager;

    /// <summary>
    /// Production manager: empty trusted-publisher set until batch E provisions
    /// the official key. The dev spike publisher is NEVER trusted here.
    /// </summary>
    private static PluginPackageManager Manager => _manager ??= new PluginPackageManager(
        Path.Combine(DeskBoxDataPathService.Current.DataDirectory, "plugins"));

    /// <summary>
    /// Dev manager: trusts the public spike publisher. Exists only when
    /// EnableDeskBoxNativeDevPilot=true compiles this type; Release builds
    /// have no dev trust root at all.
    /// </summary>
#if DESKBOX_NATIVE_DEV_PILOT
    private const string DevPublisherFingerprint = "1bc4f2db8438d2fd296bd48074088ccc726c265712125abbae975063ba719ea4";
    private static PluginPackageManager? _devManager;
    private static PluginPackageManager DevManager => _devManager ??= new PluginPackageManager(
        Path.Combine(DeskBoxDataPathService.Current.DataDirectory, "plugins"),
        [DevPublisherFingerprint]);
#endif

    /// <summary>
    /// Called once from App startup to install the dev package through the B1
    /// pipeline when DESKBOX_DEV_NATIVE_GLANCE points at a package directory.
    /// Compiled only when EnableDeskBoxNativeDevPilot=true; Release builds
    /// have no dev-install bootstrap at all.
    /// </summary>
    [System.Diagnostics.Conditional("DESKBOX_NATIVE_DEV_PILOT")]
    internal static void RunDevBootstrap()
    {
#if DESKBOX_NATIVE_DEV_PILOT
        string? packageRoot = NativeWidgetPackageLoader.TryGetDevelopmentPackageRoot();
        if (packageRoot is null) return;
        try
        {
            // Development policy (audit round 17): the record must be marked
            // isDevelopment so activation goes through the installed path's
            // DESKBOX_ALLOW_UNTRUSTED_NATIVE_DEV gate. A Store-policy install
            // would leave the record untrusted-but-not-dev, which the empty
            // production trust set always rejects - the dev smoke chain would
            // silently degrade to the raw DLL fallback.
            PluginInstallResult result = DevManager.Install(
                packageRoot, PluginPackageVerificationPolicy.Development);
            App.Log(result.Succeeded
                ? $"[NativePackage] dev package installed: {result.Package!.PackageId} v{result.Package.Version} -> {result.InstallDirectory}"
                : $"[NativePackage] dev package install failed: {string.Join("; ", result.Failures)}");
        }
        catch (Exception error)
        {
            App.Log($"[NativePackage] dev package install error: {error.Message}");
        }
#endif
    }

    public static bool TryCreate(WidgetConfig config, out IWidgetContent? content)
    {
        content = null;

        // 1. Installed path (no env-var dependency): B1 handle → runtime manager.
        //    Dev-installed records (isDevelopment) activate here too when
        //    DESKBOX_ALLOW_UNTRUSTED_NATIVE_DEV=1 - still behind the full B1
        //    verification + identity-binding chain.
        NativeInstalledPackageHandle? handle = Manager.TryCreateNativeHandle(TargetPackageId);
        if (handle is not null &&
            NativeWidgetRuntimeManager.TryCreateFromInstalled(
                handle!, "glance", config.Id,
                DeskBoxDataPathService.Current.DataDirectory,
                out NativeWidgetLease? lease))
        {
            content = new NativeWidgetPilotContent(config, lease!);
            return true;
        }

#if DESKBOX_NATIVE_DEV_PILOT
        // 2. Dev fallback (env-var gated): raw directory, no B1 pipeline.
        //    Pilot builds only - Release compiles this path out entirely so no
        //    env var can bypass manifest/signature/install verification
        //    (audit round 17).
        string? packageRoot = NativeWidgetPackageLoader.TryGetDevelopmentPackageRoot();
        if (packageRoot is null) return false;
        if (NativeWidgetRuntimeManager.TryCreateInstance(
                NativeWidgetPackageLoader.CreateDevelopmentDescriptor(packageRoot),
                contributionId: "main",
                instanceId: config.Id,
                dataDirectory: DeskBoxDataPathService.Current.DataDirectory,
                out NativeWidgetLease? devLease))
        {
            content = new NativeWidgetPilotContent(config, devLease!);
            return true;
        }
#endif
        return false;
    }
}

internal sealed class NativeWidgetPilotContent :
    IWidgetContent,
    IWidgetResponsiveLayoutContent,
    IWidgetHostViewportContent,
    IWidgetPerformanceAwareContent,
    IWidgetInteractiveResizeContent,
    IDisposable
{
    private readonly NativeWidgetLease _lease;

    internal NativeWidgetPilotContent(WidgetConfig config, NativeWidgetLease lease)
    {
        Config = config;
        _lease = lease;
    }

    public WidgetConfig Config { get; }
    public string WidgetId => Config.Id;
    public WidgetKind WidgetKind => Config.WidgetKind;
    public FrameworkElement View => _lease.View;

    public Task InitializeAsync()
    {
        // create_widget already starts the package's initial lifecycle;
        // InitializeAsync must NOT send RefreshRequested (audit round 16: conflating
        // initialization with refresh causes double-fetch in Weather/Music).
        return Task.CompletedTask;
    }

    public Task RefreshAsync()
    {
        _lease.InvokeWidgetEvent(WidgetLifecycleEventKind.RefreshRequested, 0, 0, 0);
        return Task.CompletedTask;
    }

    public void ApplyAppearance() =>
        _lease.InvokeWidgetEvent(WidgetLifecycleEventKind.AppearanceChanged, 0, 0, 0);

    public void OnActivated() =>
        _lease.InvokeWidgetEvent(WidgetLifecycleEventKind.Activated, 0, 0, 0);

    public void OnDeactivated() =>
        _lease.InvokeWidgetEvent(WidgetLifecycleEventKind.Deactivated, 0, 0, 0);

    public void OnWindowVisibilityChanged(bool visible) =>
        _lease.InvokeWidgetEvent(WidgetLifecycleEventKind.VisibilityChanged, 0, 0, visible ? 1u : 0u);

    public void OnWindowRevealCompleted() =>
        _lease.InvokeWidgetEvent(WidgetLifecycleEventKind.RevealCompleted, 0, 0, 0);

    public void OnWindowLongHidden() =>
        _lease.InvokeWidgetEvent(WidgetLifecycleEventKind.LongHidden, 0, 0, 0);

    public void OnCompactStateChanged(bool collapsed) =>
        _lease.InvokeWidgetEvent(WidgetLifecycleEventKind.CompactStateChanged, 0, 0, collapsed ? 1u : 0u);

    public void BeginResponsiveLayoutTransition(double targetContentWidth, double targetContentHeight, bool isCollapsing) =>
        _lease.InvokeWidgetEvent(WidgetLifecycleEventKind.ResponsiveLayoutBegin, targetContentWidth, targetContentHeight, isCollapsing ? 1u : 0u);

    public void CompleteResponsiveLayoutTransition(double finalContentWidth, double finalContentHeight) =>
        _lease.InvokeWidgetEvent(WidgetLifecycleEventKind.ResponsiveLayoutComplete, finalContentWidth, finalContentHeight, 0);

    public void CancelResponsiveLayoutTransition() =>
        _lease.InvokeWidgetEvent(WidgetLifecycleEventKind.ResponsiveLayoutCancel, 0, 0, 0);

    public void OnHostViewportSizeChanged(double width, double height) =>
        _lease.InvokeWidgetEvent(WidgetLifecycleEventKind.ViewportChanged, width, height, 0);

    public void ApplyPerformanceSettings() =>
        _lease.InvokeWidgetEvent(WidgetLifecycleEventKind.PerformanceSettingsChanged, 0, 0, 0);

    public void BeginInteractiveResize(double contentWidth, double contentHeight) =>
        _lease.InvokeWidgetEvent(WidgetLifecycleEventKind.InteractiveResizeBegin, contentWidth, contentHeight, 0);

    public void CompleteInteractiveResize(double contentWidth, double contentHeight) =>
        _lease.InvokeWidgetEvent(WidgetLifecycleEventKind.InteractiveResizeEnd, contentWidth, contentHeight, 0);

    public void Dispose() => ((IDisposable)_lease).Dispose();
}
