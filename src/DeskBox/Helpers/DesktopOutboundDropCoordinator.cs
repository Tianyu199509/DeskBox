using DeskBox.Controls;
using DeskBox.Services;

namespace DeskBox.Helpers;

/// <summary>
/// Coordinates bounded network reachability probes for outbound Shell file drag
/// payloads and suppresses expected Desktop arrival paths so auto-organization
/// does not immediately re-absorb files the user intentionally dropped onto Desktop.
/// </summary>
internal static class DesktopOutboundDropCoordinator
{
    /// <summary>
    /// Fast lexical / drive-type check with zero remote SMB or reparse-point I/O.
    /// Used on UI-thread drag-start paths so an offline UNC or mapped network share
    /// never blocks the XAML STA before the bounded background probe runs.
    /// </summary>
    internal static bool IsFastNetworkCandidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            FileService.IsNetworkVolumePath(path))
        {
            return true;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(@"\\", StringComparison.Ordinal) ||
                   FileService.IsNetworkVolumePath(fullPath);
        }
        catch
        {
            return false;
        }
    }

    internal static bool MightTouchNetworkPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (IsFastNetworkCandidatePath(path))
        {
            return true;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);

            // Inspect only local reparse buffers so symlinks/junctions pointing to an
            // offline SMB/WebDAV share are pre-probed asynchronously before Shell COM.
            string? current = Path.TrimEndingDirectorySeparator(fullPath);
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    FileAttributes attrs = File.GetAttributes(current);
                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                    {
                        FileSystemInfo info = Directory.Exists(current)
                            ? new DirectoryInfo(current)
                            : new FileInfo(current);
                        if (info.ResolveLinkTarget(returnFinalTarget: false) is { FullName: { } targetPath } &&
                            IsFastNetworkCandidatePath(targetPath))
                        {
                            return true;
                        }
                    }
                }

                string? parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent) ||
                    string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = parent;
            }
        }
        catch
        {
        }

        return false;
    }

    internal readonly record struct BoundedSourcePathProbeResult(
        bool TimedOut,
        string[] ExistingPaths);

    internal static BoundedSourcePathProbeResult FilterExistingPathsWithBoundedNetworkProbe(
        IReadOnlyList<string> sourcePaths,
        int timeoutMs = 650)
    {
        if (sourcePaths.Count == 0)
        {
            return new BoundedSourcePathProbeResult(TimedOut: false, []);
        }

        var networkIndices = new List<int>();
        var existsMask = new bool[sourcePaths.Count];
        for (int i = 0; i < sourcePaths.Count; i++)
        {
            string? path = sourcePaths[i];
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (MightTouchNetworkPath(path))
            {
                networkIndices.Add(i);
            }
            else
            {
                existsMask[i] = File.Exists(path) || Directory.Exists(path);
            }
        }

        if (networkIndices.Count > 0)
        {
            string[] networkSnapshot = new string[networkIndices.Count];
            for (int k = 0; k < networkIndices.Count; k++)
            {
                networkSnapshot[k] = sourcePaths[networkIndices[k]];
            }

            bool[] networkExists = new bool[networkSnapshot.Length];
            using var done = new ManualResetEventSlim(false);
            var probeThread = new Thread(() =>
            {
                try
                {
                    for (int k = 0; k < networkSnapshot.Length; k++)
                    {
                        string candidate = networkSnapshot[k];
                        networkExists[k] = File.Exists(candidate) || Directory.Exists(candidate);
                    }
                }
                catch
                {
                }

                try
                {
                    done.Set();
                }
                catch (ObjectDisposedException)
                {
                }
            })
            {
                IsBackground = true,
                Name = "DeskBox.ShellDragUncReachabilityProbe",
            };
            probeThread.SetApartmentState(ApartmentState.STA);
            probeThread.Start();

            if (!done.Wait(timeoutMs))
            {
                return new BoundedSourcePathProbeResult(TimedOut: true, []);
            }

            for (int k = 0; k < networkIndices.Count; k++)
            {
                existsMask[networkIndices[k]] = Volatile.Read(ref networkExists[k]);
            }
        }

        var existing = new List<string>(sourcePaths.Count);
        for (int i = 0; i < sourcePaths.Count; i++)
        {
            if (existsMask[i])
            {
                existing.Add(sourcePaths[i]);
            }
        }

        return new BoundedSourcePathProbeResult(TimedOut: false, [.. existing]);
    }

    internal static nint TryCreateShellDataObjectWithTimeout(
        IReadOnlyList<string> sourcePaths,
        int timeoutMs) =>
        TryCreateShellDataObjectWithTimeout(sourcePaths, timeoutMs, out _);

    internal static nint TryCreateShellDataObjectWithTimeout(
        IReadOnlyList<string> sourcePaths,
        int timeoutMs,
        out string[] resolvedPhysicalPaths,
        bool networkReachabilityVerified = false)
    {
        resolvedPhysicalPaths = [];
        if (networkReachabilityVerified)
        {
            return TryCreateShellDataObjectSafe(sourcePaths, out resolvedPhysicalPaths);
        }

        bool touchesNetwork = false;
        for (int i = 0; i < sourcePaths.Count; i++)
        {
            string? path = sourcePaths[i];
            if (!string.IsNullOrWhiteSpace(path) && MightTouchNetworkPath(path))
            {
                touchesNetwork = true;
                break;
            }
        }

        if (!touchesNetwork)
        {
            return TryCreateShellDataObjectSafe(sourcePaths, out resolvedPhysicalPaths);
        }

        // Warm and bound SMB / \\wsl / mapped-network / network-reparse reachability on
        // a background STA thread so CreateShellDataObject itself always runs on the calling
        // UI STA thread (keeping Shell COM proxies apartment-safe without freezing).
        BoundedSourcePathProbeResult probe = FilterExistingPathsWithBoundedNetworkProbe(
            sourcePaths,
            timeoutMs);
        if (probe.TimedOut || probe.ExistingPaths.Length != sourcePaths.Count)
        {
            return 0;
        }

        return TryCreateShellDataObjectSafe(probe.ExistingPaths, out resolvedPhysicalPaths);
    }

    private static nint TryCreateShellDataObjectSafe(
        IReadOnlyList<string> sourcePaths,
        out string[] resolvedPhysicalPaths)
    {
        resolvedPhysicalPaths = [];
        try
        {
            return NativeShellFileDragProvider.CreateShellDataObject(
                sourcePaths,
                out resolvedPhysicalPaths);
        }
        catch (Exception ex)
        {
            App.Log($"[DragProtocol] CreateShellDataObject failed: {ex.Message}");
            return 0;
        }
    }

    private static bool RunBoundedStaProbe(string threadName, int timeoutMs, Func<bool> probe)
    {
        bool result = false;
        using var done = new ManualResetEventSlim(false);
        var probeThread = new Thread(() =>
        {
            bool succeeded = false;
            try
            {
                succeeded = probe();
            }
            catch
            {
                succeeded = false;
            }

            Volatile.Write(ref result, succeeded);
            try
            {
                done.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        })
        {
            IsBackground = true,
            Name = threadName,
        };
        probeThread.SetApartmentState(ApartmentState.STA);
        probeThread.Start();

        return done.Wait(timeoutMs) && Volatile.Read(ref result);
    }

    internal static Task<bool> IsDestinationDirectoryReachableAsync(
        string? destinationFolder,
        int timeoutMs = 450)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder))
        {
            return Task.FromResult(false);
        }

        return Task.Run(() => RunBoundedStaProbe(
            "DeskBox.DestinationReachabilityProbe",
            timeoutMs,
            () => Directory.Exists(destinationFolder)));
    }

    internal static bool IsUserDesktopPath(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return false;
        }

        string userDesktop = Environment.GetFolderPath(
            Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(userDesktop))
        {
            return false;
        }

        try
        {
            string normalizedFolder = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(folderPath.Trim()));
            string normalizedDesktop = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(userDesktop.Trim()));
            return string.Equals(
                normalizedFolder,
                normalizedDesktop,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static void SuppressDesktopArrivals(
        SettingsService? settingsService,
        OrganizerService? organizerService,
        IReadOnlyCollection<string> sourcePaths,
        string sourceTag)
    {
        if (organizerService is not null && sourcePaths.Count > 0)
        {
            // User explicitly dragged items onto a chosen desktop location:
            // clear any pre-organization coordinates so a later move or
            // refresh never pulls them back to an older spot.
            organizerService.DesktopIconPositions?.ForgetEntriesInBackground(sourcePaths);
        }

        if (settingsService is null ||
            organizerService is null ||
            sourcePaths.Count == 0 ||
            !settingsService.Settings.DesktopOrganization.DesktopAutoOrganizationEnabled)
        {
            return;
        }

        try
        {
            TimeSpan organizationDelay = DesktopAutoOrganizationPolicy.GetDelay(
                settingsService.Settings);
            string userDesktop = Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(userDesktop))
            {
                return;
            }

            // Desktop auto-organization monitors only the user's personal Desktop,
            // never CommonDesktopDirectory. Restricting candidates to the personal
            // desktop and the active culture + English copy/shortcut names prevents
            // multi-file drags (16+ items) from overflowing MaxEntries and evicting
            // their own claims.
            var arrivals = new List<(string SourcePath, string DestinationPath)>(
                sourcePaths.Count * 8);
            foreach (string sourcePath in sourcePaths)
            {
                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    continue;
                }

                string name = Path.GetFileName(sourcePath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                bool existsAsDirectory = Directory.Exists(sourcePath);
                bool existsAsFile = !existsAsDirectory && File.Exists(sourcePath);
                if (existsAsDirectory || existsAsFile || !name.Contains('.'))
                {
                    foreach (string candidateName in GetDesktopArrivalNameCandidates(name, existsAsDirectory))
                    {
                        arrivals.Add((sourcePath, Path.Combine(userDesktop, candidateName)));
                    }
                }
                else
                {
                    // Same-volume Move can relocate a dotted folder (e.g. "Project.v2")
                    // before DropCompleted runs; when the source is already gone, claim
                    // both file- and directory-style conflict names.
                    var perSourceSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string candidateName in GetDesktopArrivalNameCandidates(name, isDirectory: false)
                        .Concat(GetDesktopArrivalNameCandidates(name, isDirectory: true)))
                    {
                        if (perSourceSeen.Add(candidateName))
                        {
                            arrivals.Add((sourcePath, Path.Combine(userDesktop, candidateName)));
                        }
                    }
                }
            }

            if (arrivals.Count == 0)
            {
                return;
            }

            organizerService.AutoOrganizationSuppressions.SuppressDraggedArrivals(
                arrivals,
                organizationDelay);
            App.LogVerbose(
                $"[DragProtocol] stage=DesktopArrivalSuppressed " +
                $"source={sourceTag} names={arrivals.Count}");
        }
        catch (Exception ex)
        {
            App.Log(
                $"[DragProtocol] Desktop arrival suppression failed " +
                $"source={sourceTag}: {ex.Message}");
        }
    }

    internal static IEnumerable<string> GetDesktopArrivalNameCandidates(
        string name,
        bool isDirectory,
        string? uiLanguageName = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfNew(List<string> target, string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
            {
                target.Add(value);
            }
        }

        var results = new List<string>(10);
        AddIfNew(results, name);

        string activeCulture = !string.IsNullOrWhiteSpace(uiLanguageName)
            ? uiLanguageName
            : System.Globalization.CultureInfo.CurrentUICulture.Name;
        (string? localizedCopySuffix, string? localizedShortcutSuffix) =
            ResolveLocalizedSuffixesForCulture(activeCulture);

        string stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        string extension = isDirectory ? string.Empty : Path.GetExtension(name);
        if (string.IsNullOrEmpty(stem))
        {
            stem = name;
            extension = string.Empty;
        }

        AddIfNew(results, $"{stem} (2){extension}");
        AddIfNew(results, $"{stem} (3){extension}");
        AddIfNew(results, $"{stem} - Copy{extension}");
        if (!string.IsNullOrEmpty(localizedCopySuffix))
        {
            AddIfNew(results, $"{stem}{localizedCopySuffix}{extension}");
        }

        // Alt+Drag / Ctrl+Shift+Drag creates a .lnk shortcut on Desktop.
        if (!string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            AddIfNew(results, $"{name}.lnk");
            AddIfNew(results, $"{stem}.lnk");
            AddIfNew(results, $"{stem} - Shortcut.lnk");
            if (!string.IsNullOrEmpty(localizedShortcutSuffix))
            {
                AddIfNew(results, $"{stem}{localizedShortcutSuffix}.lnk");
                AddIfNew(results, $"{name}{localizedShortcutSuffix}.lnk");
            }
        }

        return results;
    }

    private static readonly (string CulturePrefix, string CopySuffix, string? ShortcutSuffix)[] LocalizedCultureSuffixes =
    [
        ("zh-TW", " - 複製", " - 捷徑"),
        ("zh-HK", " - 複製", " - 捷徑"),
        ("zh-Hant", " - 複製", " - 捷徑"),
        ("zh", " - 副本", " - 快捷方式"),
        ("ja", " - コピー", " - ショートカット"),
        ("ko", " - 복사본", " - 바로 가기"),
        ("de", " - Kopie", " - Verknüpfung"),
        ("fr", " - Copie", " - Raccourci"),
        ("es", " - Copia", null),
        ("pt", " - Cópia", null),
        ("ru", " - копия", null),
        ("ar", " - نسخة", null),
        ("hi", " - प्रतिलिपि", null),
        ("bn", " - প্রতিलिपि", null),
    ];

    private static (string? CopySuffix, string? ShortcutSuffix) ResolveLocalizedSuffixesForCulture(
        string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return (null, null);
        }

        foreach ((string prefix, string copySuffix, string? shortcutSuffix) in LocalizedCultureSuffixes)
        {
            if (cultureName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return (copySuffix, shortcutSuffix);
            }
        }

        return (null, null);
    }

    internal static IEnumerable<string> GetAllArrivalNameCandidates(string name)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in GetArrivalNameCandidates(name, isDirectory: false))
        {
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }

        if (name.Contains('.'))
        {
            foreach (string dirCandidate in GetArrivalNameCandidates(name, isDirectory: true))
            {
                if (seen.Add(dirCandidate))
                {
                    yield return dirCandidate;
                }
            }
        }
    }

    private static readonly string[] LocalizedCopySuffixes =
    [
        " - Copy",
        " - 副本",
        " - 複製",
        " - コピー",
        " - 복사본",
        " - Kopie",
        " - Copie",
        " — Copie",
        " - Copia",
        " - Cópia",
        " - копия",
        " - نسخة",
        " - प्रतिलिपि",
        " - প্রতিলিপি",
    ];

    internal static IEnumerable<string> GetArrivalNameCandidates(
        string name,
        bool isDirectory = false)
    {
        yield return name;
        if (isDirectory)
        {
            yield return $"{name} (2)";
            yield return $"{name} (3)";
            foreach (string copySuffix in LocalizedCopySuffixes)
            {
                yield return $"{name}{copySuffix}";
            }

            yield break;
        }

        string stem = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);
        if (string.IsNullOrEmpty(stem))
        {
            yield return $"{name} (2)";
            yield return $"{name} (3)";
            foreach (string copySuffix in LocalizedCopySuffixes)
            {
                yield return $"{name}{copySuffix}";
            }

            yield break;
        }

        yield return $"{stem} (2){extension}";
        yield return $"{stem} (3){extension}";
        foreach (string copySuffix in LocalizedCopySuffixes)
        {
            yield return $"{stem}{copySuffix}{extension}";
        }

        if (!string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            yield return $"{name}.lnk";
            yield return $"{stem}.lnk";
            yield return $"{stem} - Shortcut.lnk";
        }
    }
}
