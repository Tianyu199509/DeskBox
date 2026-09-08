using System.Runtime.InteropServices;
using DeskBox.Helpers;

namespace DeskBox.Services.Plugins;

/// <summary>Verification uses ed25519-dalek verify_strict through the versioned native ABI. No managed crypto fallback.</summary>
internal static unsafe class PluginEd25519
{
    public static bool Verify(ReadOnlySpan<byte> signature, ReadOnlySpan<byte> message, ReadOnlySpan<byte> publicKey)
    {
        if (signature.Length != 64 || publicKey.Length != 32 || message.Length > 1024 * 1024) return false;
        ShortcutNativeModule? module = ShortcutNativeModule.Default.Module;
        if (module is null || (module.Capabilities & (1UL << 9)) == 0 ||
            !NativeLibrary.TryGetExport(module.ModuleHandle, "deskbox_plugin_verify_ed25519_v1", out nint address))
            return false;
        var verify = (delegate* unmanaged[Cdecl]<byte*, uint, byte*, uint, byte*, uint, uint*, uint>)address;
        uint valid = 0;
        fixed (byte* signaturePointer = signature)
        fixed (byte* messagePointer = message)
        fixed (byte* keyPointer = publicKey)
        {
            return verify(signaturePointer, 64, messagePointer, (uint)message.Length, keyPointer, 32, &valid) == 0 && valid == 1;
        }
    }
}
