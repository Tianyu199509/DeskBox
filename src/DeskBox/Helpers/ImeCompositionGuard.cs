using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DeskBox.Helpers;

/// <summary>
/// Centralized 3-layer CJK IME composition tracker and key-collision guard across all
/// DeskBox windows, widgets, popovers, and inline editors:
/// <list type="number">
/// <item><c>VK_PROCESSKEY (229)</c> check on both <see cref="KeyRoutedEventArgs.Key"/> and <see cref="KeyRoutedEventArgs.OriginalKey"/>.</item>
/// <item>Active <c>TextCompositionStarted</c> / <c>TextCompositionEnded</c> tracking across <see cref="TextBox"/>, <see cref="RichEditBox"/>, and <see cref="AutoSuggestBox"/>.</item>
/// <item>A 95ms post-<c>TextCompositionEnded</c> grace window, covering the WinUI 3 / TSF sequence where <c>TextCompositionEnded</c> fires synchronously right before the <c>PreviewKeyDown</c>/<c>KeyDown</c> for the <c>Enter</c>, <c>Escape</c>, <c>Space</c>, <c>Back</c>, or <c>Tab</c> keystroke that finalized or cancelled composition.</item>
/// </list>
/// </summary>
internal static class ImeCompositionGuard
{
    public const int VkProcessKey = 229;
    public const long PostCompositionGraceMs = 95;

    private sealed class ControlCompositionState
    {
        public bool IsTracked { get; set; }
        public bool IsControlComposing { get; set; }
    }

    private static readonly ConditionalWeakTable<DependencyObject, ControlCompositionState> s_controlStates = new();

    private static bool GetIsTracked(DependencyObject node) =>
        s_controlStates.TryGetValue(node, out ControlCompositionState? state) && state.IsTracked;

    private static void SetIsTracked(DependencyObject node, bool value) =>
        s_controlStates.GetOrCreateValue(node).IsTracked = value;

    private static bool GetIsControlComposing(DependencyObject node) =>
        s_controlStates.TryGetValue(node, out ControlCompositionState? state) && state.IsControlComposing;

    private static void SetIsControlComposing(DependencyObject node, bool value) =>
        s_controlStates.GetOrCreateValue(node).IsControlComposing = value;

    private static int s_activeCompositionCount;
    private static long s_lastCompositionEndedTickMs = -10_000;
    private static bool s_globalFocusTrackingInstalled;

    public static event Action<DependencyObject>? CompositionEnded;

    public static void EnsureGlobalFocusTracking()
    {
        if (s_globalFocusTrackingInstalled)
        {
            return;
        }

        try
        {
            FocusManager.GotFocus += FocusManager_GotFocus;
            s_globalFocusTrackingInstalled = true;
        }
        catch
        {
            // Headless unit test environments without a XAML island ignore global hookup.
        }
    }

    public static bool IsVkProcessKey(VirtualKey key, VirtualKey originalKey) =>
        (int)originalKey == VkProcessKey || (int)key == VkProcessKey;

    public static bool IsImeCompositionKeyEvent(KeyRoutedEventArgs args)
    {
        if (IsVkProcessKey(args.Key, args.OriginalKey))
        {
            return true;
        }

        return IsComposingOrWithinGrace();
    }

    public static bool IsComposingOrWithinGrace()
    {
        if (s_activeCompositionCount > 0)
        {
            return true;
        }

        long elapsed = Environment.TickCount64 - s_lastCompositionEndedTickMs;
        return elapsed >= 0 && elapsed < PostCompositionGraceMs;
    }

    public static bool IsActivelyComposing(DependencyObject? control = null)
    {
        if (s_activeCompositionCount > 0)
        {
            return true;
        }

        if (control is not null && GetIsControlComposing(control))
        {
            return true;
        }

        return false;
    }

    public static void AttachAncestors(DependencyObject? leaf)
    {
        for (DependencyObject? cur = leaf; cur is not null; cur = SafeGetParent(cur))
        {
            AttachSingle(cur);
        }
    }

    public static void AttachRecursive(DependencyObject? root)
    {
        if (root is null)
        {
            return;
        }

        AttachSingle(root);

        int count = SafeGetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            if (SafeGetChild(root, i) is { } child)
            {
                AttachRecursive(child);
            }
        }

