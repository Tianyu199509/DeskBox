using System.Diagnostics;
using System.Runtime.InteropServices;
using DeskBox.Helpers;

namespace DeskBox.Tests;

public sealed class WicBuiltInThumbnailDecoderContractTests
{
    [Theory]
    [InlineData("photo.png")]
    [InlineData("PHOTO.PNG")]
    [InlineData("photo.jpg")]
    [InlineData("photo.JpEg")]
    [InlineData("photo.tiff")]
    [InlineData("photo.tif")]
    [InlineData("photo.bmp")]
    public void BuiltInDecoderWhitelist_AcceptsWicBuiltInContainers(string path)
    {
        Assert.True(WicBuiltInThumbnailDecoder.TryGetBuiltInDecoderId(path, out _));
    }

    [Theory]
    [InlineData("animation.gif")]
    [InlineData("image.webp")]
    [InlineData("photo.heic")]
    [InlineData("document.pdf")]
    [InlineData("vector.svg")]
    [InlineData("texture.psd")]
    [InlineData("application.exe")]
    [InlineData("noextension")]
    public void BuiltInDecoderWhitelist_RejectsEverythingElse(string path)
    {
        Assert.False(WicBuiltInThumbnailDecoder.TryGetBuiltInDecoderId(path, out _));
    }

    [Fact]
    public void BuiltInDecoderWhitelist_MapsEachExtensionToItsBuiltInDecoder()
    {
        Assert.True(WicBuiltInThumbnailDecoder.TryGetBuiltInDecoderId(
            "a.png",
            out Guid png));
        Assert.True(WicBuiltInThumbnailDecoder.TryGetBuiltInDecoderId(
            "a.jpg",
            out Guid jpg));
        Assert.True(WicBuiltInThumbnailDecoder.TryGetBuiltInDecoderId(
            "a.tiff",
            out Guid tiff));
        Assert.True(WicBuiltInThumbnailDecoder.TryGetBuiltInDecoderId(
            "a.bmp",
            out Guid bmp));

        Assert.Equal(
            Windows.Graphics.Imaging.BitmapDecoder.PngDecoderId,
            png);
        Assert.Equal(
            Windows.Graphics.Imaging.BitmapDecoder.JpegDecoderId,
            jpg);
        Assert.Equal(
            Windows.Graphics.Imaging.BitmapDecoder.TiffDecoderId,
            tiff);
        Assert.Equal(
            Windows.Graphics.Imaging.BitmapDecoder.BmpDecoderId,
            bmp);
    }

    [Fact]
    public void BmpV5Payload_MatchesNativeProxyLayoutAndPassesVisibility()
    {
        byte[] payload = WicBuiltInThumbnailDecoder.EncodeBgraAsBitmapV5(
            2,
            3,
            CreateBgra(2, 3, alpha: 0xFF));

        Assert.Equal(138 + (2 * 3 * 4), payload.Length);
        Assert.Equal((byte)'B', payload[0]);
        Assert.Equal((byte)'M', payload[1]);
        Assert.Equal(payload.Length, BitConverter.ToInt32(payload, 2));
        Assert.Equal(138, BitConverter.ToInt32(payload, 10));
        Assert.Equal(124, BitConverter.ToInt32(payload, 14));
        Assert.Equal(2, BitConverter.ToInt32(payload, 18));
        Assert.Equal(-3, BitConverter.ToInt32(payload, 22));
        Assert.Equal(32, BitConverter.ToUInt16(payload, 28));
        Assert.Equal(3, BitConverter.ToInt32(payload, 30)); // BI_BITFIELDS
        Assert.Equal(0x00FF_0000u, BitConverter.ToUInt32(payload, 54));
        Assert.Equal(0x0000_FF00u, BitConverter.ToUInt32(payload, 58));
        Assert.Equal(0x0000_00FFu, BitConverter.ToUInt32(payload, 62));
        Assert.Equal(0xFF00_0000u, BitConverter.ToUInt32(payload, 66));
        Assert.True(ShellThumbnailProxy.IsVisibleBitmapPayload(payload));

        // A fully transparent decode is a blank and must not be served.
        byte[] blank = WicBuiltInThumbnailDecoder.EncodeBgraAsBitmapV5(
            2,
            3,
            CreateBgra(2, 3, alpha: 0));
        Assert.False(ShellThumbnailProxy.IsVisibleBitmapPayload(blank));
    }

    [Fact]
    public void ScaledDimensions_FitInsideBoxWithoutUpscaling()
    {
        Assert.Equal(
            (256, 192),
            WicBuiltInThumbnailDecoder.ComputeScaledDimensions(4000, 3000, 256));
        Assert.Equal(
            (192, 256),
            WicBuiltInThumbnailDecoder.ComputeScaledDimensions(3000, 4000, 256));
        Assert.Equal(
            (256, 256),
            WicBuiltInThumbnailDecoder.ComputeScaledDimensions(1000, 1000, 256));
        Assert.Equal(
            (256, 128),
            WicBuiltInThumbnailDecoder.ComputeScaledDimensions(512, 256, 256));
        Assert.Equal(
            (256, 1),
            WicBuiltInThumbnailDecoder.ComputeScaledDimensions(10000, 10, 256));

        // Sources already inside the box keep their native size.
        Assert.Equal(
            (96, 72),
            WicBuiltInThumbnailDecoder.ComputeScaledDimensions(96, 72, 256));
    }

