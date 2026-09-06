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
/// A configurable system backdrop for transient widget surfaces. The built-in
/// MicaBackdrop and DesktopAcrylicBackdrop types do not expose their material
/// strength, so this controller-backed implementation uses the same visual
/// calculator as the owning widget window.
/// </summary>
internal sealed partial class WidgetMaterialSystemBackdrop : SystemBackdrop, IDisposable
{
    private WidgetMaterialBackdropAppearance _appearance;
    private ICompositionSupportsSystemBackdrop? _target;
    private SystemBackdropConfiguration? _configuration;
    private MicaController? _micaController;
    private DesktopAcrylicController? _acrylicController;
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

    internal void UpdateAppearance(
        WidgetMaterialBackdropAppearance appearance)
    {
        if (_isDisposed || (_appearance == appearance && !_isSuspended))
        {
            return;
        }

        _appearance = appearance;
        _isSuspended = false;
        if (_target is not null && _configuration is not null)
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
        _target = connectedTarget;
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
            _target = null;
            _configuration = null;
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
        if (_target is null ||
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
            if (!_micaController.AddSystemBackdropTarget(_target))
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
        if (_target is null ||
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
            if (!_acrylicController.AddSystemBackdropTarget(_target))
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
