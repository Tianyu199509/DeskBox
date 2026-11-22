using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json.Nodes;
using DeskBox.Controls.Studio;
using DeskBox.FileSafety;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Services.Studio;
using DeskBox.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DeskBox.Tests;

public sealed class CustomWidgetStudioArchitectureTests
{
    private const string SampleManifestV1 = """
        {
          "schemaVersion": 1,
          "id": "custom.arch-test",
          "name": "Arch Widget",
          "icon": "✨",
          "root": "root",
          "elements": {
            "root": {
              "type": "Stack",
              "spacing": 8,
              "children": ["title_text"]
            },
            "title_text": {
              "type": "Text",
              "text": "@i18n:title",
              "style": "bodyStrong"
            }
          }
        }
        """;

    private const string SampleManifestV2 = """
        {
          "schemaVersion": 1,
          "id": "custom.arch-test",
          "name": "Arch Widget V2",
          "icon": "🚀",
          "root": "root",
          "elements": {
            "root": {
              "type": "Stack",
              "spacing": 12,
              "children": ["subtitle_text"]
            },
            "subtitle_text": {
              "type": "Text",
              "text": "{{i18n.subtitle}}",
              "style": "title"
            }
          }
        }
        """;

    [Fact]
    public async Task InProcessStudioHostBridge_DelegatesStageMutationsAndLoggingCleanly()
    {
        string? committedStageId = null;
        string? discardedStageId = null;
        var logs = new List<string>();

        var bridge = new InProcessStudioHostBridge(
            getWidgetManager: static () => null,
            getNotificationService: static () => null,
            commitAgentStageAsync: stageId =>
            {
                committedStageId = stageId;
                return Task.FromResult("{\"committed\":true}");
            },
            discardAgentStageAsync: stageId =>
            {
                discardedStageId = stageId;
                return Task.CompletedTask;
            },
            log: msg => logs.Add(msg));

        Assert.False(bridge.HasWidgetManager);
        Assert.Empty(bridge.GetCustomWidgetInstances());

        string commitJson = await bridge.CommitAgentStageAsync("stage-101");
        Assert.Equal("{\"committed\":true}", commitJson);
        Assert.Equal("stage-101", committedStageId);

        await bridge.DiscardAgentStageAsync("stage-102");
        Assert.Equal("stage-102", discardedStageId);

        bridge.Log("hello-bridge");
        Assert.Equal(["hello-bridge"], logs);
    }

    [Fact]
    public void CustomWidgetStudioWindow_LifecycleIL_SubscribesOnceInCtorAndUnsubscribesAndDisposesInClosed()
    {
        Type windowType = typeof(CustomWidgetStudioWindow);
        ConstructorInfo? ctor = windowType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            [typeof(LocalizationService), typeof(IStudioHostBridge)],
            modifiers: null);
        Assert.NotNull(ctor);

        MethodInfo? closedMethod = windowType.GetMethod(
            "CustomWidgetStudioWindow_Closed",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(closedMethod);

        List<MethodBase> ctorCalls = ReadCalledMethods(ctor!);
        Assert.Equal(1, ctorCalls.Count(static m => m.Name == "InstallMinimumSizeHook"));
        Assert.Equal(1, ctorCalls.Count(static m => m.Name == "add_DisplayTopologyChanged"));
        Assert.Equal(1, ctorCalls.Count(static m => m.Name == "add_AppearanceChanged"));

        List<MethodBase> closedCalls = ReadCalledMethods(closedMethod!);
        Assert.Equal(1, closedCalls.Count(static m => m.Name == "remove_DisplayTopologyChanged"));
        Assert.Equal(1, closedCalls.Count(static m => m.Name == "remove_AppearanceChanged"));

        string[] expectedDisposedControllerNames =
        [
            nameof(StudioChannelOnboardingController),
            nameof(StudioLocaleCenterPanel),
            nameof(StudioStartGalleryView),
            nameof(StudioPreviewFrameThemeRenderer),
            nameof(StudioStageViewportController),
            nameof(StudioCoCreationChatPanel),
            nameof(StudioGenerationCoordinator),
            nameof(StudioCommitAndWorkspaceCoordinator),
        ];

        var disposedDeclaringTypes = closedCalls
            .Where(static m => m.Name == nameof(IDisposable.Dispose))
            .Select(static m => m.DeclaringType?.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string controllerName in expectedDisposedControllerNames)
        {
            Assert.Contains(controllerName, disposedDeclaringTypes);
        }
    }

