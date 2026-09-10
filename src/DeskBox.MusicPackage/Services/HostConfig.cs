using System.Runtime.CompilerServices;
using System.Text.Json;
namespace DeskBox.MusicPackage.Services;

internal static unsafe class HostConfig
{
    private static nint _get;
    internal static void Initialize(nint get) => _get = get;
    internal static void Reset() => _get = 0;
    internal static string? ReadLocale()
    {
        if (_get == 0) return null;
        try
        {
            var call = (delegate* unmanaged[Cdecl]<byte*, int, int>)_get;
            int size = call(null, 0);
            if (size is <= 0 or > 65536) return null;
            byte[] bytes = new byte[size];
            fixed (byte* p = bytes)
            {
                int count = call(p, bytes.Length);
                if (count <= 0 || count > bytes.Length) return null;
                using var doc = JsonDocument.Parse(bytes.AsMemory(0, count));
                return doc.RootElement.TryGetProperty("locale", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            }
        }
        catch { return null; }
    }
}
