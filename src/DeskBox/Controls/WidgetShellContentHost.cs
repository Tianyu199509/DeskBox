using DeskBox.Contracts;
using DeskBox.Services;

namespace DeskBox.Controls;

/// <summary>
/// Bridges an <see cref="IWidgetContent"/> into a <see cref="WidgetShell"/> while
/// keeping content lifecycle separate from window and z-order behavior.
/// </summary>
public sealed class WidgetShellContentHost
{
    private readonly Action<IWidgetContent> _setContent;
    private readonly Action _clearContent;
    private readonly Action<IWidgetContent, IWidgetContent> _beginTransition;
    private readonly Action _completeTransition;
    private readonly Action<IWidgetContent> _rollbackTransition;
    private readonly Func<IWidgetContent, bool>? _retainContent;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        IWidgetContent,
        object> _disposedContents = new();
    private readonly object _disposedContentGate = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        IWidgetContent, AppearanceStamp> _appliedAppearances = new();
    private long _appearanceVersion;

    private sealed class AppearanceStamp
    {
        internal long Version = -1;
    }

    private IWidgetContent? _pendingContent;
    private Task? _pendingInitializationTask;
    private WidgetShellPreparedContent? _preparedContent;
    private WidgetShellContentTransition? _activeTransition;
    private int _contentVersion;
    private bool _isDisposed;
    private bool _isWindowVisible;
    private bool _isWindowRevealCompleted;
    private int _windowRevealGeneration;
    private bool _isActivated;

    public WidgetShellContentHost(
        WidgetShell shell,
        Func<IWidgetContent, bool>? retainContent = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _setContent = shell.SetContent;
        _clearContent = shell.ClearContent;
        _beginTransition = shell.BeginContentTransition;
        _completeTransition = shell.CompleteContentTransition;
        _rollbackTransition = shell.RollbackContentTransition;
        _retainContent = retainContent;
    }

    internal WidgetShellContentHost(
        Action<IWidgetContent> setContent,
        Action? clearContent = null,
        Action<IWidgetContent, IWidgetContent>? beginTransition = null,
        Action? completeTransition = null,
        Action<IWidgetContent>? rollbackTransition = null,
        Func<IWidgetContent, bool>? retainContent = null)
    {
        _setContent = setContent ?? throw new ArgumentNullException(nameof(setContent));
        _clearContent = clearContent ?? (() => { });
        _beginTransition = beginTransition ?? ((_, incoming) => _setContent(incoming));
        _completeTransition = completeTransition ?? (() => { });
        _rollbackTransition = rollbackTransition ?? _setContent;
        _retainContent = retainContent;
    }

    public IWidgetContent? CurrentContent { get; private set; }

    internal int LiveContentCount =>
        (CurrentContent is null ? 0 : 1) +
        (_activeTransition?.OutgoingContent is null ? 0 : 1);

    public async Task SetContentAsync(IWidgetContent content)
    {
        using WidgetShellPreparedContent? prepared =
            await PrepareContentAsync(content, CancellationToken.None);
        if (prepared is null)
        {
            return;
        }

        using WidgetShellContentTransition? transition =
            CommitPreparedContent(prepared);
        transition?.Complete();
    }

