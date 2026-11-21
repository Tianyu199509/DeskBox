using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Services.Studio;
using Xunit;

namespace DeskBox.Tests;

public sealed class NativeInteropAndShellSafetyRegressionTests : IDisposable
{
    private readonly string _tempRoot;

    public NativeInteropAndShellSafetyRegressionTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "DeskBoxTests",
            "NativeInteropAndShellSafety",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeListSizeCanaryFrame
    {
        public ulong LowerCanary;
        public nuint AttributeListSize;
        public ulong UpperCanary;
    }

    [Fact]
    public void InitializeProcThreadAttributeList_QueryAndAllocate_PreservesAdjacentCanaries()
    {
        var frame = new AttributeListSizeCanaryFrame
        {
            LowerCanary = 0xDEAD_BEEF_CAFE_BABEu,
            AttributeListSize = 0,
            UpperCanary = 0xA5A5_5A5A_F00D_1234u
        };

        bool firstResult = ShellUnelevatedLaunch.InitializeProcThreadAttributeList(
            IntPtr.Zero,
            1,
            0,
            ref frame.AttributeListSize);

        Assert.False(firstResult);
        Assert.Equal(122, Marshal.GetLastWin32Error()); // ERROR_INSUFFICIENT_BUFFER
        Assert.True(frame.AttributeListSize > 0 && frame.AttributeListSize <= 4096);
        Assert.Equal(0xDEAD_BEEF_CAFE_BABEu, frame.LowerCanary);
        Assert.Equal(0xA5A5_5A5A_F00D_1234u, frame.UpperCanary);

        IntPtr attributeList = Marshal.AllocHGlobal((nint)frame.AttributeListSize);
        try
        {
            bool initialized = ShellUnelevatedLaunch.InitializeProcThreadAttributeList(
                attributeList,
                1,
                0,
                ref frame.AttributeListSize);
            Assert.True(initialized);
            try
            {
                Assert.Equal(0xDEAD_BEEF_CAFE_BABEu, frame.LowerCanary);
                Assert.Equal(0xA5A5_5A5A_F00D_1234u, frame.UpperCanary);
            }
            finally
            {
                ShellUnelevatedLaunch.DeleteProcThreadAttributeList(attributeList);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(attributeList);
        }
    }

    [Fact]
    public void ZoneIdentifierWriter_EmitsBomFreeMotwRecognizedByWin32ComAndSanitizesNewlines()
    {
        string filePath = Path.Combine(_tempRoot, "downloaded-sample.bin");
        File.WriteAllBytes(filePath, [0x4D, 0x5A, 0x90, 0x00]);

        bool marked = ZoneIdentifierWriter.TryMarkFile(
            filePath,
            "https://downloads.example.test/app.exe\r\nZoneId=0\nReferrerUrl=file:///C:/trusted");
        Assert.True(marked);

        byte[] adsBytes = File.ReadAllBytes(filePath + ":Zone.Identifier");
        Assert.True(adsBytes.Length >= 20);
        Assert.False(
            adsBytes.Length >= 3 && adsBytes[0] == 0xEF && adsBytes[1] == 0xBB && adsBytes[2] == 0xBF,
            "Zone.Identifier stream must not start with a UTF-8 BOM because urlmon!IZoneIdentifier rejects BOM-prefixed headers.");

        string adsText = Encoding.UTF8.GetString(adsBytes);
        Assert.StartsWith("[ZoneTransfer]\r\nZoneId=3\r\n", adsText, StringComparison.Ordinal);
        Assert.DoesNotContain("\nZoneId=0", adsText, StringComparison.Ordinal);

        // Verify live Windows urlmon PersistentZoneIdentifier COM parser recognizes ZoneId == 3 (URLZONE_INTERNET).
        uint zone = ReadWindowsZoneIdentifierViaCom(filePath);
        Assert.Equal(3u, zone);
    }

    [Fact]
    public void StorageBusTypeHelper_TryGetBusType_SystemDriveReturnsValidBusType()
    {
        string? systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        Assert.False(string.IsNullOrWhiteSpace(systemRoot));

        int? busTypeWithSlash = StorageBusTypeHelper.TryGetBusType(systemRoot);
        int? busTypeBareDrive = StorageBusTypeHelper.TryGetBusType(systemRoot!.TrimEnd('\\', '/'));
        Assert.NotNull(busTypeWithSlash);
        Assert.Equal(busTypeWithSlash, busTypeBareDrive);
        Assert.InRange(busTypeWithSlash.Value, 1, 0x7F);
        Assert.Null(StorageBusTypeHelper.TryGetBusType(@"\\server\share"));
    }

    [Fact]
    public void JumpListService_TryStampShortcutAppUserModelId_WritesReadableVtLpwstrAndIsIdempotent()
    {
        string targetDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "TargetFolder")).FullName;
        string shortcutPath = Path.Combine(_tempRoot, "DeskBoxSample.lnk");
        ShortcutHelper.CreateOrUpdateFolderShortcut(shortcutPath, targetDir, "JumpList AUMID test");
        Assert.True(File.Exists(shortcutPath));

