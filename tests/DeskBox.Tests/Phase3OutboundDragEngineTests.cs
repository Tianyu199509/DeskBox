using System.Text.Json;
using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Tests;

public sealed class Phase3OutboundDragEngineTests
{
    [Theory]
    [InlineData(1u)] // DROPEFFECT_COPY
    [InlineData(2u)] // DROPEFFECT_MOVE
    [InlineData(4u)] // DROPEFFECT_LINK
    public void GuardDataObject_EnumFormatEtc_ExposesServiceablePreferredDropEffect_AndBlocksCompletionFormats(
        uint preferredEffect)
    {
        string root = CreateTempTestDir();
        string filePath = Path.Combine(root, "outbound.txt");
        File.WriteAllText(filePath, "phase3-guard");
        nint inner = NativeShellFileDragProvider.CreateShellDataObject([filePath]);
        nint guard = FileDragSourceGuardDataObject.CreateInterfacePointer(inner);

        try
        {
            var receiver = new NativeOleDataObject(guard);
            ushort preferredId = Win32Helper.GetRegisteredClipboardFormat("Preferred DropEffect");
            ushort performedId = Win32Helper.GetRegisteredClipboardFormat("Performed DropEffect");
            ushort logicalPerformedId = Win32Helper.GetRegisteredClipboardFormat("Logical Performed DropEffect");
            ushort pasteSucceededId = Win32Helper.GetRegisteredClipboardFormat("Paste Succeeded");
            ushort targetClsidId = Win32Helper.GetRegisteredClipboardFormat("TargetCLSID");
            ushort shellIdListId = Win32Helper.GetRegisteredClipboardFormat("Shell IDList Array");

            // 1. Before setting any preference, Preferred DropEffect must NOT be enumerated.
            HashSet<ushort> initialFormats = receiver.EnumerateGetFormatIds();
            Assert.Contains((ushort)15, initialFormats); // CF_HDROP
            Assert.Contains(shellIdListId, initialFormats);
            Assert.DoesNotContain(preferredId, initialFormats);

            // 2. Write a serviceable single-bit Preferred DropEffect and forged completion receipts.
            WriteDwordFormat(guard, "Preferred DropEffect", preferredEffect);
            WriteDwordFormat(guard, "Performed DropEffect", 2u);
            WriteDwordFormat(guard, "Paste Succeeded", 2u);

            HashSet<ushort> activeFormats = receiver.EnumerateGetFormatIds();
            Assert.Contains((ushort)15, activeFormats);
            Assert.Contains(shellIdListId, activeFormats);
            Assert.Contains(preferredId, activeFormats);
            Assert.DoesNotContain(performedId, activeFormats);
            Assert.DoesNotContain(logicalPerformedId, activeFormats);
            Assert.DoesNotContain(pasteSucceededId, activeFormats);
            Assert.DoesNotContain(targetClsidId, activeFormats);

            // 3. Clearing with DROPEFFECT_NONE (0) or multi-bit mask (7) removes Preferred DropEffect from enumeration.
            WriteDwordFormat(guard, "Preferred DropEffect", 0u);
            HashSet<ushort> clearedFormats = receiver.EnumerateGetFormatIds();
            Assert.DoesNotContain(preferredId, clearedFormats);

            WriteDwordFormat(guard, "Preferred DropEffect", 7u);
            HashSet<ushort> maskFormats = receiver.EnumerateGetFormatIds();
            Assert.DoesNotContain(preferredId, maskFormats);

            // 4. Null enumerator out-pointer returns E_POINTER cleanly.
            Assert.Equal(unchecked((int)0x80004003), receiver.EnumFormatEtcRaw(1u, 0));
        }
        finally
        {
            FileDragSourceGuardDataObject.ReleaseInterfacePointer(guard);
            NativeShellFileDragProvider.ReleaseInterface(inner);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MultiParentShellDataObject_RoundTripsNativeFormats_AndPreservesAllSourcesUnderMoveReceipt()
    {
        string root = CreateTempTestDir();
        string dirA = Directory.CreateDirectory(Path.Combine(root, "DirA")).FullName;
        string dirB = Directory.CreateDirectory(Path.Combine(root, "DirB")).FullName;
        string fileA = Path.Combine(dirA, "alpha.txt");
        string fileB = Path.Combine(dirB, "beta.lnk");
        File.WriteAllText(fileA, "alpha-content");
        File.WriteAllBytes(fileB, [0x4C, 0x00, 0x00, 0x00, 1, 2, 3, 4]);

        nint inner = NativeShellFileDragProvider.CreateShellDataObject([fileA, fileB]);
        Assert.NotEqual(0, inner);
        nint guard = FileDragSourceGuardDataObject.CreateInterfacePointer(inner);

        try
        {
            var receiver = new NativeOleDataObject(guard);
            WriteDwordFormat(guard, "Preferred DropEffect", 2u);
            WriteDwordFormat(guard, "Performed DropEffect", 2u);
            WriteDwordFormat(guard, "Logical Performed DropEffect", 2u);

            HashSet<ushort> enumerated = receiver.EnumerateGetFormatIds();
            Assert.Contains((ushort)15, enumerated); // CF_HDROP
            Assert.Contains(Win32Helper.GetRegisteredClipboardFormat("Shell IDList Array"), enumerated);
            Assert.Contains(Win32Helper.GetRegisteredClipboardFormat("Preferred DropEffect"), enumerated);

            string[] droppedPaths = ReadHDropPaths(receiver);
            Assert.Equal(2, droppedPaths.Length);
            Assert.Contains(fileA, droppedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(fileB, droppedPaths, StringComparer.OrdinalIgnoreCase);

            Assert.True(File.Exists(fileA));
            Assert.True(File.Exists(fileB));
        }
        finally
        {
            FileDragSourceGuardDataObject.ReleaseInterfacePointer(guard);
            NativeShellFileDragProvider.ReleaseInterface(inner);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DecisionAAndA1_Win11RestoresLinkCapability_AndWin10FollowWindowsDefaultsToSilentCopyWithGuide()
    {
        // Win11 FollowWindows: Copy | Move (0x3 != 0x7) with None preference
        // so WinUI 3 C++ writes AllowedOperations without collapsing to 0x0.
        Assert.Equal(
            DataPackageOperation.Copy | DataPackageOperation.Move,
            FileItemDragPackage.ResolveDragOutAllowedOperations(
                SettingsService.ManagedDragOutActionFollowWindows,
                isWindows11OrLater: true));
        Assert.Equal(
            DataPackageOperation.None,
            FileItemDragPackage.ResolveDragOutPreferredOperation(
                SettingsService.ManagedDragOutActionFollowWindows));

        // Win10 FollowWindows (Decision A1): single-bit silent Copy with None preference
        DataPackageOperation win10FollowAllowed =
            FileItemDragPackage.ResolveDragOutAllowedOperations(
                SettingsService.ManagedDragOutActionFollowWindows,
                isWindows11OrLater: false);
        Assert.Equal(DataPackageOperation.Copy, win10FollowAllowed);
        Assert.Equal(
            "Widget.DragOutTip.AfterCopy.NoModifiers",
            FileSurfaceContent.GetDragOutResultHintKey(DataPackageOperation.Copy, win10FollowAllowed));

        // Win10 Desktop & Explorer folder silent Copy triggers HintIdWin10DefaultCopy once,
        // while Ctrl-drag and protected sources do not.
        var desktopSnapshot = new DropReleaseSnapshot(
            DropTargetWindowKind.ExplorerFileView,
            EscapePressed: false,
            default,
            IsDesktopShellView: true);
        var folderSnapshot = new DropReleaseSnapshot(
            DropTargetWindowKind.ExplorerFileView,
            EscapePressed: false,
            default,
            IsDesktopShellView: false);

        Assert.Equal(
            DesktopToastHudService.HintIdWin10DefaultCopy,
            FileSurfaceContent.EvaluateContextualDragOutGuideId(
                DataPackageOperation.Copy,
                SettingsService.ManagedDragOutActionFollowWindows,
                isWindows11OrLater: false,
                desktopSnapshot,
                handledAsStackMembership: false,
                hasStorageItems: true,
                controlHeld: false,
                crossVolumeFromDesktop: false,
                protectedCopyOnlySource: false));
        Assert.Equal(
            DesktopToastHudService.HintIdWin10DefaultCopy,
            FileSurfaceContent.EvaluateContextualDragOutGuideId(
                DataPackageOperation.Copy,
                SettingsService.ManagedDragOutActionFollowWindows,
                isWindows11OrLater: false,
                folderSnapshot,
                handledAsStackMembership: false,
                hasStorageItems: true,
                controlHeld: false,
                crossVolumeFromDesktop: false,
                protectedCopyOnlySource: false));
        Assert.Null(FileSurfaceContent.EvaluateContextualDragOutGuideId(
            DataPackageOperation.Copy,
            SettingsService.ManagedDragOutActionFollowWindows,
            isWindows11OrLater: false,
            desktopSnapshot,
            handledAsStackMembership: false,
            hasStorageItems: true,
            controlHeld: true,
            crossVolumeFromDesktop: false,
            protectedCopyOnlySource: false));
        Assert.Null(FileSurfaceContent.EvaluateContextualDragOutGuideId(
            DataPackageOperation.Copy,
            SettingsService.ManagedDragOutActionFollowWindows,
            isWindows11OrLater: false,
            desktopSnapshot,
            handledAsStackMembership: false,
            hasStorageItems: true,
            controlHeld: false,
            crossVolumeFromDesktop: false,
            protectedCopyOnlySource: true));
    }

    [Fact]
    public void AllThirteenLocales_IncludeWin10DefaultCopyHudAndUpdatedWin10Note()
    {
        string stringsDir = TestPaths.FromRepository("src/DeskBox/Strings");
        string[] localeFiles = Directory.GetFiles(stringsDir, "*.json");
        Assert.Equal(13, localeFiles.Length);

        foreach (string file in localeFiles)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            Assert.True(
                doc.RootElement.TryGetProperty("Widget.DragHud.Win10DefaultCopy", out JsonElement hudEl) &&
                !string.IsNullOrWhiteSpace(hudEl.GetString()),
                $"Missing Widget.DragHud.Win10DefaultCopy in {Path.GetFileName(file)}");
            Assert.True(
                doc.RootElement.TryGetProperty("Settings.DragOutAction.Win10Note", out JsonElement noteEl) &&
                !string.IsNullOrWhiteSpace(noteEl.GetString()),
                $"Missing Settings.DragOutAction.Win10Note in {Path.GetFileName(file)}");
        }
    }

    private static void WriteDwordFormat(nint dataObject, string formatName, uint value)
    {
        var format = new NativeFormatEtc
        {
            ClipboardFormat = Win32Helper.GetRegisteredClipboardFormat(formatName),
            Aspect = 1,
            Index = -1,
            MediumType = 1,
        };
        Assert.True(Win32Helper.TryCreateGlobalMemory(BitConverter.GetBytes(value), out nint memory));
        var medium = new NativeStorageMedium { MediumType = 1, Content = memory };
        int hr = new NativeOleDataObject(dataObject).SetData(ref format, ref medium, release: true);
        if (hr < 0)
        {
            Win32Helper.ReleaseStorageMedium(ref medium);
        }

        Assert.Equal(0, hr);
    }

    private static string[] ReadHDropPaths(NativeOleDataObject receiver)
    {
        var hdropFormat = new NativeFormatEtc
        {
            ClipboardFormat = 15, // CF_HDROP
            Aspect = 1,
            Index = -1,
            MediumType = 1,
        };
        Assert.Equal(0, receiver.GetData(ref hdropFormat, out NativeStorageMedium medium));
        try
        {
            uint count = Win32Helper.DragQueryFile(medium.Content, 0xFFFFFFFF, null, 0);
            var paths = new string[count];
            for (uint i = 0; i < count; i++)
            {
                uint length = Win32Helper.DragQueryFile(medium.Content, i, null, 0);
                var sb = new System.Text.StringBuilder((int)length + 1);
                Win32Helper.DragQueryFile(medium.Content, i, sb, (uint)sb.Capacity);
                paths[i] = sb.ToString();
            }

            return paths;
        }
        finally
        {
            Win32Helper.ReleaseStorageMedium(ref medium);
        }
    }

    private static string CreateTempTestDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"))).FullName;
}
