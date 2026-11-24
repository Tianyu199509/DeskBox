using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using DeskBox.Helpers;
using DeskBox.Platform;

namespace DeskBox.Tests;

public sealed class SteamShortcutAndDesktopGhostRegressionTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBox.Tests",
        $"steam-url-desktop-ghost-{Guid.NewGuid():N}");

    public SteamShortcutAndDesktopGhostRegressionTests()
    {
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

    [Fact]
    public void FilterMovedDesktopItems_CapturesPersonalAndPublicDesktopMovesOnly()
    {
        string personalDesktop = Path.Combine(_tempRoot, "UserDesktop");
        string publicDesktop = Path.Combine(_tempRoot, "PublicDesktop");
        string managedFolder = Path.Combine(_tempRoot, "ManagedBox");
        string downloads = Path.Combine(_tempRoot, "Downloads");

        string terrariaUrl = Path.Combine(personalDesktop, "Terraria.url");
        string sharedAppLnk = Path.Combine(publicDesktop, "SharedApp.lnk");
        string movedFolder = Path.Combine(personalDesktop, "PortableGame");
        string renamedOnDesktop = Path.Combine(personalDesktop, "Renamed.url");
        string nonDesktopFile = Path.Combine(downloads, "Archive.zip");

        IReadOnlyList<MovedDesktopItem> filtered = DesktopIconPositionInterop.FilterMovedDesktopItems(
            [
                (terrariaUrl, Path.Combine(managedFolder, "Terraria.url"), false),
                (terrariaUrl.ToUpperInvariant(), Path.Combine(managedFolder, "Terraria.url"), false),
                (sharedAppLnk, Path.Combine(managedFolder, "SharedApp.lnk"), false),
                (movedFolder, Path.Combine(managedFolder, "PortableGame"), true),
                (renamedOnDesktop, Path.Combine(personalDesktop, "Renamed2.url"), false),
                (renamedOnDesktop, Path.Combine(publicDesktop, "CrossDesktop.url"), false),
                (nonDesktopFile, Path.Combine(managedFolder, "Archive.zip"), false),
            ],
            personalDesktop,
            publicDesktop);

        Assert.Equal(3, filtered.Count);
        Assert.Contains(filtered, item =>
            string.Equals(item.SourcePath, Path.GetFullPath(terrariaUrl), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.DesktopDirectory, Path.GetFullPath(personalDesktop), StringComparison.OrdinalIgnoreCase) &&
            !item.WasDirectory);
        Assert.Contains(filtered, item =>
            string.Equals(item.SourcePath, Path.GetFullPath(sharedAppLnk), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.DesktopDirectory, Path.GetFullPath(publicDesktop), StringComparison.OrdinalIgnoreCase) &&
            !item.WasDirectory);
        Assert.Contains(filtered, item =>
            string.Equals(item.SourcePath, Path.GetFullPath(movedFolder), StringComparison.OrdinalIgnoreCase) &&
            item.WasDirectory);
    }

    [Fact]
    public void ResolveInternetShortcut_HandlesForwardSlashesUtf16BomAndNonUtf8AnsiBytes()
    {
        string utf8Url = Path.Combine(_tempRoot, "ForwardSlash.url");
        File.WriteAllText(
            utf8Url,
            "[InternetShortcut]\r\nURL=steam://rungameid/105600\r\nIconIndex=0\r\nIconFile=D:/Steam/steam/games/terraria.ico\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        ShortcutInfo? parsedForward = ShortcutHelper.ResolveInternetShortcut(utf8Url);
        Assert.NotNull(parsedForward);
        Assert.Equal("steam://rungameid/105600", parsedForward!.TargetPath);
        Assert.Equal(@"D:\Steam\steam\games\terraria.ico", parsedForward.IconLocation);

        string utf16Url = Path.Combine(_tempRoot, "ChineseUtf16.url");
        File.WriteAllText(
            utf16Url,
            "[InternetShortcut]\r\nURL=steam://rungameid/1669980\r\nIconIndex=2\r\nIconFile=\"D:/游戏库/Steam/steam/games/volcano.ico\"\r\n",
            Encoding.Unicode);

        ShortcutInfo? parsedUtf16 = ShortcutHelper.ResolveInternetShortcut(utf16Url);
        Assert.NotNull(parsedUtf16);
        Assert.Equal(@"D:\游戏库\Steam\steam\games\volcano.ico", parsedUtf16!.IconLocation);
        Assert.Equal(2, parsedUtf16.IconIndex);

        // Non-UTF8 high byte in another INI comment/key must not break parsing via UTF-8 replacement corruption.
        string ansiUrl = Path.Combine(_tempRoot, "AnsiEncoded.url");
        byte[] prefix = Encoding.ASCII.GetBytes("[InternetShortcut]\r\nURL=steam://rungameid/646570\r\nAuthor=");
        byte[] nonUtf8Tail = [0xC4, 0xE3, 0xBA, 0xC3, (byte)'\r', (byte)'\n'];
        byte[] iconLine = Encoding.ASCII.GetBytes("IconFile=C:\\Steam\\steam\\games\\spire.ico\r\nIconIndex=0\r\n");
        File.WriteAllBytes(ansiUrl, [.. prefix, .. nonUtf8Tail, .. iconLine]);

        ShortcutInfo? parsedAnsi = ShortcutHelper.ResolveInternetShortcut(ansiUrl);
        Assert.NotNull(parsedAnsi);
        Assert.Equal("steam://rungameid/646570", parsedAnsi!.TargetPath);
        Assert.Equal(@"C:\Steam\steam\games\spire.ico", parsedAnsi.IconLocation);
    }

    [Theory]
    [InlineData("steam://rungameid/105600", true, "105600")]
    [InlineData("STEAM://RUNGAMEID/646570", true, "646570")]
    [InlineData("steam://launch/322330/Dialog", true, "322330")]
    [InlineData("steam://open/main", false, "")]
    [InlineData("steam://rungameid/notanumber", false, "")]
    [InlineData("https://store.steampowered.com/app/105600", false, "")]
    [InlineData("", false, "")]
    public void TryParseSteamAppId_ExtractsNumericAppIdOnlyForGameLaunchUrls(
        string url,
        bool expectedSuccess,
        string expectedAppId)
    {
        bool actual = SteamShortcutIconResolver.TryParseSteamAppId(url, out string appId);
        Assert.Equal(expectedSuccess, actual);
        Assert.Equal(expectedAppId, appId);
    }

    [Fact]
    public void SteamShortcutIconResolver_RebasesMovedSteamGamesIcoBeforeOtherFallbacks()
    {
        string newSteamRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "NewSteam")).FullName;
        string gamesDir = Directory.CreateDirectory(Path.Combine(newSteamRoot, "steam", "games")).FullName;
        string rebasedIco = Path.Combine(gamesDir, "65161992978d9f0ba3be83f6db88b92bc61a5b9b.ico");
        File.WriteAllBytes(rebasedIco, CreateSolidIcoBytes(32));

        SteamResolvedIcon? resolved = SteamShortcutIconResolver.TryResolve(
            "steam://rungameid/105600",
            @"C:\OldDrive\Steam\steam\games\65161992978d9f0ba3be83f6db88b92bc61a5b9b.ico",
            steamRootsOverride: [newSteamRoot]);

        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(rebasedIco), resolved!.Value.Path);
        Assert.Equal(0, resolved.Value.IconIndex);
        Assert.True(resolved.Value.UsesExplicitIconIndex);
    }

    [Fact]
    public void SteamShortcutIconResolver_FindsPrimaryGameExeAcrossVdfLibraryAndSkipsCrashAndAnticheatHelpers()
    {
        string steamRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "SteamMain")).FullName;
        string mainSteamApps = Directory.CreateDirectory(Path.Combine(steamRoot, "steamapps")).FullName;
        string externalLibrary = Directory.CreateDirectory(Path.Combine(_tempRoot, "ExternalSteamLib")).FullName;
        string extSteamApps = Directory.CreateDirectory(Path.Combine(externalLibrary, "steamapps")).FullName;

        string escapedExternal = externalLibrary.Replace(@"\", @"\\", StringComparison.Ordinal);
        File.WriteAllText(
            Path.Combine(mainSteamApps, "libraryfolders.vdf"),
            "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + escapedExternal + "\"\n\t}\n}\n");

        File.WriteAllText(
            Path.Combine(extSteamApps, "appmanifest_646570.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\t\"646570\"\n\t\"name\"\t\t\"Slay the Spire\"\n\t\"installdir\"\t\t\"SlayTheSpire\"\n}\n");

        string gameDir = Directory.CreateDirectory(Path.Combine(extSteamApps, "common", "SlayTheSpire")).FullName;
        string crashExe = Path.Combine(gameDir, "UnityCrashHandler64.exe");
        string antiCheatExe = Path.Combine(gameDir, "start_protected_game.exe");
        string mainGameExe = Path.Combine(gameDir, "SlayTheSpire.exe");
        File.WriteAllBytes(crashExe, [0x4D, 0x5A, 1, 2]);
        File.WriteAllBytes(antiCheatExe, [0x4D, 0x5A, 1, 2]);
        File.WriteAllBytes(mainGameExe, [0x4D, 0x5A, 1, 2, 3, 4]);

        SteamResolvedIcon? resolved = SteamShortcutIconResolver.TryResolve(
            "steam://rungameid/646570",
            @"C:\Missing\Steam\steam\games\missing.ico",
            steamRootsOverride: [steamRoot],
            executableHasIconProbe: static _ => true);

        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(mainGameExe), resolved!.Value.Path);
        Assert.True(resolved.Value.UsesExplicitIconIndex);
    }

    [Fact]
    public void SteamShortcutIconResolver_SelectsSquareLibraryCacheIconAndRejectsWideBanners()
    {
        string steamRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "SteamCacheOnly")).FullName;
        string appCacheDir = Directory.CreateDirectory(
            Path.Combine(steamRoot, "appcache", "librarycache", "2835570")).FullName;

        // Wide banners and non-square assets that must NEVER be chosen as a square app icon:
        File.WriteAllBytes(Path.Combine(appCacheDir, "header.jpg"), CreateMinimalJpegHeaderBytes(460, 215));
        File.WriteAllBytes(Path.Combine(appCacheDir, "library_600x900.jpg"), CreateMinimalJpegHeaderBytes(600, 900));
        File.WriteAllBytes(
            Path.Combine(appCacheDir, "1111111111111111111111111111111111111111.jpg"),
            CreateMinimalJpegHeaderBytes(184, 69));

        // Actual 32x32 square clienticon JPEG cached by Steam:
        string squareIconJpg = Path.Combine(
            appCacheDir,
            "abcdef0123456789abcdef0123456789abcdef01.jpg");
        File.WriteAllBytes(squareIconJpg, CreateMinimalJpegHeaderBytes(32, 32));

        SteamResolvedIcon? resolved = SteamShortcutIconResolver.TryResolve(
            "steam://rungameid/2835570",
            @"C:\Missing\abcdef0123456789abcdef0123456789abcdef01.ico",
            steamRootsOverride: [steamRoot],
            executableHasIconProbe: static _ => false);

        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(squareIconJpg), resolved!.Value.Path);
        Assert.False(resolved.Value.UsesExplicitIconIndex);
    }

    [Fact]
    public void InternetShortcutLoadDecision_BypassesUrlDllBlueGlobeWhenIconFileMissingAndKeepsProxyForWebBookmarks()
    {
        MethodInfo? evaluateMethod = typeof(IconHelper).GetMethod(
            "EvaluateInternetShortcutLoadDecision",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(evaluateMethod);

        string brokenSteamUrl = Path.Combine(_tempRoot, "BrokenSteam.url");
        string existingIco = Path.Combine(_tempRoot, "ExistingGame.ico");
        File.WriteAllBytes(existingIco, CreateSolidIcoBytes(32));
        string validSteamUrl = Path.Combine(_tempRoot, "ValidSteam.url");
        string webBookmarkUrl = Path.Combine(_tempRoot, "GitHub.url");

        File.WriteAllText(
            brokenSteamUrl,
            $"[InternetShortcut]\r\nURL=steam://rungameid/999999\r\nIconFile=notepad.exe\r\nIconIndex=0\r\n");
        File.WriteAllText(
            validSteamUrl,
            $"[InternetShortcut]\r\nURL=steam://rungameid/105600\r\nIconFile={existingIco}\r\nIconIndex=0\r\n");
        File.WriteAllText(
            webBookmarkUrl,
            "[InternetShortcut]\r\nURL=https://github.com\r\n");

        // Missing relative IconFile ("notepad.exe" not beside .url, recovered via PATH) -> bypasses url.dll blue globe!
        object recoveredDecision = evaluateMethod!.Invoke(null, [brokenSteamUrl, false])!;
        Assert.False(GetBoolProperty(recoveredDecision, "UseShellProxyFirst"));
        Assert.NotNull(GetObjectProperty(recoveredDecision, "FallbackSource"));

        // Valid IconFile + hidden shortcut arrow -> direct extraction (UseShellProxyFirst = false).
        object validHiddenDecision = evaluateMethod.Invoke(null, [validSteamUrl, true])!;
        Assert.False(GetBoolProperty(validHiddenDecision, "UseShellProxyFirst"));
        Assert.NotNull(GetObjectProperty(validHiddenDecision, "FallbackSource"));

        // Valid IconFile + visible shortcut arrow -> Shell overlay composite (UseShellProxyFirst = true).
        object validOverlayDecision = evaluateMethod.Invoke(null, [validSteamUrl, false])!;
        Assert.True(GetBoolProperty(validOverlayDecision, "UseShellProxyFirst"));

        // Web bookmark with no IconFile -> Shell proxy first to preserve :favicon:$DATA.
        object webDecision = evaluateMethod.Invoke(null, [webBookmarkUrl, true])!;
        Assert.True(GetBoolProperty(webDecision, "UseShellProxyFirst"));
    }

    [Theory]
    [InlineData(96, 48, 48, true)]   // Exact Windows 11 32/48 px Steam .url payload at requestedSize=96 (ratio 0.50)
    [InlineData(96, 32, 32, true)]   // 32 px glyph centered inside 96 px canvas (ratio 0.33)
    [InlineData(256, 128, 128, true)] // 128 px glyph centered inside 256 px Jumbo canvas (ratio 0.50)
    [InlineData(96, 52, 86, false)]  // Tall asymmetric game icon (e.g. Dead Cells flame: height 0.90) must NOT crop
    [InlineData(96, 78, 78, false)]  // Standard full-bleed rounded-square icon must NOT crop
    public void PaddedShellCanvasDetection_CropsCenteredSmallGameIconsWithoutTrimmingNormalArtwork(
        int canvasSize,
        int visibleWidth,
        int visibleHeight,
        bool expectedPadded)
    {
        int left = (canvasSize - visibleWidth) / 2;
        int top = (canvasSize - visibleHeight) / 2;
        byte[] payload = CreateBmpV5PayloadWithArtwork(
            canvasSize,
            canvasSize,
            left,
            top,
            visibleWidth,
            visibleHeight);

        Assert.Equal(expectedPadded, ShellThumbnailProxy.IsLikelyPaddedIconPayload(payload));

        byte[] normalized = Assert.IsType<byte[]>(ShellThumbnailProxy.NormalizeIconPayload(payload));
        int outWidth = BitConverter.ToInt32(normalized, 18);
        int outHeight = Math.Abs(BitConverter.ToInt32(normalized, 22));
        if (expectedPadded)
        {
            Assert.Equal(visibleWidth, outWidth);
            Assert.Equal(visibleHeight, outHeight);
            Assert.False(ShellThumbnailProxy.IsLikelyPaddedIconPayload(normalized));
        }
        else
        {
            Assert.Equal(canvasSize, outWidth);
            Assert.Equal(canvasSize, outHeight);
        }
    }

    private static bool GetBoolProperty(object instance, string propertyName) =>
        (bool)instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(instance)!;

    private static object? GetObjectProperty(object instance, string propertyName) =>
        instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(instance);

    private static byte[] CreateSolidIcoBytes(int size)
    {
        int andStride = ((size + 31) / 32) * 4;
        int xorBytes = size * size * 4;
        int andBytes = andStride * size;
        int imageBytes = 40 + xorBytes + andBytes;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((ushort)0);
        bw.Write((ushort)1);
        bw.Write((ushort)1);
        bw.Write((byte)(size >= 256 ? 0 : size));
        bw.Write((byte)(size >= 256 ? 0 : size));
        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write(imageBytes);
        bw.Write(22);
        bw.Write(40);
        bw.Write(size);
        bw.Write(size * 2);
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write(0);
        bw.Write(imageBytes);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        for (int i = 0; i < size * size; i++)
        {
            bw.Write((byte)0x30);
            bw.Write((byte)0x90);
            bw.Write((byte)0xF0);
            bw.Write((byte)0xFF);
        }

        bw.Write(new byte[andBytes]);
        bw.Flush();
        return ms.ToArray();
    }

    private static byte[] CreateMinimalJpegHeaderBytes(ushort width, ushort height)
    {
        // SOI (FF D8) + APP0 JFIF (16B) + SOF0 (FF C0, length 11, precision 8, height, width, 1 component) + EOI (FF D9)
        byte[] bytes = new byte[2 + 18 + 13 + 2];
        int o = 0;
        bytes[o++] = 0xFF;
        bytes[o++] = 0xD8;

        // APP0 segment
        bytes[o++] = 0xFF;
        bytes[o++] = 0xE0;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(o, 2), 16);
        o += 16;

        // SOF0 segment
        bytes[o++] = 0xFF;
        bytes[o++] = 0xC0;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(o, 2), 11);
        o += 2;
        bytes[o++] = 8; // precision
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(o, 2), height);
        o += 2;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(o, 2), width);
        o += 2;
        bytes[o++] = 1; // components
        bytes[o++] = 1;
        bytes[o++] = 0x11;
        bytes[o++] = 0;

        // EOI
        bytes[o++] = 0xFF;
        bytes[o] = 0xD9;
        return bytes;
    }

    private static byte[] CreateBmpV5PayloadWithArtwork(
        int width,
        int height,
        int visibleLeft,
        int visibleTop,
        int visibleWidth,
        int visibleHeight)
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

        for (int y = visibleTop; y < visibleTop + visibleHeight; y++)
        {
            for (int x = visibleLeft; x < visibleLeft + visibleWidth; x++)
            {
                int offset = pixelOffset + (((y * width) + x) * 4);
                payload[offset] = 0x22;
                payload[offset + 1] = 0x66;
                payload[offset + 2] = 0xCC;
                payload[offset + 3] = 0xFF;
            }
        }

        return payload;
    }
}
