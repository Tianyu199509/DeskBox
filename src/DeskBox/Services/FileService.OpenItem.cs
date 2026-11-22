using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using System.Diagnostics;

namespace DeskBox.Services;

/// <summary>How <see cref="FileService.OpenItemAsync"/> dispatches an item.</summary>
internal enum OpenItemDispatchMode
{
    /// <summary>The explorer-first explicit-"open" pipeline plain items use.</summary>
    ShellDispatch,

    /// <summary>
    /// Local launch with the Shell default verb — the desktop double-click
    /// dispatch — for items where an explicit "open" verb is wrong or
    /// fragile: template documents, whose default verb creates a copy
    /// (feedback 366 / confirmed 81), and shortcuts to local-filesystem
    /// targets, where the "open" verb both bypasses third-party Folder
    /// handlers (#459) and dies silently inside the explorer-hosted
    /// fire-and-forget dispatch when the OS-side "open" resolution is
    /// damaged (feedback 246).
    /// </summary>
    LocalDefaultVerb
}

public sealed partial class FileService
{
    private static readonly BoundedStaOperationRunner s_openItemRunner =
        ShellLaunchService.LaunchRunner;

    /// <summary>
    /// Opens an item without running filesystem or Shell work on the caller's
    /// UI thread. The item is snapshotted before dispatch so the worker never
    /// raises WidgetItem property notifications from a background thread.
    /// </summary>
    public static async Task<OpenItemResult> OpenItemAsync(
        WidgetItem item,
        IntPtr ownerHwnd,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        string itemPath = item.Path;
        string targetPath = item.TargetPath;
        bool isShortcut = item.IsShortcut;
        string kind = FileOpenTrace.GetPathKind(itemPath, isShortcut);
        FileOpenTrace? trace = FileOpenTrace.Start(itemPath, isShortcut);
        using IDisposable timing = PerformanceLogger.Measure(
            "FileService.OpenItemAsync",
            $"kind={kind}");

        Stopwatch runStopwatch = Stopwatch.StartNew();
        StaOperationResult<OpenItemResult> operation = await s_openItemRunner.RunAsync(
            () => OpenItemCore(
                itemPath,
                targetPath,
                isShortcut,
                ownerHwnd,
                trace,
                out _),
            cancellationToken).ConfigureAwait(false);
        trace?.Mark(
            "admission",
            $"started={operation.Started} queueWaitMs={operation.QueueWait.TotalMilliseconds:F1} " +
            $"runTotalMs={runStopwatch.Elapsed.TotalMilliseconds:F1}");
        if (!operation.Started)
        {
            trace?.Mark("result", "result=Busy");
            return OpenItemResult.Busy;
        }

        return operation.Value;
    }

