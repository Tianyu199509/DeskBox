using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using DeskBox.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace DeskBox.Helpers;
/// <summary>
/// Soft widget text shadow drawn by the compositor.
///
/// TextBlocks opt in through the <see cref="CastProperty"/> attached
/// property, which an application-level implicit style sets on every
/// TextBlock. It has to live there: elements expanded from data and control
/// templates (list items, forecast rows, history entries) only see implicit
/// styles from the application resources. A text only casts a shadow when it
/// loads into a window whose root carries <see cref="ScopeProperty"/> — the
/// widget windows — so feature widgets, titles, capsules, template and
/// code-built text are covered without per-site markup while every other
/// window ignores the setting.
///
/// A composition child visual always renders above its element, so the
/// shadow cannot hang on the TextBlock itself. Instead an empty, hit-test
/// invisible host element is inserted into the nearest overlay panel (Grid,
/// Canvas, RelativePanel) directly before the branch that holds the text, so
/// it renders beneath it. The shadow is a SpriteVisual on that host: a
/// DropShadow masked by the text's own glyph alpha
/// (<c>TextBlock.GetAlphaMask</c>, which stays live when the text changes),
/// with Offset, Size and Opacity bound to the hand-out visuals between the
/// text and the panel by ExpressionAnimations. Plain StackPanels, borders,
/// content presenters and controls without an opaque background are crossed
/// on the way up; anything that would scroll, transform, clip or paint an
/// opaque plate between the host and the text stops the search, and the
/// text simply goes unshadowed.
///
/// Because host and text share the panel, everything above it — list
/// scrolling, marquee transforms, reposition transitions, clips, fades and
/// visibility — carries the shadow along inside the compositor. Nothing runs
/// per frame on the UI thread, nothing walks down the tree and nothing
/// listens to layout passes; the only tree walk is a short, bounded climb
/// from a text when it loads. That is the difference from the 1.5.1 route,
/// which tracked every TextBlock from one window-level layer on the UI
/// thread (a full tree walk and a coordinate transform per layout pass),
/// could not follow scrolling or render-transform animations, and froze
/// widgets.
///
/// The shadow is the real glyph outline, so it can never wrap, trim or align
/// differently from the text the way a second TextBlock copy can. Its color
/// follows the text's actual foreground (see <see cref="TextShadowPalette"/>),
/// which covers palette modes and custom colors without extra settings.
///
/// While the setting is off or any ancestor up to the scoped window root is
/// collapsed, nothing is inserted and no composition object exists; opted-in
/// elements stay dormant until both enabled and genuinely visible, and tear
/// down their host and composition objects immediately when collapsed.
/// </summary>
public static class WidgetTextShadow
{
    // Plain layout wrappers crossed between a text and its host panel.
    private const int MaxPassThroughDepth = 4;

    // Bounded climb to the scoped window root so collapsed outer containers
    // keep descendant shadows dormant without full-tree walks.
    private const int MaxVisibilityDepth = 48;

    // Background opacity above which a crossed element counts as a plate.
    private const double MaxSeeThroughAlpha = 0.5;

    // Effective card opacity at or above which solid/tinted plates provide full
    // contrast without per-glyph DropShadow composition objects.
    public const double OpaquePlateThreshold = 0.85;

    private static readonly ConditionalWeakTable<FrameworkElement, ShadowAttachment> Attachments = new();
    private static readonly ConditionalWeakTable<FrameworkElement, DeferredTextPin> DeferredPins = new();
    private static readonly List<WeakReference<ShadowAttachment>> KnownAttachments = [];
    private static readonly List<WeakReference<FrameworkElement>> PendingTexts = [];
    private static int _pendingCompactionThreshold = 64;
    private static int _knownCompactionThreshold = 256;
    private static bool _enabled;
    private static bool _isActive;

    /// <summary>Whether the user enabled the shadow (the raw setting value).</summary>
    public static bool IsEnabled => _enabled;

    /// <summary>
    /// Applies the setting. High contrast always keeps the shadows off: on
    /// the solid high-contrast backgrounds a shadow is noise, matching the
    /// native icon-label behavior. Called on every appearance pass; only a
    /// change of the effective state touches the texts.
    /// </summary>
    public static event Action? ShadowSettingChanged;

    public static void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        bool active = enabled && !WindowsCompatibilityService.IsHighContrast;
        if (active == _isActive)
        {
            return;
        }

        _isActive = active;
        if (active)
        {
            AdoptPendingTexts();
        }

