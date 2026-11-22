using DeskBox.Helpers;
using System.Reflection;

namespace DeskBox.Tests;

public class IconHelperTests
{
    [Theory]
    [InlineData("solution.sln", true)]
    [InlineData("solution.slnx", true)]
    [InlineData("solution.csproj", false)]
    [InlineData("solution.sln.bak", false)]
    public void SolutionFilePathDetection_IsLimitedToSolutionFormats(
        string path,
        bool expected)
    {
        Assert.Equal(expected, IconHelper.IsSolutionFilePath(path));
    }

    [Theory]
    [InlineData("clip.mp4")]
    [InlineData("clip.MOV")]
    [InlineData(@"C:\media\clip.mkv")]
    [InlineData("clip.webm")]
    [InlineData("clip.m2ts")]
    public void IsVideoFile_RecognizesSupportedVideoExtensions(string path)
    {
        Assert.True(IconHelper.IsVideoFile(path));
        Assert.True(IconHelper.IsMediaFile(path));
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("photo.HEIC")]
    public void IsMediaFile_IncludesImages(string path)
    {
        Assert.True(IconHelper.IsImageFile(path));
        Assert.True(IconHelper.IsMediaFile(path));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("archive.zip")]
    [InlineData("video.mp4.txt")]
    public void IsMediaFile_RejectsNonMediaExtensions(string path)
    {
        Assert.False(IconHelper.IsMediaFile(path));
    }

    [Theory]
    [InlineData("game.url", true)]
    [InlineData("GAME.URL", true)]
    [InlineData("game.lnk", false)]
    [InlineData("game.url.txt", false)]
    [InlineData("", false)]
    public void InternetShortcutPathDetection_IsLimitedToUrlFiles(
        string path,
        bool expected)
    {
        Assert.Equal(expected, ShortcutHelper.IsInternetShortcutPath(path));
    }

    [Theory]
    [InlineData(@"C:\icons\game.ico", @"C:\icons\game.ico", null)]
    [InlineData(@"C:\Windows\System32\shell32.dll,-5", @"C:\Windows\System32\shell32.dll", -5)]
    [InlineData("\"C:\\icons\\game,alternate.dll\", 12", @"C:\icons\game,alternate.dll", 12)]
    public void SplitIconLocation_PreservesSignedIndexesAndQuotedCommaPaths(
        string iconLocation,
        string expectedPath,
        int? expectedIndex)
    {
        var (path, index) = IconHelper.SplitIconLocation(iconLocation);

        Assert.Equal(expectedPath, path);
        Assert.Equal(expectedIndex, index);
    }

    [Fact]
    public void RelativeIconLocation_ResolvesBesideShortcutAndRejectsMalformedPath()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"DeskBox-relative-url-icon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            string shortcutPath = Path.Combine(temporaryDirectory, "game.url");
            string iconPath = Path.Combine(temporaryDirectory, "game.ico");
            File.WriteAllText(shortcutPath, "[InternetShortcut]\nURL=https://example.invalid/\n");
            File.WriteAllBytes(iconPath, [0]);

