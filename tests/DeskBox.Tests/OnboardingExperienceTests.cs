using System.Text.Json;

namespace DeskBox.Tests;

public sealed class OnboardingExperienceTests
{
    private static readonly string[] RequiredTaskFlowKeys =
    [
        "Onboarding.SkipAll",
        "Onboarding.Start",
        "Onboarding.Task.SkipPractice",
        "Onboarding.Task.Continue",
        "Onboarding.Task.Step3.Title",
        "Onboarding.Task.Step3.Body",
        "Onboarding.Task.Step3.StatusCompleted",
        "Onboarding.Task.Step4.Title",
        "Onboarding.Task.Step4.Body",
        "Onboarding.Task.Step4.ToggleBody",
        "Onboarding.Task.Step4.StatusHidden",
        "Onboarding.Task.Step4.StatusShown",
        "Onboarding.Task.Step4.StatusCompleted",
        "Onboarding.Task.Step4.TrayTitle",
        "Onboarding.Task.Step4.TrayBody",
        "Onboarding.Task.Step4.TrayButton",
        "Onboarding.Task.Step4.ManagedEntry",
        "Onboarding.Task.Step4.MappedEntry",
        "Onboarding.Task.Step5.Title",
        "Onboarding.Task.Step5.Body",
        "Onboarding.Task.Step5.TodoDescription",
        "Onboarding.Task.Step5.QuickCaptureDescription",
        "Onboarding.Task.Step5.SearchDescription",
        "Onboarding.Task.Step5.WeatherDescription",
        "Onboarding.Task.Step5.MusicDescription",
        "Onboarding.Task.Step5.OptionalBody",
        "Widget.Empty.ActionsHint"
    ];

    [Fact]
    public void TaskFlow_HasFilePracticeVisibilityPracticeManagementAndOptionalFeatures()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.xaml.cs"));

        Assert.Contains("private static readonly int StepCount = 4", codeBehind, StringComparison.Ordinal);
        Assert.Contains("0 => TaskStep3Panel", codeBehind, StringComparison.Ordinal);
        Assert.Contains("1 => TaskStep4Panel", codeBehind, StringComparison.Ordinal);
        Assert.Contains("2 => TaskStep2Panel", codeBehind, StringComparison.Ordinal);
        Assert.Contains("3 => TaskStep5Panel", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"TaskStep3TryButton\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=\"TaskStep5OpenAppearance_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"TaskStep4OpenTrayMenu_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep5TodoToggle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep5QuickCaptureToggle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep5SearchToggle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep5WeatherToggle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep5MusicToggle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep5GlanceToggle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep3StoragePathText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep3QuickAccessToggle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep3DesktopShortcutToggle\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"TaskStep2ConfirmPathButton\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"TaskStep2StoragePathText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("0 when !_hasCompletedFilePractice", codeBehind, StringComparison.Ordinal);
        Assert.Contains("1 when !_hasCompletedVisibilityPractice", codeBehind, StringComparison.Ordinal);

        string activeFlow = xaml[xaml.IndexOf(
            "x:Name=\"TaskStep2Panel\"",
            StringComparison.Ordinal)..xaml.IndexOf(
            "x:Name=\"Step1Panel\"",
            StringComparison.Ordinal)];
        Assert.DoesNotContain("Onboarding.Task.Step4.FeatureEntry", activeFlow, StringComparison.Ordinal);
        Assert.Contains("Click=\"TaskStep4OpenTrayMenu_Click\"", activeFlow, StringComparison.Ordinal);
        Assert.DoesNotContain("Onboarding.Task.Step2.ManagedBody", activeFlow, StringComparison.Ordinal);
        Assert.DoesNotContain("Onboarding.Task.Step2.MappedBody", activeFlow, StringComparison.Ordinal);
        Assert.DoesNotContain("Onboarding.Task.Step3.DragBody", activeFlow, StringComparison.Ordinal);
        Assert.Contains("Onboarding.Task.Step5.OptionalBody", activeFlow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep5OptionalHint\"", activeFlow, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"TaskStep5OptionalCard\"", activeFlow, StringComparison.Ordinal);
        Assert.Contains("Toggled=\"TaskStep3QuickAccessToggle_Toggled\"", activeFlow, StringComparison.Ordinal);
        Assert.Contains("Toggled=\"TaskStep3DesktopShortcutToggle_Toggled\"", activeFlow, StringComparison.Ordinal);
    }

