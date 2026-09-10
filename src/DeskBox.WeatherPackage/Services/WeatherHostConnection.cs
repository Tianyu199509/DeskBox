using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DeskBox.WeatherPackage.Services;

/// <summary>Value-only projection of frozen HostApi v4; no host managed references.</summary>
internal sealed unsafe class WeatherHostConnection(nint context, nint log, nint getConfig, nint subscribe, nint setConfig)
{
    internal nint Context { get; } = context;
    internal string ReadConfig()
    {
        if (getConfig == 0) return "{}";
        var get = (delegate* unmanaged[Cdecl]<byte*, int, int>)getConfig;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int size = get(null, 0);
            if (size is <= 0 or > 65536) return "{}";
            byte[] bytes = new byte[size];
            fixed (byte* p = bytes)
            {
                int written = get(p, bytes.Length);
                if (written > bytes.Length) continue;
                if (written <= 0) return "{}";
                return Encoding.UTF8.GetString(bytes, 0, written);
            }
        }
        return "{}";
    }
    internal string ReadLocale()
    {
        try
        {
            using var doc = JsonDocument.Parse(ReadConfig());
            if (doc.RootElement.TryGetProperty("locale", out var l) && l.ValueKind == JsonValueKind.String)
                return CultureInfo.GetCultureInfo(l.GetString()!).Name;
        }
        catch (Exception error) { WriteLog("[WeatherPackage] host locale unavailable: " + error.Message); }
        return "en-US";
    }
    internal bool Subscribe(nint handler) => subscribe != 0 &&
        ((delegate* unmanaged[Cdecl]<nint, nint, int>)subscribe)(Context, handler) == 0;
    internal bool WritePatch(string instanceId, string json)
    {
        if (setConfig == 0 || json.Length > 65536) return false;
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        fixed (char* id = instanceId)
        fixed (byte* payload = bytes)
            return ((delegate* unmanaged[Cdecl]<char*, int, byte*, int, nint, int>)setConfig)(id, instanceId.Length, payload, bytes.Length, Context) == 0;
    }
    internal void WriteLog(string message)
    {
        if (log == 0) return;
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        fixed (byte* p = bytes) ((delegate* unmanaged[Cdecl]<byte*, int, void>)log)(p, bytes.Length);
    }
}
