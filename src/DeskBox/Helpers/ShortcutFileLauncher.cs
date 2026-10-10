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
    /// <summary>
    /// Returns false when Windows refused the launch (missing target, no
    /// association, elevation declined) or when no usable path was left.
    /// Callers must then report the failure instead of importing: the gesture
    /// means "open with that application".
    /// </summary>
    internal static bool TryLaunchWithFiles(
        string shortcutPath,
        IReadOnlyList<string> filePaths)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) || filePaths.Count == 0)
        {
            return false;
        }

        string[] paths = SanitizeDroppedPaths(filePaths);
        if (paths.Length == 0)
        {
            return false;
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

        // ELECTRON_RUN_AS_NODE=1 makes an Electron target (Obsidian, VS Code,
        // MarkText, ...) start as a plain Node process and fail on the file.
        // Windows does not carry that variable, so scrub it around the launch
        // exactly like Win32Helper.OpenFileOrChooseApp does for a plain open.
        string? savedElectronRunAsNode =
            Environment.GetEnvironmentVariable("ELECTRON_RUN_AS_NODE");
        if (savedElectronRunAsNode is not null)
        {
            Environment.SetEnvironmentVariable("ELECTRON_RUN_AS_NODE", null);
        }

        try
        {
            // No WorkingDirectory: the link's own start-in folder must win.
            // No Verb either (feedback 246): a NULL verb is the desktop
            // double-click dispatch, and link resolution forwards it to the
            // target's default launch. An explicit "open" verb adds nothing
            // on healthy machines and breaks where only the OS-side "open"
            // resolution is damaged — the same machine still drops onto the
            // shortcut in Explorer fine.
            Process.Start(new ProcessStartInfo(shortcutPath, arguments)
            {
                UseShellExecute = true
            });
            App.Log(
                $"[ShortcutLaunch] Launched '{shortcutPath}' with " +
                $"{paths.Length} dropped file(s).");
            return true;
        }
        catch (Exception ex)
        {
            App.Log(
                $"[ShortcutLaunch] Launch failed for '{shortcutPath}': " +
                $"{ex.Message}");
            return false;
        }
        finally
        {
            if (savedElectronRunAsNode is not null)
            {
                Environment.SetEnvironmentVariable(
                    "ELECTRON_RUN_AS_NODE",
                    savedElectronRunAsNode);
            }
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

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append('"').Append(path.Trim()).Append('"');
        }

        return builder.ToString();
    }
}
