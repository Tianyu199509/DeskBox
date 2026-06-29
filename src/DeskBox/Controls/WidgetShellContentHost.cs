using DeskBox.Contracts;

namespace DeskBox.Controls;

/// <summary>
/// Bridges an <see cref="IWidgetContent"/> into a <see cref="WidgetShell"/> while
/// keeping content lifecycle separate from window and z-order behavior.
/// </summary>
public sealed class WidgetShellContentHost
{
    private readonly Action<IWidgetContent> _setContent;

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

        await content.InitializeAsync();
        if (!ReferenceEquals(CurrentContent, content))
        {
            CurrentContent?.OnDeactivated();
        }

        CurrentContent = content;
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
}
