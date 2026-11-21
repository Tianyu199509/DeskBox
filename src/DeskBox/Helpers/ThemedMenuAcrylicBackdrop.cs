// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Color = Windows.UI.Color;

namespace DeskBox.Helpers;

/// <summary>
/// Theme-accurate <see cref="DesktopAcrylicController"/> backdrop for WinUI 3
/// <see cref="MenuFlyoutPresenter"/> and <see cref="CommandBarFlyoutCommandBar"/>
/// popups.
/// <para>
/// WinUI 3's built-in <see cref="DesktopAcrylicBackdrop"/> delegates theme
/// resolution to <c>GetDefaultSystemBackdropConfiguration</c>, which for a
/// flyout <see cref="Popup"/> reads <c>Popup.ActualTheme</c> under
/// <c>PopupRoot</c>. Because <c>FlyoutBase</c> only stamps
/// <see cref="FrameworkElement.RequestedTheme"/> on the presenter child while
/// leaving <c>Popup.RequestedTheme == ElementTheme.Default</c>, <c>PopupRoot</c>
/// stays pinned to <see cref="Application.RequestedTheme"/> (the startup OS
/// theme). Whenever DeskBox's theme override disagrees with the OS theme (for
/// example Light mode in DeskBox on a Dark Windows desktop), built-in
/// <see cref="DesktopAcrylicBackdrop"/> paints dark <c>#2C2C2C</c> acrylic
/// behind black Light-mode menu text, or light <c>#FCFCFC</c> acrylic behind
/// white Dark-mode menu text.
/// </para>
/// <para>
/// One shared instance inside an implicit <see cref="Style"/> may be connected
/// to multiple simultaneous targets (a root menu plus open submenus), so each
/// connected target owns an isolated <see cref="TargetSession"/>.
/// </para>
/// </summary>
public sealed partial class ThemedMenuAcrylicBackdrop : SystemBackdrop
{
    private static readonly Color LightFallbackColor = Color.FromArgb(0xFF, 0xF9, 0xF9, 0xF9);
    private static readonly Color DarkFallbackColor = Color.FromArgb(0xFF, 0x2C, 0x2C, 0x2C);

    private readonly Dictionary<ICompositionSupportsSystemBackdrop, TargetSession> _sessions = new();

    public ThemedMenuAcrylicBackdrop()
    {
    }

    /// <summary>
    /// Pure theme resolution contract for transient menu/command-bar popups.
    /// Explicit presenter or popup overrides win first (supporting local scopes
    /// such as Studio stage preview buttons), followed by the owning window
    /// root (<c>XamlRoot.Content</c>) before any unrequested presenter
    /// <c>ActualTheme</c> (which could otherwise reflect <c>PopupRoot</c>'s OS
    /// theme), then the application's effective theme, and finally OS dark mode.
    /// </summary>
    internal static bool ResolveIsDarkTheme(
        ElementTheme presenterRequestedTheme,
        ElementTheme popupRequestedTheme,
        ElementTheme rootRequestedTheme,
        ElementTheme rootActualTheme,
        ElementTheme presenterActualTheme,
        bool presenterIsLoaded,
        ElementTheme appEffectiveTheme,
        bool systemIsDark)
    {
        if (presenterRequestedTheme is ElementTheme.Dark or ElementTheme.Light)
        {
            return presenterRequestedTheme == ElementTheme.Dark;
        }

        if (popupRequestedTheme is ElementTheme.Dark or ElementTheme.Light)
        {
            return popupRequestedTheme == ElementTheme.Dark;
        }

        if (rootRequestedTheme is ElementTheme.Dark or ElementTheme.Light)
        {
            return rootRequestedTheme == ElementTheme.Dark;
        }

        if (rootActualTheme is ElementTheme.Dark or ElementTheme.Light)
        {
            return rootActualTheme == ElementTheme.Dark;
        }

        if (presenterIsLoaded &&
            presenterActualTheme is ElementTheme.Dark or ElementTheme.Light)
        {
            return presenterActualTheme == ElementTheme.Dark;
        }

        if (appEffectiveTheme is ElementTheme.Dark or ElementTheme.Light)
        {
            return appEffectiveTheme == ElementTheme.Dark;
        }

        return systemIsDark;
    }

    protected override void OnTargetConnected(
        ICompositionSupportsSystemBackdrop connectedTarget,
        XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        if (_sessions.Remove(connectedTarget, out TargetSession? existing))
        {
            existing.Dispose();
        }

        HashSet<IntPtr> preExistingBridges = CaptureVisiblePopupBridges();
        var session = new TargetSession(this, connectedTarget, xamlRoot, preExistingBridges);
        _sessions[connectedTarget] = session;
        session.Initialize();
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(
        ICompositionSupportsSystemBackdrop target,
        XamlRoot xamlRoot)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);

