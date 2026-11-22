using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DeskBox.Helpers;

namespace DeskBox.Services.Media;

/// <summary>
/// Process-wide client managing the dedicated out-of-process Rust SMTC server
/// (<c>deskbox-thumbnail-proxy.exe --media-session-server</c>). Keeps
/// <c>Windows.Media.MediaControl.dll</c> completely out of <c>DeskBox.exe</c>
/// while multiplexing events, cached session lists, and requests across all
/// Music widgets and Custom Widget media subscribers.
/// </summary>
internal sealed class MediaSessionProxyClient
{
    private const string ServerArgument = "--media-session-server";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan IdleShutdownDelay = TimeSpan.FromSeconds(5);
    private static readonly Encoding ProtocolEncoding =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    internal static MediaSessionProxyClient Shared { get; } = new();

    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _processLifecycleGate = new(1, 1);
    private readonly MediaSessionCircuitBreaker _circuitBreaker = new();
    private readonly Func<DateTimeOffset> _utcNow;

    private int _subscriberCount;
    private ProxyProcess? _process;
    private Timer? _idleShutdownTimer;
    private Timer? _reconnectTimer;
    private long _nextRequestId;
    private string _lastOperation = "none";
    private IReadOnlyList<MusicSessionOption> _cachedSessions = [];
    private MediaSessionConnectionState _publishedState = MediaSessionConnectionState.Healthy;

    internal event EventHandler? SessionsChanged;
    internal event EventHandler? CurrentSessionChanged;
    internal event EventHandler? PlaybackInfoChanged;
    internal event EventHandler? MediaPropertiesChanged;
    internal event EventHandler? TimelinePropertiesChanged;
    internal event EventHandler? ConnectionStateChanged;

