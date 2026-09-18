using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// Thin declarations for the official Everything 1.4 SDK wrapper. The build maps
/// the matching x64 or ARM64 SDK binary to EverythingSdk.dll.
/// </summary>
internal static class EverythingNativeMethods
{
    internal const string DllName = "EverythingSdk.dll";

    internal const uint ErrorOk = 0;
    internal const uint ErrorIpc = 2;

    internal const uint SortNameAscending = 1;

    internal const uint RequestFileName = 0x00000001;
    internal const uint RequestPath = 0x00000002;
    internal const uint RequestSize = 0x00000010;
    internal const uint RequestDateModified = 0x00000040;

    [DllImport(DllName, EntryPoint = "Everything_SetSearchW", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    internal static extern void SetSearch(string search);

    [DllImport(DllName, EntryPoint = "Everything_SetMatchPath", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern void SetMatchPath(int enabled);

    [DllImport(DllName, EntryPoint = "Everything_SetMatchCase", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern void SetMatchCase(int enabled);

    [DllImport(DllName, EntryPoint = "Everything_SetMatchWholeWord", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern void SetMatchWholeWord(int enabled);

    [DllImport(DllName, EntryPoint = "Everything_SetRegex", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern void SetRegex(int enabled);

    [DllImport(DllName, EntryPoint = "Everything_SetMax", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern void SetMax(uint maximum);

    [DllImport(DllName, EntryPoint = "Everything_SetOffset", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern void SetOffset(uint offset);

    [DllImport(DllName, EntryPoint = "Everything_SetSort", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern void SetSort(uint sort);

    [DllImport(DllName, EntryPoint = "Everything_SetRequestFlags", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern void SetRequestFlags(uint flags);

    [DllImport(DllName, EntryPoint = "Everything_QueryW", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern int Query(int wait);

    [DllImport(DllName, EntryPoint = "Everything_GetLastError", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern uint GetLastError();

    [DllImport(DllName, EntryPoint = "Everything_GetNumResults", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern uint GetNumResults();

    [DllImport(DllName, EntryPoint = "Everything_GetTotResults", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern uint GetTotalResults();

    [DllImport(DllName, EntryPoint = "Everything_IsFolderResult", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern int IsFolderResult(uint index);

    [DllImport(DllName, EntryPoint = "Everything_GetResultFileNameW", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern nint GetResultFileName(uint index);

    [DllImport(DllName, EntryPoint = "Everything_GetResultPathW", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern nint GetResultPath(uint index);

    [DllImport(DllName, EntryPoint = "Everything_GetResultSize", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern int GetResultSize(uint index, out long size);

    [DllImport(DllName, EntryPoint = "Everything_GetResultDateModified", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern int GetResultDateModified(uint index, out long fileTime);

    [DllImport(DllName, EntryPoint = "Everything_IsDBLoaded", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern int IsDatabaseLoaded();

    [DllImport(DllName, EntryPoint = "Everything_GetMajorVersion", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern uint GetMajorVersion();

    [DllImport(DllName, EntryPoint = "Everything_GetMinorVersion", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern uint GetMinorVersion();

    [DllImport(DllName, EntryPoint = "Everything_GetRevision", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern uint GetRevision();

    [DllImport(DllName, EntryPoint = "Everything_GetBuildNumber", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern uint GetBuildNumber();

    [DllImport(DllName, EntryPoint = "Everything_Reset", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern void Reset();

    [DllImport(DllName, EntryPoint = "Everything_CleanUp", ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    internal static extern void CleanUp();
}
