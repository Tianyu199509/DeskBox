// Copyright (c) DeskBox. All rights reserved.

namespace DeskBox.Models;

/// <summary>
/// Event-driven recorder state for the hotkey capture dialog. Held modifiers
/// are tracked live; each non-modifier key down snapshots a chord from the
/// modifiers held at that moment. Releasing keys keeps the last captured
/// chord so the dialog keeps showing it — matching the PowerToys shortcut
/// editor semantics. Standalone single-modifier presses are also tracked so
/// double-tap mode can record modifier-only double-tap shortcuts.
/// </summary>
internal sealed class HotkeyRecordingSession
{
    private const uint VirtualKeyTab = 0x09;
    private const uint VirtualKeyEscape = 0x1B;
    // Synthetic mask key injected by the reserved-gesture hooks; never real input.
    private const uint InternalMaskVirtualKey = 0xE8;

    private readonly HashSet<uint> _heldModifierKeys = [];
    private HotkeyModifierKeys _chordModifiers;
    private uint _primaryKey;
    private HotkeyModifierKeys _candidateStandaloneModifier;
    private bool _candidateStandaloneAborted;

    public HotkeyModifierKeys HeldModifiers { get; private set; }
    public HotkeyModifierKeys LastStandaloneModifier { get; private set; }
    public bool HasGesture => _primaryKey != 0;
    public bool HasStandaloneModifier => _primaryKey == 0 && LastStandaloneModifier != HotkeyModifierKeys.None;
    public bool CancelRequested { get; private set; }
    public GlobalHotkeyGesture Gesture => new(_chordModifiers, (int)_primaryKey);

    public void ProcessKey(uint virtualKey, bool isKeyDown)
    {
        if (virtualKey == InternalMaskVirtualKey)
        {
            return;
        }

        if (TryGetModifierFlag(virtualKey, out HotkeyModifierKeys flag))
        {
            if (isKeyDown)
            {
                bool startingFreshStroke = _heldModifierKeys.Count == 0;
                _heldModifierKeys.Add(virtualKey);
                HeldModifiers = RecomputeHeldModifiers();

                if (startingFreshStroke)
                {
                    // Starting a new modifier hold after releasing previous keys
                    // clears the prior chord so live modifier preview updates immediately.
                    _primaryKey = 0;
                    _chordModifiers = HotkeyModifierKeys.None;
                    LastStandaloneModifier = HotkeyModifierKeys.None;
                    _candidateStandaloneModifier = flag;
                    _candidateStandaloneAborted = false;
                }
                else if (HeldModifiers != _candidateStandaloneModifier)
                {
                    _candidateStandaloneAborted = true;
                }
            }
            else
            {
                _heldModifierKeys.Remove(virtualKey);
                HeldModifiers = RecomputeHeldModifiers();

                if (_heldModifierKeys.Count == 0)
                {
                    if (!_candidateStandaloneAborted &&
                        _primaryKey == 0 &&
                        GlobalHotkeyActivation.IsSingleModifierFlag(_candidateStandaloneModifier))
                    {
                        LastStandaloneModifier = _candidateStandaloneModifier;
                    }

                    _candidateStandaloneModifier = HotkeyModifierKeys.None;
                    _candidateStandaloneAborted = false;
                }
            }

            return;
        }

        if (!isKeyDown)
        {
            return;
        }

        if (virtualKey == VirtualKeyTab && HeldModifiers == HotkeyModifierKeys.None)
        {
            return;
        }

        if (virtualKey == VirtualKeyEscape && HeldModifiers == HotkeyModifierKeys.None)
        {
            CancelRequested = true;
            return;
        }

        _candidateStandaloneAborted = true;
        LastStandaloneModifier = HotkeyModifierKeys.None;
        _primaryKey = virtualKey;
        _chordModifiers = HeldModifiers;
    }

    public void Clear()
    {
        _primaryKey = 0;
        _chordModifiers = HotkeyModifierKeys.None;
        LastStandaloneModifier = HotkeyModifierKeys.None;
        _candidateStandaloneModifier = HotkeyModifierKeys.None;
        _candidateStandaloneAborted = false;
        CancelRequested = false;
    }

    private HotkeyModifierKeys RecomputeHeldModifiers()
    {
        var modifiers = HotkeyModifierKeys.None;
        foreach (uint virtualKey in _heldModifierKeys)
        {
            if (TryGetModifierFlag(virtualKey, out HotkeyModifierKeys flag))
            {
                modifiers |= flag;
            }
        }

        return modifiers;
    }

    internal static bool TryGetModifierFlag(uint virtualKey, out HotkeyModifierKeys flag)
    {
        flag = virtualKey switch
        {
            0x5B or 0x5C => HotkeyModifierKeys.Windows,
            0x11 or 0xA2 or 0xA3 => HotkeyModifierKeys.Control,
            0x12 or 0xA4 or 0xA5 => HotkeyModifierKeys.Alt,
            0x10 or 0xA0 or 0xA1 => HotkeyModifierKeys.Shift,
            _ => HotkeyModifierKeys.None
        };
        return flag != HotkeyModifierKeys.None;
    }
}
