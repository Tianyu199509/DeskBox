using DeskBox.Contracts;
using DeskBox.Models;
using Microsoft.UI.Xaml;

namespace DeskBox.Services.Plugins;

/// <summary>
/// Development pilot: widget content served by a native package when the
/// DESKBOX_DEV_NATIVE_GLANCE environment variable points at a valid package
/// directory. Default off; any failure falls back to the built-in provider.
///
/// Batch D2: when the env var is set, the pilot first INSTALLS the pointed
/// directory through the B1 pipeline (PluginPackageManager.Install with the
/// dev key as a trusted publisher), then activates through the full installed
/// path (TryCreateNativeHandle → TryCreateFromInstalled). This proves the
/// complete schema → sign → install → verify → activate chain on the real host.
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
    /// Called by WidgetContentFactory at construction; installs the pointed
    /// package through the B1 pipeline at app startup (D2 smoke proof).
    /// </summary>
    internal static void Initialize()
    {
        string? packageRoot = NativeWidgetPackageLoader.TryGetDevelopmentPackageRoot();
        if (packageRoot is not null)
        {
            TryInstallDevelopmentPackage(packageRoot);
        }
    }

    public static bool TryCreate(WidgetConfig config, out IWidgetContent? content)
    {
        content = null;
        string? packageRoot = NativeWidgetPackageLoader.TryGetDevelopmentPackageRoot();
        if (packageRoot is null) return false;

        // Activate through the full installed path: B1 handle → runtime manager.
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

        // Fallback: direct directory load (batch C dev path, no B1 pipeline).
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

    private static void TryInstallDevelopmentPackage(string packageRoot)
    {
        try
        {
            PluginInstallResult result = Manager.Install(packageRoot);
            if (result.Succeeded)
            {
                App.Log($"[NativePackage] dev package installed: {result.Package!.PackageId} v{result.Package.Version} -> {result.InstallDirectory}");
            }
            else
            {
                App.Log($"[NativePackage] dev package install failed: {string.Join("; ", result.Failures)}");
            }
        }
        catch (Exception error)
        {
            App.Log($"[NativePackage] dev package install error: {error.Message}");
        }
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

    /// <summary>
    /// The host disposes widget content via IDisposable (WidgetManager); the
    /// lease routes that to handle-based destroy, and the runtime manager
    /// shuts the package down when this was the last live instance.
    /// </summary>
    public void Dispose() => ((IDisposable)_lease).Dispose();
}
