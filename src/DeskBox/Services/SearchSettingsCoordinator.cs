using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Adapts Search's settings slice and borrowed runtime capabilities. Commands
/// and runtime replacement run on the host UI thread; notifications may arrive
/// on a worker thread and are marshalled by the settings editor.
/// </summary>
public sealed class SearchSettingsCoordinator : ISearchSettings, IDisposable
{
    private readonly SettingsService _settings;
    private readonly LocalizationService _localization;
    private readonly Func<ISearchConnectionClient?> _getConnection;
    private readonly Func<ISearchConnectionClient?> _ensureConnection;
    private readonly Func<ISearchHotkeyController?> _getHotkey;
    private readonly Action<bool> _setContentSearchEnabled;
    private readonly Func<Action, bool> _tryEnqueue;
    private readonly Action<Exception> _reportError;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _requestLock = new();
    private readonly HashSet<Task> _requests = [];
    private ISearchConnectionClient? _connection;
    private Action<EverythingConnectionSnapshot>? _connectionChanged;
    private CancellationTokenSource _connectionLifetime = new();
    private bool _runtimeStopping;
    private bool _stopping;
    private Task? _stopTask;
    private bool _disposed;

    public SearchSettingsCoordinator(
        SettingsService settings,
        LocalizationService localization,
        Func<ISearchConnectionClient?> getConnection,
        Func<ISearchConnectionClient?> ensureConnection,
        Func<ISearchHotkeyController?> getHotkey,
        Action<bool> setContentSearchEnabled,
        Func<Action, bool>? tryEnqueue = null,
        Action<Exception>? reportError = null)
    {
        _settings = settings;
        _localization = localization;
        _getConnection = getConnection;
        _ensureConnection = ensureConnection;
        _getHotkey = getHotkey;
        _setContentSearchEnabled = setContentSearchEnabled;
        _tryEnqueue = tryEnqueue ?? (action => { action(); return true; });
        _reportError = reportError ?? (_ => { });
        _settings.SettingsChanged += OnSettingsChanged;
    }

    public event Action? StateChanged;

    private bool FeatureEnabled => !_disposed && !_stopping && !_runtimeStopping;

    public EverythingConnectionSnapshot Connection => FeatureEnabled
        ? _connection?.CurrentSnapshot ?? EverythingConnectionSnapshot.Unknown
        : EverythingConnectionSnapshot.Unknown;

    public SearchSettingsSnapshot Read()
    {
        AppSettings appSettings = _settings.Settings;
        SearchSettingsSlice search = appSettings.Search;
        GlobalHotkeyActivation activation = GlobalHotkeyService.NormalizeActivation(
            search.SearchHotkeyActivationKind,
            search.SearchHotkeyModifiers, search.SearchHotkeyKey);
        ISearchHotkeyController? hotkey = FeatureEnabled ? _getHotkey() : null;
        return new(
            FeatureEnabled,
            ReadPreferences(),
            new(
                hotkey is not null, search.SearchHotkeyEnabled, hotkey?.IsRegistered == true,
                activation.Gesture, GlobalHotkeyService.FormatActivation(activation, _localization), activation.Kind),
            TodoFeatureEnabled: FeatureWidgetSettings.IsEnabled(appSettings, WidgetKind.Todo),
            QuickCaptureFeatureEnabled: FeatureWidgetSettings.IsEnabled(appSettings, WidgetKind.QuickCapture));
    }

    private SearchPreferences ReadPreferences()
    {
        SearchSettingsSlice search = _settings.Settings.Search;
        return new(search.SearchEverythingEnabled, search.SearchEverythingAdvancedSyntaxEnabled,
            search.SearchIncludeDeskBoxContent, search.SearchShowRecommendations,
            search.SearchDefaultTab, search.SearchAppIconAnimation,
            search.SearchCategoryBarMode, search.SearchShowSortHeader,
            search.SearchDefaultSortColumn, search.SearchIncludeClipboard,
            search.SearchIncludeCalculator, search.SearchShowDetailsPane,
            search.SearchIncludeWebFallback, search.SearchDefaultWebEngine,
            search.SearchCustomWebUrl,
            search.SearchQuickCreateTodoEnabled, search.SearchQuickCreateNoteEnabled,
            search.SearchQuickCreateDisplayMode, search.SearchQuickCreateDefaultTarget,
            search.SearchQuickCreateShowHud);
    }

    private async Task DrainRequestsAsync(bool forShutdown = false)
    {
        Task[] requests;
        lock (_requestLock) requests = _requests.ToArray();
        if (requests.Length == 0) return;
        try
        {
            Task drain = Task.WhenAll(requests);
            if (forShutdown) await drain;
            else await drain.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException ex) { _reportError(ex); }
        catch (OperationCanceledException) { }
        catch (Exception) { } // request errors belong to their original callers
    }

    public string FormatHotkey(GlobalHotkeyGesture gesture) =>
        GlobalHotkeyService.FormatGesture(gesture, _localization);

