using DeskBox.Helpers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeskBox.Tests;

public sealed class ShellThumbnailProxyContractTests
{
    [Theory]
    [InlineData("program.exe")]
    [InlineData("library.dll")]
    [InlineData("shortcut.lnk")]
    [InlineData("website.url")]
    public async Task ProviderProbe_RejectsExecutableAndShortcutTypes(
        string path)
    {
        Assert.False(
            await ShellThumbnailProxy.HasRegisteredThumbnailProviderAsync(path));
    }

    [Fact]
    public void ProxyProcess_IsBoundedAndNeverLoadsShellHandlersInDeskBox()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/ShellThumbnailProxy.cs"));
        string iconHelper = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/IconHelper.cs"));

        Assert.Contains("UseShellExecute = false", source, StringComparison.Ordinal);
        Assert.Contains("RedirectStandardOutput = true", source, StringComparison.Ordinal);
        Assert.Contains("CreateNoWindow = true", source, StringComparison.Ordinal);
        Assert.Contains("ExtractionTimeout", source, StringComparison.Ordinal);
        Assert.Contains("Kill(entireProcessTree: true)", source, StringComparison.Ordinal);
        Assert.Contains("MaximumPayloadBytes", source, StringComparison.Ordinal);
        Assert.Contains(
            "await ShellThumbnailProxy.HasRegisteredThumbnailProviderAsync(path)",
            iconHelper,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShellThumbnailProxy.TryLoadIconAsync(",
            iconHelper,
            StringComparison.Ordinal);
        Assert.Contains("UsesShellItemIcon", iconHelper, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IShellItemImageFactory",
            iconHelper,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NativeProxy_RequiresARealThumbnailAndReturnsAnAlphaBitmap()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "native/deskbox-thumbnail-proxy/src/main.rs"));

