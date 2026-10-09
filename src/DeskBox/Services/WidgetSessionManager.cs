namespace DeskBox.Services;

public enum WidgetSessionState
{
    DesktopResting,
    RaisedSession,
    InteractionActive,
    Hidden
}

/// <summary>
/// First-pass session coordinator. It records the widget session state only;
/// window z-order and animation decisions remain owned by WidgetManager/windows.
/// </summary>
public sealed class WidgetSessionManager
{
    private readonly Action<string>? _log;
    private int _interactionDepth;
    private WidgetSessionState _stateBeforeInteraction = WidgetSessionState.DesktopResting;

    public WidgetSessionManager(Action<string>? log = null)
    {
        _log = log;
    }

    public WidgetSessionState State { get; private set; } = WidgetSessionState.DesktopResting;
    public bool IsRaised => State == WidgetSessionState.RaisedSession || State == WidgetSessionState.InteractionActive;
    public bool IsInteractionActive => _interactionDepth > 0;

    /// <summary>
    /// Raised when IsInteractionActive flips. The display-topology gate parks
    /// restores while an interaction (drag/resize/capsule arrangement) runs so
    /// long interactions cannot burn the coordinator's retry budget (spec 5.6).
    /// </summary>
    public event Action? InteractionActiveChanged;

    private void OnInteractionDepthChanged()
    {
        try
        {
            InteractionActiveChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[WidgetSession] InteractionActiveChanged handler failed: {ex.Message}");
        }
    }

    public void MarkDesktopResting(string reason)
    {
        _interactionDepth = 0;
        SetState(WidgetSessionState.DesktopResting, reason);
    }

    public void MarkRaisedSession(string reason)
    {
        if (_interactionDepth > 0)
        {
            SetState(WidgetSessionState.InteractionActive, reason);
            return;
        }

        SetState(WidgetSessionState.RaisedSession, reason);
    }

    public void MarkHidden(string reason)
    {
        _interactionDepth = 0;
        SetState(WidgetSessionState.Hidden, reason);
    }

    public void BeginInteraction(string reason)
    {
        if (_interactionDepth == 0 && State != WidgetSessionState.InteractionActive)
        {
            _stateBeforeInteraction = State;
        }

        _interactionDepth++;
        if (_interactionDepth == 1)
        {
            OnInteractionDepthChanged();
        }

        SetState(WidgetSessionState.InteractionActive, reason);
    }

    public void EndInteraction(string reason)
    {
        if (_interactionDepth <= 0)
        {
            _log?.Invoke($"[WidgetSession] ignored end reason={reason} state={State}");
            return;
        }

        _interactionDepth--;
        if (_interactionDepth == 0)
        {
            OnInteractionDepthChanged();
        }

        SetState(_interactionDepth > 0 ? WidgetSessionState.InteractionActive : _stateBeforeInteraction, reason);
    }

    private void SetState(WidgetSessionState nextState, string reason)
    {
        if (State == nextState)
        {
            _log?.Invoke($"[WidgetSession] kept state={State} reason={reason} interactions={_interactionDepth}");
            return;
        }

        var previousState = State;
        State = nextState;
        _log?.Invoke($"[WidgetSession] changed {previousState} -> {nextState} reason={reason} interactions={_interactionDepth}");
    }
}
