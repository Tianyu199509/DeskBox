using System.Runtime.InteropServices;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DeskBox.Tests;

public sealed class FileDragSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ErroneousMoveCompletion_KeepsNativeFileAndShortcutSources(bool managedShortcut)
    {
        string root = CreateTestDirectory();
        string path = Path.Combine(root, managedShortcut ? "source.lnk" : "source.txt");
        byte[] original = [0x4C, 0, 0, 0, 1, 2, 3, 4];
        File.WriteAllBytes(path, original);
        try
        {
            var package = new DataPackage();
            Assert.True(FileItemDragPackage.TryPrepare(package,
                [new WidgetItem { Path = path }], "test-source", _ => [], _ => "source",
                out var result));
            Assert.True(result.UsesNativeShellDataObject);
            Assert.Equal(DataPackageOperation.None, package.GetView().RequestedOperation);

            // A receiver lies: it reports Move without saving any copy.
            package.GetView().ReportOperationCompleted(DataPackageOperation.Move);
            await Task.Delay(150);
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ErroneousMoveCompletion_KeepsStorageItemsFallbackSources()
    {
        string root = CreateTestDirectory();
        string first = Path.Combine(root, "first.txt");
        string second = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "other")).FullName, "second.txt");
        File.WriteAllText(first, "first original");
        File.WriteAllText(second, "second original");
        try
        {
            IStorageItem[] items = [await StorageFile.GetFileFromPathAsync(first), await StorageFile.GetFileFromPathAsync(second)];
            var package = new DataPackage();
            Assert.True(FileItemDragPackage.TryPrepare(package,
                [new WidgetItem { Path = first }, new WidgetItem { Path = second }],
                "test-source", _ => items, _ => "2 files", out var result, allowNativeShellDataObject: false));
            Assert.False(result.UsesNativeShellDataObject);
            Assert.Equal(DataPackageOperation.None, package.GetView().RequestedOperation);
            package.GetView().ReportOperationCompleted(DataPackageOperation.Move);
            await Task.Delay(150);
            Assert.Equal("first original", File.ReadAllText(first));
            Assert.Equal("second original", File.ReadAllText(second));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("Performed DropEffect", false)]
    [InlineData("Performed DropEffect", true)]
    [InlineData("Logical Performed DropEffect", true)]
    [InlineData("Paste Succeeded", true)]
    [InlineData("TargetCLSID", true)]
    public void NativeCompletionReceipt_DoesNotReachShellSource(string formatName, bool release)
    {
        string root = CreateTestDirectory();
        string path = Path.Combine(root, "source.txt");
        File.WriteAllText(path, "must survive");
        nint inner = NativeShellFileDragProvider.CreateShellDataObject([path]);
        nint guard = FileDragSourceGuardDataObject.CreateInterfacePointer(inner);
        try
        {
            var format = Format(formatName);
            // Recycle Bin identity is a cleanup request even without Move.
            byte[] payload = formatName == "TargetCLSID"
                ? new Guid("645FF040-5081-101B-9F08-00AA002F954E").ToByteArray()
                : BitConverter.GetBytes(2u);
            Assert.True(Win32Helper.TryCreateGlobalMemory(payload, out nint memory));
            var medium = new NativeStorageMedium { MediumType = 1, Content = memory };
            int hr = new NativeOleDataObject(guard).SetData(ref format, ref medium, release);
            if (!release || hr < 0)
            {
                // Failed calls never transfer ownership; fRelease=false never does.
                Win32Helper.ReleaseStorageMedium(ref medium);
            }
            Assert.Equal(0, hr);
            Assert.NotEqual(0, new NativeOleDataObject(inner).QueryGetData(ref format));
            Assert.NotEqual(0, new NativeOleDataObject(guard).QueryGetData(ref format));
            Assert.Equal("must survive", File.ReadAllText(path));
        }
        finally
        {
            FileDragSourceGuardDataObject.ReleaseInterfacePointer(guard);
            NativeShellFileDragProvider.ReleaseInterface(inner);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void NativePreference_PassesThrough_AndFilePayloadRemainsReadable()
    {
        string root = CreateTestDirectory();
        string path = Path.Combine(root, "source.txt");
        File.WriteAllText(path, "content");
        nint inner = NativeShellFileDragProvider.CreateShellDataObject([path]);
        nint guard = FileDragSourceGuardDataObject.CreateInterfacePointer(inner);
        try
        {
            var receiver = new NativeOleDataObject(guard);
            var format = Format("Preferred DropEffect");
            Assert.True(Win32Helper.TryCreateGlobalMemory(BitConverter.GetBytes(2u), out nint memory));
            var medium = new NativeStorageMedium { MediumType = 1, Content = memory };
            try
            {
                // A single-effect preferred value is stored verbatim and
                // served back on read.
                Assert.Equal(0, receiver.SetData(ref format, ref medium, false));
                Assert.Equal(2, ReadDword(memory));
            }
            finally { Win32Helper.ReleaseStorageMedium(ref medium); }

            Assert.Equal(0, receiver.GetData(ref format, out var preferred));
            try { Assert.Equal(2, ReadDword(preferred.Content)); }
            finally { Win32Helper.ReleaseStorageMedium(ref preferred); }

            format.ClipboardFormat = 15; // CF_HDROP
            Assert.Equal(0, receiver.GetData(ref format, out var files));
            Win32Helper.ReleaseStorageMedium(ref files);
        }
        finally
        {
            FileDragSourceGuardDataObject.ReleaseInterfacePointer(guard);
            NativeShellFileDragProvider.ReleaseInterface(inner);
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(0u)]   // DROPEFFECT_NONE: the engine's "no preference"
    [InlineData(3u)]   // Copy|Move mask: multi-effect spells "no preference"
    [InlineData(5u)]   // Copy|Link mask
    public void NativePreference_UnserviceableValueIsNeverAnswered(uint written)
    {
        string root = CreateTestDirectory();
        string path = Path.Combine(root, "source.txt");
        File.WriteAllText(path, "content");
        nint inner = NativeShellFileDragProvider.CreateShellDataObject([path]);
        nint guard = FileDragSourceGuardDataObject.CreateInterfacePointer(inner);
        try
        {
            var receiver = new NativeOleDataObject(guard);
            var format = Format("Preferred DropEffect");
            Assert.True(Win32Helper.TryCreateGlobalMemory(
                BitConverter.GetBytes(written), out nint memory));
            var medium = new NativeStorageMedium { MediumType = 1, Content = memory };
            try
            {
                Assert.Equal(0, receiver.SetData(ref format, ref medium, false));
            }
            finally { Win32Helper.ReleaseStorageMedium(ref medium); }

            // "Format present but value 0/multi-bit" is indistinguishable from
            // a preference for some Win10/third-party targets: the guard must
            // keep the format absent, exactly like a Shell source without a
            // preference. A stale earlier value must also be cleared.
            Assert.NotEqual(0, receiver.QueryGetData(ref format));
            format.ClipboardFormat = 15; // CF_HDROP still served
            Assert.Equal(0, receiver.GetData(ref format, out var files));
            Win32Helper.ReleaseStorageMedium(ref files);
        }
        finally
        {
            FileDragSourceGuardDataObject.ReleaseInterfacePointer(guard);
            NativeShellFileDragProvider.ReleaseInterface(inner);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void NativePreference_UnserviceableValueClearsAnEarlierPreference()
    {
        string root = CreateTestDirectory();
        string path = Path.Combine(root, "source.txt");
        File.WriteAllText(path, "content");
        nint inner = NativeShellFileDragProvider.CreateShellDataObject([path]);
        nint guard = FileDragSourceGuardDataObject.CreateInterfacePointer(inner);
        try
        {
            var receiver = new NativeOleDataObject(guard);
            var format = Format("Preferred DropEffect");
            Assert.True(Win32Helper.TryCreateGlobalMemory(
                BitConverter.GetBytes(2u), out nint first));
            var medium = new NativeStorageMedium { MediumType = 1, Content = first };
            try
            {
                Assert.Equal(0, receiver.SetData(ref format, ref medium, false));
            }
            finally { Win32Helper.ReleaseStorageMedium(ref medium); }

            // The engine overwrites the preference with DROPEFFECT_NONE: the
            // wrapper must stop answering the earlier single-effect value.
            Assert.True(Win32Helper.TryCreateGlobalMemory(
                BitConverter.GetBytes(0u), out nint second));
            medium = new NativeStorageMedium { MediumType = 1, Content = second };
            try
            {
                Assert.Equal(0, receiver.SetData(ref format, ref medium, false));
            }
            finally { Win32Helper.ReleaseStorageMedium(ref medium); }

            Assert.NotEqual(0, receiver.QueryGetData(ref format));
        }
        finally
        {
            FileDragSourceGuardDataObject.ReleaseInterfacePointer(guard);
            NativeShellFileDragProvider.ReleaseInterface(inner);
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false, false, "Move")]
    [InlineData(true, false, "Copy")]
    [InlineData(false, true, "Move")]
    public void InternalTransfer_KeepsIntentWithoutGrantingOleMove(bool control, bool shift, string expected)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.None };
        package.Properties[DeskBoxDragData.InternalFileDragTokenProperty] = DeskBoxDragData.InternalFileDragToken;
        package.Properties[DeskBoxDragData.SourcePathsProperty] = new[] { @"C:\source.txt" };
        var view = package.GetView();
        var operations = DeskBoxDragData.GetFileTransferOperations(view,
            DataPackageOperation.Copy | DataPackageOperation.Move);
        Assert.Equal(expected, FileDropIntentPolicy.ResolveMappedTransfer(true, false, control, shift,
            defaultMove: true, canCopy: operations.HasFlag(DataPackageOperation.Copy),
            canMove: operations.HasFlag(DataPackageOperation.Move)).ToString());
        Assert.Equal(DataPackageOperation.Copy,
            DeskBoxDragData.ResolveFileDragFeedbackOperation(view, DataPackageOperation.Move));
        Assert.Equal(DataPackageOperation.Copy, DeskBoxDragData.GetFileAssociationOperation(view));
        Assert.Equal(DataPackageOperation.None,
            DeskBoxDragData.ResolveFileDragFeedbackOperation(view, DataPackageOperation.None));
        var external = new DataPackage { RequestedOperation = DataPackageOperation.None };
        Assert.Equal(
            DataPackageOperation.Copy | DataPackageOperation.Move,
            DeskBoxDragData.GetFileTransferOperations(external.GetView(),
                DataPackageOperation.Copy | DataPackageOperation.Move));
    }

    private static NativeFormatEtc Format(string name) => new()
    {
        ClipboardFormat = Win32Helper.GetRegisteredClipboardFormat(name),
        Aspect = 1, Index = -1, MediumType = 1
    };

    private static int ReadDword(nint memory)
    {
        nint pointer = OleDropTargetNativeMethods.GlobalLock(memory);
        Assert.NotEqual(0, pointer);
        try { return Marshal.ReadInt32(pointer); }
        finally { OleDropTargetNativeMethods.GlobalUnlock(memory); }
    }

    private static string CreateTestDirectory() => Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"))).FullName;
}
