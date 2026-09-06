using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace DeskBox.Helpers;

/// <summary>
/// Deterministic focus for inline rename editors. An editor injected into a
/// ContentPresenter only joins the visual tree during the next layout pass,
/// so an immediate <see cref="UIElement.Focus(FocusState)"/> call fails
/// silently. This helper retries on Loaded/LayoutUpdated plus a slow timer
/// backstop until Focus reports success, then applies the selection exactly
/// once. Callers must not add their own deferred focus retries on top.
/// </summary>
internal static class InlineEditorFocus
{
    /// <summary>
    /// Newly opened rename sessions can lose focus to flyout teardown before
    /// the user ever sees the caret. Within this window a LostFocus commit is
    /// converted into a single focus recovery instead.
    /// </summary>
    public const int FocusGracePeriodMs = 300;

    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(100);

    private const int GiveUpAfterMs = 3000;

    public static void FocusWhenLoaded(
        TextBox editor,
        Action<TextBox> applySelection,
        DispatcherQueue dispatcherQueue,
        string logScope)
    {
        long startedAtTick = Environment.TickCount64;
        bool settled = false;
        bool loggedWaitingForLayout = false;
        RoutedEventHandler? loadedHandler = null;
        EventHandler<object>? layoutHandler = null;
        DispatcherQueueTimer? retryTimer = null;
        TypedEventHandler<DispatcherQueueTimer, object>? timerHandler = null;

        void detach()
        {
            editor.Loaded -= loadedHandler;
            editor.LayoutUpdated -= layoutHandler;
            loadedHandler = null;
            layoutHandler = null;
            if (retryTimer is not null && timerHandler is not null)
            {
                retryTimer.Tick -= timerHandler;
                retryTimer.Stop();
                retryTimer = null;
                timerHandler = null;
            }
        }

        void settle(bool focused)
        {
            settled = true;
            detach();
            if (focused)
            {
                applySelection(editor);
            }
            else
            {
                App.Log(
                    $"[{logScope}] Inline editor focus abandoned after " +
                    $"{Environment.TickCount64 - startedAtTick}ms; the user " +
                    $"must click the editor before typing.");
            }
        }

        void tryFocus()
        {
            if (settled)
            {
                return;
            }

            if (editor.Focus(FocusState.Programmatic))
            {
                settle(focused: true);
                return;
            }

            if (Environment.TickCount64 - startedAtTick < GiveUpAfterMs)
            {
                if (!loggedWaitingForLayout)
                {
                    loggedWaitingForLayout = true;
                    App.LogVerbose(
                        $"[{logScope}] Inline editor not focusable yet; " +
                        $"retrying on layout.");
                }
                return;
            }

            settle(focused: false);
        }

        tryFocus();
        if (settled)
        {
            return;
        }

        loadedHandler = (_, _) => tryFocus();
        layoutHandler = (_, _) => tryFocus();
        editor.Loaded += loadedHandler;
        editor.LayoutUpdated += layoutHandler;

        // Slow backstop only: the layout pass this helper waits for runs when
        // the dispatcher queue drains, so a fast self-reenqueueing dispatcher
        // loop would starve it and freeze the UI until the next input forces
        // a synchronous layout.
        retryTimer = dispatcherQueue.CreateTimer();
        retryTimer.Interval = RetryInterval;
        retryTimer.IsRepeating = true;
        timerHandler = (_, _) => tryFocus();
        retryTimer.Tick += timerHandler;
        retryTimer.Start();
    }

    /// <summary>
    /// Returns true when the lost focus was recovered inside the grace window
    /// and the caller must swallow the LostFocus commit.
    /// </summary>
    public static bool TryRecoverFocusWithinGrace(
        long openedAtTick,
        TextBox? editor)
    {
        if (editor is null)
        {
            return false;
        }

        long elapsed = Environment.TickCount64 - openedAtTick;
        if (elapsed < 0 || elapsed >= FocusGracePeriodMs)
        {
            return false;
        }

        return editor.Focus(FocusState.Programmatic);
    }
}
