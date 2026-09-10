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
        // Binding-driven (audit round 20 §18): the pilot is generic — which
        // package id, contribution, and migration adapter serve this widget
        // kind comes from the registry, so the second package is a
        // registration rather than a new branch here.
        OfficialPackageBinding? binding = PackageBindingRegistry.TryGetByKind(config.WidgetKind);
        if (binding is null) return false;

        // 1. Installed path (no env-var dependency): B1 handle → runtime manager.
        //    Dev-installed records (isDevelopment) activate here too when
        //    DESKBOX_ALLOW_UNTRUSTED_NATIVE_DEV=1 - still behind the full B1
        //    verification + identity-binding chain.
        NativeInstalledPackageHandle? handle = Manager.TryCreateNativeHandle(binding.PackageId);
        if (handle is not null)
        {
            // Legacy data handoff (D3): the feature adapter resolves and
            // validates the authoritative bytes; they are synced into the
            // package's instance data root before the first native create.
            NativeWidgetDataMigration.TrySync(
                binding.Migration,
                handle.Record.PublisherFingerprint, handle.Record.PackageId, config.Id,
                DeskBoxDataPathService.Current.DataDirectory);
            if (NativeWidgetRuntimeManager.TryCreateFromInstalled(
                    handle!, binding.ContributionId, config.Id,
                    DeskBoxDataPathService.Current.DataDirectory,
                    out NativeWidgetLease? lease))
            {
                // Ownership for the write-through routing (audit 21 §11).
                if (binding.Migration is not null)
                {
                    PackageInstanceRegistry.Register(binding.Migration, config.Id);
                }
                content = new NativeWidgetPilotContent(config, lease!);
                return true;
            }
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
    IWidgetCompactBackgroundContent,
    IWidgetResponsiveLayoutContent,
    IWidgetHostViewportContent,
    IWidgetPerformanceAwareContent,
    IWidgetInteractiveResizeContent,
    IDisposable
{
    private readonly NativeWidgetLease _lease;
    private readonly NativeInstanceSettingsSubscription? _settingsSubscription;
    private FrameworkElement? _compactBackgroundView;
    private bool _compactBackgroundNotificationsStopped;

    internal NativeWidgetPilotContent(WidgetConfig config, NativeWidgetLease lease)
    {
        Config = config;
        _lease = lease;
        ILegacyInstanceMigration? migration = PackageBindingRegistry.TryGetByKind(config.WidgetKind)?.Migration;
        if (migration is not null && lease.View is { } view)
        {
            var dispatcher = view.DispatcherQueue;
            NativePackageIdentity identity = lease.Session.Identity;
            _settingsSubscription = new NativeInstanceSettingsSubscription(
                migration, config.Id,
                action => dispatcher.TryEnqueue(() => action()),
                () => NativeWidgetDataMigration.TrySync(
                    migration, identity.PublisherFingerprint, identity.PackageId,
                    config.Id, DeskBoxDataPathService.Current.DataDirectory),
                // Bit 0 means settings-only; ordinary/older flags=0 refreshes
                // retain their full-content meaning.
                refreshContent => _lease.InvokeWidgetEvent(WidgetLifecycleEventKind.RefreshRequested, 0, 0, refreshContent ? 0u : 1u));
        }
        // Treat DataContextChanged only as an invalidation signal. The package
        // publishes a fresh context after decode/settings updates; its CLR
        // business object is never inspected or projected by the host.
        if (lease.View is { } backgroundView)
        {
            _compactBackgroundView = backgroundView;
            backgroundView.DataContextChanged += CompactBackgroundView_DataContextChanged;
        }
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
        if (_settingsSubscription is not null) _settingsSubscription.RequestRefresh(refreshContent: true);
        else _lease.InvokeWidgetEvent(WidgetLifecycleEventKind.RefreshRequested, 0, 0, 0);
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

    public WidgetCompactBackgroundSnapshot? GetCompactBackground() =>
        _lease.GetCompactBackground();

    public event EventHandler? CompactBackgroundChanged;

    private void CompactBackgroundView_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (ReferenceEquals(sender, _compactBackgroundView)) NotifyCompactBackgroundChanged();
    }

    // Managed seam: no WinUI object or package DataContext is needed to
    // verify notification delivery and suppression after disposal.
    internal void NotifyCompactBackgroundChanged()
    {
        if (_compactBackgroundNotificationsStopped) return;
        try
        {
            CompactBackgroundChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error)
        {
            // A host subscriber must not throw back into the package's
            // WinRT DataContext update.
            App.LogVerbose($"[NativePackage] compact background notification failed: {error.Message}");
        }
    }

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

    public void Dispose()
    {
        _compactBackgroundNotificationsStopped = true;
        CompactBackgroundChanged = null;
        // Unregister ownership BEFORE destroy so the write-through callback
        // doesn't route to a destroyed instance (audit 21 §11).
        PackageInstanceRegistry.Unregister(WidgetId);
        FrameworkElement? backgroundView = _compactBackgroundView;
        try
        {
            if (backgroundView is not null)
                backgroundView.DataContextChanged -= CompactBackgroundView_DataContextChanged;
            _compactBackgroundView = null;
        }
        catch (Exception error)
        {
            // Keep the view for a later detach retry; notifications are
            // already suppressed, and package destroy must still run.
            App.LogVerbose($"[NativePackage] compact background detach failed: {error.Message}");
        }
        // Keep the existing destroy retry path even after notifications stop.
        _settingsSubscription?.Dispose();
        ((IDisposable)_lease).Dispose();
    }
}
