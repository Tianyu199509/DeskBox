extern alias GlancePkg;

using System.Runtime.InteropServices;
using DeskBox.Contracts;
using DeskBox.Services.Plugins;
using PackageExports = GlancePkg::DeskBox.GlancePackage.Abi.Exports;
using PackageBackground = GlancePkg::DeskBox.GlancePackage.Abi.Exports.DeskBoxCompactBackgroundV1;

namespace DeskBox.Tests;

/// <summary>
/// Headless ownership tests use an actual Cdecl thunk and opaque sentinel
/// pointers. They do not instantiate WinUI or prove NativeAOT/COM projection.
/// </summary>
public class NativeWidgetCompactBackgroundTests
{
    private const int EFail = unchecked((int)0x80004005);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BackgroundThunk(nint handle, nint background);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int StatusThunk(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ShutdownThunk();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ActivateThunk(nint packageRoot, int packageLength, nint dataRoot, int dataLength, nint hostApi);

    private sealed class Probe
    {
        internal readonly object Image = new();
        internal bool Live = true;
        internal int Status;
        internal nint Output = (nint)0xCAFE;
        internal int Calls;
        internal int Projections;
        internal int Releases;
        internal nint ReceivedHandle;
        internal nint InitialOutput;
        internal uint InitialSize;
        internal uint InitialVersion;
        internal double InitialOpacity;
        internal double Opacity = 0.35;
        internal uint ResponseSize = 24;
        internal uint ResponseVersion = 1;
        internal nint ProjectedPointer;
        internal nint ReleasedPointer;
        internal bool ThrowOnProjection;
        internal bool ThrowOnRelease;
        internal Action? OnCall;
        internal Action? OnProjection;

        internal (object ImageSource, double Opacity)? Read(bool hasExport = true, int handle = 0x1234)
        {
            BackgroundThunk thunk = (widget, output) =>
            {
                Calls++;
                ReceivedHandle = widget;
                var request = Marshal.PtrToStructure<NativeWidgetCompactBackgroundV1>(output);
                InitialOutput = request.ImageSource;
                InitialSize = request.Size;
                InitialVersion = request.Version;
                InitialOpacity = request.Opacity;
                request.ImageSource = Output;
                request.Opacity = Opacity;
                request.Size = ResponseSize;
                request.Version = ResponseVersion;
                Marshal.StructureToPtr(request, output, false);
                OnCall?.Invoke();
                return Status;
            };
            try
            {
                return NativeWidgetCompactBackground.Read<object>(
                    hasExport ? Marshal.GetFunctionPointerForDelegate(thunk) : 0,
                    handle, () => Live,
                    pointer =>
                    {
                        ProjectedPointer = pointer;
                        Projections++;
                        OnProjection?.Invoke();
                        if (ThrowOnProjection) throw new InvalidOperationException("projection failed");
                        return Image;
                    },
                    pointer =>
                    {
                        ReleasedPointer = pointer;
                        Releases++;
                        if (ThrowOnRelease) throw new InvalidOperationException("release failed");
                    });
            }
            finally
            {
                GC.KeepAlive(thunk);
                // Assert outside the contained-error seam so assertion
                // exceptions cannot be mistaken for projection failures.
                if (Projections != 0) Assert.Equal(Output, ProjectedPointer);
                if (Releases != 0) Assert.Equal(Output, ReleasedPointer);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SuccessfulHresultProjectsAndReleasesOwnedPointerExactlyOnce(int status)
    {
        var probe = new Probe { Status = status };
        var snapshot = probe.Read();
        Assert.True(snapshot.HasValue);
        Assert.Same(probe.Image, snapshot.Value.ImageSource);
        Assert.Equal(0.35, snapshot.Value.Opacity);
        Assert.Equal((nint)0x1234, probe.ReceivedHandle);
        Assert.Equal((nint)0, probe.InitialOutput);
        Assert.Equal(24u, probe.InitialSize);
        Assert.Equal(1u, probe.InitialVersion);
        Assert.Equal(0d, probe.InitialOpacity);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(1, probe.Projections);
        Assert.Equal(1, probe.Releases);
    }

    [Fact]
    public void MissingOptionalExportReturnsNullWithoutCallingOrMarshalling()
    {
        var probe = new Probe();
        Assert.Null(probe.Read(hasExport: false));
        Assert.Equal(0, probe.Calls);
        Assert.Equal(0, probe.Projections);
        Assert.Equal(0, probe.Releases);
    }

    [Fact]
    public void SuccessfulNoImageDoesNotProjectOrReleaseNull()
    {
        var probe = new Probe { Output = 0 };
        Assert.Null(probe.Read());
        Assert.Equal(1, probe.Calls);
        Assert.Equal(0, probe.Projections);
        Assert.Equal(0, probe.Releases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedHresultReleasesOnlyNonzeroPartialOutput(bool partialOutput)
    {
        var probe = new Probe { Status = EFail, Output = partialOutput ? (nint)0xCAFE : 0 };
        Assert.Null(probe.Read());
        Assert.Equal(1, probe.Calls);
        Assert.Equal(0, probe.Projections);
        Assert.Equal(partialOutput ? 1 : 0, probe.Releases);
    }

    [Fact]
    public void ProjectionExceptionStillReleasesOnceAndIsContained()
    {
        var probe = new Probe { ThrowOnProjection = true };
        Assert.Null(probe.Read());
        Assert.Equal(1, probe.Projections);
        Assert.Equal(1, probe.Releases);
    }

    [Fact]
    public void ReleaseExceptionIsContainedAndNeverRetried()
    {
        var probe = new Probe { ThrowOnRelease = true };
        Assert.Null(probe.Read());
        Assert.Equal(1, probe.Projections);
        Assert.Equal(1, probe.Releases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosedAccessOrZeroHandleDoesNotEnterPackage(bool zeroHandle)
    {
        var probe = new Probe { Live = zeroHandle };
        Assert.Null(probe.Read(handle: zeroHandle ? 0 : 0x1234));
        Assert.Equal(0, probe.Calls);
        Assert.Equal(0, probe.Projections);
        Assert.Equal(0, probe.Releases);
    }

    [Fact]
    public void ClosingDuringExportDiscardsAndReleasesOutputWithoutProjection()
    {
        var probe = new Probe();
        probe.OnCall = () => probe.Live = false;
        Assert.Null(probe.Read());
        Assert.Equal(1, probe.Calls);
        Assert.Equal(0, probe.Projections);
        Assert.Equal(1, probe.Releases);
    }

    [Fact]
    public void ClosingDuringProjectionDiscardsResultAndReleasesOutput()
    {
        var probe = new Probe();
        probe.OnProjection = () => probe.Live = false;
        Assert.Null(probe.Read());
        Assert.Equal(1, probe.Projections);
        Assert.Equal(1, probe.Releases);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void TransparentOrInvalidOpacityDiscardsImageAndReleasesOnce(double opacity)
    {
        var probe = new Probe { Opacity = opacity };
        Assert.Null(probe.Read());
        Assert.Equal(0, probe.Projections);
        Assert.Equal(1, probe.Releases);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(1d)]
    public void SnapshotPreservesInstanceOpacity(double opacity)
    {
        var probe = new Probe { Opacity = opacity };
        var result = probe.Read();
        Assert.True(result.HasValue);
        Assert.Equal(opacity, result.Value.Opacity);
        Assert.Same(probe.Image, result.Value.ImageSource);
        Assert.Equal(1, probe.Releases);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(23, 1)]
    [InlineData(32, 1)]
    [InlineData(24, 0)]
    [InlineData(24, 2)]
    public void ChangedResponseHeaderIsRejectedButOwnedPointerIsReleased(int size, int version)
    {
        var probe = new Probe { ResponseSize = (uint)size, ResponseVersion = (uint)version };
        Assert.Null(probe.Read());
        Assert.Equal(0, probe.Projections);
        Assert.Equal(1, probe.Releases);
    }

    [Fact]
    public void OptionalExportDeclaresVersionedCdeclSnapshotSignature()
    {
        var method = typeof(PackageExports).GetMethod(nameof(PackageExports.GetCompactBackground))!;
        Assert.Equal(typeof(int), method.ReturnType);
        var parameters = method.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(nint), parameters[0].ParameterType);
        Assert.True(parameters[1].ParameterType.IsPointer);
        Assert.Equal(typeof(PackageBackground), parameters[1].ParameterType.GetElementType());
        var export = Assert.IsType<UnmanagedCallersOnlyAttribute>(
            Attribute.GetCustomAttribute(method, typeof(UnmanagedCallersOnlyAttribute)));
        Assert.Equal("deskbox_widget_get_compact_background", export.EntryPoint);
        Assert.Contains(typeof(System.Runtime.CompilerServices.CallConvCdecl), export.CallConvs!);
    }

    [Fact]
    public void HostAndPackageSnapshotLayoutsMatch()
    {
        Assert.Equal(24, Marshal.SizeOf<NativeWidgetCompactBackgroundV1>());
        Assert.Equal(24, Marshal.SizeOf<PackageBackground>());
        string[] fields = ["Size", "Version", "ImageSource", "Opacity"];
        int[] offsets = [0, 4, 8, 16];
        for (int i = 0; i < fields.Length; i++)
        {
            Assert.Equal((nint)offsets[i], Marshal.OffsetOf<NativeWidgetCompactBackgroundV1>(fields[i]));
            Assert.Equal((nint)offsets[i], Marshal.OffsetOf<PackageBackground>(fields[i]));
        }
        Assert.Equal(NativeWidgetCompactBackgroundV1.CurrentVersion, PackageBackground.CurrentVersion);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(32)]
    public void PackageAcceptsV1PrefixAndPreservesCallerHeaderAndTail(int size)
    {
        var buffer = new ExtendedBackground
        {
            Background = new PackageBackground { Size = (uint)size, Version = 1 },
            Tail = 0x0123456789ABCDEF,
        };
        Assert.Equal(0, PackageExports.InitializeCompactBackground(ref buffer.Background));
        Assert.Equal((uint)size, buffer.Background.Size);
        Assert.Equal(1u, buffer.Background.Version);
        Assert.Equal((nint)0, buffer.Background.ImageSource);
        Assert.Equal(0d, buffer.Background.Opacity);
        Assert.Equal(0x0123456789ABCDEFul, buffer.Tail);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedBackground
    {
        internal PackageBackground Background;
        internal ulong Tail;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(23)]
    public void PackageRejectsShortBufferWithoutWritingOutsideDeclaredCapacity(int size)
    {
        // Sentinel fields model bytes outside declared capacity. No real
        // owned reference is supplied to this headless initialization seam.
        var output = new PackageBackground { Size = (uint)size, Version = 9, ImageSource = (nint)0xCAFE, Opacity = 0.5 };
        Assert.Equal(unchecked((int)0x80070057), PackageExports.InitializeCompactBackground(ref output));
        Assert.Equal(9u, output.Version);
        Assert.Equal((nint)0xCAFE, output.ImageSource);
        Assert.Equal(0.5, output.Opacity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void PackageRejectsUnknownVersionWithEmptyOutputs(int version)
    {
        var output = new PackageBackground { Size = 24, Version = (uint)version };
        Assert.Equal(unchecked((int)0x80070057), PackageExports.InitializeCompactBackground(ref output));
        Assert.Equal((uint)version, output.Version);
        Assert.Equal((nint)0, output.ImageSource);
        Assert.Equal(0d, output.Opacity);
    }

    private sealed class StaticBackgroundContent : IWidgetCompactBackgroundContent
    {
        public WidgetCompactBackgroundSnapshot? GetCompactBackground() => null;
    }

    [Fact]
    public void BackgroundInterfaceAllowsExistingAdaptersToUseNoOpNotification()
    {
        IWidgetCompactBackgroundContent content = new StaticBackgroundContent();
        int notifications = 0;
        EventHandler handler = (_, _) => notifications++;
        content.CompactBackgroundChanged += handler;
        Assert.Null(content.GetCompactBackground());
        content.CompactBackgroundChanged -= handler;
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void PilotStopsNotificationsOnDisposeWhileKeepingDestroyRetry()
    {
        int destroys = 0;
        int shutdowns = 0;
        StatusThunk destroy = _ => ++destroys == 1 ? EFail : 0;
        ShutdownThunk shutdown = () => { shutdowns++; return 0; };
        var session = new NativePackageSession(
            new NativePackageIdentity("compact-notification-tests", "test.compact-notifications"), "", "", 0, 0,
            Marshal.GetFunctionPointerForDelegate(destroy), Marshal.GetFunctionPointerForDelegate(shutdown), 0);
        try
        {
            var lease = NativeWidgetLease.Create(session, 0x1234, null!, "compact-notifications-instance");
            var content = new NativeWidgetPilotContent(new DeskBox.Models.WidgetConfig(), lease);
            int notifications = 0;
            object? senderSeen = null;
            content.CompactBackgroundChanged += (sender, _) => { notifications++; senderSeen = sender; };
            content.NotifyCompactBackgroundChanged();
            Assert.Equal(1, notifications);
            Assert.Same(content, senderSeen);

            content.Dispose(); // failed native destroy must stay retryable
            content.CompactBackgroundChanged += (_, _) => notifications++;
            content.NotifyCompactBackgroundChanged();
            Assert.Equal(1, notifications);
            Assert.Equal(1, destroys);
            Assert.Equal(0, shutdowns);

            content.Dispose(); // retry succeeds despite notifications stopping
            content.NotifyCompactBackgroundChanged();
            Assert.Equal(1, notifications);
            Assert.Equal(2, destroys);
            Assert.Equal(1, shutdowns);
        }
        finally
        {
            GC.KeepAlive(destroy);
            GC.KeepAlive(shutdown);
        }
    }

    [Fact]
    public async Task ConcurrentLeaseReleaseDestroysHandleExactlyOnce()
    {
        int destroys = 0;
        using var entered = new ManualResetEventSlim();
        using var allowDestroy = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        StatusThunk destroy = _ =>
        {
            Interlocked.Increment(ref destroys);
            entered.Set();
            allowDestroy.Wait();
            return 0;
        };
        ShutdownThunk shutdown = () => 0;
        var session = new NativePackageSession(
            new NativePackageIdentity("lease-concurrency-tests", "test.lease-concurrency"), "", "", 0, 0,
            Marshal.GetFunctionPointerForDelegate(destroy), Marshal.GetFunctionPointerForDelegate(shutdown), 0);
        try
        {
            var lease = NativeWidgetLease.Create(session, 0x1234, null!, "lease-concurrency-instance");
            Task<bool> first = Task.Run(lease.TryRelease);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Task<bool> second = Task.Run(() =>
            {
                secondStarted.Set();
                return lease.TryRelease();
            });
            Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(5)));
            await Task.Delay(50);
            Assert.False(second.IsCompleted);
            allowDestroy.Set();

            bool[] results = [await first, await second];
            Assert.Single(results, value => value);
            Assert.Equal(1, destroys);
        }
        finally
        {
            allowDestroy.Set();
            GC.KeepAlive(destroy);
            GC.KeepAlive(shutdown);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(EFail)]
    public void SessionAndLeaseRejectUnknownReleasedAndShutDownHandles(int shutdownStatus)
    {
        int calls = 0;
        BackgroundThunk background = (_, output) =>
        {
            calls++;
            var snapshot = Marshal.PtrToStructure<NativeWidgetCompactBackgroundV1>(output);
            snapshot.ImageSource = 0;
            snapshot.Opacity = 0;
            Marshal.StructureToPtr(snapshot, output, false);
            return 0;
        };
        StatusThunk destroy = _ => 0;
        ShutdownThunk shutdown = () => shutdownStatus;
        ActivateThunk activate = (_, _, _, _, _) => 0;
        var session = new NativePackageSession(
            new NativePackageIdentity("compact-tests", "test.compact"), "", "",
            Marshal.GetFunctionPointerForDelegate(activate), 0,
            Marshal.GetFunctionPointerForDelegate(destroy),
            Marshal.GetFunctionPointerForDelegate(shutdown), 0,
            Marshal.GetFunctionPointerForDelegate(background));
        try
        {
            Assert.Null(session.GetCompactBackground(0x1234));
            NativePackageSession.Activate(session);
            // A fabricated lease cannot bypass the session's live-handle set.
            var lease = NativeWidgetLease.Create(session, 0x1234, null!, "compact-instance");
            var content = new NativeWidgetPilotContent(new DeskBox.Models.WidgetConfig(), lease);
            Assert.Null(session.GetCompactBackground(0));
            Assert.Null(content.GetCompactBackground());
            Assert.True(lease.TryRelease());
            Assert.Null(content.GetCompactBackground());
            Assert.Equal(shutdownStatus == 0, session.Shutdown());
            Assert.Null(session.GetCompactBackground(0x1234));
            Assert.Equal(0, calls);
        }
        finally
        {
            NativeHostApiBridge.DetachSession(session._hostApiContext);
            GC.KeepAlive(activate);
            GC.KeepAlive(background);
            GC.KeepAlive(destroy);
            GC.KeepAlive(shutdown);
        }
    }
}