    private static OpenItemResult OpenItemCore(
        string itemPath,
        string targetPath,
        bool isShortcut,
        IntPtr ownerHwnd,
        FileOpenTrace? trace,
        out string resolvedTargetPath)
    {
        resolvedTargetPath = targetPath;
        string kind = FileOpenTrace.GetPathKind(itemPath, isShortcut);
        using IDisposable timing = PerformanceLogger.Measure(
            "FileService.OpenItemCore",
            $"kind={kind}");

        OpenItemResult result = OpenItemResult.Failed;
        try
        {
            bool shellLink = ShortcutHelper.IsShellLinkPath(itemPath);
            ShortcutInfo? shortcutMetadata = null;
            if (shellLink)
            {
                trace?.Mark("shortcut-metadata-start");
                using (PerformanceLogger.Measure(
                           "FileService.OpenItem.ShortcutMetadata",
                           $"kind={kind}"))
                {
                    shortcutMetadata = ShortcutHelper.ReadStoredMetadata(itemPath);
                    if (string.IsNullOrWhiteSpace(targetPath))
                    {
                        targetPath = shortcutMetadata?.TargetPath ?? string.Empty;
                        resolvedTargetPath = targetPath;
                    }
                }
                trace?.Mark("shortcut-metadata-end");
            }

            ShortcutTargetProbeResult shortcutProbe = shellLink
                ? ShortcutTargetProbe.Probe(itemPath, targetPath)
                : default;
            if (shellLink)
            {
                trace?.Mark(
                    "shortcut-target",
                    $"kind={shortcutProbe.Kind} status={shortcutProbe.Status}");
            }

            if (shellLink && shortcutProbe.IsBroken)
            {
                // Windows resolves links by tracking the file (NTFS Distributed
                // Link Tracking) or letting the user browse to the moved target.
                // Hand the link to the Shell with SLR_UPDATE; if the link is
                // repaired on disk, continue directly into normal launch instead
                // of requiring a second double-click.
                App.Log(
                    $"[OpenItem] branch=shortcut-target-missing path='{itemPath}' " +
                    $"target='{targetPath}'");
                BrokenShortcutResolution resolution;
                using (PerformanceLogger.Measure(
                           "FileService.OpenItem.BrokenShortcutUi",
                           $"kind={kind}"))
                {
                    resolution = ShortcutHelper.ResolveBrokenShortcutWithShellUi(
                        itemPath,
                        ownerHwnd);
                }

                if (resolution == BrokenShortcutResolution.ShortcutDeleted)
                {
                    result = OpenItemResult.ShortcutDeleted;
                    trace?.Mark("broken-shortcut-result", $"result={result}");
                    return result;
                }

                shortcutMetadata = ShortcutHelper.ReadStoredMetadata(itemPath);
                string repairedTarget = shortcutMetadata?.TargetPath ??
                                        ShortcutHelper.TryReadEffectiveTargetPath(itemPath) ??
                                        string.Empty;
                ShortcutTargetProbeResult repairedProbe = ShortcutTargetProbe.Probe(
                    itemPath,
                    repairedTarget);
                if (repairedProbe.IsBroken)
                {
                    result = OpenItemResult.ShortcutTargetMissing;
                    trace?.Mark("broken-shortcut-result", $"result={result}");
                    return result;
                }

                targetPath = repairedTarget;
                resolvedTargetPath = repairedTarget;
                shortcutProbe = repairedProbe;
                App.Log(
                    $"[OpenItem] Shortcut target repaired via Shell resolution " +
                    $"path='{itemPath}' target='{targetPath}'");
                trace?.Mark("broken-shortcut-repaired", $"kind={shortcutProbe.Kind}");
            }

            string pathToOpen = isShortcut
                ? itemPath
                : (!string.IsNullOrWhiteSpace(targetPath) ? targetPath : itemPath);
            if (string.IsNullOrEmpty(pathToOpen))
            {
                return result;
            }

            string? traversalFallbackPath = null;
            if (!isShortcut)
            {
                using (PerformanceLogger.Measure(
                           "FileService.OpenItem.PathTraversal",
                           $"kind={kind}"))
                {
                    // Preserve the logical symlink/junction path as primary so
                    // argv[0] and working-directory-sensitive runtimes (Python
                    // .venv\Scripts\python.exe, Scoop current\app.exe, Node .bin)
                    // retain their virtual environment context; keep the physical
                    // hop-resolved path as fallback for RedirectionGuard mount
                    // points or extensionless symlinks.
                    if (TryResolveExistingPathForTraversal(
                            pathToOpen,
                            out string traversalPath) &&
                        !AreEquivalentNormalizedPaths(pathToOpen, traversalPath))
                    {
                        traversalFallbackPath = traversalPath;
                    }
                }
                trace?.Mark("path-traversal-end");
            }

            // Forward the real owner hwnd so any system UI (Open With / UAC)
            // remains associated with the widget. The call is deliberately
            // isolated on the STA worker because it can synchronously wait for
            // Explorer, a provider, or a modal Shell dialog.
            if (!Win32Helper.HasShellOpenAssociation(pathToOpen))
            {
                if (traversalFallbackPath is not null &&
                    Win32Helper.HasShellOpenAssociation(traversalFallbackPath))
                {
                    pathToOpen = traversalFallbackPath;
                    traversalFallbackPath = null;
                }
                else if (TryExtractFallbackInternetShortcutUri(itemPath, out string protocolUri) &&
                         Win32Helper.HasShellOpenAssociation(protocolUri))
                {
                    App.Log(
                        $"[OpenItem] .url association missing; dispatching stored protocol URI " +
                        $"for '{itemPath}'");
                    pathToOpen = protocolUri;
                    traversalFallbackPath = null;
                }
                else
                {
                    // Both Explorer's dispatch and a local ShellExecuteEx report a
                    // dismissed picker as a silent success. Defer to the UI layer,
                    // whose application picker reports its own outcome.
                    trace?.Mark("no-association", "deferred-to-open-with-picker");
                    return OpenItemResult.RequiresOpenWithPicker;
                }
            }

            bool isElevated = DragDropPermissionService.IsCurrentProcessElevatedCached();
            bool isPackaged = AppDistributionService.Current.IsPackaged;

            // The probe only ran for real .lnk paths, so a shortcut flag
            // without a .lnk keeps the legacy pipeline untouched.
            OpenItemDispatchMode dispatchMode = SelectOpenDispatchMode(
                isShortcut && shellLink,
                shortcutProbe.Kind,
                pathToOpen,
                isElevated,
                isPackaged);
            if (dispatchMode == OpenItemDispatchMode.LocalDefaultVerb)
            {
                // Desktop double-click semantics: a NULL verb resolves the
                // item's default handler — the Folder default handler for a
                // folder shortcut (#459), the template "New" verb that
                // creates a copy (feedback 366), the target's default launch
                // for an app shortcut (feedback 246), and direct local
                // executables when DeskBox runs unelevated and unpackaged.
                string launchDirectory = shellLink
                    ? ResolveShortcutLaunchDirectory(shortcutMetadata, targetPath, itemPath)
                    : Win32Helper.ResolveShellLaunchDirectory(pathToOpen);
                using (PerformanceLogger.Measure(
                           "FileService.OpenItem.LocalDefaultVerb",
                           $"kind={kind}"))
                {
                    ShellLaunchOutcome outcome = ShellLaunchOutcome.Failed;
                    // When a local .lnk stores a non-empty WorkingDirectory whose
                    // directory was deleted, Windows CShellLink ignores outer
                    // lpDirectory and fails CreateProcessW with ERROR_DIRECTORY
                    // (and pops a modal error dialog when SEE_MASK_FLAG_NO_UI is
                    // absent). Launch the resolved target directly with its stored
                    // arguments, repaired directory, and SLDF_RUNAS_USER verb first.
                    if (shellLink &&
                        HasBrokenShortcutWorkingDirectory(shortcutMetadata) &&
                        TryResolveDirectLocalShortcutTarget(
                            itemPath,
                            targetPath,
                            shortcutMetadata,
                            out string directTarget,
                            out string? storedArguments,
                            out string? shortcutVerb))
                    {
                        Win32Helper.OpenWithParametersLocally(
                            ownerHwnd,
                            directTarget,
                            storedArguments,
                            launchDirectory,
                            shortcutVerb,
                            out outcome);
                    }

                    if (outcome == ShellLaunchOutcome.Failed)
                    {
                        Win32Helper.OpenWithDefaultVerbLocally(
                            ownerHwnd,
                            pathToOpen,
                            launchDirectory,
                            out outcome);
                    }

                    if (outcome == ShellLaunchOutcome.Failed &&
                        shellLink &&
                        TryResolveDirectLocalShortcutTarget(
                            itemPath,
                            targetPath,
                            shortcutMetadata,
                            out string fallbackDirectTarget,
                            out string? fallbackArguments,
                            out string? fallbackVerb))
                    {
                        Win32Helper.OpenWithParametersLocally(
                            ownerHwnd,
                            fallbackDirectTarget,
                            fallbackArguments,
                            launchDirectory,
                            fallbackVerb,
                            out outcome);
                    }

                    if (outcome == ShellLaunchOutcome.Failed && traversalFallbackPath is not null)
                    {
                        Win32Helper.OpenWithDefaultVerbLocally(
                            ownerHwnd,
                            traversalFallbackPath,
                            Win32Helper.ResolveShellLaunchDirectory(traversalFallbackPath),
                            out outcome);
                    }

                    result = outcome != ShellLaunchOutcome.Failed
                        ? OpenItemResult.OpenedOrHandled
                        : OpenItemResult.Failed;
                }

                trace?.Mark("local-default-verb", $"result={result}");
            }
            else
            {
                using (PerformanceLogger.Measure(
                           "FileService.OpenItem.ShellDispatch",
                           $"kind={kind}"))
                {
                    bool opened = Win32Helper.OpenFile(ownerHwnd, pathToOpen);
                    if (!opened && traversalFallbackPath is not null)
                    {
                        opened = Win32Helper.OpenFile(ownerHwnd, traversalFallbackPath);
                    }

                    result = opened
                        ? OpenItemResult.OpenedOrHandled
                        : OpenItemResult.Failed;
                }

                trace?.Mark("shell-dispatch-end", $"result={result}");
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[OpenItem] Unexpected failure path='{itemPath}' " +
                $"target='{targetPath}' type={ex.GetType().Name}: {ex.Message}");
            result = OpenItemResult.Failed;
        }
        finally
        {
            PerformanceLogger.Mark(
                "FileService.OpenItem.Result",
                $"kind={kind} result={result}");
            trace?.Mark("result", $"result={result}");
        }

        return result;
    }

