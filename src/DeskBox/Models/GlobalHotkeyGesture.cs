using System.Text.Json.Serialization;

namespace DeskBox.Models;

[Flags]
public enum HotkeyModifierKeys
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

public readonly record struct GlobalHotkeyGesture(HotkeyModifierKeys Modifiers, int VirtualKey);

public enum HotkeyActivationKind
{
    Chord = 0,
    DoubleControl = 1,
    WindowsTap = 2,
    DoubleTap = 3
}

public readonly record struct GlobalHotkeyActivation(
    HotkeyActivationKind Kind,
    GlobalHotkeyGesture Gesture)
{
    [JsonIgnore]
    public bool IsDoubleTap => Kind is HotkeyActivationKind.DoubleControl or HotkeyActivationKind.DoubleTap;

    [JsonIgnore]
    public bool IsModifierOnlyDoubleTap =>
        Kind == HotkeyActivationKind.DoubleControl ||
        (Kind == HotkeyActivationKind.DoubleTap &&
         Gesture.VirtualKey <= 0 &&
         IsSingleModifierFlag(Gesture.Modifiers));

    [JsonIgnore]
    public bool IsStandaloneSpaceDoubleTap =>
        Kind == HotkeyActivationKind.DoubleTap &&
        Gesture.Modifiers == HotkeyModifierKeys.None &&
        Gesture.VirtualKey == 0x20;

    [JsonIgnore]
    public HotkeyModifierKeys DoubleTapModifier => Kind switch
    {
        HotkeyActivationKind.DoubleControl => HotkeyModifierKeys.Control,
        HotkeyActivationKind.DoubleTap when Gesture.VirtualKey <= 0 && IsSingleModifierFlag(Gesture.Modifiers) =>
            Gesture.Modifiers,
        _ => HotkeyModifierKeys.None
    };

    public static GlobalHotkeyActivation FromChord(GlobalHotkeyGesture gesture)
    {
        return new GlobalHotkeyActivation(HotkeyActivationKind.Chord, gesture);
    }

    public static GlobalHotkeyActivation FromDoubleTapChord(GlobalHotkeyGesture gesture)
    {
        return new GlobalHotkeyActivation(HotkeyActivationKind.DoubleTap, gesture);
    }

    public static GlobalHotkeyActivation FromDoubleTapModifier(HotkeyModifierKeys modifier)
    {
        return modifier == HotkeyModifierKeys.Control
            ? new GlobalHotkeyActivation(
                HotkeyActivationKind.DoubleControl,
                new GlobalHotkeyGesture(HotkeyModifierKeys.Control, 0))
            : new GlobalHotkeyActivation(
                HotkeyActivationKind.DoubleTap,
                new GlobalHotkeyGesture(modifier, 0));
    }

    public static bool IsSingleModifierFlag(HotkeyModifierKeys modifiers)
    {
        return modifiers is
            HotkeyModifierKeys.Alt or
            HotkeyModifierKeys.Control or
            HotkeyModifierKeys.Shift or
            HotkeyModifierKeys.Windows;
    }
}
