using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DeskBox.Services.Plugins;

namespace DeskBox.Tests;

/// <summary>
/// ABI v4 lifecycle contract tests. Audit round 17 found the host emitting
/// event kinds 13-15 while the package rejected anything above 12 — the two
/// ends of the wire protocol had drifted with no test noticing. These tests
/// pin the mapping twice: real marshaling through NativePackageSession on the
/// host side, and source-level numeric parity between the host enum and the
/// package constants so neither end can renumber silently.
/// </summary>
public class NativeWidgetLifecycleAbiTests
{
    private static NativeWidgetEventV1 _lastPayload;
    private static int _calls;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WidgetEventThunk(nint handle, nint payload);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int StatusThunk(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ShutdownThunk();

    private static readonly WidgetEventThunk WidgetEventStub = StubWidgetEvent;
    private static readonly StatusThunk DestroyStub = _ => 0;
    private static readonly ShutdownThunk ShutdownStub = () => 0;

    private static int StubWidgetEvent(nint handle, nint payload)
    {
        _calls++;
        _lastPayload = Marshal.PtrToStructure<NativeWidgetEventV1>(payload);
        return 0;
    }

    private static NativeWidgetPilotContent CreateContent()
    {
        var session = new NativePackageSession(
            new NativePackageIdentity("a".PadLeft(64, '0'), "test.lifecycle"),
            packageRoot: "", packageDataRoot: "",
            activateExport: 0, createExport: 0,
            destroyExport: Marshal.GetFunctionPointerForDelegate(DestroyStub),
            shutdownExport: Marshal.GetFunctionPointerForDelegate(ShutdownStub),
            widgetEventExport: Marshal.GetFunctionPointerForDelegate(WidgetEventStub));
        NativeWidgetLease lease = NativeWidgetLease.Create(session, 0x1234, null!, "instance-1", "/tmp/instance");
        return new NativeWidgetPilotContent(new DeskBox.Models.WidgetConfig(), lease);
    }

    private static NativeWidgetEventV1 Fire(Action<NativeWidgetPilotContent> action)
    {
        _calls = 0;
        action(CreateContent());
        Assert.Equal(1, _calls);
        return _lastPayload;
    }

    [Fact]
    public void InitializeAsyncDoesNotEmitEvents()
    {
        // Audit round 16: initialization must not be conflated with refresh
        // (double-fetch in Weather/Music); create_widget already starts the
        // package's initial lifecycle.
        _calls = 0;
        NativeWidgetPilotContent content = CreateContent();
        Assert.True(content.InitializeAsync().IsCompletedSuccessfully);
        Assert.Equal(0, _calls);
    }

    [Fact]
    public void SimpleCallbacksMapToTheirEventKinds()
    {
        Assert.Equal(1u, Fire(c => c.RefreshAsync()).Kind);
        Assert.Equal(2u, Fire(c => c.ApplyAppearance()).Kind);
        Assert.Equal(3u, Fire(c => c.OnActivated()).Kind);
        Assert.Equal(4u, Fire(c => c.OnDeactivated()).Kind);
        Assert.Equal(6u, Fire(c => c.OnWindowRevealCompleted()).Kind);
        Assert.Equal(7u, Fire(c => c.OnWindowLongHidden()).Kind);
        Assert.Equal(10u, Fire(c => c.ApplyPerformanceSettings()).Kind);
    }

    [Fact]
    public void FlagAndSizeCallbacksCarryTheirPayload()
    {
        NativeWidgetEventV1 visible = Fire(c => c.OnWindowVisibilityChanged(true));
        Assert.Equal(5u, visible.Kind);
        Assert.Equal(1u, visible.Flags);

        NativeWidgetEventV1 hidden = Fire(c => c.OnWindowVisibilityChanged(false));
        Assert.Equal(5u, hidden.Kind);
        Assert.Equal(0u, hidden.Flags);

        NativeWidgetEventV1 collapsed = Fire(c => c.OnCompactStateChanged(true));
        Assert.Equal(8u, collapsed.Kind);
        Assert.Equal(1u, collapsed.Flags);

        NativeWidgetEventV1 viewport = Fire(c => c.OnHostViewportSizeChanged(320, 240));
        Assert.Equal(9u, viewport.Kind);
        Assert.Equal(320, viewport.Width);
        Assert.Equal(240, viewport.Height);
    }

    [Fact]
    public void ResizeCallbacksCarrySizes()
    {
        NativeWidgetEventV1 interactiveBegin = Fire(c => c.BeginInteractiveResize(100, 50));
        Assert.Equal(11u, interactiveBegin.Kind);
        Assert.Equal(100, interactiveBegin.Width);
        Assert.Equal(50, interactiveBegin.Height);

        NativeWidgetEventV1 interactiveEnd = Fire(c => c.CompleteInteractiveResize(102, 52));
        Assert.Equal(12u, interactiveEnd.Kind);
        Assert.Equal(102, interactiveEnd.Width);
        Assert.Equal(52, interactiveEnd.Height);
    }

    [Fact]
    public void ResponsiveCallbacksCoverKinds13Through15()
    {
        // The exact events audit round 17 found rejected by the package
        // (host 13-15 vs package accepting only 1-12).
        NativeWidgetEventV1 begin = Fire(c => c.BeginResponsiveLayoutTransition(440, 560, isCollapsing: true));
        Assert.Equal(13u, begin.Kind);
        Assert.Equal(440, begin.Width);
        Assert.Equal(560, begin.Height);
        Assert.Equal(1u, begin.Flags);

        NativeWidgetEventV1 expand = Fire(c => c.BeginResponsiveLayoutTransition(440, 560, isCollapsing: false));
        Assert.Equal(13u, expand.Kind);
        Assert.Equal(0u, expand.Flags);

        NativeWidgetEventV1 complete = Fire(c => c.CompleteResponsiveLayoutTransition(260, 200));
        Assert.Equal(14u, complete.Kind);
        Assert.Equal(260, complete.Width);
        Assert.Equal(200, complete.Height);

        NativeWidgetEventV1 cancel = Fire(c => c.CancelResponsiveLayoutTransition());
        Assert.Equal(15u, cancel.Kind);
    }

    [Fact]
    public void EveryEventTravelsInTheVersionedStructEnvelope()
    {
        NativeWidgetEventV1 payload = Fire(c => c.RefreshAsync());
        Assert.Equal((uint)Unsafe.SizeOf<NativeWidgetEventV1>(), payload.Size);
        Assert.Equal(NativeWidgetEventV1.CurrentVersion, payload.Version);
    }

    [Fact]
    public void HostAndPackageEventKindTablesStayAligned()
    {
        // The host enum and the package constants are maintained in two
        // projects that cannot reference each other; this source scan is the
        // single place that forces their names and wire values to match.
        string host = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox/Services/Plugins/NativeWidgetPackageLoader.cs"));
        string package = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox.GlancePackage/Abi/Exports.cs"));

        Dictionary<string, uint> hostKinds = ExtractPairs(host,
            host.IndexOf("internal enum WidgetLifecycleEventKind : uint", StringComparison.Ordinal),
            "}");
        Dictionary<string, uint> packageKinds = ExtractPairs(package,
            package.IndexOf("Widget lifecycle event kinds (ABI v4)", StringComparison.Ordinal),
            "private static readonly Dictionary");

        Assert.NotEmpty(hostKinds);
        Assert.Equal(hostKinds.Count, packageKinds.Count);
        foreach ((string name, uint value) in hostKinds)
        {
            Assert.True(packageKinds.TryGetValue(name, out uint packageValue),
                $"package Exports.cs is missing event kind {name}");
            Assert.Equal(value, packageValue);
        }
        // The full 1..15 table (audit round 17 drift baseline).
        Assert.Equal(15, hostKinds.Count);
        Assert.Equal(15u, hostKinds["ResponsiveLayoutCancel"]);
    }

    [Fact]
    public void EventStructLayoutIsPinnedAcrossTheAbi()
    {
        string host = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox/Services/Plugins/NativeWidgetPackageLoader.cs"));
        string package = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox.GlancePackage/Abi/Exports.cs"));

        string[] hostFields = ExtractStructFields(host, "internal struct NativeWidgetEventV1");
        string[] packageFields = ExtractStructFields(package, "public struct DeskBoxWidgetEventV1");

        Assert.Equal(hostFields, packageFields);
        Assert.Equal(
            ["Size", "Version", "Kind", "Flags", "Width", "Height",
             "Reserved0", "Reserved1", "Reserved2", "Reserved3"],
            hostFields);
    }

    private static Dictionary<string, uint> ExtractPairs(string source, int start, string endMarker)
    {
        Assert.True(start >= 0, "anchor not found in source");
        string block = source[start..source.IndexOf(endMarker, start, StringComparison.Ordinal)];
        var pairs = new Dictionary<string, uint>();
        foreach (System.Text.RegularExpressions.Match match in
            System.Text.RegularExpressions.Regex.Matches(block, @"(\w+)\s*=\s*(\d+)"))
        {
            pairs[match.Groups[1].Value] = uint.Parse(match.Groups[2].Value);
        }
        return pairs;
    }

    private static string[] ExtractStructFields(string source, string structDeclaration)
    {
        int start = source.IndexOf(structDeclaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"struct declaration not found: {structDeclaration}");
        string block = source[start..source.IndexOf("}", start, StringComparison.Ordinal)];
        return System.Text.RegularExpressions.Regex.Matches(block, @"public\s+\w+\s+(\w+)\s*;")
            .Select(m => m.Groups[1].Value)
            .ToArray();
    }
}
