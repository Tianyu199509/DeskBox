using System.Diagnostics;
using System.Globalization;

namespace DeskBox.Helpers;

/// <summary>
/// Shows Explorer's context menu in a short-lived native process. Shell menu
/// extensions are third-party native code, so they must never be loaded into
/// the DeskBox process where an access violation would terminate the app.
/// </summary>
internal static class ShellContextMenuProxy
{
    internal enum MenuResult
    {
        Invoked,
        Cancelled,
        Failed
    }

    internal const int InvokedExitCode = 0;
    internal const int CancelledExitCode = 2;
    internal const int FailedExitCode = 3;
    private const string ReadyMessage = "ready";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan InteractionTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TerminationTimeout = TimeSpan.FromSeconds(5);

    public static async Task<MenuResult> ShowAsync(
        string path,
        int screenX,
        int screenY)
    {
        string normalizedPath = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            App.Log(
                $"[ShellContextMenuProxy] Invalid source path={normalizedPath}");
            return MenuResult.Failed;
        }

        string executablePath = Path.Combine(
            AppContext.BaseDirectory,
            ShellThumbnailProxy.ExecutableName);
        if (!File.Exists(executablePath))
        {
            App.Log(
                $"[ShellContextMenuProxy] Native proxy is missing: " +
                executablePath);
            return MenuResult.Failed;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--context-menu");
        startInfo.ArgumentList.Add(normalizedPath);
        startInfo.ArgumentList.Add(screenX.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(screenY.ToString(CultureInfo.InvariantCulture));

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                App.Log("[ShellContextMenuProxy] Native proxy did not start");
                return MenuResult.Failed;
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[ShellContextMenuProxy] Native proxy start failed " +
                $"path={normalizedPath}: {ex.Message}");
            return MenuResult.Failed;
        }

        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        string? readyMessage;
        try
        {
            readyMessage = await process.StandardOutput
                .ReadLineAsync()
                .WaitAsync(StartupTimeout);
        }
        catch (TimeoutException)
        {
            TryKill(process);
            await ObserveExitAsync(process);
            string error = await ObserveErrorAsync(errorTask);
            App.Log(
                $"[ShellContextMenuProxy] Menu preparation timed out " +
                $"timeoutMs={StartupTimeout.TotalMilliseconds:0} " +
                $"path={normalizedPath} error={error}");
            return MenuResult.Failed;
        }
        catch (Exception ex)
        {
            TryKill(process);
            await ObserveExitAsync(process);
            _ = await ObserveErrorAsync(errorTask);
            App.Log(
                $"[ShellContextMenuProxy] Ready handshake failed " +
                $"path={normalizedPath}: {ex.Message}");
            return MenuResult.Failed;
        }

        if (!string.Equals(readyMessage, ReadyMessage, StringComparison.Ordinal))
        {
            TryKill(process);
            await ObserveExitAsync(process);
            string error = await ObserveErrorAsync(errorTask);
            App.Log(
                $"[ShellContextMenuProxy] Menu preparation failed " +
                $"exit={FormatExitCode(process)} path={normalizedPath} " +
                $"ready={readyMessage ?? "<eof>"} error={error}");
            return MenuResult.Failed;
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(InteractionTimeout);
        }
        catch (TimeoutException)
        {
            TryKill(process);
            await ObserveExitAsync(process);
            string error = await ObserveErrorAsync(errorTask);
            App.Log(
                $"[ShellContextMenuProxy] Menu interaction timed out " +
                $"timeoutMs={InteractionTimeout.TotalMilliseconds:0} " +
                $"path={normalizedPath} error={error}");
            return MenuResult.Failed;
        }
        catch (Exception ex)
        {
            TryKill(process);
            await ObserveExitAsync(process);
            _ = await ObserveErrorAsync(errorTask);
            App.Log(
                $"[ShellContextMenuProxy] Native proxy wait failed " +
                $"path={normalizedPath}: {ex.Message}");
            return MenuResult.Failed;
        }

        string standardError = await ObserveErrorAsync(errorTask);
        MenuResult result = MapExitCode(process.ExitCode);
        if (result == MenuResult.Failed)
        {
            App.Log(
                $"[ShellContextMenuProxy] Native proxy failed " +
                $"exit={FormatExitCode(process)} path={normalizedPath} " +
                $"error={standardError}");
        }
        else
        {
            App.LogVerbose(
                $"[ShellContextMenuProxy] Completed result={result} " +
                $"path={normalizedPath}");
        }

        return result;
    }

    internal static MenuResult MapExitCode(int exitCode) => exitCode switch
    {
        InvokedExitCode => MenuResult.Invoked,
        CancelledExitCode => MenuResult.Cancelled,
        _ => MenuResult.Failed
    };

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path?.Trim() ?? string.Empty;
        }
    }

    private static string FormatExitCode(Process process)
    {
        try
        {
            return $"0x{unchecked((uint)process.ExitCode):X8}";
        }
        catch
        {
            return "unavailable";
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static async Task ObserveExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync()
                .WaitAsync(TerminationTimeout);
        }
        catch
        {
        }
    }

    private static async Task<string> ObserveErrorAsync(Task<string> errorTask)
    {
        try
        {
            return (await errorTask.WaitAsync(TerminationTimeout)).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }
}
