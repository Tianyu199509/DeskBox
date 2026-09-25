using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Adapts Search's settings slice and borrowed runtime capabilities. Commands
/// and runtime replacement run on the host UI thread; notifications may arrive
/// on a worker thread and are marshalled by the settings editor.
/// </summary>
public sealed class SearchSettingsCoordinator : ISearchSettings, ISearchFeatureSettings, IDisposable
{
    private readonly SettingsService _settings;
    private readonly LocalizationService _localization;
    private readonly Func<ISearchConnectionClient?> _getConnection;
    private readonly Func<ISearchConnectionClient?> _ensureConnection;
    private readonly Func<ISearchHotkeyController?> _getHotkey;
    private readonly Action<bool> _setContentSearchEnabled;
    private readonly Func<bool, bool, Task> _applyWidget;
    private readonly Action<bool> _setRuntimeEnabled;
    private readonly Func<Action, bool> _tryEnqueue;
    private readonly Action<Exception> _reportError;
    private readonly SemaphoreSlim _enableGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _requestLock = new();
    private readonly HashSet<Task> _requests = [];
    private ISearchConnectionClient? _connection;
    private Action<EverythingConnectionSnapshot>? _connectionChanged;
    private CancellationTokenSource _connectionLifetime = new();
    private bool _runtimeStopping;
    private bool _stopping;
    private bool _desiredEnabled;
    private bool _applyingWindow;
    private int _enableGeneration;
    private Task? _stopTask;
    private bool _disposed;

    public SearchSettingsCoordinator(
        SettingsService settings,
        LocalizationService localization,
        Func<ISearchConnectionClient?> getConnection,
        Func<ISearchConnectionClient?> ensureConnection,
        Func<ISearchHotkeyController?> getHotkey,
        Action<bool> setContentSearchEnabled,
        Func<bool, bool, Task>? applyWidget = null,
        Action<bool>? setRuntimeEnabled = null,
        Func<Action, bool>? tryEnqueue = null,
        Action<Exception>? reportError = null)
    {
        _settings = settings;
        _localization = localization;
        _getConnection = getConnection;
        _ensureConnection = ensureConnection;
        _getHotkey = getHotkey;
        _setContentSearchEnabled = setContentSearchEnabled;
        _applyWidget = applyWidget ?? ((_, _) => _settings.SaveAsync());
        _setRuntimeEnabled = setRuntimeEnabled ?? (_ => { });
        _tryEnqueue = tryEnqueue ?? (action => { action(); return true; });
        _reportError = reportError ?? (_ => { });
        _desiredEnabled = FeatureWidgetSettings.IsEnabled(_settings.Settings, WidgetKind.Search);
        _settings.SettingsChanged += OnSettingsChanged;
    }

    public event Action? StateChanged;
    public event Action? FeatureChanged;
    public bool Enabled => FeatureWidgetSettings.IsEnabled(_settings.Settings, WidgetKind.Search);

    private bool FeatureEnabled => !_disposed && !_stopping && !_runtimeStopping &&
        FeatureWidgetSettings.IsEnabled(_settings.Settings, WidgetKind.Search);

    public EverythingConnectionSnapshot Connection => FeatureEnabled
        ? _connection?.CurrentSnapshot ?? EverythingConnectionSnapshot.Unknown
        : EverythingConnectionSnapshot.Unknown;

    public SearchSettingsSnapshot Read()
    {
        SearchSettingsSlice search = _settings.Settings.Search;
        GlobalHotkeyGesture gesture = GlobalHotkeyService.NormalizeGesture(
            search.SearchHotkeyModifiers, search.SearchHotkeyKey);
        ISearchHotkeyController? hotkey = FeatureEnabled ? _getHotkey() : null;
        return new(FeatureEnabled, ReadPreferences(), new(
            hotkey is not null, search.SearchHotkeyEnabled, hotkey?.IsRegistered == true,
            gesture, GlobalHotkeyService.FormatGesture(gesture, _localization)));
    }

    private SearchPreferences ReadPreferences()
    {
        SearchSettingsSlice search = _settings.Settings.Search;
        return new(search.SearchEverythingEnabled, search.SearchEverythingAdvancedSyntaxEnabled,
            search.SearchIncludeDeskBoxContent, search.SearchShowRecommendations,
            search.SearchDefaultTab, search.SearchAppIconAnimation);
    }