        if (root is Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                AttachRecursive(child);
            }
        }
        else if (root is Border { Child: { } borderChild })
        {
            AttachRecursive(borderChild);
        }
        else if (root is ContentControl { Content: DependencyObject contentDep })
        {
            AttachRecursive(contentDep);
        }
        else if (root is ScrollViewer { Content: DependencyObject scrollDep })
        {
            AttachRecursive(scrollDep);
        }
    }

    internal static void NotifyCompositionStarted(DependencyObject? sender = null)
    {
        if (sender is not null)
        {
            if (GetIsControlComposing(sender))
            {
                return;
            }

            SetIsControlComposing(sender, true);
        }

        s_activeCompositionCount++;
    }

    internal static void NotifyCompositionEnded(DependencyObject? sender = null, bool recordGrace = true)
    {
        if (sender is not null)
        {
            if (!GetIsControlComposing(sender))
            {
                if (recordGrace)
                {
                    s_lastCompositionEndedTickMs = Environment.TickCount64;
                }

                return;
            }

            SetIsControlComposing(sender, false);
        }

        s_activeCompositionCount = Math.Max(0, s_activeCompositionCount - 1);
        if (recordGrace)
        {
            s_lastCompositionEndedTickMs = Environment.TickCount64;
        }

        if (sender is not null)
        {
            CompositionEnded?.Invoke(sender);
        }
    }

    internal static void ResetForTest()
    {
        s_activeCompositionCount = 0;
        s_lastCompositionEndedTickMs = -10_000;
    }

    private static void FocusManager_GotFocus(object? sender, FocusManagerGotFocusEventArgs e)
    {
        if (e.NewFocusedElement is DependencyObject focused)
        {
            AttachAncestors(focused);
        }
    }

    private static void AttachSingle(DependencyObject node)
    {
        if (node is TextBox tb)
        {
            if (GetIsTracked(tb))
            {
                return;
            }

            SetIsTracked(tb, true);
            tb.TextCompositionStarted += TextBox_TextCompositionStarted;
            tb.TextCompositionEnded += TextBox_TextCompositionEnded;
            tb.Unloaded += Control_Unloaded;
        }
        else if (node is RichEditBox reb)
        {
            if (GetIsTracked(reb))
            {
                return;
            }

            SetIsTracked(reb, true);
            reb.TextCompositionStarted += RichEditBox_TextCompositionStarted;
            reb.TextCompositionEnded += RichEditBox_TextCompositionEnded;
            reb.Unloaded += Control_Unloaded;
        }
        else if (node is AutoSuggestBox asb)
        {
            if (GetIsTracked(asb))
            {
                return;
            }

            SetIsTracked(asb, true);
            asb.Loaded += AutoSuggestBox_Loaded;
            if (asb.IsLoaded)
            {
                AttachAutoSuggestInnerTextBox(asb);
            }
        }
    }

    private static void AutoSuggestBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is AutoSuggestBox asb)
        {
            AttachAutoSuggestInnerTextBox(asb);
        }
    }

    private static void AttachAutoSuggestInnerTextBox(DependencyObject root)
    {
        int count = SafeGetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            if (SafeGetChild(root, i) is { } child)
            {
                if (child is TextBox tb)
                {
                    AttachSingle(tb);
                }
                else
                {
                    AttachAutoSuggestInnerTextBox(child);
                }
            }
        }
    }

    private static void TextBox_TextCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args) =>
        NotifyCompositionStarted(sender);

    private static void TextBox_TextCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args) =>
        NotifyCompositionEnded(sender, recordGrace: true);

    private static void RichEditBox_TextCompositionStarted(RichEditBox sender, TextCompositionStartedEventArgs args) =>
        NotifyCompositionStarted(sender);

    private static void RichEditBox_TextCompositionEnded(RichEditBox sender, TextCompositionEndedEventArgs args) =>
        NotifyCompositionEnded(sender, recordGrace: true);

    private static void Control_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject dep && GetIsControlComposing(dep))
        {
            NotifyCompositionEnded(dep, recordGrace: false);
        }
    }

    private static DependencyObject? SafeGetParent(DependencyObject node)
    {
        try
        {
            return VisualTreeHelper.GetParent(node);
        }
        catch
        {
            return null;
        }
    }

    private static int SafeGetChildrenCount(DependencyObject node)
    {
        try
        {
            return VisualTreeHelper.GetChildrenCount(node);
        }
        catch
        {
            return 0;
        }
    }

    private static DependencyObject? SafeGetChild(DependencyObject node, int index)
    {
        try
        {
            return VisualTreeHelper.GetChild(node, index);
        }
        catch
        {
            return null;
        }
    }
}