    [Fact]
    public void CustomWidgetStudioWindow_AndStudioModules_HonorFieldCapAndGlobalIsolationBoundaries()
    {
        FieldInfo[] uiFields = typeof(CustomWidgetStudioWindow)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(static f =>
                typeof(UIElement).IsAssignableFrom(f.FieldType) ||
                typeof(FlyoutBase).IsAssignableFrom(f.FieldType))
            .ToArray();

        Assert.True(
            uiFields.Length <= 16,
            $"CustomWidgetStudioWindow must stay <= 16 UIElement/FlyoutBase fields, found {uiFields.Length}.");

        Assembly deskBoxAssembly = typeof(CustomWidgetStudioWindow).Assembly;
        Type[] studioTypes = deskBoxAssembly
            .GetTypes()
            .Where(static t =>
                t == typeof(CustomWidgetStudioWindow) ||
                string.Equals(t.Namespace, typeof(StudioSessionController).Namespace, StringComparison.Ordinal) ||
                string.Equals(t.Namespace, typeof(StudioUiPrimitives).Namespace, StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(studioTypes);

        var forbiddenGlobalGetters = new HashSet<string>(StringComparer.Ordinal)
        {
            "get_Current",
            "get_UiDispatcherQueue",
        };

        foreach (Type type in studioTypes)
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                Assert.NotEqual(typeof(IServiceProvider), field.FieldType);
                Assert.NotEqual(typeof(App), field.FieldType);
            }

            foreach (ConstructorInfo ctor in type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                foreach (ParameterInfo p in ctor.GetParameters())
                {
                    Assert.NotEqual(typeof(IServiceProvider), p.ParameterType);
                    Assert.NotEqual(typeof(App), p.ParameterType);
                }
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (method.GetMethodBody() is null)
                {
                    continue;
                }

                foreach (MethodBase called in ReadCalledMethods(method))
                {
                    bool hitsAppGlobal = called.DeclaringType == typeof(App) &&
                                         (forbiddenGlobalGetters.Contains(called.Name) ||
                                          called.Name is "Log" or "LogVerbose");
                    Assert.False(hitsAppGlobal, $"{type.Name}.{method.Name} calls forbidden App member {called.Name}.");
                }
            }
        }
    }

    [Fact]
    public void StudioSessionController_RevisionsUndoRedoTicksCapabilitiesAndReset_BehaveDeterministically()
    {
        using var tempRoot = new TempDirectoryScope();
        var settingsService = new SettingsService(Path.Combine(tempRoot.Path, "settings.json"));
        var loc = new LocalizationService(settingsService);
        var prefs = new CustomWidgetStudioPreferencesStore(Path.Combine(tempRoot.Path, "studio-prefs.json"));
        var sessionStore = new CustomWidgetStudioSessionStore(Path.Combine(tempRoot.Path, "studio-sessions"));

        var session = new StudioSessionController(
            loc,
            NullStudioHostBridge.Instance,
            prefs,
            sessionStore,
            hasSystemWallpaperAvailable: false,
            initialBackdropMode: StageBackdropMode.Dark);

        session.TargetWidgetId = "custom.arch-test";
        session.TargetWidgetName = "Arch Widget";

        StudioPersistedRevision rev1 = session.AddRevision(
            SampleManifestV1,
            "v1",
            isAiChange: false,
            source: "manual",
            isCommitted: false,
            markDirty: false);
        StudioPersistedRevision rev2 = session.AddRevision(
            SampleManifestV2,
            "v2",
            isAiChange: true,
            source: "ai",
            isCommitted: false,
            markDirty: false);

        Assert.Equal("v1", rev1.ChangeSummary);
        Assert.Equal("v2", rev2.ChangeSummary);
        Assert.Equal(2, session.Revisions.Count);
        Assert.Equal(1, session.RevisionIndex);
        Assert.True(session.CanUndo);
        Assert.False(session.CanRedo);

        Assert.True(session.SelectRevisionIndex(0));
        Assert.False(session.CanUndo);
        Assert.True(session.CanRedo);
        Assert.Equal(SampleManifestV1, session.CurrentManifestJson);

        session.ChatMessages.Add(new StudioChatMessage
        {
            Id = "m-v2",
            Role = "assistant",
            Kind = "ai_edit",
            Text = "updated",
            LinkedRevisionId = rev2.RevisionId,
        });
        session.SyncChatSupersededWithRevisionIndex();
        Assert.True(session.ChatMessages[0].IsSuperseded);

        long firstPreviousTick = session.TouchDraftWriteTick();
        Assert.Equal(0L, firstPreviousTick);
        long secondPreviousTick = session.TouchDraftWriteTick();
        Assert.True(secondPreviousTick > 0);
        Assert.InRange(session.ElapsedSinceLastDraftWriteMs(), 0, 5000);

        Assert.True(session.TogglePinnedCapability("weather"));
        Assert.True(session.IsCapabilityPinned("weather"));
        Assert.True(session.ClearPinnedCapabilities());
        Assert.False(session.IsCapabilityPinned("weather"));

        session.ResetState();
        Assert.Empty(session.Revisions);
        Assert.Empty(session.ChatMessages);
        Assert.Equal(-1, session.RevisionIndex);
        Assert.False(session.CanUndo);
        Assert.False(session.CanRedo);
    }