        for (int index = KnownAttachments.Count - 1; index >= 0; index--)
        {
            if (KnownAttachments[index].TryGetTarget(out ShadowAttachment? attachment))
            {
                Sync(attachment);
            }
            else
            {
                KnownAttachments.RemoveAt(index);
            }
        }

        ShadowSettingChanged?.Invoke();
    }

    /// <summary>
    /// Marks a window's root element: TextBlocks loading into that window may
    /// cast the shadow. Read once per text, through <c>XamlRoot.Content</c>.
    /// </summary>
    public static readonly DependencyProperty ScopeProperty =
        DependencyProperty.RegisterAttached(
            "Scope",
            typeof(bool),
            typeof(WidgetTextShadow),
            new PropertyMetadata(false));

    public static bool GetScope(DependencyObject obj) => (bool)obj.GetValue(ScopeProperty);

    public static void SetScope(DependencyObject obj, bool value) => obj.SetValue(ScopeProperty, value);

    /// <summary>
    /// Marks a scoped window root whose card plate is currently opaque enough
    /// that per-glyph shadows are invisible overhead. Toggling back to false
    /// adopts deferred texts and restores shadows live.
    /// </summary>
    public static readonly DependencyProperty PlateOpaqueProperty =
        DependencyProperty.RegisterAttached(
            "PlateOpaque",
            typeof(bool),
            typeof(WidgetTextShadow),
            new PropertyMetadata(false, OnPlateOpaqueChanged));

    public static bool GetPlateOpaque(DependencyObject obj) =>
        (bool)obj.GetValue(PlateOpaqueProperty);

    public static void SetPlateOpaque(DependencyObject obj, bool value) =>
        obj.SetValue(PlateOpaqueProperty, value);

    public static bool ShouldSuppressForOpaquePlate(
        double effectiveWidgetOpacity,
        string? materialType,
        bool hasImageBackground = false)
    {
        if (hasImageBackground ||
            string.Equals(materialType, "None", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return double.IsFinite(effectiveWidgetOpacity) &&
               effectiveWidgetOpacity >= OpaquePlateThreshold;
    }

    public static bool IsActiveFor(FrameworkElement element)
    {
        if (!_isActive)
        {
            return false;
        }

        if (!element.IsLoaded)
        {
            return true;
        }

        return element.XamlRoot?.Content is UIElement root &&
               GetScope(root) &&
               !GetPlateOpaque(root);
    }

    private static bool IsScopePlateOpaque(FrameworkElement text) =>
        text.IsLoaded &&
        text.XamlRoot?.Content is UIElement root &&
        GetPlateOpaque(root);

    private static void OnPlateOpaqueChanged(
        DependencyObject d,
        DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement root || !GetScope(root))
        {
            return;
        }

        bool isPlateOpaque = (bool)e.NewValue;
        if (!isPlateOpaque && _isActive)
        {
            AdoptPendingTexts();
        }

        for (int index = KnownAttachments.Count - 1; index >= 0; index--)
        {
            if (KnownAttachments[index].TryGetTarget(out ShadowAttachment? attachment))
            {
                if (!attachment.Element.IsLoaded ||
                    ReferenceEquals(attachment.Element.XamlRoot?.Content, root))
                {
                    Sync(attachment);
                }
            }
            else
            {
                KnownAttachments.RemoveAt(index);
            }
        }

        ShadowSettingChanged?.Invoke();
    }

    /// <summary>
    /// Whether a text element casts the widget text shadow. Set by the
    /// app-level implicit TextBlock style (a local <c>False</c> opts a text
    /// out) or, for elements without their own glyph mask, by the surface
    /// that owns an <see cref="Microsoft.UI.Xaml.Controls.Image"/> mirror.
    /// </summary>
    public static readonly DependencyProperty CastProperty =
        DependencyProperty.RegisterAttached(
            "Cast",
            typeof(bool),
            typeof(WidgetTextShadow),
            new PropertyMetadata(false, OnCastChanged));

    public static bool GetCast(DependencyObject obj) => (bool)obj.GetValue(CastProperty);

    public static void SetCast(DependencyObject obj, bool value) => obj.SetValue(CastProperty, value);

    /// <summary>
    /// Optional edge-color override for elements that have no Foreground of
    /// their own (the markdown mirror Image): when set, the shadow polarity
    /// follows this color instead of the element theme.
    /// </summary>
    public static readonly DependencyProperty EdgeColorProperty =
        DependencyProperty.RegisterAttached(
            "EdgeColor",
            typeof(Windows.UI.Color?),
            typeof(WidgetTextShadow),
            new PropertyMetadata(null, OnEdgeColorChanged));

    public static Windows.UI.Color? GetEdgeColor(DependencyObject obj) =>
        (Windows.UI.Color?)obj.GetValue(EdgeColorProperty);

    public static void SetEdgeColor(DependencyObject obj, Windows.UI.Color? value) =>
        obj.SetValue(EdgeColorProperty, value);

    private static void OnEdgeColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element &&
            Attachments.TryGetValue(element, out ShadowAttachment? attachment))
        {
            ApplyEdge(attachment);
        }
    }

    private static void OnCastChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not (TextBlock or Shape or Image))
        {
            return;
        }

        FrameworkElement text = (FrameworkElement)d;
        if (Attachments.TryGetValue(text, out ShadowAttachment? attachment))
        {
            Sync(attachment);
        }
        else if ((bool)e.NewValue)
        {
            if (_isActive && !IsScopePlateOpaque(text))
            {
                Adopt(text);
            }
            else
            {
                Defer(text);
            }
        }
        else
        {
            ReleaseDeferredPin(text);
        }
    }

    private static void Defer(FrameworkElement text)
    {
        if (text.IsLoaded &&
            !(text.XamlRoot?.Content is UIElement root && GetScope(root)))
        {
            return;
        }

        if (DeferredPins.TryGetValue(text, out _))
        {
            return;
        }

        var pin = new DeferredTextPin(text);
        DeferredPins.Add(text, pin);
        text.Loaded += pin.LoadedHandler;

        PendingTexts.Add(new WeakReference<FrameworkElement>(text));
        if (PendingTexts.Count < _pendingCompactionThreshold)
        {
            return;
        }

        PendingTexts.RemoveAll(ShouldPrunePendingReference);
        _pendingCompactionThreshold = Math.Max(64, PendingTexts.Count * 2);
    }

    private static bool ShouldPrunePendingReference(WeakReference<FrameworkElement> reference)
    {
        if (!reference.TryGetTarget(out FrameworkElement? text))
        {
            return true;
        }

        if (text.IsLoaded && !(text.XamlRoot?.Content is UIElement root && GetScope(root)))
        {
            ReleaseDeferredPin(text);
            return true;
        }

        return false;
    }

    private static void ReleaseDeferredPin(FrameworkElement text)
    {
        if (DeferredPins.TryGetValue(text, out DeferredTextPin? pin))
        {
            text.Loaded -= pin.LoadedHandler;
            DeferredPins.Remove(text);
        }
    }

    public static void CompactTrackedReferences()
    {
        PendingTexts.RemoveAll(ShouldPrunePendingReference);
        PendingTexts.TrimExcess();
        _pendingCompactionThreshold = Math.Max(64, PendingTexts.Count * 2);

        KnownAttachments.RemoveAll(static reference =>
            !reference.TryGetTarget(out ShadowAttachment? attachment) ||
            attachment.IsOutOfScope);
        KnownAttachments.TrimExcess();
        _knownCompactionThreshold = Math.Max(256, KnownAttachments.Count * 2);
    }

    private static void AdoptPendingTexts()
    {
        for (int index = PendingTexts.Count - 1; index >= 0; index--)
        {
            if (!PendingTexts[index].TryGetTarget(out FrameworkElement? text))
            {
                PendingTexts.RemoveAt(index);
                continue;
            }

            if (IsScopePlateOpaque(text))
            {
                continue;
            }

            if (GetCast(text) && !Attachments.TryGetValue(text, out _))
            {
                Adopt(text);
            }
            else
            {
                ReleaseDeferredPin(text);
            }

            PendingTexts.RemoveAt(index);
        }

        _pendingCompactionThreshold = Math.Max(64, PendingTexts.Count * 2);
    }

    private static void Adopt(FrameworkElement text)
    {
        ReleaseDeferredPin(text);
        var attachment = new ShadowAttachment(text);
        Attachments.AddOrUpdate(text, attachment);
        // Loaded and Unloaded can arrive out of order when an element is
        // re-parented; Sync reads IsLoaded instead of trusting the event.
        attachment.LoadedHandler = (_, _) => Sync(attachment);
        text.Loaded += attachment.LoadedHandler;
        text.Unloaded += attachment.LoadedHandler;
        KnownAttachments.Add(attachment.Weak);
        if (KnownAttachments.Count >= _knownCompactionThreshold)
        {
            KnownAttachments.RemoveAll(static reference => !reference.TryGetTarget(out _));
            _knownCompactionThreshold = Math.Max(256, KnownAttachments.Count * 2);
        }

        Sync(attachment);
    }

    private static void Sync(ShadowAttachment attachment)
    {
        FrameworkElement text = attachment.Element;
        if (attachment.IsOutOfScope)
        {
            return;
        }

        if (text.IsLoaded && !attachment.IsScopeResolved)
        {
            attachment.IsScopeResolved = true;
            if (!(text.XamlRoot?.Content is UIElement root && GetScope(root)))
            {
                // Settings, dialogs and other non-widget windows: let go of
                // the text for good instead of tracking it.
                attachment.IsOutOfScope = true;
                text.Loaded -= attachment.LoadedHandler;
                text.Unloaded -= attachment.LoadedHandler;
                UnwatchVisibility(attachment);
                Deactivate(attachment, deferred: false);
                return;
            }
        }

        bool plateOpaque = IsScopePlateOpaque(text);
        if (!_isActive || plateOpaque || !GetCast(text) || !text.IsLoaded)
        {
            UnwatchVisibility(attachment);
            Deactivate(attachment, deferred: text.IsLoaded == false);
            return;
        }

        if (!TryResolvePlacement(text, out Panel? panel, out List<UIElement>? chain))
        {
            UnwatchVisibility(attachment);
            Deactivate(attachment, deferred: false);
            return;
        }

        List<UIElement> visibilityChain = EnsureVisibilityWatch(attachment);
        if (!IsAncestorChainVisible(visibilityChain))
        {
            Deactivate(attachment, deferred: false);
            return;
        }

        Activate(attachment, panel, chain);
    }

    private static void Activate(
        ShadowAttachment attachment,
        Panel panel,
        List<UIElement> chain)
    {
        if (attachment.Sprite is not null)
        {
            if (IsHostStillInPlace(attachment))
            {
                return;
            }

            Deactivate(attachment, deferred: false);
        }

        try
        {
            if (EnsureHost(attachment, panel, chain[^1]) is not Border host)
            {
                return;
            }

            attachment.RetryAttempts = 0;
            Visual hostVisual = ElementCompositionPreview.GetElementVisual(host);
            Compositor compositor = hostVisual.Compositor;
            FrameworkElement text = attachment.Element;

            attachment.Chain = chain;
            // GetAlphaMask returns the element's own mask brush: disposing it
            // would close the element's cached mask and poison every later
            // re-activation of the same text (group switches, recycling).
            attachment.Mask = TryGetElementMask(text);
            if (attachment.Mask is null)
            {
                throw new InvalidOperationException(
                    $"No glyph mask available for {text.GetType().Name}.");
            }
            attachment.Shadow = compositor.CreateDropShadow();
            attachment.Shadow.Mask = attachment.Mask;
            attachment.Sprite = compositor.CreateSpriteVisual();
            attachment.Sprite.Shadow = attachment.Shadow;

            // XAML keeps hand-out Offset/Size/Opacity in sync with layout and
            // state, so these bindings track the text without UI-thread code.
            var offset = new StringBuilder();
            var opacity = new StringBuilder("strength");
            attachment.OffsetBinding = compositor.CreateExpressionAnimation();
            attachment.OpacityBinding = compositor.CreateExpressionAnimation();
            for (int index = 0; index < chain.Count; index++)
            {
                string name = "c" + index;
                Visual visual = ElementCompositionPreview.GetElementVisual(chain[index]);
                attachment.OffsetBinding.SetReferenceParameter(name, visual);
                attachment.OpacityBinding.SetReferenceParameter(name, visual);
                offset.Append(index == 0 ? string.Empty : " + ").Append(name).Append(".Offset");
                opacity.Append(" * ").Append(name).Append(".Opacity");
            }

            offset.Append(" - host.Offset");
            attachment.OffsetBinding.Expression = offset.ToString();
            attachment.OffsetBinding.SetReferenceParameter("host", hostVisual);
            attachment.Sprite.StartAnimation("Offset", attachment.OffsetBinding);
            attachment.OpacityBinding.Expression = opacity.ToString();
            attachment.SizeBinding = compositor.CreateExpressionAnimation("c0.Size");
            attachment.SizeBinding.SetReferenceParameter(
                "c0",
                ElementCompositionPreview.GetElementVisual(text));
            attachment.Sprite.StartAnimation("Size", attachment.SizeBinding);

            WatchText(attachment);
            ApplyEdge(attachment);
            ElementCompositionPreview.SetElementChildVisual(host, attachment.Sprite);
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[TextShadow] Shadow attach failed: {ex.Message}");
            Deactivate(attachment, deferred: false);
            // Composition objects can be momentarily closed while a reloaded
            // subtree settles (group tab switch reparents the whole page);
            // retry once on the dispatcher so a transient failure does not
            // permanently drop the shadow.
            QueueRetry(attachment);
        }
    }

    // Retries only cover transient composition states seen while a subtree
    // re-loads (the group switch moves the page between presenters); a bounded
    // count keeps a permanently failing text from churning the dispatcher.
    private const int MaxAttachRetries = 3;

    private static void QueueRetry(ShadowAttachment attachment)
    {
        if (attachment.RetryPending ||
            attachment.RetryAttempts >= MaxAttachRetries ||
            !attachment.Element.IsLoaded)
        {
            return;
        }

        attachment.RetryPending = true;
        attachment.RetryAttempts++;
        attachment.Element.DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () =>
            {
                attachment.RetryPending = false;
                Sync(attachment);
            });
    }

    /// <summary>
    /// Climbs from the text to the nearest panel that can take an extra,
    /// layout-neutral child. Every element crossed on the way must neither
    /// paint, scroll, clip nor transform, because the host sits outside it.
    /// </summary>
    private static bool TryResolvePlacement(
        FrameworkElement text,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Panel? panel,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out List<UIElement>? chain)
    {
        panel = null;
        chain = [text];
        UIElement current = text;
        for (int depth = 0; depth <= MaxPassThroughDepth; depth++)
        {
            if (VisualTreeHelper.GetParent(current) is not UIElement parent ||
                IsExcludedOwner(parent))
            {
                return false;
            }

            if (parent is Panel candidate && IsHostPanel(candidate))
            {
                if (IsInsideExcludedOwner(candidate))
                {
                    return false;
                }

                panel = candidate;
                return true;
            }

            if (!IsPassThrough(parent))
            {
                return false;
            }

            chain.Add(parent);
            current = parent;
        }

        return false;
    }

    private static bool IsHostPanel(Panel panel) =>
        !panel.IsItemsHost &&
        panel is not SwapChainPanel &&
        panel is Grid or Canvas or RelativePanel;

    private static bool IsPassThrough(UIElement element)
    {
        if (!IsUntransformed(element.RenderTransform) ||
            element.Projection is not null ||
            element.Clip is not null ||
            element.Translation != Vector3.Zero ||
            element.Scale != Vector3.One ||
            element.Rotation != 0f ||
            !element.TransformMatrix.IsIdentity)
        {
            return false;
        }

        return element switch
        {
            StackPanel stackPanel => !stackPanel.IsItemsHost && IsSeeThrough(stackPanel.Background),
            ScrollContentPresenter => false,
            ContentPresenter presenter => IsSeeThrough(presenter.Background),
            Border border => IsSeeThrough(border.Background),
            ScrollViewer or ItemsControl => false,
            Control => true,
            _ => false
        };
    }

    // Every element reports an identity MatrixTransform by default. Any other
    // transform object was put there by the app, usually to animate it, so it
    // blocks even while it still happens to be at rest.
    private static bool IsUntransformed(Transform? transform) =>
        transform is null ||
        (transform is MatrixTransform { Matrix: var matrix } &&
            matrix.M11 == 1 && matrix.M12 == 0 &&
            matrix.M21 == 0 && matrix.M22 == 1 &&
            matrix.OffsetX == 0 && matrix.OffsetY == 0);

    // The host renders beneath a crossed element's background. Subtle layer
    // fills (button and card tints) only tint the shadow; badges, toasts and
    // other opaque plates would hide it and carry their own contrast anyway.
    private static bool IsSeeThrough(Brush? brush) =>
        brush is null ||
        (brush is SolidColorBrush solid && solid.Color.A / 255d * solid.Opacity <= MaxSeeThroughAlpha);

    // Text inside editors, menus, tooltips and drop-downs sits on its own
    // opaque plate (or next to editable text that cannot be shadowed); glyph
    // icons render through an internal TextBlock but are not text.
    private static bool IsExcludedOwner(DependencyObject element) =>
        element is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or
            MenuFlyoutItemBase or MenuFlyoutPresenter or FlyoutPresenter or
            ToolTip or ComboBoxItem or ItemsPresenter or Viewbox or
            IconElement or IconSourceElement;

    private static bool IsInsideExcludedOwner(Panel panel)
    {
        DependencyObject? current = panel;
        for (int depth = 0; depth < 2 && current is not null; depth++)
        {
            current = VisualTreeHelper.GetParent(current);
            if (current is not null && IsExcludedOwner(current))
            {
                return true;
            }
        }

        return false;
    }

    private static Border? EnsureHost(ShadowAttachment attachment, Panel panel, UIElement anchor)
    {
        int anchorIndex = panel.Children.IndexOf(anchor);
        if (attachment.Host is Border existing &&
            ReferenceEquals(VisualTreeHelper.GetParent(existing), panel) &&
            anchorIndex > 0 &&
            ReferenceEquals(panel.Children[anchorIndex - 1], existing))
        {
            return existing;
        }

        RemoveHost(attachment, deferred: false);
        anchorIndex = panel.Children.IndexOf(anchor);
        if (anchorIndex < 0)
        {
            return null;
        }

        var host = new Border { IsHitTestVisible = false };
        AutomationProperties.SetAccessibilityView(host, AccessibilityView.Raw);
        // A 0x0 child at the panel origin changes no Grid, Canvas or
        // RelativePanel layout; the offset binding compensates for where it
        // lands, and sitting right before the anchor puts it beneath the text.
        panel.Children.Insert(anchorIndex, host);
        attachment.Host = host;
        return host;
    }

    private static bool IsHostStillInPlace(ShadowAttachment attachment)
    {
        if (attachment.Host is not Border host ||
            VisualTreeHelper.GetParent(host) is not Panel panel ||
            attachment.Chain is not List<UIElement> chain)
        {
            return false;
        }

        return ReferenceEquals(VisualTreeHelper.GetParent(chain[^1]), panel);
    }

    private static void RemoveHost(ShadowAttachment attachment, bool deferred)
    {
        if (attachment.Host is not Border host)
        {
            return;
        }

        attachment.Host = null;
        attachment.Chain = null;
        if (VisualTreeHelper.GetParent(host) is not Panel panel)
        {
            return;
        }

        // An unloading subtree must not have its children collection edited
        // mid-walk; the removal then waits for the dispatcher.
        if (!deferred || !host.DispatcherQueue.TryEnqueue(() => panel.Children.Remove(host)))
        {
            panel.Children.Remove(host);
        }
    }

    private static void Deactivate(ShadowAttachment attachment, bool deferred)
    {
        ReleaseComposition(attachment);
        RemoveHost(attachment, deferred);
    }

    private static void ReleaseComposition(ShadowAttachment attachment)
    {
        UnwatchText(attachment);
        if (attachment.Sprite is null && attachment.Shadow is null)
        {
            attachment.Mask = null;
            return;
        }

        try
        {
            if (attachment.Host is Border host)
            {
                ElementCompositionPreview.SetElementChildVisual(host, null);
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[TextShadow] Shadow detach failed: {ex.Message}");
        }
        finally
        {
            attachment.Sprite?.Dispose();
            attachment.Shadow?.Dispose();
            attachment.OffsetBinding?.Dispose();
            attachment.SizeBinding?.Dispose();
            attachment.OpacityBinding?.Dispose();
            attachment.Sprite = null;
            attachment.Shadow = null;
            attachment.Mask = null;
            attachment.OffsetBinding = null;
            attachment.SizeBinding = null;
            attachment.OpacityBinding = null;
            attachment.AppliedEdge = null;
        }
    }

    private static List<UIElement> EnsureVisibilityWatch(ShadowAttachment attachment)
    {
        FrameworkElement text = attachment.Element;
        if (attachment.VisibilityChain is List<UIElement> existing &&
            IsSameVisibilityChain(existing, text))
        {
            return existing;
        }

        UnwatchVisibility(attachment);
        List<UIElement> visibilityChain = BuildVisibilityChain(text);
        long[] tokens = new long[visibilityChain.Count];
        WeakReference<ShadowAttachment> weak = attachment.Weak;
        DependencyPropertyChangedCallback callback = (_, _) =>
        {
            if (weak.TryGetTarget(out ShadowAttachment? target))
            {
                Sync(target);
            }
        };

        for (int index = 0; index < visibilityChain.Count; index++)
        {
            tokens[index] = visibilityChain[index].RegisterPropertyChangedCallback(
                UIElement.VisibilityProperty,
                callback);
        }

        attachment.VisibilityChain = visibilityChain;
        attachment.VisibilityTokens = tokens;
        return visibilityChain;
    }

    private static void UnwatchVisibility(ShadowAttachment attachment)
    {
        if (attachment.VisibilityChain is List<UIElement> chain &&
            attachment.VisibilityTokens is long[] tokens)
        {
            for (int index = 0; index < chain.Count && index < tokens.Length; index++)
            {
                chain[index].UnregisterPropertyChangedCallback(
                    UIElement.VisibilityProperty,
                    tokens[index]);
            }
        }

        attachment.VisibilityChain = null;
        attachment.VisibilityTokens = null;
    }

    private static List<UIElement> BuildVisibilityChain(FrameworkElement text)
    {
        UIElement? scopeRoot = text.XamlRoot?.Content as UIElement;
        List<UIElement> visibilityChain = [text];
        DependencyObject? current = text;
        for (int depth = 0; depth < MaxVisibilityDepth && !ReferenceEquals(current, scopeRoot); depth++)
        {
            current = VisualTreeHelper.GetParent(current);
            if (current is null)
            {
                break;
            }

            if (current is UIElement uiElement)
            {
                visibilityChain.Add(uiElement);
            }
        }

        return visibilityChain;
    }

    private static bool IsSameVisibilityChain(List<UIElement> existing, FrameworkElement text)
    {
        if (existing.Count == 0 || !ReferenceEquals(existing[0], text))
        {
            return false;
        }

        UIElement? scopeRoot = text.XamlRoot?.Content as UIElement;
        int index = 1;
        DependencyObject? current = text;
        for (int depth = 0; depth < MaxVisibilityDepth && !ReferenceEquals(current, scopeRoot); depth++)
        {
            current = VisualTreeHelper.GetParent(current);
            if (current is null)
            {
                break;
            }

            if (current is UIElement uiElement)
            {
                if (index >= existing.Count || !ReferenceEquals(existing[index], uiElement))
                {
                    return false;
                }

                index++;
            }
        }

        return index == existing.Count;
    }

    private static bool IsAncestorChainVisible(List<UIElement> chain)
    {
        for (int index = 0; index < chain.Count; index++)
        {
            if (chain[index].Visibility != Visibility.Visible)
            {
                return false;
            }
        }

        return true;
    }

    private static void WatchText(ShadowAttachment attachment)
    {
        if (attachment.IsWatching)
        {
            return;
        }

        attachment.IsWatching = true;
        FrameworkElement text = attachment.Element;
        if (text is TextBlock textBlock)
        {
            attachment.ForegroundToken = textBlock.RegisterPropertyChangedCallback(
                TextBlock.ForegroundProperty,
                (_, _) => ApplyEdge(attachment));
        }
        else if (text is Shape shape)
        {
            attachment.ForegroundToken = shape.RegisterPropertyChangedCallback(
                Shape.FillProperty,
                (_, _) => ApplyEdge(attachment));
        }

        // Inherited foregrounds re-resolve on a theme flip without a local
        // property change on the text itself.
        attachment.ThemeChangedHandler ??= (_, _) => ApplyEdge(attachment);
        text.ActualThemeChanged += attachment.ThemeChangedHandler;
    }

    private static void UnwatchText(ShadowAttachment attachment)
    {
        UnwatchBrush(attachment);
        if (!attachment.IsWatching)
        {
            return;
        }

        attachment.IsWatching = false;
        FrameworkElement text = attachment.Element;
        if (text is TextBlock textBlock)
        {
            textBlock.UnregisterPropertyChangedCallback(
                TextBlock.ForegroundProperty, attachment.ForegroundToken);
        }
        else if (text is Shape shape)
        {
            shape.UnregisterPropertyChangedCallback(
                Shape.FillProperty, attachment.ForegroundToken);
        }

        if (attachment.ThemeChangedHandler is not null)
        {
            text.ActualThemeChanged -= attachment.ThemeChangedHandler;
        }
    }

    /// <summary>
    /// Widget palettes recolor their shared foreground brushes in place, so
    /// the shadow listens to the brush itself. The callback holds the
    /// attachment weakly: app-level brushes outlive every widget window.
    /// </summary>
    private static void WatchBrush(ShadowAttachment attachment, SolidColorBrush? brush)
    {
        if (ReferenceEquals(brush, attachment.WatchedBrush))
        {
            return;
        }

        UnwatchBrush(attachment);
        if (brush is null)
        {
            return;
        }

        WeakReference<ShadowAttachment> weak = attachment.Weak;
        attachment.BrushToken = brush.RegisterPropertyChangedCallback(
            SolidColorBrush.ColorProperty,
            (_, _) =>
            {
                if (weak.TryGetTarget(out ShadowAttachment? target))
                {
                    ApplyEdge(target);
                }
            });
        attachment.WatchedBrush = brush;
    }

    private static void UnwatchBrush(ShadowAttachment attachment)
    {
        if (attachment.WatchedBrush is not SolidColorBrush brush)
        {
            return;
        }

        attachment.WatchedBrush = null;
        brush.UnregisterPropertyChangedCallback(SolidColorBrush.ColorProperty, attachment.BrushToken);
    }

    private static void ApplyEdge(ShadowAttachment attachment)
    {
        if (attachment.Shadow is not DropShadow shadow ||
            attachment.OpacityBinding is not ExpressionAnimation opacity)
        {
            return;
        }

        FrameworkElement element = attachment.Element;
        Windows.UI.Color? edgeOverride = GetEdgeColor(element);
        SolidColorBrush? brush = element switch
        {
            TextBlock text => text.Foreground as SolidColorBrush,
            Shape shape => (shape.Fill ?? shape.Stroke) as SolidColorBrush,
            // The markdown mirror Image has no foreground of its own; its
            // owner keeps the override in sync with the document's semantic
            // foreground.
            Image => null,
            _ => null
        };
        WatchBrush(attachment, brush);
        TextShadowEdge edge;
        if (edgeOverride is { } overrideColor)
        {
            edge = TextShadowPalette.Resolve(overrideColor);
        }
        else if (brush is not null)
        {
            edge = TextShadowPalette.Resolve(brush.Color);
        }
        else
        {
            edge = element.ActualTheme == ElementTheme.Dark
                ? TextShadowPalette.DarkShadow
                : TextShadowPalette.LightHalo;
        }

        if (attachment.AppliedEdge == edge)
        {
            return;
        }

        attachment.AppliedEdge = edge;
        shadow.Color = edge.Color;
        shadow.BlurRadius = edge.BlurRadius;
        shadow.Offset = new Vector3(0, edge.OffsetY, 0);
        opacity.SetScalarParameter("strength", edge.Opacity);
        shadow.StartAnimation("Opacity", opacity);
    }

    // TextBlock, Shape, and the markdown mirror's Image are the glyph-mask
    // sources; RichTextBlock has no GetAlphaMask, which is why the markdown
    // body shadows through the snapshot mirror instead of its own element.
    private static CompositionBrush? TryGetElementMask(FrameworkElement element) => element switch
    {
        TextBlock text => text.GetAlphaMask(),
        Shape shape => shape.GetAlphaMask(),
        Image image => image.GetAlphaMask(),
        _ => null
    };

    private sealed class ShadowAttachment
    {
        public ShadowAttachment(FrameworkElement element)
        {
            Element = element;
            Weak = new WeakReference<ShadowAttachment>(this);
        }

        public FrameworkElement Element { get; }

        public WeakReference<ShadowAttachment> Weak { get; }

        public RoutedEventHandler? LoadedHandler { get; set; }

        public bool IsScopeResolved { get; set; }

        public bool IsOutOfScope { get; set; }

        public bool RetryPending { get; set; }

        public int RetryAttempts { get; set; }

        public Border? Host { get; set; }

        public List<UIElement>? Chain { get; set; }

        public List<UIElement>? VisibilityChain { get; set; }

        public SpriteVisual? Sprite { get; set; }

        public DropShadow? Shadow { get; set; }

        public CompositionBrush? Mask { get; set; }

        public ExpressionAnimation? OffsetBinding { get; set; }

        public ExpressionAnimation? SizeBinding { get; set; }

        public ExpressionAnimation? OpacityBinding { get; set; }

        public TextShadowEdge? AppliedEdge { get; set; }

        public bool IsWatching { get; set; }

        public long ForegroundToken { get; set; }

        public long[]? VisibilityTokens { get; set; }

        public TypedEventHandler<FrameworkElement, object>? ThemeChangedHandler { get; set; }

        public SolidColorBrush? WatchedBrush { get; set; }

        public long BrushToken { get; set; }
    }

    /// <summary>
    /// Pins a dormant template-expanded <see cref="FrameworkElement"/> RCW to its
    /// native XAML lifetime via <c>Loaded</c> so <see cref="PendingTexts"/> survives
    /// managed GCs while shadows are disabled or suppressed, while shedding
    /// non-widget windows immediately on first load.
    /// </summary>
    private sealed class DeferredTextPin
    {
        public DeferredTextPin(FrameworkElement element)
        {
            Element = element;
            LoadedHandler = OnLoaded;
        }

        public FrameworkElement Element { get; }

        public RoutedEventHandler LoadedHandler { get; }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!(Element.XamlRoot?.Content is UIElement root && GetScope(root)))
            {
                ReleaseDeferredPin(Element);
                return;
            }

            if (_isActive &&
                !GetPlateOpaque(root) &&
                GetCast(Element) &&
                !Attachments.TryGetValue(Element, out _))
            {
                Adopt(Element);
            }
        }
    }
}
