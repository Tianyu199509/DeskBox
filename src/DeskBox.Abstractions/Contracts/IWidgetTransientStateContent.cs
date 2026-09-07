namespace DeskBox.Contracts;

/// <summary>
/// Optional opaque member-state boundary used by the Surface transaction.
/// The manager stores the value without knowing a widget kind.
/// </summary>
public interface IWidgetTransientStateContent
{
    object? CaptureTransientState();

    void RestoreTransientState(object? state);
}
