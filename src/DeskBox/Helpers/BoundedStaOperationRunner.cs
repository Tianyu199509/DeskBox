using DeskBox.Platform;
using System.Runtime.InteropServices;

namespace DeskBox.Helpers;

internal readonly record struct StaOperationResult<T>(bool Started, T? Value = default);

/// <summary>
/// Short-lived STA workers for synchronous Shell operations. Both the running
/// calls and the waiting queue are bounded. A cancelled caller must not release
/// a running call's slot: native Shell calls cannot be safely aborted.
/// </summary>
internal sealed class BoundedStaOperationRunner
{
    private readonly SemaphoreSlim _workers;
    private readonly SemaphoreSlim _admission;
    private readonly TimeSpan _queueTimeout;

    internal BoundedStaOperationRunner(
        int maxConcurrency,
        int maxQueued,
        TimeSpan queueTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxQueued);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(queueTimeout, TimeSpan.Zero);
        _workers = new(maxConcurrency, maxConcurrency);
        _admission = new(maxConcurrency + maxQueued, maxConcurrency + maxQueued);
        _queueTimeout = queueTimeout;
    }

    internal async Task<StaOperationResult<T>> RunAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_admission.Wait(0))
        {
            return new(false);
        }

        try
        {
            if (!await _workers.WaitAsync(_queueTimeout, cancellationToken).ConfigureAwait(false))
            {
                return new(false);
            }

            try
            {
                // Do not WaitAsync(cancellationToken) here: the worker owns the
                // capacity until the underlying native operation really ends.
                return new(true, await RunOnStaAsync(operation, cancellationToken).ConfigureAwait(false));
            }
            finally
            {
                _workers.Release();
            }
        }
        finally
        {
            _admission.Release();
        }
    }

    private static Task<T> RunOnStaAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Explicitly initialize COM even under Native AOT, where
                // setting the managed apartment flag alone is not enough.
                int hresult = Ole32NativeMethods.CoInitializeEx(IntPtr.Zero, 0x2 | 0x4);
                Marshal.ThrowExceptionForHR(hresult);
                T result;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result = operation();
                }
                finally
                {
                    Ole32NativeMethods.CoUninitialize();
                }

                completion.TrySetResult(result);
            }
            catch (OperationCanceledException ex)
            {
                completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "DeskBox File Open"
        };

        try
        {
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }

        return completion.Task;
    }
}
