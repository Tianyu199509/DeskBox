using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Features.QuickCapture;

/// <summary>Owns at most one active listener; retired sessions drain before shutdown completes.</summary>
public sealed class QuickCaptureClipboardRuntime
{
    private readonly Func<bool> _shouldListen;
    private readonly Func<IQuickCaptureClipboardSession> _create;
    private readonly Action<Exception> _reportError;
    private readonly List<Task> _retired = [];
    private IQuickCaptureClipboardSession? _current;
    private Task? _stopTask;
    private bool _stopping;

    public QuickCaptureClipboardRuntime(Func<bool> shouldListen,
        Func<IQuickCaptureClipboardSession> create, Action<Exception> reportError)
    {
        _shouldListen = shouldListen;
        _create = create;
        _reportError = reportError;
    }

    public event Action? DiagnosticsChanged;
    public IQuickCaptureClipboardSession? Current => _current;
    public QuickCaptureClipboardDiagnostics? CurrentDiagnostics => _current?.GetDiagnostics();
    public bool IsStopping => _stopping;

    public void Refresh(bool captureCurrent = false)
    {
        if (_stopping) return;
        if (!_shouldListen())
        {
            RetireCurrent();
            return;
        }

        bool created = false;
        if (_current is null)
        {
            IQuickCaptureClipboardSession candidate = _create();
            candidate.DiagnosticsChanged += OnDiagnosticsChanged;
            _current = candidate;
            created = true;
        }

        try
        {
            _current.Refresh();
            // Start captures the current clipboard once. A subsequent user
            // action requests a fresh capture only on an existing session.
            if (captureCurrent && !created) _current.CaptureCurrent();
        }
        catch
        {
            RetireCurrent();
            throw;
        }
        DiagnosticsChanged?.Invoke();
    }

    private void OnDiagnosticsChanged() => DiagnosticsChanged?.Invoke();

    private void RetireCurrent()
    {
        IQuickCaptureClipboardSession? old = _current;
        if (old is null) return;
        _current = null;
        old.DiagnosticsChanged -= OnDiagnosticsChanged;
        _retired.RemoveAll(task => task.IsCompleted);
        _retired.Add(RetireAsync(old));
        DiagnosticsChanged?.Invoke();
    }

    private async Task RetireAsync(IQuickCaptureClipboardSession session)
    {
        try { await session.StopAsync(); }
        catch (Exception ex) { _reportError(ex); }
        finally { session.Dispose(); }
    }

    public Task DrainRetiredAsync() => Task.WhenAll(_retired.ToArray());

    public Task StopAsync() => _stopTask ??= StopCoreAsync();

    private async Task StopCoreAsync()
    {
        _stopping = true;
        RetireCurrent();
        await Task.WhenAll(_retired.ToArray());
    }
}
