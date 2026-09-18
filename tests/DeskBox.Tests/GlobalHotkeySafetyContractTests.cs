using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class GlobalHotkeySafetyContractTests
{
    [Fact]
    public void SettingsExposeExplicitPresetsAndPersistentSystemOverrideWarning()
    {
        string xaml = Read("src/DeskBox/Views/SettingsWindow.xaml");
        string settingsWindow = Read("src/DeskBox/Views/SettingsWindow.xaml.cs");
        string hotkeyCode = Read("src/DeskBox/Views/SettingsWindow.HotkeyAndAppearance.cs");
        string presetButtons = Slice(
            xaml,
            "x:Name=\"GlobalHotkeyPresetButtonsPanel\"",
            "</StackPanel>");

        Assert.Contains("x:Name=\"GlobalHotkeyCaptureButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GlobalHotkeyPresetButtonsPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GlobalHotkeyPresetF7Button\"", presetButtons, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GlobalHotkeyPresetDoubleControlButton\"", presetButtons, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GlobalHotkeyPresetAltSpaceButton\"", presetButtons, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GlobalHotkeyPresetWinSpaceButton\"", presetButtons, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GlobalHotkeyPresetWindowsTapButton\"", presetButtons, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GlobalHotkeyPresetCopilotKeyButton\"", presetButtons, StringComparison.Ordinal);
        Assert.Contains("Settings.GlobalHotkey.PresetsTitle", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.GlobalHotkey.PresetsDescription", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.GlobalHotkey.RecommendedTitle", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.GlobalHotkey.SystemTitle", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GlobalHotkeyCustomRow\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DesktopDoubleClickToggle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GlobalHotkeyReservedWarning\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CanShowGlobalHotkeyWarning", xaml, StringComparison.Ordinal);
        Assert.Contains("WmReservedHotkeyCapture", settingsWindow, StringComparison.Ordinal);
        Assert.Contains("_hotkeyRecordingHook.TryStart", hotkeyCode, StringComparison.Ordinal);
        Assert.Contains("_hotkeyRecordingHook.Stop", hotkeyCode, StringComparison.Ordinal);
        Assert.Contains("IsInternalMaskKey", hotkeyCode, StringComparison.Ordinal);
        Assert.DoesNotContain("UseWinSpaceHotkeyButton_Click", hotkeyCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchAltSpacePreset_RidesTheReservedHookAndCannotDuplicateTheMainHotkey()
    {
        string search = Read("src/DeskBox/Services/SearchHotkeyService.cs");
        string global = Read("src/DeskBox/Services/GlobalHotkeyService.cs");
        string settingsService = Read("src/DeskBox/Services/SettingsService.cs");
        string sectionXaml = Read("src/DeskBox/Views/SettingsSections/SearchSettingsSection.xaml");
        string sectionCode = Read("src/DeskBox/Views/SettingsSections/SearchSettingsSection.xaml.cs");

        // The Alt+Space search preset must reuse the opt-in reserved hook, not
        // the RegisterHotKey path that Windows reserves for the system menu.
        Assert.Contains("ReservedHotkeyMode.AltSpace", search, StringComparison.Ordinal);
        Assert.Contains("WmReservedSearchHotkey", search, StringComparison.Ordinal);

        // Ownership is exclusive between the main and search hotkeys, enforced
        // in both directions.
        Assert.Contains("IsGestureOwnedByMainHotkey", search, StringComparison.Ordinal);
        Assert.Contains(
            "Settings.Search.Hotkey.Status.GlobalHotkeyConflict",
            search,
            StringComparison.Ordinal);
        Assert.Contains("IsGestureOwnedBySearchHotkey", global, StringComparison.Ordinal);
        Assert.Contains(
            "Settings.GlobalHotkey.Status.SearchHotkeyConflict",
            global,
            StringComparison.Ordinal);

        // Settings normalization only resets a saved Alt+Space search gesture
        // when the main hotkey owns the chord, instead of unconditionally.
        string normalize = Slice(
            settingsService,
            "private static bool NormalizeHotkeySettings",
            "private static bool NormalizeSearchSettings");
        Assert.Contains("GlobalHotkeyActivationKind", normalize, StringComparison.Ordinal);

        Assert.Contains(
            "x:Name=\"SearchHotkeyPresetAltSpaceButton\"",
            sectionXaml,
            StringComparison.Ordinal);
        Assert.Contains("Settings.GlobalHotkey.AltSpaceWarning", sectionCode, StringComparison.Ordinal);
        Assert.Contains("ConfirmSearchReservedHotkeyOverrideAsync", sectionCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CopilotKeyPreset_StaysAPlainChordWithAFriendlyDisplayName()
    {
        string serviceCode = Read("src/DeskBox/Services/GlobalHotkeyService.cs");
        string hotkeyCode = Read("src/DeskBox/Views/SettingsWindow.HotkeyAndAppearance.cs");

        Assert.Contains(
            "gesture.Equals(CopilotKeyGesture)",
            Slice(serviceCode, "public static string FormatGesture", "private IntPtr WindowSubclassProc"),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"CopilotKey\" => GlobalHotkeyActivation.FromChord(GlobalHotkeyService.CopilotKeyGesture)",
            hotkeyCode,
            StringComparison.Ordinal);
        Assert.Contains("GlobalHotkeyPresetCopilotKeyButton.IsChecked", hotkeyCode, StringComparison.Ordinal);

        // The Copilot preset must keep riding the standard RegisterHotKey path:
        // a reserved-hook mode would make it depend on the low-level hook.
        string hookModes = Slice(
            serviceCode,
            "private static bool TryGetReservedHookMode",
            "private static uint ToWin32Modifiers");
        Assert.DoesNotContain("Copilot", hookModes, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsAndOnboardingRecorders_IgnoreReservedHookMaskKey()
    {
        Assert.True(ReservedHotkeyHookService.IsInternalMaskKey(0xE8));
        Assert.False(ReservedHotkeyHookService.IsInternalMaskKey(0x20));

        string settingsSource = Read("src/DeskBox/Views/SettingsWindow.HotkeyAndAppearance.cs");
        string settingsHandler = Slice(
            settingsSource,
            "private void GlobalHotkeyCaptureButton_KeyDown",
            "private void GlobalHotkeyCaptureButton_LostFocus");
        Assert.True(
            settingsHandler.IndexOf("IsInternalMaskKey", StringComparison.Ordinal) <
            settingsHandler.IndexOf("ApplyRecordedHotkeyAsync", StringComparison.Ordinal));

        string onboardingSource = Read("src/DeskBox/Views/OnboardingWindow.Hotkey.cs");
        string onboardingHandler = Slice(
            onboardingSource,
            "private void OnHotkeyKeyDown",
            "private void Step4HotkeyToggle_Toggled");
        Assert.True(
            onboardingHandler.IndexOf("IsInternalMaskKey", StringComparison.Ordinal) <
            onboardingHandler.IndexOf("ApplyRecordedHotkeyAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void ActivationChange_UsesTheRealRegistrationAsCommitPointAndRestoresPreviousActivation()
    {
        string source = Read("src/DeskBox/Services/GlobalHotkeyService.cs");
        string apply = Slice(
            source,
            "public bool TryApplyActivation",
            "public void SetEnabled");

        Assert.DoesNotContain("CanRegister", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProbeHotkeyId", source, StringComparison.Ordinal);
        Assert.Contains("HotkeyActivationKind previousKind", apply, StringComparison.Ordinal);
        Assert.Contains("int previousModifiers", apply, StringComparison.Ordinal);
        Assert.Contains("int previousVirtualKey", apply, StringComparison.Ordinal);
        Assert.Contains("RefreshRegistration();", apply, StringComparison.Ordinal);
        Assert.Contains("settings.GlobalHotkeyActivationKind = previousKind", apply, StringComparison.Ordinal);
        Assert.Contains("settings.GlobalHotkeyModifiers = previousModifiers", apply, StringComparison.Ordinal);
        Assert.Contains("settings.GlobalHotkeyKey = previousVirtualKey", apply, StringComparison.Ordinal);
        Assert.Contains("if (IsRegistered)", apply, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchGestureChange_UsesTheRealRegistrationAsCommitPointAndRestoresPreviousGesture()
    {
        string source = Read("src/DeskBox/Services/SearchHotkeyService.cs");
        string apply = Slice(
            source,
            "public bool TryApplyGesture",
            "public void SetEnabled");

        Assert.Contains("int previousModifiers", apply, StringComparison.Ordinal);
        Assert.Contains("int previousVirtualKey", apply, StringComparison.Ordinal);
        Assert.Contains("bool shouldBeActive", apply, StringComparison.Ordinal);
        Assert.Contains("RefreshRegistration();", apply, StringComparison.Ordinal);
        Assert.Contains("settings.SearchHotkeyModifiers = previousModifiers", apply, StringComparison.Ordinal);
        Assert.Contains("settings.SearchHotkeyKey = previousVirtualKey", apply, StringComparison.Ordinal);
        Assert.Contains("if (IsRegistered)", apply, StringComparison.Ordinal);
        Assert.Contains("return false;", apply, StringComparison.Ordinal);
    }

    [Fact]
    public void HotkeyServices_ExposeReceiveInvokeAndDispatchFailureCounters()
    {
        string global = Read("src/DeskBox/Services/GlobalHotkeyService.cs");
        string search = Read("src/DeskBox/Services/SearchHotkeyService.cs");

        foreach (string source in new[] { global, search })
        {
            Assert.Contains("public long ReceivedCount", source, StringComparison.Ordinal);
            Assert.Contains("public long InvocationCount", source, StringComparison.Ordinal);
            Assert.Contains("public long DispatchFailureCount", source, StringComparison.Ordinal);
            Assert.Contains("Interlocked.Increment(ref _receivedSequence)", source, StringComparison.Ordinal);
            Assert.Contains("Interlocked.Increment(ref _invocationSequence)", source, StringComparison.Ordinal);
            Assert.Contains("Interlocked.Increment(ref _dispatchFailureSequence)", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReservedHook_IgnoresInjectedInputAndFailsOpenWhenDeliveryFails()
    {
        string hook = Read("src/DeskBox/Services/ReservedHotkeyHookService.cs");
        string win32 = Read("src/DeskBox/Platform/Win32Helper.cs");

        Assert.Contains("LLKHF_INJECTED", hook, StringComparison.Ordinal);
        Assert.Contains("TaskCompletionSource", hook, StringComparison.Ordinal);
        Assert.DoesNotContain("ManualResetEventSlim", hook, StringComparison.Ordinal);
        Assert.Contains("_lifecycleGeneration", hook, StringComparison.Ordinal);
        Assert.Contains("generation != _lifecycleGeneration", hook, StringComparison.Ordinal);
        Assert.Contains("TrySendTaggedKeyPress", hook, StringComparison.Ordinal);
        Assert.Contains("ReservedHotkeyMode.DoubleControl", hook, StringComparison.Ordinal);
        Assert.Contains("ReservedHotkeyMode.WindowsTap", hook, StringComparison.Ordinal);
        Assert.Contains("TriggerAndPassThrough", hook, StringComparison.Ordinal);
        Assert.Contains("CancelSuppression();", hook, StringComparison.Ordinal);
        Assert.Contains("PostMessage", hook, StringComparison.Ordinal);
        Assert.Contains("SendInput", win32, StringComparison.Ordinal);
        Assert.DoesNotContain("keybd_event", win32, StringComparison.Ordinal);
    }

    [Fact]
    public void LifecycleRecovery_ReRegistersGlobalHotkeyAfterExternalSessionChanges()
    {
        string app = Read("src/DeskBox/App.xaml.cs");
        string recovery = Slice(
            app,
            "private void OnLifecycleRecoveryRequested",
            "private void FlushSettingsForEndSession");

        Assert.Contains("GlobalHotkeyService?.RefreshRegistration();", recovery, StringComparison.Ordinal);
        Assert.Contains("DesktopDoubleClickActivationService?.RefreshRegistration();", recovery, StringComparison.Ordinal);
        Assert.Contains("requiresExternalRecovery", recovery, StringComparison.Ordinal);
    }

    private static string Read(string path)
    {
        return File.ReadAllText(TestPaths.FromRepository(path));
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }
}