        Assert.Contains("SIIGBF_THUMBNAILONLY", source, StringComparison.Ordinal);
        Assert.Contains("SIIGBF_ICONONLY", source, StringComparison.Ordinal);
        Assert.Contains("SHGFI_ADDOVERLAYS", source, StringComparison.Ordinal);
        Assert.Contains("--icon-only", source, StringComparison.Ordinal);
        Assert.Contains("--icon-with-overlays", source, StringComparison.Ordinal);
        Assert.Contains("IShellItemImageFactory", source, StringComparison.Ordinal);
        Assert.Contains("if !path.exists()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("if !path.is_file()", source, StringComparison.Ordinal);
        Assert.Contains("BITMAP_V5_HEADER_SIZE", source, StringComparison.Ordinal);
        Assert.Contains("0xFF00_0000", source, StringComparison.Ordinal);
        Assert.Contains("empty transparent bitmap", source, StringComparison.Ordinal);
        Assert.Contains("DeleteObject", source, StringComparison.Ordinal);
        Assert.Contains("CoUninitialize", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProxyPayloadValidation_RejectsTransparentBlankBitmap()
    {
        Assert.False(ShellThumbnailProxy.IsVisibleBitmapPayload(
            CreateBitmapPayload(alpha: 0)));
        Assert.True(ShellThumbnailProxy.IsVisibleBitmapPayload(
            CreateBitmapPayload(alpha: 0xFF)));
    }

    [Fact]
    public void ShortcutIconPayload_CropsClearlyPaddedCanvas()
    {
        byte[] payload = CreateBitmapPayload(
            width: 256,
            height: 256,
            visibleLeft: 104,
            visibleTop: 104,
            visibleWidth: 48,
            visibleHeight: 48,
            alpha: 0xFF);

        Assert.True(ShellThumbnailProxy.IsLikelyPaddedIconPayload(payload));

        byte[] normalized = Assert.IsType<byte[]>(
            ShellThumbnailProxy.NormalizeIconPayload(payload));
        Assert.True(ShellThumbnailProxy.IsVisibleBitmapPayload(normalized));
        Assert.False(ShellThumbnailProxy.IsLikelyPaddedIconPayload(normalized));
        Assert.Equal(48, BitConverter.ToInt32(normalized, 18));
        Assert.Equal(-48, BitConverter.ToInt32(normalized, 22));
    }

    [Fact]
    public void ShortcutIconPayload_CropsCanvasWithFaintShellBorder()
    {
        // Measured shape of an icon whose registered resource is gone (an
        // uninstalled app that left its file association behind): the Shell
        // centers the 48 px artwork in the requested canvas and paints a
        // near-invisible border around it. Counting that border as content used
        // to make the padding gate miss the canvas and render a tiny glyph.
        byte[] payload = CreateBitmapPayload(
            width: 256,
            height: 256,
            visibleLeft: 111,
            visibleTop: 106,
            visibleWidth: 34,
            visibleHeight: 44,
            alpha: 0xFF,
            faintCanvasBorder: true);

        Assert.True(ShellThumbnailProxy.IsLikelyPaddedIconPayload(payload));

        byte[] normalized = Assert.IsType<byte[]>(
            ShellThumbnailProxy.NormalizeIconPayload(payload));
        Assert.True(ShellThumbnailProxy.IsVisibleBitmapPayload(normalized));
        Assert.False(ShellThumbnailProxy.IsLikelyPaddedIconPayload(normalized));
        Assert.Equal(34, BitConverter.ToInt32(normalized, 18));
        Assert.Equal(-44, BitConverter.ToInt32(normalized, 22));
    }

    [Fact]
    public void ArtworkBelowSignificantAlpha_KeepsNonTransparentBounds()
    {
        // A payload whose every pixel is that faint has no artwork to frame, so
        // the measurement must fall back to the non-transparent bounds instead
        // of collapsing to nothing.
        byte[] payload = CreateBitmapPayload(
            width: 256,
            height: 256,
            visibleLeft: 104,
            visibleTop: 104,
            visibleWidth: 48,
            visibleHeight: 48,
            alpha: 20);

        Assert.Equal(32, IconBitmapQuality.SignificantAlphaThreshold(20));
        Assert.True(ShellThumbnailProxy.IsLikelyPaddedIconPayload(payload));
    }

    [Fact]
    public async Task IconOnlyProxy_ReturnsVisiblePixelsForPidlShortcut()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"DeskBox-issue119-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            string shortcutPath = Path.Combine(
                temporaryDirectory,
                "Recycle Bin.lnk");
            Helpers.WindowsShellLinkTestOracle.CreateShellNamespaceShortcut(
                shortcutPath,
                "shell:RecycleBinFolder",
                "DeskBox issue 119 regression");

            byte[] output = await RunIconProxyAsync(shortcutPath, 64);
            Assert.True(
                ShellThumbnailProxy.IsVisibleBitmapPayload(output),
                "Icon-only proxy returned an empty or transparent bitmap.");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task IconOnlyProxy_ReturnsVisiblePixelsForFolder()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"DeskBox-folder-icon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            byte[] output = await RunIconProxyAsync(
                temporaryDirectory,
                256);
            Assert.True(
                ShellThumbnailProxy.IsVisibleBitmapPayload(output),
                "Folder icon proxy returned an empty or transparent bitmap.");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IconProxy_ReturnsVisiblePixelsForInternetShortcut(
        bool includeOverlays)
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"DeskBox-url-icon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            string shortcutPath = Path.Combine(
                temporaryDirectory,
                "Steam game.url");
            string shellIconPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "shell32.dll");
            await File.WriteAllTextAsync(
                shortcutPath,
                $"[InternetShortcut]\n" +
                $"URL=steam://rungameid/123\n" +
                $"IconFile={shellIconPath}\n" +
                $"IconIndex=0\n");

            byte[] output = await RunIconProxyAsync(
                shortcutPath,
                256,
                includeOverlays);
            Assert.True(
                ShellThumbnailProxy.IsVisibleBitmapPayload(output),
                $"Internet shortcut proxy returned a blank icon " +
                $"(includeOverlays={includeOverlays}).");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void Build_AlwaysCopiesProxyAndAddsItToStorePayload()
    {
        string project = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/DeskBox.csproj"));

        Assert.Contains(
            "<DeskBoxShellThumbnailProxy Condition=\"'$(DeskBoxShellThumbnailProxy)' == ''\">true</DeskBoxShellThumbnailProxy>",
            project,
            StringComparison.Ordinal);
        Assert.Contains("BuildDeskBoxThumbnailProxy", project, StringComparison.Ordinal);
        Assert.Contains("CopyDeskBoxThumbnailProxyToOutput", project, StringComparison.Ordinal);
        Assert.Contains("CopyDeskBoxThumbnailProxyToPublish", project, StringComparison.Ordinal);
        Assert.Contains("PrepareDeskBoxStoreThumbnailProxyPayload", project, StringComparison.Ordinal);
        Assert.Contains("DeskBox.ThumbnailProxy.exe", project, StringComparison.Ordinal);
        Assert.Contains("<TargetPath>DeskBox.ThumbnailProxy.pdb</TargetPath>", project, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseAudits_RequireProxyPayloadSymbolsAndTargetArchitecture()
    {
        string audit = Read("scripts/publish-aot-audit.ps1");
        string retail = Read("scripts/publish-aot-retail.ps1");
        string arm64 = Read("scripts/publish-arm64-aot-static-audit.ps1");
        string distribution = Read("scripts/build-stage-7c1-distribution.ps1");
        string store = Read("scripts/audit-store-native-aot-package.ps1");

        foreach (string script in new[] { audit, retail, arm64, distribution, store })
        {
            Assert.Contains("DeskBox.ThumbnailProxy.exe", script, StringComparison.Ordinal);
        }

        Assert.Contains("DeskBox.ThumbnailProxy.pdb", audit, StringComparison.Ordinal);
        Assert.Contains("DeskBox.ThumbnailProxy.pdb", retail, StringComparison.Ordinal);
        Assert.Contains("DeskBox.ThumbnailProxy.pdb", arm64, StringComparison.Ordinal);
        Assert.Contains("DeskBox.ThumbnailProxy.pdb", store, StringComparison.Ordinal);
        Assert.Contains("$thumbnailProxyMachine = Get-PeMachine", distribution, StringComparison.Ordinal);
        Assert.Contains("$thumbnailProxyPe = Get-PeFacts", store, StringComparison.Ordinal);
        Assert.Contains("thumbnailProxy = $thumbnailProxyPe", store, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));

    private static string GetBuiltProxyPath()
    {
        string configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        string platform = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "ARM64"
            : "x64";
        // CI builds pass -p:RuntimeIdentifier=win-x64, which moves outputs
        // into a RID-suffixed subfolder; local canonical builds use the plain
        // output root. Prefer whichever copy exists.
        string outputRoot = Path.Combine(
            "src",
            "DeskBox",
            "bin",
            platform,
            configuration,
            "net10.0-windows10.0.22621.0");
        // Prefer the canonical non-RID output (the local dev flow keeps it
        // current); fall back to the RID-suffixed output, which is the only
        // copy CI produces. Stale RID copies must never shadow the canonical
        // build.
        string canonicalPath = TestPaths.FromRepository(Path.Combine(
            outputRoot,
            ShellThumbnailProxy.ExecutableName));
        if (File.Exists(canonicalPath))
        {
            return canonicalPath;
        }

        return TestPaths.FromRepository(Path.Combine(
            outputRoot,
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "win-arm64"
                : "win-x64",
            ShellThumbnailProxy.ExecutableName));
    }

    private static async Task<byte[]> RunIconProxyAsync(
        string path,
        int requestedSize,
        bool includeOverlays = false)
    {
        string proxyPath = GetBuiltProxyPath();
        Assert.True(File.Exists(proxyPath), $"Proxy not found: {proxyPath}");
        var startInfo = new ProcessStartInfo
        {
            FileName = proxyPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(
            includeOverlays ? "--icon-with-overlays" : "--icon-only");
        startInfo.ArgumentList.Add(path);
        startInfo.ArgumentList.Add(requestedSize.ToString(
            System.Globalization.CultureInfo.InvariantCulture));

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        using var output = new MemoryStream();
        Task outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
        await outputTask;
        string error = await errorTask;

        Assert.True(
            process.ExitCode == 0,
            $"Icon-only proxy failed with {process.ExitCode}: {error}");
        return output.ToArray();
    }

    private static byte[] CreateBitmapPayload(byte alpha)
    {
        return CreateBitmapPayload(
            width: 1,
            height: 1,
            visibleLeft: 0,
            visibleTop: 0,
            visibleWidth: 1,
            visibleHeight: 1,
            alpha: alpha);
    }

    private static byte[] CreateBitmapPayload(
        int width,
        int height,
        int visibleLeft,
        int visibleTop,
        int visibleWidth,
        int visibleHeight,
        byte alpha,
        bool faintCanvasBorder = false)
    {
        const int pixelOffset = 138;
        byte[] payload = new byte[pixelOffset + (width * height * 4)];
        payload[0] = (byte)'B';
        payload[1] = (byte)'M';
        BitConverter.GetBytes(payload.Length).CopyTo(payload, 2);
        BitConverter.GetBytes(pixelOffset).CopyTo(payload, 10);
        BitConverter.GetBytes(124).CopyTo(payload, 14);
        BitConverter.GetBytes(width).CopyTo(payload, 18);
        BitConverter.GetBytes(-height).CopyTo(payload, 22);
        BitConverter.GetBytes((ushort)1).CopyTo(payload, 26);
        BitConverter.GetBytes((ushort)32).CopyTo(payload, 28);
        if (faintCanvasBorder)
        {
            PaintFaintCanvasBorder(payload, width, height, pixelOffset);
        }

        for (int y = visibleTop; y < visibleTop + visibleHeight; y++)
        {
            for (int x = visibleLeft; x < visibleLeft + visibleWidth; x++)
            {
                int offset = pixelOffset + ((y * width + x) * 4);
                payload[offset] = 0x11;
                payload[offset + 1] = 0x22;
                payload[offset + 2] = 0x33;
                payload[offset + 3] = alpha;
            }
        }

        return payload;
    }

    /// <summary>
    /// Reproduces the border the Shell paints when it centers a small icon frame
    /// inside a canvas it has no artwork for: a dark outer edge plus a light
    /// inner edge whose alpha stays far below the artwork's.
    /// </summary>
    private static void PaintFaintCanvasBorder(
        byte[] payload,
        int width,
        int height,
        int pixelOffset)
    {
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int distanceToEdge = Math.Min(
                    Math.Min(x, width - 1 - x),
                    Math.Min(y, height - 1 - y));
                if (distanceToEdge > 4)
                {
                    continue;
                }

                bool isOuterEdge = distanceToEdge <= 1;
                int offset = pixelOffset + ((y * width + x) * 4);
                byte value = isOuterEdge ? (byte)0x00 : (byte)0xFF;
                payload[offset] = value;
                payload[offset + 1] = value;
                payload[offset + 2] = value;
                payload[offset + 3] = isOuterEdge ? (byte)38 : (byte)90;
            }
        }
    }
}
