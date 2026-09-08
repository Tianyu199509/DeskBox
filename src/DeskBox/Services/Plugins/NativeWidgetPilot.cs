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
    public static bool TryCreate(WidgetConfig config, out IWidgetContent? content)
    {
        content = null;
        string? packageRoot = NativeWidgetPackageLoader.TryGetDevelopmentPackageRoot();
        if (packageRoot is null) return false;
        NativeWidgetPackage? package = NativeWidgetPackageLoader.TryActivate(
            packageRoot,
            NativeWidgetPackageLoader.ResolveDataRoot(packageRoot, DeskBoxDataPathService.Current.DataDirectory),
            config.Id);
        if (package is null) return false;
        FrameworkElement? view = package.TryCreateWidget(config.Id);
        if (view is null)
        {
            package.Shutdown();
            return false;
        }
        content = new NativeWidgetPilotContent(config, package, view);
        return true;
    }
}

internal sealed class NativeWidgetPilotContent : IWidgetContent
{
    private readonly NativeWidgetPackage _package;

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
}
