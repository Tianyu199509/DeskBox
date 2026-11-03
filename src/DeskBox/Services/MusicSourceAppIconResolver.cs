using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using DeskBox.Helpers;
using DeskBox.Platform;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.Storage.Streams;

namespace DeskBox.Services;

internal static class MusicSourceAppIconResolver
{
    private const int MaxCachedSourceIcons = 32;
    private const int IconExtractSizePixels = 48;
    private const int IconDecodeWidthPixels = 32;
    private const long NegativeCacheRetryMs = 10_000;
    private const uint Th32csSnapProcess = 0x00000002;

    private static readonly ConcurrentDictionary<string, byte[]> s_pngByteCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, long> s_negativeCacheUntilTick =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> s_excludedSystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost.exe",
        "explorer.exe",
        "rundll32.exe",
        "conhost.exe",
        "dllhost.exe",
        "RuntimeBroker.exe",
        "ApplicationFrameHost.exe",
        "DeskBox.exe"
    };

    public static async Task<BitmapImage?> ResolveIconAsync(
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcher,
        string? sourceAppUserModelId,
        string? sourceDisplayName = null)
    {
        if (dispatcher is null || string.IsNullOrWhiteSpace(sourceAppUserModelId))
        {
            return null;
        }

        string cacheKey = sourceAppUserModelId.Trim();
        if (s_pngByteCache.TryGetValue(cacheKey, out byte[]? cachedBytes) && cachedBytes.Length > 0)
        {
            return await IconHelper.CreateBitmapImageAsync(dispatcher, cachedBytes, IconDecodeWidthPixels);
        }

        long nowTick = Environment.TickCount64;
        if (s_negativeCacheUntilTick.TryGetValue(cacheKey, out long retryAfterTick) &&
            nowTick < retryAfterTick)
        {
            return null;
        }

        byte[]? resolvedBytes = await Task.Run(() => TryResolveIconPngBytesAsync(cacheKey, sourceDisplayName));
        if (resolvedBytes is { Length: > 0 })
        {
            s_negativeCacheUntilTick.TryRemove(cacheKey, out _);
            StorePngCache(cacheKey, resolvedBytes);
            return await IconHelper.CreateBitmapImageAsync(dispatcher, resolvedBytes, IconDecodeWidthPixels);
        }

        string? fallbackExePath = await Task.Run(() => TryResolveExecutablePath(cacheKey, sourceDisplayName));
        if (!string.IsNullOrWhiteSpace(fallbackExePath))
        {
            BitmapImage? shellIcon = await IconHelper.GetIconAsync(
                fallbackExePath,
                hideShortcutArrowOverlay: true,
                showImageFilesAsIcons: true,
                decodePixelWidth: IconDecodeWidthPixels,
                cacheScope: "music-source");
            if (shellIcon is not null)
            {
                s_negativeCacheUntilTick.TryRemove(cacheKey, out _);
                return shellIcon;
            }
        }

        s_negativeCacheUntilTick[cacheKey] = nowTick + NegativeCacheRetryMs;
        return null;
    }

    private static async Task<byte[]?> TryResolveIconPngBytesAsync(
        string sourceAppUserModelId,
        string? sourceDisplayName)
    {
        byte[]? appInfoBytes = await TryLoadFromAppInfoAsync(sourceAppUserModelId);
        if (appInfoBytes is { Length: > 0 })
        {
            return appInfoBytes;
        }

        string? exePath = TryResolveExecutablePath(sourceAppUserModelId, sourceDisplayName);
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            byte[]? peIconBytes = IconHelper.GetIndexedIconPngBytes(exePath, 0, IconExtractSizePixels);
            if (peIconBytes is { Length: > 0 })
            {
                return peIconBytes;
            }
        }

        return null;
    }

    private static async Task<byte[]?> TryLoadFromAppInfoAsync(string sourceAppUserModelId)
    {
        try
        {
            AppInfo? appInfo = AppInfo.GetFromAppUserModelId(sourceAppUserModelId);
            AppDisplayInfo? displayInfo = appInfo?.DisplayInfo;
            if (displayInfo is null)
            {
                return null;
            }

            RandomAccessStreamReference logoRef = displayInfo.GetLogo(new Windows.Foundation.Size(48, 48));
            using IRandomAccessStreamWithContentType stream = await logoRef.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > 2 * 1024 * 1024)
            {
                return null;
            }

            byte[] buffer = new byte[(int)stream.Size];
            await stream.ReadAsync(buffer.AsBuffer(), (uint)buffer.Length, InputStreamOptions.None);
            return buffer.Length > 0 ? buffer : null;
        }
        catch
        {
            return null;
        }
    }

    internal static string? TryResolveExecutablePath(
        string sourceAppUserModelId,
        string? sourceDisplayName)
    {
        if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
        {
            return null;
        }

        string trimmed = sourceAppUserModelId.Trim().Trim('"');
        if (Path.IsPathRooted(trimmed) && File.Exists(trimmed))
        {
            return trimmed;
        }

        IReadOnlyList<string> candidates = GetCandidateExecutableNames(trimmed, sourceDisplayName);
        string? runningPath = TryFindRunningProcessPath(trimmed, sourceDisplayName, candidates);
        if (!string.IsNullOrWhiteSpace(runningPath))
        {
            return runningPath;
        }

        foreach (string exeName in candidates)
        {
            string? registryPath = TryLookupAppPathsRegistry(exeName);
            if (!string.IsNullOrWhiteSpace(registryPath))
            {
                return registryPath;
            }
        }

        return null;
    }

    internal static IReadOnlyList<string> GetCandidateExecutableNames(
        string? sourceAppUserModelId,
        string? sourceDisplayName = null)
    {
        var list = new List<string>(6);
        void AddCandidate(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            string normalized = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name
                : $"{name}.exe";
            if (!list.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(normalized);
            }
        }

        string raw = (sourceAppUserModelId ?? string.Empty).Trim();
        string lower = raw.ToLowerInvariant();
        string displayLower = (sourceDisplayName ?? string.Empty).Trim().ToLowerInvariant();

        string beforeBang = raw.Split('!')[0].Trim();
        if (beforeBang.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !beforeBang.Contains('\\') &&
            !beforeBang.Contains('/'))
        {
            AddCandidate(beforeBang);
        }

        if (lower.Contains("qqmusic", StringComparison.Ordinal) ||
            displayLower.Contains("qq音乐", StringComparison.Ordinal) ||
            displayLower.Contains("qqmusic", StringComparison.Ordinal))
        {
            AddCandidate("QQMusic.exe");
        }

        if (lower.Contains("cloudmusic", StringComparison.Ordinal) ||
            lower.Contains("netease", StringComparison.Ordinal) ||
            displayLower.Contains("网易云", StringComparison.Ordinal))
        {
            AddCandidate("cloudmusic.exe");
        }

        if (lower.Contains("kugou", StringComparison.Ordinal) ||
            displayLower.Contains("酷狗", StringComparison.Ordinal))
        {
            AddCandidate("KuGou.exe");
        }

        if (lower.Contains("kuwo", StringComparison.Ordinal) ||
            lower.Contains("kwmusic", StringComparison.Ordinal) ||
            displayLower.Contains("酷我", StringComparison.Ordinal))
        {
            AddCandidate("kwmusic.exe");
        }

        if (lower.Contains("lx-music", StringComparison.Ordinal) ||
            lower.Contains("lxmusic", StringComparison.Ordinal) ||
            displayLower.Contains("洛雪", StringComparison.Ordinal))
        {
            AddCandidate("lx-music-desktop.exe");
        }

        if (lower.Contains("yesplaymusic", StringComparison.Ordinal))
        {
            AddCandidate("YesPlayMusic.exe");
        }

        if (lower.Contains("musicfree", StringComparison.Ordinal))
        {
            AddCandidate("MusicFree.exe");
        }

        if (lower.Contains("spotify", StringComparison.Ordinal))
        {
            AddCandidate("Spotify.exe");
        }

        if (lower.Contains("applemusic", StringComparison.Ordinal) ||
            displayLower.Contains("apple music", StringComparison.Ordinal))
        {
            AddCandidate("AppleMusic.exe");
        }

        if (lower.Contains("itunes", StringComparison.Ordinal))
        {
            AddCandidate("iTunes.exe");
        }

        if (lower.Contains("foobar", StringComparison.Ordinal))
        {
            AddCandidate("foobar2000.exe");
        }

        if (lower.Contains("potplayer", StringComparison.Ordinal))
        {
            AddCandidate("PotPlayerMini64.exe");
            AddCandidate("PotPlayerMini.exe");
        }

        if (lower.Contains("aimp", StringComparison.Ordinal))
        {
            AddCandidate("AIMP.exe");
        }

        if (lower.Contains("musicbee", StringComparison.Ordinal))
        {
            AddCandidate("MusicBee.exe");
        }

        if (lower.Contains("vlc", StringComparison.Ordinal))
        {
            AddCandidate("vlc.exe");
        }

        if (lower.Contains("cider", StringComparison.Ordinal))
        {
            AddCandidate("Cider.exe");
        }

        if (lower.Contains("msedge", StringComparison.Ordinal) ||
            displayLower.Contains("edge", StringComparison.Ordinal))
        {
            AddCandidate("msedge.exe");
        }

        if (lower.Contains("brave", StringComparison.Ordinal) ||
            displayLower.Contains("brave", StringComparison.Ordinal))
        {
            AddCandidate("brave.exe");
        }

        if (lower.Contains("chrome", StringComparison.Ordinal) ||
            displayLower.Contains("chrome", StringComparison.Ordinal))
        {
            AddCandidate("chrome.exe");
        }

        if (lower.Contains("firefox", StringComparison.Ordinal) ||
            lower.Contains("308046b0af4a39cb", StringComparison.Ordinal) ||
            displayLower.Contains("firefox", StringComparison.Ordinal))
        {
            AddCandidate("firefox.exe");
        }

        if (lower.Contains("vivaldi", StringComparison.Ordinal))
        {
            AddCandidate("vivaldi.exe");
        }

        if (lower.Contains("opera", StringComparison.Ordinal))
        {
            AddCandidate("opera.exe");
        }

        if (lower.Contains("arc", StringComparison.Ordinal) &&
            (lower.Equals("arc", StringComparison.Ordinal) || lower.StartsWith("arc.", StringComparison.Ordinal)))
        {
            AddCandidate("Arc.exe");
        }

        if (!string.IsNullOrWhiteSpace(beforeBang) && !beforeBang.Contains(' '))
        {
            string stem = beforeBang.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(beforeBang)
                : beforeBang;
            int underscoreIdx = stem.IndexOf('_');
            if (underscoreIdx > 0)
            {
                stem = stem[..underscoreIdx];
            }

            string[] parts = stem.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                string lastToken = parts[^1];
                if (lastToken.Length >= 3 && IsAsciiAlphaNumericToken(lastToken))
                {
                    AddCandidate(lastToken);
                }

                if (parts.Length > 1 && parts[0].Length >= 3 && IsAsciiAlphaNumericToken(parts[0]))
                {
                    AddCandidate(parts[0]);
                }
            }
        }

        return list;
    }

    private static string? TryFindRunningProcessPath(
        string sourceAppUserModelId,
        string? sourceDisplayName,
        IReadOnlyList<string> candidateExeNames)
    {
        IntPtr snapshot = ProcessDiagnosticsNativeMethods.CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return null;
        }

        string normalizedSource = NormalizeAlphaNum(sourceAppUserModelId);
        string normalizedDisplay = NormalizeAlphaNum(sourceDisplayName);
        string? fuzzyMatchPath = null;

        try
        {
            var entry = new ProcessDiagnosticsNativeMethods.ProcessEntry32
            {
                dwSize = (uint)Marshal.SizeOf<ProcessDiagnosticsNativeMethods.ProcessEntry32>()
            };

            if (!ProcessDiagnosticsNativeMethods.Process32First(snapshot, ref entry))
            {
                return null;
            }

            do
            {
                string exeName = entry.szExeFile;
                if (string.IsNullOrWhiteSpace(exeName) || s_excludedSystemProcesses.Contains(exeName))
                {
                    continue;
                }

                for (int i = 0; i < candidateExeNames.Count; i++)
                {
                    if (string.Equals(exeName, candidateExeNames[i], StringComparison.OrdinalIgnoreCase))
                    {
                        string path = ProcessImageNameResolver.GetProcessExecutablePath(entry.th32ProcessID);
                        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        {
                            return path;
                        }
                    }
                }

                if (fuzzyMatchPath is null)
                {
                    string stem = NormalizeAlphaNum(Path.GetFileNameWithoutExtension(exeName));
                    if (stem.Length >= 4 &&
                        ((normalizedSource.Length >= 4 && normalizedSource.Contains(stem, StringComparison.Ordinal)) ||
                         (normalizedDisplay.Length >= 4 && normalizedDisplay.Contains(stem, StringComparison.Ordinal))))
                    {
                        string path = ProcessImageNameResolver.GetProcessExecutablePath(entry.th32ProcessID);
                        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        {
                            fuzzyMatchPath = path;
                        }
                    }
                }
            }
            while (ProcessDiagnosticsNativeMethods.Process32Next(snapshot, ref entry));

            return fuzzyMatchPath;
        }
        catch
        {
            return null;
        }
        finally
        {
            _ = ProcessDiagnosticsNativeMethods.CloseHandle(snapshot);
        }
    }

    private static string? TryLookupAppPathsRegistry(string exeName)
    {
        string subKey = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}";
        return TryReadAppPathsKey(Registry.CurrentUser, subKey) ??
               TryReadAppPathsKey(Registry.LocalMachine, subKey);
    }

    private static string? TryReadAppPathsKey(RegistryKey root, string subKeyPath)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(subKeyPath, writable: false);
            string? raw = key?.GetValue(name: null) as string;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string candidate = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
            return File.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    private static void StorePngCache(string key, byte[] bytes)
    {
        if (s_pngByteCache.Count >= MaxCachedSourceIcons)
        {
            foreach (string existingKey in s_pngByteCache.Keys)
            {
                if (s_pngByteCache.TryRemove(existingKey, out _))
                {
                    break;
                }
            }
        }

        s_pngByteCache[key] = bytes;
    }

    private static bool IsAsciiAlphaNumericToken(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeAlphaNum(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[Math.Min(value.Length, 256)];
        int count = 0;
        for (int i = 0; i < value.Length && count < buffer.Length; i++)
        {
            char c = value[i];
            if (char.IsLetterOrDigit(c))
            {
                buffer[count++] = char.ToLowerInvariant(c);
            }
        }

        return count == 0 ? string.Empty : new string(buffer[..count]);
    }
}
