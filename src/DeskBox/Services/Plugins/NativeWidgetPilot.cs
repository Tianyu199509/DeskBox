using DeskBox.Contracts;
using DeskBox.Models;
using Microsoft.UI.Xaml;

namespace DeskBox.Services.Plugins;

/// <summary>
/// Development pilot: widget content served by a native package. The
/// INSTALLED path is tried first regardless of any environment variable;
/// DESKBOX_DEV_NATIVE_GLANCE only controls the dev-time install bootstrap
/// and the raw-directory fallback for spike iteration.
/// </summary>
internal static class NativeWidgetPilot
{
    private const string DevPublisherFingerprint = "1bc4f2db8438d2fd296bd48074088ccc726c265712125abbae975063ba719ea4";
    private const string TargetPackageId = "deskbox.glance";

    private static PluginPackageManager? _manager;
    private static PluginPackageManager Manager => _manager ??= new PluginPackageManager(
        Path.Combine(DeskBoxDataPathService.Current.DataDirectory, "plugins"),
        [DevPublisherFingerprint]);

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
            PluginInstallResult result = Manager.Install(packageRoot);
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

        // 2. Dev fallback (env-var gated): raw directory, no B1 pipeline.
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
        return false;
    }
}

internal sealed class NativeWidgetPilotContent : IWidgetContent, IDisposable
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
        App.LogVerbose($"[NativePackage] pilot initialized for {WidgetId}");
        return Task.CompletedTask;
    }

    public Task RefreshAsync() => Task.CompletedTask;
    public void ApplyAppearance() { }
    public void OnActivated() { }
    public void OnDeactivated() { }

    public void Dispose() => ((IDisposable)_lease).Dispose();
}