    [Fact]
    public void StudioManifestInspector_ParsingClassificationAndJsonPathHelpers_HandleEdgeCasesCleanly()
    {
        Assert.True(StudioManifestInspector.TryExtractPureI18nKey("${$i18n.card.title}", out string i18nKey));
        Assert.Equal("card.title", i18nKey);

        Assert.True(StudioManifestInspector.TryExtractPureI18nKey("  ${ $i18n.card.sub_title-1 }  ", out string trimmedKey));
        Assert.Equal("card.sub_title-1", trimmedKey);

        Assert.False(StudioManifestInspector.TryExtractPureI18nKey("Hello ${$i18n.title}", out _));
        Assert.False(StudioManifestInspector.TryExtractPureI18nKey("${$i18n.bad key}", out _));
        Assert.False(StudioManifestInspector.TryExtractPureI18nKey("@i18n:card.title", out _));

        var root = JsonNode.Parse(SampleManifestV1)!.AsObject();
        Assert.True(StudioManifestInspector.TryGetParentNodePath(root, "#title_text", out string parentPath));
        Assert.Equal("#root", parentPath);
        Assert.False(StudioManifestInspector.TryGetParentNodePath(root, "#root", out _));

        Assert.True(StudioManifestInspector.TryResolveJsonNodeByPath(root, "#title_text", out JsonObject? flatLeafNode));
        Assert.NotNull(flatLeafNode);
        Assert.Equal("Text", flatLeafNode!["type"]?.GetValue<string>());

        Assert.True(StudioManifestInspector.TryResolveJsonNodeByPath(root, "$.elements.title_text", out JsonObject? leafNode));
        Assert.NotNull(leafNode);
        Assert.Equal("Text", leafNode!["type"]?.GetValue<string>());

        Assert.Equal(
            "CustomWidget.Workshop.Direct.FailedAuth",
            StudioManifestInspector.ClassifyDirectErrorMessageKey("HTTP 401 Unauthorized"));

        (bool ok, bool committed, string? widgetId, string error) =
            StudioManifestInspector.ParseAgentStageResponse(
                """{"ok":true,"result":{"committed":true,"widgetId":"custom.arch-test"}}""");
        Assert.True(ok);
        Assert.True(committed);
        Assert.Equal("custom.arch-test", widgetId);
        Assert.Equal(string.Empty, error);

        Assert.Equal("Hello\u2026", StudioManifestInspector.TruncateTextElements("HelloWorld", 5));
        Assert.Equal("second line", StudioManifestInspector.ExtractLatestNonEmptySingleLine("first\n  second line  \n\n"));
    }

    [Fact]
    public void FluentMotionHelper_TranslationAndElevationMethods_UseUiElementFacadeAnimationsWithoutHandoffVisualMixing()
    {
        Type helperType = typeof(DeskBox.Helpers.FluentMotionHelper);
        Type previewType = typeof(Microsoft.UI.Xaml.Hosting.ElementCompositionPreview);
        Type compositionTargetType = typeof(Microsoft.UI.Xaml.Media.CompositionTarget);

        var allHelperTypes = new List<Type> { helperType };
        allHelperTypes.AddRange(helperType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));

        foreach (Type t in allHelperTypes)
        {
            foreach (MethodInfo method in t.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                foreach (MethodBase called in ReadCalledMethods(method))
                {
                    bool callsSetIsTranslationEnabled =
                        called.DeclaringType == previewType &&
                        string.Equals(called.Name, nameof(Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled), StringComparison.Ordinal);
                    Assert.False(
                        callsSetIsTranslationEnabled,
                        $"{t.Name}.{method.Name} must not call ElementCompositionPreview.SetIsTranslationEnabled.");
                }
            }
        }

        string[] facadeOnlyMethods =
        [
            nameof(DeskBox.Helpers.FluentMotionHelper.PlayEntranceSlideAndFade),
            nameof(DeskBox.Helpers.FluentMotionHelper.AnimateHoverScale),
            nameof(DeskBox.Helpers.FluentMotionHelper.AnimateStageFocus),
            nameof(DeskBox.Helpers.FluentMotionHelper.PlayConduitPulse),
            nameof(DeskBox.Helpers.FluentMotionHelper.AnimateCardElevation),
        ];

