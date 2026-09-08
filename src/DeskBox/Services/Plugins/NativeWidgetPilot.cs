using DeskBox.Contracts;
using DeskBox.Models;
using Microsoft.UI.Xaml;

namespace DeskBox.Services.Plugins;

/// <summary>
/// Development pilot: widget content served by a native package when the
/// DESKBOX_DEV_NATIVE_GLANCE environment variable points at a valid package
/// directory. Default off; any failure falls back to the built-in provider
/// path - the pilot can never break the affected widget kind. Runs entirely
/// through the batch C1 runtime contract (session per identity, instance
/// leases, handle-based destroy, shutdown on last release).
/// </summary>
internal static class NativeWidgetPilot
{
    // 1. Installed path (batch D): B1 pipeline → native handle → runtime manager.
    //    The manager is created lazily; it reads the B1 registry from the
    //    standard plugins root under the host data directory.
    private static PluginPackageManager? _installedPackageManager;
    private static PluginPackageManager InstalledPackageManager => _installedPackageManager ??= new PluginPackageManager(
        Path.Combine(DeskBoxDataPathService.Current.DataDirectory, "plugins"));
    public static bool TryCreate(WidgetConfig config, out IWidgetContent? content)
    {
        content = null;

        // 1. Installed path (batch D): B1 pipeline → native handle → runtime manager.
        NativeInstalledPackageHandle? handle = InstalledPackageManager.TryCreateNativeHandle("deskbox.glance");
        if (handle is not null &&
            NativeWidgetRuntimeManager.TryCreateFromInstalled(
                handle!, "glance", config.Id,
                DeskBoxDataPathService.Current.DataDirectory,
                out NativeWidgetLease? installedLease))
        {
            content = new NativeWidgetPilotContent(config, installedLease!);
            return true;
        }

        // 2. Development path (batch C spike): env-var gated, directory package.
        string? packageRoot = NativeWidgetPackageLoader.TryGetDevelopmentPackageRoot();
        if (packageRoot is null) return false;
        if (!NativeWidgetRuntimeManager.TryCreateInstance(
                NativeWidgetPackageLoader.CreateDevelopmentDescriptor(packageRoot),
                contributionId: "main",
                instanceId: config.Id,
                dataDirectory: DeskBoxDataPathService.Current.DataDirectory,
                out NativeWidgetLease? lease))
        {
            return false;
        }
        content = new NativeWidgetPilotContent(config, lease!);
        return true;
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
