using DeskBox.Contracts;

namespace DeskBox.Controls;

/// <summary>
/// Bridges an <see cref="IWidgetContent"/> into a <see cref="WidgetShell"/> while
/// keeping content lifecycle separate from window and z-order behavior.
/// </summary>
public sealed class WidgetShellContentHost
{
    private readonly Action<IWidgetContent> _setContent;
    private IWidgetContent? _pendingContent;
    private int _contentVersion;
    private bool _isDisposed;

    public WidgetShellContentHost(WidgetShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _setContent = shell.SetContent;
    }

    internal WidgetShellContentHost(Action<IWidgetContent> setContent)
    {
        _setContent = setContent ?? throw new ArgumentNullException(nameof(setContent));
    }

    public IWidgetContent? CurrentContent { get; private set; }

    public async Task SetContentAsync(IWidgetContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (_isDisposed)
        {
            (content as IDisposable)?.Dispose();
            return;
        }

        int contentVersion = ++_contentVersion;
        _pendingContent = content;
        try
        {
            await content.InitializeAsync();
        }
        catch
        {
            if (ReferenceEquals(_pendingContent, content))
            {
                _pendingContent = null;
            }

            (content as IDisposable)?.Dispose();
            throw;
        }

        if (_isDisposed || contentVersion != _contentVersion)
        {
            if (ReferenceEquals(_pendingContent, content))
            {
                _pendingContent = null;
            }

            (content as IDisposable)?.Dispose();
            return;
        }

        if (!ReferenceEquals(CurrentContent, content))
        {
            CurrentContent?.OnDeactivated();
            (CurrentContent as IDisposable)?.Dispose();
        }

        CurrentContent = content;
        _pendingContent = null;
        _setContent(content);
        content.ApplyAppearance();
    }

    public Task RefreshAsync()
    {
        return CurrentContent?.RefreshAsync() ?? Task.CompletedTask;
    }

    public void ApplyAppearance()
    {
        CurrentContent?.ApplyAppearance();
    }

    public void OnActivated()
    {
        CurrentContent?.OnActivated();
    }

    public void OnDeactivated()
    {
        CurrentContent?.OnDeactivated();
    }

    public void OnWindowVisibilityChanged(bool visible)
    {
        CurrentContent?.OnWindowVisibilityChanged(visible);
    }

    public void DisposeContent()
    {
        if (_isDisposed && CurrentContent is null && _pendingContent is null)
        {
            return;
        }

        _isDisposed = true;
        _contentVersion++;
        if (_pendingContent is not null && !ReferenceEquals(_pendingContent, CurrentContent))
        {
            (_pendingContent as IDisposable)?.Dispose();
            _pendingContent = null;
        }

        CurrentContent?.OnDeactivated();
        (CurrentContent as IDisposable)?.Dispose();
        CurrentContent = null;
    }
}
