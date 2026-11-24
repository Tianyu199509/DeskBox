using System.Buffers.Binary;
using DeskBox.Platform;
using Microsoft.Win32;

namespace DeskBox.Helpers;

internal readonly record struct SteamResolvedIcon(
    string Path,
    int IconIndex,
    bool UsesExplicitIconIndex);

/// <summary>
/// Resolves per-game artwork for Steam <c>.url</c> shortcuts
/// (<c>steam://rungameid/&lt;AppId&gt;</c> / <c>steam://launch/&lt;AppId&gt;</c>) when
/// the stored <c>IconFile</c> is missing, stale after a Steam relocation, or
/// omitted by the Steam client. Prevents Windows <c>url.dll</c> from degrading
/// tiles into the generic <c>url.dll,5</c> blue globe or <c>steam.exe,0</c>.
/// </summary>
internal static class SteamShortcutIconResolver
{
    private const int MaxInstallSearchDepth = 3;
    private const int MaxInstallVisitedDirectories = 48;

    private static readonly string[] ExcludedExecutablePrefixes =
    [
        "unitycrashhandler",
        "crashreport",
        "crashsender",
        "crashpad",
        "unrealcefsubprocess",
        "cefsharp",
        "easyanticheat",
        "start_protected_game",
        "battleye",
        "beservice",
        "vc_redist",
        "vcredist",
        "dxsetup",
        "dotnet",
        "createdump",
        "unins000",
        "uninstall",
        "steamerrorreporter",
    ];

    private static readonly string[] ExcludedLibraryCacheAssetPrefixes =
    [
        "header",
        "library_600x900",
        "library_hero",
        "library_capsule",
        "logo",
        "capsule_",
        "page_bg",
    ];

    internal static bool TryParseSteamAppId(string? url, out string appId)
    {
        appId = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        ReadOnlySpan<char> trimmed = url.AsSpan().Trim();
        ReadOnlySpan<char> remainder;
        if (trimmed.StartsWith("steam://rungameid/", StringComparison.OrdinalIgnoreCase))
        {
            remainder = trimmed["steam://rungameid/".Length..];
        }
        else if (trimmed.StartsWith("steam://launch/", StringComparison.OrdinalIgnoreCase))
        {
            remainder = trimmed["steam://launch/".Length..];
        }
        else
        {
            return false;
        }

        int length = 0;
        while (length < remainder.Length && char.IsAsciiDigit(remainder[length]))
        {
            length++;
        }

        if (length is <= 0 or > 12)
        {
            return false;
        }

        if (length < remainder.Length &&
            remainder[length] is not ('/' or '?' or '#' or '\\'))
        {
            return false;
        }

        appId = remainder[..length].ToString();
        return true;
    }

