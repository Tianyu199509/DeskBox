// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Helpers;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Reference-counted toggle used when several short animations share the
/// DirectComposition clock. One animation finishing must not disable the
/// active cadence while another animation still owns a lease.
/// </summary>
internal sealed class ReferenceCountedToggleLeasePool
{
    private readonly object _sync = new();
    private readonly Action<bool> _setEnabled;
    private int _leaseCount;

    public ReferenceCountedToggleLeasePool(Action<bool> setEnabled)
    {
        _setEnabled = setEnabled ?? throw new ArgumentNullException(nameof(setEnabled));
    }

    public int ActiveLeaseCount
    {
        get
        {
            lock (_sync)
            {
                return _leaseCount;
            }
        }
    }

    public IDisposable Acquire()
    {
        lock (_sync)
        {
            if (_leaseCount++ == 0)
            {
                _setEnabled(true);
            }
        }

        return new Lease(this);
    }

    private void Release()
    {
        lock (_sync)
        {
            if (_leaseCount <= 0)
            {
                return;
            }

            if (--_leaseCount == 0)
            {
                _setEnabled(false);
            }
        }
    }

    private sealed class Lease(ReferenceCountedToggleLeasePool owner) : IDisposable
    {
        private ReferenceCountedToggleLeasePool? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}

internal static class CompositorClockBoostCoordinator
{
    private static readonly ReferenceCountedToggleLeasePool LeasePool =
        new(enabled =>
        {
            _ = Win32Helper.TrySetCompositorClockBoost(enabled);
            if (!WindowsCompatibilityService.IsWindows11OrLater)
            {
                _ = Win32Helper.TrySetHighResolutionTimer(enabled);
            }
        });

    public static IDisposable Acquire() => LeasePool.Acquire();
}
