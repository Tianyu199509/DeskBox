using DeskBox.Platform;
using System.Runtime.InteropServices;

namespace DeskBox.Services;

/// <summary>
/// Provides the installed font families for settings that offer a system-font
/// picker. The enumeration is deliberately lazy because most users never open
/// that picker during a session.
/// </summary>
public sealed class SystemFontCatalogService
{
    private const byte DefaultCharSetValue = 1;
    private readonly Lazy<IReadOnlyList<string>> _fontFamilies;

    public SystemFontCatalogService()
    {
        _fontFamilies = new Lazy<IReadOnlyList<string>>(
            EnumerateInstalledFontFamilies,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Gets the installed font families, sorted and de-duplicated. The
    /// returned collection never contains the UI's "system default" option;
    /// that option is a caller-level preference rather than an installed font.
    /// </summary>
    public IReadOnlyList<string> GetFontFamilies() => _fontFamilies.Value;

    internal static IReadOnlyList<string> NormalizeFontFamilies(IEnumerable<string?> values)
    {
        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .Where(static value => !value.StartsWith("@", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> EnumerateInstalledFontFamilies()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<string>();
        }

        var names = new List<string>();
        var deviceContext = Win32Helper.CreateCompatibleDC(IntPtr.Zero);
        if (deviceContext == IntPtr.Zero)
        {
            return Array.Empty<string>();
        }

        try
        {
            var logFont = new Win32Helper.LogFont
            {
                CharSet = DefaultCharSetValue,
                FaceName = string.Empty
            };
            Win32Helper.EnumFontFamilyDelegate callback = (fontInfo, _, _, _) =>
            {
                try
                {
                    var logFontInfo = Marshal.PtrToStructure<Win32Helper.LogFont>(fontInfo);
                    if (!string.IsNullOrWhiteSpace(logFontInfo.FaceName))
                    {
                        names.Add(logFontInfo.FaceName);
                    }
                }
                catch
                {
                    // A malformed provider must not make the settings page fail.
                }

                return 1;
            };

            _ = Win32Helper.EnumFontFamiliesExW(
                deviceContext,
                ref logFont,
                callback,
                IntPtr.Zero,
                0);

            return NormalizeFontFamilies(names);
        }
        catch
        {
            // Font enumeration is a convenience. API/driver failures should
            // degrade to an empty picker instead of affecting application boot.
            return Array.Empty<string>();
        }
        finally
        {
            _ = Win32Helper.DeleteDC(deviceContext);
        }
    }
}