    internal static SteamResolvedIcon? TryResolve(
        string? targetUrl,
        string? rawIconLocation,
        IReadOnlyList<string>? steamRootsOverride = null,
        Func<string, bool>? executableHasIconProbe = null)
    {
        if (string.IsNullOrWhiteSpace(targetUrl) ||
            !targetUrl.TrimStart().StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        _ = TryParseSteamAppId(targetUrl, out string appId);
        IReadOnlyList<string> steamRoots = steamRootsOverride is not null
            ? NormalizeExistingDirectories(steamRootsOverride)
            : DiscoverSteamRoots(rawIconLocation);
        if (steamRoots.Count == 0)
        {
            return null;
        }

        string normalizedRawIcon = NormalizePathSeparators(rawIconLocation);
        string iconFileName = string.IsNullOrWhiteSpace(normalizedRawIcon)
            ? string.Empty
            : GetSafeFileName(normalizedRawIcon);
        string iconStem = string.IsNullOrWhiteSpace(iconFileName)
            ? string.Empty
            : Path.GetFileNameWithoutExtension(iconFileName);

        // 1. Rebase IconFile (<sha1>.ico) onto discovered Steam root(s) (\steam\games\).
        if (!string.IsNullOrWhiteSpace(iconFileName))
        {
            foreach (string steamRoot in steamRoots)
            {
                string candidate = Path.Combine(steamRoot, "steam", "games", iconFileName);
                if (IsNonEmptyFile(candidate))
                {
                    return new SteamResolvedIcon(Path.GetFullPath(candidate), 0, UsesExplicitIconIndex: true);
                }
            }
        }

        // 2. High-resolution lossless icon (>= 64 px .ico/.png) in appcache\librarycache\<AppId>.
        if (appId.Length > 0)
        {
            SteamResolvedIcon? losslessCached = TryFindLibraryCacheSquareIcon(
                steamRoots,
                appId,
                iconStem,
                minDimension: 64,
                losslessOnly: true);
            if (losslessCached is not null)
            {
                return losslessCached;
            }
        }

        // 3. Primary game executable (with embedded PE icon) via libraryfolders.vdf + appmanifest_<AppId>.acf.
        if (appId.Length > 0)
        {
            Func<string, bool> iconProbe = executableHasIconProbe ?? HasEmbeddedExecutableIcon;
            string? gameExe = TryFindInstalledGameExecutable(steamRoots, appId, iconProbe);
            if (!string.IsNullOrWhiteSpace(gameExe))
            {
                return new SteamResolvedIcon(gameExe, 0, UsesExplicitIconIndex: true);
            }

            // 4. Square cached game icon of any resolution (including Steam's 32x32 <sha1>.jpg clienticon).
            SteamResolvedIcon? anySquareCached = TryFindLibraryCacheSquareIcon(
                steamRoots,
                appId,
                iconStem,
                minDimension: 16,
                losslessOnly: false);
            if (anySquareCached is not null)
            {
                return anySquareCached;
            }
        }

        // 5. Last resort: generic steam.exe icon (still preferable to url.dll,5 blue globe).
        foreach (string steamRoot in steamRoots)
        {
            string steamExe = Path.Combine(steamRoot, "steam.exe");
            if (IsNonEmptyFile(steamExe))
            {
                return new SteamResolvedIcon(Path.GetFullPath(steamExe), 0, UsesExplicitIconIndex: true);
            }
        }

        return null;
    }

    internal static IReadOnlyList<string> DiscoverSteamRoots(string? rawIconLocation)
    {
        var candidates = new List<string>(10);

        AddStaleIconPathSteamRootCandidates(candidates, rawIconLocation);

        try
        {
            using RegistryKey? cuSteam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (cuSteam?.GetValue("SteamPath") is string steamPath)
            {
                candidates.Add(NormalizePathSeparators(steamPath));
            }

            if (cuSteam?.GetValue("SteamExe") is string steamExe)
            {
                string? exeDir = Path.GetDirectoryName(NormalizePathSeparators(steamExe));
                if (!string.IsNullOrWhiteSpace(exeDir))
                {
                    candidates.Add(exeDir);
                }
            }
        }
        catch
        {
        }

        try
        {
            using RegistryKey? lmWow = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (lmWow?.GetValue("InstallPath") is string wowPath)
            {
                candidates.Add(NormalizePathSeparators(wowPath));
            }

            using RegistryKey? lmNative = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (lmNative?.GetValue("InstallPath") is string nativePath)
            {
                candidates.Add(NormalizePathSeparators(nativePath));
            }
        }
        catch
        {
        }

        try
        {
            using RegistryKey? protocolCmd = Registry.ClassesRoot.OpenSubKey(@"steam\Shell\Open\Command");
            if (protocolCmd?.GetValue(null) is string cmdLine &&
                TryExtractQuotedPath(cmdLine, out string protocolExe))
            {
                string? dir = Path.GetDirectoryName(NormalizePathSeparators(protocolExe));
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    candidates.Add(dir);
                }
            }
        }
        catch
        {
        }

        candidates.Add(@"C:\Program Files (x86)\Steam");
        candidates.Add(@"C:\Program Files\Steam");

        return NormalizeExistingDirectories(candidates);
    }

    private static void AddStaleIconPathSteamRootCandidates(List<string> candidates, string? rawIconLocation)
    {
        string normalized = NormalizePathSeparators(rawIconLocation);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        const string marker = @"\steam\games\";
        int markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 0)
        {
            return;
        }

        string rootFromIcon = normalized[..markerIndex];
        candidates.Add(rootFromIcon);

