using System.Runtime.InteropServices;
using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class AppLifecycleRecoverySignalClassifierTests
{
    private const uint TaskbarCreatedMessage = 0xC123;

    [Theory]
    [InlineData(0x0012)]
    [InlineData(0x0007)]
    [InlineData(0x0006)]
    public void PowerResumeSignals_RequestExternalRecovery(uint powerEvent)
    {
        string? reason = AppLifecycleRecoverySignalClassifier.ResolveRecoveryReason(
            AppLifecycleRecoverySignalClassifier.WmPowerBroadcast,
            new UIntPtr(powerEvent),
            TaskbarCreatedMessage);

        Assert.Equal("resume", reason);
    }

    [Fact]
    public void PowerSuspendSignal_WaitsForResumeBeforeRecovery()
    {
        string? reason = AppLifecycleRecoverySignalClassifier.ResolveRecoveryReason(
            AppLifecycleRecoverySignalClassifier.WmPowerBroadcast,
            new UIntPtr(0x0004),
            TaskbarCreatedMessage);

        Assert.Null(reason);
    }

    [Theory]
    [InlineData(AppLifecycleRecoverySignalClassifier.WmDisplayChange)]
    [InlineData(AppLifecycleRecoverySignalClassifier.WmDpiChanged)]
    public void DisplayAndDpiSignals_RequestPositionRecovery(uint message)
    {
        string? reason = AppLifecycleRecoverySignalClassifier.ResolveRecoveryReason(
            message,
            UIntPtr.Zero,
            TaskbarCreatedMessage);

        Assert.Equal("display-message", reason);
    }

    [Fact]
    public void ExplorerRestartSignal_RequestsShellRecovery()
    {
        string? reason = AppLifecycleRecoverySignalClassifier.ResolveRecoveryReason(
            TaskbarCreatedMessage,
            UIntPtr.Zero,
            TaskbarCreatedMessage);

        Assert.Equal("explorer-restart", reason);
    }

    [Theory]
    [InlineData(0x0008, "session-unlock")]
    [InlineData(0x0005, "session-reconnect")]
    [InlineData(0x0009, "session-reconnect")]
    public void SessionRecoverySignals_AreClassified(uint sessionEvent, string expected)
    {
        string? reason = AppLifecycleRecoverySignalClassifier.ResolveRecoveryReason(
            AppLifecycleRecoverySignalClassifier.WmWtsSessionChange,
            new UIntPtr(sessionEvent),
            TaskbarCreatedMessage);

        Assert.Equal(expected, reason);
    }

    [Fact]
    public void DisplayPowerOn_RequestsHookRecovery()
    {
        IntPtr setting = MarshalPowerBroadcastSetting(
            Win32Helper.ConsoleDisplayStatePowerSetting, data: 1);
        try
        {
            string? reason = AppLifecycleRecoverySignalClassifier.ResolveRecoveryReason(
                AppLifecycleRecoverySignalClassifier.WmPowerBroadcast,
                new UIntPtr(Win32Helper.PbtPowerSettingChange),
                setting,
                TaskbarCreatedMessage);

            Assert.Equal("display-power-on", reason);
        }
        finally
        {
            Marshal.FreeHGlobal(setting);
        }
    }

    [Fact]
    public void DisplayPowerOff_IsGateSignal_NotRecoveryReason()
    {
        // Screen-off is a topology-gate signal (spec 5.6): the classifier
        // surfaces it so the watcher can close the gate, but it is not a
        // recovery reason and must never schedule a restore on its own.
        IntPtr setting = MarshalPowerBroadcastSetting(
            Win32Helper.ConsoleDisplayStatePowerSetting, data: 0);
        try
        {
            string? reason = AppLifecycleRecoverySignalClassifier.ResolveRecoveryReason(
                AppLifecycleRecoverySignalClassifier.WmPowerBroadcast,
                new UIntPtr(Win32Helper.PbtPowerSettingChange),
                setting,
                TaskbarCreatedMessage);

            Assert.Equal("display-power-off", reason);
        }
        finally
        {
            Marshal.FreeHGlobal(setting);
        }
    }

    [Fact]
    public void DisplayDimmed_DoesNotRecover()
    {
        IntPtr setting = MarshalPowerBroadcastSetting(
            Win32Helper.ConsoleDisplayStatePowerSetting, data: 2);
        try
        {
            string? reason = AppLifecycleRecoverySignalClassifier.ResolveRecoveryReason(
                AppLifecycleRecoverySignalClassifier.WmPowerBroadcast,
                new UIntPtr(Win32Helper.PbtPowerSettingChange),
                setting,
                TaskbarCreatedMessage);

            Assert.Null(reason);
        }
        finally
        {
            Marshal.FreeHGlobal(setting);
        }
    }

    [Fact]
    public void PowerSettingChange_WithUnrelatedGuid_DoesNotRecover()
    {
        IntPtr setting = MarshalPowerBroadcastSetting(Guid.NewGuid(), data: 1);
        try
        {
            string? reason = AppLifecycleRecoverySignalClassifier.ResolveRecoveryReason(
                AppLifecycleRecoverySignalClassifier.WmPowerBroadcast,
                new UIntPtr(Win32Helper.PbtPowerSettingChange),
                setting,
                TaskbarCreatedMessage);

            Assert.Null(reason);
        }
        finally
        {
            Marshal.FreeHGlobal(setting);
        }
    }

    [Fact]
    public void PowerSettingChange_WithoutPayload_DoesNotRecover()
    {
        string? reason = AppLifecycleRecoverySignalClassifier.ResolveRecoveryReason(
            AppLifecycleRecoverySignalClassifier.WmPowerBroadcast,
            new UIntPtr(Win32Helper.PbtPowerSettingChange),
            IntPtr.Zero,
            TaskbarCreatedMessage);

        Assert.Null(reason);
    }

    private static IntPtr MarshalPowerBroadcastSetting(Guid powerSetting, byte data)
    {
        var setting = new Win32Helper.PowerBroadcastSetting
        {
            PowerSetting = powerSetting,
            DataLength = 1,
            Data = data,
        };
        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<Win32Helper.PowerBroadcastSetting>());
        Marshal.StructureToPtr(setting, ptr, false);
        return ptr;
    }
}
