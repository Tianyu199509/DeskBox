using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Contracts;

namespace DeskBox.Features.Todo;

/// <summary>
/// Todo's enablement, reminder, display, and input editor. The legacy settings shell
/// forwards its existing binding names here; this editor needs neither App nor WinUI.
/// </summary>
public sealed class TodoSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ITodoSettings _settings;
    private readonly Action<Exception> _reportError;
    private readonly CancellationTokenSource _lifetime = new();
    private TodoReminderSettings _snapshot;
    private TodoLayoutSettings _layoutSnapshot;
    private TodoTabSettings _tabSnapshot;
    private TodoContentDisplaySettings _contentDisplaySnapshot;
    private TodoInputSettings _inputSnapshot;
    private TodoDisplayOptions _displaySnapshot;
    private int _enableGeneration;
    private bool _enablePending;
    private bool _disposed;

    public TodoSettingsViewModel(ITodoSettings settings, Action<Exception> reportError)
    {
        _settings = settings;
        _reportError = reportError;
        _snapshot = settings.Read();
        _layoutSnapshot = settings.ReadLayout();
        _tabSnapshot = settings.ReadTabs();
        _contentDisplaySnapshot = settings.ReadContentDisplay();
        _inputSnapshot = settings.ReadInput();
        _displaySnapshot = settings.ReadDisplayOptions();
    }

    public Task PendingChange { get; private set; } = Task.CompletedTask;

    public bool Enabled
    {
        get => _snapshot.Enabled;
        set
        {
            if (_disposed || value == Enabled)
            {
                return;
            }
            SetSnapshot(_snapshot with { Enabled = value });
            _enablePending = true;
            PendingChange = ApplyEnabledAsync(value, ++_enableGeneration);
        }
    }

    public bool RemindersEnabled
    {
        get => _snapshot.RemindersEnabled;
        set
        {
            if (_disposed || value == RemindersEnabled)
            {
                return;
            }
            try
            {
                _settings.SetRemindersEnabled(value);
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
            Refresh();
        }
    }

    public int DefaultOffsetMinutes
    {
        get => _snapshot.DefaultOffsetMinutes;
        set
        {
            if (_disposed)
            {
                return;
            }
            try
            {
                _settings.SetDefaultReminderOffset(value);
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
            Refresh();
        }
    }

    public void ResetReminderPreferences(bool scheduleSave = true)
    {
        if (_disposed) return;
        try
        {
            _settings.ResetReminderPreferences(scheduleSave);
        }
        catch (Exception ex)
        {
            _reportError(ex);
        }
        Refresh();
    }

    public string LayoutMode
    {
        get => _layoutSnapshot.LayoutMode;
        set
        {
            if (_disposed || value == LayoutMode) return;
            try
            {
                _settings.SetLayoutMode(value);
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
            Refresh();
        }
    }

    public bool AutoSelectFirstInWideLayout
    {
        get => _layoutSnapshot.AutoSelectFirstInWideLayout;
        set
        {
            if (_disposed || value == AutoSelectFirstInWideLayout) return;
            try
            {
                _settings.SetAutoSelectFirstInWideLayout(value);
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
            Refresh();
        }
    }

    public void SetLegacyWideDetailPane(bool enabled)
    {
        if (_disposed) return;
        try
        {
            _settings.SetLegacyWideDetailPane(enabled);
        }
        catch (Exception ex)
        {
            _reportError(ex);
        }
        Refresh();
    }

    public void ResetLayoutPreferences(bool scheduleSave = true)
    {
        if (_disposed) return;
        try
        {
            _settings.ResetLayoutPreferences(scheduleSave);
        }
        catch (Exception ex)
        {
            _reportError(ex);
        }
        Refresh();
    }

    public TodoTabSettings Tabs => _tabSnapshot;

    public string DefaultFilter
    {
        get => _tabSnapshot.DefaultFilter;
        set
        {
            if (_disposed || value == DefaultFilter) return;
            try
            {
                _settings.SetDefaultFilter(value);
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
            Refresh();
        }
    }

    public bool ShowTabBar
    {
        get => _tabSnapshot.ShowTabBar;
        set
        {
            if (_disposed || value == ShowTabBar) return;
            try
            {
                _settings.SetTabBarVisible(value);
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
            Refresh();
        }
    }

    public void SetTabVisible(string? filter, bool visible)
    {
        if (_disposed) return;
        try
        {
            _settings.SetTabVisible(filter, visible);
        }
        catch (Exception ex)
        {
            _reportError(ex);
        }
        Refresh();
    }

    public void ResetTabPreferences(bool scheduleSave = true)
    {
        if (_disposed) return;
        try
        {
            _settings.ResetTabPreferences(scheduleSave);
        }
        catch (Exception ex)
        {
            _reportError(ex);
        }
        Refresh();
    }

    public int PreviewLineCount
    {
        get => _contentDisplaySnapshot.PreviewLineCount;
        set
        {
            if (_disposed || value == PreviewLineCount) return;
            try
            {
                _settings.SetPreviewLineCount(value);
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
            Refresh();
        }
    }

    public double ListTextSize => _contentDisplaySnapshot.ListTextSize;

    public double ContentTextSize => _contentDisplaySnapshot.ContentTextSize;

    public bool TrySetListTextSize(double size, bool scheduleSave = true)
    {
        if (_disposed) return false;
        try
        {
            _settings.SetListTextSize(size, scheduleSave);
        }
        catch (Exception ex)
        {
            _reportError(ex);
            Refresh();
            return false;
        }
        Refresh();
        return true;
    }

    public bool TrySetContentTextSize(double size, bool scheduleSave = true)
    {
        if (_disposed) return false;
        try
        {
            _settings.SetContentTextSize(size, scheduleSave);
        }
        catch (Exception ex)
        {
            _reportError(ex);
            Refresh();
            return false;
        }
        Refresh();
        return true;
    }

    public void ResetPreviewLineCount(bool scheduleSave = true)
    {
        if (_disposed) return;
        try
        {
            _settings.ResetPreviewLineCount(scheduleSave);
        }
        catch (Exception ex)
        {
            _reportError(ex);
        }
        Refresh();
    }

    public string NewTaskPosition
    {
        get => _inputSnapshot.NewTaskPosition;
        set
        {
            if (_disposed || value == NewTaskPosition) return;
            try
            {
                _settings.SetNewTaskPosition(value);
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
            Refresh();
        }
    }

    public string EditorEnterBehavior
    {
        get => _inputSnapshot.EditorEnterBehavior;
        set
        {
            if (_disposed || value == EditorEnterBehavior) return;
            try
            {
                _settings.SetEditorEnterBehavior(value);
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
            Refresh();
        }
    }

    public void ResetInputPreferences(bool scheduleSave = true)
    {
        if (_disposed) return;
        try
        {
            _settings.ResetInputPreferences(scheduleSave);
        }
        catch (Exception ex)
        {
            _reportError(ex);
        }
        Refresh();
    }

    public bool ShowCompletedTasks
    {
        get => _displaySnapshot.ShowCompletedTasks;
        set
        {
            if (_disposed || value == ShowCompletedTasks) return;
            try
            {
                _settings.SetShowCompletedTasks(value);
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
            Refresh();
        }
    }

    public bool ShowFooterStats
    {
        get => _displaySnapshot.ShowFooterStats;
        set
        {
            if (_disposed || value == ShowFooterStats) return;
            try
            {
                _settings.SetShowFooterStats(value);
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
            Refresh();
        }
    }

    public bool ShowClearCompletedButton
    {
        get => _displaySnapshot.ShowClearCompletedButton;
        set
        {
            if (_disposed || value == ShowClearCompletedButton) return;
            try
            {
                _settings.SetShowClearCompletedButton(value);
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
            Refresh();
        }
    }

    public string TabStyle
    {
        get => _displaySnapshot.TabStyle;
        set
        {
            if (_disposed || value == TabStyle) return;
            try
            {
                _settings.SetTabStyle(value);
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
            Refresh();
        }
    }

    public void ResetDisplayOptions(bool scheduleSave = true)
    {
        if (_disposed) return;
        try
        {
            _settings.ResetDisplayOptions(scheduleSave);
        }
        catch (Exception ex)
        {
            _reportError(ex);
        }
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }
        TodoReminderSettings snapshot = _settings.Read();
        SetSnapshot(_enablePending ? snapshot with { Enabled = Enabled } : snapshot);
        SetLayoutSnapshot(_settings.ReadLayout());
        SetTabSnapshot(_settings.ReadTabs());
        SetContentDisplaySnapshot(_settings.ReadContentDisplay());
        SetInputSnapshot(_settings.ReadInput());
        SetDisplaySnapshot(_settings.ReadDisplayOptions());
    }

    private async Task ApplyEnabledAsync(bool enabled, int generation)
    {
        try
        {
            await _settings.SetEnabledAsync(enabled, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            _reportError(ex);
        }
        finally
        {
            if (generation == _enableGeneration && !_disposed)
            {
                _enablePending = false;
                Refresh();
            }
        }
    }

    private void SetSnapshot(TodoReminderSettings snapshot)
    {
        TodoReminderSettings previous = _snapshot;
        _snapshot = snapshot;
        if (previous.Enabled != snapshot.Enabled) OnPropertyChanged(nameof(Enabled));
        if (previous.RemindersEnabled != snapshot.RemindersEnabled) OnPropertyChanged(nameof(RemindersEnabled));
        if (previous.DefaultOffsetMinutes != snapshot.DefaultOffsetMinutes) OnPropertyChanged(nameof(DefaultOffsetMinutes));
    }

    private void SetLayoutSnapshot(TodoLayoutSettings snapshot)
    {
        TodoLayoutSettings previous = _layoutSnapshot;
        _layoutSnapshot = snapshot;
        if (previous.LayoutMode != snapshot.LayoutMode)
            OnPropertyChanged(nameof(LayoutMode));
        if (previous.AutoSelectFirstInWideLayout != snapshot.AutoSelectFirstInWideLayout)
            OnPropertyChanged(nameof(AutoSelectFirstInWideLayout));
    }

    private void SetTabSnapshot(TodoTabSettings snapshot)
    {
        TodoTabSettings previous = _tabSnapshot;
        _tabSnapshot = snapshot;
        if (previous == snapshot) return;
        OnPropertyChanged(nameof(Tabs));
        if (previous.DefaultFilter != snapshot.DefaultFilter)
            OnPropertyChanged(nameof(DefaultFilter));
        if (previous.ShowTabBar != snapshot.ShowTabBar)
            OnPropertyChanged(nameof(ShowTabBar));
    }

    private void SetContentDisplaySnapshot(
        TodoContentDisplaySettings snapshot)
    {
        TodoContentDisplaySettings previous = _contentDisplaySnapshot;
        _contentDisplaySnapshot = snapshot;
        if (previous.PreviewLineCount != snapshot.PreviewLineCount)
            OnPropertyChanged(nameof(PreviewLineCount));
        if (previous.ListTextSize != snapshot.ListTextSize)
            OnPropertyChanged(nameof(ListTextSize));
        if (previous.ContentTextSize != snapshot.ContentTextSize)
            OnPropertyChanged(nameof(ContentTextSize));
    }

    private void SetInputSnapshot(TodoInputSettings snapshot)
    {
        TodoInputSettings previous = _inputSnapshot;
        _inputSnapshot = snapshot;
        if (previous.NewTaskPosition != snapshot.NewTaskPosition)
            OnPropertyChanged(nameof(NewTaskPosition));
        if (previous.EditorEnterBehavior != snapshot.EditorEnterBehavior)
            OnPropertyChanged(nameof(EditorEnterBehavior));
    }

    private void SetDisplaySnapshot(TodoDisplayOptions snapshot)
    {
        TodoDisplayOptions previous = _displaySnapshot;
        _displaySnapshot = snapshot;
        if (previous.ShowCompletedTasks != snapshot.ShowCompletedTasks)
            OnPropertyChanged(nameof(ShowCompletedTasks));
        if (previous.ShowFooterStats != snapshot.ShowFooterStats)
            OnPropertyChanged(nameof(ShowFooterStats));
        if (previous.ShowClearCompletedButton != snapshot.ShowClearCompletedButton)
            OnPropertyChanged(nameof(ShowClearCompletedButton));
        if (previous.TabStyle != snapshot.TabStyle)
            OnPropertyChanged(nameof(TabStyle));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
