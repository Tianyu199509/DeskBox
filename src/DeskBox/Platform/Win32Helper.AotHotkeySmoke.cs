#if DESKBOX_NATIVE_AOT
using System.Runtime.InteropServices;

namespace DeskBox.Platform;

public static partial class Win32Helper
{
    /// <summary>
    /// Sends one tagged synthetic chord for the Native AOT hotkey matrix.
    /// The reserved low-level hook deliberately ignores these records, while
    /// RegisterHotKey can still exercise its normal OS dispatch path.
    /// </summary>
    internal static unsafe bool TrySendTaggedKeyChord(
        ReadOnlySpan<ushort> modifiers,
        ushort virtualKey,
        IntPtr extraInfo,
        out int errorCode)
    {
        int inputCount = checked((modifiers.Length * 2) + 2);
        var tag = new UIntPtr(unchecked((ulong)extraInfo.ToInt64()));
        INPUT* inputs = stackalloc INPUT[inputCount];
        int index = 0;

        foreach (ushort modifier in modifiers)
        {
            inputs[index++] = CreateKeyboardInput(modifier, 0, tag);
        }

        inputs[index++] = CreateKeyboardInput(virtualKey, 0, tag);
        inputs[index++] = CreateKeyboardInput(virtualKey, KEYEVENTF_KEYUP, tag);
        for (int modifierIndex = modifiers.Length - 1; modifierIndex >= 0; modifierIndex--)
        {
            inputs[index++] = CreateKeyboardInput(
                modifiers[modifierIndex],
                KEYEVENTF_KEYUP,
                tag);
        }

        uint sent = SendInput((uint)inputCount, inputs, sizeof(INPUT));
        if (sent == inputCount)
        {
            errorCode = 0;
            return true;
        }

        errorCode = Marshal.GetLastWin32Error();
        if (errorCode == 0)
        {
            errorCode = 31; // ERROR_GEN_FAILURE
        }

        // A partial SendInput must not leave a test key logically pressed.
        _ = TrySendKeyboardEvent(virtualKey, KEYEVENTF_KEYUP, tag, out _);
        for (int modifierIndex = modifiers.Length - 1; modifierIndex >= 0; modifierIndex--)
        {
            _ = TrySendKeyboardEvent(
                modifiers[modifierIndex],
                KEYEVENTF_KEYUP,
                tag,
                out _);
        }

        return false;
    }
}
#endif
