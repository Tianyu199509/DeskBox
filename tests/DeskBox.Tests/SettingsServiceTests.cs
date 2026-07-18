using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _tempRoot;
    private readonly string _settingsRoot;

    public SettingsServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        _settingsRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "settings")).FullName;
    }

    [Fact]
    public async Task LoadAsync_PreservesQuickCaptureWidgetsAndRemovesLegacyProductivityWidgets()
    {
        var settings = new AppSettings
        {
            QuickCaptureEnabled = true,
            Widgets =
            [
                new WidgetConfig
                {
                    Id = "quick-capture",
                    Name = "Quick Capture",
                    WidgetKind = WidgetKind.QuickCapture,
                    IsVisible = true
                },
                new WidgetConfig
                {
                    Id = "legacy-productivity",
                    Name = "Legacy",
                    WidgetKind = WidgetKind.Productivity,
                    IsVisible = true
                }
            ]
        };

        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            JsonSerializer.Serialize(settings, s_jsonOptions));

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.True(service.Settings.QuickCaptureEnabled);
        var widget = Assert.Single(service.Settings.Widgets);
        Assert.Equal("quick-capture", widget.Id);
        Assert.Equal(WidgetKind.QuickCapture, widget.WidgetKind);
    }

    [Fact]
    public async Task LoadAsync_PreservesFutureWidgetKindsAndMetadata()
    {
        var settings = new AppSettings
        {
            Widgets =
            [
                new WidgetConfig
                {
                    Id = "weather",
                    Name = "Weather",
                    WidgetKind = WidgetKind.Weather,
                    Metadata =
                    {
                        ["city"] = "Shanghai",
                        ["unit"] = "metric"
                    }
                }
            ]
        };

        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            JsonSerializer.Serialize(settings, s_jsonOptions));

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        var widget = Assert.Single(service.Settings.Widgets);
        Assert.Equal(WidgetKind.Weather, widget.WidgetKind);
        Assert.Equal("Shanghai", widget.Metadata["city"]);
        Assert.Equal("metric", widget.Metadata["unit"]);
    }

    [Fact]
    public async Task LoadAsync_SafelyDowngradesUnknownWidgetKind()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            """
            {
              "widgets": [
                {
                  "id": "unknown-kind",
                  "name": "Unknown",
                  "widgetKind": "FutureExperimentalWidget",
                  "isVisible": true
                }
              ]
            }
            """);

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        var widget = Assert.Single(service.Settings.Widgets);
        Assert.Equal("unknown-kind", widget.Id);
        Assert.Equal(WidgetKind.File, widget.WidgetKind);
    }

    [Fact]
    public async Task LoadAsync_NormalizesQuickCaptureRecentLimit()
    {
        var settings = new AppSettings
        {
            QuickCaptureRecentLimit = 2
        };

        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            JsonSerializer.Serialize(settings, s_jsonOptions));

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.Equal(QuickCaptureService.DefaultRecentLimit, service.Settings.QuickCaptureRecentLimit);
    }

    [Fact]
    public async Task LoadAsync_DefaultsQuickCaptureClipboardRecordingToDisabled()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            "{}");

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.False(service.Settings.QuickCaptureClipboardEnabled);
        Assert.False(service.Settings.QuickCaptureImageClipboardEnabled);
    }

    [Fact]
    public async Task LoadAsync_DisabledQuickCaptureDisablesClipboardRecording()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            """
            {
              "quickCaptureEnabled": false,
              "quickCaptureClipboardEnabled": true,
              "quickCaptureImageClipboardEnabled": true
            }
            """);

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.False(FeatureWidgetSettings.IsEnabled(service.Settings, WidgetKind.QuickCapture));
        Assert.False(service.Settings.QuickCaptureClipboardEnabled);
        Assert.False(service.Settings.QuickCaptureImageClipboardEnabled);
    }

    [Fact]
    public async Task LoadAsync_DisabledClipboardRecordingDisablesImageRecording()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            """
            {
              "quickCaptureEnabled": true,
              "quickCaptureClipboardEnabled": false,
              "quickCaptureImageClipboardEnabled": true
            }
            """);

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.True(FeatureWidgetSettings.IsEnabled(service.Settings, WidgetKind.QuickCapture));
        Assert.False(service.Settings.QuickCaptureClipboardEnabled);
        Assert.False(service.Settings.QuickCaptureImageClipboardEnabled);
    }

    [Fact]
    public async Task LoadAsync_MigratesLegacyFeatureWidgetEnabledStates()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            """
            {
              "quickCaptureEnabled": false,
              "todoEnabled": true
            }
            """);

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.False(FeatureWidgetSettings.IsEnabled(service.Settings, WidgetKind.QuickCapture));
        Assert.True(FeatureWidgetSettings.IsEnabled(service.Settings, WidgetKind.Todo));
        Assert.False(service.Settings.QuickCaptureEnabled);
        Assert.True(service.Settings.TodoEnabled);
    }

    [Fact]
    public async Task LoadAsync_FeatureWidgetEnabledStatesSynchronizeLegacyMirrors()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            """
            {
              "quickCaptureEnabled": true,
              "todoEnabled": true,
              "featureWidgetEnabledStates": {
                "QuickCapture": false,
                "Todo": false
              }
            }
            """);

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.False(FeatureWidgetSettings.IsEnabled(service.Settings, WidgetKind.QuickCapture));
        Assert.False(FeatureWidgetSettings.IsEnabled(service.Settings, WidgetKind.Todo));
        Assert.False(service.Settings.QuickCaptureEnabled);
        Assert.False(service.Settings.TodoEnabled);
    }

    [Fact]
    public async Task LoadAsync_ClampsQuickCaptureRecentLimitToMaximum()
    {
        var settings = new AppSettings
        {
            QuickCaptureRecentLimit = 500
        };

        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            JsonSerializer.Serialize(settings, s_jsonOptions));

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.Equal(QuickCaptureService.MaxRecentLimit, service.Settings.QuickCaptureRecentLimit);
    }

    [Fact]
    public async Task LoadAsync_NormalizesQuickCaptureEditorSettings()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            """
            {
              "quickCaptureItemPreviewLineCount": 40,
              "quickCaptureEditorEnterBehavior": "unexpected"
            }
            """);

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.Equal(
            SettingsService.MaxItemPreviewLineCount,
            service.Settings.QuickCaptureItemPreviewLineCount);
        Assert.Equal(
            SettingsService.EditorEnterBehaviorCtrlEnterSaves,
            service.Settings.QuickCaptureEditorEnterBehavior);
    }

    [Fact]
    public async Task LoadAsync_NormalizesTodoSettings()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            """
            {
              "todoNewTaskPosition": "Middle",
              "todoDefaultFilter": "Someday",
              "todoShowCompletedTasks": false,
              "todoShowFooterStats": false,
              "todoShowClearCompletedButton": false,
              "todoConfirmBeforeDelete": true,
              "todoReminderEnabled": false,
              "todoDefaultReminderOffsetMinutes": 999,
              "todoItemPreviewLineCount": -4,
              "todoEditorEnterBehavior": "unexpected",
              "managedDropAction": "Copy"
            }
            """);

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.Equal(SettingsService.TodoNewTaskPositionTop, service.Settings.TodoNewTaskPosition);
        Assert.Equal(SettingsService.TodoDefaultFilterAll, service.Settings.TodoDefaultFilter);
        Assert.False(service.Settings.TodoShowCompletedTasks);
        Assert.False(service.Settings.TodoShowFooterStats);
        Assert.False(service.Settings.TodoShowClearCompletedButton);
        Assert.True(service.Settings.TodoConfirmBeforeDelete);
        Assert.False(service.Settings.TodoReminderEnabled);
        Assert.Equal(SettingsService.DefaultTodoReminderOffsetMinutes, service.Settings.TodoDefaultReminderOffsetMinutes);
        Assert.Equal(SettingsService.MinItemPreviewLineCount, service.Settings.TodoItemPreviewLineCount);
        Assert.Equal(
            SettingsService.EditorEnterBehaviorCtrlEnterSaves,
            service.Settings.TodoEditorEnterBehavior);
        Assert.Equal(SettingsService.ManagedDropActionCopy, service.Settings.ManagedDropAction);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(5, 5)]
    [InlineData(6, 6)]
    [InlineData(7, 7)]
    [InlineData(8, 8)]
    [InlineData(9, 9)]
    [InlineData(10, 10)]
    [InlineData(0, SettingsService.MinItemPreviewLineCount)]
    [InlineData(100, SettingsService.MaxItemPreviewLineCount)]
    public void NormalizeItemPreviewLineCount_ClampsToSupportedRange(int value, int expected)
    {
        Assert.Equal(expected, SettingsService.NormalizeItemPreviewLineCount(value));
    }

    [Theory]
    [InlineData(SettingsService.EditorEnterBehaviorCtrlEnterSaves, false, false)]
    [InlineData(SettingsService.EditorEnterBehaviorCtrlEnterSaves, true, true)]
    [InlineData(SettingsService.EditorEnterBehaviorEnterSaves, false, true)]
    [InlineData(SettingsService.EditorEnterBehaviorEnterSaves, true, false)]
    public void ShouldSubmitEditorOnEnter_UsesConfiguredModifier(
        string behavior,
        bool controlPressed,
        bool expected)
    {
        Assert.Equal(
            expected,
            SettingsService.ShouldSubmitEditorOnEnter(behavior, controlPressed));
    }

    [Fact]
    public async Task LoadAsync_NormalizesWidgetChromeSettingsAndMetadata()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            """
            {
              "displayWidgetChromeMode": "Floaty",
              "interactiveWidgetChromeMode": "hidden",
              "widgets": [
                {
                  "id": "music",
                  "name": "Music",
                  "widgetKind": "Music",
                  "metadata": {
                    "ChromeMode": "compact"
                  }
                },
                {
                  "id": "todo",
                  "name": "Todo",
                  "widgetKind": "Todo",
                  "metadata": {
                    "ChromeMode": "System"
                  }
                }
              ]
            }
            """);

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.Equal(SettingsService.WidgetChromeModeOverlay, service.Settings.DisplayWidgetChromeMode);
        Assert.Equal(SettingsService.WidgetChromeModeHidden, service.Settings.InteractiveWidgetChromeMode);
        Assert.Equal(SettingsService.WidgetChromeModeCompact, service.Settings.Widgets[0].Metadata[WidgetChromeModeNames.MetadataKey]);
        Assert.False(service.Settings.Widgets[1].Metadata.ContainsKey(WidgetChromeModeNames.MetadataKey));
    }

    [Fact]
    public async Task LoadAsync_NormalizesWidgetTitleIconMode()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            """
            {
              "widgetTitleIconMode": "Badge"
            }
            """);

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.Equal(SettingsService.WidgetTitleIconModeColor, service.Settings.WidgetTitleIconMode);
    }

    [Theory]
    [InlineData(SettingsService.WidgetMaterialTypeMicaAlt)]
    [InlineData(SettingsService.WidgetMaterialTypeAcrylicBase)]
    public async Task LoadAsync_PreservesNewNativeWidgetMaterials(string materialType)
    {
        var settings = new AppSettings
        {
            WidgetMaterialType = materialType,
            WidgetMaterialIntensity = 0.72,
            WidgetBorderColorMode = SettingsService.WidgetBorderColorModeAccent
        };
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            JsonSerializer.Serialize(settings, s_jsonOptions));

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.Equal(materialType, service.Settings.WidgetMaterialType);
        Assert.Equal(0.72, service.Settings.WidgetMaterialIntensity, precision: 3);
        Assert.Equal(SettingsService.WidgetBorderColorModeAccent, service.Settings.WidgetBorderColorMode);
    }

    [Fact]
    public async Task LoadAsync_MigratesLegacyNoBorderAndClampsMaterialIntensity()
    {
        var settings = new AppSettings
        {
            WidgetBorderStyle = SettingsService.WidgetBorderStyleNone,
            WidgetMaterialIntensity = 2.0
        };
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            JsonSerializer.Serialize(settings, s_jsonOptions));

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.Equal(SettingsService.WidgetBorderColorModeNone, service.Settings.WidgetBorderColorMode);
        Assert.Equal(SettingsService.WidgetBorderStyleThin, service.Settings.WidgetBorderStyle);
        Assert.Equal(SettingsService.MaxWidgetMaterialIntensity, service.Settings.WidgetMaterialIntensity);
    }

    [Theory]
    [InlineData(
        SettingsService.WidgetCollapsedStylePill,
        SettingsService.WidgetCompactContentModeSummary)]
    [InlineData(
        SettingsService.WidgetCollapsedStyleSmart,
        SettingsService.WidgetCompactContentModeSmart)]
    [InlineData(
        SettingsService.WidgetCollapsedStyleSummary,
        SettingsService.WidgetCompactContentModeSummary)]
    [InlineData(
        SettingsService.WidgetCollapsedStyleMinimal,
        SettingsService.WidgetCompactContentModeMinimal)]
    public async Task LoadAsync_MigratesLegacyCompactStyleIntoContentMode(
        string legacyStyle,
        string expectedContentMode)
    {
        var settings = new AppSettings
        {
            WidgetCollapsedStyle = legacyStyle,
            WidgetCompactSettingsVersion = 0
        };
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            JsonSerializer.Serialize(settings, s_jsonOptions));

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.Equal(expectedContentMode, service.Settings.WidgetCompactContentMode);
        Assert.Equal(
            SettingsService.CurrentWidgetCompactSettingsVersion,
            service.Settings.WidgetCompactSettingsVersion);
    }

    [Theory]
    [InlineData(
        SettingsService.SensitiveWidgetCompactExpandDelayMs,
        SettingsService.SensitiveWidgetCompactCollapseDelayMs,
        SettingsService.WidgetCompactHoverResponseSensitive)]
    [InlineData(
        SettingsService.DefaultWidgetCompactExpandDelayMs,
        SettingsService.DefaultWidgetCompactCollapseDelayMs,
        SettingsService.WidgetCompactHoverResponseBalanced)]
    [InlineData(
        SettingsService.PreventAccidentalWidgetCompactExpandDelayMs,
        SettingsService.PreventAccidentalWidgetCompactCollapseDelayMs,
        SettingsService.WidgetCompactHoverResponsePreventAccidental)]
    [InlineData(275, 735, SettingsService.WidgetCompactHoverResponseCustom)]
    public void ResolveWidgetCompactHoverResponse_MapsStoredDelaysToPreset(
        int expandDelayMs,
        int collapseDelayMs,
        string expected)
    {
        Assert.Equal(
            expected,
            SettingsService.ResolveWidgetCompactHoverResponse(expandDelayMs, collapseDelayMs));
    }

    [Theory]
    [InlineData(SettingsService.WidgetCompactHoverResponseSensitive)]
    [InlineData(SettingsService.WidgetCompactHoverResponseBalanced)]
    [InlineData(SettingsService.WidgetCompactHoverResponsePreventAccidental)]
    [InlineData(SettingsService.WidgetCompactHoverResponseCustom)]
    public void NormalizeWidgetCompactHoverResponse_PreservesKnownPreset(string value)
    {
        Assert.Equal(value, SettingsService.NormalizeWidgetCompactHoverResponse(value));
    }

    [Theory]
    [InlineData(
        SettingsService.WidgetAnimationEffectSlideLeftFade,
        SettingsService.WidgetAnimationSlideDirectionLeft)]
    [InlineData(
        SettingsService.WidgetAnimationEffectSlideRight,
        SettingsService.WidgetAnimationSlideDirectionRight)]
    [InlineData(
        SettingsService.WidgetAnimationEffectSlideUpFade,
        SettingsService.WidgetAnimationSlideDirectionUp)]
    [InlineData(
        SettingsService.WidgetAnimationEffectSlideDown,
        SettingsService.WidgetAnimationSlideDirectionDown)]
    public async Task LoadAsync_MigratesLegacyDirectionalAnimation(
        string legacyEffect,
        string expectedDirection)
    {
        var settings = new AppSettings
        {
            WidgetAnimationEffect = legacyEffect,
            WidgetAnimationSlideDirection = SettingsService.WidgetAnimationSlideDirectionNone
        };
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            JsonSerializer.Serialize(settings, s_jsonOptions));

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.Equal(SettingsService.WidgetAnimationEffectSlideFade, service.Settings.WidgetAnimationEffect);
        Assert.Equal(expectedDirection, service.Settings.WidgetAnimationSlideDirection);
    }

    [Fact]
    public async Task LoadAsync_SlideAnimationWithoutDirectionDefaultsToRight()
    {
        var settings = new AppSettings
        {
            WidgetAnimationEffect = SettingsService.WidgetAnimationEffectSlideFade,
            WidgetAnimationSlideDirection = SettingsService.WidgetAnimationSlideDirectionNone
        };
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            JsonSerializer.Serialize(settings, s_jsonOptions));

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.Equal(
            SettingsService.WidgetAnimationSlideDirectionRight,
            service.Settings.WidgetAnimationSlideDirection);
    }

    [Fact]
    public async Task LoadAsync_RepairsEmptyTabSelectionsAndHiddenDefaults()
    {
        var settings = new AppSettings
        {
            QuickCaptureDefaultView = SettingsService.QuickCaptureDefaultViewPinned,
            QuickCaptureShowRecordsTab = false,
            QuickCaptureShowPinnedTab = false,
            QuickCaptureShowRecentTab = false,
            TodoDefaultFilter = SettingsService.TodoDefaultFilterCompleted,
            TodoShowAllTab = false,
            TodoShowActiveTab = false,
            TodoShowTodayTab = false,
            TodoShowThisWeekTab = false,
            TodoShowThisMonthTab = false,
            TodoShowImportantTab = false,
            TodoShowCompletedTab = false
        };
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            JsonSerializer.Serialize(settings, s_jsonOptions));

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.True(service.Settings.QuickCaptureShowRecordsTab);
        Assert.Equal(SettingsService.QuickCaptureDefaultViewRecords, service.Settings.QuickCaptureDefaultView);
        Assert.True(service.Settings.TodoShowAllTab);
        Assert.Equal(SettingsService.TodoDefaultFilterAll, service.Settings.TodoDefaultFilter);
    }

    [Theory]
    [InlineData(SettingsService.TodoDefaultFilterThisWeek)]
    [InlineData(SettingsService.TodoDefaultFilterThisMonth)]
    public async Task LoadAsync_PreservesEnabledCalendarTabAsDefault(string filter)
    {
        var settings = new AppSettings
        {
            TodoDefaultFilter = filter,
            TodoShowAllTab = false,
            TodoShowTodayTab = false,
            TodoShowImportantTab = false,
            TodoShowCompletedTab = false,
            TodoShowThisWeekTab = filter == SettingsService.TodoDefaultFilterThisWeek,
            TodoShowThisMonthTab = filter == SettingsService.TodoDefaultFilterThisMonth
        };
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            JsonSerializer.Serialize(settings, s_jsonOptions));

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.Equal(filter, service.Settings.TodoDefaultFilter);
    }

    [Fact]
    public void ApplyDefaultPreferences_MatchesNewUserAppearanceDefaults()
    {
        var newUserDefaults = new AppSettings();
        var restoredDefaults = new AppSettings
        {
            WidgetAnimationEffect = SettingsService.WidgetAnimationEffectFade,
            WidgetCapsuleModeEnabled = true,
            WidgetCompactWidthMode = SettingsService.WidgetCompactWidthModeIndependent,
            WidgetCompactAnimationEffect = SettingsService.WidgetCompactAnimationSnappy,
            FileStackThreshold = 5,
            FileStackOrderBy = SettingsService.FileStackOrderByDateModified,
            WidgetTitleIconMode = SettingsService.WidgetTitleIconModeHidden,
            WidgetBorderStyle = SettingsService.WidgetBorderStyleThick,
            WidgetBorderColorMode = SettingsService.WidgetBorderColorModeNone,
            WidgetMaterialIntensity = 0.1,
            LayoutDensity = "Compact",
            Language = SettingsService.LanguageChinese,
            AutoStart = false,
            QuickCaptureEnabled = true,
            TodoEnabled = true,
            FeatureWidgetEnabledStates = new Dictionary<string, bool>
            {
                [WidgetKind.Music.ToString()] = true
            },
            QuickCaptureShowCreatedTime = false,
            QuickCaptureItemPreviewLineCount = 1,
            QuickCaptureEditorEnterBehavior = SettingsService.EditorEnterBehaviorEnterSaves,
            ResizeSnapEnabled = false,
            QuickCaptureTabStyle = SettingsService.WidgetTabStylePivot,
            TodoTabStyle = SettingsService.WidgetTabStylePivot,
            TodoShowFooterStats = true,
            TodoItemPreviewLineCount = 1,
            TodoEditorEnterBehavior = SettingsService.EditorEnterBehaviorEnterSaves,
            TodoReminderEnabled = false,
            TodoDefaultReminderOffsetMinutes = 999,
            ManagedDropAction = SettingsService.ManagedDropActionCopy,
            GlobalHotkeyEnabled = false,
            GlobalHotkeyModifiers = (int)HotkeyModifierKeys.Control,
            GlobalHotkeyKey = (int)Windows.System.VirtualKey.A,
            ShowFileItemPathTooltips = false,
            WidgetHoverButtonActions = SettingsService.WidgetHoverActionAdd
        };

        SettingsService.ApplyDefaultPreferences(restoredDefaults);

        Assert.Equal(SettingsService.WidgetAnimationEffectSlideFade, newUserDefaults.WidgetAnimationEffect);
        Assert.Equal(newUserDefaults.WidgetAnimationEffect, restoredDefaults.WidgetAnimationEffect);
        Assert.False(newUserDefaults.WidgetCapsuleModeEnabled);
        Assert.Equal(newUserDefaults.WidgetCapsuleModeEnabled, restoredDefaults.WidgetCapsuleModeEnabled);
        Assert.Equal(SettingsService.WidgetCompactWidthModeAligned, newUserDefaults.WidgetCompactWidthMode);
        Assert.Equal(newUserDefaults.WidgetCompactWidthMode, restoredDefaults.WidgetCompactWidthMode);
        Assert.Equal(SettingsService.WidgetCompactAnimationSmooth, newUserDefaults.WidgetCompactAnimationEffect);
        Assert.Equal(newUserDefaults.WidgetCompactAnimationEffect, restoredDefaults.WidgetCompactAnimationEffect);
        Assert.Equal(SettingsService.DefaultFileStackThreshold, restoredDefaults.FileStackThreshold);
        Assert.Equal(SettingsService.FileStackOrderByWidget, restoredDefaults.FileStackOrderBy);
        Assert.Equal(SettingsService.WidgetCompactContentModeSmart, restoredDefaults.WidgetCompactContentMode);
        Assert.Equal(SettingsService.WidgetTitleIconModeColor, newUserDefaults.WidgetTitleIconMode);
        Assert.Equal(newUserDefaults.WidgetTitleIconMode, restoredDefaults.WidgetTitleIconMode);
        Assert.Equal(SettingsService.WidgetBorderStyleThin, newUserDefaults.WidgetBorderStyle);
        Assert.Equal(newUserDefaults.WidgetBorderStyle, restoredDefaults.WidgetBorderStyle);
        Assert.Equal(SettingsService.WidgetBorderColorModeNeutral, newUserDefaults.WidgetBorderColorMode);
        Assert.Equal(newUserDefaults.WidgetBorderColorMode, restoredDefaults.WidgetBorderColorMode);
        Assert.Equal(SettingsService.DefaultWidgetMaterialIntensity, newUserDefaults.WidgetMaterialIntensity);
        Assert.Equal(newUserDefaults.WidgetMaterialIntensity, restoredDefaults.WidgetMaterialIntensity);
        Assert.True(newUserDefaults.AutoCheckForUpdates);
        Assert.Equal(newUserDefaults.AutoCheckForUpdates, restoredDefaults.AutoCheckForUpdates);
        Assert.False(newUserDefaults.QuickCaptureClipboardEnabled);
        Assert.False(newUserDefaults.QuickCaptureImageClipboardEnabled);
        Assert.Equal(newUserDefaults.QuickCaptureClipboardEnabled, restoredDefaults.QuickCaptureClipboardEnabled);
        Assert.Equal(newUserDefaults.QuickCaptureImageClipboardEnabled, restoredDefaults.QuickCaptureImageClipboardEnabled);
        Assert.True(newUserDefaults.TodoReminderEnabled);
        Assert.Equal(SettingsService.DefaultTodoReminderOffsetMinutes, newUserDefaults.TodoDefaultReminderOffsetMinutes);
        Assert.Equal(newUserDefaults.TodoReminderEnabled, restoredDefaults.TodoReminderEnabled);
        Assert.Equal(newUserDefaults.TodoDefaultReminderOffsetMinutes, restoredDefaults.TodoDefaultReminderOffsetMinutes);
        Assert.Equal(newUserDefaults.QuickCaptureTabStyle, restoredDefaults.QuickCaptureTabStyle);
        Assert.Equal(newUserDefaults.TodoTabStyle, restoredDefaults.TodoTabStyle);
        Assert.Equal(SettingsService.WidgetTabStyleButton, restoredDefaults.QuickCaptureTabStyle);
        Assert.Equal(SettingsService.WidgetTabStyleButton, restoredDefaults.TodoTabStyle);
        Assert.Equal(SettingsService.LayoutDensityStandard, restoredDefaults.LayoutDensity);
        Assert.True(restoredDefaults.QuickCaptureShowCreatedTime);
        Assert.Equal(newUserDefaults.QuickCaptureItemPreviewLineCount, restoredDefaults.QuickCaptureItemPreviewLineCount);
        Assert.Equal(SettingsService.DefaultQuickCaptureItemPreviewLineCount, newUserDefaults.QuickCaptureItemPreviewLineCount);
        Assert.Equal(newUserDefaults.QuickCaptureEditorEnterBehavior, restoredDefaults.QuickCaptureEditorEnterBehavior);
        Assert.True(restoredDefaults.ResizeSnapEnabled);
        Assert.False(restoredDefaults.TodoShowFooterStats);
        Assert.Equal(newUserDefaults.TodoItemPreviewLineCount, restoredDefaults.TodoItemPreviewLineCount);
        Assert.Equal(SettingsService.DefaultTodoItemPreviewLineCount, newUserDefaults.TodoItemPreviewLineCount);
        Assert.False(newUserDefaults.TodoShowCompletedTasks);
        Assert.Equal(newUserDefaults.TodoEditorEnterBehavior, restoredDefaults.TodoEditorEnterBehavior);
        Assert.Equal(SettingsService.LanguageChinese, restoredDefaults.Language);
        Assert.False(restoredDefaults.AutoStart);
        Assert.True(restoredDefaults.QuickCaptureEnabled);
        Assert.True(restoredDefaults.TodoEnabled);
        Assert.True(restoredDefaults.FeatureWidgetEnabledStates[WidgetKind.Music.ToString()]);
        Assert.Equal(newUserDefaults.ManagedDropAction, restoredDefaults.ManagedDropAction);
        Assert.Equal(newUserDefaults.GlobalHotkeyEnabled, restoredDefaults.GlobalHotkeyEnabled);
        Assert.Equal(newUserDefaults.GlobalHotkeyModifiers, restoredDefaults.GlobalHotkeyModifiers);
        Assert.Equal(newUserDefaults.GlobalHotkeyKey, restoredDefaults.GlobalHotkeyKey);
        Assert.Equal(newUserDefaults.ShowFileItemPathTooltips, restoredDefaults.ShowFileItemPathTooltips);
        Assert.Equal(SettingsService.DefaultWidgetHoverButtonActions, newUserDefaults.WidgetHoverButtonActions);
        Assert.Equal(newUserDefaults.WidgetHoverButtonActions, restoredDefaults.WidgetHoverButtonActions);
    }

    [Fact]
    public void ApplyDefaultPreferences_CoversEveryAppSettingAccordingToPreservationPolicy()
    {
        var defaults = new AppSettings();
        SettingsService.ApplyDefaultPreferences(defaults);
        var settings = new AppSettings();
        var changedValues = new Dictionary<string, string>(StringComparer.Ordinal);
        var properties = typeof(AppSettings).GetProperties()
            .Where(property => property.CanRead && property.CanWrite)
            .ToArray();

        Assert.All(
            SettingsService.DefaultPreferencePreservationPolicy.Keys,
            propertyName => Assert.Contains(properties, property => property.Name == propertyName));

        foreach (var property in properties)
        {
            object? changedValue = CreateNonDefaultSettingValue(
                property.PropertyType,
                property.GetValue(defaults));
            property.SetValue(settings, changedValue);
            changedValues[property.Name] = SerializeSettingValue(changedValue, property.PropertyType);
        }

        SettingsService.ApplyDefaultPreferences(settings);

        foreach (var property in properties)
        {
            string actual = SerializeSettingValue(property.GetValue(settings), property.PropertyType);
            if (SettingsService.DefaultPreferencePreservationPolicy.ContainsKey(property.Name))
            {
                Assert.True(
                    string.Equals(changedValues[property.Name], actual, StringComparison.Ordinal),
                    $"{property.Name} should be preserved by the default preference reset policy.");
            }
            else
            {
                string expected = SerializeSettingValue(
                    property.GetValue(defaults),
                    property.PropertyType);
                Assert.True(
                    string.Equals(expected, actual, StringComparison.Ordinal),
                    $"{property.Name} should reset to {expected}, but was {actual}.");
            }
        }
    }

    [Fact]
    public void ApplyDefaultPreferences_PreservesWidgetInstancesAndPerWidgetOverrides()
    {
        var widget = new WidgetConfig
        {
            Id = "widget",
            CompactWidth = 212,
            CompactPlacement = new WidgetCompactPlacement { X = 40, Y = 72 },
            Metadata = new Dictionary<string, string>
            {
                [WidgetChromeModeNames.MetadataKey] = "Hidden",
                [WidgetCollapseBehaviorNames.MetadataKey] = "Smart",
                [WidgetFileStackSettings.GroupByOverrideMetadataKey] =
                    SettingsService.FileStackGroupByCustom
            }
        };
        var settings = new AppSettings { Widgets = [widget] };

        SettingsService.ApplyDefaultPreferences(settings);

        WidgetConfig preserved = Assert.Single(settings.Widgets);
        Assert.Same(widget, preserved);
        Assert.Equal(212, preserved.CompactWidth);
        Assert.Equal(40, preserved.CompactPlacement?.X);
        Assert.Equal("Hidden", preserved.Metadata[WidgetChromeModeNames.MetadataKey]);
        Assert.Equal("Smart", preserved.Metadata[WidgetCollapseBehaviorNames.MetadataKey]);
        Assert.Equal(
            SettingsService.FileStackGroupByCustom,
            preserved.Metadata[WidgetFileStackSettings.GroupByOverrideMetadataKey]);
    }

    [Theory]
    [InlineData(SettingsService.LayoutDensityCompact)]
    [InlineData(SettingsService.LayoutDensityStandard)]
    [InlineData(SettingsService.LayoutDensityRelaxed)]
    public void LayoutDensityPreset_AppliesAndResolvesUnderlyingMetrics(string preset)
    {
        var settings = new AppSettings();

        SettingsService.ApplyLayoutDensityPreset(settings, preset);

        Assert.True(SettingsService.TryGetLayoutDensityPresetValues(preset, out LayoutDensityPresetValues expected));
        Assert.Equal(expected.IconSize, settings.IconSize);
        Assert.Equal(expected.TextSize, settings.TextSize);
        Assert.Equal(expected.DensityScale, settings.LayoutDensityScale);
        Assert.Equal(expected.HorizontalSpacingScale, settings.HorizontalSpacingScale);
        Assert.Equal(expected.VerticalSpacingScale, settings.VerticalSpacingScale);
        Assert.Equal(expected.FileNameWidthScale, settings.FileNameWidthScale);
        Assert.Equal(preset, SettingsService.ResolveLayoutDensityPreset(settings));
    }

    [Fact]
    public void ResolveLayoutDensityPreset_ReturnsCustomWhenOneMetricChanges()
    {
        var settings = new AppSettings();
        SettingsService.ApplyLayoutDensityPreset(settings, SettingsService.LayoutDensityStandard);
        settings.VerticalSpacingScale += 0.02;

        Assert.Equal(SettingsService.LayoutDensityCustom, SettingsService.ResolveLayoutDensityPreset(settings));
    }

    [Theory]
        [InlineData(null, "More")]
    [InlineData("", "More")]
    [InlineData("Unknown", "More")]
    [InlineData("add,More,delete,LockSize", "Add,More,Delete")]
    [InlineData("Add,Add,LockSize", "Add,LockSize")]
    public void NormalizeWidgetHoverButtonActions_ConstrainsSelection(string? value, string expected)
    {
        Assert.Equal(expected, SettingsService.NormalizeWidgetHoverButtonActions(value));
    }

    [Fact]
    public async Task LoadAsync_NormalizesQuickCaptureDefaultView()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            """
            {
              "quickCaptureDefaultView": "Timeline"
            }
            """);

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.Equal(SettingsService.QuickCaptureDefaultViewRecords, service.Settings.QuickCaptureDefaultView);
    }

    [Theory]
    [InlineData(null, SettingsService.AttachmentStorageModeLink)]
    [InlineData("", SettingsService.AttachmentStorageModeLink)]
    [InlineData("unknown", SettingsService.AttachmentStorageModeLink)]
    [InlineData("link", SettingsService.AttachmentStorageModeLink)]
    [InlineData("copy", SettingsService.AttachmentStorageModeCopy)]
    public void NormalizeAttachmentStorageMode_UsesLinkAsSafeDefault(
        string? value,
        string expected)
    {
        Assert.Equal(expected, SettingsService.NormalizeAttachmentStorageMode(value));
    }

    [Theory]
    [InlineData("DateAdded")]
    [InlineData("DateCreated")]
    public async Task LoadAsync_MigratesRemovedDateAddedStackGroupingToKind(string legacyValue)
    {
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            $$"""
            {
              "fileStackGroupBy": "{{legacyValue}}"
            }
            """);

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.Equal(SettingsService.FileStackGroupByKind, service.Settings.FileStackGroupBy);
    }

    [Theory]
    [InlineData(null, SettingsService.MusicDisplayModeAuto)]
    [InlineData("", SettingsService.MusicDisplayModeAuto)]
    [InlineData("Unknown", SettingsService.MusicDisplayModeAuto)]
    [InlineData(SettingsService.MusicDisplayModeAuto, SettingsService.MusicDisplayModeAuto)]
    [InlineData(SettingsService.MusicDisplayModeCover, SettingsService.MusicDisplayModeCover)]
    [InlineData(SettingsService.MusicDisplayModeControls, SettingsService.MusicDisplayModeControls)]
    public void NormalizeMusicDisplayMode_UsesAutoAsSafeDefault(string? value, string expected)
    {
        Assert.Equal(expected, SettingsService.NormalizeMusicDisplayMode(value));
    }

    [Fact]
    public async Task SaveAsync_PreservesExplicitCustomDensityWhenMetricsMatchPreset()
    {
        var service = new SettingsService(_settingsRoot);
        SettingsService.ApplyLayoutDensityPreset(service.Settings, SettingsService.LayoutDensityStandard);
        service.Settings.LayoutDensity = SettingsService.LayoutDensityCustom;

        await service.SaveAsync(notifySubscribers: false);

        Assert.Equal(SettingsService.LayoutDensityCustom, service.Settings.LayoutDensity);
    }

    [Fact]
    public async Task SaveAsync_SolidMaterialForcesFullOpacity()
    {
        var service = new SettingsService(_settingsRoot);
        service.Settings.WidgetMaterialType = SettingsService.WidgetMaterialTypeSolid;
        service.Settings.WidgetOpacity = 0.24;

        await service.SaveAsync(notifySubscribers: false);

        Assert.Equal(SettingsService.MaxWidgetOpacity, service.Settings.WidgetOpacity);
    }

    private static object? CreateNonDefaultSettingValue(Type type, object? defaultValue)
    {
        if (type == typeof(string))
        {
            return $"{defaultValue}-changed";
        }

        if (type == typeof(bool))
        {
            return !(bool)(defaultValue ?? false);
        }

        if (type == typeof(int))
        {
            return (int)(defaultValue ?? 0) + 17;
        }

        if (type == typeof(double))
        {
            return (double)(defaultValue ?? 0d) + 0.137;
        }

        if (type == typeof(DateTimeOffset?))
        {
            return new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var list = (System.Collections.IList)Activator.CreateInstance(type)!;
            Type itemType = type.GetGenericArguments()[0];
            list.Add(itemType == typeof(string)
                ? "changed"
                : Activator.CreateInstance(itemType)!);
            return list;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var dictionary = (System.Collections.IDictionary)Activator.CreateInstance(type)!;
            Type[] arguments = type.GetGenericArguments();
            object key = arguments[0] == typeof(string)
                ? "changed"
                : Activator.CreateInstance(arguments[0])!;
            object value = arguments[1] == typeof(bool)
                ? true
                : Activator.CreateInstance(arguments[1])!;
            dictionary.Add(key, value);
            return dictionary;
        }

        throw new NotSupportedException($"No AppSettings test value factory for {type}.");
    }

    private static string SerializeSettingValue(object? value, Type type)
    {
        return JsonSerializer.Serialize(value, type, s_jsonOptions);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
