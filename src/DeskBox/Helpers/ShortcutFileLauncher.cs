using System.Diagnostics;
using System.Text;

namespace DeskBox.Helpers;

/// <summary>
/// Opens dropped files with the application a shortcut points at — the
/// behavior Windows itself provides when files are dropped on a shortcut in an
/// Explorer window.
///
/// Runs <c>ShellExecuteEx</c> on the .lnk with the dropped paths appended as
/// arguments, so Windows resolves the link: stored arguments are prepended, the
/// link's working directory and elevation flag apply, link tracking still
/// resolves a moved target, and packaged-app activation behaves natively. The
/// dropped paths are the only thing we format ourselves.
///
/// This is not a shell command line and nothing here goes through cmd.exe: the
/// paths travel in <c>ShellExecuteEx</c>'s <c>lpParameters</c> channel to the
/// application the shortcut points at. Because every path is wrapped in double
/// quotes and a double quote is a reserved character in Windows file and folder
/// names, a path cannot end its argument early — the quoting is the complete
/// escaping story, and <see cref="SanitizeDroppedPaths"/> enforces that
/// invariant instead of assuming it.
///
/// Not using <see cref="ProcessStartInfo.ArgumentList"/>: it is silently ignored
/// when <c>UseShellExecute</c> is true (measured 2026-09-12 — the process starts
/// and the arguments are dropped), and it cannot be combined with the direct
/// CreateProcess route because a .lnk is not an executable image.
///
/// Deliberately not using the shortcut's own IDropTarget: on real hardware its
/// <c>DragEnter</c> accepts but <c>Drop</c> answers DROPEFFECT_NONE and Windows
/// then shows its "Open with" picker (measured 2026-09-12, see
/// docs/architecture/drop_on_shortcut_open.md §10.2), which is neither the
/// requested open nor a quiet refusal.
/// </summary>
internal static class ShortcutFileLauncher
{
    internal static bool CanLaunchWithFiles(
        string? shortcutPath,
        IReadOnlyList<string>? filePaths) =>
        !string.IsNullOrWhiteSpace(shortcutPath) &&
        filePaths is { Count: > 0 } &&
        SanitizeDroppedPaths(filePaths).Length > 0;

    internal static Task<DeskBox.Platform.ShellLaunchOutcome> LaunchWithFilesAsync(
        string shortcutPath,
        IReadOnlyList<string> filePaths,
        IntPtr ownerHwnd = default,
        CancellationToken cancellationToken = default) =>
        DeskBox.Services.ShellLaunchService.LaunchShortcutWithFilesAsync(
            shortcutPath,
            filePaths,
            ownerHwnd,
            cancellationToken);

    /// <summary>
    /// Returns false when Windows refused the launch (missing target, no
    /// association, elevation declined) or when no usable path was left.
    /// Callers must then report the failure instead of importing: the gesture
    /// means "open with that application".
    /// </summary>
    internal static bool TryLaunchWithFiles(
        string shortcutPath,
        IReadOnlyList<string> filePaths,
        IntPtr ownerHwnd = default) =>
        TryLaunchWithFiles(shortcutPath, filePaths, ownerHwnd, out _) ==
        DeskBox.Platform.ShellLaunchOutcome.Launched;

