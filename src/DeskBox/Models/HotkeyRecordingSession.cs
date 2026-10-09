// Copyright (c) DeskBox. All rights reserved.

namespace DeskBox.Models;

/// <summary>
/// Event-driven recorder state for the hotkey capture dialog. Held modifiers
/// are tracked live; each non-modifier key down snapshots a chord from the
/// modifiers held at that moment. Releasing keys keeps the last captured
/// chord so the dialog keeps showing it — matching the PowerToys shortcut
/// editor semantics.
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

    public HotkeyModifierKeys HeldModifiers { get; private set; }
    public bool HasGesture => _primaryKey != 0;
    public bool CancelRequested { get; private set; }
    public GlobalHotkeyGesture Gesture => new(_chordModifiers, (int)_primaryKey);

    public void ProcessKey(uint virtualKey, bool isKeyDown)
    {
        if (TryGetModifierFlag(virtualKey, out HotkeyModifierKeys flag))
        {
            if (isKeyDown)
            {
                _heldModifierKeys.Add(virtualKey);
            }
            else
            {
                _heldModifierKeys.Remove(virtualKey);
            }

            HeldModifiers = RecomputeHeldModifiers();
            return;
        }

        if (!isKeyDown ||
            virtualKey == VirtualKeyTab ||
            virtualKey == InternalMaskVirtualKey)
        {
            return;
        }

        if (virtualKey == VirtualKeyEscape)
        {
            CancelRequested = true;
            return;
        }

        _primaryKey = virtualKey;
        _chordModifiers = HeldModifiers;
    }

    public void Clear()
    {
        _primaryKey = 0;
        _chordModifiers = HotkeyModifierKeys.None;
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
