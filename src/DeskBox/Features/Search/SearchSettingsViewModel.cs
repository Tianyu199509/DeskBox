using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Features.Search;

public enum SearchSettingsFailure { None, Connection, InvalidExecutable, Launch }

/// <summary>Owns one visible settings visit and its probes, not the global search session.</summary>
public sealed class SearchSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISearchSettings _settings;
    private readonly Func<Action, bool> _tryEnqueue;
    private readonly Action<Exception> _reportError;
    private CancellationTokenSource? _visit;
    private CancellationTokenSource? _operation;
    private Action? _stateChanged;
    private int _visitGeneration;
    private int _operationGeneration;
    private bool _disposed;
    private bool _isBusy;
    private SearchSettingsSnapshot _state;
    private EverythingConnectionSnapshot _connection = EverythingConnectionSnapshot.Unknown;
    private SearchSettingsFailure _failure;
    private string? _hotkeyError;

    public SearchSettingsViewModel(ISearchSettings settings, Func<Action, bool> tryEnqueue, Action<Exception> reportError)
    {
        _settings = settings;
        _tryEnqueue = tryEnqueue;
        _reportError = reportError;
        _state = settings.Read();
    }

    public static GlobalHotkeyGesture AltSpaceGesture { get; } = new(HotkeyModifierKeys.Alt, 0x20);
    public static GlobalHotkeyGesture DefaultGesture { get; } = new(HotkeyModifierKeys.Alt, 0x44);
    public string ReservedHotkeyDisplayText => _settings.FormatHotkey(AltSpaceGesture);
    public bool IsActive => !_disposed && _visit is not null;
    public CancellationToken VisitToken => _visit?.Token ?? new CancellationToken(canceled: true);
    public Task PendingOperation { get; private set; } = Task.CompletedTask;
    public SearchSettingsSnapshot State { get => _state; private set => SetProperty(ref _state, value); }
    public EverythingConnectionSnapshot Connection { get => _connection; private set => SetProperty(ref _connection, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public SearchSettingsFailure Failure { get => _failure; private set => SetProperty(ref _failure, value); }
    public string? HotkeyError { get => _hotkeyError; private set => SetProperty(ref _hotkeyError, value); }

    public void Activate()
    {
        if (_disposed || IsActive) return;
        _visit = new();
        int generation = ++_visitGeneration;
        _stateChanged = () => _tryEnqueue(() =>
        {
            if (!IsActive || generation != _visitGeneration) return;
            bool wasEnabled = State.FeatureEnabled;
            RefreshState();
            if (!State.FeatureEnabled) CancelOperation();
            else if (!wasEnabled) _ = RefreshAsync();
        });
        _settings.StateChanged += _stateChanged;
        HotkeyError = null;
        RefreshState();
        _ = RefreshAsync();
    }

    public void Deactivate()
    {
        ++_visitGeneration;
        if (_stateChanged is not null) _settings.StateChanged -= _stateChanged;
        _stateChanged = null;
        _visit?.Cancel();
        _visit?.Dispose();
        _visit = null;
        CancelOperation();
    }

    public void RefreshState()
    {
        if (_disposed) return;
        State = _settings.Read();
        Connection = _settings.Connection;
    }

    public void UpdatePreferences(SearchPreferenceChange change)
    {
        if (!IsActive) return;
        try
        {
            _settings.UpdatePreferences(change);
            RefreshState();
            if (change.EverythingEnabled.HasValue) _ = RefreshAsync();
        }
        catch (Exception ex) { _reportError(ex); }
    }

    public bool RequiresReservedHotkeyConfirmation(GlobalHotkeyGesture gesture) =>
        gesture == AltSpaceGesture && State.Hotkey.Gesture != gesture;

    public void SetHotkeyEnabled(bool enabled) => ApplyHotkeyChange(() => _settings.SetHotkeyEnabled(enabled));
    public void ApplyHotkey(GlobalHotkeyGesture gesture) => ApplyHotkeyChange(() => _settings.ApplyHotkey(gesture));
    public void ResetHotkey() => ApplyHotkey(DefaultGesture);

    private void ApplyHotkeyChange(Func<SearchHotkeyUpdateResult> action)
    {
        if (!IsActive) return;
        try
        {
            SearchHotkeyUpdateResult result = action();
            HotkeyError = result.Succeeded ? null : result.Error;
            RefreshState();
        }
        catch (Exception ex) { _reportError(ex); }
    }

    public Task RefreshAsync() => BeginOperation(async token =>
    {
        await _settings.RefreshConnectionAsync(token);
        return SearchSettingsFailure.None;
    });

    public Task DetectAutomaticallyAsync() => BeginOperation(async token =>
    {
        await _settings.DetectAutomaticallyAsync(token);
        return SearchSettingsFailure.None;
    });

    public Task SelectExecutableAsync(string path) => BeginOperation(async token =>
        await _settings.SelectExecutableAsync(path, token) ? SearchSettingsFailure.None : SearchSettingsFailure.InvalidExecutable);

    public Task LaunchEverythingAsync() => BeginOperation(async token =>
        await _settings.LaunchEverythingAsync(token) ? SearchSettingsFailure.None : SearchSettingsFailure.Launch);

    private Task BeginOperation(Func<CancellationToken, Task<SearchSettingsFailure>> action)
    {
        if (!IsActive) return Task.CompletedTask;
        CancelOperation();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(VisitToken);
        _operation = cancellation;
        int visit = _visitGeneration;
        int operation = ++_operationGeneration;
        Failure = SearchSettingsFailure.None;
        IsBusy = true;
        return PendingOperation = RunOperationAsync(action, cancellation, visit, operation);
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task<SearchSettingsFailure>> action,
        CancellationTokenSource cancellation, int visit, int operation)
    {
        CancellationToken token = cancellation.Token;
        bool IsCurrent() => IsActive && visit == _visitGeneration && operation == _operationGeneration && !token.IsCancellationRequested;
        try
        {
            SearchSettingsFailure failure = await action(token);
            if (!IsCurrent()) return;
            RefreshState();
            Failure = State.FeatureEnabled ? failure : SearchSettingsFailure.None;
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent()) RefreshState();
        }
        catch (Exception ex)
        {
            if (IsCurrent())
            {
                Failure = SearchSettingsFailure.Connection;
                _reportError(ex);
            }
        }
        finally
        {
            if (IsCurrent()) IsBusy = false;
            if (ReferenceEquals(_operation, cancellation)) _operation = null;
            cancellation.Dispose();
        }
    }

    private void CancelOperation()
    {
        ++_operationGeneration;
        _operation?.Cancel();
        _operation = null;
        IsBusy = false;
    }

    public void ReportViewError(Exception exception) => _reportError(exception);

    public void Dispose()
    {
        if (_disposed) return;
        Deactivate();
        _disposed = true;
    }
}
