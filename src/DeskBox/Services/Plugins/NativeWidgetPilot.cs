using DeskBox.Contracts;
using DeskBox.Models;
using Microsoft.UI.Xaml;

namespace DeskBox.Services.Plugins;

/// <summary>
/// Development pilot: widget content served by a native package when the
/// DESKBOX_DEV_NATIVE_GLANCE environment variable points at a valid package
/// directory. Default off; any failure falls back to the built-in provider
/// path - the pilot can never break the affected widget kind.
/// </summary>
internal static class NativeWidgetPilot
{
    // Interim session cache (audit round 11): one activation per package root
    // per process - re-activating per widget would overwrite the package's
    // static session state and pair the wrong instance id. The full
    // NativePackageSession/instance model lands in batch C1.
    private static NativeWidgetPackage? _activePackage;
    private static string? _activePackageRoot;

    public static bool TryCreate(WidgetConfig config, out IWidgetContent? content)
    {
        content = null;
        string? packageRoot = NativeWidgetPackageLoader.TryGetDevelopmentPackageRoot();
        if (packageRoot is null) return false;
        NativeWidgetPackage? package = _activePackageRoot == packageRoot
            ? _activePackage
            : NativeWidgetPackageLoader.TryActivate(
                packageRoot,
                NativeWidgetPackageLoader.ResolveDataRoot(packageRoot, DeskBoxDataPathService.Current.DataDirectory),
                "pilot-session");
        if (package is null) return false;
        _activePackage = package;
        _activePackageRoot = packageRoot;
        FrameworkElement? view = package.TryCreateWidget(config.Id);
        if (view is null) return false;
        content = new NativeWidgetPilotContent(config, package, view);
        return true;
    }
}

internal sealed class NativeWidgetPilotContent : IWidgetContent, IDisposable
{
    private readonly NativeWidgetPackage _package;
    private bool _disposed;

    internal NativeWidgetPilotContent(WidgetConfig config, NativeWidgetPackage package, FrameworkElement view)
    {
        Config = config;
        _package = package;
        View = view;
    }

    public WidgetConfig Config { get; }
    public string WidgetId => Config.Id;
    public WidgetKind WidgetKind => Config.WidgetKind;
    public FrameworkElement View { get; }

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
    /// The host disposes widget content via IDisposable (WidgetManager); route
    /// that to the package's instance destroy. Package shutdown intentionally
    /// NOT triggered here - other widget instances may still use the session.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _package.DestroyWidget(WidgetId);
            App.LogVerbose($"[NativePackage] pilot widget destroyed: {WidgetId}");
        }
        catch (Exception error)
        {
            App.Log($"[NativePackage] destroy failed for {WidgetId}: {error.Message}");
        }
    }
}
