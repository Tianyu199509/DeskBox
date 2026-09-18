using System.Collections;
using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// Compares strings the same way Windows Explorer sorts names such as 2 before 10.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>, IComparer
{
    public static NaturalStringComparer CurrentCultureIgnoreCase { get; } = new();

    private NaturalStringComparer()
    {
    }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int result = StrCmpLogicalW(x, y);
        return result != 0
            ? result
            : StringComparer.CurrentCultureIgnoreCase.Compare(x, y);
    }

    int IComparer.Compare(object? x, object? y)
    {
        return Compare(x as string, y as string);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);
}
