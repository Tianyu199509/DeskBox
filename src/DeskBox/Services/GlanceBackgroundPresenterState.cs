namespace DeskBox.Services;

/// <summary>
/// One background decode request issued by <see cref="GlanceBackgroundPresenterState"/>.
/// The token is the only proof a decode callback may still publish its frame.
/// </summary>
internal readonly record struct GlanceBackgroundLoadTicket(
    int Token,
    string Path,
    int DecodePixelWidth,
    bool AllowTransition,
    int Attempt);

/// <summary>
/// Pure state machine behind the Glance background image surface.
///
/// The previous surface tracked "requested" and "decoded" paths in loose
/// fields guarded by a load version that clearing never invalidated: a decode
/// that completed after the window hid re-marked the image as shown while its
/// brush had already been dropped, so the next reveal skipped the reload and
/// left the widget blank until an unrelated event reloaded it.
///
/// Invariants enforced here:
/// <list type="bullet">
/// <item>Every suspend, release, reset and superseding request invalidates the
/// outstanding token, so a stale decode can never publish.</item>
/// <item><see cref="HasFrame"/> is true only after a decode was accepted while
/// the surface was active, and becomes false whenever the view drops the
/// brush — the view must call <see cref="ReleaseFrame"/> in that case.</item>
/// <item>Resuming with a retained frame for the same image is a no-op, so a
/// quick hide/show (Show desktop, tray toggle) does not flash or re-decode.</item>
/// </list>
/// </summary>
internal sealed class GlanceBackgroundPresenterState
{
    internal const int MaxDecodeAttempts = 3;

    private int _token;
    private GlanceBackgroundLoadTicket? _pending;

    public bool IsActive { get; private set; }

    public bool HasFrame { get; private set; }

    public string? FramePath { get; private set; }

    public int FrameDecodePixelWidth { get; private set; }

    public bool HasPending => _pending is not null;

    public GlanceBackgroundLoadTicket? Pending => _pending;

    /// <summary>Marks the surface visible and allowed to decode.</summary>
    public void Activate() => IsActive = true;

    /// <summary>
    /// Hidden, collapsed or unloaded: cancel any in-flight decode but keep the
    /// accepted frame so a quick reveal can reuse it.
    /// </summary>
    public void Suspend()
    {
        IsActive = false;
        CancelPending();
    }

    /// <summary>The view dropped the displayed brush (grace expiry, clear).</summary>
    public void ReleaseFrame()
    {
        CancelPending();
        HasFrame = false;
        FramePath = null;
        FrameDecodePixelWidth = 0;
    }

    /// <summary>
    /// Requests that <paramref name="path"/> be displayed at
    /// <paramref name="decodePixelWidth"/>. Returns a ticket when a decode must
    /// start, or null when the current frame or in-flight decode already
    /// satisfies the request (or the surface is inactive).
    /// </summary>
    public GlanceBackgroundLoadTicket? Request(
        string path,
        int decodePixelWidth,
        bool allowTransition)
    {
        if (!IsActive || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (HasFrame &&
            IsSamePath(FramePath, path) &&
            !GlanceImageDecodeSizeCalculator.NeedsRefresh(FrameDecodePixelWidth, decodePixelWidth))
        {
            // The displayed frame is already right; anything still decoding
            // (e.g. the user flipped back) must not replace it afterwards.
            CancelPending();
            return null;
        }

        if (_pending is { } pending &&
            IsSamePath(pending.Path, path) &&
            !GlanceImageDecodeSizeCalculator.NeedsRefresh(pending.DecodePixelWidth, decodePixelWidth))
        {
            return null;
        }

        var ticket = new GlanceBackgroundLoadTicket(
            ++_token,
            path,
            decodePixelWidth,
            allowTransition,
            Attempt: 1);
        _pending = ticket;
        return ticket;
    }

    /// <summary>
    /// A decode finished. Returns true only when it is the current request and
    /// the surface is still active; the caller then shows the frame.
    /// </summary>
    public bool TryAcceptOpened(int token)
    {
        if (!IsActive || _pending is not { } pending || pending.Token != token)
        {
            return false;
        }

        _pending = null;
        HasFrame = true;
        FramePath = pending.Path;
        FrameDecodePixelWidth = pending.DecodePixelWidth;
        return true;
    }

    /// <summary>
    /// A decode failed. Returns a retry ticket while attempts remain for the
    /// current request; stale failures and exhausted retries return null.
    /// </summary>
    public GlanceBackgroundLoadTicket? OnFailed(int token)
    {
        if (_pending is not { } pending || pending.Token != token)
        {
            return null;
        }

        if (!IsActive || pending.Attempt >= MaxDecodeAttempts)
        {
            _pending = null;
            return null;
        }

        var retry = pending with
        {
            Token = ++_token,
            Attempt = pending.Attempt + 1,
            AllowTransition = pending.AllowTransition
        };
        _pending = retry;
        return retry;
    }

    /// <summary>True when <paramref name="token"/> is still the request to run.</summary>
    public bool IsCurrent(int token) =>
        IsActive && _pending is { } pending && pending.Token == token;

    /// <summary>
    /// Largest decode width known for <paramref name="path"/> (displayed or
    /// in flight); zero when neither matches.
    /// </summary>
    public int GetKnownDecodePixelWidth(string? path)
    {
        int width = 0;
        if (HasFrame && IsSamePath(FramePath, path))
        {
            width = FrameDecodePixelWidth;
        }

        if (_pending is { } pending && IsSamePath(pending.Path, path))
        {
            width = Math.Max(width, pending.DecodePixelWidth);
        }

        return width;
    }

    /// <summary>Retry back-off for a ticket's attempt number.</summary>
    public static TimeSpan GetRetryDelay(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Clamp(attempt - 1, 1, 4) * 400);

    private void CancelPending()
    {
        _pending = null;
        _token++;
    }

    private static bool IsSamePath(string? left, string? right) =>
        left is not null &&
        right is not null &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