    [Fact]
    public void OutputDimensions_SwapOnlyForTransposingExifOrientation()
    {
        // 90-degree EXIF rotation transposes the frame.
        Assert.Equal(
            (192, 256),
            WicBuiltInThumbnailDecoder.ResolveOutputDimensions(
                4000, 3000, 3000, 4000, 256, 192));
        // Identity orientation keeps the scaled rectangle.
        Assert.Equal(
            (256, 192),
            WicBuiltInThumbnailDecoder.ResolveOutputDimensions(
                4000, 3000, 4000, 3000, 256, 192));
        // Squares are unaffected by a transpose.
        Assert.Equal(
            (256, 256),
            WicBuiltInThumbnailDecoder.ResolveOutputDimensions(
                1000, 1000, 1000, 1000, 256, 256));
    }

    [Fact]
    public void WicFallback_PinsBuiltInDecodersAndNeverArbitratesCodecs()
    {
        string decoder = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/WicBuiltInThumbnailDecoder.cs"));
        string iconHelper = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/IconHelper.cs"));

        // The Guid overload explicitly selects a built-in decoder and, per
        // the Windows documentation, bypasses automatic codec arbitration;
        // the plain stream overload must never appear in the fallback.
        Assert.Contains(
            "CreateAsync(decoderId, stream)",
            decoder,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "BitmapDecoder.CreateAsync(stream",
            decoder,
            StringComparison.Ordinal);
        Assert.Contains("PngDecoderId", decoder, StringComparison.Ordinal);
        Assert.Contains("JpegDecoderId", decoder, StringComparison.Ordinal);
        Assert.Contains("TiffDecoderId", decoder, StringComparison.Ordinal);
        Assert.Contains("BmpDecoderId", decoder, StringComparison.Ordinal);

        // The fallback engages exactly when the isolated proxy path yields
        // nothing (failure or 30 s blacklist) and shares its failure
        // lifecycle.
        Assert.Contains(
            "WicBuiltInThumbnailDecoder.DecodeAsync",
            iconHelper,
            StringComparison.Ordinal);
        Assert.Contains(
            "WicBuiltInThumbnailDecoder.Invalidate(path)",
            iconHelper,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            CountOccurrences(
                iconHelper,
                "WicBuiltInThumbnailDecoder.ClearTransientFailures()"));
    }

    [Fact]
    public void NativeProxy_StartsWorkersLazilyWithABoundedFanOut()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "native/deskbox-thumbnail-proxy/src/main.rs"));

        Assert.Contains(
            "EXTRACT_BATCH_MAX_WORKERS: usize = 4",
            source,
            StringComparison.Ordinal);
        Assert.Contains("spawn_scoped", source, StringComparison.Ordinal);
        Assert.Contains(
            "stack_size(EXTRACT_BATCH_WORKER_STACK_BYTES)",
            source,
            StringComparison.Ordinal);
        // The eager full fan-out is gone; only the supervisor's
        // spawn_scoped remains.
        Assert.DoesNotContain("scope.spawn(", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractBatch_CompletesAFullHostBatchThroughTheRamp()
    {
        string proxyPath = GetBuiltProxyPath();
        Assert.True(File.Exists(proxyPath), $"Proxy not found: {proxyPath}");

        string windowsFolder = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        string notepadPath = Path.Combine(
            Environment.SystemDirectory,
            "notepad.exe");

        var batch = new List<ShellThumbnailProxy.ProxyBatchRequest>();
        for (int index = 0; index < 8; index++)
        {
            batch.Add(new ShellThumbnailProxy.ProxyBatchRequest(
                ShellThumbnailProxy.ShellImageMode.Icon,
                index % 2 == 0 ? notepadPath : windowsFolder,
                48,
                new TaskCompletionSource<byte[]?>(
                    TaskCreationOptions.RunContinuationsAsynchronously)));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = proxyPath,
            WorkingDirectory = Path.GetDirectoryName(proxyPath)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--extract-batch");

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        ShellThumbnailProxy.WriteBatchManifest(
            process.StandardInput.BaseStream,
            batch);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var fulfilled = new bool[batch.Count];
        int failedCount = await ShellThumbnailProxy.ReadBatchFramesAsync(
            process.StandardOutput.BaseStream,
            batch,
            fulfilled,
            timeout.Token);
        await process.WaitForExitAsync(timeout.Token);

        Assert.True(
            process.ExitCode == 0,
            $"Batch proxy exited with {process.ExitCode}: {await errorTask}");
        Assert.All(fulfilled, value => Assert.True(value));
        Assert.Equal(0, failedCount);

        byte[]? fileIcon = await batch[0].Completion.Task;
        Assert.True(
            fileIcon is not null &&
            ShellThumbnailProxy.IsVisibleBitmapPayload(fileIcon),
            "Batch did not return a visible icon for notepad.exe.");
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static byte[] CreateBgra(int width, int height, byte alpha)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int index = 0; index < width * height; index++)
        {
            pixels[index * 4] = 0x11;
            pixels[index * 4 + 1] = 0x22;
            pixels[index * 4 + 2] = 0x33;
            pixels[index * 4 + 3] = alpha;
        }

        return pixels;
    }

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
}