            Assert.True(IconHelper.TryResolveRelativeIconLocation(
                shortcutPath,
                "game.ico",
                out string resolvedPath));
            Assert.Equal(Path.GetFullPath(iconPath), resolvedPath);
            Assert.False(IconHelper.TryResolveRelativeIconLocation(
                shortcutPath,
                "invalid\0icon.ico",
                out _));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void InternetShortcutIconPolicy_IsShellFirstOverlayAwareAndUsedBySearch()
    {
        string iconHelper = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/IconHelper.cs"));
        string fileMetaService = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/FileMetaService.cs"));

        Assert.Contains(
            "CreateInternetShortcutIconSource(",
            iconHelper,
            StringComparison.Ordinal);
        Assert.Contains(
            "includeOverlays: !hideShortcutArrowOverlay",
            iconHelper,
            StringComparison.Ordinal);
        Assert.Contains(
            "InternetShortcutIconStrategyVersion",
            iconHelper,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResolveInternetShortcutFallbackSourceAsync(",
            iconHelper,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShortcutHelper.IsShortcutPath(item.DetailPath)",
            fileMetaService,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ShortcutIconResolution_IsBoundedAndCacheInvalidationAvoidsShellReads()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/IconHelper.cs"));
        int getIconStart = source.IndexOf(
            "public static async Task<BitmapImage?> GetIconAsync(",
            StringComparison.Ordinal);
        int clearCacheStart = source.IndexOf(
            "public static void ClearIconCache(",
            getIconStart,
            StringComparison.Ordinal);
        int clearCacheEnd = source.IndexOf(
            "private static void InvalidateShellIconCache(",
            clearCacheStart,
            StringComparison.Ordinal);
        Assert.True(getIconStart >= 0);
        Assert.True(clearCacheStart > getIconStart);
        Assert.True(clearCacheEnd > clearCacheStart);

        string getIcon = source[getIconStart..clearCacheStart];
        Assert.Contains(
            "await ResolveIconSourceWithCacheKeyAsync(",
            getIcon,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ResolveIconSource(path",
            getIcon,
            StringComparison.Ordinal);

        string clearCache = source[clearCacheStart..clearCacheEnd];
        Assert.DoesNotContain(
            "ResolveIconSource(",
            clearCache,
            StringComparison.Ordinal);
        Assert.DoesNotContain("File.Exists(", clearCache, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Exists(", clearCache, StringComparison.Ordinal);

        Assert.Contains(
            "BoundedBackgroundWorkScheduler.SharedShell",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "IconSourceResolutionTimeout",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PidlShortcutIconResolution_UsesIsolatedIconOnlyModeAndRejectsBlankIcons()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/IconHelper.cs"));
        string proxy = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/ShellThumbnailProxy.cs"));

        Assert.Contains(
            "return new IconSource(path, UsesShellItemIcon: true);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShellThumbnailProxy.TryLoadIconAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryLoadHighResolutionShellItemIconAsync(\n" +
            "                    originalSourcePath)",
            source.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains(
            "loadIconSource.UsesShellItemIcon",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "EncodeVisibleBitmapAsPng(bitmap)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsVisibleBitmapPayload(output)",
            proxy,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".sln")]
    [InlineData(".slnx")]
    public void SolutionShortcut_ExtractsAVisibleIconWhenArrowIsHidden(
        string extension)
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"DeskBox-solution-icon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            string targetPath = Path.Combine(
                temporaryDirectory,
                $"test-solution{extension}");
            File.Copy(
                TestPaths.FromRepository("DeskBox.sln"),
                targetPath,
                overwrite: true);
            string shortcutPath = Path.Combine(
                temporaryDirectory,
                $"test-solution{extension}.lnk");
            ShortcutHelper.CreateOrUpdateFolderShortcut(
                shortcutPath,
                targetPath,
                "solution icon regression");

            MethodInfo resolveMethod = typeof(IconHelper).GetMethod(
                "ResolveIconSource",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "Icon source resolver was not found.");
            object iconSource = resolveMethod.Invoke(
                null,
                [shortcutPath, true])
                ?? throw new InvalidOperationException(
                    "Icon source resolver returned null.");
            MethodInfo loadMethod = typeof(IconHelper)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Single(method =>
                    method.Name == "LoadIconBytes" &&
                    method.GetParameters().Length == 2);
            byte[] iconBytes = Assert.IsType<byte[]>(
                loadMethod.Invoke(null, [iconSource, false]));

            Assert.True(
                IconPngEncoder.TryDecodePngHasVisiblePixels(iconBytes),
                $"The {extension} shortcut icon decoded as transparent.");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void ExplicitIconIndexGate_DefersToTheIndexedExtractionChannel()
    {
        string normalized = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/IconHelper.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        int gateIndex = normalized.IndexOf(
            "if (!ShortcutHelper.IsShortcutPath(loadIconSource.Path) &&\n" +
            "                !loadIconSource.UsesExplicitIconIndex)",
            StringComparison.Ordinal);
        Assert.True(gateIndex >= 0, "The Shell item gate must skip explicit icon index sources.");

        int indexedChannelIndex = normalized.IndexOf(
            ": LoadIconBytes(",
            gateIndex,
            StringComparison.Ordinal);
        Assert.True(
            indexedChannelIndex > gateIndex,
            "Explicit icon index sources must fall through to the indexed extraction channel.");

        int fallbackIndex = normalized.IndexOf(
            "Recovered explicit index icon through",
            indexedChannelIndex,
            StringComparison.Ordinal);
        int recoveryIndex = normalized.IndexOf(
            "Recovered shortcut icon through Shell proxy",
            StringComparison.Ordinal);
        Assert.True(fallbackIndex > indexedChannelIndex);
        Assert.True(
            recoveryIndex > fallbackIndex,
            "The explicit index fallback must run before the Shell proxy restore block.");
    }

    [Fact]
    public void ExplicitIconIndexFallback_RecoversThroughShellItemOnlyForEmptyBytes()
    {
        string normalized = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/IconHelper.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        int fallbackConditionIndex = normalized.IndexOf(
            "if (bytes is not { Length: > 0 } &&\n" +
            "                loadIconSource.UsesExplicitIconIndex)",
            StringComparison.Ordinal);
        Assert.True(
            fallbackConditionIndex >= 0,
            "The explicit index fallback must be gated on empty bytes.");

        string fallbackBlock = normalized[fallbackConditionIndex..];
        int recoveryIndex = fallbackBlock.IndexOf(
            "Recovered shortcut icon through Shell proxy",
            StringComparison.Ordinal);
        Assert.True(recoveryIndex > 0);
        fallbackBlock = fallbackBlock[..recoveryIndex];

        Assert.Contains(
            "bytes = await TryLoadFileShellItemIconAsync(loadIconSource.Path);",
            fallbackBlock,
            StringComparison.Ordinal);
        int guardIndex = fallbackBlock.IndexOf(
            "if (bytes is { Length: > 0 })",
            StringComparison.Ordinal);
        int storeIndex = fallbackBlock.IndexOf(
            "StoreCachedIconBytes(iconBytesCacheKey, bytes);",
            StringComparison.Ordinal);
        Assert.True(
            guardIndex >= 0 && storeIndex > guardIndex,
            "Only a successful explicit index fallback may write the byte cache.");
    }

    [Fact]
    public void ExplicitIconIndexSource_ExtractsTheRequestedFrameInsteadOfTheDefaultOne()
    {
        string iconLibraryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "imageres.dll");
        Assert.True(
            File.Exists(iconLibraryPath),
            "imageres.dll is required to exercise the indexed icon channel.");

        byte[]? defaultFrame = InvokeIconBytesMethod(
            "LoadIconBytes",
            CreateExplicitIconSource(iconLibraryPath, 0));
        Assert.NotNull(defaultFrame);

        byte[]? requestedFrame = null;
        int requestedIndex = -1;
        for (int candidate = 1;
             candidate <= 12 && requestedFrame is null;
             candidate++)
        {
            byte[]? candidateFrame = InvokeIconBytesMethod(
                "LoadIconBytes",
                CreateExplicitIconSource(iconLibraryPath, candidate));
            if (candidateFrame is not null &&
                !candidateFrame.AsSpan().SequenceEqual(defaultFrame))
            {
                requestedFrame = candidateFrame;
                requestedIndex = candidate;
            }
        }

        Assert.True(
            requestedIndex > 0,
            "No imageres.dll frame between index 1 and 12 differed from the index 0 frame.");
        byte[]? indexedFrame = InvokeIconBytesMethod(
            "LoadIndexedIconBytes",
            CreateExplicitIconSource(iconLibraryPath, requestedIndex));
        Assert.NotNull(indexedFrame);
        Assert.True(
            requestedFrame!.AsSpan().SequenceEqual(indexedFrame),
            $"The explicit-index channel must return frame {requestedIndex} exactly " +
            "as LoadIndexedIconBytes extracts it.");
    }

    [Fact]
    public void ExplicitIconLocationPointingAtIcoFile_ExtractsVisibleBytes()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"DeskBox-explicit-ico-icon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            string iconPath = Path.Combine(temporaryDirectory, "explicit.ico");
            File.WriteAllBytes(iconPath, CreateOpaqueIconFileBytes());

            byte[]? iconBytes = InvokeIconBytesMethod(
                "LoadIconBytes",
                CreateExplicitIconSource(iconPath, 0));
            Assert.NotNull(iconBytes);

            Assert.True(
                IconPngEncoder.TryDecodePngHasVisiblePixels(iconBytes!),
                "A standalone .ico icon location must decode into visible pixels.");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, 256)]
    [InlineData(-1, 256)]
    [InlineData(24, 48)]
    [InlineData(48, 48)]
    [InlineData(49, 96)]
    [InlineData(72, 96)]
    [InlineData(96, 96)]
    [InlineData(97, 256)]
    [InlineData(256, 256)]
    public void QuantizeShellIconRequestSize_MapsDecodeWidthToNonLohBucketsForStandardTiles(
        int decodePixelWidth,
        int expectedBucket)
    {
        Assert.Equal(
            expectedBucket,
            IconHelper.QuantizeShellIconRequestSize(decodePixelWidth));
    }

    [Fact]
    public void StoreCachedIconBytes_Compresses256BmpV5PayloadToCompactPng()
    {
        string cacheKey = $"ext:.deskbox-bmpv5-{Guid.NewGuid():N}:size=256";
        byte[] bmpV5Bytes = CreateBmpV5IconPayload(width: 256, height: 256);
        Assert.Equal(14 + 124 + (256 * 256 * 4), bmpV5Bytes.Length);

        try
        {
            IconHelper.StoreCachedIconBytes(cacheKey, bmpV5Bytes);
            byte[]? cached = IconHelper.GetCachedIconBytesForTest(cacheKey);
            Assert.NotNull(cached);
            Assert.True(
                cached!.Length < 16_384,
                $"Expected compact PNG (<16 KB), got {cached.Length} bytes.");
            Assert.Equal((byte)0x89, cached[0]);
            Assert.Equal((byte)'P', cached[1]);
            Assert.Equal((byte)'N', cached[2]);
            Assert.Equal((byte)'G', cached[3]);
            Assert.True(
                IconPngEncoder.TryDecodePngHasVisiblePixels(cached),
                "Transcoded PNG payload must decode with visible pixels.");
        }
        finally
        {
            IconHelper.RemoveCachedIconBytesForTest(cacheKey);
        }
    }

    [Fact]
    public void ClearCacheScope_EvictsTransientNonSharedIconBytesWhilePreservingShared()
    {
        string scope = $"transient-{Guid.NewGuid():N}";
        string exclusiveKey = $"source:C:\\temp\\{Guid.NewGuid():N}.exe|path:exe:size=48";
        string sharedAlsoKey = $"source:C:\\temp\\{Guid.NewGuid():N}.exe|path:exe:size=48";
        string extKey = $"ext:.deskbox-scope-{Guid.NewGuid():N}:size=48";
        byte[] payload = CreateOpaqueIconFileBytes();

        try
        {
            IconHelper.StoreCachedIconBytes(exclusiveKey, payload);
            IconHelper.IndexIconBytesScopeKey(scope, exclusiveKey);

            IconHelper.StoreCachedIconBytes(sharedAlsoKey, payload);
            IconHelper.IndexIconBytesScopeKey("shared", sharedAlsoKey);
            IconHelper.IndexIconBytesScopeKey(scope, sharedAlsoKey);

            IconHelper.StoreCachedIconBytes(extKey, payload);
            IconHelper.IndexIconBytesScopeKey(scope, extKey);

            IconHelper.ClearCacheScope(scope);

            Assert.False(IconHelper.HasCachedIconBytesForTest(exclusiveKey));
            Assert.True(IconHelper.HasCachedIconBytesForTest(sharedAlsoKey));
            Assert.True(IconHelper.HasCachedIconBytesForTest(extKey));
        }
        finally
        {
            IconHelper.RemoveCachedIconBytesForTest(exclusiveKey);
            IconHelper.RemoveCachedIconBytesForTest(sharedAlsoKey);
            IconHelper.RemoveCachedIconBytesForTest(extKey);
        }
    }

    [Fact]
    public void CreateMediaThumbnailFallback_UsesPinnedWicThumbnailDecoderInsteadOfRawFileStream()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/IconHelper.cs"));
        int start = source.IndexOf(
            "private static async Task<BitmapImage?> CreateMediaThumbnailAsync(",
            StringComparison.Ordinal);
        int end = source.IndexOf(
            "private static async Task<BitmapImage?> CreateShellThumbnailAsync(",
            start,
            StringComparison.Ordinal);
        Assert.True(start >= 0);
        Assert.True(end > start);

        string method = source[start..end];
        Assert.Contains(
            "WicBuiltInThumbnailDecoder.DecodeAsync(",
            method,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AsRandomAccessStream()",
            method,
            StringComparison.Ordinal);
    }

    private static byte[] CreateBmpV5IconPayload(int width, int height)
    {
        const int fileHeaderSize = 14;
        const int dibHeaderSize = 124;
        int pixelBytes = width * height * 4;
        byte[] buffer = new byte[fileHeaderSize + dibHeaderSize + pixelBytes];
        Span<byte> span = buffer;

        span[0] = (byte)'B';
        span[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            span.Slice(2, 4),
            buffer.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            span.Slice(10, 4),
            fileHeaderSize + dibHeaderSize);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            span.Slice(14, 4),
            dibHeaderSize);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            span.Slice(18, 4),
            width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            span.Slice(22, 4),
            -height); // top-down BMPv5
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
            span.Slice(26, 2),
            1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
            span.Slice(28, 2),
            32);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            span.Slice(30, 4),
            3u); // BI_BITFIELDS
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            span.Slice(54, 4),
            0x00FF0000u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            span.Slice(58, 4),
            0x0000FF00u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            span.Slice(62, 4),
            0x000000FFu);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            span.Slice(66, 4),
            0xFF000000u);

        Span<byte> pixels = span[(fileHeaderSize + dibHeaderSize)..];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0xE2;     // B
            pixels[i + 1] = 0xD8; // G
            pixels[i + 2] = 0x80; // R
            pixels[i + 3] = 0xFF; // A
        }

        return buffer;
    }

    private static object CreateExplicitIconSource(string path, int iconIndex)
    {
        Type? iconSourceType = typeof(IconHelper).GetNestedType(
            "IconSource",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(iconSourceType);
        return Activator.CreateInstance(
            iconSourceType!,
            path,
            iconIndex,
            true,   // UsesExplicitIconIndex
            false)  // UsesShellItemIcon
            ?? throw new InvalidOperationException(
                "The icon source could not be created.");
    }

    private static byte[]? InvokeIconBytesMethod(string methodName, object iconSource)
    {
        MethodInfo method = typeof(IconHelper)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method =>
                method.Name == methodName &&
                method.GetParameters().Length == 2);
        return (byte[]?)method.Invoke(null, [iconSource, false]);
    }

    private static byte[] CreateOpaqueIconFileBytes()
    {
        const int Size = 16;
        const int AndStride = 4; // 16 mask bits padded to a 32-bit row.
        int xorSize = Size * Size * 4;
        int andSize = AndStride * Size;
        int imageSize = 40 + xorSize + andSize;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        // ICONDIR
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        // ICONDIRENTRY
        writer.Write((byte)Size);
        writer.Write((byte)Size);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(imageSize);
        writer.Write(22); // image offset: ICONDIR (6) + ICONDIRENTRY (16)
        // BITMAPINFOHEADER
        writer.Write(40);
        writer.Write(Size);
        writer.Write(Size * 2); // XOR mask + AND mask
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(imageSize);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        // XOR mask: opaque teal pixels, bottom-up.
        for (int i = 0; i < Size * Size; i++)
        {
            writer.Write((byte)128);
            writer.Write((byte)216);
            writer.Write((byte)226);
            writer.Write((byte)255);
        }
        // AND mask: unused once alpha is present.
        writer.Write(new byte[andSize]);
        writer.Flush();
        return stream.ToArray();
    }
}
