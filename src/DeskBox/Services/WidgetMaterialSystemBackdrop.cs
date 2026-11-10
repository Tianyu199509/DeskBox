using System.Runtime.InteropServices;
using DeskBox.Platform;
using static DeskBox.Platform.MonitorAndBackdropInterop;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT;

namespace DeskBox.Services;

/// <summary>
/// Appearance inputs shared by transient widget-owned material surfaces.
/// The material type is expected to have already passed through the Windows
/// compatibility resolver used by the owning widget window.
/// </summary>
internal readonly record struct WidgetMaterialBackdropAppearance(
    string MaterialType,
    bool IsDark,
    Windows.UI.Color AccentColor,
    double SurfaceOpacity,
    double MaterialIntensity);

/// <summary>
/// AOT-safe native solid/transparent tint <see cref="SystemBackdrop"/> backed by a
/// <see cref="Windows.UI.Composition.CompositionColorBrush"/> on the window target.
/// </summary>
internal sealed partial class SolidTintSystemBackdrop : SystemBackdrop, IDisposable
{
    private const int DqTypeThreadCurrent = 2;
    private const int DqtatComSta = 2;

    [ThreadStatic]
    private static IntPtr t_dispatcherQueueController;

    [ThreadStatic]
    private static Windows.UI.Composition.Compositor? t_compositor;

    private Windows.UI.Color _tintColor;
    private ICompositionSupportsSystemBackdrop? _target;
    private Windows.UI.Composition.CompositionColorBrush? _brush;
    private IntPtr _hwnd;
    private bool _isDisposed;

    internal SolidTintSystemBackdrop(Windows.UI.Color tintColor)
    {
        _tintColor = tintColor;
    }

    internal Windows.UI.Color TintColor
    {
        get => _tintColor;
        set
        {
            _tintColor = value;
            if (_brush is not null && !_isDisposed)
            {
                _brush.Color = value;
            }
        }
    }

