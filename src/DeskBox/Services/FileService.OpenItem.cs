using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using System.Diagnostics;

namespace DeskBox.Services;

/// <summary>How <see cref="FileService.OpenItemAsync"/> dispatches an item.</summary>
internal enum OpenItemDispatchMode
{
    /// <summary>The explorer-first explicit-"open" pipeline every item used before #459.</summary>
    ShellDispatch,

    /// <summary>
    /// Local launch with the Shell default verb — the desktop double-click
    /// dispatch — used for folder shortcuts so third-party file managers
    /// registered as the Folder default handler take over (#459).
    /// </summary>
    LocalDefaultVerb
}

public sealed partial class FileService
{
    private static readonly BoundedStaOperationRunner s_openItemRunner =
        new(maxConcurrency: 2, maxQueued: 6, queueTimeout: TimeSpan.FromSeconds(2));

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
            if (shellLink && string.IsNullOrWhiteSpace(targetPath))
            {
                trace?.Mark("shortcut-metadata-start");
                using (PerformanceLogger.Measure(
                           "FileService.OpenItem.ShortcutMetadata",
                           $"kind={kind}"))
                {
                    targetPath = ShortcutHelper.ReadStoredMetadata(itemPath)?.TargetPath ??
                        string.Empty;
                    resolvedTargetPath = targetPath;
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
                // Windows resolves links by tracking the file, so a shortcut
                // whose stored path moved can still open from Explorer. Hand the
                // link to the shell, and report this separately from a real
                // dispatch: nothing was launched on this path.
                App.Log(
                    $"[OpenItem] branch=shortcut-target-missing path='{itemPath}' " +
                    $"target='{targetPath}'");
                using (PerformanceLogger.Measure(
                           "FileService.OpenItem.BrokenShortcutUi",
                           $"kind={kind}"))
                {
                    BrokenShortcutResolution resolution =
                        ShortcutHelper.ResolveBrokenShortcutWithShellUi(
                            itemPath,
                            ownerHwnd);
                    result = resolution == BrokenShortcutResolution.ShortcutDeleted
                        ? OpenItemResult.ShortcutDeleted
                        : OpenItemResult.ShortcutTargetMissing;
                }
                trace?.Mark("broken-shortcut-result", $"result={result}");

                return result;
            }

            string pathToOpen = isShortcut ? itemPath : targetPath;
            if (string.IsNullOrEmpty(pathToOpen))
            {
                return result;
            }

            if (!isShortcut)
            {
                using (PerformanceLogger.Measure(
                           "FileService.OpenItem.PathTraversal",
                           $"kind={kind}"))
                {
                    if (TryResolveExistingPathForTraversal(
                            pathToOpen,
                            out string traversalPath))
                    {
                        pathToOpen = traversalPath;
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
                // Both Explorer's dispatch and a local ShellExecuteEx report a
                // dismissed picker as a silent success. Defer to the UI layer,
                // whose application picker reports its own outcome.
                trace?.Mark("no-association", "deferred-to-open-with-picker");
                return OpenItemResult.RequiresOpenWithPicker;
            }

            // The probe only ran for real .lnk paths, so a shortcut flag
            // without a .lnk keeps the legacy pipeline untouched.
            OpenItemDispatchMode dispatchMode = SelectOpenDispatchMode(
                isShortcut && shellLink,
                shortcutProbe.Kind,
                shortcutProbe.TargetIsDirectory);
            if (dispatchMode == OpenItemDispatchMode.LocalDefaultVerb)
            {
                // Desktop double-click semantics: a NULL verb resolves the
                // Folder default handler (possibly a third-party file
                // manager). Explorer-hosted environment inheritance is
                // pointless for a file manager window, so launch locally.
                using (PerformanceLogger.Measure(
                           "FileService.OpenItem.FolderShortcutDefaultVerb",
                           $"kind={kind}"))
                {
                    result = Win32Helper.OpenWithDefaultVerbLocally(
                            pathToOpen,
                            Win32Helper.ResolveShellLaunchDirectory(pathToOpen))
                        ? OpenItemResult.OpenedOrHandled
                        : OpenItemResult.Failed;
                }

                trace?.Mark("folder-shortcut-default-verb", $"result={result}");
            }
            else
            {
                using (PerformanceLogger.Measure(
                           "FileService.OpenItem.ShellDispatch",
                           $"kind={kind}"))
                {
                    result = Win32Helper.OpenFile(ownerHwnd, pathToOpen)
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
    /// Chooses how an item is dispatched. A shortcut whose target is a local
    /// directory must use the Shell's default verb — the desktop double-click
    /// dispatch — because third-party file managers register as the Folder
    /// default handler and an explicit "open" verb bypasses their takeover
    /// (#459). Every other kind, including plain folders, keeps the
    /// explorer-first pipeline.
    /// </summary>
    internal static OpenItemDispatchMode SelectOpenDispatchMode(
        bool isShortcut,
        ShortcutTargetKind targetKind,
        bool targetIsDirectory)
    {
        return isShortcut &&
               targetKind == ShortcutTargetKind.LocalFileSystem &&
               targetIsDirectory
            ? OpenItemDispatchMode.LocalDefaultVerb
            : OpenItemDispatchMode.ShellDispatch;
    }

}
