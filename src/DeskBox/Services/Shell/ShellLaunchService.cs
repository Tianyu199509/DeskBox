using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Unified, UI-thread-isolated Shell launch facade for DeskBox.
/// Centralizes STA worker pooling, modal dialog owner-HWND validation,
/// unpackaged <c>IInitializeWithWindow</c> binding with <c>SHOpenWithDialog</c>
/// fallback, child environment scrubbing, working-directory safety, and
/// Explorer reveal across all UI surfaces.
/// </summary>
public static class ShellLaunchService
{
    internal static readonly BoundedStaOperationRunner LaunchRunner =
        new(
            maxConcurrency: 4,
            maxQueued: 8,
            queueTimeout: TimeSpan.FromSeconds(2),
            threadName: "DeskBox File Open");

    internal static readonly BoundedStaOperationRunner ModalShellRunner =
        new(
            maxConcurrency: 2,
            maxQueued: 4,
            queueTimeout: TimeSpan.FromSeconds(2),
            threadName: "DeskBox Modal Shell");

    /// <summary>
    /// Normalizes a candidate owner window handle so destroyed or hidden HWNDs
    /// (for example a SearchPopup that already hid on Enter) never break modal
    /// WinRT/Win32 parenting.
    /// </summary>
    internal static IntPtr NormalizeVisibleOwnerWindow(IntPtr ownerHwnd)
    {
        if (ownerHwnd == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            return Win32Helper.IsWindow(ownerHwnd) && Win32Helper.IsWindowVisible(ownerHwnd)
                ? ownerHwnd
                : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public static async Task<FileService.OpenItemResult> OpenPathAsync(
        string? path,
        IntPtr ownerHwnd = default,
        bool showPickerWhenUnassociated = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return FileService.OpenItemResult.Failed;
        }

        string trimmed = path.Trim();
        bool isProtocolUri = Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsedUri) &&
                             !parsedUri.IsFile;
        bool isShellToken = !isProtocolUri &&
                            !Path.IsPathFullyQualified(trimmed) &&
                            !ShortcutTargetProbe.IsUncPath(trimmed);
        if (isProtocolUri || isShellToken)
        {
            IntPtr effectiveOwner = NormalizeVisibleOwnerWindow(ownerHwnd);
            StaOperationResult<bool> uriOperation = await LaunchRunner.RunAsync(
                () => Win32Helper.OpenFile(effectiveOwner, trimmed),
                cancellationToken).ConfigureAwait(false);
            if (!uriOperation.Started)
            {
                return FileService.OpenItemResult.Busy;
            }

            return uriOperation.Value
                ? FileService.OpenItemResult.OpenedOrHandled
                : FileService.OpenItemResult.Failed;
        }

        bool isShortcut = ShortcutHelper.IsShortcutPath(trimmed);
        var openTarget = new WidgetItem
        {
            Path = trimmed,
            TargetPath = isShortcut ? string.Empty : trimmed,
            IsShortcut = isShortcut
        };

        FileService.OpenItemResult result = await FileService.OpenItemAsync(
            openTarget,
            NormalizeVisibleOwnerWindow(ownerHwnd),
            cancellationToken).ConfigureAwait(false);

        if (result == FileService.OpenItemResult.RequiresOpenWithPicker &&
            showPickerWhenUnassociated)
        {
            (_, bool pickerFailed) = await ShowOpenWithPickerAsync(
                ownerHwnd,
                trimmed,
                cancellationToken).ConfigureAwait(false);
            return pickerFailed
                ? FileService.OpenItemResult.Failed
                : FileService.OpenItemResult.OpenedOrHandled;
        }

        return result;
    }