    internal async Task<WidgetShellPreparedContent?> PrepareContentAsync(
        IWidgetContent content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (cancellationToken.IsCancellationRequested)
        {
            DisposeContentOnce(content);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (_isDisposed)
        {
            DisposeContentOnce(content);
            return null;
        }

        if (_activeTransition is not null)
        {
            DisposeContentOnce(content);
            throw new InvalidOperationException(
                "A content transition must be completed or rolled back before preparing another content.");
        }

        _preparedContent?.Dispose();
        int contentVersion = ++_contentVersion;
        _pendingContent = content;
        Task? initializationTask = null;
        try
        {
            if (content is IWidgetGroupContentCacheable
                {
                    IsReadyForReuse: true
                } reusableContent)
            {
                reusableContent.PrepareForReuse();
                initializationTask = Task.CompletedTask;
            }
            else
            {
                initializationTask = content is ICancellableWidgetContent cancellableContent
                    ? cancellableContent.InitializeAsync(cancellationToken)
                    : content.InitializeAsync();
            }
            _pendingInitializationTask = initializationTask;
            await initializationTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ClearPendingContent(content, initializationTask);
            bool supportsCancellableInitialization =
                content is ICancellableWidgetContent;
            if (supportsCancellableInitialization)
            {
                // A cancellable file surface can stop its watcher/enumeration
                // work. Do not retain an obsolete view until slow shell work
                // eventually completes.
                DisposeContentOnce(content);
            }
            if (initializationTask is { IsCompleted: false })
            {
                _ = ObserveInitializationAndDisposeAsync(initializationTask, content);
            }
            else if (!supportsCancellableInitialization)
            {
                DisposeContentOnce(content);
            }

            throw;
        }
        catch
        {
            ClearPendingContent(content, initializationTask);
            DisposeContentOnce(content);
            throw;
        }

        ClearPendingContent(content, initializationTask);
        if (_isDisposed || contentVersion != _contentVersion)
        {
            DisposeContentOnce(content);
            return null;
        }

        var prepared = new WidgetShellPreparedContent(this, content, contentVersion);
        _preparedContent = prepared;
        return prepared;
    }

    internal WidgetShellContentTransition? CommitPreparedContent(
        WidgetShellPreparedContent prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (_isDisposed ||
            !ReferenceEquals(_preparedContent, prepared) ||
            prepared.ContentVersion != _contentVersion ||
            prepared.IsConsumed)
        {
            prepared.Dispose();
            return null;
        }

        _preparedContent = null;
        prepared.MarkConsumed();
        IWidgetContent content = prepared.Content;
        IWidgetContent? outgoingContent = CurrentContent;
        if (ReferenceEquals(outgoingContent, content))
        {
            return null;
        }

        outgoingContent?.OnDeactivated();
        CurrentContent = content;
        try
        {
            if (outgoingContent is null)
            {
                _setContent(content);
            }
            else
            {
                _beginTransition(outgoingContent, content);
            }

            ApplyContentAppearance(content);
            content.OnWindowVisibilityChanged(_isWindowVisible);
            if (_isWindowVisible && _isWindowRevealCompleted)
            {
                content.OnWindowRevealCompleted();
            }
            if (_isActivated)
            {
                content.OnActivated();
            }
        }
        catch
        {
            CurrentContent = outgoingContent;
            DisposeContentOnce(content);
            if (outgoingContent is not null)
            {
                _rollbackTransition(outgoingContent);
                outgoingContent.OnWindowVisibilityChanged(_isWindowVisible);
                if (_isWindowVisible && _isWindowRevealCompleted)
                {
                    outgoingContent.OnWindowRevealCompleted();
                }
                if (_isActivated)
                {
                    outgoingContent.OnActivated();
                }
            }
            throw;
        }

        var transition = new WidgetShellContentTransition(
            this,
            content,
            outgoingContent,
            prepared.ContentVersion);
        _activeTransition = transition;
        return transition;
    }

    public Task RefreshAsync()
    {
        return CurrentContent?.RefreshAsync() ?? Task.CompletedTask;
    }

    public void ApplyAppearance(bool invalidate = true)
    {
        if (invalidate)
        {
            ++_appearanceVersion;
        }
        if (CurrentContent is { } content)
        {
            ApplyContentAppearance(content);
        }
    }

    private void ApplyContentAppearance(IWidgetContent content)
    {
        AppearanceStamp stamp = _appliedAppearances.GetValue(
            content, static _ => new AppearanceStamp());
        long version = _appearanceVersion;
        if (stamp.Version == version)
        {
            PerformanceLogger.RecordContentAppearanceSkipped();
            return;
        }

        content.ApplyAppearance();
        // A failed or reentrant update must not mark a newer appearance as applied.
        stamp.Version = version;
        PerformanceLogger.RecordContentAppearanceApplied();
    }

    public void OnActivated()
    {
        if (_isActivated)
        {
            return;
        }

        _isActivated = true;
        CurrentContent?.OnActivated();
    }

    public void OnDeactivated()
    {
        if (!_isActivated)
        {
            return;
        }

        _isActivated = false;
        CurrentContent?.OnDeactivated();
    }

    public void OnWindowVisibilityChanged(bool visible)
    {
        if (_isWindowVisible != visible)
        {
            _windowRevealGeneration++;
            _isWindowRevealCompleted = false;
        }

        _isWindowVisible = visible;
        CurrentContent?.OnWindowVisibilityChanged(visible);
    }

    public void OnWindowRevealCompleted()
    {
        if (_isDisposed || !_isWindowVisible || _isWindowRevealCompleted)
        {
            return;
        }

        _isWindowRevealCompleted = true;
        CurrentContent?.OnWindowRevealCompleted();
    }

    internal int WindowRevealGeneration => _windowRevealGeneration;

    public void DisposeContent()
    {
        if (_isDisposed && CurrentContent is null && _pendingContent is null)
        {
            return;
        }

        _isDisposed = true;
        _contentVersion++;
        _preparedContent?.Dispose();
        _preparedContent = null;

        WidgetShellContentTransition? activeTransition = _activeTransition;
        _activeTransition = null;
        activeTransition?.MarkCompleted();

        IWidgetContent? currentContent = CurrentContent;
        CurrentContent = null;
        currentContent?.OnWindowVisibilityChanged(false);
        currentContent?.OnDeactivated();
        _clearContent();
        DisposeContentOnce(currentContent);
        if (activeTransition?.OutgoingContent is { } outgoingContent)
        {
            outgoingContent.OnWindowVisibilityChanged(false);
            DisposeContentOnce(outgoingContent);
        }

        IWidgetContent? pendingContent = _pendingContent;
        Task? pendingInitialization = _pendingInitializationTask;
        _pendingContent = null;
        _pendingInitializationTask = null;
        if (pendingContent is not null &&
            !ReferenceEquals(pendingContent, currentContent))
        {
            if (pendingInitialization is { IsCompleted: false })
            {
                _ = ObserveInitializationAndDisposeAsync(
                    pendingInitialization,
                    pendingContent);
            }
            else
            {
                DisposeContentOnce(pendingContent);
            }
        }
    }

    private void CompleteTransition(WidgetShellContentTransition transition)
    {
        if (!ReferenceEquals(_activeTransition, transition))
        {
            transition.MarkCompleted();
            return;
        }

        // Keep the transition active until the UI presenter has accepted the
        // commit. If presenter cleanup fails, the caller can still roll back
        // to the outgoing content.
        if (transition.OutgoingContent is not null)
        {
            _completeTransition();
        }

        _activeTransition = null;
        transition.MarkCompleted();

        if (transition.OutgoingContent is { } outgoingContent)
        {
            outgoingContent.OnWindowVisibilityChanged(false);
            if (TryRetainContent(outgoingContent))
            {
                return;
            }

            try
            {
                DisposeContentOnce(outgoingContent);
            }
            catch (Exception ex)
            {
                // The incoming member is already committed visually and in
                // CurrentContent. Cleanup failure must not reopen a settled
                // transaction and attach a partially disposed old member.
                App.Log(
                    $"[WidgetSurface] Outgoing content cleanup failed " +
                    $"member={outgoingContent.WidgetId}: {ex}");
            }
        }
    }

    private void RollbackTransition(WidgetShellContentTransition transition)
    {
        if (!ReferenceEquals(_activeTransition, transition))
        {
            transition.MarkCompleted();
            return;
        }

        _activeTransition = null;
        IWidgetContent incomingContent = transition.IncomingContent;
        incomingContent.OnWindowVisibilityChanged(false);
        incomingContent.OnDeactivated();
        if (transition.OutgoingContent is { } outgoingContent)
        {
            _rollbackTransition(outgoingContent);
            CurrentContent = outgoingContent;
            ApplyContentAppearance(outgoingContent);
            outgoingContent.OnWindowVisibilityChanged(_isWindowVisible);
            if (_isWindowVisible && _isWindowRevealCompleted)
            {
                outgoingContent.OnWindowRevealCompleted();
            }
            if (_isActivated)
            {
                outgoingContent.OnActivated();
            }
        }
        else
        {
            _clearContent();
            CurrentContent = null;
        }

        DisposeContentOnce(incomingContent);
        transition.MarkCompleted();
    }

    private void CancelPreparedContent(WidgetShellPreparedContent prepared)
    {
        if (!ReferenceEquals(_preparedContent, prepared) || prepared.IsConsumed)
        {
            return;
        }

        _preparedContent = null;
        _contentVersion++;
        prepared.MarkConsumed();
        DisposeContentOnce(prepared.Content);
    }

    private void ClearPendingContent(
        IWidgetContent content,
        Task? initializationTask)
    {
        if (ReferenceEquals(_pendingContent, content))
        {
            _pendingContent = null;
        }

        if (ReferenceEquals(_pendingInitializationTask, initializationTask))
        {
            _pendingInitializationTask = null;
        }
    }

    private async Task ObserveInitializationAndDisposeAsync(
        Task initializationTask,
        IWidgetContent content)
    {
        try
        {
            await initializationTask;
        }
        catch
        {
        }
        finally
        {
            DisposeContentOnce(content);
        }
    }

    private void DisposeContentOnce(IWidgetContent? content)
    {
        if (content is not IDisposable disposable)
        {
            return;
        }

        lock (_disposedContentGate)
        {
            if (_disposedContents.TryGetValue(content, out _))
            {
                return;
            }
            _disposedContents.Add(content, new object());
        }

        disposable.Dispose();
    }

    private bool TryRetainContent(IWidgetContent content)
    {
        if (_retainContent is null)
        {
            return false;
        }

        try
        {
            return _retainContent(content);
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetSurface] Retaining inactive content failed " +
                $"member={content.WidgetId}: {ex}");
            return false;
        }
    }

    internal sealed class WidgetShellPreparedContent : IDisposable
    {
        private readonly WidgetShellContentHost _owner;

        internal WidgetShellPreparedContent(
            WidgetShellContentHost owner,
            IWidgetContent content,
            int contentVersion)
        {
            _owner = owner;
            Content = content;
            ContentVersion = contentVersion;
        }

        internal IWidgetContent Content { get; }

        internal int ContentVersion { get; }

        internal bool IsConsumed { get; private set; }

        internal void MarkConsumed()
        {
            IsConsumed = true;
        }

        public void Dispose()
        {
            _owner.CancelPreparedContent(this);
        }
    }

    internal sealed class WidgetShellContentTransition : IDisposable
    {
        private readonly WidgetShellContentHost _owner;
        private bool _isCompleted;

        internal WidgetShellContentTransition(
            WidgetShellContentHost owner,
            IWidgetContent incomingContent,
            IWidgetContent? outgoingContent,
            int contentVersion)
        {
            _owner = owner;
            IncomingContent = incomingContent;
            OutgoingContent = outgoingContent;
            ContentVersion = contentVersion;
        }

        internal IWidgetContent IncomingContent { get; }

        internal IWidgetContent? OutgoingContent { get; }

        internal int ContentVersion { get; }

        public void Complete()
        {
            if (!_isCompleted)
            {
                _owner.CompleteTransition(this);
            }
        }

        public void Rollback()
        {
            if (!_isCompleted)
            {
                _owner.RollbackTransition(this);
            }
        }

        internal void MarkCompleted()
        {
            _isCompleted = true;
        }

        public void Dispose()
        {
            Rollback();
        }
    }
}
