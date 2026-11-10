using DeskBox.Helpers;
using Windows.System;

namespace DeskBox.Tests;

public sealed class ImeCompatibilityTests
{
    [Fact]
    public void ImeCompositionGuard_DetectsVkProcessKeyOnKeyOrOriginalKey()
    {
        Assert.True(ImeCompositionGuard.IsVkProcessKey((VirtualKey)229, VirtualKey.Enter));
        Assert.True(ImeCompositionGuard.IsVkProcessKey(VirtualKey.Enter, (VirtualKey)229));
        Assert.True(ImeCompositionGuard.IsVkProcessKey((VirtualKey)229, (VirtualKey)229));

        Assert.False(ImeCompositionGuard.IsVkProcessKey(VirtualKey.Enter, VirtualKey.Enter));
        Assert.False(ImeCompositionGuard.IsVkProcessKey(VirtualKey.Escape, VirtualKey.Escape));
        Assert.False(ImeCompositionGuard.IsVkProcessKey(VirtualKey.Space, VirtualKey.Space));
    }

    [Fact]
    public void ImeCompositionGuard_TracksActiveCompositionAndPostCompositionGrace()
    {
        ImeCompositionGuard.ResetForTest();
        try
        {
            Assert.False(ImeCompositionGuard.IsActivelyComposing());
            Assert.False(ImeCompositionGuard.IsComposingOrWithinGrace());

            ImeCompositionGuard.NotifyCompositionStarted();
            Assert.True(ImeCompositionGuard.IsActivelyComposing());
            Assert.True(ImeCompositionGuard.IsComposingOrWithinGrace());

            ImeCompositionGuard.NotifyCompositionStarted();
            ImeCompositionGuard.NotifyCompositionEnded(recordGrace: false);
            Assert.True(ImeCompositionGuard.IsActivelyComposing());

            // TSF fires TextCompositionEnded right before Enter/Escape KeyDown;
            // IsActivelyComposing becomes false while IsComposingOrWithinGrace stays true.
            ImeCompositionGuard.NotifyCompositionEnded(recordGrace: true);
            Assert.False(ImeCompositionGuard.IsActivelyComposing());
            Assert.True(ImeCompositionGuard.IsComposingOrWithinGrace());
        }
        finally
        {
            ImeCompositionGuard.ResetForTest();
        }
    }

    [Fact]
    public void ImeCompositionGuard_UnloadWithoutGrace_ClearsImmediately()
    {
        ImeCompositionGuard.ResetForTest();
        try
        {
            ImeCompositionGuard.NotifyCompositionStarted();
            Assert.True(ImeCompositionGuard.IsComposingOrWithinGrace());

            ImeCompositionGuard.NotifyCompositionEnded(recordGrace: false);
            Assert.False(ImeCompositionGuard.IsActivelyComposing());
            Assert.False(ImeCompositionGuard.IsComposingOrWithinGrace());
        }
        finally
        {
            ImeCompositionGuard.ResetForTest();
        }
    }

    [Theory]
    [InlineData("IME", "explorer")]
    [InlineData("MSCTFIME UI", "notepad")]
    [InlineData("Microsoft.IME.UIManager.CandidateWindow.Host", "chsime")]
    [InlineData("Windows.UI.Core.CoreWindow", "TextInputHost")]
    [InlineData("InputSiteWindowClass", "TextInputHost")]
    [InlineData("ApplicationFrameWindow", "TextInputHost")]
    [InlineData("IPTip_Main_Window", "TabTip")]
    [InlineData("CandidateBarWindow", "chsime")]
    [InlineData("CandidateBarWindow", "chtspime")]
    [InlineData("CandidateBarWindow", "imjp81k")]
    [InlineData("CandidateBarWindow", "imkrhjd")]
    [InlineData("SogouCandidateWindow", "SogouCloud")]
    [InlineData("SGToolStatusBar", "SGTool")]
    [InlineData("WeTypeCandidate", "WeType")]
    [InlineData("WeChatCandidate", "WeChatInput")]
    [InlineData("WeChatCandidate", "WeChatIME")]
    [InlineData("BaiduCandidate", "BaiduPinyin")]
    [InlineData("QQPinyinCandidate", "QQPYUserCenter")]
    [InlineData("WeaselPanel", "WeaselServer")]
    public void TransientInputFocusGuard_RecognizesWindowsAndThirdPartyImeSurfaces(
        string className,
        string processName)
    {
        Assert.True(TransientInputFocusGuard.IsKnownTextInputProcessOrClass(className, processName));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("Chrome_WidgetWin_1", "chrome")]
    [InlineData("CabinetWClass", "explorer")]
    [InlineData("Notepad", "notepad")]
    [InlineData("#32770", "TextInputHost")]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS", "WindowsTerminal")]
    public void TransientInputFocusGuard_RejectsForeignAppWindows(
        string className,
        string processName)
    {
        Assert.False(TransientInputFocusGuard.IsKnownTextInputProcessOrClass(className, processName));
    }

