using System.Runtime.InteropServices;

namespace DeskBox.Services.Plugins;

/// <summary>
/// Development-only pilot loader for native widget packages (batch C wiring).
/// Gated by DESKBOX_DEV_NATIVE_GLANCE pointing at a package directory that
/// contains a native package DLL; never active by default. The full product
/// path (B1 PackageManager verification bound to module open) lands in batch C.
/// </summary>
internal static class NativeWidgetPackageLoader
{
    public const string DevelopmentPackageEnvironmentVariable = "DESKBOX_DEV_NATIVE_GLANCE";
    public const string PackageDllFileName = "DeskBox.Glance.NativePackage.dll";
    public const int RequiredAbiVersion = 1;

    /// <summary>Path validation only - no module loading (unit-testable).</summary>
    public static string? TryGetDevelopmentPackageRoot()
    {
        string? configured = Environment.GetEnvironmentVariable(DevelopmentPackageEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured)) return null;
        try
        {
            string root = Path.GetFullPath(configured.Trim());
            if (!Directory.Exists(root)) return null;
            return File.Exists(Path.Combine(root, PackageDllFileName)) ? root : null;
        }
        catch
        {
            return null;
        }
    }

    public static string ResolveDataRoot(string packageRoot, string dataDirectory)
    {
        string packageName = new DirectoryInfo(packageRoot).Name;
        return Path.Combine(dataDirectory, "native-packages", packageName);
    }

    /// <summary>Loads the module once per process (NativeAOT DLLs are never unloaded) and resolves the unified package ABI.</summary>
    public static NativeWidgetPackage? TryActivate(string packageRoot, string dataRoot, string instanceId)
    {
        try
        {
            nint module = NativeLibrary.Load(Path.Combine(packageRoot, PackageDllFileName));
            if (!TryGetExport(module, "deskbox_package_get_abi_version", out nint versionExport) ||
                !TryGetExport(module, "deskbox_package_activate", out nint activateExport) ||
                !TryGetExport(module, "deskbox_widget_create", out nint createExport) ||
                !TryGetExport(module, "deskbox_widget_destroy", out nint destroyExport) ||
                !TryGetExport(module, "deskbox_package_shutdown", out nint shutdownExport))
            {
                App.LogVerbose("[NativePackage] unified ABI exports missing; pilot disabled");
                return null;
            }
            var package = new NativeWidgetPackage(module, versionExport, activateExport, createExport, destroyExport, shutdownExport);
            if (package.AbiVersion != RequiredAbiVersion)
            {
                App.Log($"[NativePackage] ABI version {package.AbiVersion} != {RequiredAbiVersion}; pilot disabled");
                return null;
            }
            int status = package.Activate(packageRoot, dataRoot, instanceId);
            if (status != 0)
            {
                App.Log($"[NativePackage] activate failed 0x{status:X8}; pilot disabled");
                return null;
            }
            App.Log($"[NativePackage] pilot package active: {Path.GetFileName(packageRoot)} (instance {instanceId})");
            return package;
        }
        catch (Exception error)
        {
            App.Log($"[NativePackage] load failed: {error.Message}");
            return null;
        }
    }

    private static bool TryGetExport(nint module, string name, out nint export)
    {
        try
        {
            export = NativeLibrary.GetExport(module, name);
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            export = 0;
            return false;
        }
    }
}

/// <summary>Wrapper over the unified native package ABI. UI thread only.</summary>
internal sealed unsafe class NativeWidgetPackage
{
    private readonly nint _module;
    private readonly nint _versionExport;
    private readonly nint _activateExport;
    private readonly nint _createExport;
    private readonly nint _destroyExport;
    private readonly nint _shutdownExport;

    internal NativeWidgetPackage(nint module, nint versionExport, nint activateExport, nint createExport, nint destroyExport, nint shutdownExport)
    {
        _module = module;
        _versionExport = versionExport;
        _activateExport = activateExport;
        _createExport = createExport;
        _destroyExport = destroyExport;
        _shutdownExport = shutdownExport;
    }

    public int AbiVersion => ((delegate* unmanaged[Cdecl]<int>)_versionExport)();

    public int Activate(string packageRoot, string dataRoot, string instanceId)
    {
        var activate = (delegate* unmanaged[Cdecl]<char*, int, char*, int, char*, int, int>)_activateExport;
        fixed (char* package = packageRoot)
        fixed (char* data = dataRoot)
        fixed (char* instance = instanceId)
        {
            return activate(package, packageRoot.Length, data, dataRoot.Length, instance, instanceId.Length);
        }
    }

    public Microsoft.UI.Xaml.FrameworkElement? TryCreateWidget(string widgetId)
    {
        var create = (delegate* unmanaged[Cdecl]<char*, int, nint*, int>)_createExport;
        nint abi = 0;
        int status;
        fixed (char* id = widgetId)
        {
            status = create(id, widgetId.Length, &abi);
        }
        if (status != 0 || abi == 0)
        {
            App.Log($"[NativePackage] create widget failed 0x{status:X8}");
            return null;
        }
        try
        {
            var view = WinRT.MarshalInspectable<Microsoft.UI.Xaml.FrameworkElement>.FromAbi(abi);
            view.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
            view.VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch;
            return view;
        }
        catch (Exception error)
        {
            App.Log($"[NativePackage] widget projection failed: {error.Message}");
            return null;
        }
        finally
        {
            WinRT.MarshalInspectable<Microsoft.UI.Xaml.FrameworkElement>.DisposeAbi(abi);
        }
    }

    public void DestroyWidget(string widgetId)
    {
        var destroy = (delegate* unmanaged[Cdecl]<char*, int, int>)_destroyExport;
        fixed (char* id = widgetId) destroy(id, widgetId.Length);
    }

    public void Shutdown()
    {
        ((delegate* unmanaged[Cdecl]<int>)_shutdownExport)();
        _ = _module; // Module stays loaded for process lifetime by design.
    }
}
