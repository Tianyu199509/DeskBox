using System.Runtime.InteropServices;
using DeskBox.Platform;
using DeskBox.Helpers;

namespace DeskBox.Tests;

public sealed class NativeDropDescriptionWriterTests
{
    private const int DvEFormatEtc = unchecked((int)0x80040064);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint memory);

    [Theory]
    [InlineData(NativeDropEffectPolicy.Copy, 1, "复制到%1", "我的桌面")]
    [InlineData(NativeDropEffectPolicy.Move, 2, "移动到%1", "综合")]
    public void ApplyWritesTheShellDropDescriptionPayload(
        uint effect,
        int expectedImageType,
        string expectedMessage,
        string expectedInsert)
    {
        using var fake = new FakeComObject(slotCount: 8);
        int actualImageType = int.MinValue;
        string? actualMessage = null;
        string? actualInsert = null;
        ushort actualFormat = 0;
        int actualRelease = 0;
        GetDataCallback getData = (
            nint _,
            ref NativeFormatEtc _,
            out NativeStorageMedium medium) =>
        {
            medium = default;
            return DvEFormatEtc;
        };
        SetDataCallback setData = (
            nint _,
            ref NativeFormatEtc format,
            ref NativeStorageMedium medium,
            int release) =>
        {
            actualFormat = format.ClipboardFormat;
            actualRelease = release;
            Assert.Equal((uint)1, medium.MediumType);
            nint payload = GlobalLock(medium.Content);
            Assert.NotEqual(0, payload);
            try
            {
                actualImageType = Marshal.ReadInt32(payload);
                actualMessage = Marshal.PtrToStringUni(payload + sizeof(int));
                actualInsert = Marshal.PtrToStringUni(
                    payload + sizeof(int) + (260 * sizeof(char)));
            }
            finally
            {
                GlobalUnlock(medium.Content);
                Assert.Equal(0, GlobalFree(medium.Content));
                medium.Content = 0;
            }

            return 0;
        };
        fake.SetSlot(3, getData);
        fake.SetSlot(7, setData);

        bool applied = NativeDropDescriptionWriter.TryApply(
            fake.Pointer,
            effect,
            new NativeDropDescriptionText(
                expectedMessage,
                expectedInsert));

        Assert.True(applied);
        Assert.NotEqual(0, actualFormat);
        Assert.Equal(1, actualRelease);
        Assert.Equal(expectedImageType, actualImageType);
        Assert.Equal(expectedMessage, actualMessage);
        Assert.Equal(expectedInsert, actualInsert);
    }

    [Fact]
    public void ClearWritesDropImageInvalidAndEmptyText()
    {
        using var fake = new FakeComObject(slotCount: 8);
        int actualImageType = int.MinValue;
        string? actualMessage = null;
        string? actualInsert = null;
        GetDataCallback getData = (
            nint _,
            ref NativeFormatEtc _,
            out NativeStorageMedium medium) =>
        {
            medium = default;
            return DvEFormatEtc;
        };
        SetDataCallback setData = (
            nint _,
            ref NativeFormatEtc _,
            ref NativeStorageMedium medium,
            int _) =>
        {
            nint payload = GlobalLock(medium.Content);
            Assert.NotEqual(0, payload);
            try
            {
                actualImageType = Marshal.ReadInt32(payload);
                actualMessage = Marshal.PtrToStringUni(payload + sizeof(int));
                actualInsert = Marshal.PtrToStringUni(
                    payload + sizeof(int) + (260 * sizeof(char)));
            }
            finally
            {
                GlobalUnlock(medium.Content);
                Assert.Equal(0, GlobalFree(medium.Content));
                medium.Content = 0;
            }

            return 0;
        };
        fake.SetSlot(3, getData);
        fake.SetSlot(7, setData);

        bool cleared = NativeDropDescriptionWriter.TryClear(fake.Pointer);

        Assert.True(cleared);
        Assert.Equal(-1, actualImageType);
        Assert.Equal(string.Empty, actualMessage);
        Assert.Equal(string.Empty, actualInsert);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDataCallback(
        nint self,
        ref NativeFormatEtc format,
        out NativeStorageMedium medium);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetDataCallback(
        nint self,
        ref NativeFormatEtc format,
        ref NativeStorageMedium medium,
        int release);

    private sealed class FakeComObject : IDisposable
    {
        private readonly List<Delegate> _callbacks = [];
        private readonly nint _vtable;

        public FakeComObject(int slotCount)
        {
            _vtable = Marshal.AllocHGlobal(checked(slotCount * IntPtr.Size));
            Pointer = Marshal.AllocHGlobal(IntPtr.Size);
            for (int slot = 0; slot < slotCount; slot++)
            {
                Marshal.WriteIntPtr(_vtable, slot * IntPtr.Size, 0);
            }

            Marshal.WriteIntPtr(Pointer, _vtable);
        }

        public nint Pointer { get; }

        public void SetSlot<TDelegate>(int slot, TDelegate callback)
            where TDelegate : Delegate
        {
            _callbacks.Add(callback);
            Marshal.WriteIntPtr(
                _vtable,
                checked(slot * IntPtr.Size),
                Marshal.GetFunctionPointerForDelegate(callback));
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Pointer);
            Marshal.FreeHGlobal(_vtable);
            GC.KeepAlive(_callbacks);
        }
    }
}
