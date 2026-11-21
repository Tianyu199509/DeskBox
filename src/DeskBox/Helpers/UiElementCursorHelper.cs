using System.Runtime.CompilerServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;

namespace DeskBox.Helpers;

/// <summary>
/// Assigns a <see cref="UIElement"/>'s ProtectedCursor via a compile-time
/// <see cref="UnsafeAccessorAttribute"/> direct call instead of runtime reflection.
/// The property is protected in the WinUI 3 projection, and Native AOT trims
/// unreferenced members; <see cref="UnsafeAccessorAttribute"/> both emits a zero-overhead
/// direct call (no <c>GetProperty</c> lookup or <c>SetValue</c> argument array boxing on hover)
/// and statically roots <c>UIElement.set_ProtectedCursor</c> for ILC across Win10 and Win11.
/// Cursors are cached per shape so resize borders do not allocate native cursor handles on hover.
/// </summary>
internal static class UiElementCursorHelper
{
    private static readonly object Gate = new();
    private static readonly Dictionary<InputSystemCursorShape, InputSystemCursor> Cursors = [];

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_ProtectedCursor")]
    private static extern void SetProtectedCursor(UIElement element, InputCursor? value);

    internal static void SetCursor(UIElement element, InputSystemCursorShape shape)
    {
        InputSystemCursor cursor;
        lock (Gate)
        {
            if (!Cursors.TryGetValue(shape, out cursor!))
            {
                cursor = InputSystemCursor.Create(shape);
                Cursors[shape] = cursor;
            }
        }

        SetProtectedCursor(element, cursor);
    }
}
