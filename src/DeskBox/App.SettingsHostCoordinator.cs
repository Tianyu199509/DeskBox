namespace DeskBox;

using System.Diagnostics;

using DeskBox.Services;

/// <summary>
/// Main-process side of the settings-host lifecycle (batch 3d): every
/// settings entry point (tray, widget menus, toast deep links, bare
/// activation, jump list) routes through here — spawn the host if it is not
/// running, wake the existing one otherwise. The in-process window path
/// stays compiled only for the AOT smoke harness, which drives the window
/// directly inside its single process.
/// </summary>
public partial class App
{
    private Process? _settingsHostProcess;

    internal bool IsSettingsHostRunning =>
        _settingsHostProcess is { } host && !host.HasExited;

    /// <summary>
    /// True while a settings-host process exists and its window is up. The
    /// host exits when its window closes, so "process alive" is the
    /// user-visible state for every IsSettingsOpen consumer.
    /// </summary>
    internal bool IsSettingsHostVisible => IsSettingsHostRunning;

    private void ShowSettingsViaHost(string? sectionTag, string? glanceWidgetId)
    {
        if (IsShuttingDown)
        {
            return;
        }

        if (!IsSettingsHostRunning)
        {
            StartSettingsHostProcess(BuildSettingsHostArguments(sectionTag, glanceWidgetId));
            return;
        }

        // The host is up, so the pipe is connected: push the navigation.
        if (glanceWidgetId is not null)
        {
            _ = PushSettingsIpcEventAsync(SettingsIpcMethods.HostShowGlance, glanceWidgetId);
        }
        else
        {
            _ = PushSettingsIpcEventAsync(SettingsIpcMethods.HostShow, sectionTag);
        }
    }

    /// <summary>
    /// The host command line: the launch argument pair plus the initial
    /// deep link (a section tag or a glance widget id). The deep link rides
    /// the command line because the host cannot receive pushed events
    /// before its pipe connects, and callers do not wait for that.
    /// </summary>
    private static List<string> BuildSettingsHostArguments(string? sectionTag, string? glanceWidgetId)
    {
        var arguments = new List<string>
        {
            "--settings",
            Environment.ProcessId.ToString(),
        };
        if (glanceWidgetId is not null)
        {
            arguments.Add("--settings-glance");
            arguments.Add(glanceWidgetId);
        }
        else if (!string.IsNullOrEmpty(sectionTag))
        {
            arguments.Add("--settings-section");
            arguments.Add(sectionTag);
        }

        return arguments;
    }

    /// <summary>Spawns the settings-host process; see BuildSettingsHostArguments.</summary>
    private void StartSettingsHostProcess(List<string> arguments)
    {
        try
        {
            string exePath = Environment.ProcessPath ??
                Path.Combine(AppContext.BaseDirectory, "DeskBox.exe");
            var startInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            _settingsHostProcess = Process.Start(startInfo);
            Log("[SettingsHost] Spawned pid=" + (_settingsHostProcess?.Id.ToString() ?? "null"));
        }
        catch (Exception ex)
        {
            Log($"[SettingsHost] Spawn failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Shutdown step (batch 3d): tell the host to close, give it a short
    /// grace window, then hard-terminate — mirroring the updater's pattern.
    /// </summary>
    private void ShutdownSettingsHost()
    {
        if (_settingsHostProcess is not { } host || host.HasExited)
        {
            return;
        }

        _ = PushSettingsIpcEventAsync(SettingsIpcMethods.HostShutdown, null);
        try
        {
            if (!host.WaitForExit(4000))
            {
                host.Kill();
                host.WaitForExit(2000);
            }
        }
        catch (Exception ex)
        {
            Log($"[SettingsHost] Shutdown wait failed: {ex.Message}");
        }

        _settingsHostProcess = null;
    }
}
