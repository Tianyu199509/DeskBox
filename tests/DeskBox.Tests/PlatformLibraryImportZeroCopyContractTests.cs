using System;
using System.CodeDom.Compiler;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DeskBox.Platform;
using DeskBox.Services;
using Xunit;

namespace DeskBox.Tests;

public sealed class PlatformLibraryImportZeroCopyContractTests
{
    private static readonly string[] MigratedPlatformTypeNames =
    [
        "DeskBox.Platform.CustomWidgetNativeMetrics",
        "DeskBox.Platform.ClipboardPlatformInterop",
        "DeskBox.Platform.ClipboardNativeMethods",
        "DeskBox.Platform.ShellIconNativeMethods",
        "DeskBox.Platform.Win32Helper",
        "DeskBox.Platform.OleDropTargetNativeMethods",
        "DeskBox.Platform.SingleInstanceAndActivationInterop",
        "DeskBox.Platform.Kernel32NativeMethods",
        "DeskBox.Platform.ComVtable",
        "DeskBox.Platform.FileTransferNativeMethods",
        "DeskBox.Platform.DragDropPermissionNativeMethods",
        "DeskBox.Platform.StorageMigrationNativeMethods",
        "DeskBox.Platform.DriveMediaInterop",
        "DeskBox.Platform.ProcessImageNameResolver",
        "DeskBox.Platform.DesktopToastHudInterop",
        "DeskBox.Platform.PathOriginClassifier",
    ];

    [Fact]
    public void MigratedHotPathPlatformTypes_UseLibraryImportAndContainZeroClassicDllImports()
    {
        Assembly deskBoxAssembly = typeof(Win32Helper).Assembly;

        foreach (string fullTypeName in MigratedPlatformTypeNames)
        {
            Type? targetType = deskBoxAssembly.GetType(fullTypeName);
            Assert.NotNull(targetType);

            MethodInfo[] allMethods = targetType!
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            // LibraryImport methods always carry LibraryImportAttribute (and emit local
            // `*__PInvoke*` extern stubs when custom marshalling or SetLastError is involved).
            // Classic hand-authored [DllImport] methods have PinvokeImpl without LibraryImportAttribute.
            string[] classicDllImports = allMethods
                .Where(m => (m.Attributes & MethodAttributes.PinvokeImpl) != 0 &&
                            !m.Name.Contains("__PInvoke", StringComparison.Ordinal) &&
                            m.GetCustomAttribute<LibraryImportAttribute>() is null)
                .Select(m => $"{targetType.Name}.{m.Name}")
                .ToArray();

            Assert.Empty(classicDllImports);
        }
    }

