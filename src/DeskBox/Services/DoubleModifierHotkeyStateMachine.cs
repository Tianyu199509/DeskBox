// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Recognizes two standalone taps of a target modifier key (Control, Shift,
/// Alt, or Windows) within <see cref="DefaultMaximumIntervalMilliseconds"/>.
/// Any intervening chord key or simultaneous left+right press cancels the
/// pending tap so ordinary typing and modifier shortcuts pass through cleanly.
/// </summary>
internal sealed class DoubleModifierHotkeyStateMachine
{
    internal const uint DefaultMaximumIntervalMilliseconds = 320;

    private const uint Shift = 0x10;
    private const uint Control = 0x11;
    private const uint Menu = 0x12;
    private const uint LeftWindows = 0x5B;
    private const uint RightWindows = 0x5C;
    private const uint LeftShift = 0xA0;
    private const uint RightShift = 0xA1;
    private const uint LeftControl = 0xA2;
    private const uint RightControl = 0xA3;
    private const uint LeftMenu = 0xA4;
    private const uint RightMenu = 0xA5;

    private readonly HotkeyModifierKeys _targetModifier;
    private readonly uint _maximumIntervalMilliseconds;
    private PhysicalKeySlot _pressedSlots;
    private bool _currentPressUsedAsChord;
    private uint _lastStandaloneReleaseTime;
    private bool _hasPendingTap;

    [Flags]
    private enum PhysicalKeySlot
    {
        None = 0,
        Generic = 1,
        Left = 2,
        Right = 4
    }

    public DoubleModifierHotkeyStateMachine(
        HotkeyModifierKeys targetModifier,
        uint maximumIntervalMilliseconds = DefaultMaximumIntervalMilliseconds)
    {
        if (!GlobalHotkeyActivation.IsSingleModifierFlag(targetModifier))
        {
            throw new ArgumentOutOfRangeException(nameof(targetModifier));
        }

        _targetModifier = targetModifier;
        _maximumIntervalMilliseconds = maximumIntervalMilliseconds;
    }

    public ReservedHotkeyEventDisposition Process(uint virtualKey, bool isKeyDown, uint eventTime)
    {
        if (!TryGetTargetSlot(virtualKey, out PhysicalKeySlot slot))
        {
            if (isKeyDown)
            {
                if (_pressedSlots != PhysicalKeySlot.None)
                {
                    _currentPressUsedAsChord = true;
                }

                _hasPendingTap = false;
            }

            return ReservedHotkeyEventDisposition.PassThrough;
        }

        if (isKeyDown)
        {
            if ((_pressedSlots & slot) == 0 && _pressedSlots != PhysicalKeySlot.None)
            {
                _currentPressUsedAsChord = true;
            }

            _pressedSlots |= slot;
            return ReservedHotkeyEventDisposition.PassThrough;
        }

        if ((_pressedSlots & slot) == 0)
        {
            return ReservedHotkeyEventDisposition.PassThrough;
        }

        _pressedSlots &= ~slot;
        if (_pressedSlots != PhysicalKeySlot.None)
        {
            return ReservedHotkeyEventDisposition.PassThrough;
        }

        if (_currentPressUsedAsChord)
        {
            _currentPressUsedAsChord = false;
            _hasPendingTap = false;
            return ReservedHotkeyEventDisposition.PassThrough;
        }

        if (_hasPendingTap &&
            unchecked(eventTime - _lastStandaloneReleaseTime) <= _maximumIntervalMilliseconds)
        {
            _hasPendingTap = false;
            return ReservedHotkeyEventDisposition.TriggerAndPassThrough;
        }

        _lastStandaloneReleaseTime = eventTime;
        _hasPendingTap = true;
        return ReservedHotkeyEventDisposition.PassThrough;
    }

    public void Reset()
    {
        _pressedSlots = PhysicalKeySlot.None;
        _currentPressUsedAsChord = false;
        _lastStandaloneReleaseTime = 0;
        _hasPendingTap = false;
    }