    protected override void OnTargetConnected(
        ICompositionSupportsSystemBackdrop connectedTarget,
        XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (_isDisposed)
        {
            return;
        }

        DetachBrushAndWindowState(connectedTarget);
        _target = connectedTarget;

        EnsureWindowsSystemDispatcherQueue();
        Windows.UI.Composition.Compositor compositor =
            t_compositor ??= new Windows.UI.Composition.Compositor();
        _brush = compositor.CreateColorBrush(_tintColor);
        connectedTarget.SystemBackdrop = _brush;

        try
        {
            if (xamlRoot?.ContentIslandEnvironment is { } islandEnvironment)
            {
                _hwnd = Win32Interop.GetWindowFromWindowId(islandEnvironment.AppWindowId);
                if (_hwnd != IntPtr.Zero)
                {
                    Win32Helper.EnableTransparentWindowHost(_hwnd);
                }
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[SolidTintBackdrop] Window frame alpha setup failed: {ex.Message}");
        }
    }

    protected override void OnTargetDisconnected(
        ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        try
        {
            base.OnTargetDisconnected(disconnectedTarget);
        }
        finally
        {
            DetachBrushAndWindowState(disconnectedTarget);
            if (ReferenceEquals(_target, disconnectedTarget))
            {
                _target = null;
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        DetachBrushAndWindowState(_target);
        _target = null;
    }

    private void DetachBrushAndWindowState(ICompositionSupportsSystemBackdrop? target)
    {
        if (target is not null)
        {
            try
            {
                if (_brush is null || ReferenceEquals(target.SystemBackdrop, _brush))
                {
                    target.SystemBackdrop = null;
                }
            }
            catch
            {
            }
        }

        if (_brush is not null)
        {
            try
            {
                _brush.Dispose();
            }
            catch
            {
            }
            finally
            {
                _brush = null;
            }
        }

        if (_hwnd != IntPtr.Zero)
        {
            Win32Helper.DisableTransparentWindowHost(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private static void EnsureWindowsSystemDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null ||
            t_dispatcherQueueController != IntPtr.Zero)
        {
            return;
        }

        var options = new DispatcherQueueOptions
        {
            dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = DqTypeThreadCurrent,
            apartmentType = DqtatComSta
        };
        _ = CreateDispatcherQueueController(options, out t_dispatcherQueueController);
    }
}

/// <summary>
/// Bridges a <see cref="Microsoft.UI.Xaml.Controls.SystemBackdropElement"/>'s
/// <see cref="ICompositionSupportsSystemBackdrop"/> target to external shared
/// or window-owned backdrop controllers via a smooth 256-level anti-aliased
/// <see cref="MaskedBackdropTargetProxy"/>, and disposes the thread-affine native
/// target link on the UI thread when XAML disconnects the element.
/// </summary>
internal sealed partial class ElementBackdropTargetBridge : SystemBackdrop, IDisposable
{
    private readonly Action? _onTargetChanged;
    private readonly Action<ICompositionSupportsSystemBackdrop>? _onTargetDisconnecting;
    private ICompositionSupportsSystemBackdrop? _rawTarget;
    private MaskedBackdropTargetProxy? _maskedProxy;
    private XamlRoot? _xamlRoot;
    private SystemBackdropConfiguration? _configuration;
    private double _cornerRadiusDip;
    private double _rasterizationScale = 1.0;
    private bool _isDisposed;

    internal ElementBackdropTargetBridge(
        Action? onTargetChanged = null,
        Action<ICompositionSupportsSystemBackdrop>? onTargetDisconnecting = null)
    {
        _onTargetChanged = onTargetChanged;
        _onTargetDisconnecting = onTargetDisconnecting;
    }

    internal ICompositionSupportsSystemBackdrop? Target => _isDisposed ? null : _maskedProxy;

    internal SystemBackdropConfiguration? Configuration => _isDisposed ? null : _configuration;

    internal void SetCornerRadius(double cornerRadiusDip)
    {
        _cornerRadiusDip = Math.Max(0.0, cornerRadiusDip);
        _maskedProxy?.UpdateParameters(_cornerRadiusDip, _rasterizationScale);
    }

    protected override void OnTargetConnected(
        ICompositionSupportsSystemBackdrop connectedTarget,
        XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (_isDisposed)
        {
            return;
        }

        DetachXamlRoot();
        _maskedProxy?.Dispose();

        _rawTarget = connectedTarget;
        AttachXamlRoot(xamlRoot);
        _maskedProxy = new MaskedBackdropTargetProxy(
            connectedTarget,
            _cornerRadiusDip,
            _rasterizationScale);
        _configuration = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
        _onTargetChanged?.Invoke();
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(
        ICompositionSupportsSystemBackdrop target,
        XamlRoot xamlRoot)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
        if (_isDisposed)
        {
            return;
        }

        if (xamlRoot is { RasterizationScale: > 0 } validRoot)
        {
            _rasterizationScale = validRoot.RasterizationScale;
            _maskedProxy?.UpdateParameters(_cornerRadiusDip, _rasterizationScale);
        }

        // Window focus transitions flip IsInputActive on the default
        // configuration without replacing the composition target proxy;
        // shared controllers keep their own always-active configuration, so
        // only target connect/disconnect should notify _onTargetChanged.
        _configuration = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
    }

    protected override void OnTargetDisconnected(
        ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        try
        {
            base.OnTargetDisconnected(disconnectedTarget);
        }
        finally
        {
            bool wasCurrent = ReferenceEquals(_rawTarget, disconnectedTarget);
            if (wasCurrent)
            {
                DetachXamlRoot();
                if (_maskedProxy is { } activeProxy)
                {
                    _onTargetDisconnecting?.Invoke(activeProxy);
                    activeProxy.Dispose();
                    _maskedProxy = null;
                }

                _rawTarget = null;
                _configuration = null;
            }

            try
            {
                disconnectedTarget.As<IDisposable>().Dispose();
            }
            catch (Exception ex)
            {
                App.Log($"[ElementBackdropBridge] Element target close failed: {ex}");
            }

            if (wasCurrent && !_isDisposed)
            {
                _onTargetChanged?.Invoke();
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        DetachXamlRoot();
        if (_maskedProxy is { } activeProxy)
        {
            _onTargetDisconnecting?.Invoke(activeProxy);
            activeProxy.Dispose();
            _maskedProxy = null;
        }

        _rawTarget = null;
        _configuration = null;
    }

    private void AttachXamlRoot(XamlRoot? xamlRoot)
    {
        _xamlRoot = xamlRoot;
        if (xamlRoot is not null)
        {
            if (xamlRoot.RasterizationScale > 0)
            {
                _rasterizationScale = xamlRoot.RasterizationScale;
            }

            xamlRoot.Changed += XamlRoot_Changed;
        }
    }

    private void DetachXamlRoot()
    {
        if (_xamlRoot is not null)
        {
            _xamlRoot.Changed -= XamlRoot_Changed;
            _xamlRoot = null;
        }
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_isDisposed || sender.RasterizationScale <= 0)
        {
            return;
        }

        _rasterizationScale = sender.RasterizationScale;
        _maskedProxy?.UpdateParameters(_cornerRadiusDip, _rasterizationScale);
    }
}

/// <summary>
/// A configurable system backdrop for transient widget surfaces. The built-in
/// MicaBackdrop and DesktopAcrylicBackdrop types do not expose their material
/// strength, so this controller-backed implementation uses the same visual
/// calculator as the owning widget window.
/// </summary>
internal sealed partial class WidgetMaterialSystemBackdrop : SystemBackdrop, IDisposable
{
    private WidgetMaterialBackdropAppearance _appearance;
    private ICompositionSupportsSystemBackdrop? _rawTarget;
    private MaskedBackdropTargetProxy? _maskedProxy;
    private XamlRoot? _xamlRoot;
    private SystemBackdropConfiguration? _configuration;
    private MicaController? _micaController;
    private DesktopAcrylicController? _acrylicController;
    private double _cornerRadiusDip;
    private double _rasterizationScale = 1.0;
    private bool _isSuspended;
    private bool _isDisposed;
    private readonly bool _closeElementTargetOnDisconnect;

    internal WidgetMaterialSystemBackdrop(
        WidgetMaterialBackdropAppearance appearance,
        bool closeElementTargetOnDisconnect = false)
    {
        _appearance = appearance;
        _closeElementTargetOnDisconnect = closeElementTargetOnDisconnect;
    }

    internal static bool IsSupported(string materialType) =>
        SettingsService.IsMicaMaterial(materialType)
            ? WindowsCompatibilityService.SupportsMica
            : SettingsService.IsAcrylicMaterial(materialType) &&
              WindowsCompatibilityService.SupportsDesktopAcrylic;

    internal void SetCornerRadius(double cornerRadiusDip)
    {
        _cornerRadiusDip = Math.Max(0.0, cornerRadiusDip);
        _maskedProxy?.UpdateParameters(_cornerRadiusDip, _rasterizationScale);
    }

    internal void UpdateAppearance(
        WidgetMaterialBackdropAppearance appearance)
    {
        if (_isDisposed || (_appearance == appearance && !_isSuspended))
        {
            return;
        }

        _appearance = appearance;
        _isSuspended = false;
        if (_maskedProxy is not null && _configuration is not null)
        {
            ApplyController();
        }
    }

    protected override void OnTargetConnected(
        ICompositionSupportsSystemBackdrop connectedTarget,
        XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        DisposeControllers();
        DetachXamlRoot();
        _maskedProxy?.Dispose();

        _rawTarget = connectedTarget;
        AttachXamlRoot(xamlRoot);
        _maskedProxy = new MaskedBackdropTargetProxy(
            connectedTarget,
            _cornerRadiusDip,
            _rasterizationScale);
        _configuration = GetDefaultSystemBackdropConfiguration(
            connectedTarget,
            xamlRoot);
        ApplyController();
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(
        ICompositionSupportsSystemBackdrop target,
        XamlRoot xamlRoot)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
        if (xamlRoot is { RasterizationScale: > 0 } validRoot)
        {
            _rasterizationScale = validRoot.RasterizationScale;
            _maskedProxy?.UpdateParameters(_cornerRadiusDip, _rasterizationScale);
        }

        _configuration = GetDefaultSystemBackdropConfiguration(
            target,
            xamlRoot);
        ApplyConfiguration();
    }

    protected override void OnTargetDisconnected(
        ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        try
        {
            base.OnTargetDisconnected(disconnectedTarget);
        }
        finally
        {
            DisposeControllers();
            DetachXamlRoot();
            if (ReferenceEquals(_rawTarget, disconnectedTarget))
            {
                _maskedProxy?.Dispose();
                _maskedProxy = null;
                _rawTarget = null;
                _configuration = null;
            }

            // SystemBackdropElement drops its native backdrop link after this
            // callback. Close it on its owning UI thread before C#/WinRT releases
            // the remaining projection references from the finalizer thread.
            // Window targets are owned by their windows and must not be closed here.
            if (_closeElementTargetOnDisconnect)
            {
                try { disconnectedTarget.As<IDisposable>().Dispose(); }
                catch (Exception ex)
                {
                    App.Log($"[WidgetMaterialBackdrop] Element target close failed: {ex}");
                }
            }
        }
    }

    // SystemBackdropElement targets wrap thread-affine native links. Keep the
    // projection rooted until XAML disconnects the element itself; clearing the
    // element's SystemBackdrop during teardown can leave that link to finalization.
    internal void Suspend()
    {
        _isSuspended = true;
        DisposeControllers();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Suspend();
        DetachXamlRoot();
        _maskedProxy?.Dispose();
        _maskedProxy = null;
        _rawTarget = null;
    }

    private void AttachXamlRoot(XamlRoot? xamlRoot)
    {
        _xamlRoot = xamlRoot;
        if (xamlRoot is not null)
        {
            if (xamlRoot.RasterizationScale > 0)
            {
                _rasterizationScale = xamlRoot.RasterizationScale;
            }

            xamlRoot.Changed += XamlRoot_Changed;
        }
    }

    private void DetachXamlRoot()
    {
        if (_xamlRoot is not null)
        {
            _xamlRoot.Changed -= XamlRoot_Changed;
            _xamlRoot = null;
        }
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_isDisposed || sender.RasterizationScale <= 0)
        {
            return;
        }

        _rasterizationScale = sender.RasterizationScale;
        _maskedProxy?.UpdateParameters(_cornerRadiusDip, _rasterizationScale);
    }

    private void ApplyController()
    {
        if (_isDisposed || _isSuspended) return;
        try
        {
            if (SettingsService.IsMicaMaterial(_appearance.MaterialType))
            {
                ApplyMicaController();
                return;
            }

            if (SettingsService.IsAcrylicMaterial(_appearance.MaterialType))
            {
                ApplyAcrylicController();
                return;
            }

            DisposeControllers();
        }
        catch (Exception ex)
        {
            App.LogVerbose(
                $"[WidgetMaterialBackdrop] Could not apply " +
                $"{_appearance.MaterialType}: {ex.Message}");
            DisposeControllers();
        }
    }

    private void ApplyMicaController()
    {
        if (_maskedProxy is null ||
            _configuration is null ||
            !WindowsCompatibilityService.SupportsMica)
        {
            DisposeControllers();
            return;
        }

        DisposeAcrylicController();
        bool useAlt = _appearance.MaterialType ==
            SettingsService.WidgetMaterialTypeMicaAlt;
        if (_micaController is null)
        {
            _micaController = new MicaController();
            _micaController.SetSystemBackdropConfiguration(_configuration);
            if (!_micaController.AddSystemBackdropTarget(_maskedProxy))
            {
                throw new InvalidOperationException(
                    "Mica backdrop target registration failed.");
            }
        }

        _micaController.Kind = useAlt ? MicaKind.BaseAlt : MicaKind.Base;
        _micaController.TintColor =
            WidgetMaterialVisualCalculator.BuildContentTintColor(
                _appearance.IsDark,
                _appearance.AccentColor);
        _micaController.FallbackColor =
            WidgetMaterialVisualCalculator.BuildMicaFallbackColor(
                _appearance.IsDark,
                useAlt);
        WidgetMaterialOpacityProfile profile =
            WidgetMaterialVisualCalculator.CalculateMica(
                _appearance.IsDark,
                useAlt,
                _appearance.MaterialIntensity);
        _micaController.TintOpacity = (float)profile.TintOpacity;
        _micaController.LuminosityOpacity =
            (float)profile.LuminosityOpacity;
    }

    private void ApplyAcrylicController()
    {
        if (_maskedProxy is null ||
            _configuration is null ||
            !WindowsCompatibilityService.SupportsDesktopAcrylic)
        {
            DisposeControllers();
            return;
        }

        DisposeMicaController();
        bool useBase = _appearance.MaterialType ==
            SettingsService.WidgetMaterialTypeAcrylicBase;
        if (_acrylicController is null || _acrylicController.IsClosed)
        {
            _acrylicController = new DesktopAcrylicController();
            _acrylicController.SetSystemBackdropConfiguration(_configuration);
            if (!_acrylicController.AddSystemBackdropTarget(_maskedProxy))
            {
                throw new InvalidOperationException(
                    "Acrylic backdrop target registration failed.");
            }
        }

        _acrylicController.Kind = useBase
            ? DesktopAcrylicKind.Base
            : DesktopAcrylicKind.Thin;
        Windows.UI.Color tintColor =
            WidgetMaterialVisualCalculator.BuildContentTintColor(
                _appearance.IsDark,
                _appearance.AccentColor);
        _acrylicController.TintColor = tintColor;
        _acrylicController.FallbackColor = tintColor;
        WidgetMaterialOpacityProfile profile =
            WidgetMaterialVisualCalculator.CalculateAcrylic(
                _appearance.IsDark,
                useBase,
                _appearance.SurfaceOpacity,
                _appearance.MaterialIntensity);
        _acrylicController.TintOpacity = (float)profile.TintOpacity;
        _acrylicController.LuminosityOpacity =
            (float)profile.LuminosityOpacity;
    }

    private void ApplyConfiguration()
    {
        if (_configuration is null || _isDisposed || _isSuspended)
        {
            return;
        }

        _micaController?.SetSystemBackdropConfiguration(_configuration);
        if (_acrylicController is { IsClosed: false })
        {
            _acrylicController.SetSystemBackdropConfiguration(_configuration);
        }
    }

    private void DisposeControllers()
    {
        DisposeMicaController();
        DisposeAcrylicController();
    }

    private void DisposeMicaController()
    {
        if (_micaController is null)
        {
            return;
        }

        try
        {
            _micaController.RemoveAllSystemBackdropTargets();
            _micaController.Dispose();
        }
        catch
        {
        }
        finally
        {
            _micaController = null;
        }
    }

    private void DisposeAcrylicController()
    {
        if (_acrylicController is null)
        {
            return;
        }

        try
        {
            _acrylicController.RemoveAllSystemBackdropTargets();
            _acrylicController.Dispose();
        }
        catch
        {
        }
        finally
        {
            _acrylicController = null;
        }
    }
}