    [Fact]
    public void BlittablePlatformStructs_MatchWin32NativeAbiSizesAndHoldNoManagedReferences()
    {
        Assembly deskBoxAssembly = typeof(Win32Helper).Assembly;

        AssertBlittableStructSize(deskBoxAssembly, "DeskBox.Platform.ShellIconNativeMethods+SHFILEINFO", expectedSize64: 696);
        AssertBlittableStructSize(deskBoxAssembly, "DeskBox.Platform.Win32Helper+MONITORINFOEX", expectedSize64: 104);
        AssertBlittableStructSize(deskBoxAssembly, "DeskBox.Platform.Win32Helper+DEVMODE", expectedSize64: 220);
        AssertBlittableStructSize(deskBoxAssembly, "DeskBox.Platform.Win32Helper+DISPLAY_DEVICEW", expectedSize64: 840);
        AssertBlittableStructSize(deskBoxAssembly, "DeskBox.Platform.Win32Helper+DisplayDevice", expectedSize64: 840);
        AssertBlittableStructSize(deskBoxAssembly, "DeskBox.Platform.Win32Helper+DisplaySourceName", expectedSize64: 84);
        AssertBlittableStructSize(deskBoxAssembly, "DeskBox.Platform.Win32Helper+DisplayTargetName", expectedSize64: 420);
        AssertBlittableStructSize(deskBoxAssembly, "DeskBox.Platform.Win32Helper+LogFont", expectedSize64: 92);
        AssertBlittableStructSize(deskBoxAssembly, "DeskBox.Platform.Win32Helper+OpenAsInfo", expectedSize64: 24);
        AssertBlittableStructSize(deskBoxAssembly, "DeskBox.Platform.Win32Helper+ProcessMemoryCountersEx", expectedSize64: 80);
        AssertBlittableStructSize(deskBoxAssembly, "DeskBox.Helpers.NativeDropTarget+FILEDESCRIPTORW", expectedSize64: 592);
        AssertBlittableStructSize(deskBoxAssembly, "DeskBox.Platform.FileTransferNativeMethods+FileDispositionInfo", expectedSize64: 1);
        AssertBlittableStructSize(deskBoxAssembly, "DeskBox.Platform.StorageMigrationNativeMethods+StreamData", expectedSize64: 600);

        foreach (string secAttrName in new[]
                 {
                     "DeskBox.Platform.SingleInstanceAndActivationInterop+SecurityAttributes",
                     "DeskBox.Platform.Win32Helper+SecurityAttributes",
                 })
        {
            AssertBlittableStructSize(deskBoxAssembly, secAttrName, expectedSize64: 24);
            Type secAttrType = deskBoxAssembly.GetType(secAttrName)!;
            Assert.Equal(typeof(uint), secAttrType.GetField("Length", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.FieldType);
            Assert.Equal(typeof(int), secAttrType.GetField("InheritHandle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.FieldType);
        }
    }

    [Fact]
    public void HotPathPlatformOperations_AllocateZeroManagedHeapBytes()
    {
        IntPtr desktopHwnd = Win32Helper.GetDesktopWindow();
        Assert.NotEqual(IntPtr.Zero, desktopHwnd);

        IntPtr posPtr = Marshal.AllocHGlobal(Unsafe.SizeOf<Win32Helper.WINDOWPOS>());
        try
        {
            var initialPos = new Win32Helper.WINDOWPOS
            {
                hwnd = desktopHwnd,
                x = 10,
                y = 20,
                cx = 300,
                cy = 200,
                flags = 0,
            };
            Marshal.StructureToPtr(initialPos, posPtr, fDeleteOld: false);

            Span<char> classSpan = stackalloc char[128];

            // Warmup JIT & lazy P/Invoke binding
            for (int i = 0; i < 16; i++)
            {
                Assert.True(Win32Helper.TryReadWindowPos(posPtr, out Win32Helper.WINDOWPOS warmPos));
                Win32Helper.SuppressWindowPosZOrder(posPtr, warmPos);
                _ = Win32Helper.GetClassName(desktopHwnd, classSpan);
                _ = ClipboardPlatformInterop.IsEligibleExternalPasteTarget(desktopHwnd);
                var warmShInfo = new ShellIconNativeMethods.SHFILEINFO();
                _ = ShellIconNativeMethods.SHGetFileInfo(
                    ".txt",
                    0x80,
                    ref warmShInfo,
                    ShellIconNativeMethods.ShFileInfoSize,
                    ShellIconNativeMethods.SHGFI_SYSICONINDEX | ShellIconNativeMethods.SHGFI_USEFILEATTRIBUTES);
                Assert.True(Win32Helper.TryGetCurrentProcessMemoryBytes(out long warmWs, out long warmPriv));
                Assert.True(warmWs > 0 && warmPriv > 0);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            int totalClassChars = 0;
            int iconIndexSum = 0;
            long memoryBytesSum = 0;

            for (int i = 0; i < 200; i++)
            {
                if (Win32Helper.TryReadWindowPos(posPtr, out Win32Helper.WINDOWPOS readPos))
                {
                    Win32Helper.SuppressWindowPosZOrder(posPtr, readPos);
                }

                totalClassChars += Win32Helper.GetClassName(desktopHwnd, classSpan);
                _ = ClipboardPlatformInterop.IsEligibleExternalPasteTarget(desktopHwnd);

                var shInfo = new ShellIconNativeMethods.SHFILEINFO();
                _ = ShellIconNativeMethods.SHGetFileInfo(
                    ".txt",
                    0x80,
                    ref shInfo,
                    ShellIconNativeMethods.ShFileInfoSize,
                    ShellIconNativeMethods.SHGFI_SYSICONINDEX | ShellIconNativeMethods.SHGFI_USEFILEATTRIBUTES);
                iconIndexSum += shInfo.iIcon;

                if (Win32Helper.TryGetCurrentProcessMemoryBytes(out long wsBytes, out long privBytes))
                {
                    memoryBytesSum += wsBytes + privBytes;
                }
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(totalClassChars > 0);
            Assert.NotEqual(-1, iconIndexSum);
            Assert.True(memoryBytesSum > 0);
            Assert.Equal(0, allocated);
            Assert.True(Win32Helper.TryReadWindowPos(posPtr, out Win32Helper.WINDOWPOS finalPos));
            Assert.NotEqual(0u, finalPos.flags & Win32Helper.SWP_NOZORDER);
        }
        finally
        {
            Marshal.FreeHGlobal(posPtr);
        }
    }

    [Fact]
    public void StoreAndDirectPackageIdentityProbe_WorksAcrossWin10Win11AndChannels()
    {
        const int appModelErrorNoPackage = 15700;
        const int appModelErrorNoApplication = 15703;
        const int errorInsufficientBuffer = 122;

        int pkgResult = Kernel32NativeMethods.ProbeCurrentPackageFullNameLength(out uint pkgLen);
        Assert.True(
            pkgResult == appModelErrorNoPackage || (pkgResult == errorInsufficientBuffer && pkgLen > 0),
            $"Unexpected GetCurrentPackageFullName probe result: {pkgResult} (len={pkgLen})");

        int aumidResult = Kernel32NativeMethods.ProbeCurrentApplicationUserModelIdLength(out uint aumidLen);
        Assert.True(
            aumidResult == appModelErrorNoPackage ||
            aumidResult == appModelErrorNoApplication ||
            (aumidResult == errorInsufficientBuffer && aumidLen > 0),
            $"Unexpected GetCurrentApplicationUserModelId probe result: {aumidResult} (len={aumidLen})");

        var directService = new AppDistributionService(AppDistributionChannel.Direct);
        var storeService = new AppDistributionService(AppDistributionChannel.MicrosoftStore);
        Assert.Equal(pkgResult != appModelErrorNoPackage, directService.IsPackaged);
        Assert.Equal(directService.IsPackaged, storeService.IsPackaged);
    }

    private static void AssertBlittableStructSize(Assembly assembly, string fullTypeName, int expectedSize64)
    {
        Type? structType = assembly.GetType(fullTypeName);
        Assert.NotNull(structType);
        Assert.True(structType!.IsValueType);

        MethodInfo checkRefs = typeof(RuntimeHelpers)
            .GetMethod(nameof(RuntimeHelpers.IsReferenceOrContainsReferences))!
            .MakeGenericMethod(structType);
        bool containsRefs = (bool)checkRefs.Invoke(null, null)!;
        Assert.False(containsRefs, $"{fullTypeName} must be pure unmanaged/blittable with zero managed references.");

        if (IntPtr.Size == 8)
        {
            MethodInfo unsafeSizeOf = typeof(Unsafe)
                .GetMethod(nameof(Unsafe.SizeOf))!
                .MakeGenericMethod(structType);
            int actualSize = (int)unsafeSizeOf.Invoke(null, null)!;
            Assert.Equal(expectedSize64, actualSize);
            Assert.Equal(expectedSize64, Marshal.SizeOf(structType));
        }
    }
}
