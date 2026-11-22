using DeskBox.Platform;

namespace DeskBox.Helpers;

/// <summary>
/// What happened when a drop was offered to an application shortcut. The
/// caller must distinguish "no shortcut owned this drop" (an ordinary import
/// continues) from "the shortcut refused or failed" (the drop is consumed and
/// must never become an import - importing moves the user's files).
/// </summary>
internal enum ShellDropLaunchOutcome
{
    /// <summary>No application-shortcut tile owned this drop.</summary>
    NotAttempted,

    /// <summary>The Shell's shortcut handler accepted and ran the drop.</summary>
    Launched,

    /// <summary>
    /// The linked application does not accept this drop: the Shell's
    /// DragEnter reported DROPEFFECT_NONE (a document target, or an
    /// application that cannot open the dragged file type).
    /// </summary>
    TargetRefused,

    /// <summary>
    /// The Shell could not be asked (no drop target bound) or its Drop failed.
    /// </summary>
    LaunchFailed,

    /// <summary>
    /// Another entry point for the same physical release already resolved this
    /// gesture as a launch. The drop stays consumed (never an import) but must
    /// not be delegated or reported a second time.
    /// </summary>
    AlreadyResolved
}

internal readonly struct ShellDropLaunchResult(
    ShellDropLaunchOutcome outcome,
    uint effect,
    int launchHResult)
{
    internal ShellDropLaunchOutcome Outcome => outcome;

    /// <summary>The Shell ran the drop and the caller must not import it.</summary>
    internal bool Handled => outcome == ShellDropLaunchOutcome.Launched;

    /// <summary>
    /// The drop belonged to an application shortcut and did not launch. The
    /// caller consumes it: no transfer session, no import, and the effect
    /// handed back to the drag source stays DROPEFFECT_NONE so the source's
    /// own move never runs either.
    /// </summary>
    internal bool Consumed => ShortcutDropOutcomePolicy.IsConsumed(outcome);

    internal uint Effect => effect;

    internal int LaunchHResult => launchHResult;

    internal static ShellDropLaunchResult NotAttempted => default;

    internal static ShellDropLaunchResult AlreadyResolved =>
        new(ShellDropLaunchOutcome.AlreadyResolved, NativeDropEffectPolicy.None, 0);

    /// <summary>
    /// The dropped files were opened with the shortcut's application (see
    /// <see cref="ShortcutFileLauncher"/>). The effect handed back to the drag
    /// source is DROPEFFECT_LINK so a move-drag never deletes the source.
    /// </summary>
    internal static ShellDropLaunchResult Launched(uint effect, int hResult) =>
        new(ShellDropLaunchOutcome.Launched, effect, hResult);

    internal static ShellDropLaunchResult Refused(uint effect, int hResult) =>
        new(ShellDropLaunchOutcome.TargetRefused, effect, hResult);

    internal static ShellDropLaunchResult Failed(int hResult) =>
        new(ShellDropLaunchOutcome.LaunchFailed, NativeDropEffectPolicy.None, hResult);
}

/// <summary>
/// The single decision every launch call site shares: a shortcut drop that did
/// not launch is consumed rather than falling back to import, and only a drop
/// no shortcut owned keeps its ordinary import semantics.
/// </summary>
internal static class ShortcutDropOutcomePolicy
{
    internal static bool IsConsumed(ShellDropLaunchOutcome outcome) =>
        outcome is ShellDropLaunchOutcome.TargetRefused or
            ShellDropLaunchOutcome.LaunchFailed or
            ShellDropLaunchOutcome.AlreadyResolved;

    internal static bool ShouldContinueAsImport(ShellDropLaunchOutcome outcome) =>
        outcome == ShellDropLaunchOutcome.NotAttempted;
}