        // If Steam moved from C:\...\Steam to D:\...\Steam (same relative path on another fixed drive), probe fixed drives.
        if (rootFromIcon.Length >= 3 &&
            char.IsAsciiLetter(rootFromIcon[0]) &&
            rootFromIcon[1] == ':' &&
            rootFromIcon[2] == '\\')
        {
            string tail = rootFromIcon[2..];
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    if (drive.DriveType == DriveType.Fixed && drive.Name.Length >= 2)
                    {
                        candidates.Add(drive.Name[..2] + tail);
                    }
                }
            }
            catch
            {
            }
        }
    }

    private static SteamResolvedIcon? TryFindLibraryCacheSquareIcon(
        IReadOnlyList<string> steamRoots,
        string appId,
        string preferredStem,
        int minDimension,
        bool losslessOnly)
    {
        string? bestPath = null;
        int bestScore = -1;
        bool bestIsIco = false;

        foreach (string steamRoot in steamRoots)
        {
            string libraryCacheRoot = Path.Combine(steamRoot, "appcache", "librarycache");
            if (!Directory.Exists(libraryCacheRoot))
            {
                continue;
            }

            // Legacy flat <AppId>_icon.* layout
            ReadOnlySpan<string> extensions = losslessOnly
                ? [".ico", ".png"]
                : [".ico", ".png", ".jpg", ".jpeg"];
            foreach (string ext in extensions)
            {
                string flatCandidate = Path.Combine(libraryCacheRoot, $"{appId}_icon{ext}");
                EvaluateLibraryCacheCandidate(
                    flatCandidate,
                    preferredStem,
                    minDimension,
                    ref bestPath,
                    ref bestScore,
                    ref bestIsIco);
            }

            // Modern Steam v3 per-app directory: appcache\librarycache\<AppId>\
            string appCacheDir = Path.Combine(libraryCacheRoot, appId);
            if (!Directory.Exists(appCacheDir))
            {
                continue;
            }

            try
            {
                foreach (string file in Directory.EnumerateFiles(appCacheDir))
                {
                    string ext = Path.GetExtension(file);
                    if (losslessOnly)
                    {
                        if (!ext.Equals(".ico", StringComparison.OrdinalIgnoreCase) &&
                            !ext.Equals(".png", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }
                    else if (!ext.Equals(".ico", StringComparison.OrdinalIgnoreCase) &&
                             !ext.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
                             !ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
                             !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string stem = Path.GetFileNameWithoutExtension(file);
                    if (IsExcludedLibraryCacheAsset(stem))
                    {
                        continue;
                    }

                    EvaluateLibraryCacheCandidate(
                        file,
                        preferredStem,
                        minDimension,
                        ref bestPath,
                        ref bestScore,
                        ref bestIsIco);
                }
            }
            catch
            {
            }
        }

        return bestPath is null
            ? null
            : new SteamResolvedIcon(
                Path.GetFullPath(bestPath),
                0,
                UsesExplicitIconIndex: bestIsIco);
    }

    private static void EvaluateLibraryCacheCandidate(
        string candidatePath,
        string preferredStem,
        int minDimension,
        ref string? bestPath,
        ref int bestScore,
        ref bool bestIsIco)
    {
        if (!TryReadSquareImageDimension(candidatePath, out int dimension) ||
            dimension < minDimension ||
            dimension > 512)
        {
            return;
        }

        string ext = Path.GetExtension(candidatePath);
        bool isIco = ext.Equals(".ico", StringComparison.OrdinalIgnoreCase);
        bool isPng = ext.Equals(".png", StringComparison.OrdinalIgnoreCase);
        string stem = Path.GetFileNameWithoutExtension(candidatePath);
        bool matchesPreferredStem = !string.IsNullOrEmpty(preferredStem) &&
            stem.Equals(preferredStem, StringComparison.OrdinalIgnoreCase);

        int score = (dimension * 10) +
                    (matchesPreferredStem ? 5_000 : 0) +
                    (isIco ? 400 : isPng ? 200 : 0);
        if (score > bestScore)
        {
            bestScore = score;
            bestPath = candidatePath;
            bestIsIco = isIco;
        }
    }

    private static bool IsExcludedLibraryCacheAsset(string stem)
    {
        foreach (string prefix in ExcludedLibraryCacheAssetPrefixes)
        {
            if (stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool TryReadSquareImageDimension(string path, out int dimension)
    {
        dimension = 0;
        if (!IsNonEmptyFile(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[32];
            int read = stream.Read(header);
            if (read < 6)
            {
                return false;
            }

            // PNG: 89 50 4E 47 0D 0A 1A 0A + IHDR at offset 16..24
            if (read >= 24 &&
                header[0] == 0x89 &&
                header[1] == (byte)'P' &&
                header[2] == (byte)'N' &&
                header[3] == (byte)'G')
            {
                int width = BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4));
                int height = BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4));
                if (width > 0 && width == height)
                {
                    dimension = width;
                    return true;
                }

                return false;
            }

            // ICO: 00 00 01 00 <count:u16>, followed by 16-byte ICONDIRENTRY records
            if (header[0] == 0 &&
                header[1] == 0 &&
                header[2] == 1 &&
                header[3] == 0)
            {
                ushort count = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(4, 2));
                if (count == 0 || count > 64)
                {
                    return false;
                }

                stream.Position = 6;
                Span<byte> entry = stackalloc byte[16];
                int maxSquare = 0;
                for (int i = 0; i < count; i++)
                {
                    if (stream.Read(entry) < 16)
                    {
                        break;
                    }

                    int w = entry[0] == 0 ? 256 : entry[0];
                    int h = entry[1] == 0 ? 256 : entry[1];
                    if (w == h && w > maxSquare)
                    {
                        maxSquare = w;
                    }
                }

                if (maxSquare > 0)
                {
                    dimension = maxSquare;
                    return true;
                }

                return false;
            }

            // JPEG: FF D8 ... scan markers for SOF0/SOF1/SOF2 (C0, C1, C2)
            if (header[0] == 0xFF && header[1] == 0xD8)
            {
                return TryReadJpegSquareDimension(stream, out dimension);
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryReadJpegSquareDimension(FileStream stream, out int dimension)
    {
        dimension = 0;
        stream.Position = 2;
        Span<byte> buf = stackalloc byte[9];
        int markersVisited = 0;

        while (markersVisited++ < 64)
        {
            int b = stream.ReadByte();
            if (b < 0)
            {
                return false;
            }

            if (b != 0xFF)
            {
                continue;
            }

            int marker;
            do
            {
                marker = stream.ReadByte();
                if (marker < 0)
                {
                    return false;
                }
            }
            while (marker == 0xFF);

            if (marker is 0xD8 or 0xD9 or 0x00)
            {
                continue;
            }

            if (stream.Read(buf[..2]) < 2)
            {
                return false;
            }

            ushort segmentLength = BinaryPrimitives.ReadUInt16BigEndian(buf[..2]);
            if (segmentLength < 2)
            {
                return false;
            }

            // SOF0 (baseline), SOF1 (extended sequential), SOF2 (progressive)
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                if (segmentLength < 7 || stream.Read(buf[..5]) < 5)
                {
                    return false;
                }

                int height = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(1, 2));
                int width = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(3, 2));
                if (width > 0 && width == height)
                {
                    dimension = width;
                    return true;
                }

                return false;
            }

            stream.Seek(segmentLength - 2, SeekOrigin.Current);
        }

        return false;
    }

    private static string? TryFindInstalledGameExecutable(
        IReadOnlyList<string> steamRoots,
        string appId,
        Func<string, bool> executableHasIconProbe)
    {
        IReadOnlyList<string> libraryRoots = DiscoverLibraryFolders(steamRoots);
        foreach (string libraryRoot in libraryRoots)
        {
            string steamAppsDir = Path.Combine(libraryRoot, "steamapps");
            string manifestPath = Path.Combine(steamAppsDir, $"appmanifest_{appId}.acf");
            if (!File.Exists(manifestPath) ||
                !TryReadAppManifest(manifestPath, out string installDir, out string gameName))
            {
                continue;
            }

            string gameDirectory = Path.Combine(steamAppsDir, "common", installDir);
            if (!Directory.Exists(gameDirectory))
            {
                continue;
            }

            string? bestExe = SelectPrimaryGameExecutable(
                gameDirectory,
                installDir,
                gameName,
                executableHasIconProbe);
            if (!string.IsNullOrWhiteSpace(bestExe))
            {
                return bestExe;
            }
        }

        return null;
    }

    internal static IReadOnlyList<string> DiscoverLibraryFolders(IReadOnlyList<string> steamRoots)
    {
        var libraries = new List<string>(steamRoots.Count * 2);
        foreach (string steamRoot in steamRoots)
        {
            libraries.Add(steamRoot);
            string vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdfPath))
            {
                continue;
            }

            try
            {
                foreach (string rawLine in File.ReadLines(vdfPath))
                {
                    if (!TryParseVdfKeyValue(rawLine, out string key, out string value))
                    {
                        continue;
                    }

                    if (key.Equals("path", StringComparison.OrdinalIgnoreCase) ||
                        int.TryParse(key, out _))
                    {
                        string unescaped = value.Replace(@"\\", @"\", StringComparison.Ordinal);
                        if (Path.IsPathRooted(unescaped))
                        {
                            libraries.Add(unescaped);
                        }
                    }
                }
            }
            catch
            {
            }
        }

        return NormalizeExistingDirectories(libraries);
    }

    internal static bool TryReadAppManifest(
        string manifestPath,
        out string installDir,
        out string gameName)
    {
        installDir = string.Empty;
        gameName = string.Empty;
        try
        {
            foreach (string rawLine in File.ReadLines(manifestPath))
            {
                if (!TryParseVdfKeyValue(rawLine, out string key, out string value))
                {
                    continue;
                }

                if (key.Equals("installdir", StringComparison.OrdinalIgnoreCase))
                {
                    installDir = value.Replace(@"\\", @"\", StringComparison.Ordinal).Trim();
                }
                else if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    gameName = value.Trim();
                }

                if (installDir.Length > 0 && gameName.Length > 0)
                {
                    break;
                }
            }
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(installDir) ||
            installDir.Contains("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(installDir))
        {
            return false;
        }

        return true;
    }

    private static string? SelectPrimaryGameExecutable(
        string gameRootDirectory,
        string installDirName,
        string gameName,
        Func<string, bool> executableHasIconProbe)
    {
        string normalizedInstallToken = NormalizeComparisonToken(installDirName);
        string normalizedNameToken = NormalizeComparisonToken(gameName);

        var candidates = new List<(string ExePath, int Depth, long Length)>();
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((gameRootDirectory, 0));
        int visitedDirs = 0;

        while (queue.Count > 0 && visitedDirs < MaxInstallVisitedDirectories)
        {
            (string currentDir, int depth) = queue.Dequeue();
            visitedDirs++;

            try
            {
                foreach (string file in Directory.EnumerateFiles(currentDir, "*.exe"))
                {
                    string stem = Path.GetFileNameWithoutExtension(file);
                    if (IsExcludedExecutableStem(stem))
                    {
                        continue;
                    }

                    long length = 0;
                    try
                    {
                        length = new FileInfo(file).Length;
                    }
                    catch
                    {
                    }

                    if (length <= 0)
                    {
                        continue;
                    }

                    candidates.Add((file, depth, length));
                }

                if (depth < MaxInstallSearchDepth)
                {
                    foreach (string subDir in Directory.EnumerateDirectories(currentDir))
                    {
                        string dirName = Path.GetFileName(subDir);
                        if (dirName.StartsWith("_CommonRedist", StringComparison.OrdinalIgnoreCase) ||
                            dirName.Equals("Redist", StringComparison.OrdinalIgnoreCase) ||
                            dirName.Equals("DirectX", StringComparison.OrdinalIgnoreCase) ||
                            dirName.Equals("MonoBleedingEdge", StringComparison.OrdinalIgnoreCase) ||
                            dirName.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        queue.Enqueue((subDir, depth + 1));
                    }
                }
            }
            catch
            {
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        foreach ((string exePath, _, _) in candidates
                     .OrderByDescending(c => ScoreExecutableCandidate(
                         c.ExePath,
                         c.Depth,
                         c.Length,
                         normalizedInstallToken,
                         normalizedNameToken)))
        {
            if (executableHasIconProbe(exePath))
            {
                return Path.GetFullPath(exePath);
            }
        }

        return null;
    }

    private static int ScoreExecutableCandidate(
        string exePath,
        int depth,
        long fileLength,
        string normalizedInstallToken,
        string normalizedNameToken)
    {
        string stem = Path.GetFileNameWithoutExtension(exePath);
        string token = NormalizeComparisonToken(stem);
        int score = Math.Max(0, 300 - (depth * 80));

        if (token.Length > 0)
        {
            if (token.Equals(normalizedInstallToken, StringComparison.OrdinalIgnoreCase) ||
                token.Equals(normalizedNameToken, StringComparison.OrdinalIgnoreCase))
            {
                score += 2_000;
            }
            else if ((normalizedInstallToken.Length >= 3 &&
                      (token.Contains(normalizedInstallToken, StringComparison.OrdinalIgnoreCase) ||
                       normalizedInstallToken.Contains(token, StringComparison.OrdinalIgnoreCase))) ||
                     (normalizedNameToken.Length >= 3 &&
                      (token.Contains(normalizedNameToken, StringComparison.OrdinalIgnoreCase) ||
                       normalizedNameToken.Contains(token, StringComparison.OrdinalIgnoreCase))))
            {
                score += 1_000;
            }
        }

        if (stem.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase))
        {
            score += 400;
        }

        if (stem.Contains("launcher", StringComparison.OrdinalIgnoreCase) ||
            stem.Contains("server", StringComparison.OrdinalIgnoreCase) ||
            stem.Contains("config", StringComparison.OrdinalIgnoreCase) ||
            stem.Contains("editor", StringComparison.OrdinalIgnoreCase))
        {
            score -= 350;
        }

        // Tie-break toward larger main binaries over tiny stub wrappers.
        score += (int)Math.Min(200, fileLength / (512 * 1024));
        return score;
    }

    private static bool IsExcludedExecutableStem(string stem)
    {
        foreach (string prefix in ExcludedExecutablePrefixes)
        {
            if (stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static unsafe bool HasEmbeddedExecutableIcon(string exePath)
    {
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            int hr = ShellIconNativeMethods.SHDefExtractIcon(exePath, 0, 0, out hIcon, null, 48);
            return hr == 0 && hIcon != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hIcon != IntPtr.Zero)
            {
                ShellIconNativeMethods.DestroyIcon(hIcon);
            }
        }
    }

    private static bool TryParseVdfKeyValue(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        ReadOnlySpan<char> span = line.AsSpan().Trim();
        if (span.Length < 4 || span[0] != '"')
        {
            return false;
        }

        int firstClose = FindClosingVdfQuote(span, 1);
        if (firstClose <= 1)
        {
            return false;
        }

        ReadOnlySpan<char> afterKey = span[(firstClose + 1)..].TrimStart();
        if (afterKey.Length < 2 || afterKey[0] != '"')
        {
            return false;
        }

        int secondClose = FindClosingVdfQuote(afterKey, 1);
        if (secondClose < 1)
        {
            return false;
        }

        key = span[1..firstClose].ToString();
        value = afterKey[1..secondClose].ToString();
        return true;
    }

    private static int FindClosingVdfQuote(ReadOnlySpan<char> span, int startIndex)
    {
        for (int i = startIndex; i < span.Length; i++)
        {
            if (span[i] == '"')
            {
                int backslashes = 0;
                for (int j = i - 1; j >= startIndex && span[j] == '\\'; j--)
                {
                    backslashes++;
                }

                if ((backslashes & 1) == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string NormalizeComparisonToken(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[Math.Min(input.Length, 128)];
        int written = 0;
        foreach (char ch in input)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[written++] = char.ToLowerInvariant(ch);
                if (written == buffer.Length)
                {
                    break;
                }
            }
        }

        return buffer[..written].ToString();
    }

    private static bool TryExtractQuotedPath(string commandLine, out string path)
    {
        path = string.Empty;
        int firstQuote = commandLine.IndexOf('"');
        if (firstQuote < 0)
        {
            return false;
        }

        int secondQuote = commandLine.IndexOf('"', firstQuote + 1);
        if (secondQuote <= firstQuote + 1)
        {
            return false;
        }

        path = commandLine[(firstQuote + 1)..secondQuote];
        return true;
    }

    private static string NormalizePathSeparators(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Trim().Trim('"').Replace('/', '\\');

    private static string GetSafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsNonEmptyFile(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string> NormalizeExistingDirectories(IEnumerable<string> rawDirectories)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in rawDirectories)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            try
            {
                string full = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(NormalizePathSeparators(raw)));
                if (seen.Add(full) && Directory.Exists(full))
                {
                    result.Add(full);
                }
            }
            catch
            {
            }
        }

        return result;
    }
}