    private bool TryGetTargetSlot(uint virtualKey, out PhysicalKeySlot slot)
    {
        slot = _targetModifier switch
        {
            HotkeyModifierKeys.Control => virtualKey switch
            {
                Control => PhysicalKeySlot.Generic,
                LeftControl => PhysicalKeySlot.Left,
                RightControl => PhysicalKeySlot.Right,
                _ => PhysicalKeySlot.None
            },
            HotkeyModifierKeys.Shift => virtualKey switch
            {
                Shift => PhysicalKeySlot.Generic,
                LeftShift => PhysicalKeySlot.Left,
                RightShift => PhysicalKeySlot.Right,
                _ => PhysicalKeySlot.None
            },
            HotkeyModifierKeys.Alt => virtualKey switch
            {
                Menu => PhysicalKeySlot.Generic,
                LeftMenu => PhysicalKeySlot.Left,
                RightMenu => PhysicalKeySlot.Right,
                _ => PhysicalKeySlot.None
            },
            HotkeyModifierKeys.Windows => virtualKey switch
            {
                LeftWindows => PhysicalKeySlot.Left,
                RightWindows => PhysicalKeySlot.Right,
                _ => PhysicalKeySlot.None
            },
            _ => PhysicalKeySlot.None
        };
        return slot != PhysicalKeySlot.None;
    }
}

/// <summary>
/// Recognizes two quick standalone Spacebar taps while letting the first tap
/// pass through with zero latency. Modifiers, overlapping typing keys, key
/// auto-repeat, and long Space holds invalidate the tap; the second Space
/// press triggers immediately and suppresses its own down/up pair so a stray
/// space never leaks into the newly opened search input box.
/// </summary>
internal sealed class DoubleSpaceHotkeyStateMachine
{
    internal const uint DefaultMaximumIntervalMilliseconds = 300;
    internal const uint DefaultMaximumHoldMilliseconds = 220;

    private const uint VirtualKeySpace = 0x20;
    private const uint Shift = 0x10;
    private const uint Control = 0x11;
    private const uint Menu = 0x12;
    private const uint LeftWindows = 0x5B;
    private const uint RightWindows = 0x5C;
    private const uint LeftShift = 0xA0;
    private const uint RightShift = 0xA1;
    private const uint LeftControl = 0xA2;
    private const uint RightControl = 0xA3;
    private const uint LeftMenu = 0xA4;
    private const uint RightMenu = 0xA5;

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

    private readonly uint _maximumIntervalMilliseconds;
    private readonly uint _maximumHoldMilliseconds;
    private PressedModifiers _pressedModifiers;
    private bool _spaceDown;
    private uint _spaceDownTime;
    private bool _currentPressInvalidated;
    private bool _hasPendingTap;
    private uint _lastStandaloneReleaseTime;
    private bool _suppressSpaceUp;

    public DoubleSpaceHotkeyStateMachine(
        uint maximumIntervalMilliseconds = DefaultMaximumIntervalMilliseconds,
        uint maximumHoldMilliseconds = DefaultMaximumHoldMilliseconds)
    {
        _maximumIntervalMilliseconds = maximumIntervalMilliseconds;
        _maximumHoldMilliseconds = maximumHoldMilliseconds;
    }

