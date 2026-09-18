using DeskBox.Platform;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;

namespace DeskBox.Helpers;

/// <summary>
/// Shows Explorer's context menu through a persistent native server process.
/// Shell menu extensions are third-party native code, so they must never be
/// loaded into the DeskBox process where an access violation would terminate
/// the app. The server keeps one STA apartment warm across right-clicks, exits
/// by itself when DeskBox closes its stdin pipe, and is killed and restarted
/// whenever a round violates its phase budget. If a server round fails before
/// any menu became visible, one one-shot round is attempted as a fallback so a
/// broken server can never silently disable the feature. Only one menu may be
/// open at a time; further right-clicks are ignored while the native modal loop
/// runs.
/// </summary>
internal static class ShellContextMenuProxy
{
    internal enum MenuResult
    {
        Invoked,
        Cancelled,
        Failed
    }

    private enum ProtocolKind
    {
        ServerReady,
        MenuShown,
        Result,
        Bye,
        Warm,
        EndOfStream
    }

    private sealed record ProtocolLine(ProtocolKind Kind, int ResultCode, string Detail = "");

    private sealed record RoundOutcome(MenuResult Result, bool TransportFailure);

    private readonly record struct PendingRequest(string Path, int ScreenX, int ScreenY);

    internal const int InvokedExitCode = 0;
    internal const int CancelledExitCode = 2;
    internal const int FailedExitCode = 3;
    private const string ServerArgument = "--context-menu-server";
    private const string OneShotArgument = "--context-menu";
    private const string ReadyMessage = "ready";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan InteractionTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TerminationTimeout = TimeSpan.FromSeconds(5);
    // Encoding.UTF8 emits a byte order mark on the first write to a child's
    // stdin; the native parser then discards that command as handler noise.
    private static readonly Encoding ProtocolEncoding =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly SemaphoreSlim s_serverGate = new(1, 1);
    private static readonly object s_pendingGate = new();
    private static PendingRequest? s_pendingRequest;
    private static int s_menuInFlight;
    private static int s_serverGeneration;
    private static ProxyServer? s_server;

    static ShellContextMenuProxy()
    {
        // The server also exits by itself on stdin EOF; this kill is a belt
        // for shutdown paths that terminate the runtime abruptly.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeServer();
    }

    public static async Task<MenuResult> ShowAsync(
        string path,
        int screenX,
        int screenY)
    {
        string normalizedPath = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            App.Log($"[ShellContextMenuProxy] Invalid source path={normalizedPath}");
            return MenuResult.Failed;
        }

        if (Interlocked.CompareExchange(ref s_menuInFlight, 1, 0) != 0)
        {
            // A menu is already open. DeskBox's widget windows never take
            // activation, so the open menu cannot notice this click through the
            // usual foreground change: close it explicitly and remember the
            // request, so the new menu opens as soon as the old one is gone.
            lock (s_pendingGate)
            {
                s_pendingRequest = new PendingRequest(normalizedPath, screenX, screenY);
            }

            CancelOpenMenu();
            return MenuResult.Cancelled;
        }