    [Fact]
    public void GlobalImeFocusTracking_IsInstalledInBothMainAndSettingsHosts()
    {
        string mainApp = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/App.xaml.cs"));
        string settingsApp = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/App.SettingsHost.cs"));

        Assert.Contains("ImeCompositionGuard.EnsureGlobalFocusTracking()", mainApp, StringComparison.Ordinal);
        Assert.Contains("ImeCompositionGuard.EnsureGlobalFocusTracking()", settingsApp, StringComparison.Ordinal);
        Assert.Contains("IsDeskBoxOrTextInputExperienceWindow", mainApp, StringComparison.Ordinal);
    }

    [Fact]
    public void WidgetHostLayersAndSmartCapsules_ProtectImeAndTextInputSessions()
    {
        string zOrder = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string interaction = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/ContentWidgetWindow.WindowInteraction.cs"));
        string windowBase = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/WidgetWindowBase.cs"));
        string bounds = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));

        Assert.Contains("IsDeskBoxOrTextInputExperienceWindow", zOrder, StringComparison.Ordinal);
        Assert.Contains("IsDeskBoxOrTextInputExperienceWindow", interaction, StringComparison.Ordinal);
        Assert.Contains("HasActiveFocusedTextInput", windowBase, StringComparison.Ordinal);
        Assert.Contains("FocusManager_GotFocusForDesktopPinnedLayer", bounds, StringComparison.Ordinal);
        Assert.Contains("SetActiveAndFocusIsland(HWnd)", bounds, StringComparison.Ordinal);
    }

    [Fact]
    public void StandaloneAndPopupWindows_ActivateContentIslandAndGuardImeDismissal()
    {
        string quickPaste = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/ClipboardQuickPasteWindow.xaml.cs"));
        string stackHost = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/StackPopoverHostWindow.cs"));
        string quickCapturePopout = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/QuickCapturePopoutWindow.xaml.cs"));
        string searchPopup = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/SearchPopupWindow.xaml.cs"));
        string settingsWindow = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.xaml.cs"));

        Assert.Contains("ActivateWindowAndFocusIsland", quickPaste, StringComparison.Ordinal);
        Assert.Contains("WatchPopupWindowInputExperienceFocus", quickPaste, StringComparison.Ordinal);
        Assert.Contains("WatchPopupWindowInputExperienceFocus", stackHost, StringComparison.Ordinal);
        Assert.Contains("ActivateWindowAndFocusIsland", quickCapturePopout, StringComparison.Ordinal);
        Assert.Contains("ActivateWindowAndFocusIsland", searchPopup, StringComparison.Ordinal);
        Assert.Contains("_appWindow.MoveAndResize(", settingsWindow, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/DeskBox/Views/SearchPopupWindow.xaml.cs", "ImeCompositionGuard")]
    [InlineData("src/DeskBox/Views/SearchPopupWindow.CommandPalette.cs", "ImeCompositionGuard")]
    [InlineData("src/DeskBox/Controls/WidgetContents/ClipboardWidgetContent.xaml.cs", "ImeCompositionGuard")]
    [InlineData("src/DeskBox/Controls/Todo/TodoQuickAddBar.xaml.cs", "ImeCompositionGuard")]
    [InlineData("src/DeskBox/Controls/WidgetInlineEditor.xaml.cs", "ImeCompositionGuard")]
    [InlineData("src/DeskBox/Controls/MarkdownSourceEditor.xaml.cs", "ImeCompositionGuard")]
    [InlineData("src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs", "ImeCompositionGuard")]
    [InlineData("src/DeskBox/Controls/WidgetContents/TodoWidgetContent.DetailNotesAndSteps.cs", "ImeCompositionGuard")]
    [InlineData("src/DeskBox/Controls/WidgetContents/TodoWidgetContent.ListInteraction.cs", "ImeCompositionGuard")]
    [InlineData("src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs", "ImeCompositionGuard")]
    [InlineData("src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.InlineComposer.cs", "ImeCompositionGuard")]
    [InlineData("src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs", "ShouldDeferInlineEditorBlurCommit")]
    [InlineData("src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopover.cs", "ShouldDeferInlineEditorBlurCommit")]
    [InlineData("src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopoverRename.cs", "ShouldDeferInlineEditorBlurCommit")]
    [InlineData("src/DeskBox/Views/ContentWidgetWindow.Commands.cs", "ShouldDeferInlineEditorBlurCommit")]
    [InlineData("src/DeskBox/Controls/WidgetContents/CustomWidgetNativeRenderer.cs", "ImeCompositionGuard")]
    [InlineData("src/DeskBox/Controls/WidgetContents/CustomWidgetNativeRenderer.Primitives.cs", "ImeCompositionGuard")]
    [InlineData("src/DeskBox/Views/CustomWidgetStudioWindow.Pages.cs", "ImeCompositionGuard")]
    [InlineData("src/DeskBox/Views/CustomWidgetStudioWindow.xaml.cs", "ImeCompositionGuard")]
    [InlineData("src/DeskBox/Views/SettingsWindow.Navigation.cs", "ImeCompositionGuard")]
    [InlineData("src/DeskBox/Views/SettingsWindow.HotkeyAndAppearance.cs", "ImeCompositionGuard")]
    [InlineData("src/DeskBox/Views/SettingsSections/CustomWidgetSettingsSection.xaml.cs", "ShouldDeferInlineEditorBlurCommit")]
    [InlineData("src/DeskBox/Views/SettingsSections/SearchSettingsSection.xaml.cs", "ImeCompositionGuard")]
    [InlineData("src/DeskBox/Views/SettingsSections/DesktopOrganizationSettingsSection.xaml.cs", "ImeCompositionGuard")]
    [InlineData("src/DeskBox/Controls/DesktopOrganizationPreviewCard.xaml.cs", "ShouldDeferInlineEditorBlurCommit")]
    [InlineData("src/DeskBox/Views/DesktopOrganizationWindow.xaml.cs", "ImeCompositionGuard")]
    public void ProjectWideTextEditors_EnforceImeCompositionAndBlurGuards(
        string relativePath,
        string expectedGuardSymbol)
    {
        string source = File.ReadAllText(TestPaths.FromRepository(relativePath));
        Assert.Contains(expectedGuardSymbol, source, StringComparison.Ordinal);
    }

    [Fact]
    public void EmojiInputs_AllowZwjGraphemeSequencesAndPinyinPreedit()
    {
        string studioPages = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/CustomWidgetStudioWindow.Pages.cs"));
        string settingsSection = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/SettingsSections/CustomWidgetSettingsSection.xaml.cs"));

        Assert.DoesNotContain("MaxLength = 8", studioPages, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxLength = 12", settingsSection, StringComparison.Ordinal);
        Assert.Contains("MaxLength = 32", studioPages, StringComparison.Ordinal);
        Assert.Contains("MaxLength = 32", settingsSection, StringComparison.Ordinal);
    }
}