    public static void FireAndForgetOpenPath(
        string? path,
        IntPtr ownerHwnd = default,
        bool showPickerWhenUnassociated = true)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string capturedPath = path.Trim();
        UiDispatch.PostAfterCallback(() =>
        {
            _ = OpenPathDetachedCoreAsync(
                capturedPath,
                ownerHwnd,
                showPickerWhenUnassociated);
        });
    }

    private static async Task OpenPathDetachedCoreAsync(
        string path,
        IntPtr ownerHwnd,
        bool showPickerWhenUnassociated)
    {
        try
        {
            FileService.OpenItemResult result = await OpenPathAsync(
                path,
                ownerHwnd,
                showPickerWhenUnassociated).ConfigureAwait(false);
            if (result is not (FileService.OpenItemResult.OpenedOrHandled
                or FileService.OpenItemResult.ShortcutDeleted))
            {
                App.Log(
                    $"[ShellLaunch] Detached open completed with result={result} " +
                    $"path='{path}'");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ShellLaunch] Detached open failed for '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// Displays the system application picker for an unassociated file.
    /// Initializes <c>IInitializeWithWindow</c> when a visible owner HWND is
    /// available (required for unpackaged WinUI 3 apps) and falls back to the
    /// documented Win32 <c>SHOpenWithDialog</c> on the modal STA pool whenever
    /// the owner window is hidden/absent or WinRT <c>Launcher</c> declines.
    /// </summary>
    public static async Task<(bool Launched, bool PickerFailed)> ShowOpenWithPickerAsync(
        IntPtr ownerHwnd,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return (false, true);
        }

        IntPtr visibleOwner = NormalizeVisibleOwnerWindow(ownerHwnd);
        if (visibleOwner != IntPtr.Zero)
        {
            try
            {
                Windows.Storage.StorageFile file = await Windows.Storage.StorageFile
                    .GetFileFromPathAsync(filePath);
                var options = new Windows.System.LauncherOptions
                {
                    DisplayApplicationPicker = true
                };
                WinRT.Interop.InitializeWithWindow.Initialize(options, visibleOwner);
                using IDisposable? childScope =
                    ProcessEnvironmentSyncService.AcquireChildLaunchContaminationScope();
                bool launched = await Windows.System.Launcher.LaunchFileAsync(
                    file,
                    options);
                return (launched, false);
            }
            catch (Exception ex)
            {
                App.Log(
                    "[ShellLaunch] WinRT DisplayApplicationPicker failed; " +
                    $"falling back to SHOpenWithDialog for '{filePath}': {ex.Message}");
            }
        }

        bool win32Shown = await ShowChooseAppDialogAsync(
            visibleOwner,
            filePath,
            cancellationToken).ConfigureAwait(false);
        return (win32Shown, !win32Shown);
    }

    public static async Task<bool> ShowChooseAppDialogAsync(
        IntPtr ownerHwnd,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        IntPtr visibleOwner = NormalizeVisibleOwnerWindow(ownerHwnd);
        StaOperationResult<bool> operation = await ModalShellRunner.RunAsync(
            () =>
            {
                using IDisposable? childScope =
                    ProcessEnvironmentSyncService.AcquireChildLaunchContaminationScope();
                _ = Win32Helper.AllowSetForegroundWindow(uint.MaxValue);
                return ShellOpenWithHelper.ShowChooseAppDialog(visibleOwner, filePath);
            },
            cancellationToken).ConfigureAwait(false);

        return operation.Started && operation.Value;
    }

    public static async Task<bool> ShowFilePropertiesAsync(
        IntPtr ownerHwnd,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        IntPtr visibleOwner = NormalizeVisibleOwnerWindow(ownerHwnd);
        StaOperationResult<bool> operation = await ModalShellRunner.RunAsync(
            () =>
            {
                using IDisposable? childScope =
                    ProcessEnvironmentSyncService.AcquireChildLaunchContaminationScope();
                _ = Win32Helper.AllowSetForegroundWindow(uint.MaxValue);
                return ShellContextMenuHelper.ShowProperties(visibleOwner, filePath);
            },
            cancellationToken).ConfigureAwait(false);

        return operation.Started && operation.Value;
    }

    public static async Task<ElevatedFileLaunchResult> RunAsAdministratorAsync(
        IntPtr ownerHwnd,
        WidgetItem item,
        CancellationToken cancellationToken = default)
    {
        if (item is null)
        {
            return new ElevatedFileLaunchResult(ElevatedFileLaunchStatus.Failed);
        }

        string itemPath = item.Path;
        string targetPath = item.TargetPath;
        IntPtr visibleOwner = NormalizeVisibleOwnerWindow(ownerHwnd);
        StaOperationResult<ElevatedFileLaunchResult> operation =
            await ModalShellRunner.RunAsync(
                () => ElevatedFileLauncher.RunAsAdministrator(
                    visibleOwner,
                    itemPath,
                    targetPath),
                cancellationToken).ConfigureAwait(false);

        return operation.Started
            ? operation.Value
            : new ElevatedFileLaunchResult(ElevatedFileLaunchStatus.Failed);
    }

    public static async Task<ElevatedFileLaunchResult> RunAsAdministratorAsync(
        IntPtr ownerHwnd,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new ElevatedFileLaunchResult(ElevatedFileLaunchStatus.Failed);
        }

        string capturedPath = filePath.Trim();
        IntPtr visibleOwner = NormalizeVisibleOwnerWindow(ownerHwnd);
        StaOperationResult<ElevatedFileLaunchResult> operation =
            await ModalShellRunner.RunAsync(
                () => ElevatedFileLauncher.RunAsAdministrator(visibleOwner, capturedPath),
                cancellationToken).ConfigureAwait(false);

        return operation.Started
            ? operation.Value
            : new ElevatedFileLaunchResult(ElevatedFileLaunchStatus.Failed);
    }

    internal static async Task<ShellLaunchOutcome> LaunchShortcutWithFilesAsync(
        string shortcutPath,
        IReadOnlyList<string> filePaths,
        IntPtr ownerHwnd = default,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) || filePaths is null || filePaths.Count == 0)
        {
            return ShellLaunchOutcome.Failed;
        }

        string[] snapshotPaths = filePaths as string[] ?? filePaths.ToArray();
        IntPtr visibleOwner = NormalizeVisibleOwnerWindow(ownerHwnd);
        StaOperationResult<ShellLaunchOutcome> operation = await LaunchRunner.RunAsync(
            () => ShortcutFileLauncher.TryLaunchWithFiles(
                shortcutPath,
                snapshotPaths,
                visibleOwner,
                out _),
            cancellationToken).ConfigureAwait(false);

        return operation.Started ? operation.Value : ShellLaunchOutcome.Failed;
    }

    public static async Task RevealInExplorerAsync(
        string? path,
        bool resolveShortcutTarget = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string capturedPath = path.Trim();
        _ = await LaunchRunner.RunAsync(
            () =>
            {
                string targetToReveal = resolveShortcutTarget
                    ? ResolveShortcutRevealTarget(capturedPath)
                    : capturedPath;
                Win32Helper.ShowInExplorer(targetToReveal);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public static void FireAndForgetRevealInExplorer(
        string? path,
        bool resolveShortcutTarget = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string capturedPath = path.Trim();
        UiDispatch.PostAfterCallback(() =>
        {
            _ = RevealInExplorerDetachedCoreAsync(capturedPath, resolveShortcutTarget);
        });
    }

    private static async Task RevealInExplorerDetachedCoreAsync(
        string path,
        bool resolveShortcutTarget)
    {
        try
        {
            await RevealInExplorerAsync(path, resolveShortcutTarget).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            App.Log($"[ShellLaunch] Reveal in Explorer failed for '{path}': {ex.Message}");
        }
    }

    internal static string ResolveShortcutRevealTarget(string path)
    {
        if (!ShortcutHelper.IsShortcutPath(path))
        {
            return path;
        }

        ShortcutInfo? metadata = ShortcutHelper.ReadStoredMetadata(path);
        string? rawTarget = metadata?.TargetPath;
        if (!string.IsNullOrWhiteSpace(rawTarget))
        {
            string expanded = Environment.ExpandEnvironmentVariables(rawTarget.Trim());
            if (File.Exists(expanded) || Directory.Exists(expanded))
            {
                return expanded;
            }

            if (ShortcutTargetProbe.TryResolveProgramFilesX86Counterpart(
                    rawTarget,
                    expanded,
                    out string x86Counterpart) &&
                (File.Exists(x86Counterpart) || Directory.Exists(x86Counterpart)))
            {
                return x86Counterpart;
            }
        }

        string? effective = ShortcutHelper.TryReadEffectiveTargetPath(path);
        if (!string.IsNullOrWhiteSpace(effective))
        {
            string expandedEffective = Environment.ExpandEnvironmentVariables(effective.Trim());
            if (File.Exists(expandedEffective) || Directory.Exists(expandedEffective))
            {
                return expandedEffective;
            }
        }

        return path;
    }
}
