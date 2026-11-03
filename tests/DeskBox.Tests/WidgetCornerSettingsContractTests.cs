using System.Runtime.InteropServices;
using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetCornerSettingsContractTests
{
    [Fact]
    public void CornerSelector_OffersLargeRoundSmallAndSquareWithLargeFirst()
    {
        string editor = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Features/Appearance/AppearanceSettingsViewModel.cs"));

        Assert.Contains(
            "WidgetCornerKinds.Large,\n        WidgetCornerKinds.Round,\n        WidgetCornerKinds.Small,\n        WidgetCornerKinds.Square",
            editor.Replace("\r\n", "\n"),
            StringComparison.Ordinal);
        Assert.DoesNotContain("CornerDefault", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.Corner.Default", editor, StringComparison.Ordinal);
    }

    [Fact]
    public void CornerSelector_LabelKeysAlignWithTheValueArray()
    {
        // The label keys zip onto the Large,Round,Small,Square value array by index;
        // swapping the key order made "Large radius" apply Square and vice
        // versa, so pin the alignment.
        string editor = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Features/Appearance/AppearanceSettingsViewModel.cs"));

        int large = editor.IndexOf("\"Settings.Corner.Large\"", StringComparison.Ordinal);
        int round = editor.IndexOf("\"Settings.Corner.Round\"", StringComparison.Ordinal);
        int small = editor.IndexOf("\"Settings.Corner.Small\"", StringComparison.Ordinal);
        int square = editor.IndexOf("\"Settings.Corner.Square\"", StringComparison.Ordinal);

        Assert.True(large >= 0 && round >= 0 && small >= 0 && square >= 0,
            "The corner option label keys are missing.");
        Assert.True(large < round && round < small && small < square,
            "Corner label keys must stay index-aligned with the Large,Round,Small,Square value array.");
    }

    [Fact]
    public void CornerLocalization_DefinesAllFourTiersAndOmitsLegacyDefaultInEveryLocale()
    {
        string stringsRoot = TestPaths.FromRepository("src/DeskBox/Strings");
        foreach (string file in Directory.EnumerateFiles(stringsRoot, "*.json"))
        {
            string json = File.ReadAllText(file);
            Assert.DoesNotContain("\"Settings.Corner.Default\"", json, StringComparison.Ordinal);

            using var document = System.Text.Json.JsonDocument.Parse(json);
            foreach (string key in new[]
            {
                "Settings.Corner.Large",
                "Settings.Corner.Round",
                "Settings.Corner.Small",
                "Settings.Corner.Square"
            })
            {
                Assert.True(
                    document.RootElement.TryGetProperty(key, out var element) &&
                    !string.IsNullOrWhiteSpace(element.GetString()),
                    $"Missing or empty '{key}' in {Path.GetFileName(file)}.");
            }
        }
    }

    [Fact]
    public void ShadowlessHosting_UsesAntiAliasedMaskedBackdropsWithoutOuterGridDoubleClipping()
    {
        string win32 = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Platform/Win32Helper.cs"));
        string maskService = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Services/RoundedBackdropMaskService.cs"));
        string backdropBridge = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Services/WidgetMaterialSystemBackdrop.cs"));
        string shellXaml = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Controls/WidgetShell.xaml"));
        string shellCode = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Controls/WidgetShell.xaml.cs"));
        string widgetBase = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        string searchPopup = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/SearchPopupWindow.xaml.cs"));
        string stackPopover = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/StackPopoverHostWindow.cs"));

        Assert.Contains("DisableWindowFrameShadow(", win32, StringComparison.Ordinal);
        Assert.Contains("CreateMaskBrush()", maskService, StringComparison.Ordinal);
        Assert.Contains("CreateNineGridBrush()", maskService, StringComparison.Ordinal);
        Assert.Contains("MaskedBackdropTargetProxy", backdropBridge, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MaterialBackdropSurface\"", shellXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ContentCornerClipHost\"", shellXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ShellRoot.CornerRadius = outerCornerRadius", shellCode, StringComparison.Ordinal);
        Assert.DoesNotContain("RootGrid.CornerRadius = outerCornerRadius", searchPopup, StringComparison.Ordinal);
        Assert.Contains("DisableWindowFrameShadow(HWnd)", widgetBase, StringComparison.Ordinal);
        Assert.Contains("DisableWindowFrameShadow(_hwnd)", searchPopup, StringComparison.Ordinal);
        Assert.Contains("DisableWindowFrameShadow(WindowHandle)", stackPopover, StringComparison.Ordinal);

        int syncMethodStart = shellCode.IndexOf("private void SyncContentCornerClip()", StringComparison.Ordinal);
        Assert.True(syncMethodStart >= 0, "Expected SyncContentCornerClip() in WidgetShell.xaml.cs.");
        int syncMethodEnd = shellCode.IndexOf("private void SyncMaterialBackdropCornerRadius()", syncMethodStart, StringComparison.Ordinal);
        Assert.True(syncMethodEnd > syncMethodStart);
        string syncBody = shellCode[(syncMethodStart + "private void SyncContentCornerClip()".Length)..syncMethodEnd];
        Assert.DoesNotContain("SyncContentCornerClip(", syncBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4.0)]
    [InlineData(8.0)]
    [InlineData(16.0)]
    [InlineData(16.5)]
    public void RoundedBackdropMaskCoverage_IsSymmetricAndProducesSmoothAntiAliasedRamp(double radiusPx)
    {
        int inset = (int)Math.Ceiling(radiusPx) + 1;
        int texSize = (inset * 2) + 4;

        // Exterior corners must be completely transparent.
        Assert.Equal(0, RoundedBackdropMaskService.ComputeCornerCoverageByte(0, 0, texSize, radiusPx));
        Assert.Equal(0, RoundedBackdropMaskService.ComputeCornerCoverageByte(texSize - 1, 0, texSize, radiusPx));
        Assert.Equal(0, RoundedBackdropMaskService.ComputeCornerCoverageByte(0, texSize - 1, texSize, radiusPx));
        Assert.Equal(0, RoundedBackdropMaskService.ComputeCornerCoverageByte(texSize - 1, texSize - 1, texSize, radiusPx));

        // Center and straight-edge midpoints must be completely opaque.
        int mid = texSize / 2;
        Assert.Equal(255, RoundedBackdropMaskService.ComputeCornerCoverageByte(mid, mid, texSize, radiusPx));
        Assert.Equal(255, RoundedBackdropMaskService.ComputeCornerCoverageByte(mid, 0, texSize, radiusPx));
        Assert.Equal(255, RoundedBackdropMaskService.ComputeCornerCoverageByte(0, mid, texSize, radiusPx));

        // All 4 corners must be bit-for-bit mirror symmetric, and row 0 must monotonically
        // ramp from 0 to 255 with intermediate sub-pixel AA values.
        int intermediateSamples = 0;
        byte previous = 0;
        for (int x = 0; x <= inset; x++)
        {
            byte c = RoundedBackdropMaskService.ComputeCornerCoverageByte(x, 0, texSize, radiusPx);
            Assert.True(c >= previous, $"Row 0 coverage must be non-decreasing at x={x} ({c} < {previous}).");
            previous = c;
            if (c is > 0 and < 255)
            {
                intermediateSamples++;
            }

            for (int y = 0; y <= inset; y++)
            {
                byte tl = RoundedBackdropMaskService.ComputeCornerCoverageByte(x, y, texSize, radiusPx);
                byte tr = RoundedBackdropMaskService.ComputeCornerCoverageByte(texSize - 1 - x, y, texSize, radiusPx);
                byte bl = RoundedBackdropMaskService.ComputeCornerCoverageByte(x, texSize - 1 - y, texSize, radiusPx);
                byte br = RoundedBackdropMaskService.ComputeCornerCoverageByte(texSize - 1 - x, texSize - 1 - y, texSize, radiusPx);
                Assert.Equal(tl, tr);
                Assert.Equal(tl, bl);
                Assert.Equal(tl, br);
            }
        }

        Assert.True(intermediateSamples >= 2, $"Expected multiple smooth AA samples on row 0 for R={radiusPx}, got {intermediateSamples}.");
    }

    [Fact]
    public void Win10TransparentWindowHost_ClearsGdiRedirectionBitmapAndSurvivesAccentPolicyResets()
    {
        string win32 = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Platform/Win32Helper.cs"));
        string materialBackdrop = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Services/WidgetMaterialSystemBackdrop.cs"));
        string widgetBackdrop = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/WidgetWindowBase.Backdrop.cs"));
        string searchPopup = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/SearchPopupWindow.xaml.cs"));
        string stackPopover = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/StackPopoverHostWindow.cs"));
        string trayMenu = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/TrayContextMenuWindow.cs"));
        string detachPreview = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/WidgetDetachPlacementPreviewWindow.cs"));

        Assert.Contains("public static void EnableTransparentWindowHost(IntPtr hWnd)", win32, StringComparison.Ordinal);
        Assert.Contains("public static void DisableTransparentWindowHost(IntPtr hWnd)", win32, StringComparison.Ordinal);
        Assert.Contains("case WmEraseBkgnd:", win32, StringComparison.Ordinal);
        Assert.Contains("GetStockObject(BlackBrushStockObject)", win32, StringComparison.Ordinal);
        Assert.Contains("InvalidateRect(hWnd, IntPtr.Zero, bErase: 1)", win32, StringComparison.Ordinal);

        string disableAccentBody = win32[win32.IndexOf("public static void DisableAccentPolicy(IntPtr hWnd)", StringComparison.Ordinal)..];
        int nextMethod = disableAccentBody.IndexOf("private static void ApplyAccentPolicy(", StringComparison.Ordinal);
        Assert.True(nextMethod > 0);
        Assert.Contains("IsTransparentWindowHostActive(hWnd)", disableAccentBody[..nextMethod], StringComparison.Ordinal);
        Assert.Contains("ApplyEmptyRegionBlurBehind(hWnd, enable: true)", disableAccentBody[..nextMethod], StringComparison.Ordinal);

        Assert.Contains("Win32Helper.EnableTransparentWindowHost(_hwnd)", materialBackdrop, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.DisableTransparentWindowHost(_hwnd)", materialBackdrop, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.EnableTransparentWindowHost(HWnd)", widgetBackdrop, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.DisableTransparentWindowHost(HWnd)", widgetBackdrop, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.EnableTransparentWindowHost(_hwnd)", searchPopup, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.DisableTransparentWindowHost(_hwnd)", searchPopup, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.EnableTransparentWindowHost(WindowHandle)", stackPopover, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.EnableTransparentWindowHost(WindowHandle)", trayMenu, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.EnableTransparentWindowHost(_hWnd)", detachPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void TransparentWindowHostSubclass_ErasesWhiteBackingSurfaceToPremultipliedTransparentBlack()
    {
        IntPtr hwnd = CreateWindowExW(
            0,
            "STATIC",
            "DeskBoxTransparentHostTest",
            0x80000000u, // WS_POPUP
            -32000,
            -32000,
            8,
            8,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, hwnd);

        IntPtr memoryDc = CreateCompatibleDC(IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, memoryDc);

        var bmi = new BitmapInfoHeader
        {
            BiSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            BiWidth = 8,
            BiHeight = -8,
            BiPlanes = 1,
            BiBitCount = 32,
            BiCompression = 0
        };

        IntPtr dib = CreateDIBSection(memoryDc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        Assert.NotEqual(IntPtr.Zero, dib);
        Assert.NotEqual(IntPtr.Zero, bits);
        IntPtr oldBitmap = SelectObject(memoryDc, dib);

        try
        {
            int[] initialWhitePixels = Enumerable.Repeat(unchecked((int)0xFFFFFFFFu), 64).ToArray();
            Marshal.Copy(initialWhitePixels, 0, bits, initialWhitePixels.Length);

            Win32Helper.EnableTransparentWindowHost(hwnd);
            Assert.True(Win32Helper.IsTransparentWindowHostActive(hwnd));

            // Re-applying frame shadow suppression or disabling legacy accent policy
            // must keep the transparent host active on Windows 10 and Windows 11.
            Win32Helper.DisableWindowFrameShadow(hwnd);
            Win32Helper.DisableAccentPolicy(hwnd);
            Assert.True(Win32Helper.IsTransparentWindowHostActive(hwnd));

            IntPtr handled = SendMessageW(hwnd, 0x0014u, (UIntPtr)(nuint)(nint)memoryDc, IntPtr.Zero);
            Assert.Equal((IntPtr)1, handled);

            int[] actualPixels = new int[64];
            Marshal.Copy(bits, actualPixels, 0, actualPixels.Length);
            Assert.All(actualPixels, static pixel => Assert.Equal(0, pixel));

            Win32Helper.DisableTransparentWindowHost(hwnd);
            Assert.False(Win32Helper.IsTransparentWindowHostActive(hwnd));
        }
        finally
        {
            _ = SelectObject(memoryDc, oldBitmap);
            _ = DeleteObject(dib);
            _ = DeleteDC(memoryDc);
            _ = DestroyWindow(hwnd);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint BiSize;
        public int BiWidth;
        public int BiHeight;
        public ushort BiPlanes;
        public ushort BiBitCount;
        public uint BiCompression;
        public uint BiSizeImage;
        public int BiXPelsPerMeter;
        public int BiYPelsPerMeter;
        public uint BiClrUsed;
        public uint BiClrImportant;
    }

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc,
        ref BitmapInfoHeader pbmi,
        uint usage,
        out IntPtr ppvBits,
        IntPtr hSection,
        uint offset);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int DeleteObject(IntPtr ho);
}