        if (_sessions.TryGetValue(target, out TargetSession? session))
        {
            session.RefreshTheme(xamlRoot);
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
            if (_sessions.Remove(disconnectedTarget, out TargetSession? session))
            {
                session.Dispose();
            }
        }
    }

    private bool IsPopupClaimedByOtherSession(Popup candidate, TargetSession requester)
    {
        foreach (TargetSession session in _sessions.Values)
        {
            if (!ReferenceEquals(session, requester) &&
                ReferenceEquals(session.Popup, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<IntPtr> CaptureVisiblePopupBridges()
    {
        var snapshot = new HashSet<IntPtr>();
        try
        {
            foreach (IntPtr hwnd in Win32Helper.GetVisiblePopupBridgeWindows())
            {
                if (hwnd != IntPtr.Zero)
                {
                    snapshot.Add(hwnd);
                }
            }
        }
        catch
        {
            // Best-effort snapshot for DWM popup frame theming.
        }

        return snapshot;
    }

    private sealed class TargetSession : IDisposable
    {
        private readonly ThemedMenuAcrylicBackdrop _owner;
        private readonly ICompositionSupportsSystemBackdrop _target;
        private readonly HashSet<IntPtr> _preExistingBridges;
        private readonly HashSet<IntPtr> _ownedBridges = new();
        private XamlRoot? _xamlRoot;
        private FrameworkElement? _rootContent;
        private FrameworkElement? _presenter;
        private SystemBackdropConfiguration? _configuration;
        private DesktopAcrylicController? _acrylicController;
        private bool _appliedSolidFallback;
        private bool _isApplying;
        private bool _isDisposed;

        public TargetSession(
            ThemedMenuAcrylicBackdrop owner,
            ICompositionSupportsSystemBackdrop target,
            XamlRoot? xamlRoot,
            HashSet<IntPtr> preExistingBridges)
        {
            _owner = owner;
            _target = target;
            _xamlRoot = xamlRoot;
            _preExistingBridges = preExistingBridges;
        }

        public Popup? Popup { get; private set; }

        public void Initialize()
        {
            if (_isDisposed)
            {
                return;
            }

            AttachRootContent(_xamlRoot?.Content as FrameworkElement);
            TryBindOpenPopup();
            ApplyMaterialAndTheme();

            if (DispatcherQueue.GetForCurrentThread() is { } queue)
            {
                _ = queue.TryEnqueue(() =>
                {
                    if (_isDisposed)
                    {
                        return;
                    }

                    if (Popup is null || _presenter is null)
                    {
                        TryBindOpenPopup();
                    }

                    ApplyMaterialAndTheme();
                });
            }
        }

        public void RefreshTheme(XamlRoot? xamlRoot)
        {
            if (_isDisposed)
            {
                return;
            }

            if (xamlRoot is not null && !ReferenceEquals(_xamlRoot, xamlRoot))
            {
                _xamlRoot = xamlRoot;
                AttachRootContent(xamlRoot.Content as FrameworkElement);
            }

            if (Popup is null || _presenter is null)
            {
                TryBindOpenPopup();
            }

            ApplyMaterialAndTheme();
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            DetachPresenter();
            DetachRootContent();
            DisposeController();
            Popup = null;
            _xamlRoot = null;
            _configuration = null;
            _ownedBridges.Clear();
        }

        private void AttachRootContent(FrameworkElement? rootContent)
        {
            if (ReferenceEquals(_rootContent, rootContent))
            {
                return;
            }

            DetachRootContent();
            _rootContent = rootContent;
            if (_rootContent is not null)
            {
                _rootContent.ActualThemeChanged += OnTrackedThemeChanged;
            }
        }

        private void DetachRootContent()
        {
            if (_rootContent is not null)
            {
                _rootContent.ActualThemeChanged -= OnTrackedThemeChanged;
                _rootContent = null;
            }
        }

        private void TryBindOpenPopup()
        {
            if (_xamlRoot is null)
            {
                return;
            }

            IReadOnlyList<Popup> openPopups;
            try
            {
                openPopups = VisualTreeHelper.GetOpenPopupsForXamlRoot(_xamlRoot);
            }
            catch
            {
                return;
            }

            foreach (Popup candidate in openPopups)
            {
                if (_owner.IsPopupClaimedByOtherSession(candidate, this))
                {
                    continue;
                }

                Popup = candidate;
                AttachPresenter(candidate.Child as FrameworkElement);
                break;
            }
        }

        private void AttachPresenter(FrameworkElement? presenter)
        {
            if (ReferenceEquals(_presenter, presenter))
            {
                return;
            }

            DetachPresenter();
            _presenter = presenter;
            if (_presenter is not null)
            {
                _presenter.ActualThemeChanged += OnTrackedThemeChanged;
                _presenter.Loaded += OnPresenterLoaded;
            }
        }

        private void DetachPresenter()
        {
            if (_presenter is not null)
            {
                _presenter.ActualThemeChanged -= OnTrackedThemeChanged;
                _presenter.Loaded -= OnPresenterLoaded;
                _presenter = null;
            }
        }

        private void OnPresenterLoaded(object sender, RoutedEventArgs e)
        {
            if (!_isDisposed)
            {
                ApplyMaterialAndTheme();
            }
        }

        private void OnTrackedThemeChanged(FrameworkElement sender, object args)
        {
            if (!_isDisposed)
            {
                ApplyMaterialAndTheme();
            }
        }

        private bool ResolveCurrentIsDark()
        {
            ElementTheme presenterRequested = _presenter?.RequestedTheme ?? ElementTheme.Default;
            ElementTheme popupRequested = Popup?.RequestedTheme ?? ElementTheme.Default;
            ElementTheme rootRequested = _rootContent?.RequestedTheme ?? ElementTheme.Default;
            ElementTheme rootActual = _rootContent?.ActualTheme ?? ElementTheme.Default;
            ElementTheme presenterActual = _presenter?.ActualTheme ?? ElementTheme.Default;
            bool presenterLoaded = _presenter?.IsLoaded == true;
            ElementTheme appEffective = App.Current?.ThemeService?.EffectiveTheme ?? ElementTheme.Default;
            bool systemIsDark = Win32Helper.IsSystemDarkMode();

            return ResolveIsDarkTheme(
                presenterRequested,
                popupRequested,
                rootRequested,
                rootActual,
                presenterActual,
                presenterLoaded,
                appEffective,
                systemIsDark);
        }

        private void ApplyMaterialAndTheme()
        {
            if (_isDisposed || _isApplying)
            {
                return;
            }

            _isApplying = true;
            try
            {
                if (WindowsCompatibilityService.IsHighContrast)
                {
                    DisposeController();
                    ClearSolidFallbackIfApplied();
                    return;
                }

                bool isDark = ResolveCurrentIsDark();
                SyncPopupBridgeWindowTheme(isDark);

                if (!WindowsCompatibilityService.SupportsDesktopAcrylic)
                {
                    DisposeController();
                    ApplySolidFallback(isDark);
                    return;
                }

                try
                {
                    _configuration ??= new SystemBackdropConfiguration
                    {
                        IsInputActive = true,
                    };
                    _configuration.IsInputActive = true;
                    _configuration.Theme = isDark
                        ? SystemBackdropTheme.Dark
                        : SystemBackdropTheme.Light;

                    if (_acrylicController is null || _acrylicController.IsClosed)
                    {
                        DisposeController();
                        var controller = new DesktopAcrylicController();
                        controller.SetSystemBackdropConfiguration(_configuration);
                        if (!controller.AddSystemBackdropTarget(_target))
                        {
                            controller.Dispose();
                            ApplySolidFallback(isDark);
                            return;
                        }

                        _acrylicController = controller;
                    }
                    else
                    {
                        _acrylicController.SetSystemBackdropConfiguration(_configuration);
                    }

                    ClearSolidFallbackIfApplied();
                }
                catch (Exception ex)
                {
                    App.LogVerbose($"[MenuBackdrop] Acrylic controller fallback: {ex.Message}");
                    DisposeController();
                    ApplySolidFallback(isDark);
                }
            }
            finally
            {
                _isApplying = false;
            }
        }

        private void SyncPopupBridgeWindowTheme(bool isDark)
        {
            try
            {
                foreach (IntPtr hwnd in Win32Helper.GetVisiblePopupBridgeWindows())
                {
                    if (hwnd == IntPtr.Zero)
                    {
                        continue;
                    }

                    if (!_preExistingBridges.Contains(hwnd))
                    {
                        _ownedBridges.Add(hwnd);
                    }

                    if (_ownedBridges.Contains(hwnd))
                    {
                        Win32Helper.SetWindowTheme(hwnd, isDark);
                    }
                }
            }
            catch
            {
                // Best-effort DWM border theme sync on PopupWindowSiteBridge.
            }
        }

        private void ApplySolidFallback(bool isDark)
        {
            if (_presenter is Control control)
            {
                control.Background = new SolidColorBrush(isDark ? DarkFallbackColor : LightFallbackColor);
                _appliedSolidFallback = true;
            }
        }

        private void ClearSolidFallbackIfApplied()
        {
            if (_appliedSolidFallback && _presenter is Control control)
            {
                control.ClearValue(Control.BackgroundProperty);
                _appliedSolidFallback = false;
            }
        }

        private void DisposeController()
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
                // Ignore teardown races when popup closes natively.
            }
            finally
            {
                _acrylicController = null;
            }
        }
    }
}