    [Fact]
    public void StorageEntryChoices_AreInTheActiveFlowAndRequireUserActions()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.xaml"));
        string taskFlow = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.TaskFlow.cs"));
        string appCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/App.xaml.cs"));
        string shortcutService = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Services/ManagedStorageDesktopShortcutService.cs"));

        string activeFlow = xaml[xaml.IndexOf(
            "x:Name=\"TaskStep2Panel\"",
            StringComparison.Ordinal)..xaml.IndexOf(
            "x:Name=\"Step1Panel\"",
            StringComparison.Ordinal)];
        Assert.Contains("Onboarding.Step4.PinTitle", activeFlow, StringComparison.Ordinal);
        Assert.Contains("Settings.ManagedPath.DesktopShortcut.Title", activeFlow, StringComparison.Ordinal);
        Assert.Contains("CreateAsync()", taskFlow, StringComparison.Ordinal);
        Assert.Contains("RemoveAsync()", taskFlow, StringComparison.Ordinal);
        Assert.Contains("ManagedStorageDesktopShortcutService.SyncAsync()", appCode, StringComparison.Ordinal);
        Assert.Contains("preference instead of resurrecting", shortcutService, StringComparison.Ordinal);
        Assert.DoesNotContain("ShouldMaintainShortcut", shortcutService, StringComparison.Ordinal);
    }

    [Fact]
    public void Practices_CompleteOnlyAfterRealFileAndVisibilityOperations()
    {
        string root = FindRepositoryRoot();
        string taskFlow = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.TaskFlow.cs"));
        string appCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/App.xaml.cs"));
        string fileSurfaceCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"));

        Assert.Contains("OnOnboardingFileImportCompleted", taskFlow, StringComparison.Ordinal);
        Assert.Contains("_hasHiddenWidgetsDuringPractice", taskFlow, StringComparison.Ordinal);
        Assert.Contains("OnboardingFileImportCompleted", appCode, StringComparison.Ordinal);
        Assert.Contains("NotifyOnboardingFileImportCompleted", fileSurfaceCode, StringComparison.Ordinal);
        Assert.Contains("ReleaseOnboardingFileWidgetRaise", taskFlow, StringComparison.Ordinal);
        Assert.Contains("SetWidgetOnboardingTopMost", appCode, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaceWindowForWidgetPractice", taskFlow, StringComparison.Ordinal);
        Assert.DoesNotContain("_hasCompletedFilePractice = true", taskFlow[..taskFlow.IndexOf(
            "OnOnboardingFileImportCompleted",
            StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    [Fact]
    public void TaskFlow_IsLocalizedInEveryLanguage()
    {
        string root = FindRepositoryRoot();
        string stringsDirectory = Path.Combine(root, "src/DeskBox/Strings");
        string viewsDirectory = Path.Combine(root, "src/DeskBox/Views");
        var referencedKeys = new HashSet<string>(RequiredTaskFlowKeys, StringComparer.Ordinal);

        IEnumerable<string> onboardingSources =
        [
            Path.Combine(viewsDirectory, "OnboardingWindow.xaml"),
            .. Directory.GetFiles(viewsDirectory, "OnboardingWindow*.cs")
        ];
        foreach (string sourcePath in onboardingSources)
        {
            string source = File.ReadAllText(sourcePath);
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(
                         source,
                         "svc:Localized\\.Key=\"([^\"]+)\""))
            {
                referencedKeys.Add(match.Groups[1].Value);
            }

            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(
                         source,
                         "(?:\\.T|\\.Format)\\(\"([^\"]+)\""))
            {
                referencedKeys.Add(match.Groups[1].Value);
            }
        }

        foreach (string path in Directory.GetFiles(stringsDirectory, "*.json"))
        {
            using JsonDocument strings = JsonDocument.Parse(File.ReadAllText(path));
            foreach (string key in referencedKeys)
            {
                Assert.True(
                    strings.RootElement.TryGetProperty(key, out JsonElement value) &&
                    !string.IsNullOrWhiteSpace(value.GetString()),
                    $"{Path.GetFileName(path)} is missing {key}.");
            }
        }
    }

    [Fact]
    public void ChineseTaskFlow_IsDirectAndKeepsAdvancedConceptsOutOfFilePractice()
    {
        string root = FindRepositoryRoot();
        using JsonDocument strings = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Strings/zh-CN.json")));

        Assert.Contains(
            "移动",
            strings.RootElement.GetProperty("Onboarding.Task.Step3.Body").GetString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "复制",
            strings.RootElement.GetProperty("Onboarding.Task.Step3.Body").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "哪个窗口",
            strings.RootElement.GetProperty("Onboarding.Task.Step4.Body").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "映射为格子",
            strings.RootElement.GetProperty("Onboarding.Task.Step4.TrayBody").GetString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "叫回来",
            strings.RootElement.GetProperty("Onboarding.Task.Step4.StatusHidden").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(
            "让文件、随记和待办随时留在桌面的轻量格子里",
            strings.RootElement.GetProperty("Onboarding.Intro.Body").GetString());
        Assert.DoesNotContain(
            "。",
            strings.RootElement.GetProperty("Onboarding.Intro.Body").GetString(),
            StringComparison.Ordinal);
        Assert.True(
            strings.RootElement.GetProperty("Onboarding.Task.Step4.Body").GetString()!.Length < 50,
            "The first-screen explanation should stay scannable.");
    }

    [Fact]
    public void DefaultManagedDropAction_RemainsMove()
    {
        string root = FindRepositoryRoot();
        string settingsModel = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Models/AppSettings.cs"));
        string userGuide = File.ReadAllText(Path.Combine(
            root,
            "docs/articles/15-getting-started.md"));

        Assert.Contains("ManagedDropAction { get; set; } = \"Move\"", settingsModel, StringComparison.Ordinal);
        Assert.Contains("默认拖入行为是移动", userGuide, StringComparison.Ordinal);
        Assert.DoesNotContain("默认拖入行为是复制", userGuide, StringComparison.Ordinal);
    }

    [Fact]
    public void IntroLogoAnimation_HoldsTextForOnePointFiveSecondsThenCrossfadesToFirstStep()
    {
        string root = FindRepositoryRoot();
        string introCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.IntroAnimations.cs"));

        Assert.Contains("CreateDeskBoxMark", introCode, StringComparison.Ordinal);
        Assert.Contains("IntroAnimationTargetMilliseconds = 3720", introCode, StringComparison.Ordinal);
        Assert.Contains("IntroAnimationTargetMilliseconds + 1000", introCode, StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(1500)", introCode, StringComparison.Ordinal);
        Assert.Contains("Task.WhenAll(", introCode, StringComparison.Ordinal);
        Assert.Contains(
            "introGeneration, IntroMarkHost, 1, 0, 0, 0, 0, 0, 1, 1, 480",
            introCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain("GetIntroMarkTargetTransform", introCode, StringComparison.Ordinal);
        Assert.DoesNotContain("target.Translate", introCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskFlow_UsesVisualGuidanceAndIconBackedStatus()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.xaml.cs"));
        string activeFlow = xaml[xaml.IndexOf(
            "x:Name=\"TaskStep2Panel\"",
            StringComparison.Ordinal)..xaml.IndexOf(
            "x:Name=\"Step1Panel\"",
            StringComparison.Ordinal)];

        Assert.Contains("Onboarding.Task.Step3.DragTitle", activeFlow, StringComparison.Ordinal);
        Assert.Contains("Onboarding.Task.Step3.PasteTitle", activeFlow, StringComparison.Ordinal);
        Assert.Contains("Onboarding.Task.Step3.AddTitle", activeFlow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep3StatusIcon\"", activeFlow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep4StatusIcon\"", activeFlow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep2OpenTrayMenuButton\"", activeFlow, StringComparison.Ordinal);
        Assert.Contains("Glyph=\"&#xE713;\"", activeFlow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep3VisualStage\"", activeFlow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep3FileToken\"", activeFlow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep4PreviewWidgets\"", activeFlow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep2MenuPreview\"", activeFlow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep5FeatureGrid\"", activeFlow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep5FeatureSection\"", activeFlow, StringComparison.Ordinal);
        Assert.Contains("Width=\"720\"", activeFlow, StringComparison.Ordinal);
        Assert.Contains("xmlns:controls=\"using:DeskBox.Controls\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<controls:WidgetTitleIcon IconKind=\"Todo\" Mode=\"Color\"", activeFlow, StringComparison.Ordinal);
        Assert.Contains("<controls:WidgetTitleIcon IconKind=\"QuickCapture\" Mode=\"Color\"", activeFlow, StringComparison.Ordinal);
        Assert.Contains("<controls:WidgetTitleIcon IconKind=\"Search\" Mode=\"Color\"", activeFlow, StringComparison.Ordinal);
        Assert.Contains("<controls:WidgetTitleIcon IconKind=\"Weather\" Mode=\"Color\"", activeFlow, StringComparison.Ordinal);
        Assert.Contains("<controls:WidgetTitleIcon IconKind=\"Music\" Mode=\"Color\"", activeFlow, StringComparison.Ordinal);
        Assert.Contains("<controls:WidgetTitleIcon IconKind=\"Glance\" Mode=\"Color\"", activeFlow, StringComparison.Ordinal);
        Assert.Contains("Onboarding.Task.Step5.TodoDescription", activeFlow, StringComparison.Ordinal);
        Assert.Contains("Onboarding.Task.Step5.QuickCaptureDescription", activeFlow, StringComparison.Ordinal);
        Assert.Contains("Onboarding.Task.Step5.SearchDescription", activeFlow, StringComparison.Ordinal);
        Assert.Contains("Onboarding.Task.Step5.WeatherDescription", activeFlow, StringComparison.Ordinal);
        Assert.Contains("Onboarding.Task.Step5.MusicDescription", activeFlow, StringComparison.Ordinal);
        Assert.Contains("WidgetContent.Glance.StatusDescription", activeFlow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StepCounterText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep5TodoToggle\" Width=\"40\" MinWidth=\"0\" HorizontalAlignment=\"Right\"", activeFlow, StringComparison.Ordinal);
        Assert.Contains("dot.Width = active ? 8 : 6", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskFlow_UsesPurposefulMotionAndResponsiveVisualStages()
    {
        string root = FindRepositoryRoot();
        string codeBehind = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.xaml.cs"));
        string taskFlow = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.TaskFlow.cs"));

        Assert.Contains("CreateFilePracticeAmbientStoryboard", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CreateVisibilityPracticeAmbientStoryboard", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CreateTrayAmbientStoryboard", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CreateFeatureCardEntranceStoryboard", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ApplyTwoColumnTaskLayout", codeBehind, StringComparison.Ordinal);
        Assert.Contains("TaskStep5FeatureSection.Width = compact", codeBehind, StringComparison.Ordinal);
        Assert.Contains("AnimateStatusFeedback", taskFlow, StringComparison.Ordinal);
        Assert.Contains("UpdateVisibilityPreview", taskFlow, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ja-JP.json", "グリッド")]
    [InlineData("de-DE.json", "Raster")]
    [InlineData("pt-BR.json", "grade")]
    public void TaskFlow_UsesWidgetTermInsteadOfLayoutGrid(
        string fileName,
        string forbiddenTerm)
    {
        string root = FindRepositoryRoot();
        using JsonDocument strings = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Strings",
            fileName)));

        foreach (JsonProperty property in strings.RootElement.EnumerateObject()
                     .Where(property => property.Name.StartsWith(
                         "Onboarding.Task.",
                         StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(
                forbiddenTerm,
                property.Value.GetString(),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Onboarding_IsCompletedOnlyByTheWindowAndPersistsProgress()
    {
        string root = FindRepositoryRoot();
        string appCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/App.xaml.cs"));
        string windowCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.xaml.cs"));

        string ensureMethod = appCode[appCode.IndexOf(
            "private async Task<bool> EnsureOnboardingAsync",
            StringComparison.Ordinal)..appCode.IndexOf(
            "public void ShowOnboarding",
            StringComparison.Ordinal)];
        Assert.DoesNotContain("HasCompletedOnboarding = true", ensureMethod, StringComparison.Ordinal);
        Assert.Contains("OnboardingStepIndex = newStep", windowCode, StringComparison.Ordinal);
        Assert.Contains("CompletedOnboardingVersion = CurrentOnboardingVersion", windowCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalFeatureSwitches_PersistImmediatelyAndFinishWaitsForSynchronization()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.xaml"));
        string windowCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.xaml.cs"));
        string taskFlow = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.TaskFlow.cs"));

        foreach (string kind in new[] { "Todo", "QuickCapture", "Search", "Weather", "Music", "Glance" })
        {
            Assert.Contains(
                $"Tag=\"{kind}\" Toggled=\"TaskStep5FeatureToggle_Toggled\"",
                xaml,
                StringComparison.Ordinal);
        }

        Assert.Contains("PersistFeatureWidgetSelectionAfterAsync", taskFlow, StringComparison.Ordinal);
        Assert.Contains("reveal: enabled", taskFlow, StringComparison.Ordinal);
        Assert.Contains("SynchronizeFeatureTogglesFromSettings", taskFlow, StringComparison.Ordinal);
        Assert.Contains("_settingsService.SettingsChanged += OnFeatureWidgetSettingsChanged", windowCode, StringComparison.Ordinal);
        Assert.Contains("_settingsService.SettingsChanged -= OnFeatureWidgetSettingsChanged", windowCode, StringComparison.Ordinal);
        Assert.DoesNotContain("_featureWidgetsSelectedForReveal", taskFlow, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyFeatureWidgetSelectionsAsync", taskFlow, StringComparison.Ordinal);

        string completionMethod = windowCode[windowCode.IndexOf(
            "private async Task CompleteOnboardingAsync",
            StringComparison.Ordinal)..windowCode.IndexOf(
            "private async Task NavigateToStepAsync",
            StringComparison.Ordinal)];
        Assert.True(
            completionMethod.IndexOf("await _featureWidgetSelectionUpdateTask", StringComparison.Ordinal) <
            completionMethod.IndexOf("Close();", StringComparison.Ordinal));
    }

    [Fact]
    public void FeatureWidgetEnableOperations_AreSerializedAndShowingRestoresVisibility()
    {
        string root = FindRepositoryRoot();
        string manager = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Services/WidgetManager.cs"));
        string featureManager = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox/Services/WidgetManager.FeatureWidgets.cs"));
        string settingsCallbacks = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/ViewModels/SettingsViewModel.FeatureCallbacks.cs"));

        Assert.Contains("_featureWidgetUpdateLocks", featureManager, StringComparison.Ordinal);
        Assert.Contains("await updateLock.WaitAsync()", featureManager, StringComparison.Ordinal);
        Assert.Contains("updateLock.Release()", featureManager, StringComparison.Ordinal);
        Assert.Contains("config.IsVisible = true;", manager, StringComparison.Ordinal);
        Assert.Contains(
            "SetFeatureWidgetEnabledAsync(\n                    WidgetKind.QuickCapture",
            settingsCallbacks.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyFileWidget_KeepsOneConciseActionHint()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml"));

        Assert.Contains(
            "svc:Localized.Key=\"Widget.Empty.ActionsHint\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Text=\"{Binding EmptyStateText}\"",
            xaml,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "src",
                    "DeskBox",
                    "DeskBox.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "DeskBox repository root was not found.");
    }
}