    /// <summary>
    /// Chooses how an item is dispatched. Two classes must use the Shell's
    /// default verb — the desktop double-click dispatch, launched locally:
    ///
    /// - Template documents (feedback 366 / confirmed 81): their Shell class's
    ///   default verb is "New", which creates a copy of the template. An
    ///   explicit "open" verb opens the template as a regular document, and
    ///   saving then overwrites the template file.
    /// - Shortcuts whose target is on the local filesystem (#459 folders,
    ///   feedback 246 apps): an explicit "open" verb bypasses third-party
    ///   Folder default handlers, and the explorer-hosted "open" dispatch is
    ///   fire-and-forget — when the OS-side "open" resolution is damaged the
    ///   launch dies silently inside Explorer while reporting success.
    ///
    /// Everything else — plain files and folders, and shortcuts to UNC,
    /// network or shell-namespace targets — keeps the explorer-first
    /// pipeline: Explorer owns credentials and shell-item resolution for
    /// those.
    /// </summary>
    internal static OpenItemDispatchMode SelectOpenDispatchMode(
        bool isShellLinkShortcut,
        ShortcutTargetKind targetKind,
        string pathToOpen,
        bool isProcessElevated = false,
        bool isPackagedProcess = false)
    {
        if (TemplateDocumentVerbPolicy.IsTemplateDocument(pathToOpen))
        {
            return OpenItemDispatchMode.LocalDefaultVerb;
        }

        if (isShellLinkShortcut &&
            targetKind == ShortcutTargetKind.LocalFileSystem)
        {
            return OpenItemDispatchMode.LocalDefaultVerb;
        }

        if (!isShellLinkShortcut &&
            !isProcessElevated &&
            !isPackagedProcess &&
            ShortcutTargetProbe.Classify(pathToOpen) == ShortcutTargetKind.LocalFileSystem &&
            IsDirectLocalExecutablePath(pathToOpen))
        {
            return OpenItemDispatchMode.LocalDefaultVerb;
        }

        return OpenItemDispatchMode.ShellDispatch;
    }