    public async Task SetEnabledAsync(bool enabled, bool reveal = true,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_stopping || _disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        int generation = ++_enableGeneration;
        WriteEnabled(enabled);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token);
        await _enableGate.WaitAsync(linked.Token);
        try
        {
            if (generation != _enableGeneration || _stopping) return;
            if (enabled) _setRuntimeEnabled(true);
            _applyingWindow = true;
            try { await _applyWidget(enabled, reveal); }
            finally { _applyingWindow = false; }
            if (generation != _enableGeneration || _stopping) return;
            if (!enabled)
            {
                await DrainRequestsAsync();
                _setRuntimeEnabled(false);
            }
            await _settings.SaveAsync();
        }
        finally { _enableGate.Release(); }
    }

    /// <summary>Used by direct widget creation and reset after their window work.</summary>
    public async Task CommitEnabledStateAsync(bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (_stopping || _disposed) return;
        if (_applyingWindow)
        {
            if (enabled == _desiredEnabled) WriteEnabled(enabled);
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        int generation = ++_enableGeneration;
        WriteEnabled(enabled);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token);
        await _enableGate.WaitAsync(linked.Token);
        try
        {
            if (generation != _enableGeneration || _stopping) return;
            if (enabled) _setRuntimeEnabled(true);
            else
            {
                await DrainRequestsAsync();
                _setRuntimeEnabled(false);
            }
            await _settings.SaveAsync();
        }
        finally { _enableGate.Release(); }
    }

    private void WriteEnabled(bool enabled)
    {
        bool changed = Enabled != enabled || _desiredEnabled != enabled;
        FeatureWidgetSettings.SetEnabled(_settings.Settings, WidgetKind.Search, enabled);
        _desiredEnabled = enabled;
        _runtimeStopping = !enabled;
        if (!enabled) ObserveConnection(null);
        if (changed)
        {
            _settings.SaveDebounced();
            FeatureChanged?.Invoke();
        }
        StateChanged?.Invoke();
    }

    private async Task DrainRequestsAsync()
    {
        Task[] requests;
        lock (_requestLock) requests = _requests.ToArray();
        if (requests.Length == 0) return;
        try { await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException ex) { _reportError(ex); }
        catch (OperationCanceledException) { }
        catch (Exception) { } // request errors belong to their original callers
    }

    public string FormatHotkey(GlobalHotkeyGesture gesture) =>
        GlobalHotkeyService.FormatGesture(gesture, _localization);

    public void UpdatePreferences(SearchPreferenceChange change)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SearchPreferences before = ReadPreferences();
        SearchSettingsSlice search = _settings.Settings.Search;
        if (change.EverythingEnabled is { } consent) search.SearchEverythingEnabled = consent;
        if (change.AdvancedSyntax is { } syntax) search.SearchEverythingAdvancedSyntaxEnabled = syntax;
        if (change.IncludeDeskBoxContent is { } content) search.SearchIncludeDeskBoxContent = content;
        if (change.ShowRecommendations is { } recommendations) search.SearchShowRecommendations = recommendations;
        if (change.DefaultTab is { } tab)
        {
            tab = tab.Trim().ToLowerInvariant();
            search.SearchDefaultTab = tab is "all" or "app" or "file" or "deskbox" ? tab : "all";
        }
        if (change.IconAnimation is { } animation) search.SearchAppIconAnimation = Math.Clamp(animation, 0, 3);
        SearchPreferences after = ReadPreferences();
        if (before == after) return;
        _settings.SaveDebounced();
        if (before.IncludeDeskBoxContent != after.IncludeDeskBoxContent)
            _setContentSearchEnabled(after.IncludeDeskBoxContent);
    }

    public SearchHotkeyUpdateResult SetHotkeyEnabled(bool enabled)
    {
        ISearchHotkeyController? hotkey = GetHotkeyForUserAction();
        if (hotkey is null) return UnavailableHotkey();
        hotkey.SetEnabled(enabled);
        StateChanged?.Invoke();
        return enabled && !hotkey.IsRegistered ? FailedHotkey() : new(true);
    }

    public SearchHotkeyUpdateResult ApplyHotkey(GlobalHotkeyGesture gesture)
    {
        ISearchHotkeyController? hotkey = GetHotkeyForUserAction();
        if (hotkey is null) return UnavailableHotkey();
        bool applied = hotkey.TryApplyGesture(gesture, out string? error);
        StateChanged?.Invoke();
        return applied ? new(true) : FailedHotkey(error);
    }

    private SearchHotkeyUpdateResult UnavailableHotkey() =>
        new(false, _localization.T("Settings.Search.Hotkey.Status.Disabled"));

    private SearchHotkeyUpdateResult FailedHotkey(string? error = null) =>
        new(false, error ?? _localization.T("Settings.Search.Hotkey.Status.Failed"));

    private ISearchHotkeyController? GetHotkeyForUserAction()
    {
        if (!FeatureEnabled) return null;
        if (_getHotkey() is null) EnsureConnection();
        return _getHotkey();
    }

    public Task<EverythingConnectionSnapshot> RefreshConnectionAsync(CancellationToken cancellationToken) =>
        RunConnectionAsync((connection, token) => connection.RefreshConnectionAsync(
            _settings.Settings.Search.SearchEverythingEnabled, token),
            EverythingConnectionSnapshot.Unknown, cancellationToken);

    public Task<EverythingConnectionSnapshot> DetectAutomaticallyAsync(CancellationToken cancellationToken) =>
        RunConnectionAsync(async (connection, token) =>
        {
            await connection.UseAutomaticDetectionAsync(token).ConfigureAwait(false);
            return connection.CurrentSnapshot;
        }, EverythingConnectionSnapshot.Unknown, cancellationToken);

    public Task<bool> SelectExecutableAsync(string path, CancellationToken cancellationToken) =>
        RunConnectionAsync((connection, token) => connection.SetExecutablePathAsync(path, token), false, cancellationToken);

    public Task<bool> LaunchEverythingAsync(CancellationToken cancellationToken) =>
        RunConnectionAsync((connection, token) => connection.LaunchEverythingAsync(token), false, cancellationToken);

    private Task<T> RunConnectionAsync<T>(
        Func<ISearchConnectionClient, CancellationToken, Task<T>> action,
        T unavailable, CancellationToken cancellationToken)
    {
        Task<T> request = RunConnectionCoreAsync(action, unavailable, cancellationToken);
        lock (_requestLock) _requests.Add(request);
        _ = request.ContinueWith(completed =>
        {
            lock (_requestLock) _requests.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return request;
    }

    private async Task<T> RunConnectionCoreAsync<T>(
        Func<ISearchConnectionClient, CancellationToken, Task<T>> action,
        T unavailable, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ISearchConnectionClient? connection = EnsureConnection();
        if (connection is null) return unavailable;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _connectionLifetime.Token);
        T result = await action(connection, request.Token).ConfigureAwait(false);
        // Also reject a backend that completes after ignoring cancellation.
        request.Token.ThrowIfCancellationRequested();
        return result;
    }

    private ISearchConnectionClient? EnsureConnection()
    {
        if (!FeatureEnabled) return null;
        ISearchConnectionClient? connection = _getConnection() ?? _ensureConnection();
        ObserveConnection(connection);
        return connection;
    }

    internal void OnRuntimeStopping()
    {
        if (_disposed) return;
        _runtimeStopping = true;
        ObserveConnection(null);
        StateChanged?.Invoke();
    }

    internal void OnRuntimeChanged()
    {
        if (_disposed) return;
        _runtimeStopping = false;
        ObserveConnection(FeatureEnabled ? _getConnection() : null);
        StateChanged?.Invoke();
    }

    private void ObserveConnection(ISearchConnectionClient? connection)
    {
        if (ReferenceEquals(_connection, connection)) return;
        if (_connection is not null && _connectionChanged is not null)
            _connection.ConnectionChanged -= _connectionChanged;
        _connectionLifetime.Cancel();
        _connectionLifetime.Dispose();
        _connectionLifetime = new();
        _connection = connection;
        _connectionChanged = null;
        if (connection is null) return;
        _connectionChanged = _ =>
        {
            if (!_disposed && ReferenceEquals(_connection, connection)) StateChanged?.Invoke();
        };
        connection.ConnectionChanged += _connectionChanged;
    }

    private void OnSettingsChanged()
    {
        if (_disposed || _stopping) return;
        _tryEnqueue(() =>
        {
            if (_disposed || _stopping) return;
            bool enabled = Enabled;
            if (enabled == _desiredEnabled)
            {
                StateChanged?.Invoke();
                return;
            }
            _ = ApplyExternalChangeAsync(enabled);
        });
    }

    private async Task ApplyExternalChangeAsync(bool enabled)
    {
        try { await SetEnabledAsync(enabled, reveal: enabled); }
        catch (OperationCanceledException) when (_stopping) { }
        catch (Exception ex) { _reportError(ex); }
    }

    public Task StopAsync() => _stopTask ??= StopCoreAsync();

    private async Task StopCoreAsync()
    {
        _stopping = true;
        ++_enableGeneration;
        _settings.SettingsChanged -= OnSettingsChanged;
        _lifetime.Cancel();
        ObserveConnection(null);
        await _enableGate.WaitAsync();
        try { await DrainRequestsAsync(); }
        finally
        {
            _enableGate.Release();
            Dispose();
            _enableGate.Dispose();
            _lifetime.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping = true;
        _settings.SettingsChanged -= OnSettingsChanged;
        _lifetime.Cancel();
        ObserveConnection(null);
        _connectionLifetime.Cancel();
        _connectionLifetime.Dispose();
    }
}