    internal static DeskBox.Platform.ShellLaunchOutcome TryLaunchWithFiles(
        string shortcutPath,
        IReadOnlyList<string> filePaths,
        IntPtr ownerHwnd,
        out DeskBox.Platform.ShellLaunchOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) || filePaths.Count == 0)
        {
            outcome = DeskBox.Platform.ShellLaunchOutcome.Failed;
            return outcome;
        }

        string[] paths = SanitizeDroppedPaths(filePaths);
        if (paths.Length == 0)
        {
            outcome = DeskBox.Platform.ShellLaunchOutcome.Failed;
            return outcome;
        }

        if (paths.Length != filePaths.Count)
        {
            App.Log(
                "[ShortcutLaunch] Skipped " +
                $"{filePaths.Count - paths.Length} dropped path(s) that cannot " +
                "exist on Windows.");
        }

        string arguments = BuildArgumentString(paths);
        App.LogVerbose(
            $"[ShortcutLaunch] lpFile='{shortcutPath}' lpParameters={arguments}");

        try
        {
            ShortcutInfo? storedMetadata = ShortcutHelper.ReadStoredMetadata(shortcutPath);
            string fallbackWorkingDirectory = DeskBox.Services.FileService.ResolveShortcutLaunchDirectory(
                storedMetadata,
                storedMetadata?.TargetPath,
                shortcutPath);

            // When the .lnk stores a non-empty WorkingDirectory whose local folder
            // was deleted, CShellLink ignores outer lpDirectory and fails with
            // ERROR_DIRECTORY (267). Bypass CShellLink by launching the resolved
            // target executable directly with combined arguments and preserved
            // SLDF_RUNAS_USER elevation semantics.
            if (ShortcutHelper.IsShellLinkPath(shortcutPath) &&
                DeskBox.Services.FileService.HasBrokenShortcutWorkingDirectory(storedMetadata) &&
                DeskBox.Services.FileService.TryResolveDirectLocalShortcutTarget(
                    shortcutPath,
                    storedMetadata?.TargetPath,
                    storedMetadata,
                    out string directTarget,
                    out string? combinedArgs,
                    out string? shortcutVerb,
                    extraAppendedArguments: arguments) &&
                File.Exists(directTarget))
            {
                DeskBox.Platform.Win32Helper.OpenWithParametersLocally(
                    ownerHwnd,
                    directTarget,
                    combinedArgs,
                    fallbackWorkingDirectory,
                    shortcutVerb,
                    out DeskBox.Platform.ShellLaunchOutcome directOutcome);
                if (directOutcome == DeskBox.Platform.ShellLaunchOutcome.Launched)
                {
                    App.Log(
                        $"[ShortcutLaunch] Launched '{shortcutPath}' via repaired working-directory target '{directTarget}' " +
                        $"with {paths.Length} dropped file(s).");
                    outcome = DeskBox.Platform.ShellLaunchOutcome.Launched;
                    return outcome;
                }

                if (directOutcome == DeskBox.Platform.ShellLaunchOutcome.UserCancelled)
                {
                    outcome = DeskBox.Platform.ShellLaunchOutcome.UserCancelled;
                    return outcome;
                }
            }

            // No Verb either (feedback 246): a NULL verb is the desktop
            // double-click dispatch, and link resolution forwards it to the
            // target's default launch.
            DeskBox.Platform.Win32Helper.OpenWithParametersLocally(
                ownerHwnd,
                shortcutPath,
                arguments,
                fallbackWorkingDirectory,
                verb: null,
                out outcome);
            if (outcome == DeskBox.Platform.ShellLaunchOutcome.Launched)
            {
                App.Log(
                    $"[ShortcutLaunch] Launched '{shortcutPath}' with " +
                    $"{paths.Length} dropped file(s).");
            }

            return outcome;
        }
        catch (Exception ex)
        {
            App.Log(
                $"[ShortcutLaunch] Launch failed for '{shortcutPath}': " +
                $"{ex.Message}");
            outcome = DeskBox.Platform.ShellLaunchOutcome.Failed;
            return outcome;
        }
    }

    /// <summary>
    /// Drops entries that cannot be a real Windows path, so the quoting below is
    /// provably unbreakable: a double quote is a reserved character in Windows
    /// file and folder names, so a path carrying one was forged (or came from a
    /// virtual drop) and is never handed to the command line.
    /// </summary>
    internal static string[] SanitizeDroppedPaths(IReadOnlyList<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        var sanitized = new List<string>(filePaths.Count);
        foreach (string path in filePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string trimmed = path.Trim();
            if (trimmed.Contains('"'))
            {
                continue;
            }

            if (ShortcutHelper.IsInternetShortcutPath(trimmed) &&
                File.Exists(trimmed) &&
                ShortcutHelper.ReadStoredMetadata(trimmed)?.TargetPath is { Length: > 0 } targetUrl &&
                !targetUrl.Contains('"') &&
                Uri.TryCreate(targetUrl, UriKind.Absolute, out Uri? parsedUri) &&
                (string.Equals(parsedUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(parsedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                sanitized.Add(targetUrl);
                continue;
            }

            sanitized.Add(trimmed);
        }

        return [.. sanitized];
    }

    /// <summary>
    /// One quoted argument per dropped path. Always quoted: a quoted path is
    /// safe whether or not it contains spaces, and
    /// <see cref="SanitizeDroppedPaths"/> has already removed every value that
    /// could contain a double quote, so no path can terminate its own argument.
    /// </summary>
    internal static string BuildArgumentString(IReadOnlyList<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        var builder = new StringBuilder();
        foreach (string path in filePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string rawTrimmed = path.Trim();
            string trimmed = rawTrimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                             rawTrimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? rawTrimmed
                : Path.TrimEndingDirectorySeparator(rawTrimmed);
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            // In Win32 CommandLineToArgvW, a trailing backslash immediately
            // before the closing quote (e.g. "D:\") escapes the quote itself.
            // Doubling a drive-root trailing backslash ("D:\\") passes D:\ cleanly.
            builder.Append('"').Append(trimmed);
            if (trimmed.EndsWith('\\'))
            {
                builder.Append('\\');
            }

            builder.Append('"');
        }

        return builder.ToString();
    }
}
