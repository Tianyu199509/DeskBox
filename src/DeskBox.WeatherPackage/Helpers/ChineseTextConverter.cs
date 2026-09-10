using DeskBox.WeatherPackage.Services;
using System.Runtime.InteropServices;

namespace DeskBox.WeatherPackage.Helpers;

internal static partial class ChineseTextConverter
{
    private const uint SimplifiedChinese = 0x02000000;
    private const uint TraditionalChinese = 0x04000000;

    public static string ToTraditional(string? value) => Convert(value, TraditionalChinese);

    public static string ToSimplified(string? value) => Convert(value, SimplifiedChinese);

    private static unsafe string Convert(string? value, uint mapFlag)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        int requiredLength = LCMapStringEx(
            "zh-CN",
            mapFlag,
            value,
            value.Length,
            null,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (requiredLength <= 0)
        {
            return value;
        }

        var result = new char[requiredLength];
        int written;
        fixed (char* destination = result)
        {
            written = LCMapStringEx(
                "zh-CN",
                mapFlag,
                value,
                value.Length,
                destination,
                result.Length,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
        }

        return written > 0 && written <= result.Length
            ? new string(result, 0, written)
            : value;
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "LCMapStringEx",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int LCMapStringEx(
        string localeName,
        uint mapFlags,
        string source,
        int sourceLength,
        char* destination,
        int destinationLength,
        IntPtr versionInformation,
        IntPtr reserved,
        IntPtr sortHandle);
}
