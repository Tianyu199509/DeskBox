using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DeskBox.GlancePackage.Services;

/// <summary>
/// Reads the host-provided config payload (HostApi v2 GetConfigJson:
/// locale, effective theme/accent and material tokens). Degrades gracefully:
/// no host pointer, bad payload, or unknown locale → null and callers fall
/// back to ambient values.
/// </summary>
internal static unsafe class HostConfig
{
    private static delegate* unmanaged[Cdecl]<byte*, int, int> _getConfigJson;
    private static delegate* unmanaged[Cdecl]<char*, int, byte*, int, nint, int> _setInstanceConfig;
    private static nint _context;

    internal static void Initialize(nint getConfigJson) =>
        _getConfigJson = (delegate* unmanaged[Cdecl]<byte*, int, int>)getConfigJson;

    internal static void InitializeSetInstanceConfig(nint setInstanceConfig) =>
        _setInstanceConfig = (delegate* unmanaged[Cdecl]<char*, int, byte*, int, nint, int>)setInstanceConfig;

    internal static void InitializeContext(nint context) => _context = context;

    /// <summary>
    /// Write-through (audits 19-20): commit a settings mutation to the
    /// authoritative host store. The session context is echoed so the host
    /// can attribute the call; returns false when the channel is absent
    /// (older host) - callers must then treat settings as read-only instead
    /// of silently mutating a copy that the next sync overwrites.
    /// </summary>
    internal static bool TryPushInstanceConfig(string instanceId, string json)
    {
        if (_setInstanceConfig == null) return false;
        try
        {
            byte[] payload = Encoding.UTF8.GetBytes(json);
            fixed (char* id = instanceId)
            fixed (byte* bytes = payload)
            {
                return _setInstanceConfig(id, instanceId.Length, bytes, payload.Length, _context) == 0;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Drop the host callbacks; called on shutdown so a later
    /// activate in the same process never observes a stale pointer.</summary>
    internal static void Reset()
    {
        _getConfigJson = null;
        _setInstanceConfig = null;
        _context = 0;
    }

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

    internal static PackageAppearance ReadAppearance() => PackageAppearance.Parse(TryFetchJson());

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