    internal static bool IsDirectLocalExecutablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        ReadOnlySpan<char> extension = Path.GetExtension(path.AsSpan());
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".msi", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Computes the outer working directory passed to local <c>.lnk</c> launches.
    /// Returns <see cref="string.Empty"/> when the shortcut already defines a
    /// valid start-in folder so Windows <c>CShellLink</c> uses it directly;
    /// otherwise supplies the target's parent directory (or shortcut directory)
    /// so portable apps never inherit DeskBox's read-only install directory.
    /// </summary>
    internal static string ResolveShortcutLaunchDirectory(
        ShortcutInfo? metadata,
        string? targetPath,
        string shortcutPath)
    {
        if (!ShortcutHelper.IsShellLinkPath(shortcutPath))
        {
            return string.Empty;
        }

        if (metadata?.WorkingDirectory is { Length: > 0 } rawWorkDir &&
            !string.IsNullOrWhiteSpace(rawWorkDir))
        {
            try
            {
                string expandedWorkDir = Environment.ExpandEnvironmentVariables(rawWorkDir.Trim());
                ShortcutTargetKind workDirKind = ShortcutTargetProbe.Classify(expandedWorkDir);
                if (workDirKind != ShortcutTargetKind.LocalFileSystem ||
                    Directory.Exists(expandedWorkDir))
                {
                    return string.Empty;
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        string? candidateTarget = !string.IsNullOrWhiteSpace(targetPath)
            ? targetPath
            : metadata?.TargetPath;
        if (!string.IsNullOrWhiteSpace(candidateTarget))
        {
            try
            {
                string expandedTarget = Environment.ExpandEnvironmentVariables(candidateTarget.Trim());
                if (ShortcutTargetProbe.Classify(expandedTarget) == ShortcutTargetKind.LocalFileSystem)
                {
                    string effectiveTarget = expandedTarget;
                    if (!File.Exists(effectiveTarget) &&
                        !Directory.Exists(effectiveTarget) &&
                        ShortcutTargetProbe.TryResolveProgramFilesX86Counterpart(
                            candidateTarget,
                            expandedTarget,
                            out string x86Counterpart) &&
                        (File.Exists(x86Counterpart) || Directory.Exists(x86Counterpart)))
                    {
                        effectiveTarget = x86Counterpart;
                    }

                    if (Directory.Exists(effectiveTarget))
                    {
                        return effectiveTarget;
                    }

                    if (File.Exists(effectiveTarget) &&
                        Path.GetDirectoryName(effectiveTarget) is { Length: > 0 } targetParent &&
                        Directory.Exists(targetParent))
                    {
                        return targetParent;
                    }
                }
            }
            catch
            {
            }
        }

        try
        {
            if (Path.GetDirectoryName(shortcutPath) is { Length: > 0 } shortcutDirectory &&
                Directory.Exists(shortcutDirectory))
            {
                return shortcutDirectory;
            }
        }
        catch
        {
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    internal static bool HasBrokenShortcutWorkingDirectory(ShortcutInfo? metadata)
    {
        if (metadata?.WorkingDirectory is not { Length: > 0 } rawWorkDir ||
            string.IsNullOrWhiteSpace(rawWorkDir))
        {
            return false;
        }

        try
        {
            string expandedWorkDir = Environment.ExpandEnvironmentVariables(rawWorkDir.Trim());
            return ShortcutTargetProbe.Classify(expandedWorkDir) == ShortcutTargetKind.LocalFileSystem &&
                   !Directory.Exists(expandedWorkDir);
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryResolveDirectLocalShortcutTarget(
        string shortcutPath,
        string? targetPathHint,
        ShortcutInfo? metadata,
        out string resolvedTarget,
        out string? storedArguments,
        out string? verb,
        string? extraAppendedArguments = null)
    {
        resolvedTarget = string.Empty;
        string rawArgs = metadata?.Arguments?.Trim() ?? string.Empty;
        string combinedArgs = string.IsNullOrWhiteSpace(extraAppendedArguments)
            ? rawArgs
            : string.IsNullOrWhiteSpace(rawArgs)
                ? extraAppendedArguments.Trim()
                : $"{rawArgs} {extraAppendedArguments.Trim()}";
        storedArguments = combinedArgs.Length > 0 ? combinedArgs : null;
        verb = null;

        string? rawCandidate = !string.IsNullOrWhiteSpace(metadata?.TargetPath)
            ? metadata.TargetPath
            : targetPathHint;
        if (!TryMatchExistingLocalTarget(rawCandidate, out string matchedTarget))
        {
            string? effectiveTarget = ShortcutHelper.TryReadEffectiveTargetPath(shortcutPath);
            if (!TryMatchExistingLocalTarget(effectiveTarget, out matchedTarget))
            {
                return false;
            }
        }

        // Folders never expose a "runas" verb; preserve direct folder path without verb/arguments.
        if (Directory.Exists(matchedTarget))
        {
            resolvedTarget = matchedTarget;
            storedArguments = null;
            verb = null;
            return true;
        }

        bool runAsRequested = ShortcutHelper.HasRunAsUserFlag(shortcutPath);
        if (!runAsRequested)
        {
            resolvedTarget = matchedTarget;
            return true;
        }

        string extension = Path.GetExtension(matchedTarget);
        if (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            ElevatedFileLaunchRequest batchRequest = ElevatedFileLauncher.CreateLaunchRequest(
                matchedTarget,
                combinedArgs,
                string.Empty);
            resolvedTarget = batchRequest.FileName;
            storedArguments = batchRequest.Arguments;
            verb = "runas";
            return true;
        }

        if (extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
        {
            ElevatedFileLaunchRequest msiRequest = ElevatedFileLauncher.CreateLaunchRequest(
                matchedTarget,
                combinedArgs,
                string.Empty);
            resolvedTarget = msiRequest.FileName;
            storedArguments = msiRequest.Arguments;
            verb = "runas";
            return true;
        }

        if (extension.Equals(".msp", StringComparison.OrdinalIgnoreCase))
        {
            string installer = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
            string patchArgs = "/p " + ElevatedFileLauncher.QuoteCommandLineArgument(matchedTarget);
            if (combinedArgs.Length > 0)
            {
                patchArgs += " " + combinedArgs;
            }

            resolvedTarget = installer;
            storedArguments = patchArgs;
            verb = "runas";
            return true;
        }

        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".com", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".scr", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".msc", StringComparison.OrdinalIgnoreCase))
        {
            resolvedTarget = matchedTarget;
            verb = "runas";
            return true;
        }

        // Plain document targets have no "runas" verb in HKCR; launch without "runas".
        resolvedTarget = matchedTarget;
        verb = null;
        return true;

        static bool TryMatchExistingLocalTarget(string? rawTarget, out string existingPath)
        {
            existingPath = string.Empty;
            if (string.IsNullOrWhiteSpace(rawTarget))
            {
                return false;
            }

            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(rawTarget.Trim());
                if (ShortcutTargetProbe.Classify(expanded) != ShortcutTargetKind.LocalFileSystem)
                {
                    return false;
                }

                if (File.Exists(expanded) || Directory.Exists(expanded))
                {
                    existingPath = Path.GetFullPath(expanded);
                    return true;
                }

                if (ShortcutTargetProbe.TryResolveProgramFilesX86Counterpart(
                        rawTarget,
                        expanded,
                        out string x86Counterpart) &&
                    (File.Exists(x86Counterpart) || Directory.Exists(x86Counterpart)))
                {
                    existingPath = Path.GetFullPath(x86Counterpart);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }
    }

    internal static bool TryExtractFallbackInternetShortcutUri(
        string path,
        out string protocolUri)
    {
        protocolUri = string.Empty;
        if (!ShortcutHelper.IsInternetShortcutPath(path))
        {
            return false;
        }

        string? targetUrl = ShortcutHelper.ReadStoredMetadata(path)?.TargetPath;
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            return false;
        }

        string trimmed = targetUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsedUri) ||
            parsedUri.IsFile)
        {
            return false;
        }

        protocolUri = trimmed;
        return true;
    }

    private static bool AreEquivalentNormalizedPaths(string left, string right)
    {
        try
        {
            string normalizedLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
            string normalizedRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
            return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

}