    public ReservedHotkeyEventDisposition Process(uint virtualKey, bool isKeyDown, uint eventTime)
    {
        if (TryGetModifier(virtualKey, out PressedModifiers modifier))
        {
            if (isKeyDown)
            {
                _pressedModifiers |= modifier;
                _currentPressInvalidated = true;
                _hasPendingTap = false;
            }
            else
            {
                _pressedModifiers &= ~modifier;
                if (_spaceDown)
                {
                    _currentPressInvalidated = true;
                }

                _hasPendingTap = false;
            }

            return ReservedHotkeyEventDisposition.PassThrough;
        }

        if (virtualKey != VirtualKeySpace)
        {
            if (isKeyDown || _spaceDown)
            {
                _currentPressInvalidated = true;
            }

            _hasPendingTap = false;
            return ReservedHotkeyEventDisposition.PassThrough;
        }

        if (isKeyDown)
        {
            if (_spaceDown)
            {
                if (_suppressSpaceUp)
                {
                    return ReservedHotkeyEventDisposition.Suppress;
                }

                // Auto-repeat while holding Spacebar invalidates the tap.
                _currentPressInvalidated = true;
                _hasPendingTap = false;
                return ReservedHotkeyEventDisposition.PassThrough;
            }

            _spaceDown = true;
            _spaceDownTime = eventTime;
            if (_pressedModifiers != PressedModifiers.None)
            {
                _currentPressInvalidated = true;
                _hasPendingTap = false;
                return ReservedHotkeyEventDisposition.PassThrough;
            }

            _currentPressInvalidated = false;
            if (_hasPendingTap &&
                unchecked(eventTime - _lastStandaloneReleaseTime) <= _maximumIntervalMilliseconds)
            {
                _hasPendingTap = false;
                _suppressSpaceUp = true;
                return ReservedHotkeyEventDisposition.TriggerAndSuppress;
            }

            _hasPendingTap = false;
            return ReservedHotkeyEventDisposition.PassThrough;
        }

        _spaceDown = false;
        if (_suppressSpaceUp)
        {
            _suppressSpaceUp = false;
            return ReservedHotkeyEventDisposition.Suppress;
        }

        if (_currentPressInvalidated ||
            _pressedModifiers != PressedModifiers.None ||
            unchecked(eventTime - _spaceDownTime) > _maximumHoldMilliseconds)
        {
            _currentPressInvalidated = false;
            _hasPendingTap = false;
            return ReservedHotkeyEventDisposition.PassThrough;
        }

        _lastStandaloneReleaseTime = eventTime;
        _hasPendingTap = true;
        return ReservedHotkeyEventDisposition.PassThrough;
    }

    public void CancelSuppression()
    {
        _suppressSpaceUp = false;
    }

    public void Reset()
    {
        _pressedModifiers = PressedModifiers.None;
        _spaceDown = false;
        _spaceDownTime = 0;
        _currentPressInvalidated = false;
        _hasPendingTap = false;
        _lastStandaloneReleaseTime = 0;
        _suppressSpaceUp = false;
    }

    private static bool TryGetModifier(uint virtualKey, out PressedModifiers modifier)
    {
        modifier = virtualKey switch
        {
            LeftWindows => PressedModifiers.LeftWindows,
            RightWindows => PressedModifiers.RightWindows,
            Control => PressedModifiers.Control,
            LeftControl => PressedModifiers.LeftControl,
            RightControl => PressedModifiers.RightControl,
            Menu => PressedModifiers.Alt,
            LeftMenu => PressedModifiers.LeftAlt,
            RightMenu => PressedModifiers.RightAlt,
            Shift => PressedModifiers.Shift,
            LeftShift => PressedModifiers.LeftShift,
            RightShift => PressedModifiers.RightShift,
            _ => PressedModifiers.None
        };
        return modifier != PressedModifiers.None;
    }
}

/// <summary>
/// Time-window gate for double-tap chord / function-key activations registered
/// via <c>RegisterHotKey</c> or the reserved <c>Alt+Space</c>/<c>Win+Space</c>
/// hook. Requires two activations of the same gesture within the window before
/// dispatching, and consumes each pair so a third press begins a fresh pair.
/// </summary>
internal sealed class DoubleTapChordGate
{
    internal const long DefaultMaximumIntervalMilliseconds = 350;

    private readonly long _maximumIntervalMilliseconds;
    private GlobalHotkeyGesture _pendingGesture;
    private long _pendingTapTicks;
    private bool _hasPendingTap;

    public DoubleTapChordGate(long maximumIntervalMilliseconds = DefaultMaximumIntervalMilliseconds)
    {
        _maximumIntervalMilliseconds = maximumIntervalMilliseconds;
    }

    public bool RecordPressAndShouldTrigger(GlobalHotkeyGesture gesture, long nowTicks)
    {
        if (_hasPendingTap &&
            _pendingGesture.Equals(gesture) &&
            unchecked(nowTicks - _pendingTapTicks) is >= 0 &&
            unchecked(nowTicks - _pendingTapTicks) <= _maximumIntervalMilliseconds)
        {
            _hasPendingTap = false;
            _pendingTapTicks = 0;
            return true;
        }

        _pendingGesture = gesture;
        _pendingTapTicks = nowTicks;
        _hasPendingTap = true;
        return false;
    }

    public void Reset()
    {
        _hasPendingTap = false;
        _pendingTapTicks = 0;
        _pendingGesture = default;
    }
}
