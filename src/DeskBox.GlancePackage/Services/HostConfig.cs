using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DeskBox.GlancePackage.Services;

/// <summary>
/// Reads the host-provided config payload (HostApi v2 GetConfigJson:
/// {"locale":"...","accent":"#AARRGGBB"}). Locale drives the calendar
/// culture; accent is reserved for the theme batch. Degrades gracefully:
/// no host pointer, bad payload, or unknown locale → null and callers fall
/// back to ambient values.
/// </summary>
internal static unsafe class HostConfig
{
    private static delegate* unmanaged[Cdecl]<byte*, int, int> _getConfigJson;

    internal static void Initialize(nint getConfigJson) =>
        _getConfigJson = (delegate* unmanaged[Cdecl]<byte*, int, int>)getConfigJson;

    internal static CultureInfo? TryGetCulture()
    {
        string? locale = TryReadString("locale");
        if (string.IsNullOrWhiteSpace(locale)) return null;
        try
        {
            return CultureInfo.GetCultureInfo(locale.Replace('_', '-'));
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private static string? TryReadString(string property)
    {
        string? json = TryFetchJson();
        if (json is null) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(property, out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryFetchJson()
    {
        if (_getConfigJson == null) return null;
        try
        {
            // Two-call contract: a too-small buffer returns the required size.
            int required = _getConfigJson(null, 0);
            if (required <= 0 || required > 64 * 1024) return null;
            byte[] buffer = new byte[required];
            int written;
            fixed (byte* pointer = buffer)
            {
                written = _getConfigJson(pointer, required);
            }
            if (written <= 0 || written > required) return null;
            return Encoding.UTF8.GetString(buffer, 0, written);
        }
        catch
        {
            return null;
        }
    }
}