    public string FormatHotkeyActivation(GlobalHotkeyActivation activation) =>
        GlobalHotkeyService.FormatActivation(activation, _localization);

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
        if (change.CategoryBarMode is { } barMode)
        {
            barMode = barMode.Trim().ToLowerInvariant();
            search.SearchCategoryBarMode = barMode is "auto" or "always" or "hidden" ? barMode : "auto";
        }
        if (change.ShowSortHeader is { } showSort) search.SearchShowSortHeader = showSort;
        if (change.DefaultSortColumn is { } sortCol)
        {
            sortCol = sortCol.Trim().ToLowerInvariant();
            search.SearchDefaultSortColumn = sortCol is "relevance" or "date" or "name" or "size" ? sortCol : "relevance";
        }
        if (change.IncludeClipboard is { } clip) search.SearchIncludeClipboard = clip;
        if (change.IncludeCalculator is { } calc) search.SearchIncludeCalculator = calc;
        if (change.ShowDetailsPane is { } details) search.SearchShowDetailsPane = details;
        if (change.IncludeWebFallback is { } web) search.SearchIncludeWebFallback = web;
        if (change.DefaultWebEngine is { } webEngine)
        {
            search.SearchDefaultWebEngine = SearchWebAndUrlEvaluator.NormalizeEngineId(webEngine);
        }
        if (change.CustomWebUrl is { } customUrl)
        {
            string trimmedUrl = customUrl.Trim();
            search.SearchCustomWebUrl = trimmedUrl.Length > 1024 ? trimmedUrl[..1024] : trimmedUrl;
        }
        if (change.QuickCreateTodoEnabled is { } qcTodo) search.SearchQuickCreateTodoEnabled = qcTodo;
        if (change.QuickCreateNoteEnabled is { } qcNote) search.SearchQuickCreateNoteEnabled = qcNote;
        if (change.QuickCreateDisplayMode is { } qcMode)
        {
            qcMode = qcMode.Trim().ToLowerInvariant();
            search.SearchQuickCreateDisplayMode = qcMode is "always" or "fallback" or "prefix" ? qcMode : "always";
        }
        if (change.QuickCreateDefaultTarget is { } qcTarget)
        {
            qcTarget = qcTarget.Trim().ToLowerInvariant();
            search.SearchQuickCreateDefaultTarget = qcTarget is "todo" or "note" ? qcTarget : "todo";
        }
        if (change.QuickCreateShowHud is { } qcHud) search.SearchQuickCreateShowHud = qcHud;
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
        // Deep review R13-2 (D11 audit): do NOT read IsRegistered here —
        // the enable command is fire-and-forget and still queued on the
        // main UI thread, so a just-enabled hotkey read as unregistered
        // and reported a failure the registration push then contradicted.
        // The hotkey.registration-changed push converges the card; the
        // command's refusal path already reports through its own failure
        // handling.
        return new(true);
    }

    public SearchHotkeyUpdateResult ApplyHotkey(GlobalHotkeyGesture gesture)
    {
        ISearchHotkeyController? hotkey = GetHotkeyForUserAction();
        if (hotkey is null) return UnavailableHotkey();
        bool applied = hotkey.TryApplyGesture(gesture, out string? error);
        StateChanged?.Invoke();
        return applied ? new(true) : FailedHotkey(error);
    }

    public bool IsGestureOwnedByMainHotkey(GlobalHotkeyGesture gesture) =>
        _getHotkey()?.IsGestureOwnedByMainHotkey(gesture) == true;

    public SearchHotkeyUpdateResult ApplyHotkeyActivation(GlobalHotkeyActivation activation)
    {
        ISearchHotkeyController? hotkey = GetHotkeyForUserAction();
        if (hotkey is null) return UnavailableHotkey();
        bool applied = hotkey.TryApplyActivation(activation, out string? error);
        StateChanged?.Invoke();
        return applied ? new(true) : FailedHotkey(error);
    }

    public bool IsActivationOwnedByMainHotkey(GlobalHotkeyActivation activation) =>
        _getHotkey()?.IsActivationOwnedByMainHotkey(activation) == true;

    public bool IsActivationOwnedByClipboardHotkey(GlobalHotkeyActivation activation)
    {
        AppSettings settings = _settings.Settings;
        if (!FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Clipboard) ||
            !settings.Clipboard.ClipboardHotkeyEnabled)
        {
            return false;
        }

        GlobalHotkeyActivation clipboardActivation = GlobalHotkeyService.NormalizeActivation(
            settings.Clipboard.ClipboardHotkeyActivationKind,
            settings.Clipboard.ClipboardHotkeyModifiers,
            settings.Clipboard.ClipboardHotkeyKey);
        return GlobalHotkeyService.DoActivationsConflict(activation, clipboardActivation);
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
            StateChanged?.Invoke();
        });
    }

    public Task StopAsync() => _stopTask ??= StopCoreAsync();

    private async Task StopCoreAsync()
    {
        _stopping = true;
        _settings.SettingsChanged -= OnSettingsChanged;
        _lifetime.Cancel();
        ObserveConnection(null);
        // App owns the shutdown deadline. Reporting completion here means no
        // request can still use a connection after search services are released.
        try { await DrainRequestsAsync(forShutdown: true); }
        finally
        {
            Dispose();
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