        const string expectedAumid = "DeskBox.Test.AppUserModelId";
        Assert.True(JumpListService.TryStampShortcutAppUserModelId(shortcutPath, expectedAumid));
        Assert.Equal(expectedAumid, ReadShortcutAppUserModelId(shortcutPath));

        // Second call hits fast-path read check without corrupting existing property.
        Assert.True(JumpListService.TryStampShortcutAppUserModelId(shortcutPath, expectedAumid));
        Assert.Equal(expectedAumid, ReadShortcutAppUserModelId(shortcutPath));
    }

    [Fact]
    public void CustomWidgetTrustStore_DetectRequestedPermissions_ScansContextMenuMenuItemsPaneActionButtonPointerAndSpans()
    {
        var manifestFromContextMenu = new CustomWidgetManifest
        {
            Id = "custom.trust.contextmenu",
            View = new CustomWidgetNodeSpec
            {
                Type = "Text",
                Text = "Benign surface",
                ContextMenu =
                [
                    new CustomWidgetMenuItemSpec(
                        Label: "More",
                        Children:
                        [
                            new CustomWidgetMenuItemSpec(
                                Label: "Write local file",
                                OnClick: [new CustomWidgetActionSpec(Type: "file.writeText", Path: "notes.txt", Text: "x")])
                        ])
                ]
            }
        };
        IReadOnlySet<CustomWidgetPermissionKind> menuPerms =
            CustomWidgetTrustStore.DetectRequestedPermissions(manifestFromContextMenu);
        Assert.Contains(CustomWidgetPermissionKind.LocalFileAccess, menuPerms);

        var manifestFromShellSubtrees = new CustomWidgetManifest
        {
            Id = "custom.trust.subtrees",
            View = new CustomWidgetNodeSpec
            {
                Type = "SplitView",
                Pane = new CustomWidgetNodeSpec
                {
                    Type = "DropDownButton",
                    MenuItems =
                    [
                        new CustomWidgetMenuItemSpec(
                            Label: "${$clipboard.text}",
                            OnClick: [new CustomWidgetActionSpec(Type: "toast.show", Message: "copied")])
                    ]
                },
                Children =
                [
                    new CustomWidgetNodeSpec
                    {
                        Type = "InfoBar",
                        ActionButton = new CustomWidgetNodeSpec
                        {
                            Type = "Button",
                            OnPointerDown = [new CustomWidgetActionSpec(Type: "http.request", Url: "http://127.0.0.1:8080/ping")]
                        }
                    },
                    new CustomWidgetNodeSpec
                    {
                        Type = "RichText",
                        Spans = [new CustomWidgetTextSpanSpec(Text: "Docs", Href: "https://deskbox.fun/docs")]
                    }
                ]
            }
        };
        IReadOnlySet<CustomWidgetPermissionKind> subtreePerms =
            CustomWidgetTrustStore.DetectRequestedPermissions(manifestFromShellSubtrees);
        Assert.Contains(CustomWidgetPermissionKind.SystemAndClipboard, subtreePerms);
        Assert.Contains(CustomWidgetPermissionKind.Notifications, subtreePerms);
        Assert.Contains(CustomWidgetPermissionKind.PublicNetwork, subtreePerms);
        Assert.Contains(CustomWidgetPermissionKind.LocalNetwork, subtreePerms);
    }

    [Fact]
    public void PromptCatalogs_AdvertiseOnlyCanonicalClipboardRuntimeTokens()
    {
        var diagnostics = new List<CustomWidgetValidationDiagnostic>();
        foreach (bool isChinese in new[] { true, false })
        {
            string archetype = CustomWidgetArchetypeCatalog.SelectPromptBlueprintBlock(
                userMessage: "clipboard utility widget",
                currentManifestJson: null,
                isChinese: isChinese);
            string tier0 = CustomWidgetCapabilityRegistry.BuildTier0DirectoryPromptBlock(isChinese: isChinese);

            Assert.DoesNotContain("lineCount", archetype, StringComparison.Ordinal);
            Assert.DoesNotContain("lineCount", tier0, StringComparison.Ordinal);

            CustomWidgetRuntimeTokens.ValidateBuiltInTokensInExpression("$clipboard.text", "$.test", diagnostics);
            CustomWidgetRuntimeTokens.ValidateBuiltInTokensInExpression("$clipboard.preview", "$.test", diagnostics);
            CustomWidgetRuntimeTokens.ValidateBuiltInTokensInExpression("$clipboard.length", "$.test", diagnostics);
            CustomWidgetRuntimeTokens.ValidateBuiltInTokensInExpression("$clipboard.lines", "$.test", diagnostics);
        }

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task TransferItemsWithResultAsync_LockedSourceFile_ThrowsNegativeWin32SharingViolationHResult()
    {
        var service = new FileService();
        string sourceDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "src")).FullName;
        string destDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "dst")).FullName;
        string lockedFile = Path.Combine(sourceDir, "locked.txt");
        File.WriteAllText(lockedFile, "locked-content");

        using var exclusiveLock = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var wrapperEx = await Assert.ThrowsAsync<FileService.FileTransferPartialFailureException>(() =>
            service.TransferItemsWithResultAsync(
                [lockedFile],
                destDir,
                move: true,
                progress: new Progress<FileService.FileTransferProgress>(_ => { })));

        var ex = Assert.IsType<IOException>(wrapperEx.InnerException);
        // ERROR_SHARING_VIOLATION (32) -> HRESULT_FROM_WIN32(32) == 0x80070020 (negative), never positive 32.
        Assert.True(ex.HResult < 0, $"Expected negative HRESULT, got 0x{ex.HResult:X8}");
        Assert.Equal(unchecked((int)0x80070020), ex.HResult);
    }

    [Fact]
    public void ReadVirtualFileDescriptors_SubDwordOrTruncatedHGlobal_ReturnsEmptyWithoutOverread()
    {
        const uint GmemMoveable = 0x0002;
        const uint GmemZeroInit = 0x0040;

        IntPtr twoByteHandle = ClipboardNativeMethods.GlobalAlloc(GmemMoveable | GmemZeroInit, 2);
        Assert.NotEqual(IntPtr.Zero, twoByteHandle);
        try
        {
            Assert.Empty(NativeDropTarget.ReadVirtualFileDescriptors(twoByteHandle));
        }
        finally
        {
            _ = ClipboardNativeMethods.GlobalFree(twoByteHandle);
        }

        IntPtr eightByteHandle = ClipboardNativeMethods.GlobalAlloc(GmemMoveable | GmemZeroInit, 8);
        Assert.NotEqual(IntPtr.Zero, eightByteHandle);
        try
        {
            IntPtr ptr = ClipboardNativeMethods.GlobalLock(eightByteHandle);
            try
            {
                Marshal.WriteInt32(ptr, 250); // Claims 250 descriptors in an 8-byte buffer.
            }
            finally
            {
                _ = ClipboardNativeMethods.GlobalUnlock(eightByteHandle);
            }

            Assert.Empty(NativeDropTarget.ReadVirtualFileDescriptors(eightByteHandle));
        }
        finally
        {
            _ = ClipboardNativeMethods.GlobalFree(eightByteHandle);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ComQueryInterfaceDelegate(IntPtr self, ref Guid riid, out IntPtr ppvObject);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int PersistFileLoadDelegate(IntPtr self, string pszFileName, uint dwMode);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ZoneIdentifierGetIdDelegate(IntPtr self, out uint pdwZone);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PropertyStoreGetValueDelegate(IntPtr self, ref PropertyKey key, IntPtr pv);

    private static TDelegate GetComSlot<TDelegate>(IntPtr instance, int slot)
        where TDelegate : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(instance);
        IntPtr fnPtr = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(fnPtr);
    }

    private static uint ReadWindowsZoneIdentifierViaCom(string filePath)
    {
        Guid clsidPersistentZoneIdentifier = new("0968E258-16C7-4DBA-AA86-462DD61E31A3");
        Guid iidPersistFile = new("0000010b-0000-0000-C000-000000000046");
        Guid iidZoneIdentifier = new("CD45F185-1B21-48E2-967B-EAD743A8914E");

        int hr = CoCreateInstance(
            ref clsidPersistentZoneIdentifier,
            IntPtr.Zero,
            1, // CLSCTX_INPROC_SERVER
            ref iidPersistFile,
            out IntPtr persistFilePtr);
        Assert.Equal(0, hr);
        Assert.NotEqual(IntPtr.Zero, persistFilePtr);

        try
        {
            int loadHr = GetComSlot<PersistFileLoadDelegate>(persistFilePtr, 5)(persistFilePtr, filePath, 0);
            Assert.Equal(0, loadHr);

            int qiHr = GetComSlot<ComQueryInterfaceDelegate>(persistFilePtr, 0)(
                persistFilePtr,
                ref iidZoneIdentifier,
                out IntPtr zoneIdPtr);
            Assert.Equal(0, qiHr);
            Assert.NotEqual(IntPtr.Zero, zoneIdPtr);

            try
            {
                int getIdHr = GetComSlot<ZoneIdentifierGetIdDelegate>(zoneIdPtr, 3)(zoneIdPtr, out uint zone);
                Assert.Equal(0, getIdHr);
                return zone;
            }
            finally
            {
                Marshal.Release(zoneIdPtr);
            }
        }
        finally
        {
            Marshal.Release(persistFilePtr);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    private static string? ReadShortcutAppUserModelId(string shortcutPath)
    {
        Guid iidPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
        var key = new PropertyKey
        {
            fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            pid = 5
        };

        int hr = JumpListNativeMethods.SHGetPropertyStoreFromParsingName(
            shortcutPath,
            IntPtr.Zero,
            0,
            ref iidPropertyStore,
            out IntPtr storePtr);
        Assert.Equal(0, hr);
        Assert.NotEqual(IntPtr.Zero, storePtr);

        try
        {
            int propVarSize = IntPtr.Size == 8 ? 24 : 16;
            IntPtr pv = Marshal.AllocCoTaskMem(propVarSize);
            try
            {
                Marshal.Copy(new byte[propVarSize], 0, pv, propVarSize);
                int getHr = GetComSlot<PropertyStoreGetValueDelegate>(storePtr, 5)(storePtr, ref key, pv);
                Assert.True(getHr >= 0);
                try
                {
                    short vt = Marshal.ReadInt16(pv, 0);
                    if (vt != 31)
                    {
                        return null;
                    }

                    IntPtr strPtr = Marshal.ReadIntPtr(pv, 8);
                    return strPtr == IntPtr.Zero ? null : Marshal.PtrToStringUni(strPtr);
                }
                finally
                {
                    _ = JumpListNativeMethods.PropVariantClear(pv);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(pv);
            }
        }
        finally
        {
            Marshal.Release(storePtr);
        }
    }
}
