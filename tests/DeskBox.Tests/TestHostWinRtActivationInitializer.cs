using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DeskBox.Tests;

/// <summary>
/// Enables registration-free activation of <c>Microsoft.UI.Dispatching.*</c>
/// (<see cref="Microsoft.UI.Dispatching.DispatcherQueueController"/>,
/// <see cref="Microsoft.UI.Dispatching.DispatcherQueue"/>,
/// <see cref="Microsoft.UI.Dispatching.DispatcherExitDeferral"/>) inside the
/// headless xUnit test host even when <c>WindowsAppSdkBootstrapInitialize=false</c>
/// (such as on GitHub Actions runners without the MSIX framework package installed).
/// </summary>
internal static class TestHostWinRtActivationInitializer
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DllGetActivationFactoryDelegate(IntPtr activatableClassId, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string lpPathName);

    private static readonly object s_sync = new();
    private static DllGetActivationFactoryDelegate? s_coreMessagingGetActivationFactory;
    private static bool s_coreMessagingLoadAttempted;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Func<string, Guid, IntPtr>? previous = WinRT.ActivationFactory.ActivationHandler;
        WinRT.ActivationFactory.ActivationHandler = (typeName, iid) =>
        {
            if (previous is not null)
            {
                IntPtr handled = previous(typeName, iid);
                if (handled != IntPtr.Zero)
                {
                    return handled;
                }
            }

            if (typeName.StartsWith("Microsoft.UI.Dispatching.", StringComparison.Ordinal))
            {
                return TryActivateCoreMessaging(typeName, in iid);
            }

            return IntPtr.Zero;
        };
    }

    private static IntPtr TryActivateCoreMessaging(string typeName, in Guid iid)
    {
        DllGetActivationFactoryDelegate? getFactory = EnsureCoreMessagingFactoryLoaded();
        if (getFactory is null)
        {
            return IntPtr.Zero;
        }

        if (WindowsCreateString(typeName, typeName.Length, out IntPtr hstring) < 0 || hstring == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            if (getFactory(hstring, out IntPtr factory) < 0 || factory == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            try
            {
                Guid targetIid = iid;
                int hr = Marshal.QueryInterface(factory, in targetIid, out IntPtr interfacePtr);
                return hr >= 0 ? interfacePtr : IntPtr.Zero;
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    private static DllGetActivationFactoryDelegate? EnsureCoreMessagingFactoryLoaded()
    {
        lock (s_sync)
        {
            if (s_coreMessagingLoadAttempted)
            {
                return s_coreMessagingGetActivationFactory;
            }

            s_coreMessagingLoadAttempted = true;
            string? coreMessagingPath = ResolveCoreMessagingPath();
            if (string.IsNullOrEmpty(coreMessagingPath) || !File.Exists(coreMessagingPath))
            {
                return null;
            }

            string? directory = Path.GetDirectoryName(coreMessagingPath);
            if (!string.IsNullOrEmpty(directory))
            {
                // CoreMessagingXP.dll dynamically loads sibling modules such as
                // Microsoft.Internal.FrameworkUdk.dll and Microsoft.UI.Windowing.Core.dll
                // on the dedicated DispatcherQueue thread.
                _ = SetDllDirectoryW(directory);
                foreach (string companion in new[] { "Microsoft.Internal.FrameworkUdk.dll", "Microsoft.UI.Windowing.Core.dll" })
                {
                    string companionPath = Path.Combine(directory, companion);
                    if (File.Exists(companionPath))
                    {
                        _ = NativeLibrary.TryLoad(companionPath, out _);
                    }
                }
            }

            if (NativeLibrary.TryLoad(coreMessagingPath, out IntPtr moduleHandle) &&
                NativeLibrary.TryGetExport(moduleHandle, "DllGetActivationFactory", out IntPtr export))
            {
                s_coreMessagingGetActivationFactory =
                    Marshal.GetDelegateForFunctionPointer<DllGetActivationFactoryDelegate>(export);
            }

            return s_coreMessagingGetActivationFactory;
        }
    }

    private static string? ResolveCoreMessagingPath()
    {
        string localPath = Path.Combine(AppContext.BaseDirectory, "CoreMessagingXP.dll");
        if (File.Exists(localPath))
        {
            return localPath;
        }

        string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        string? nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(nugetRoot))
        {
            nugetRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget",
                "packages");
        }

        string pkgDir = Path.Combine(nugetRoot, "microsoft.windowsappsdk.interactiveexperiences");
        if (!Directory.Exists(pkgDir))
        {
            return null;
        }

        foreach (string versionDir in Directory.GetDirectories(pkgDir).OrderByDescending(static d => d, StringComparer.OrdinalIgnoreCase))
        {
            string candidate = Path.Combine(versionDir, "runtimes-framework", arch, "native", "CoreMessagingXP.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
