using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// Kernel32, IP Helper, DXGI, PDH, and NTDLL system metrics entry points used by
/// <see cref="DeskBox.Services.CustomWidgetSystemMetricsSource"/>.
/// Kept inside <c>DeskBox.Platform</c> so P/Invoke and COM vtable calls stay isolated
/// to the platform boundary and remain 100% NativeAOT-compatible.
/// </summary>
internal static class CustomWidgetNativeMetrics
{
    // Verified against Windows SDK 10.0.26100.0 shared/netioapi.h & shared/ifdef.h:
    // MIB_IF_TABLE2: NumEntries at 0, 4-byte align pad, Table[0] at 8.
    // MIB_IF_ROW2: sizeof = 1352, Type at +1128, InterfaceAndOperStatusFlags at +1152,
    // OperStatus at +1156, InOctets at +1208, OutOctets at +1280.
    private const int MibIfTable2RowsOffset = 8;
    private const int MibIfRow2Size = 1352;
    private const int MibIfRow2TypeOffset = 1128;
    private const int MibIfRow2FlagsOffset = 1152;
    private const int MibIfRow2OperStatusOffset = 1156;
    private const int MibIfRow2InOctetsOffset = 1208;
    private const int MibIfRow2OutOctetsOffset = 1280;

    private const int IfOperStatusUp = 1;
    private const int IfTypeSoftwareLoopback = 24;
    private const int IfTypeTunnel = 131;
    private const byte FilterInterfaceFlagMask = 0x02;

    // DXGI constants & vtable slots (verified against shared/dxgi.h & shared/dxgi1_4.h)
    private static readonly Guid IidIDxgiFactory1 = new("770AAE78-F26F-4DBA-A829-253C83D1B387");
    private static readonly Guid IidIDxgiAdapter3 = new("645967A4-1392-4310-A798-8053CE3E93FD");
    private const int DxgiFactory1EnumAdapters1Slot = 12;
    private const int DxgiAdapter1GetDesc1Slot = 10;
    private const int DxgiAdapter3QueryVideoMemoryInfoSlot = 14;
    private const uint DxgiAdapterFlagSoftware = 2;
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const int DxgiMemorySegmentGroupLocal = 0;
    private const int DxgiMemorySegmentGroupNonLocal = 1;

    // PDH constants (verified against um/pdh.h & um/pdhmsg.h)
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhCstatusValidData = 0x00000000;
    private const uint PdhCstatusNewData = 0x00000001;
    private const int PdhCounterItemSize64 = 24; // LPWSTR (8) + DWORD CStatus (4) + pad (4) + double (8)

    // NTDLL SystemProcessInformation offsets on 64-bit Windows (x64 & ARM64):
    // Verified against Windows SDK ntexapi.h SYSTEM_PROCESS_INFORMATION:
    // +0 NextEntryOffset (4), +8 WorkingSetPrivateSize (8), +40 UserTime (8),
    // +48 KernelTime (8), +56 ImageName.Length (2), +64 ImageName.Buffer (8),
    // +80 UniqueProcessId (8), +144 WorkingSetSize (8).
    private const int SystemProcessInformationClass = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int SpiWorkingSetPrivateSizeOffset = 8;
    private const int SpiUserTimeOffset = 40;
    private const int SpiKernelTimeOffset = 48;
    private const int SpiImageNameLengthOffset = 56;
    private const int SpiImageNameBufferOffset = 64;
    private const int SpiUniqueProcessIdOffset = 80;
    private const int SpiWorkingSetSizeOffset = 144;
    private const int SpiMinStructSize = 152;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemFileTime
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [InlineArray(128)]
    internal struct DescriptionBuffer128
    {
        private ushort _element0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DxgiAdapterDesc1
    {
        public DescriptionBuffer128 Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public Luid AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DxgiQueryVideoMemoryInfo
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }

