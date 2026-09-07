namespace DeskBox.Tests;

public sealed class AotStage5B4C2AContractTests
{
    [Fact]
    public void Scenario_IsNativeAotOnlyIsolatedPhasedAndNormallyShutDown()
    {
        string app = Read("src/DeskBox/App.AotHotkeySmoke.cs");
        string launch = Read("src/DeskBox/App.xaml.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", app, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_HOTKEY_SMOKE", app, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_HOTKEY_PHASE", app, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_HOTKEY_RUN_ID", app, StringComparison.Ordinal);
        Assert.Contains("RegistrationLifecycle", app, StringComparison.Ordinal);
        Assert.Contains("AotHotkeyPrimaryPhase", app, StringComparison.Ordinal);
        Assert.Contains("AotHotkeyReleasePhase", app, StringComparison.Ordinal);
        Assert.Contains("dataPaths.IsDevelopmentRoot", app, StringComparison.Ordinal);
        Assert.Contains("configuredPreviewRoot", app, StringComparison.Ordinal);
        Assert.Contains("Guid.TryParseExact(runId, \"N\"", app, StringComparison.Ordinal);
        Assert.Contains("NormalShutdownRequested = true", app, StringComparison.Ordinal);
        Assert.Contains("ShutdownApplicationAsync()", app, StringComparison.Ordinal);
        Assert.Contains("StartAotHotkeySmokeIfRequested();", launch, StringComparison.Ordinal);
        Assert.Equal(1, Count(app, "JsonSerializer.Serialize("));
    }

    [Fact]
    public void StandardMatrix_UsesRealOsRegistrationAndSyntheticDispatchWithExactCounters()
    {
        string app = Read("src/DeskBox/App.AotHotkeySmoke.cs");
        string helper = Read("src/DeskBox/Helpers/Win32Helper.AotHotkeySmoke.cs");

        Assert.Contains("Ctrl + Shift", app, StringComparison.Ordinal);
        Assert.Contains("VirtualKey.F23", app, StringComparison.Ordinal);
        Assert.Contains("Ctrl + Alt", app, StringComparison.Ordinal);
        Assert.Contains("VirtualKey.F24", app, StringComparison.Ordinal);
        Assert.Contains("TrySendTaggedKeyChord", app, StringComparison.Ordinal);
        Assert.Contains("ReceivedDelta == 1", app, StringComparison.Ordinal);
        Assert.Contains("InvocationDelta == 1", app, StringComparison.Ordinal);
        Assert.Contains("DispatchFailureDelta == 0", app, StringComparison.Ordinal);
        Assert.Contains("SendInput", helper, StringComparison.Ordinal);
        Assert.Contains("partial SendInput", helper, StringComparison.Ordinal);
        Assert.Contains("KEYEVENTF_KEYUP", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void ConflictDisableAndRestartMatrix_RequireRollbackAndCrossProcessRelease()
    {
        string app = Read("src/DeskBox/App.AotHotkeySmoke.cs");
        string runner = Read("scripts/run-aot-hotkey-smoke.ps1");

        Assert.Contains("GlobalConflictRolledBack", app, StringComparison.Ordinal);
        Assert.Contains("SearchConflictRolledBack", app, StringComparison.Ordinal);
        Assert.Contains("final-disable-unregistered", app, StringComparison.Ordinal);
        Assert.Contains("final-reregistered", app, StringComparison.Ordinal);
        Assert.Contains("release-startup-reregistered", app, StringComparison.Ordinal);
        Assert.Contains("Invoke-HotkeyPhase", runner, StringComparison.Ordinal);
        Assert.Contains("-Phase \"Primary\"", runner, StringComparison.Ordinal);
        Assert.Contains("-Phase \"Release\"", runner, StringComparison.Ordinal);
        Assert.Contains("processIdsDistinct", runner, StringComparison.Ordinal);
        Assert.Contains("executableHashesMatch", runner, StringComparison.Ordinal);
        Assert.Contains("Wait-NaturalPreviewExit", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void ReservedHookMatrix_ProvesLifecycleButMakesNoSyntheticOrPhysicalClaim()
    {
        string app = Read("src/DeskBox/App.AotHotkeySmoke.cs");

        Assert.Contains("ReservedHookThreadId != 0", app, StringComparison.Ordinal);
        Assert.Contains("ReservedHookLastErrorCode == 0", app, StringComparison.Ordinal);
        Assert.Contains("ReservedHookSyntheticTriggerAttempted = false", app, StringComparison.Ordinal);
        Assert.Contains("PhysicalStandardKeyboardVerified = false", app, StringComparison.Ordinal);
        Assert.Contains("PhysicalWinSpaceVerified = false", app, StringComparison.Ordinal);
        Assert.Contains("PhysicalRecorderVerified = false", app, StringComparison.Ordinal);
        Assert.Contains("reserved-hook-no-synthetic-claim", app, StringComparison.Ordinal);

        string reservedSection = Slice(
            app,
            "private static async Task ExerciseReservedHookLifecycleAsync",
            "private static async Task<bool> WaitForAotHotkeyConditionAsync");
        Assert.DoesNotContain("TrySendTaggedKeyChord", reservedSection, StringComparison.Ordinal);
        Assert.DoesNotContain("SendInput", reservedSection, StringComparison.Ordinal);
    }

    [Fact]
    public void Runner_RequiresIsolationFingerprintArchiveAndOwnedCleanup()
    {
        string runner = Read("scripts/run-aot-hotkey-smoke.ps1");

        Assert.Contains("profile 56 / schema 53", runner, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintBefore", runner, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintAfter", runner, StringComparison.Ordinal);
        Assert.Contains("SyntheticSendInputForRegisterHotKeyOnly", runner, StringComparison.Ordinal);
        Assert.Contains("physicalStandardKeyboardVerified", runner, StringComparison.Ordinal);
        Assert.Contains("physicalWinSpaceVerified", runner, StringComparison.Ordinal);
        Assert.Contains("physicalRecorderVerified", runner, StringComparison.Ordinal);
        Assert.Contains("reservedHookSyntheticTriggerAttempted", runner, StringComparison.Ordinal);
        Assert.Contains("Refusing to clean an unowned hotkey root", runner, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $resolvedRoot -Recurse -Force", runner, StringComparison.Ordinal);
        Assert.Contains("hotkey-session.json", runner, StringComparison.Ordinal);
        Assert.Contains("Stop-ExactPreviewProcess", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditProfile_AdvancesWithoutRustExpansion()
    {
        string audit = Read("scripts/publish-aot-audit.ps1");
        string launcher = Read("scripts/start-aot-preview.ps1");
        string project = Read("src/DeskBox/DeskBox.csproj");
        string rust = Read("native/deskbox-native/src/lib.rs");

        Assert.Contains("$auditProfileVersion = 61", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C2ARequiredScenarioPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C2AMissingSmokeScriptPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C2ARustAbiUnchanged", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 61", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("Native AOT stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("assert_eq!(deskbox_native_capabilities(), 511);", rust, StringComparison.Ordinal);
        Assert.Equal(10, Count(rust, "#[unsafe(no_mangle)]"));
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));

    private static int Count(string source, string token)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static string Slice(string source, string start, string end)
    {
        int startIndex = source.IndexOf(start, StringComparison.Ordinal);
        int endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Missing start marker: {start}");
        Assert.True(endIndex > startIndex, $"Missing end marker: {end}");
        return source[startIndex..endIndex];
    }
}