    static MediaSessionProxyClient()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shared.ShutdownForProcessExit();
    }

    internal MediaSessionProxyClient(Func<DateTimeOffset>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal int ActiveLeaseCount
    {
        get
        {
            lock (_stateGate)
            {
                return _subscriberCount;
            }
        }
    }

    internal MediaSessionConnectionState ConnectionState
    {
        get
        {
            lock (_stateGate)
            {
                return _publishedState;
            }
        }
    }

    internal IReadOnlyList<MusicSessionOption> GetCachedSessionOptions()
    {
        lock (_stateGate)
        {
            return _cachedSessions;
        }
    }

    internal void AcquireLease()
    {
        lock (_stateGate)
        {
            _subscriberCount++;
            CancelIdleShutdownTimerLocked();
        }
    }

    internal void ReleaseLease()
    {
        bool shouldArmIdleShutdown = false;
        lock (_stateGate)
        {
            if (_subscriberCount <= 0)
            {
                return;
            }

            _subscriberCount--;
            if (_subscriberCount == 0)
            {
                CancelReconnectTimerLocked();
                shouldArmIdleShutdown = true;
            }
        }

        if (shouldArmIdleShutdown)
        {
            ArmIdleShutdownTimer();
        }
    }

    internal async Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        ProxyProcess? existing = GetAliveProcess();
        if (existing is not null)
        {
            return true;
        }

        if (!_circuitBreaker.CanStartNow(_utcNow(), out _))
        {
            UpdatePublishedState(_circuitBreaker.State);
            return false;
        }

        await _processLifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = GetAliveProcess();
            if (existing is not null)
            {
                return true;
            }

            if (ActiveLeaseCount <= 0)
            {
                return false;
            }

            if (!_circuitBreaker.CanStartNow(_utcNow(), out _))
            {
                UpdatePublishedState(_circuitBreaker.State);
                return false;
            }

            (ProxyProcess? started, bool alreadyReportedExit, string failureReason) =
                await StartProcessLockedAsync(cancellationToken).ConfigureAwait(false);
            if (started is null)
            {
                if (!alreadyReportedExit && ActiveLeaseCount > 0)
                {
                    HandleUnexpectedExit(exitCode: -1, stderrTail: failureReason);
                }

                return false;
            }

            _circuitBreaker.RecordStarted(_utcNow());
            UpdatePublishedState(MediaSessionConnectionState.Healthy);
            _ = RefreshCachedSessionsSafeAsync();
            return true;
        }
        finally
        {
            _processLifecycleGate.Release();
        }
    }

    internal async Task<bool> RetryConnectionAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateGate)
        {
            CancelReconnectTimerLocked();
            CancelIdleShutdownTimerLocked();
        }

        await _processLifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DisposeCurrentProcessLocked(intentional: true);
            _circuitBreaker.ResetForManualRetry();
            UpdatePublishedState(MediaSessionConnectionState.Healthy);

            if (ActiveLeaseCount <= 0)
            {
                return false;
            }

            (ProxyProcess? started, bool alreadyReportedExit, string failureReason) =
                await StartProcessLockedAsync(cancellationToken).ConfigureAwait(false);
            if (started is null)
            {
                if (!alreadyReportedExit && ActiveLeaseCount > 0)
                {
                    HandleUnexpectedExit(exitCode: -1, stderrTail: failureReason);
                }

                return false;
            }

            _circuitBreaker.RecordStarted(_utcNow());
            UpdatePublishedState(MediaSessionConnectionState.Healthy);
        }
        finally
        {
            _processLifecycleGate.Release();
        }

        await RefreshCachedSessionsSafeAsync().ConfigureAwait(false);
        SessionsChanged?.Invoke(this, EventArgs.Empty);
        CurrentSessionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal async Task<IReadOnlyList<MusicSessionOption>> RefreshSessionOptionsAsync()
    {
        MediaSessionProxyMessage? reply = await SendRequestAsync(
            new MediaSessionProxyCommand { Op = "get_sessions" },
            DefaultRequestTimeout).ConfigureAwait(false);
        if (reply?.Ok != true || reply.Sessions is null)
        {
            return GetCachedSessionOptions();
        }

        var options = new List<MusicSessionOption>(reply.Sessions.Count);
        foreach (MediaSessionOptionDto dto in reply.Sessions)
        {
            string sourceApp = dto.SourceAppUserModelId ?? string.Empty;
            options.Add(new MusicSessionOption(
                dto.SessionId,
                sourceApp,
                MusicSessionService.GetSourceDisplayName(sourceApp),
                MediaSessionProxyProtocol.ParsePlaybackState(dto.PlaybackState),
                dto.IsSystemCurrent));
        }

        lock (_stateGate)
        {
            _cachedSessions = options;
        }

        return options;
    }

    internal async Task<MusicSessionInfo?> GetCurrentSessionInfoAsync(
        string? preferredSessionId = null,
        ulong knownCoverFingerprint = 0)
    {
        MediaSessionProxyMessage? reply = await SendRequestAsync(
            new MediaSessionProxyCommand
            {
                Op = "get_info",
                PreferredSessionId = string.IsNullOrWhiteSpace(preferredSessionId) ? null : preferredSessionId,
                KnownCoverFingerprint = knownCoverFingerprint == 0
                    ? null
                    : knownCoverFingerprint.ToString(CultureInfo.InvariantCulture)
            },
            DefaultRequestTimeout).ConfigureAwait(false);

        if (reply?.Ok != true || reply.Info is null)
        {
            return null;
        }

        MediaSessionInfoDto dto = reply.Info;
        string sourceApp = dto.SourceAppUserModelId ?? string.Empty;
        ulong fingerprint = MediaSessionProxyProtocol.ParseFingerprint(dto.ThumbnailFingerprint);
        byte[]? thumbBytes = dto.HasThumbnail && !dto.ThumbnailUnchanged
            ? MediaSessionProxyProtocol.TryDecodeBase64(dto.ThumbnailBase64)
            : null;
        return new MusicSessionInfo(
            dto.SessionId ?? string.Empty,
            sourceApp,
            MusicSessionService.GetSourceDisplayName(sourceApp),
            dto.Title ?? string.Empty,
            dto.Artist ?? string.Empty,
            dto.Album ?? string.Empty,
            MediaSessionProxyProtocol.ParsePlaybackState(dto.PlaybackState),
            TimeSpan.FromTicks(Math.Max(0L, dto.PositionTicks)),
            TimeSpan.FromTicks(Math.Max(0L, dto.DurationTicks)),
            MediaSessionProxyProtocol.ParseFileTimeUtc(dto.PositionUpdatedFileTimeTicks),
            double.IsFinite(dto.PlaybackRate) && dto.PlaybackRate is >= 0.05 and <= 4.0 ? dto.PlaybackRate : 1.0,
            dto.CanPlay,
            dto.CanPause,
            dto.CanGoPrevious,
            dto.CanGoNext,
            dto.CanSeek,
            dto.CanChangeShuffle,
            dto.CanChangeRepeat,
            MediaSessionProxyProtocol.ParsePlaybackMode(dto.PlaybackMode),
            ThumbnailBytes: thumbBytes,
            ThumbnailFingerprint: fingerprint,
            ThumbnailUnchanged: dto.HasThumbnail && dto.ThumbnailUnchanged,
            ThumbnailContentType: dto.ThumbnailContentType);
    }

    internal async Task<MusicThumbnailSnapshot?> GetCurrentThumbnailAsync(
        string? preferredSessionId = null,
        ulong knownCoverFingerprint = 0)
    {
        MediaSessionProxyMessage? reply = await SendRequestAsync(
            new MediaSessionProxyCommand
            {
                Op = "get_thumbnail",
                PreferredSessionId = string.IsNullOrWhiteSpace(preferredSessionId) ? null : preferredSessionId,
                KnownCoverFingerprint = knownCoverFingerprint == 0
                    ? null
                    : knownCoverFingerprint.ToString(CultureInfo.InvariantCulture)
            },
            DefaultRequestTimeout).ConfigureAwait(false);

        if (reply?.Ok != true || reply.Thumbnail is null)
        {
            return null;
        }

        ulong fingerprint = MediaSessionProxyProtocol.ParseFingerprint(reply.Thumbnail.Fingerprint);
        if (fingerprint == 0)
        {
            return null;
        }

        byte[]? bytes = reply.Thumbnail.Unchanged
            ? null
            : MediaSessionProxyProtocol.TryDecodeBase64(reply.Thumbnail.Base64);
        return new MusicThumbnailSnapshot(
            bytes,
            fingerprint,
            reply.Thumbnail.Unchanged,
            reply.Thumbnail.ContentType);
    }

    internal async Task<MusicTimelineSnapshot?> GetCurrentTimelineAsync(string? preferredSessionId = null)
    {
        MediaSessionProxyMessage? reply = await SendRequestAsync(
            new MediaSessionProxyCommand
            {
                Op = "get_timeline",
                PreferredSessionId = string.IsNullOrWhiteSpace(preferredSessionId) ? null : preferredSessionId,
            },
            DefaultRequestTimeout).ConfigureAwait(false);

        if (reply?.Ok != true || reply.Timeline is null)
        {
            return null;
        }

        MediaSessionTimelineDto dto = reply.Timeline;
        return new MusicTimelineSnapshot(
            TimeSpan.FromTicks(Math.Max(0L, dto.PositionTicks)),
            TimeSpan.FromTicks(Math.Max(0L, dto.DurationTicks)),
            MediaSessionProxyProtocol.ParseFileTimeUtc(dto.PositionUpdatedFileTimeTicks),
            double.IsFinite(dto.PlaybackRate) && dto.PlaybackRate is >= 0.05 and <= 4.0 ? dto.PlaybackRate : 1.0,
            MediaSessionProxyProtocol.ParsePlaybackState(dto.PlaybackState));
    }

    internal async Task<MusicPlaybackSnapshot?> GetCurrentPlaybackAsync(string? preferredSessionId = null)
    {
        MediaSessionProxyMessage? reply = await SendRequestAsync(
            new MediaSessionProxyCommand
            {
                Op = "get_playback",
                PreferredSessionId = string.IsNullOrWhiteSpace(preferredSessionId) ? null : preferredSessionId,
            },
            DefaultRequestTimeout).ConfigureAwait(false);

        if (reply?.Ok != true || reply.Playback is null)
        {
            return null;
        }

        MediaSessionPlaybackDto dto = reply.Playback;
        return new MusicPlaybackSnapshot(
            MediaSessionProxyProtocol.ParsePlaybackState(dto.PlaybackState),
            double.IsFinite(dto.PlaybackRate) && dto.PlaybackRate is >= 0.05 and <= 4.0 ? dto.PlaybackRate : 1.0,
            dto.CanPlay,
            dto.CanPause,
            dto.CanGoPrevious,
            dto.CanGoNext,
            dto.CanSeek,
            dto.CanChangeShuffle,
            dto.CanChangeRepeat,
            MediaSessionProxyProtocol.ParsePlaybackMode(dto.PlaybackMode));
    }

    internal async Task<bool> TrySetPreferredSessionAsync(string sessionId)
    {
        MediaSessionProxyMessage? reply = await SendRequestAsync(
            new MediaSessionProxyCommand
            {
                Op = "set_preferred",
                SessionId = sessionId,
            },
            DefaultRequestTimeout).ConfigureAwait(false);
        return reply?.Ok == true && reply.Changed;
    }

    internal async Task<bool> TryControlAsync(
        string? sessionId,
        string action,
        long? positionTicks = null,
        MusicPlaybackMode? playbackMode = null)
    {
        MediaSessionProxyMessage? reply = await SendRequestAsync(
            new MediaSessionProxyCommand
            {
                Op = "control",
                SessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
                Action = action,
                PositionTicks = positionTicks,
                PlaybackMode = playbackMode.HasValue
                    ? MediaSessionProxyProtocol.FormatPlaybackMode(playbackMode.Value)
                    : null,
            },
            DefaultRequestTimeout).ConfigureAwait(false);
        return reply?.Ok == true && reply.Changed;
    }

    private async Task<MediaSessionProxyMessage?> SendRequestAsync(
        MediaSessionProxyCommand command,
        TimeSpan timeout)
    {
        if (!await EnsureConnectedAsync().ConfigureAwait(false))
        {
            return null;
        }

        ProxyProcess? proc = GetAliveProcess();
        if (proc is null)
        {
            return null;
        }

        long id = Interlocked.Increment(ref _nextRequestId);
        command.Id = id;
        Volatile.Write(ref _lastOperation, command.Op);

        string serialized = MediaSessionProxyProtocol.SerializeCommand(command);
        try
        {
            return await proc.SendAndReceiveAsync(id, serialized, timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            App.Log($"[MediaSessionProxy] Timed out waiting {timeout.TotalMilliseconds:F0}ms for op={command.Op}; terminating hung child.");
            string stderrTail = proc.ErrorTail;
            proc.Dispose(intentional: false);
            OnProcessUnexpectedExit(proc, -1, $"rpc timeout op={command.Op}; {stderrTail}");
            return null;
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[MediaSessionProxy] Request op={command.Op} failed: {ex.Message}");
            return null;
        }
    }

    private ProxyProcess? GetAliveProcess()
    {
        lock (_stateGate)
        {
            return _process is { IsAlive: true } proc ? proc : null;
        }
    }

    private async Task<(ProxyProcess? Process, bool AlreadyReportedExit, string FailureReason)> StartProcessLockedAsync(
        CancellationToken cancellationToken)
    {
        DisposeCurrentProcessLocked(intentional: true);
        string exePath = Path.Combine(
            AppContext.BaseDirectory,
            ShellThumbnailProxy.ExecutableName);
        if (!File.Exists(exePath))
        {
            App.Log($"[MediaSessionProxy] Native proxy binary is missing: {exePath}");
            return (null, false, $"binary missing: {exePath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
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

        var nativeProcess = new Process { StartInfo = startInfo };
        try
        {
            using IDisposable? childScope =
                ProcessEnvironmentSyncService.AcquireChildLaunchContaminationScope();
            if (!nativeProcess.Start())
            {
                nativeProcess.Dispose();
                App.Log("[MediaSessionProxy] Child process failed to start.");
                return (null, false, "process start returned false");
            }
        }
        catch (Exception ex)
        {
            nativeProcess.Dispose();
            App.Log($"[MediaSessionProxy] Child process launch threw: {ex.Message}");
            return (null, false, $"launch threw: {ex.Message}");
        }

        var wrapper = new ProxyProcess(
            nativeProcess,
            onEvent: DispatchServerEvent,
            onUnexpectedExit: OnProcessUnexpectedExit);
        lock (_stateGate)
        {
            _process = wrapper;
        }

        try
        {
            bool ready = await wrapper.WaitForReadyAsync(StartupTimeout, cancellationToken).ConfigureAwait(false);
            if (!ready)
            {
                string errTail = wrapper.ErrorTail;
                bool reported = wrapper.ReportedUnexpectedExit;
                App.Log($"[MediaSessionProxy] Child process did not send ready handshake. stderr={errTail}");
                DisposeCurrentProcessLocked(intentional: true);
                return (null, reported, $"handshake failed; stderr={errTail}");
            }

            App.LogVerbose($"[MediaSessionProxy] Started out-of-process SMTC proxy pid={nativeProcess.Id}");
            return (wrapper, false, string.Empty);
        }
        catch (OperationCanceledException)
        {
            DisposeCurrentProcessLocked(intentional: true);
            throw;
        }
        catch (Exception ex)
        {
            string errTail = wrapper.ErrorTail;
            bool reported = wrapper.ReportedUnexpectedExit;
            App.Log($"[MediaSessionProxy] Handshake failed: {ex.Message} stderr={errTail}");
            DisposeCurrentProcessLocked(intentional: true);
            return (null, reported, $"handshake threw: {ex.Message}; stderr={errTail}");
        }
    }

    private void DispatchServerEvent(string eventName)
    {
        switch (eventName)
        {
            case "sessions_changed":
                _ = RefreshCachedSessionsSafeAsync();
                SessionsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "current_session_changed":
                _ = RefreshCachedSessionsSafeAsync();
                CurrentSessionChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "playback_info_changed":
                PlaybackInfoChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "media_properties_changed":
                MediaPropertiesChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "timeline_properties_changed":
                TimelinePropertiesChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private async Task RefreshCachedSessionsSafeAsync()
    {
        try
        {
            _ = await RefreshSessionOptionsAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void OnProcessUnexpectedExit(ProxyProcess exitedProcess, int exitCode, string stderrTail)
    {
        bool wasCurrent;
        bool hasSubscribers;
        lock (_stateGate)
        {
            wasCurrent = ReferenceEquals(_process, exitedProcess);
            if (wasCurrent)
            {
                _process = null;
                _cachedSessions = [];
            }

            hasSubscribers = _subscriberCount > 0;
        }

        if (!wasCurrent)
        {
            return;
        }

        if (!hasSubscribers)
        {
            return;
        }

        HandleUnexpectedExit(exitCode, stderrTail);
    }

    private void HandleUnexpectedExit(int exitCode, string stderrTail)
    {
        (MediaSessionConnectionState nextState, TimeSpan? retryDelay, int crashesInWindow) =
            _circuitBreaker.RecordUnexpectedExit(_utcNow());
        string lastOp = Volatile.Read(ref _lastOperation);
        App.Log(
            $"[MediaSessionProxy] Child exited unexpectedly exitCode=0x{unchecked((uint)exitCode):X8} " +
            $"crashesInWindow={crashesInWindow} state={nextState} lastOp={lastOp} stderr={stderrTail}");

        UpdatePublishedState(nextState);
        CurrentSessionChanged?.Invoke(this, EventArgs.Empty);

        if (retryDelay is { } delay)
        {
            ArmReconnectTimer(delay);
        }
    }

    private void UpdatePublishedState(MediaSessionConnectionState nextState)
    {
        bool changed;
        lock (_stateGate)
        {
            changed = _publishedState != nextState;
            _publishedState = nextState;
        }

        if (changed)
        {
            ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ArmReconnectTimer(TimeSpan delay)
    {
        lock (_stateGate)
        {
            if (_subscriberCount <= 0)
            {
                return;
            }

            if (_reconnectTimer is null)
            {
                _reconnectTimer = new Timer(
                    OnReconnectTimerTick,
                    null,
                    delay,
                    Timeout.InfiniteTimeSpan);
            }
            else
            {
                _reconnectTimer.Change(delay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void CancelReconnectTimerLocked()
    {
        _reconnectTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void OnReconnectTimerTick(object? state)
    {
        if (ActiveLeaseCount <= 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (await EnsureConnectedAsync().ConfigureAwait(false))
                {
                    SessionsChanged?.Invoke(this, EventArgs.Empty);
                    CurrentSessionChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch
            {
            }
        });
    }

    private void ArmIdleShutdownTimer()
    {
        lock (_stateGate)
        {
            if (_idleShutdownTimer is null)
            {
                _idleShutdownTimer = new Timer(
                    OnIdleShutdownTimerTick,
                    null,
                    IdleShutdownDelay,
                    Timeout.InfiniteTimeSpan);
            }
            else
            {
                _idleShutdownTimer.Change(IdleShutdownDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void CancelIdleShutdownTimerLocked()
    {
        _idleShutdownTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void OnIdleShutdownTimerTick(object? state)
    {
        if (ActiveLeaseCount > 0)
        {
            return;
        }

        if (!_processLifecycleGate.Wait(0))
        {
            ArmIdleShutdownTimer();
            return;
        }

        try
        {
            if (ActiveLeaseCount == 0)
            {
                DisposeCurrentProcessLocked(intentional: true);
                lock (_stateGate)
                {
                    _cachedSessions = [];
                }
            }
        }
        finally
        {
            _processLifecycleGate.Release();
        }
    }

    private void ShutdownForProcessExit()
    {
        try
        {
            lock (_stateGate)
            {
                CancelIdleShutdownTimerLocked();
                CancelReconnectTimerLocked();
            }

            DisposeCurrentProcessLocked(intentional: true);
        }
        catch
        {
        }
    }

    private void DisposeCurrentProcessLocked(bool intentional)
    {
        ProxyProcess? toDispose;
        lock (_stateGate)
        {
            toDispose = _process;
            _process = null;
        }

        toDispose?.Dispose(intentional);
    }

    private sealed class ProxyProcess
    {
        private readonly Process _process;
        private readonly Action<string> _onEvent;
        private readonly Action<ProxyProcess, int, string> _onUnexpectedExit;
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly ConcurrentDictionary<long, TaskCompletionSource<MediaSessionProxyMessage?>> _pending = new();
        private readonly TaskCompletionSource<bool> _readyTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Queue<string> _stderrLines = new();
        private readonly object _stderrGate = new();
        private int _disposed;
        private int _intentionalShutdown;
        private int _reportedUnexpectedExit;

        internal ProxyProcess(
            Process process,
            Action<string> onEvent,
            Action<ProxyProcess, int, string> onUnexpectedExit)
        {
            _process = process;
            _onEvent = onEvent;
            _onUnexpectedExit = onUnexpectedExit;
            _ = Task.Run(ReadStdoutLoopAsync);
            _ = Task.Run(ReadStderrLoopAsync);
        }

        internal bool IsAlive => Volatile.Read(ref _disposed) == 0 && !SafeHasExited();

        internal bool ReportedUnexpectedExit => Volatile.Read(ref _reportedUnexpectedExit) != 0;

        internal string ErrorTail
        {
            get
            {
                lock (_stderrGate)
                {
                    return _stderrLines.Count == 0 ? "<none>" : string.Join(" | ", _stderrLines);
                }
            }
        }

        internal async Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken ct)
        {
            try
            {
                return await _readyTcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        internal async Task<MediaSessionProxyMessage?> SendAndReceiveAsync(
            long requestId,
            string jsonLine,
            TimeSpan timeout)
        {
            if (!IsAlive)
            {
                return null;
            }

            var tcs = new TaskCompletionSource<MediaSessionProxyMessage?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[requestId] = tcs;
            try
            {
                await _writeGate.WaitAsync(timeout).ConfigureAwait(false);
                try
                {
                    if (!IsAlive)
                    {
                        return null;
                    }

                    using var writeCts = new CancellationTokenSource(timeout);
                    try
                    {
                        await _process.StandardInput.WriteLineAsync(jsonLine.AsMemory(), writeCts.Token).ConfigureAwait(false);
                        await _process.StandardInput.FlushAsync(writeCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (writeCts.IsCancellationRequested)
                    {
                        Dispose(intentional: false);
                        throw new TimeoutException("Timed out writing SMTC proxy request.");
                    }
                }
                finally
                {
                    _writeGate.Release();
                }

                return await tcs.Task.WaitAsync(timeout).ConfigureAwait(false);
            }
            finally
            {
                _pending.TryRemove(requestId, out _);
            }
        }

        internal void Dispose(bool intentional)
        {
            if (intentional)
            {
                Volatile.Write(ref _intentionalShutdown, 1);
            }

            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _readyTcs.TrySetResult(false);
            FailAllPending();

            try
            {
                if (!SafeHasExited())
                {
                    try
                    {
                        _process.StandardInput.Close();
                    }
                    catch
                    {
                    }

                    if (!_process.WaitForExit(1500))
                    {
                        try
                        {
                            _process.Kill(entireProcessTree: true);
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
            finally
            {
                try
                {
                    _process.Dispose();
                }
                catch
                {
                }
            }
        }

        private async Task ReadStdoutLoopAsync()
        {
            try
            {
                while (Volatile.Read(ref _disposed) == 0)
                {
                    string? line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    MediaSessionProxyMessage? msg = MediaSessionProxyProtocol.DeserializeMessage(line);
                    if (msg is null)
                    {
                        continue;
                    }

                    switch (msg.Type)
                    {
                        case "ready":
                            _readyTcs.TrySetResult(true);
                            break;
                        case "response":
                            if (_pending.TryRemove(msg.Id, out TaskCompletionSource<MediaSessionProxyMessage?>? tcs))
                            {
                                tcs.TrySetResult(msg);
                            }
                            break;
                        case "event":
                            if (!string.IsNullOrWhiteSpace(msg.Name))
                            {
                                _onEvent(msg.Name);
                            }
                            break;
                        case "bye":
                            Volatile.Write(ref _intentionalShutdown, 1);
                            break;
                    }
                }
            }
            catch
            {
            }
            finally
            {
                _readyTcs.TrySetResult(false);
                FailAllPending();

                bool intentional = Volatile.Read(ref _intentionalShutdown) != 0 ||
                    Volatile.Read(ref _disposed) != 0;
                if (!intentional)
                {
                    Volatile.Write(ref _reportedUnexpectedExit, 1);
                    int exitCode = TryReadExitCode();
                    string stderrTail = ErrorTail;
                    Dispose(intentional: false);
                    _onUnexpectedExit(this, exitCode, stderrTail);
                }
            }
        }

        private async Task ReadStderrLoopAsync()
        {
            try
            {
                while (true)
                {
                    string? line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    lock (_stderrGate)
                    {
                        _stderrLines.Enqueue(line.Trim());
                        while (_stderrLines.Count > 6)
                        {
                            _stderrLines.Dequeue();
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private void FailAllPending()
        {
            foreach (KeyValuePair<long, TaskCompletionSource<MediaSessionProxyMessage?>> pair in _pending)
            {
                if (_pending.TryRemove(pair.Key, out TaskCompletionSource<MediaSessionProxyMessage?>? tcs))
                {
                    tcs.TrySetResult(null);
                }
            }
        }

        private bool SafeHasExited()
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

        private int TryReadExitCode()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.WaitForExit(500);
                }

                return _process.HasExited ? _process.ExitCode : -1;
            }
            catch
            {
                return -1;
            }
        }
    }
}
