using System.Runtime.InteropServices;
using static DeskBox.Platform.MonitorAndBackdropInterop;
using Microsoft.UI.Composition;
using WinRT;

namespace DeskBox.Services;

/// <summary>
/// Generates and caches 256-level 16x16 supersampled Euclidean SDF rounded-corner
/// alpha masks as compact 9-grid <see cref="Windows.UI.Composition.CompositionNineGridBrush"/>
/// instances so <c>MicaController</c> and <c>DesktopAcrylicController</c> backdrops render
/// with sub-pixel anti-aliased edges on 100%–400% DPI displays without DWM frame shadows
/// or jagged <c>RectangleClip</c> / <c>CompositionGeometricClip</c> artifacts.
/// </summary>
internal static unsafe class RoundedBackdropMaskService
{
    private const int D3dDriverTypeHardware = 1;
    private const int D3dDriverTypeWarp = 5;
    private const uint D3d11CreateDeviceBgraSupport = 0x20;
    private const uint D3d11SdkVersion = 7;
    private const int SupersampleGrid = 16;
    private const int MaxIdleEntriesPerCompositor = 8;
    private const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
    private const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);

    private static readonly Guid s_iidCompositorInterop = new("25297D5C-3AD4-4C9C-B5CF-E36A38512330");
    private static readonly Guid s_iidCompositionDrawingSurfaceInterop = new("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE");
    private static readonly Guid s_iidD3d11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    internal readonly record struct MaskKey(int HalfRadiusPx, int ScaleThousandths);

    internal sealed class MaskLease : IDisposable
    {
        private CompositorMaskCache? _owner;
        private MaskEntry? _entry;

        internal MaskLease(CompositorMaskCache owner, MaskEntry entry)
        {
            _owner = owner;
            _entry = entry;
            entry.RefCount++;
        }

        internal Windows.UI.Composition.CompositionNineGridBrush NineGridBrush =>
            _entry?.NineGridBrush ?? throw new ObjectDisposedException(nameof(MaskLease));

        internal MaskKey Key => _entry?.Key ?? default;

        internal Windows.UI.Composition.Compositor? Compositor => _owner?.TryGetCompositor();

        public void Dispose()
        {
            CompositorMaskCache? owner = _owner;
            MaskEntry? entry = _entry;
            _owner = null;
            _entry = null;
            if (owner is not null && entry is not null)
            {
                owner.ReleaseEntry(entry);
            }
        }
    }

    internal sealed class MaskEntry(
        MaskKey key,
        Windows.UI.Composition.CompositionDrawingSurface surface,
        Windows.UI.Composition.CompositionSurfaceBrush surfaceBrush,
        Windows.UI.Composition.CompositionNineGridBrush nineGridBrush)
    {
        public MaskKey Key { get; } = key;
        public Windows.UI.Composition.CompositionDrawingSurface Surface { get; } = surface;
        public Windows.UI.Composition.CompositionSurfaceBrush SurfaceBrush { get; } = surfaceBrush;
        public Windows.UI.Composition.CompositionNineGridBrush NineGridBrush { get; } = nineGridBrush;
        public int RefCount { get; set; }
        public long LastUsedSequence { get; set; }

        public void DisposeResources()
        {
            try { NineGridBrush.Source = null; } catch { }
            try { NineGridBrush.Dispose(); } catch { }
            try { SurfaceBrush.Surface = null; } catch { }
            try { SurfaceBrush.Dispose(); } catch { }
            try { Surface.Dispose(); } catch { }
        }
    }

    internal sealed class CompositorMaskCache
    {
        private readonly WeakReference<Windows.UI.Composition.Compositor> _compositorRef;
        private Windows.UI.Composition.CompositionGraphicsDevice? _graphicsDevice;
        private readonly Dictionary<MaskKey, MaskEntry> _entries = [];
        private long _sequence;
        private bool _disposed;

        public CompositorMaskCache(
            Windows.UI.Composition.Compositor compositor,
            Windows.UI.Composition.CompositionGraphicsDevice graphicsDevice)
        {
            _compositorRef = new WeakReference<Windows.UI.Composition.Compositor>(compositor);
            _graphicsDevice = graphicsDevice;
        }

        public Windows.UI.Composition.Compositor? TryGetCompositor() =>
            !_disposed && _compositorRef.TryGetTarget(out var compositor) ? compositor : null;

        public Windows.UI.Composition.CompositionGraphicsDevice? GraphicsDevice =>
            _disposed ? null : _graphicsDevice;

        public void ResetGraphicsDevice(Windows.UI.Composition.CompositionGraphicsDevice? replacement)
        {
            var previous = _graphicsDevice;
            _graphicsDevice = replacement;
            ClearAllEntries();
            if (previous is not null && !ReferenceEquals(previous, replacement))
            {
                try { previous.Dispose(); } catch { }
            }
        }

        public bool TryGetReusableEntry(MaskKey key, out MaskEntry? entry)
        {
            if (_entries.TryGetValue(key, out entry))
            {
                entry.LastUsedSequence = ++_sequence;
                return true;
            }

            return false;
        }

        public void StoreEntry(MaskEntry entry)
        {
            entry.LastUsedSequence = ++_sequence;
            _entries[entry.Key] = entry;
            TrimExcessIdleEntries(MaxIdleEntriesPerCompositor);
        }

        public void ReleaseEntry(MaskEntry entry)
        {
            if (entry.RefCount > 0)
            {
                entry.RefCount--;
            }

            entry.LastUsedSequence = ++_sequence;
            if (entry.RefCount == 0)
            {
                TrimExcessIdleEntries(MaxIdleEntriesPerCompositor);
            }
        }

        public void TrimIdle() => TrimExcessIdleEntries(maxIdleToKeep: 0);

        public bool HasActiveEntries
        {
            get
            {
                foreach (MaskEntry entry in _entries.Values)
                {
                    if (entry.RefCount > 0)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public void DisposeAll()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ClearAllEntries();
            if (_graphicsDevice is not null)
            {
                try { _graphicsDevice.Dispose(); } catch { }
                _graphicsDevice = null;
            }
        }

        private void ClearAllEntries()
        {
            foreach (MaskEntry entry in _entries.Values)
            {
                entry.DisposeResources();
            }

            _entries.Clear();
        }

        private void TrimExcessIdleEntries(int maxIdleToKeep)
        {
            int idleCount = 0;
            foreach (MaskEntry entry in _entries.Values)
            {
                if (entry.RefCount == 0)
                {
                    idleCount++;
                }
            }

            while (idleCount > maxIdleToKeep)
            {
                MaskEntry? oldestIdle = null;
                foreach (MaskEntry candidate in _entries.Values)
                {
                    if (candidate.RefCount != 0)
                    {
                        continue;
                    }

                    if (oldestIdle is null || candidate.LastUsedSequence < oldestIdle.LastUsedSequence)
                    {
                        oldestIdle = candidate;
                    }
                }

                if (oldestIdle is null)
                {
                    break;
                }

                _entries.Remove(oldestIdle.Key);
                oldestIdle.DisposeResources();
                idleCount--;
            }
        }
    }

    [ThreadStatic]
    private static IntPtr t_d3dDevice;

    [ThreadStatic]
    private static IntPtr t_d3dContext;

    [ThreadStatic]
    private static List<CompositorMaskCache>? t_caches;

    internal static MaskKey BuildKey(double radiusDip, double rasterizationScale)
    {
        double clampedScale = ClampScale(rasterizationScale);
        double radiusPx = Math.Max(0.5, radiusDip * clampedScale);
        int halfRadiusPx = Math.Max(1, (int)Math.Round(radiusPx * 2.0, MidpointRounding.AwayFromZero));
        int scaleThousandths = (int)Math.Round(clampedScale * 1000.0, MidpointRounding.AwayFromZero);
        return new MaskKey(halfRadiusPx, scaleThousandths);
    }

    internal static MaskLease? AcquireNineGridMask(
        Windows.UI.Composition.Compositor compositor,
        double radiusDip,
        double rasterizationScale,
        MaskLease? existingLease = null)
    {
        if (!double.IsFinite(radiusDip) || radiusDip <= 0.01)
        {
            existingLease?.Dispose();
            return null;
        }

        MaskKey desiredKey = BuildKey(radiusDip, rasterizationScale);
        if (existingLease is not null &&
            existingLease.Key == desiredKey &&
            ReferenceEquals(existingLease.Compositor, compositor))
        {
            return existingLease;
        }

        existingLease?.Dispose();

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (!EnsureD3d11Device())
                {
                    return null;
                }

                CompositorMaskCache? cache = GetOrCreateCompositorCache(compositor);
                if (cache?.GraphicsDevice is not { } graphicsDevice)
                {
                    return null;
                }

                if (cache.TryGetReusableEntry(desiredKey, out MaskEntry? cached) && cached is not null)
                {
                    return new MaskLease(cache, cached);
                }

                double quantizedRadiusPx = desiredKey.HalfRadiusPx * 0.5;
                double clampedScale = desiredKey.ScaleThousandths / 1000.0;
                int insetTexels = (int)Math.Ceiling(quantizedRadiusPx) + 1;
                int texSize = (insetTexels * 2) + 4;

                var surface = graphicsDevice.CreateDrawingSurface(
                    new Windows.Foundation.Size(texSize, texSize),
                    Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    Windows.Graphics.DirectX.DirectXAlphaMode.Premultiplied);

                int uploadHr = UploadRoundedMaskPixels(surface, texSize, quantizedRadiusPx);
                if (uploadHr < 0)
                {
                    try { surface.Dispose(); } catch { }
                    if (attempt == 0 && (uploadHr == DxgiErrorDeviceRemoved || uploadHr == DxgiErrorDeviceReset))
                    {
                        ResetThreadD3dState();
                        continue;
                    }

                    return null;
                }

                var surfaceBrush = compositor.CreateSurfaceBrush(surface);
                surfaceBrush.Stretch = Windows.UI.Composition.CompositionStretch.Fill;
                surfaceBrush.BitmapInterpolationMode =
                    Windows.UI.Composition.CompositionBitmapInterpolationMode.Linear;

                var nineGridBrush = compositor.CreateNineGridBrush();
                nineGridBrush.Source = surfaceBrush;
                nineGridBrush.SetInsets(insetTexels, insetTexels, insetTexels, insetTexels);
                float insetScale = (float)(1.0 / clampedScale);
                nineGridBrush.SetInsetScales(insetScale, insetScale, insetScale, insetScale);

                var created = new MaskEntry(desiredKey, surface, surfaceBrush, nineGridBrush);
                cache.StoreEntry(created);
                return new MaskLease(cache, created);
            }
            catch (COMException ex) when (attempt == 0 &&
                (ex.HResult == DxgiErrorDeviceRemoved || ex.HResult == DxgiErrorDeviceReset))
            {
                ResetThreadD3dState();
            }
            catch (Exception ex)
            {
                App.LogVerbose($"[RoundedBackdropMask] Mask creation skipped: {ex.Message}");
                return null;
            }
        }

        return null;
    }

    internal static void TrimIdle()
    {
        if (t_caches is not { Count: > 0 } caches)
        {
            return;
        }

        bool anyActive = false;
        for (int i = caches.Count - 1; i >= 0; i--)
        {
            CompositorMaskCache cache = caches[i];
            if (cache.TryGetCompositor() is null)
            {
                cache.DisposeAll();
                caches.RemoveAt(i);
                continue;
            }

            cache.TrimIdle();
            if (cache.HasActiveEntries)
            {
                anyActive = true;
            }
            else
            {
                cache.DisposeAll();
                caches.RemoveAt(i);
            }
        }

        if (!anyActive && caches.Count == 0)
        {
            ReleaseD3dDeviceOnly();
        }
    }

    /// <summary>
    /// Computes the exact 256-level 16x16 supersampled Euclidean rounded-rectangle
    /// coverage for pixel <c>(px, py)</c> in an even-sized <c>texSize x texSize</c>
    /// 9-grid corner mask texture with corner radius <paramref name="radiusPx"/>.
    /// </summary>
    internal static byte ComputeCornerCoverageByte(
        int px,
        int py,
        int texSize,
        double radiusPx)
    {
        if (radiusPx <= 0 || texSize <= 0)
        {
            return 255;
        }

        int mx = px < (texSize / 2) ? px : (texSize - 1 - px);
        int my = py < (texSize / 2) ? py : (texSize - 1 - py);
        if (mx >= radiusPx || my >= radiusPx)
        {
            return 255;
        }

        // Fast-path pixels strictly outside or strictly inside the circle arc.
        double nearDx = Math.Max(0.0, radiusPx - (mx + 1.0));
        double nearDy = Math.Max(0.0, radiusPx - (my + 1.0));
        double r2 = radiusPx * radiusPx;
        if ((nearDx * nearDx) + (nearDy * nearDy) >= r2)
        {
            return 0;
        }

        double farDx = radiusPx - mx;
        double farDy = radiusPx - my;
        if ((farDx * farDx) + (farDy * farDy) <= r2)
        {
            return 255;
        }

        int inside = 0;
        const double invGrid = 1.0 / SupersampleGrid;
        for (int sy = 0; sy < SupersampleGrid; sy++)
        {
            double dy = radiusPx - (my + ((sy + 0.5) * invGrid));
            double dy2 = dy * dy;
            for (int sx = 0; sx < SupersampleGrid; sx++)
            {
                double dx = radiusPx - (mx + ((sx + 0.5) * invGrid));
                if ((dx * dx) + dy2 <= r2)
                {
                    inside++;
                }
            }
        }

        const int totalSamples = SupersampleGrid * SupersampleGrid;
        return (byte)(((inside * 255) + (totalSamples / 2)) / totalSamples);
    }

    private static double ClampScale(double scale) =>
        double.IsFinite(scale) && scale > 0.0
            ? Math.Clamp(scale, 0.5, 4.0)
            : 1.0;

    private static bool EnsureD3d11Device()
    {
        if (t_d3dDevice != IntPtr.Zero && t_d3dContext != IntPtr.Zero)
        {
            return true;
        }

        ReleaseD3dDeviceOnly();
        int hr = D3D11CreateDevice(
            IntPtr.Zero,
            D3dDriverTypeHardware,
            IntPtr.Zero,
            D3d11CreateDeviceBgraSupport,
            IntPtr.Zero,
            0,
            D3d11SdkVersion,
            out t_d3dDevice,
            out _,
            out t_d3dContext);
        if (hr < 0)
        {
            hr = D3D11CreateDevice(
                IntPtr.Zero,
                D3dDriverTypeWarp,
                IntPtr.Zero,
                D3d11CreateDeviceBgraSupport,
                IntPtr.Zero,
                0,
                D3d11SdkVersion,
                out t_d3dDevice,
                out _,
                out t_d3dContext);
        }

        if (hr < 0 || t_d3dDevice == IntPtr.Zero || t_d3dContext == IntPtr.Zero)
        {
            ReleaseD3dDeviceOnly();
            return false;
        }

        return true;
    }

    private static CompositorMaskCache? GetOrCreateCompositorCache(
        Windows.UI.Composition.Compositor compositor)
    {
        List<CompositorMaskCache> caches = t_caches ??= [];
        for (int i = caches.Count - 1; i >= 0; i--)
        {
            CompositorMaskCache existing = caches[i];
            var target = existing.TryGetCompositor();
            if (target is null)
            {
                existing.DisposeAll();
                caches.RemoveAt(i);
                continue;
            }

            if (ReferenceEquals(target, compositor))
            {
                if (existing.GraphicsDevice is null)
                {
                    var recreated = TryCreateCompositionGraphicsDevice(compositor, t_d3dDevice);
                    if (recreated is null)
                    {
                        return null;
                    }

                    existing.ResetGraphicsDevice(recreated);
                }

                return existing;
            }
        }

        var graphicsDevice = TryCreateCompositionGraphicsDevice(compositor, t_d3dDevice);
        if (graphicsDevice is null)
        {
            return null;
        }

        var createdCache = new CompositorMaskCache(compositor, graphicsDevice);
        caches.Add(createdCache);
        return createdCache;
    }

    private static Windows.UI.Composition.CompositionGraphicsDevice? TryCreateCompositionGraphicsDevice(
        Windows.UI.Composition.Compositor compositor,
        IntPtr d3dDevice)
    {
        IntPtr compUnk = ((IWinRTObject)compositor).NativeObject.ThisPtr;
        int qiHr = Marshal.QueryInterface(compUnk, in s_iidCompositorInterop, out IntPtr compInterop);
        if (qiHr < 0 || compInterop == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            IntPtr* vtbl = *(IntPtr**)compInterop;
            var createGraphicsDevice =
                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, out IntPtr, int>)vtbl[5];
            int hr = createGraphicsDevice(compInterop, d3dDevice, out IntPtr gdPtr);
            if (hr < 0 || gdPtr == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return MarshalInterface<Windows.UI.Composition.CompositionGraphicsDevice>.FromAbi(gdPtr);
            }
            finally
            {
                Marshal.Release(gdPtr);
            }
        }
        finally
        {
            Marshal.Release(compInterop);
        }
    }

    private static int UploadRoundedMaskPixels(
        Windows.UI.Composition.CompositionDrawingSurface surface,
        int texSize,
        double radiusPx)
    {
        byte[] pixels = new byte[texSize * texSize * 4];
        for (int y = 0; y < texSize; y++)
        {
            int rowOffset = y * texSize * 4;
            for (int x = 0; x < texSize; x++)
            {
                byte coverage = ComputeCornerCoverageByte(x, y, texSize, radiusPx);
                int idx = rowOffset + (x * 4);
                pixels[idx] = coverage;
                pixels[idx + 1] = coverage;
                pixels[idx + 2] = coverage;
                pixels[idx + 3] = coverage;
            }
        }

        IntPtr surfUnk = ((IWinRTObject)surface).NativeObject.ThisPtr;
        int qiHr = Marshal.QueryInterface(
            surfUnk,
            in s_iidCompositionDrawingSurfaceInterop,
            out IntPtr surfInterop);
        if (qiHr < 0 || surfInterop == IntPtr.Zero)
        {
            return qiHr < 0 ? qiHr : -1;
        }

        try
        {
            IntPtr* surfVtbl = *(IntPtr**)surfInterop;
            var beginDraw =
                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, in Guid, out IntPtr, out NativePoint, int>)surfVtbl[3];
            var endDraw =
                (delegate* unmanaged[Stdcall]<IntPtr, int>)surfVtbl[4];

            Guid iidTex = s_iidD3d11Texture2D;
            int hrBegin = beginDraw(
                surfInterop,
                IntPtr.Zero,
                in iidTex,
                out IntPtr texPtr,
                out NativePoint offset);
            if (hrBegin < 0 || texPtr == IntPtr.Zero)
            {
                return hrBegin < 0 ? hrBegin : -1;
            }

            try
            {
                IntPtr* ctxVtbl = *(IntPtr**)t_d3dContext;
                var updateSubresource =
                    (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, D3d11Box*, void*, uint, uint, void>)ctxVtbl[48];
                var box = new D3d11Box
                {
                    Left = (uint)offset.X,
                    Top = (uint)offset.Y,
                    Front = 0,
                    Right = (uint)(offset.X + texSize),
                    Bottom = (uint)(offset.Y + texSize),
                    Back = 1
                };
                fixed (byte* pPixels = pixels)
                {
                    updateSubresource(
                        t_d3dContext,
                        texPtr,
                        0,
                        &box,
                        pPixels,
                        (uint)(texSize * 4),
                        (uint)(texSize * texSize * 4));
                }
            }
            finally
            {
                Marshal.Release(texPtr);
            }

            return endDraw(surfInterop);
        }
        finally
        {
            Marshal.Release(surfInterop);
        }
    }

    private static void ResetThreadD3dState()
    {
        if (t_caches is { } caches)
        {
            foreach (CompositorMaskCache cache in caches)
            {
                cache.DisposeAll();
            }

            caches.Clear();
        }

        ReleaseD3dDeviceOnly();
    }

    private static void ReleaseD3dDeviceOnly()
    {
        if (t_d3dContext != IntPtr.Zero)
        {
            Marshal.Release(t_d3dContext);
            t_d3dContext = IntPtr.Zero;
        }

        if (t_d3dDevice != IntPtr.Zero)
        {
            Marshal.Release(t_d3dDevice);
            t_d3dDevice = IntPtr.Zero;
        }
    }
}

