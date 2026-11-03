using System.Text.Json;

namespace DeskBox.Tests;

public sealed class OnboardingExperienceTests
{
    private static readonly string[] RequiredOnboardingKeys =
    [
        "Onboarding.Back",
        "Onboarding.Next",
        "Onboarding.Start",
        "Onboarding.Step1.Title",
        "Onboarding.Step1.Body",
        "Onboarding.Step1.Hint",
        "Onboarding.Step2.Title",
        "Onboarding.Step2.Body",
        "Onboarding.Step2.ChangeInSettings",
        "Onboarding.Step2.MoveExplain",
        "Onboarding.Step2.CopyExplain",
        "Onboarding.Step2.WarningTitle",
        "Onboarding.Step2.WarningBody",
        "Onboarding.Kind.Managed.Title",
        "Onboarding.Kind.Managed.Body",
        "Onboarding.Kind.Mapped.Title",
        "Onboarding.Kind.Mapped.Body",
        "Onboarding.Step3.ChangeHotkey",
        "Onboarding.Step4.QuickSettings",
        "Onboarding.Skip",
        "Onboarding.Scene.DesktopLabel",
        "Onboarding.Scene.FileName1",
        "Onboarding.Scene.FileName2",
        "Onboarding.Scene.FileName3",
        "Onboarding.Step3.Title",
        "Onboarding.Step3.Body",
        "Onboarding.Step3.Try",
        "Onboarding.Step3.StatusReady",
        "Onboarding.Step3.StatusShown",
        "Onboarding.Step3.StatusHidden",
        "Onboarding.Step3.StatusDone",
        "Onboarding.Step3.Hint",
        "Onboarding.Step4.Title",
        "Onboarding.Step4.Body",
        "Onboarding.Step4.OpenMenu",
        "Onboarding.Step4.Hint",
        "Onboarding.Step5.Title",
        "Onboarding.Step5.Body",
        "Onboarding.Step5.OptionalBody",
        "Onboarding.Feature.Todo",
        "Onboarding.Feature.QuickCapture",
        "Onboarding.Feature.Search",
        "Onboarding.Feature.Weather",
        "Onboarding.Feature.Music",
        "Onboarding.Scene.MoveBadge",
        "Widget.Empty.ActionsHint"
    ];

    [Fact]
    public void TaskFlow_PresentsAFiveStepJourneyWithOnePersistentScene()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.xaml.cs"));

        Assert.Contains("StepCount = 5", codeBehind, StringComparison.Ordinal);
        Assert.Contains("0 => Step1Panel", codeBehind, StringComparison.Ordinal);
        Assert.Contains("1 => Step2Panel", codeBehind, StringComparison.Ordinal);
        Assert.Contains("2 => Step3Panel", codeBehind, StringComparison.Ordinal);
        Assert.Contains("3 => Step4Panel", codeBehind, StringComparison.Ordinal);
        Assert.Contains("4 => Step5Panel", codeBehind, StringComparison.Ordinal);

        // The scene stage is mounted once; each step owns one hero illustration.
        Assert.Contains("x:Name=\"ScenePanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SceneStage\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SceneHalo\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SceneMark\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SceneDropBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SceneFileToken\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SceneKeycapHost\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SceneTrayMenu\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SceneFeatureTiles\"", xaml, StringComparison.Ordinal);