    internal readonly record struct RawProcessSample(
        uint ProcessId,
        string ExeName,
        ulong CpuTicks,
        long PrivateMemoryBytes);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Invoker(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Invoker(IntPtr self, out DxgiAdapterDesc1 desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryVideoMemoryInfoInvoker(
        IntPtr self,
        uint nodeIndex,
        int memorySegmentGroup,
        out DxgiQueryVideoMemoryInfo info);

    [DllImport("kernel32.dll", EntryPoint = "GetSystemTimes", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemTimes(
        out SystemFileTime lpIdleTime,
        out SystemFileTime lpKernelTime,
        out SystemFileTime lpUserTime);

    [DllImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [DllImport("kernel32.dll", EntryPoint = "GetSystemPowerStatus", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemPowerStatus(out SystemPowerStatus lpSystemPowerStatus);

    [DllImport("iphlpapi.dll", EntryPoint = "GetIfTable2", ExactSpelling = true)]
    private static extern int GetIfTable2Native(out IntPtr table);

    [DllImport("iphlpapi.dll", EntryPoint = "FreeMibTable", ExactSpelling = true)]
    private static extern void FreeMibTableNative(IntPtr memory);

    [DllImport("dxgi.dll", EntryPoint = "CreateDXGIFactory1", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1Native(ref Guid riid, out IntPtr ppFactory);

    [DllImport("gdi32.dll", EntryPoint = "D3DKMTQueryStatistics", ExactSpelling = true)]
    private static extern int D3DKMTQueryStatisticsNative(IntPtr queryStatistics);

    // Verified against Windows SDK 10.0.26100.0 shared/d3dkmthk.h (64-bit layout):
    // D3DKMT_QUERYSTATISTICS: sizeof = 0x328, Type at +0x00, AdapterLuid at +0x04,
    // hProcess at +0x10, QueryResult at +0x18, QuerySegment/QueryNode input union at +0x320.
    // AdapterInformation: NbSegments at +0x18, NodeCount at +0x1C.
    // SegmentInformation: CommitLimit at +0x18, BytesCommitted at +0x20, BytesResident at +0x28, Aperture at +0x40.
    private const int D3dkmtQueryStatisticsSizeBytes = 0x328;
    private const int D3dkmtQueryStatisticsAdapter = 0;
    private const int D3dkmtQueryStatisticsSegment = 3;
    private const int D3dkmtQueryStatisticsNode = 5;
    private const int D3dkmtAdapterNbSegmentsOffset = 0x18;
    private const int D3dkmtAdapterNodeCountOffset = 0x1C;
    private const int D3dkmtNodeRunningTimeOffset = 0x18;
    private const int D3dkmtSegmentCommitLimitOffset = 0x18;
    private const int D3dkmtSegmentBytesCommittedOffset = 0x20;
    private const int D3dkmtSegmentBytesResidentOffset = 0x28;
    private const int D3dkmtSegmentApertureOffset = 0x40;
    private const int D3dkmtQuerySegmentIdOffset = 0x320;
    private const int D3dkmtQueryNodeIdOffset = 0x320;

    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", ExactSpelling = true)]
    private static extern uint PdhOpenQueryNative(IntPtr szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterNative(
        IntPtr hQuery,
        string szFullCounterPath,
        IntPtr dwUserData,
        out IntPtr phCounter);

    [DllImport("pdh.dll", EntryPoint = "PdhCollectQueryData", ExactSpelling = true)]
    private static extern uint PdhCollectQueryDataNative(IntPtr hQuery);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayNative(
        IntPtr hCounter,
        uint dwFormat,
        ref uint lpdwBufferSize,
        out uint lpdwItemCount,
        IntPtr itemBuffer);

    [DllImport("pdh.dll", EntryPoint = "PdhCloseQuery", ExactSpelling = true)]
    private static extern uint PdhCloseQueryNative(IntPtr hQuery);

    [DllImport("ntdll.dll", EntryPoint = "NtQuerySystemInformation", ExactSpelling = true)]
    private static extern int NtQuerySystemInformationNative(
        int systemInformationClass,
        IntPtr systemInformation,
        uint systemInformationLength,
        out uint returnLength);

    /// <summary>
    /// Sums active non-loopback, non-tunnel, non-filter adapter octet counters
    /// directly from the native <c>MIB_IF_TABLE2</c> buffer without allocating
    /// managed <c>NetworkInterface[]</c> graphs on every sample tick.
    /// </summary>
    internal static bool TryReadActiveNetworkTotals(out long receivedBytes, out long sentBytes)
    {
        receivedBytes = 0;
        sentBytes = 0;

        IntPtr tablePtr = IntPtr.Zero;
        try
        {
            if (GetIfTable2Native(out tablePtr) != 0 || tablePtr == IntPtr.Zero)
            {
                return false;
            }

            int count = Marshal.ReadInt32(tablePtr);
            if (count < 0 || count > 4096)
            {
                return false;
            }

            long rx = 0;
            long tx = 0;
            for (int i = 0; i < count; i++)
            {
                IntPtr rowPtr = IntPtr.Add(tablePtr, MibIfTable2RowsOffset + (i * MibIfRow2Size));
                int operStatus = Marshal.ReadInt32(rowPtr, MibIfRow2OperStatusOffset);
                if (operStatus != IfOperStatusUp)
                {
                    continue;
                }

                int ifType = Marshal.ReadInt32(rowPtr, MibIfRow2TypeOffset);
                if (ifType is IfTypeSoftwareLoopback or IfTypeTunnel)
                {
                    continue;
                }

                byte flags = Marshal.ReadByte(rowPtr, MibIfRow2FlagsOffset);
                if ((flags & FilterInterfaceFlagMask) != 0)
                {
                    continue;
                }

                ulong inOctets = unchecked((ulong)Marshal.ReadInt64(rowPtr, MibIfRow2InOctetsOffset));
                ulong outOctets = unchecked((ulong)Marshal.ReadInt64(rowPtr, MibIfRow2OutOctetsOffset));
                rx = unchecked(rx + (long)inOctets);
                tx = unchecked(tx + (long)outOctets);
            }

            receivedBytes = Math.Max(0, rx);
            sentBytes = Math.Max(0, tx);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (tablePtr != IntPtr.Zero)
            {
                FreeMibTableNative(tablePtr);
            }
        }
    }

    /// <summary>
    /// Queries primary hardware GPU name and video memory usage via DXGI vtable slots.
    /// Automatically uses Dedicated VRAM on discrete GPUs and falls back to Shared GPU memory
    /// on integrated GPUs so laptops/APUs never show 0/0 GB.
    /// </summary>
    internal static bool TryReadPrimaryGpuVideoMemory(
        out string gpuShortName,
        out ulong usedBytes,
        out ulong totalBytes,
        out bool isSharedMemory)
    {
        gpuShortName = "GPU";
        usedBytes = 0;
        totalBytes = 0;
        isSharedMemory = false;

        IntPtr factory = IntPtr.Zero;
        IntPtr bestAdapter = IntPtr.Zero;
        try
        {
            Guid factoryIid = IidIDxgiFactory1;
            if (CreateDXGIFactory1Native(ref factoryIid, out factory) < 0 || factory == IntPtr.Zero)
            {
                return false;
            }

            var enumAdapters = ComVtable.Slot<EnumAdapters1Invoker>(factory, DxgiFactory1EnumAdapters1Slot);
            DxgiAdapterDesc1 bestDesc = default;
            ulong bestScore = 0;

            for (uint index = 0; index < 16; index++)
            {
                int hr = enumAdapters(factory, index, out IntPtr candidate);
                if (hr == DxgiErrorNotFound || hr < 0 || candidate == IntPtr.Zero)
                {
                    break;
                }

                try
                {
                    var getDesc1 = ComVtable.Slot<GetDesc1Invoker>(candidate, DxgiAdapter1GetDesc1Slot);
                    if (getDesc1(candidate, out DxgiAdapterDesc1 desc) < 0)
                    {
                        continue;
                    }

                    if ((desc.Flags & DxgiAdapterFlagSoftware) != 0)
                    {
                        continue;
                    }

                    ulong dedicated = desc.DedicatedVideoMemory;
                    ulong shared = desc.SharedSystemMemory;
                    // Prefer discrete GPUs with >= 512 MB dedicated VRAM, otherwise highest total memory
                    ulong score = dedicated >= 512UL * 1024 * 1024
                        ? (1UL << 56) + dedicated
                        : dedicated + shared;

                    if (bestAdapter == IntPtr.Zero || score > bestScore)
                    {
                        if (bestAdapter != IntPtr.Zero)
                        {
                            Marshal.Release(bestAdapter);
                        }
                        bestAdapter = candidate;
                        candidate = IntPtr.Zero; // Ownership transferred
                        bestDesc = desc;
                        bestScore = score;
                    }
                }
                finally
                {
                    if (candidate != IntPtr.Zero)
                    {
                        Marshal.Release(candidate);
                    }
                }
            }

            if (bestAdapter == IntPtr.Zero)
            {
                return false;
            }

            gpuShortName = FormatShortGpuName(ReadDescriptionString(in bestDesc.Description));

            ulong dedicatedTotal = bestDesc.DedicatedVideoMemory;
            ulong sharedTotal = bestDesc.SharedSystemMemory;
            bool useShared = dedicatedTotal < 512UL * 1024 * 1024 && sharedTotal > 0;
            isSharedMemory = useShared;

            int uncachedSegCount = 0;
            if (IntPtr.Size == 8 &&
                (bestDesc.AdapterLuid.LowPart != 0 || bestDesc.AdapterLuid.HighPart != 0) &&
                TryQueryD3dkmtVideoMemory(
                    bestDesc.AdapterLuid,
                    useShared,
                    ref uncachedSegCount,
                    out ulong d3dkmtResidentBytes,
                    out ulong d3dkmtCommitLimitBytes))
            {
                totalBytes = useShared
                    ? (sharedTotal > 0 ? sharedTotal : d3dkmtCommitLimitBytes)
                    : (dedicatedTotal > 0 ? dedicatedTotal : d3dkmtCommitLimitBytes);
                usedBytes = totalBytes > 0 ? Math.Min(d3dkmtResidentBytes, totalBytes) : d3dkmtResidentBytes;
                return totalBytes > 0;
            }

            Guid adapter3Iid = IidIDxgiAdapter3;
            if (ComVtable.QueryInterface(bestAdapter, ref adapter3Iid, out IntPtr adapter3) >= 0 &&
                adapter3 != IntPtr.Zero)
            {
                try
                {
                    if (TryQueryDxgiVideoMemory(adapter3, useShared, dedicatedTotal, sharedTotal, out usedBytes, out totalBytes))
                    {
                        return true;
                    }
                }
                finally
                {
                    Marshal.Release(adapter3);
                }
            }

            totalBytes = useShared ? sharedTotal : dedicatedTotal;
            return totalBytes > 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (bestAdapter != IntPtr.Zero)
            {
                Marshal.Release(bestAdapter);
            }
            if (factory != IntPtr.Zero)
            {
                Marshal.Release(factory);
            }
        }
    }

    private static string ReadDescriptionString(in DescriptionBuffer128 buffer)
    {
        ReadOnlySpan<ushort> chars = buffer;
        int len = chars.IndexOf((ushort)0);
        if (len < 0)
        {
            len = chars.Length;
        }
        return len == 0
            ? "GPU"
            : MemoryMarshal.Cast<ushort, char>(chars[..len]).ToString().Trim();
    }

    internal static string FormatShortGpuName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return "GPU";
        }

        string s = rawName.Trim()
            .Replace("AMD Radeon(TM) RX ", "RX ", StringComparison.OrdinalIgnoreCase)
            .Replace("AMD Radeon RX ", "RX ", StringComparison.OrdinalIgnoreCase);

        string[] stripTokens =
        [
            "NVIDIA GeForce ",
            "NVIDIA ",
            "AMD ",
            "Intel(R) ",
            "Qualcomm(R) ",
            "(TM)",
            "(R)",
            " Laptop GPU",
            " Series",
            " Graphics"
        ];

        foreach (string token in stripTokens)
        {
            s = s.Replace(token, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        s = s.Trim();
        if (s.Length > 18)
        {
            s = s[..18].TrimEnd();
        }

        return string.IsNullOrWhiteSpace(s) ? "GPU" : s;
    }

    private static bool TryQueryDxgiVideoMemory(
        IntPtr adapter3,
        bool useShared,
        ulong dedicatedTotal,
        ulong sharedTotal,
        out ulong usedBytes,
        out ulong totalBytes)
    {
        usedBytes = 0;
        totalBytes = 0;
        var queryMem = ComVtable.Slot<QueryVideoMemoryInfoInvoker>(
            adapter3,
            DxgiAdapter3QueryVideoMemoryInfoSlot);

        if (useShared)
        {
            // UMA integrated GPUs expose their primary shared memory pool under Local (0) with NonLocal (1) Budget == 0.
            int hr = queryMem(adapter3, 0, DxgiMemorySegmentGroupNonLocal, out DxgiQueryVideoMemoryInfo memInfo);
            if (hr < 0 || memInfo.Budget == 0)
            {
                hr = queryMem(adapter3, 0, DxgiMemorySegmentGroupLocal, out memInfo);
            }

            if (hr >= 0)
            {
                totalBytes = memInfo.Budget > 0 ? memInfo.Budget : sharedTotal;
                usedBytes = totalBytes > 0 ? Math.Min(memInfo.CurrentUsage, totalBytes) : memInfo.CurrentUsage;
                return totalBytes > 0;
            }

            return false;
        }

        if (queryMem(adapter3, 0, DxgiMemorySegmentGroupLocal, out DxgiQueryVideoMemoryInfo localInfo) >= 0)
        {
            totalBytes = dedicatedTotal > 0 ? dedicatedTotal : localInfo.Budget;
            usedBytes = totalBytes > 0 ? Math.Min(localInfo.CurrentUsage, totalBytes) : localInfo.CurrentUsage;
            return totalBytes > 0;
        }

        return false;
    }

    private static unsafe bool TryQueryD3dkmtVideoMemory(
        Luid adapterLuid,
        bool useShared,
        ref int cachedSegmentCount,
        out ulong residentBytes,
        out ulong commitLimitBytes)
    {
        residentBytes = 0;
        commitLimitBytes = 0;

        try
        {
            byte* buf = stackalloc byte[D3dkmtQueryStatisticsSizeBytes];
            if (cachedSegmentCount <= 0)
            {
                new Span<byte>(buf, D3dkmtQueryStatisticsSizeBytes).Clear();
                *(int*)(buf + 0x00) = D3dkmtQueryStatisticsAdapter;
                *(uint*)(buf + 0x04) = adapterLuid.LowPart;
                *(int*)(buf + 0x08) = adapterLuid.HighPart;

                if (D3DKMTQueryStatisticsNative((IntPtr)buf) != 0)
                {
                    return false;
                }

                uint nbSegments = *(uint*)(buf + D3dkmtAdapterNbSegmentsOffset);
                if (nbSegments == 0 || nbSegments > 64)
                {
                    return false;
                }

                cachedSegmentCount = (int)nbSegments;
            }

            ulong dedResident = 0;
            ulong dedCommitLimit = 0;
            ulong apertureResident = 0;
            ulong apertureCommitLimit = 0;

            for (uint seg = 0; seg < (uint)cachedSegmentCount; seg++)
            {
                new Span<byte>(buf, D3dkmtQueryStatisticsSizeBytes).Clear();
                *(int*)(buf + 0x00) = D3dkmtQueryStatisticsSegment;
                *(uint*)(buf + 0x04) = adapterLuid.LowPart;
                *(int*)(buf + 0x08) = adapterLuid.HighPart;
                *(uint*)(buf + D3dkmtQuerySegmentIdOffset) = seg;

                if (D3DKMTQueryStatisticsNative((IntPtr)buf) != 0)
                {
                    cachedSegmentCount = 0;
                    return false;
                }

                ulong commitLimit = *(ulong*)(buf + D3dkmtSegmentCommitLimitOffset);
                ulong bytesResident = *(ulong*)(buf + D3dkmtSegmentBytesResidentOffset);
                uint aperture = *(uint*)(buf + D3dkmtSegmentApertureOffset);

                if (aperture == 0)
                {
                    dedResident += bytesResident;
                    dedCommitLimit += commitLimit;
                }
                else
                {
                    apertureResident += bytesResident;
                    apertureCommitLimit += commitLimit;
                }
            }

            if (!useShared)
            {
                if (dedCommitLimit > 0)
                {
                    residentBytes = dedResident;
                    commitLimitBytes = dedCommitLimit;
                    return true;
                }

                return false;
            }

            if (apertureCommitLimit > 0)
            {
                residentBytes = apertureResident;
                commitLimitBytes = apertureCommitLimit;
                return true;
            }

            if (dedCommitLimit > 0)
            {
                residentBytes = dedResident;
                commitLimitBytes = dedCommitLimit;
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            cachedSegmentCount = 0;
            return false;
        }
    }

    /// <summary>
    /// Persistent DXGI/D3DKMT adapter sampler that resolves the primary hardware GPU once, queries
    /// system-wide VRAM residency via <c>D3DKMTQueryStatistics</c> (matching Task Manager), and
    /// falls back to cached <c>IDXGIAdapter3</c> without <c>CreateDXGIFactory1</c> churn on every poll.
    /// </summary>
    internal sealed class DxgiVramSampler : IDisposable
    {
        private IntPtr _adapter3;
        private Luid _adapterLuid;
        private bool _hasAdapterLuid;
        private int _d3dkmtSegmentCount;
        private bool _d3dkmtUnavailable;
        private string _gpuShortName = "GPU";
        private ulong _dedicatedTotal;
        private ulong _sharedTotal;
        private bool _useShared;
        private bool _unavailable;

        public bool IsOpen => _adapter3 != IntPtr.Zero || _hasAdapterLuid;

        public bool HasAdapterLuid => _hasAdapterLuid;

        public Luid AdapterLuid => _adapterLuid;

        public bool TrySampleVideoMemory(
            out string gpuShortName,
            out ulong usedBytes,
            out ulong totalBytes,
            out bool isSharedMemory)
        {
            gpuShortName = _gpuShortName;
            usedBytes = 0;
            totalBytes = 0;
            isSharedMemory = _useShared;

            if (_unavailable)
            {
                return false;
            }

            if (_adapter3 == IntPtr.Zero && !_hasAdapterLuid && !TryEnsureAdapter())
            {
                return false;
            }

            gpuShortName = _gpuShortName;
            isSharedMemory = _useShared;

            if (_hasAdapterLuid && !_d3dkmtUnavailable && IntPtr.Size == 8)
            {
                if (TryQueryD3dkmtVideoMemory(
                        _adapterLuid,
                        _useShared,
                        ref _d3dkmtSegmentCount,
                        out ulong residentBytes,
                        out ulong commitLimitBytes))
                {
                    totalBytes = _useShared
                        ? (_sharedTotal > 0 ? _sharedTotal : commitLimitBytes)
                        : (_dedicatedTotal > 0 ? _dedicatedTotal : commitLimitBytes);
                    usedBytes = totalBytes > 0 ? Math.Min(residentBytes, totalBytes) : residentBytes;
                    return totalBytes > 0;
                }

                if (_d3dkmtSegmentCount == 0 && _adapter3 == IntPtr.Zero)
                {
                    _d3dkmtUnavailable = true;
                }
            }

            if (_adapter3 != IntPtr.Zero)
            {
                if (TryQueryDxgiVideoMemory(_adapter3, _useShared, _dedicatedTotal, _sharedTotal, out usedBytes, out totalBytes))
                {
                    return true;
                }

                // Device reset / GPU hotplug: release stale adapter and retry on next tick.
                Dispose();
            }

            totalBytes = _useShared ? _sharedTotal : _dedicatedTotal;
            return totalBytes > 0;
        }

        private bool TryEnsureAdapter()
        {
            IntPtr factory = IntPtr.Zero;
            IntPtr bestAdapter = IntPtr.Zero;
            try
            {
                Guid factoryIid = IidIDxgiFactory1;
                if (CreateDXGIFactory1Native(ref factoryIid, out factory) < 0 || factory == IntPtr.Zero)
                {
                    return false;
                }

                var enumAdapters = ComVtable.Slot<EnumAdapters1Invoker>(factory, DxgiFactory1EnumAdapters1Slot);
                DxgiAdapterDesc1 bestDesc = default;
                ulong bestScore = 0;

                for (uint index = 0; index < 16; index++)
                {
                    int hr = enumAdapters(factory, index, out IntPtr candidate);
                    if (hr == DxgiErrorNotFound || hr < 0 || candidate == IntPtr.Zero)
                    {
                        break;
                    }

                    try
                    {
                        var getDesc1 = ComVtable.Slot<GetDesc1Invoker>(candidate, DxgiAdapter1GetDesc1Slot);
                        if (getDesc1(candidate, out DxgiAdapterDesc1 desc) < 0 ||
                            (desc.Flags & DxgiAdapterFlagSoftware) != 0)
                        {
                            continue;
                        }

                        ulong dedicated = desc.DedicatedVideoMemory;
                        ulong shared = desc.SharedSystemMemory;
                        ulong score = dedicated >= 512UL * 1024 * 1024
                            ? (1UL << 56) + dedicated
                            : dedicated + shared;

                        if (bestAdapter == IntPtr.Zero || score > bestScore)
                        {
                            if (bestAdapter != IntPtr.Zero)
                            {
                                Marshal.Release(bestAdapter);
                            }

                            bestAdapter = candidate;
                            candidate = IntPtr.Zero;
                            bestDesc = desc;
                            bestScore = score;
                        }
                    }
                    finally
                    {
                        if (candidate != IntPtr.Zero)
                        {
                            Marshal.Release(candidate);
                        }
                    }
                }

                if (bestAdapter == IntPtr.Zero)
                {
                    return false;
                }

                _gpuShortName = FormatShortGpuName(ReadDescriptionString(in bestDesc.Description));
                _adapterLuid = bestDesc.AdapterLuid;
                _hasAdapterLuid = bestDesc.AdapterLuid.LowPart != 0 || bestDesc.AdapterLuid.HighPart != 0;
                _dedicatedTotal = bestDesc.DedicatedVideoMemory;
                _sharedTotal = bestDesc.SharedSystemMemory;
                _useShared = _dedicatedTotal < 512UL * 1024 * 1024 && _sharedTotal > 0;

                Guid adapter3Iid = IidIDxgiAdapter3;
                if (ComVtable.QueryInterface(bestAdapter, ref adapter3Iid, out IntPtr adapter3) >= 0 &&
                    adapter3 != IntPtr.Zero)
                {
                    _adapter3 = adapter3;
                }

                return (_useShared ? _sharedTotal : _dedicatedTotal) > 0 || _adapter3 != IntPtr.Zero;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _unavailable = true;
                return false;
            }
            finally
            {
                if (bestAdapter != IntPtr.Zero)
                {
                    Marshal.Release(bestAdapter);
                }

                if (factory != IntPtr.Zero)
                {
                    Marshal.Release(factory);
                }
            }
        }

        public void Dispose()
        {
            if (_adapter3 != IntPtr.Zero)
            {
                Marshal.Release(_adapter3);
                _adapter3 = IntPtr.Zero;
            }

            _adapterLuid = default;
            _hasAdapterLuid = false;
            _d3dkmtSegmentCount = 0;
            _d3dkmtUnavailable = false;
        }
    }

    /// <summary>
    /// Persistent, zero-per-tick-allocation GPU Engine utilization sampler.
    /// Queries kernel WDDM adapter node running times directly via <c>D3DKMTQueryStatistics</c>
    /// (avoiding <c>pdh.dll</c>'s multi-megabyte wildcard counter heap and per-process table scan),
    /// and falls back to PDH only if kernel node statistics are unavailable.
    /// </summary>
    internal sealed class GpuEngineCounterSampler : IDisposable
    {
        private IntPtr _queryHandle;
        private IntPtr _counterHandle;
        private IntPtr _nativeBuffer;
        private uint _nativeBufferBytes;
        private bool _primed;
        private bool _unavailable;
        private bool _d3dkmtUnavailable;
        private bool _d3dkmtPrimed;
        private Luid _d3dkmtLuid;
        private int _d3dkmtNodeCount;
        private long _prevD3dkmtTimestamp;
        private long[]? _prevNodeRunningTicks;
        private Dictionary<string, double>? _engineSums;
        private List<string>? _knownEngineKeys;

        public bool IsOpen => _queryHandle != IntPtr.Zero || _d3dkmtNodeCount > 0;

        public bool TrySampleGpuPercent(bool hasAdapterLuid, Luid adapterLuid, out double gpuPercent) =>
            TrySampleGpuPercent(hasAdapterLuid, adapterLuid, out gpuPercent, out _);

        public bool TrySampleGpuPercent(
            bool hasAdapterLuid,
            Luid adapterLuid,
            out double gpuPercent,
            out bool hasRateDelta)
        {
            if (hasAdapterLuid &&
                !_d3dkmtUnavailable &&
                IntPtr.Size == 8 &&
                TrySampleD3dkmtGpuPercent(adapterLuid, out gpuPercent, out hasRateDelta))
            {
                if (_queryHandle != IntPtr.Zero || _nativeBuffer != IntPtr.Zero)
                {
                    ReleasePdhResources();
                }

                return true;
            }

            return TrySampleGpuPercent(out gpuPercent, out hasRateDelta);
        }

        private unsafe bool TrySampleD3dkmtGpuPercent(
            Luid adapterLuid,
            out double gpuPercent,
            out bool hasRateDelta)
        {
            gpuPercent = 0;
            hasRateDelta = false;
            try
            {
                byte* buf = stackalloc byte[D3dkmtQueryStatisticsSizeBytes];
                if (_d3dkmtNodeCount <= 0 ||
                    _prevNodeRunningTicks is null ||
                    _d3dkmtLuid.LowPart != adapterLuid.LowPart ||
                    _d3dkmtLuid.HighPart != adapterLuid.HighPart)
                {
                    new Span<byte>(buf, D3dkmtQueryStatisticsSizeBytes).Clear();
                    *(int*)(buf + 0x00) = D3dkmtQueryStatisticsAdapter;
                    *(uint*)(buf + 0x04) = adapterLuid.LowPart;
                    *(int*)(buf + 0x08) = adapterLuid.HighPart;

                    if (D3DKMTQueryStatisticsNative((IntPtr)buf) != 0)
                    {
                        _d3dkmtUnavailable = true;
                        return false;
                    }

                    uint nodeCount = *(uint*)(buf + D3dkmtAdapterNodeCountOffset);
                    if (nodeCount == 0 || nodeCount > 128)
                    {
                        _d3dkmtUnavailable = true;
                        return false;
                    }

                    _d3dkmtLuid = adapterLuid;
                    _d3dkmtNodeCount = (int)nodeCount;
                    _prevNodeRunningTicks = new long[_d3dkmtNodeCount];
                    _d3dkmtPrimed = false;
                }

                long nowTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                long maxNodeDelta100Ns = 0;
                long[] prevTicks = _prevNodeRunningTicks;

                for (uint node = 0; node < (uint)_d3dkmtNodeCount; node++)
                {
                    new Span<byte>(buf, D3dkmtQueryStatisticsSizeBytes).Clear();
                    *(int*)(buf + 0x00) = D3dkmtQueryStatisticsNode;
                    *(uint*)(buf + 0x04) = adapterLuid.LowPart;
                    *(int*)(buf + 0x08) = adapterLuid.HighPart;
                    *(uint*)(buf + D3dkmtQueryNodeIdOffset) = node;

                    if (D3DKMTQueryStatisticsNative((IntPtr)buf) != 0)
                    {
                        _d3dkmtNodeCount = 0;
                        _d3dkmtPrimed = false;
                        return false;
                    }

                    long runningTime100Ns = *(long*)(buf + D3dkmtNodeRunningTimeOffset);
                    long prev = prevTicks[node];
                    prevTicks[node] = runningTime100Ns;
                    if (_d3dkmtPrimed && runningTime100Ns > prev)
                    {
                        long delta = runningTime100Ns - prev;
                        if (delta > maxNodeDelta100Ns)
                        {
                            maxNodeDelta100Ns = delta;
                        }
                    }
                }

                if (!_d3dkmtPrimed)
                {
                    _prevD3dkmtTimestamp = nowTimestamp;
                    _d3dkmtPrimed = true;
                    gpuPercent = 0;
                    hasRateDelta = false;
                    return true;
                }

                double elapsed100Ns = System.Diagnostics.Stopwatch
                    .GetElapsedTime(_prevD3dkmtTimestamp, nowTimestamp)
                    .TotalSeconds * 10_000_000.0;
                _prevD3dkmtTimestamp = nowTimestamp;
                if (elapsed100Ns <= 10_000.0)
                {
                    return true;
                }

                gpuPercent = Math.Clamp((maxNodeDelta100Ns / elapsed100Ns) * 100.0, 0.0, 100.0);
                hasRateDelta = true;
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _d3dkmtUnavailable = true;
                return false;
            }
        }

        public bool TrySampleGpuPercent(out double gpuPercent) =>
            TrySampleGpuPercent(out gpuPercent, out _);

        public bool TrySampleGpuPercent(out double gpuPercent, out bool hasRateDelta)
        {
            gpuPercent = 0;
            hasRateDelta = false;
            if (_unavailable)
            {
                return false;
            }

            try
            {
                if (_queryHandle == IntPtr.Zero)
                {
                    if (PdhOpenQueryNative(IntPtr.Zero, IntPtr.Zero, out _queryHandle) != 0 ||
                        _queryHandle == IntPtr.Zero)
                    {
                        _unavailable = true;
                        return false;
                    }

                    if (PdhAddEnglishCounterNative(
                            _queryHandle,
                            @"\GPU Engine(*engtype_*)\Utilization Percentage",
                            IntPtr.Zero,
                            out _counterHandle) != 0 ||
                        _counterHandle == IntPtr.Zero)
                    {
                        Dispose();
                        _unavailable = true;
                        return false;
                    }

                    // Rate counters require two samples separated in time; prime first baseline.
                    _ = PdhCollectQueryDataNative(_queryHandle);
                    _primed = true;
                    hasRateDelta = false;
                    return true;
                }

                if (PdhCollectQueryDataNative(_queryHandle) != 0)
                {
                    return false;
                }

                uint requiredBytes = _nativeBufferBytes;
                uint itemCount = 0;
                uint status = PdhGetFormattedCounterArrayNative(
                    _counterHandle,
                    PdhFmtDouble,
                    ref requiredBytes,
                    out itemCount,
                    _nativeBuffer);

                if (status == PdhMoreData && requiredBytes > 0 && requiredBytes <= 16 * 1024 * 1024)
                {
                    EnsureBuffer(requiredBytes + 4096);
                    requiredBytes = _nativeBufferBytes;
                    status = PdhGetFormattedCounterArrayNative(
                        _counterHandle,
                        PdhFmtDouble,
                        ref requiredBytes,
                        out itemCount,
                        _nativeBuffer);
                }

                if (status != 0 || itemCount == 0 || _nativeBuffer == IntPtr.Zero)
                {
                    return _primed;
                }

                Dictionary<string, double> engineSums = _engineSums ??= new Dictionary<string, double>(StringComparer.Ordinal);
                List<string> knownEngineKeys = _knownEngineKeys ??= new List<string>(16);

                if (knownEngineKeys.Count > 64)
                {
                    engineSums.Clear();
                    knownEngineKeys.Clear();
                }
                else
                {
                    for (int k = 0; k < knownEngineKeys.Count; k++)
                    {
                        engineSums[knownEngineKeys[k]] = 0.0;
                    }
                }

                Dictionary<string, double>.AlternateLookup<ReadOnlySpan<char>> spanLookup =
                    engineSums.GetAlternateLookup<ReadOnlySpan<char>>();
                int count = (int)Math.Min(itemCount, 16384);
                for (int i = 0; i < count; i++)
                {
                    IntPtr itemPtr = IntPtr.Add(_nativeBuffer, i * PdhCounterItemSize64);
                    uint cstatus = (uint)Marshal.ReadInt32(itemPtr, 8);
                    if (cstatus != PdhCstatusValidData && cstatus != PdhCstatusNewData)
                    {
                        continue;
                    }

                    long bits = Marshal.ReadInt64(itemPtr, 16);
                    double value = BitConverter.Int64BitsToDouble(bits);
                    if (value <= 0.01 || double.IsNaN(value) || double.IsInfinity(value))
                    {
                        continue;
                    }

                    IntPtr namePtr = Marshal.ReadIntPtr(itemPtr, 0);
                    ReadOnlySpan<char> engineKey = ExtractEngineKeySpan(namePtr);
                    if (spanLookup.TryGetValue(engineKey, out double current))
                    {
                        spanLookup[engineKey] = current + value;
                    }
                    else
                    {
                        string allocatedKey = engineKey.ToString();
                        engineSums[allocatedKey] = value;
                        knownEngineKeys.Add(allocatedKey);
                    }
                }

                double maxEngine = 0;
                foreach (double engineTotal in engineSums.Values)
                {
                    if (engineTotal > maxEngine)
                    {
                        maxEngine = engineTotal;
                    }
                }

                gpuPercent = Math.Clamp(maxEngine, 0.0, 100.0);
                hasRateDelta = true;
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _unavailable = true;
                Dispose();
                return false;
            }
        }

        internal static unsafe ReadOnlySpan<char> ExtractEngineKeySpan(IntPtr wideStrPtr)
        {
            if (wideStrPtr == IntPtr.Zero)
            {
                return "default".AsSpan();
            }

            ReadOnlySpan<char> name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((char*)wideStrPtr);
            return ExtractEngineKeySpan(name);
        }

        internal static ReadOnlySpan<char> ExtractEngineKeySpan(ReadOnlySpan<char> name)
        {
            if (name.IsEmpty)
            {
                return "default".AsSpan();
            }

            int physIdx = name.IndexOf("_phys_".AsSpan(), StringComparison.Ordinal);
            return physIdx >= 0 ? name[(physIdx + 1)..] : name;
        }

        private void EnsureBuffer(uint sizeBytes)
        {
            if (_nativeBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_nativeBuffer);
                _nativeBuffer = IntPtr.Zero;
                _nativeBufferBytes = 0;
            }

            _nativeBuffer = Marshal.AllocHGlobal((int)sizeBytes);
            _nativeBufferBytes = sizeBytes;
        }

        private void ReleasePdhResources()
        {
            if (_queryHandle != IntPtr.Zero)
            {
                _ = PdhCloseQueryNative(_queryHandle);
                _queryHandle = IntPtr.Zero;
                _counterHandle = IntPtr.Zero;
            }

            if (_nativeBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_nativeBuffer);
                _nativeBuffer = IntPtr.Zero;
                _nativeBufferBytes = 0;
            }

            _engineSums = null;
            _knownEngineKeys = null;
            _primed = false;
        }

        public void Dispose()
        {
            ReleasePdhResources();
            _d3dkmtPrimed = false;
            _d3dkmtNodeCount = 0;
            _d3dkmtLuid = default;
            _prevD3dkmtTimestamp = 0;
            _prevNodeRunningTicks = null;
        }
    }

    /// <summary>
    /// Reads all active user/service processes in a single NTDLL kernel snapshot without
    /// allocating <c>System.Diagnostics.Process</c> instances or opening process handles.
    /// </summary>
    internal sealed class SystemProcessSnapshotReader : IDisposable
    {
        private IntPtr _buffer;
        private uint _bufferSize;

        public bool TryReadProcesses(List<RawProcessSample> output)
        {
            output.Clear();
            if (IntPtr.Size != 8)
            {
                return false;
            }

            try
            {
                if (_buffer == IntPtr.Zero)
                {
                    AllocateBuffer(512 * 1024);
                }

                int status = NtQuerySystemInformationNative(
                    SystemProcessInformationClass,
                    _buffer,
                    _bufferSize,
                    out uint returnLength);

                int retries = 0;
                while (status == StatusInfoLengthMismatch && retries++ < 4)
                {
                    uint nextSize = Math.Max(returnLength + (64 * 1024), _bufferSize * 2);
                    if (nextSize > 32 * 1024 * 1024)
                    {
                        return false;
                    }

                    AllocateBuffer(nextSize);
                    status = NtQuerySystemInformationNative(
                        SystemProcessInformationClass,
                        _buffer,
                        _bufferSize,
                        out returnLength);
                }

                if (status < 0 || _buffer == IntPtr.Zero)
                {
                    return false;
                }

                int offset = 0;
                int maxOffset = (int)_bufferSize - SpiMinStructSize;
                int guard = 0;

                while (offset >= 0 && offset <= maxOffset && guard++ < 16384)
                {
                    IntPtr entryPtr = IntPtr.Add(_buffer, offset);
                    uint pid = unchecked((uint)Marshal.ReadIntPtr(entryPtr, SpiUniqueProcessIdOffset).ToInt64());

                    // Skip System Idle Process (0) and kernel System (4)
                    if (pid is not 0 and not 4)
                    {
                        ushort nameByteLen = unchecked((ushort)Marshal.ReadInt16(entryPtr, SpiImageNameLengthOffset));
                        IntPtr nameBufPtr = Marshal.ReadIntPtr(entryPtr, SpiImageNameBufferOffset);
                        int charLen = nameByteLen / 2;

                        if (charLen > 0 && charLen <= 260 && nameBufPtr != IntPtr.Zero)
                        {
                            string exeName = Marshal.PtrToStringUni(nameBufPtr, charLen);
                            if (!IsKernelPseudoProcess(exeName))
                            {
                                ulong userTicks = unchecked((ulong)Marshal.ReadInt64(entryPtr, SpiUserTimeOffset));
                                ulong kernelTicks = unchecked((ulong)Marshal.ReadInt64(entryPtr, SpiKernelTimeOffset));
                                long privateBytes = Marshal.ReadInt64(entryPtr, SpiWorkingSetPrivateSizeOffset);
                                if (privateBytes <= 0)
                                {
                                    privateBytes = Marshal.ReadIntPtr(entryPtr, SpiWorkingSetSizeOffset).ToInt64();
                                }

                                output.Add(new RawProcessSample(
                                    pid,
                                    exeName,
                                    unchecked(userTicks + kernelTicks),
                                    Math.Max(0, privateBytes)));
                            }
                        }
                    }

                    uint nextDelta = unchecked((uint)Marshal.ReadInt32(entryPtr, 0));
                    if (nextDelta == 0)
                    {
                        break;
                    }

                    offset += (int)nextDelta;
                }

                return output.Count > 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }

        private static bool IsKernelPseudoProcess(string exeName) =>
            string.Equals(exeName, "Registry", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(exeName, "MemCompression", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(exeName, "Memory Compression", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(exeName, "Secure System", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(exeName, "System Idle Process", StringComparison.OrdinalIgnoreCase);

        private void AllocateBuffer(uint sizeBytes)
        {
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
                _bufferSize = 0;
            }

            _buffer = Marshal.AllocHGlobal((int)sizeBytes);
            _bufferSize = sizeBytes;
        }

        public void Dispose()
        {
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
                _bufferSize = 0;
            }
        }
    }
}
