// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Recognizes two standalone Control-key taps. Control chords are always
/// passed through and cancel the pending tap so normal editing shortcuts do
/// not summon DeskBox.
/// </summary>
internal sealed class DoubleControlHotkeyStateMachine
{
    internal const uint DefaultMaximumIntervalMilliseconds =
        DoubleModifierHotkeyStateMachine.DefaultMaximumIntervalMilliseconds;

    private readonly DoubleModifierHotkeyStateMachine _inner;

    public DoubleControlHotkeyStateMachine(
        uint maximumIntervalMilliseconds = DefaultMaximumIntervalMilliseconds)
    {
        _inner = new DoubleModifierHotkeyStateMachine(
            HotkeyModifierKeys.Control,
            maximumIntervalMilliseconds);
    }

    public ReservedHotkeyEventDisposition Process(uint virtualKey, bool isKeyDown, uint eventTime) =>
        _inner.Process(virtualKey, isKeyDown, eventTime);

    public void Reset() => _inner.Reset();
}