        Assert.Contains("Click=\"Step3Try_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"Step4OpenMenu_Click\"", xaml, StringComparison.Ordinal);
        foreach (string kind in new[] { "Todo", "QuickCapture", "Search", "Weather", "Music", "Glance" })
        {
            Assert.Contains(
                $"Tag=\"{kind}\" Toggled=\"Step5FeatureToggle_Toggled\"",
                xaml,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Scene_MorphsBetweenStepsWithCompositorMotionAndHonorsReducedMotion()
    {
        string root = FindRepositoryRoot();
        string scene = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.Scene.cs"));
        string codeBehind = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.xaml.cs"));

        Assert.Contains("ApplySceneState(int step, bool animate)", scene, StringComparison.Ordinal);
        Assert.Contains("StartSceneLoops(int step)", scene, StringComparison.Ordinal);
        Assert.Contains("StartFileFlightLoop", scene, StringComparison.Ordinal);
        Assert.Contains("StartKeycapPressLoop", scene, StringComparison.Ordinal);
        Assert.Contains("StartTrayHaloLoop", scene, StringComparison.Ordinal);
        Assert.Contains("StartFeatureFloatLoop", scene, StringComparison.Ordinal);
        Assert.Contains("AnimationIterationBehavior.Forever", scene, StringComparison.Ordinal);
        Assert.Contains(
            "WindowsCompatibilityService.AreAnimationsEnabled",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "WindowsCompatibilityService.AreAnimationsEnabled",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("PlayTextColumnSwap", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ElementCompositionPreview", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void SummonAndTraySteps_DriveTheRealEntryPoints()
    {
        string root = FindRepositoryRoot();
        string steps = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.Steps.cs"));
        string windowCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.xaml.cs"));

        Assert.Contains("ToggleWidgetsForOnboardingAsync", steps, StringComparison.Ordinal);
        Assert.Contains("ShowTrayContextMenuForOnboarding", steps, StringComparison.Ordinal);
        Assert.Contains("OnOnboardingWidgetsVisibilityChanged", steps, StringComparison.Ordinal);
        Assert.Contains(
            "OnboardingWidgetsVisibilityChanged += OnOnboardingWidgetsVisibilityChanged",
            windowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "OnboardingWidgetsVisibilityChanged -= OnOnboardingWidgetsVisibilityChanged",
            windowCode,
            StringComparison.Ordinal);
        Assert.Contains("FormatActivation", steps, StringComparison.Ordinal);
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
                         "Onboarding.",
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
        string steps = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.Steps.cs"));
        string windowCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.xaml.cs"));

        Assert.Contains("PersistFeatureWidgetSelectionAfterAsync", steps, StringComparison.Ordinal);
        Assert.Contains("reveal: enabled", steps, StringComparison.Ordinal);
        Assert.Contains("SynchronizeFeatureTogglesFromSettings", steps, StringComparison.Ordinal);
        Assert.Contains("_settingsService.SettingsChanged += OnFeatureWidgetSettingsChanged", windowCode, StringComparison.Ordinal);
        Assert.Contains("_settingsService.SettingsChanged -= OnFeatureWidgetSettingsChanged", windowCode, StringComparison.Ordinal);

        string completionMethod = windowCode[windowCode.IndexOf(
            "private async Task CompleteOnboardingAsync",
            StringComparison.Ordinal)..windowCode.IndexOf(
            "private void NavigateToStep",
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
        string featureManager = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Services/WidgetManager.FeatureWidgets.cs"));
        string quickCaptureCoordinator = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Services/QuickCaptureSettingsCoordinator.cs"));
        string quickCaptureEditor = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Features/QuickCapture/QuickCaptureSettingsViewModel.cs"));

        Assert.Contains("_featureWidgetUpdateLocks", featureManager, StringComparison.Ordinal);
        Assert.Contains("await updateLock.WaitAsync()", featureManager, StringComparison.Ordinal);
        Assert.Contains("updateLock.Release()", featureManager, StringComparison.Ordinal);
        Assert.Contains("config.IsVisible = true;", manager, StringComparison.Ordinal);
        Assert.Contains("_settings.SetEnabledAsync(value, reveal: value)",
            quickCaptureEditor, StringComparison.Ordinal);
        Assert.Contains("await _widgetGate.WaitAsync(linked.Token)",
            quickCaptureCoordinator, StringComparison.Ordinal);
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

    [Fact]
    public void TaskFlow_IsLocalizedInEveryLanguage()
    {
        string root = FindRepositoryRoot();
        string stringsDirectory = Path.Combine(root, "src/DeskBox/Strings");
        string viewsDirectory = Path.Combine(root, "src/DeskBox/Views");
        var referencedKeys = new HashSet<string>(RequiredOnboardingKeys, StringComparer.Ordinal);

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
    public void ChineseTaskFlow_IsDirectAndKeepsAdvancedConceptsOutOfFirstSteps()
    {
        string root = FindRepositoryRoot();
        using JsonDocument strings = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Strings/zh-CN.json")));

        Assert.Contains(
            "移动",
            strings.RootElement.GetProperty("Onboarding.Step2.Body").GetString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "复制",
            strings.RootElement.GetProperty("Onboarding.Step2.Body").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "映射为格子",
            strings.RootElement.GetProperty("Onboarding.Step4.Body").GetString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "叫回来",
            strings.RootElement.GetProperty("Onboarding.Step3.StatusHidden").GetString(),
            StringComparison.Ordinal);
        Assert.True(
            strings.RootElement.GetProperty("Onboarding.Step1.Title").GetString()!.Length <= 24,
            "The first-step title should stay scannable.");
        Assert.True(
            strings.RootElement.GetProperty("Onboarding.Step3.Body").GetString()!.Length < 60,
            "The summon-step explanation should stay scannable.");
    }

    [Fact]
    public void DefaultManagedDropAction_RemainsMove()
    {
        string root = FindRepositoryRoot();
        string settingsModel = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Models/FileWidgetSettingsSlice.cs"));
        string userGuide = File.ReadAllText(Path.Combine(
            root,
            "docs/articles/15-getting-started.md"));

        Assert.Contains("ManagedDropAction { get; set; } = \"Move\"", settingsModel, StringComparison.Ordinal);
        Assert.Contains("默认拖入行为是移动", userGuide, StringComparison.Ordinal);
        Assert.DoesNotContain("默认拖入行为是复制", userGuide, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstStepArrivesDirectlyWithoutAnIntroScreen()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.xaml"));
        string code = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.xaml.cs"));

        Assert.DoesNotContain("IntroOverlay", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IntroMarkHost", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PlayIntroSequence", code, StringComparison.Ordinal);
        Assert.Contains("PlaySceneEntrance", code, StringComparison.Ordinal);
    }

    [Fact]
    public void MoveStep_MakesThePathConsequenceExplicitAndEditableInPlace()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "src/DeskBox/Views/OnboardingWindow.xaml"));
        string steps = File.ReadAllText(Path.Combine(root, "src/DeskBox/Views/OnboardingWindow.Steps.cs"));
        string scene = File.ReadAllText(Path.Combine(root, "src/DeskBox/Views/OnboardingWindow.Scene.cs"));
        string code = File.ReadAllText(Path.Combine(root, "src/DeskBox/Views/OnboardingWindow.xaml.cs"));
        using JsonDocument strings = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Strings/zh-CN.json")));

        // The move consequence is the headline, and the warning names the
        // safe alternatives (folder mapping, Alt-drop shortcut).
        Assert.Contains("移动", strings.RootElement.GetProperty("Onboarding.Step2.Title").GetString(), StringComparison.Ordinal);
        string warning = strings.RootElement.GetProperty("Onboarding.Step2.WarningBody").GetString()!;
        Assert.Contains("映射", warning, StringComparison.Ordinal);
        Assert.Contains("Alt", warning, StringComparison.Ordinal);

        // Drop mode is editable on the step through the FileWidget slice, and
        // the stage replays the story in the chosen mode.
        Assert.Contains("Checked=\"Step2DropMode_Checked\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Severity=\"Warning\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Settings.FileWidget", steps, StringComparison.Ordinal);
        Assert.Contains("SetDropSceneMode(copy, replay: true)", steps, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ScenePathStrike\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SceneFileGhost1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("\"Scale.X\"", scene, StringComparison.Ordinal);

        // Skipping is only offered after the move/path safety step.
        Assert.Contains("FirstSkippableStep = 2", code, StringComparison.Ordinal);
        Assert.Contains("Click=\"SkipButton_Click\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage_AnswersRealInputAndHonorsReducedMotionWithStaticFrames()
    {
        string root = FindRepositoryRoot();
        string steps = File.ReadAllText(Path.Combine(root, "src/DeskBox/Views/OnboardingWindow.Steps.cs"));
        string scene = File.ReadAllText(Path.Combine(root, "src/DeskBox/Views/OnboardingWindow.Scene.cs"));
        string code = File.ReadAllText(Path.Combine(root, "src/DeskBox/Views/OnboardingWindow.xaml.cs"));

        // Real F7/tray toggles take over the summon scene.
        Assert.Contains("MirrorSummonScene(hasVisibleWidgets)", steps, StringComparison.Ordinal);
        // Feature switches pop their stage tiles.
        Assert.Contains("ApplyFeatureTileState(kind, toggle.IsOn, animate: true)", steps, StringComparison.Ordinal);
        // Direction-aware hand-off and staggered copy reveal.
        Assert.Contains("ApplySceneState(newStep, animate: true, direction)", code, StringComparison.Ordinal);
        Assert.Contains("PlayTextReveal", code, StringComparison.Ordinal);
        // Reduced motion lands on a meaningful end frame, not a blank stage.
        Assert.Contains("PrepareStepPose(step, finalFrame: !MotionEnabled)", scene, StringComparison.Ordinal);
    }

    [Fact]
    public void Onboarding_NeverReferencesSettingsKeysTheResidentHostFiltersOut()
    {
        // The main process drops pure settings-UI strings to save memory; the
        // guide runs there, so a filtered key would render as its raw name.
        string root = FindRepositoryRoot();
        string viewsDirectory = Path.Combine(root, "src/DeskBox/Views");
        IEnumerable<string> sources =
        [
            Path.Combine(viewsDirectory, "OnboardingWindow.xaml"),
            .. Directory.GetFiles(viewsDirectory, "OnboardingWindow*.cs")
        ];
        foreach (string source in sources)
        {
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(
                         File.ReadAllText(source),
                         "\"(Settings\\.[A-Za-z0-9.]+)\""))
            {
                string key = match.Groups[1].Value;
                Assert.False(
                    DeskBox.Services.LocalizationService.IsPureSettingsUiKey(key),
                    $"{Path.GetFileName(source)} references filtered key {key}.");
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DeskBox.slnx")) ||
                File.Exists(Path.Combine(directory.FullName, "DeskBox.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the DeskBox repository root.");
    }
}
