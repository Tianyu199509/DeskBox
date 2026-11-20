using System.Runtime.InteropServices;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class DesktopBlankHitTestTests
{
    private const int WsOverlappedWindow = 0x00CF0000;

    private delegate IntPtr WindowProcedure(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

    // Classic DllImport on purpose: the test project is not compiled with
    // unsafe blocks for LibraryImport source generation, and the Platform
    // interop ratchet only scans src/DeskBox.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int extendedStyle,
        string className,
        string? windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WindowClass windowClass);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public WindowProcedure Procedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr BackgroundBrush;
        public string? MenuName;
        public string ClassName;
    }

    // Forwarding to DefWindowProc is required: a hand-rolled WndProc that
    // answers zero would fail WM_NCCREATE and CreateWindowExW would abort.
    private static readonly WindowProcedure DefaultProcedure =
        static (window, message, wParam, lParam) =>
            DefWindowProcW(window, message, wParam, lParam);

    [Fact]
    public void IsShellDesktopListView_RejectsForeignProcessListView()
    {
        IntPtr listView = CreateTopLevelWindow("SysListView32");
        Assert.NotEqual(IntPtr.Zero, listView);
        try
        {
            IntPtr shellWindow = Win32Helper.GetShellWindow();
            if (shellWindow == IntPtr.Zero)
            {
                // Explorer-restart fallback keeps the class-only verdict.
                Assert.True(DesktopBlankHitTest.IsShellDesktopListView(listView));
                return;
            }

            Win32Helper.GetWindowThreadProcessId(shellWindow, out uint shellProcessId);
            Win32Helper.GetWindowThreadProcessId(listView, out uint listProcessId);
            Assert.NotEqual(shellProcessId, listProcessId);
            Assert.False(DesktopBlankHitTest.IsShellDesktopListView(listView));
        }
        finally
        {
            DestroyWindow(listView);
        }
    }

    [Fact]
    public void IsShellDesktopListView_AcceptsMatchingShellProcessAndRealDesktopWhenAvailable()
    {
        Assert.False(DesktopBlankHitTest.IsShellDesktopListViewForProcessIds(
            IntPtr.Zero, new IntPtr(0x100), 1200, 1200));
        Assert.True(DesktopBlankHitTest.IsShellDesktopListViewForProcessIds(
            new IntPtr(0x200), IntPtr.Zero, 0, 4500));
        Assert.True(DesktopBlankHitTest.IsShellDesktopListViewForProcessIds(
            new IntPtr(0x200), new IntPtr(0x100), 1200, 1200));
        Assert.False(DesktopBlankHitTest.IsShellDesktopListViewForProcessIds(
            new IntPtr(0x200), new IntPtr(0x100), 1200, 9999));

        IntPtr shellWindow = Win32Helper.GetShellWindow();
        if (shellWindow != IntPtr.Zero)
        {
            IntPtr defView = Win32Helper.FindWindowEx(
                shellWindow,
                IntPtr.Zero,
                "SHELLDLL_DefView",
                null);
            IntPtr listView = defView != IntPtr.Zero
                ? Win32Helper.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null)
                : IntPtr.Zero;
            if (listView != IntPtr.Zero)
            {
                Assert.True(DesktopBlankHitTest.IsShellDesktopListView(listView));
            }
        }
    }

    [Theory]
    [InlineData("WorkerW")]
    [InlineData("Progman")]
    public void IsDesktopHostWindow_RejectsForeignRootWindows(string className)
    {
        IntPtr window = CreateTopLevelWindow(className);
        Assert.NotEqual(IntPtr.Zero, window);
        try
        {
            IntPtr shellWindow = Win32Helper.GetShellWindow();
            if (shellWindow == IntPtr.Zero)
            {
                // Explorer-restart fallback keeps the class-only verdict.
                Assert.True(DesktopBlankHitTest.IsDesktopHostWindow(window));
                return;
            }

            Assert.False(DesktopBlankHitTest.IsDesktopHostWindow(window));
        }
        finally
        {
            DestroyWindow(window);
        }
    }

    [Fact]
    public void Seams_RejectMissingWindows()
    {
        Assert.False(DesktopBlankHitTest.IsShellDesktopListView(IntPtr.Zero));
        Assert.False(DesktopBlankHitTest.IsDesktopHostWindow(IntPtr.Zero));
    }

    [Fact]
    public void HitTest_SourceRestrictsClassMatchesToTheShellHost()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/DesktopBlankHitTest.cs"));

        Assert.Contains("GetShellWindow()", source, StringComparison.Ordinal);
        Assert.Contains("GetWindowThreadProcessId", source, StringComparison.Ordinal);
        Assert.Contains("GetAncestor", source, StringComparison.Ordinal);
        Assert.Contains("GA_ROOT", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HitTest_DesktopHostBranchIsNoLongerUnconditional()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/DesktopBlankHitTest.cs"));
        string walk = ExtractMethod(
            source,
            "public static bool IsBlankDesktopPoint",
            "internal static bool IsShellDesktopListView");

        Assert.Contains("IsDesktopHostWindow(current)", walk, StringComparison.Ordinal);
        Assert.Contains("IsShellDesktopListView(listView)", walk, StringComparison.Ordinal);
        Assert.DoesNotContain("return true", walk, StringComparison.Ordinal);
    }

    [Fact]
    public void HitTest_ValidatesListViewIdentityBeforeCrossProcessMemory()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/DesktopBlankHitTest.cs"));
        int seamCall = source.IndexOf(
            "IsShellDesktopListView(listView)",
            StringComparison.Ordinal);
        int openProcess = source.IndexOf(
            "RemoteProcessMemoryNativeMethods.OpenProcess",
            StringComparison.Ordinal);

        Assert.True(seamCall >= 0);
        Assert.True(openProcess > seamCall);
    }

    private static IntPtr CreateTopLevelWindow(string className)
    {
        var windowClass = new WindowClass
        {
            Procedure = DefaultProcedure,
            Instance = Win32Helper.GetModuleHandle(null),
            ClassName = className
        };

        // A global class (for example comctl32's SysListView32) may already
        // provide the name; the local registration only covers its absence.
        RegisterClassW(ref windowClass);
        return CreateWindowExW(
            0,
            className,
            null,
            WsOverlappedWindow,
            0,
            0,
            160,
            120,
            IntPtr.Zero,
            IntPtr.Zero,
            windowClass.Instance,
            IntPtr.Zero);
    }

    private static string ExtractMethod(string source, string startToken, string endToken)
    {
        int start = source.IndexOf(startToken, StringComparison.Ordinal);
        int end = source.IndexOf(endToken, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }
}