        foreach (string methodName in facadeOnlyMethods)
        {
            MethodInfo? method = helperType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(method);
            List<MethodBase> calls = ReadCalledMethods(method!);

            Assert.DoesNotContain(
                calls,
                c => c.DeclaringType == previewType &&
                     string.Equals(c.Name, nameof(Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual), StringComparison.Ordinal));
            Assert.Contains(
                calls,
                c => c.DeclaringType == compositionTargetType &&
                     string.Equals(c.Name, nameof(Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread), StringComparison.Ordinal));
            Assert.Contains(
                calls,
                c => c.DeclaringType == typeof(UIElement) &&
                     string.Equals(c.Name, nameof(UIElement.StartAnimation), StringComparison.Ordinal));
        }

        Type? exitAsyncStateMachine = helperType
            .GetNestedTypes(BindingFlags.NonPublic)
            .FirstOrDefault(static t => t.Name.Contains(nameof(DeskBox.Helpers.FluentMotionHelper.PlayExitSlideAndFadeAsync), StringComparison.Ordinal));
        Assert.NotNull(exitAsyncStateMachine);
        MethodInfo? moveNext = exitAsyncStateMachine!.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(moveNext);
        List<MethodBase> exitCalls = ReadCalledMethods(moveNext!);

        Assert.DoesNotContain(
            exitCalls,
            c => c.DeclaringType == previewType &&
                 string.Equals(c.Name, nameof(Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual), StringComparison.Ordinal));
        Assert.Contains(
            exitCalls,
            c => c.DeclaringType == compositionTargetType &&
                 string.Equals(c.Name, nameof(Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread), StringComparison.Ordinal));
        Assert.Contains(
            exitCalls,
            c => c.DeclaringType == typeof(UIElement) &&
                 string.Equals(c.Name, nameof(UIElement.StartAnimation), StringComparison.Ordinal));

        var onboardingTypes = new List<Type> { typeof(StudioChannelOnboardingController) };
        onboardingTypes.AddRange(typeof(StudioChannelOnboardingController).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
        foreach (Type t in onboardingTypes)
        {
            foreach (MethodInfo method in t.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                foreach (MethodBase called in ReadCalledMethods(method))
                {
                    Assert.NotEqual(previewType, called.DeclaringType);
                }
            }
        }
    }

    private static readonly Lazy<(Dictionary<short, OpCode> SingleByte, Dictionary<short, OpCode> TwoByte)> s_opCodeMaps =
        new(BuildOpCodeMaps);

    private static (Dictionary<short, OpCode> SingleByte, Dictionary<short, OpCode> TwoByte) BuildOpCodeMaps()
    {
        var single = new Dictionary<short, OpCode>();
        var two = new Dictionary<short, OpCode>();
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode op)
            {
                continue;
            }

            if (op.Size == 1)
            {
                single[op.Value] = op;
            }
            else if (op.Size == 2)
            {
                two[op.Value] = op;
            }
        }

        return (single, two);
    }

    private static List<MethodBase> ReadCalledMethods(MethodBase method)
    {
        MethodBody? body = method.GetMethodBody();
        if (body is null)
        {
            return [];
        }

        byte[] il = body.GetILAsByteArray() ?? [];
        Module module = method.Module;
        Type[] typeArgs = method.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : [];
        Type[] methodArgs = method is MethodInfo mi && mi.IsGenericMethod ? mi.GetGenericArguments() : [];
        (Dictionary<short, OpCode> singleMap, Dictionary<short, OpCode> twoMap) = s_opCodeMaps.Value;

        var calls = new List<MethodBase>();
        int pos = 0;
        while (pos < il.Length)
        {
            byte b = il[pos++];
            OpCode op;
            if (b == 0xFE)
            {
                if (pos >= il.Length)
                {
                    break;
                }

                short code = unchecked((short)(0xFE00 | il[pos++]));
                if (!twoMap.TryGetValue(code, out op))
                {
                    break;
                }
            }
            else if (!singleMap.TryGetValue(b, out op))
            {
                break;
            }

            switch (op.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    pos += 1;
                    break;
                case OperandType.InlineVar:
                    pos += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    pos += 8;
                    break;
                case OperandType.InlineSwitch:
                    if (pos + 4 > il.Length)
                    {
                        return calls;
                    }

                    int count = BitConverter.ToInt32(il, pos);
                    pos += 4 + Math.Max(0, count) * 4;
                    break;
                case OperandType.InlineMethod:
                    if (pos + 4 > il.Length)
                    {
                        return calls;
                    }

                    int token = BitConverter.ToInt32(il, pos);
                    pos += 4;
                    try
                    {
                        if (module.ResolveMethod(token, typeArgs, methodArgs) is { } resolved)
                        {
                            calls.Add(resolved);
                        }
                    }
                    catch
                    {
                    }
                    break;
                default:
                    pos += 4;
                    break;
            }
        }

        return calls;
    }

    private sealed class TempDirectoryScope : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("deskbox-studio-arch-").FullName;

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
