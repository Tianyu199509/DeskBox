// Copyright (c) DeskBox. All rights reserved.

namespace DeskBox.Services;

internal enum ReservedHotkeyEventDisposition
{
    PassThrough,
    Suppress,
    TriggerAndSuppress,
    TriggerAndPassThrough,

    /// <summary>
    /// A Windows-key press edge in WindowsTap mode: pass the event through,
    /// but inject the Start-menu mask key now. Masking at the down edge
    /// breaks the "isolated tap" condition for the whole key-hold duration
    /// instead of racing the key-up delivery with a trailing mask
    /// (feedback 367).
    /// </summary>
    PrepareMaskAndPassThrough
}

internal enum ReservedHotkeyMode
{
    WinSpace,
    AltSpace,
    DoubleControl,
    WindowsTap
}

/// <summary>
/// Event-driven state for the opt-in Win+Space override. It deliberately does
/// not query asynchronous keyboard state from inside a low-level hook callback.
/// </summary>
internal sealed class WinSpaceHotkeyStateMachine
{
    private const uint VirtualKeySpace = 0x20;
    private readonly ReservedHotkeyMode _mode;

    [Flags]
    private enum PressedModifiers
    {
        None = 0,
        LeftWindows = 1 << 0,
        RightWindows = 1 << 1,
        Control = 1 << 2,
        LeftControl = 1 << 3,
        RightControl = 1 << 4,
        Alt = 1 << 5,
        LeftAlt = 1 << 6,
        RightAlt = 1 << 7,
        Shift = 1 << 8,
        LeftShift = 1 << 9,
        RightShift = 1 << 10
    }

    private const PressedModifiers WindowsModifiers =
        PressedModifiers.LeftWindows | PressedModifiers.RightWindows;
    private const PressedModifiers AltModifiers =
        PressedModifiers.Alt |
        PressedModifiers.LeftAlt |
        PressedModifiers.RightAlt;
    private const PressedModifiers NonWindowsModifiers =
        PressedModifiers.Control |
        PressedModifiers.LeftControl |
        PressedModifiers.RightControl |
        PressedModifiers.Alt |
        PressedModifiers.LeftAlt |
        PressedModifiers.RightAlt |
        PressedModifiers.Shift |
        PressedModifiers.LeftShift |
        PressedModifiers.RightShift;

    private PressedModifiers _pressedModifiers;
    private bool _spaceDown;
    private bool _suppressSpaceUp;

    public WinSpaceHotkeyStateMachine()
        : this(ReservedHotkeyMode.WinSpace)
    {
    }

    internal WinSpaceHotkeyStateMachine(ReservedHotkeyMode mode)
    {
        if (mode is not (ReservedHotkeyMode.WinSpace or ReservedHotkeyMode.AltSpace))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        _mode = mode;
    }

    public ReservedHotkeyEventDisposition Process(uint virtualKey, bool isKeyDown)
    {
        if (TryGetModifier(virtualKey, out PressedModifiers modifier))
        {
            if (isKeyDown)
            {
                _pressedModifiers |= modifier;
            }
            else
            {
                _pressedModifiers &= ~modifier;
            }

            return ReservedHotkeyEventDisposition.PassThrough;
        }

        if (virtualKey != VirtualKeySpace)
        {
            return ReservedHotkeyEventDisposition.PassThrough;
        }

        if (isKeyDown)
        {
            if (_spaceDown)
            {
                return _suppressSpaceUp
                    ? ReservedHotkeyEventDisposition.Suppress
                    : ReservedHotkeyEventDisposition.PassThrough;
            }

            _spaceDown = true;
            if (!IsExactModifierSpace())
            {
                return ReservedHotkeyEventDisposition.PassThrough;
            }

            _suppressSpaceUp = true;
            return ReservedHotkeyEventDisposition.TriggerAndSuppress;
        }

        _spaceDown = false;
        if (!_suppressSpaceUp)
        {
            return ReservedHotkeyEventDisposition.PassThrough;
        }

        _suppressSpaceUp = false;
        return ReservedHotkeyEventDisposition.Suppress;
    }

    private bool IsExactModifierSpace()
    {
        if (_mode == ReservedHotkeyMode.WinSpace)
        {
            return (_pressedModifiers & WindowsModifiers) != 0 &&
                   (_pressedModifiers & NonWindowsModifiers) == 0;
        }

        const PressedModifiers nonAltModifiers =
            WindowsModifiers |
            PressedModifiers.Control |
            PressedModifiers.LeftControl |
            PressedModifiers.RightControl |
            PressedModifiers.Shift |
            PressedModifiers.LeftShift |
            PressedModifiers.RightShift;
        return (_pressedModifiers & AltModifiers) != 0 &&
               (_pressedModifiers & nonAltModifiers) == 0;
    }

    public void CancelSuppression()
    {
        _suppressSpaceUp = false;
    }

    public void Reset()
    {
        _pressedModifiers = PressedModifiers.None;
        _spaceDown = false;
        _suppressSpaceUp = false;
    }

    private static bool TryGetModifier(uint virtualKey, out PressedModifiers modifier)
    {
        modifier = virtualKey switch
        {
            0x5B => PressedModifiers.LeftWindows,
            0x5C => PressedModifiers.RightWindows,
            0x11 => PressedModifiers.Control,
            0xA2 => PressedModifiers.LeftControl,
            0xA3 => PressedModifiers.RightControl,
            0x12 => PressedModifiers.Alt,
            0xA4 => PressedModifiers.LeftAlt,
            0xA5 => PressedModifiers.RightAlt,
            0x10 => PressedModifiers.Shift,
            0xA0 => PressedModifiers.LeftShift,
            0xA1 => PressedModifiers.RightShift,
            _ => PressedModifiers.None
        };
        return modifier != PressedModifiers.None;
    }
}