/// <summary>
/// Intercepts the <see cref="Windows.UI.Composition.CompositionBrush"/> produced by
/// <c>MicaController</c> or <c>DesktopAcrylicController</c> and wraps it in a
/// <see cref="Windows.UI.Composition.CompositionMaskBrush"/> masked by
/// <see cref="RoundedBackdropMaskService"/> before forwarding to the underlying
/// <c>SystemBackdropElement</c> target.
/// </summary>
internal sealed partial class MaskedBackdropTargetProxy :
    ICompositionSupportsSystemBackdrop,
    IDisposable
{
    private readonly ICompositionSupportsSystemBackdrop _realTarget;
    private Windows.UI.Composition.CompositionBrush? _sourceBrush;
    private Windows.UI.Composition.CompositionBrush? _appliedTargetBrush;
    private Windows.UI.Composition.CompositionMaskBrush? _maskBrush;
    private Windows.UI.Composition.Compositor? _maskCompositor;
    private RoundedBackdropMaskService.MaskLease? _maskLease;
    private double _cornerRadiusDip;
    private double _rasterizationScale;
    private bool _disposed;

    internal MaskedBackdropTargetProxy(
        ICompositionSupportsSystemBackdrop realTarget,
        double cornerRadiusDip,
        double rasterizationScale)
    {
        _realTarget = realTarget;
        _cornerRadiusDip = Math.Max(0.0, cornerRadiusDip);
        _rasterizationScale = rasterizationScale > 0.0 ? rasterizationScale : 1.0;
    }

    public Windows.UI.Composition.CompositionBrush? SystemBackdrop
    {
        get => _sourceBrush;
        set
        {
            if (_disposed)
            {
                return;
            }

            _sourceBrush = value;
            SyncTargetBrush();
        }
    }

    internal void UpdateParameters(double cornerRadiusDip, double rasterizationScale)
    {
        if (_disposed)
        {
            return;
        }

        double clampedRadius = Math.Max(0.0, cornerRadiusDip);
        double clampedScale = rasterizationScale > 0.0 ? rasterizationScale : 1.0;
        if (Math.Abs(_cornerRadiusDip - clampedRadius) < 0.01 &&
            Math.Abs(_rasterizationScale - clampedScale) < 0.001)
        {
            return;
        }

        _cornerRadiusDip = clampedRadius;
        _rasterizationScale = clampedScale;
        if (_sourceBrush is not null)
        {
            SyncTargetBrush();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sourceBrush = null;
        ApplyBrushToRealTarget(null);
        ReleaseMaskResources();
    }

    private void SyncTargetBrush()
    {
        try
        {
            if (_sourceBrush is null)
            {
                ApplyBrushToRealTarget(null);
                if (_maskBrush is not null)
                {
                    _maskBrush.Source = null;
                }

                return;
            }

            if (_cornerRadiusDip <= 0.01)
            {
                ApplyBrushToRealTarget(_sourceBrush);
                ReleaseMaskResources();
                return;
            }

            Windows.UI.Composition.Compositor compositor = _sourceBrush.Compositor;
            if (!ReferenceEquals(_maskCompositor, compositor))
            {
                if (_maskBrush is not null && ReferenceEquals(_appliedTargetBrush, _maskBrush))
                {
                    ApplyBrushToRealTarget(_sourceBrush);
                }

                ReleaseMaskResources();
                _maskCompositor = compositor;
            }

            _maskLease = RoundedBackdropMaskService.AcquireNineGridMask(
                compositor,
                _cornerRadiusDip,
                _rasterizationScale,
                _maskLease);
            if (_maskLease is null)
            {
                ApplyBrushToRealTarget(_sourceBrush);
                return;
            }

            _maskBrush ??= compositor.CreateMaskBrush();
            _maskBrush.Source = _sourceBrush;
            _maskBrush.Mask = _maskLease.NineGridBrush;
            ApplyBrushToRealTarget(_maskBrush);
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[MaskedBackdropProxy] Falling back to raw backdrop brush: {ex.Message}");
            ApplyBrushToRealTarget(_sourceBrush);
        }
    }

    private void ApplyBrushToRealTarget(Windows.UI.Composition.CompositionBrush? brush)
    {
        if (ReferenceEquals(_appliedTargetBrush, brush))
        {
            return;
        }

        try
        {
            _realTarget.SystemBackdrop = brush;
            _appliedTargetBrush = brush;
        }
        catch
        {
            _appliedTargetBrush = null;
        }
    }

    private void ReleaseMaskResources()
    {
        if (_maskBrush is not null)
        {
            try { _maskBrush.Source = null; } catch { }
            try { _maskBrush.Mask = null; } catch { }
            try { _maskBrush.Dispose(); } catch { }
            _maskBrush = null;
        }

        if (_maskLease is not null)
        {
            _maskLease.Dispose();
            _maskLease = null;
        }

        _maskCompositor = null;
    }
}