        try
        {
            var current = new PendingRequest(normalizedPath, screenX, screenY);
            while (true)
            {
                MenuResult result = await ShowOneRequestAsync(current);
                PendingRequest? next;
                lock (s_pendingGate)
                {
                    next = s_pendingRequest;
                    s_pendingRequest = null;
                }

                if (next is null)
                {
                    return result;
                }

                current = next.Value;
            }
        }
        finally
        {
            Interlocked.Exchange(ref s_menuInFlight, 0);
            lock (s_pendingGate)
            {
                s_pendingRequest = null;
            }
        }
    }

    /// <summary>
    /// Closes an open native menu. DeskBox's widget windows are created without
    /// activation, so a click on them never produces the WM_ACTIVATE that would
    /// otherwise dismiss a hosted menu; the native side ends the menu when we
    /// ask it to. Safe to call on every pointer press: it returns immediately
    /// unless a menu is actually open.
    /// </summary>
    public static void CancelOpenMenu()
    {
        if (Volatile.Read(ref s_menuInFlight) == 0)
        {
            return;
        }

        if (s_server is not { } server)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await server.SendCommandAsync("cancel", TerminationTimeout);
            }
            catch (Exception ex)
            {
                App.LogVerbose(
                    $"[ShellContextMenuProxy] Cancel request failed: {ex.Message}");
            }
        });
    }

    private static async Task<MenuResult> ShowOneRequestAsync(PendingRequest request)
    {
        RoundOutcome outcome = await TryShowWithRetryAsync(
            request.Path,
            request.ScreenX,
            request.ScreenY);
        if (!outcome.TransportFailure)
        {
            return outcome.Result;
        }

        // The server path failed before any menu became visible, so the user
        // has seen nothing yet. Fall back to one self-contained round instead
        // of reporting a failure for a feature that works.
        App.Log(
            "[ShellContextMenuProxy] Server round failed before showing a " +
            $"menu; falling back to one-shot path={request.Path}");
        RoundOutcome fallback = await TryShowOneShotAsync(
            request.Path,
            request.ScreenX,
            request.ScreenY);
        return fallback.Result;
    }

    /// <summary>
    /// Starts the native server (if needed) and builds one throwaway menu so
    /// third-party handler DLLs are warm before the user's first right-click.
    /// Failures are silent: the first real menu pays the cold cost instead.
    /// </summary>
    public static void Prewarm()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await s_serverGate.WaitAsync();
                try
                {
                    ProxyServer server = await EnsureServerAsync();
                    await server.SendCommandAsync("warmup", StartupTimeout);
                    ProtocolLine reply = await server.ReadProtocolLineAsync(StartupTimeout);
                    App.LogVerbose(
                        $"[ShellContextMenuProxy] Prewarm reply={reply.Kind} " +
                        $"generation={server.Generation}");
                }
                finally
                {
                    s_serverGate.Release();
                }
            }
            catch (Exception ex)
            {
                // A warmup that never answered may have left the server stuck
                // inside a third-party handler; never reuse it.
                DisposeServer();
                App.Log($"[ShellContextMenuProxy] Prewarm failed: {ex.Message}");
            }
        });
    }

    private static async Task<RoundOutcome> TryShowWithRetryAsync(
        string normalizedPath,
        int screenX,
        int screenY)
    {
        RoundOutcome outcome = await TryShowServerRoundAsync(
            normalizedPath,
            screenX,
            screenY);
        if (!outcome.TransportFailure)
        {
            return outcome;
        }

        App.Log(
            "[ShellContextMenuProxy] Retrying menu with a fresh server " +
            $"path={normalizedPath}");
        return await TryShowServerRoundAsync(normalizedPath, screenX, screenY);
    }

    private static async Task<RoundOutcome> TryShowServerRoundAsync(
        string normalizedPath,
        int screenX,
        int screenY)
    {
        var timing = Stopwatch.StartNew();
        await s_serverGate.WaitAsync();
        try
        {
            ProxyServer server;
            try
            {
                server = await EnsureServerAsync();
            }
            catch (Exception ex)
            {
                App.Log(
                    "[ShellContextMenuProxy] Server start failed " +
                    $"path={normalizedPath}: {ex.Message}");
                return new RoundOutcome(MenuResult.Failed, TransportFailure: true);
            }

            long serverMs = timing.ElapsedMilliseconds;
            GrantForegroundTo(server.ProcessId);
            string command =
                "menu\t" +
                $"{screenX.ToString(CultureInfo.InvariantCulture)}\t" +
                $"{screenY.ToString(CultureInfo.InvariantCulture)}\t" +
                $"{MenuThemeToken()}\t" +
                normalizedPath;
            try
            {
                await server.SendCommandAsync(command, BuildTimeout);
            }
            catch (Exception ex)
            {
                DisposeServer();
                App.Log(
                    "[ShellContextMenuProxy] Menu command write failed " +
                    $"generation={server.Generation} path={normalizedPath}: {ex.Message}");
                return new RoundOutcome(MenuResult.Failed, TransportFailure: true);
            }

            ProtocolLine announcement;
            try
            {
                announcement = await server.ReadProtocolLineAsync(BuildTimeout);
            }
            catch (TimeoutException)
            {
                DisposeServer();
                App.Log(
                    "[ShellContextMenuProxy] Menu build timed out " +
                    $"timeoutMs={BuildTimeout.TotalMilliseconds:0} serverMs={serverMs:0} " +
                    $"generation={server.Generation} exited={server.HasExited} " +
                    $"path={normalizedPath} error={server.ErrorTail}");
                return new RoundOutcome(MenuResult.Failed, TransportFailure: true);
            }

            if (announcement.Kind != ProtocolKind.MenuShown)
            {
                DisposeServer();
                App.Log(
                    "[ShellContextMenuProxy] Menu build failed " +
                    $"announcement={announcement.Kind} serverMs={serverMs:0} " +
                    $"generation={server.Generation} path={normalizedPath} " +
                    $"error={server.ErrorTail}");
                // A clean "result" reply means the server saw the command and
                // reported a failure; a fresh server would fail identically.
                bool transport = announcement.Kind != ProtocolKind.Result;
                return new RoundOutcome(MenuResult.Failed, transport);
            }

            long buildMs = timing.ElapsedMilliseconds;
            ProtocolLine outcome;
            try
            {
                outcome = await server.ReadProtocolLineAsync(InteractionTimeout);
            }
            catch (TimeoutException)
            {
                DisposeServer();
                App.Log(
                    "[ShellContextMenuProxy] Menu interaction timed out " +
                    $"timeoutMs={InteractionTimeout.TotalMilliseconds:0} buildMs={buildMs:0} " +
                    $"path={normalizedPath} error={server.ErrorTail}");
                return new RoundOutcome(MenuResult.Failed, TransportFailure: false);
            }

            if (outcome.Kind != ProtocolKind.Result)
            {
                DisposeServer();
                App.Log(
                    "[ShellContextMenuProxy] Menu outcome lost " +
                    $"outcome={outcome.Kind} buildMs={buildMs:0} " +
                    $"path={normalizedPath} error={server.ErrorTail}");
                return new RoundOutcome(MenuResult.Failed, TransportFailure: false);
            }

            MenuResult result = MapExitCode(outcome.ResultCode);
            if (server.HasExited)
            {
                // Retirement or an async death after the reply; the next
                // request spawns a fresh server either way.
                DisposeServer();
            }

            LogTiming(
                normalizedPath,
                result,
                serverMs,
                buildMs,
                timing.ElapsedMilliseconds,
                outcome.Detail);
            return new RoundOutcome(result, TransportFailure: false);
        }
        catch (Exception ex)
        {
            DisposeServer();
            App.Log(
                "[ShellContextMenuProxy] Menu round crashed " +
                $"path={normalizedPath}: {ex.Message}");
            return new RoundOutcome(MenuResult.Failed, TransportFailure: true);
        }
        finally
        {
            s_serverGate.Release();
        }
    }

    /// <summary>
    /// Self-contained round: spawn, handshake, one menu, exit. Slower than the
    /// server path but it shares no long-lived state, so it is the last resort
    /// when a server round failed without showing anything.
    /// </summary>
    private static async Task<RoundOutcome> TryShowOneShotAsync(
        string normalizedPath,
        int screenX,
        int screenY)
    {
        string executablePath = ResolveProxyExecutablePath();
        if (!File.Exists(executablePath))
        {
            App.Log($"[ShellContextMenuProxy] Native proxy is missing: {executablePath}");
            return new RoundOutcome(MenuResult.Failed, TransportFailure: false);
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
        startInfo.ArgumentList.Add(OneShotArgument);
        startInfo.ArgumentList.Add(normalizedPath);
        startInfo.ArgumentList.Add(screenX.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(screenY.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(MenuThemeToken());

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                App.Log("[ShellContextMenuProxy] One-shot proxy did not start");
                return new RoundOutcome(MenuResult.Failed, TransportFailure: false);
            }
        }
        catch (Exception ex)
        {
            App.Log(
                "[ShellContextMenuProxy] One-shot proxy start failed " +
                $"path={normalizedPath}: {ex.Message}");
            return new RoundOutcome(MenuResult.Failed, TransportFailure: false);
        }

        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        GrantForegroundTo(process.Id);
        try
        {
            string? readyMessage = await process.StandardOutput
                .ReadLineAsync()
                .WaitAsync(StartupTimeout);
            if (!string.Equals(readyMessage, ReadyMessage, StringComparison.Ordinal))
            {
                TryKill(process);
                App.Log(
                    "[ShellContextMenuProxy] One-shot menu preparation failed " +
                    $"path={normalizedPath} ready={readyMessage ?? "<eof>"} " +
                    $"error={await ObserveErrorAsync(errorTask)}");
                return new RoundOutcome(MenuResult.Failed, TransportFailure: false);
            }

            await process.WaitForExitAsync().WaitAsync(InteractionTimeout);
        }
        catch (TimeoutException)
        {
            TryKill(process);
            await ObserveExitAsync(process);
            App.Log(
                "[ShellContextMenuProxy] One-shot menu timed out " +
                $"path={normalizedPath} error={await ObserveErrorAsync(errorTask)}");
            return new RoundOutcome(MenuResult.Failed, TransportFailure: false);
        }
        catch (Exception ex)
        {
            TryKill(process);
            await ObserveExitAsync(process);
            App.Log(
                "[ShellContextMenuProxy] One-shot menu failed " +
                $"path={normalizedPath}: {ex.Message}");
            return new RoundOutcome(MenuResult.Failed, TransportFailure: false);
        }

        string standardError = await ObserveErrorAsync(errorTask);
        MenuResult result = MapExitCode(process.ExitCode);
        if (result == MenuResult.Failed)
        {
            App.Log(
                "[ShellContextMenuProxy] One-shot proxy failed " +
                $"path={normalizedPath} error={standardError}");
        }
        else
        {
            App.Log(
                $"[ShellContextMenuProxy] One-shot menu result={result} " +
                $"path={normalizedPath}");
        }

        return new RoundOutcome(result, TransportFailure: false);
    }

    private static async Task<ProxyServer> EnsureServerAsync()
    {
        if (s_server is { } alive && !alive.HasExited)
        {
            return alive;
        }

        DisposeServer();
        string executablePath = ResolveProxyExecutablePath();
        if (!File.Exists(executablePath))
        {
            throw new InvalidOperationException(
                $"Native proxy is missing: {executablePath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = ProtocolEncoding,
            StandardOutputEncoding = ProtocolEncoding,
            StandardErrorEncoding = ProtocolEncoding,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(ServerArgument);
        startInfo.ArgumentList.Add(MenuThemeToken());

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Native proxy did not start");
        }

        int generation = Interlocked.Increment(ref s_serverGeneration);
        var server = new ProxyServer(process, generation);
        s_server = server;
        try
        {
            ProtocolLine handshake = await server.ReadProtocolLineAsync(StartupTimeout);
            if (handshake.Kind != ProtocolKind.ServerReady)
            {
                throw new InvalidOperationException(
                    $"Unexpected server handshake: {handshake.Kind}");
            }

            App.LogVerbose(
                $"[ShellContextMenuProxy] Server started generation={generation}");
            return server;
        }
        catch
        {
            DisposeServer();
            throw;
        }
    }

    internal static MenuResult MapExitCode(int exitCode) => exitCode switch
    {
        InvokedExitCode => MenuResult.Invoked,
        CancelledExitCode => MenuResult.Cancelled,
        _ => MenuResult.Failed
    };

    /// <summary>
    /// Whether the Shell menu should render in the dark theme, matching the
    /// theme DeskBox itself is showing.
    /// </summary>
    private static bool IsDarkMenuExpected()
    {
        try
        {
            if (App.Current is not { } app)
            {
                return false;
            }

            return app.ThemeService.CurrentTheme switch
            {
                ElementTheme.Dark => true,
                ElementTheme.Light => false,
                // "System": resolve through the registry probe instead of
                // Application.RequestedTheme. This runs on the prewarm's
                // threadpool thread, and reading a XAML application static off
                // the UI thread is not merely a catchable wrong-thread error:
                // it crashed the process as an AccessViolation (0xC0000005,
                // measured 2026-09-13), which no catch block can absorb.
                _ => Win32Helper.IsSystemDarkMode()
            };
        }
        catch
        {
            return false;
        }
    }

    private static string MenuThemeToken() => IsDarkMenuExpected() ? "dark" : "light";

    /// <summary>
    /// Lets the native proxy take the foreground, which TrackPopupMenuEx needs
    /// for its menu to behave like a foreground menu (dismissal, keyboard). The
    /// grant is consumed by the proxy's next successful SetForegroundWindow, so
    /// it is issued before every round.
    /// </summary>
    private static void GrantForegroundTo(int processId)
    {
        try
        {
            _ = AllowSetForegroundWindow(processId);
        }
        catch (Exception ex)
        {
            App.LogVerbose(
                $"[ShellContextMenuProxy] Foreground grant failed: {ex.Message}");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    private static string ResolveProxyExecutablePath() => Path.Combine(
        AppContext.BaseDirectory,
        ShellThumbnailProxy.ExecutableName);

    private static void LogTiming(
        string path,
        MenuResult result,
        long serverMs,
        long buildMs,
        long totalMs,
        string detail)
    {
        // Logged unconditionally: this is the only trace of a menu that opened
        // but produced no visible effect, which is the reported symptom. The
        // native detail reports whether the menu got the foreground and the
        // input hook, which decide how it can be dismissed.
        App.Log(
            $"[ShellContextMenuProxy] Menu timing result={result} " +
            $"serverMs={serverMs:0} buildMs={buildMs:0} totalMs={totalMs:0} " +
            $"native={detail} path={path}");
    }

    private static void DisposeServer()
    {
        s_server?.Dispose();
        s_server = null;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            // Shell item parsing rejects forward slashes with E_INVALIDARG, so
            // the canonical Windows form is mandatory here.
            return Path.GetFullPath(path);
        }
        catch
        {
            return path?.Trim() ?? string.Empty;
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
            await process.WaitForExitAsync().WaitAsync(TerminationTimeout);
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

    private sealed class ProxyServer : IDisposable
    {
        private const int ErrorTailCharacters = 4000;
        private readonly Process _process;
        private readonly Task _errorDrain;
        private readonly object _errorGate = new();
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private string _errorTail = string.Empty;
        private bool _disposed;

        internal ProxyServer(Process process, int generation)
        {
            _process = process;
            Generation = generation;
            _errorDrain = Task.Run(async () =>
            {
                try
                {
                    // Handlers loaded in the child may write to stderr; the
                    // drain must stay ahead of them or the pipe fills and the
                    // child blocks mid-menu.
                    while (await process.StandardError.ReadLineAsync() is { } line)
                    {
                        lock (_errorGate)
                        {
                            _errorTail += line + Environment.NewLine;
                            if (_errorTail.Length > ErrorTailCharacters)
                            {
                                _errorTail = _errorTail[^ErrorTailCharacters..];
                            }
                        }
                    }
                }
                catch
                {
                }
            });
        }

        internal int Generation { get; }

        internal int ProcessId
        {
            get
            {
                try
                {
                    return _process.Id;
                }
                catch
                {
                    return 0;
                }
            }
        }

        internal bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch
                {
                    return true;
                }
            }
        }

        internal string ErrorTail
        {
            get
            {
                lock (_errorGate)
                {
                    return _errorTail.Trim();
                }
            }
        }

        internal async Task SendCommandAsync(string command, TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            // A cancel request may arrive while a menu command is being sent;
            // interleaved writes would corrupt both protocol lines.
            await _writeGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                await _process.StandardInput.WriteLineAsync(
                    command.AsMemory(),
                    cancellation.Token);
                await _process.StandardInput.FlushAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"Command write timed out after {timeout.TotalMilliseconds:0}ms");
            }
            finally
            {
                _writeGate.Release();
            }
        }

        internal async Task<ProtocolLine> ReadProtocolLineAsync(TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            while (true)
            {
                string? line;
                try
                {
                    line = await _process.StandardOutput.ReadLineAsync(
                        cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException(
                        $"Protocol read timed out after {timeout.TotalMilliseconds:0}ms");
                }

                if (line is null)
                {
                    return new ProtocolLine(ProtocolKind.EndOfStream, FailedExitCode);
                }

                ProtocolLine? parsed = ParseProtocolLine(line);
                if (parsed is null)
                {
                    // Third-party handlers loaded in the child printf into the
                    // shared stdout. Report it: a dropped protocol line used to
                    // stall the round silently until the phase budget expired.
                    if (line.Length > 0)
                    {
                        App.Log(
                            "[ShellContextMenuProxy] Ignored unexpected native " +
                            $"output generation={Generation} line='{line}'");
                    }

                    continue;
                }

                return parsed;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            try
            {
                _process.Dispose();
            }
            catch
            {
            }
        }

        private static ProtocolLine? ParseProtocolLine(string line)
        {
            int separator = line.IndexOf(' ');
            string token = separator < 0 ? line : line[..separator];
            switch (token)
            {
                case ReadyMessage:
                    return new ProtocolLine(ProtocolKind.ServerReady, FailedExitCode);
                case "shown":
                    return new ProtocolLine(ProtocolKind.MenuShown, FailedExitCode);
                case "result":
                    string remainder = separator < 0
                        ? string.Empty
                        : line[(separator + 1)..].Trim();
                    // "result <code> src=… fg=… hook=…"; only the first token
                    // is the exit code.
                    int detailSeparator = remainder.IndexOf(' ');
                    string codeText = detailSeparator < 0
                        ? remainder
                        : remainder[..detailSeparator];
                    return new ProtocolLine(
                        ProtocolKind.Result,
                        int.TryParse(
                            codeText,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out int code)
                            ? code
                            : FailedExitCode,
                        remainder);
                case "bye":
                    return new ProtocolLine(ProtocolKind.Bye, FailedExitCode);
                case "warm":
                    return new ProtocolLine(ProtocolKind.Warm, FailedExitCode);
                default:
                    return null;
            }
        }
    }
}
