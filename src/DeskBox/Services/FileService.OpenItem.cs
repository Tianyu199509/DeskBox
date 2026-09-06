using DeskBox.Helpers;
using DeskBox.Models;
using System.Diagnostics;

namespace DeskBox.Services;

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

        Stopwatch queueStopwatch = Stopwatch.StartNew();
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
            $"started={operation.Started} queueWaitMs={queueStopwatch.Elapsed.TotalMilliseconds:F1}");
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
                        : OpenItemResult.OpenedOrHandled;
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

}
