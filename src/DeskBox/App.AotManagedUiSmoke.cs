#if DESKBOX_NATIVE_AOT
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using DeskBox.Views;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskBox;

public partial class App
{
    private const string AotManagedUiSmokeEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_SMOKE";
    private const string AotManagedUiBasicReadOnlyScenario = "BasicReadOnly";
    private const string AotManagedUiDeepSettingsReadOnlyScenario = "DeepSettingsReadOnly";
    private const string AotManagedUiSettingsWidgetPersistenceScenario =
        "SettingsWidgetPersistenceRestart";
    private const string AotManagedUiQuickCapturePersistenceScenario =
        "QuickCapturePersistenceRestart";
    private const string AotManagedUiTodoPersistenceScenario =
        "TodoPersistenceRestart";
    private const string AotManagedUiTodoStepsPersistenceScenario =
        "TodoStepsPersistenceRestart";
    private const string AotManagedUiTodoAttachmentsPersistenceScenario =
        "TodoAttachmentsPersistenceRestart";
    private const string AotManagedUiGlancePersistenceScenario =
        "GlancePersistenceRestart";
    private const string AotManagedUiWeatherSettingsPersistenceScenario =
        "WeatherSettingsPersistenceRestart";
    private const string AotManagedUiWeatherSurfacePersistenceScenario =
        "WeatherSurfacePersistenceRestart";
    private const string AotManagedUiLocalFilePersistenceScenario =
        "LocalFileSurfacePersistenceRestart";
    private const string AotManagedUiRecycleBinScenario =
        "RecycleBinMenuPersistenceRestart";
    private const string AotManagedUiShellMoveScenario =
        "ShellMovePersistenceRestart";
    private const string AotManagedUiFilePropertiesScenario =
        "FilePropertiesReadOnly";
    private const string AotManagedUiPickerClipboardScenario =
        "PickerClipboardStorageItemsPersistenceRestart";
    private const string AotManagedUiNativeDropScenario =
        "NativeDropPersistenceRestart";
    private const string AotManagedUiPersistencePhaseEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_PERSISTENCE_PHASE";
    private const string AotManagedUiPersistenceMutatePhase = "Mutate";
    private const string AotManagedUiPersistenceVerifyRestorePhase = "VerifyRestore";
    private const string AotManagedUiPersistencePostflightPhase = "Postflight";
    private const string AotManagedUiQuickCapturePhaseEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_QUICK_CAPTURE_PHASE";
    private const string AotManagedUiQuickCaptureMutatePhase = "Mutate";
    private const string AotManagedUiQuickCaptureVerifyDeletePhase = "VerifyDelete";
    private const string AotManagedUiQuickCapturePostflightPhase = "Postflight";
    private const string AotManagedUiTodoPhaseEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_TODO_PHASE";
    private const string AotManagedUiTodoMutatePhase = "Mutate";
    private const string AotManagedUiTodoVerifyDeletePhase = "VerifyDelete";
    private const string AotManagedUiTodoPostflightPhase = "Postflight";
    private const string AotManagedUiTodoStepsPhaseEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_TODO_STEPS_PHASE";
    private const string AotManagedUiTodoStepsMutatePhase = "Mutate";
    private const string AotManagedUiTodoStepsVerifyDeletePhase = "VerifyDelete";
    private const string AotManagedUiTodoStepsPostflightPhase = "Postflight";
    private const string AotManagedUiTodoAttachmentsPhaseEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_TODO_ATTACHMENTS_PHASE";
    private const string AotManagedUiTodoAttachmentsMutatePhase = "Mutate";
    private const string AotManagedUiTodoAttachmentsVerifyDeletePhase = "VerifyDelete";
    private const string AotManagedUiTodoAttachmentsPostflightPhase = "Postflight";
    private const string AotManagedUiGlancePhaseEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_GLANCE_PHASE";
    private const string AotManagedUiGlanceFixtureEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_GLANCE_FIXTURE";
    private const string AotManagedUiGlanceMutatePhase = "Mutate";
    private const string AotManagedUiGlanceVerifyRestorePhase = "VerifyRestore";
    private const string AotManagedUiGlancePostflightPhase = "Postflight";
    private const string AotManagedUiWeatherSettingsPhaseEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_WEATHER_SETTINGS_PHASE";
    private const string AotManagedUiWeatherSettingsMutatePhase = "Mutate";
    private const string AotManagedUiWeatherSettingsVerifyRestorePhase =
        "VerifyRestore";
    private const string AotManagedUiWeatherSettingsPostflightPhase = "Postflight";
    private const string AotManagedUiWeatherSurfacePhaseEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_WEATHER_SURFACE_PHASE";
    private const string AotManagedUiWeatherSurfaceMutatePhase = "Mutate";
    private const string AotManagedUiWeatherSurfaceVerifyRestorePhase =
        "VerifyRestore";
    private const string AotManagedUiWeatherSurfacePostflightPhase = "Postflight";
    private const string AotManagedUiLocalFilePhaseEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_LOCAL_FILE_PHASE";
    private const string AotManagedUiLocalFileMutatePhase = "Mutate";
    private const string AotManagedUiLocalFileVerifyRestorePhase = "VerifyRestore";
    private const string AotManagedUiLocalFilePostflightPhase = "Postflight";
    private const string AotManagedUiRecycleBinPhaseEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_RECYCLE_BIN_PHASE";
    private const string AotManagedUiRecycleBinMutatePhase = "Mutate";
    private const string AotManagedUiRecycleBinVerifyRestorePhase = "VerifyRestore";
    private const string AotManagedUiRecycleBinPostflightPhase = "Postflight";
    private const string AotManagedUiRecycleBinCompensatePhase = "Compensate";
    private const string AotManagedUiShellMovePhaseEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_SHELL_MOVE_PHASE";
    private const string AotManagedUiShellMoveMutatePhase = "Mutate";
    private const string AotManagedUiShellMoveVerifyRestorePhase = "VerifyRestore";
    private const string AotManagedUiShellMovePostflightPhase = "Postflight";
    private const string AotManagedUiShellMoveCompensatePhase = "Compensate";
    private const string AotManagedUiPickerClipboardPhaseEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_PICKER_CLIPBOARD_PHASE";
    private const string AotManagedUiPickerClipboardMutatePhase = "Mutate";
    private const string AotManagedUiPickerClipboardVerifyRestorePhase =
        "VerifyRestore";
    private const string AotManagedUiPickerClipboardPostflightPhase =
        "Postflight";
    private const string AotManagedUiNativeDropPhaseEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_NATIVE_DROP_PHASE";
    private const string AotManagedUiNativeDropMutatePhase = "Mutate";
    private const string AotManagedUiNativeDropVerifyRestorePhase =
        "VerifyRestore";
    private const string AotManagedUiNativeDropPostflightPhase =
        "Postflight";
    private const string AotManagedUiSmokeDirectoryName = "aot-managed-ui-smoke";
    private const string AotManagedUiBasicReadOnlyDirectoryName = "basic-read-only";
    private const string AotManagedUiDeepSettingsReadOnlyDirectoryName =
        "deep-settings-read-only";
    private const string AotManagedUiSettingsWidgetPersistenceDirectoryName =
        "settings-widget-persistence-restart";
    private const string AotManagedUiQuickCapturePersistenceDirectoryName =
        "quick-capture-persistence-restart";
    private const string AotManagedUiTodoPersistenceDirectoryName =
        "todo-persistence-restart";
    private const string AotManagedUiTodoStepsPersistenceDirectoryName =
        "todo-steps-persistence-restart";
    private const string AotManagedUiTodoAttachmentsPersistenceDirectoryName =
        "todo-attachments-persistence-restart";
    private const string AotManagedUiGlancePersistenceDirectoryName =
        "glance-persistence-restart";
    private const string AotManagedUiWeatherSettingsPersistenceDirectoryName =
        "weather-settings-persistence-restart";
    private const string AotManagedUiWeatherSurfacePersistenceDirectoryName =
        "weather-surface-persistence-restart";
    private const string AotManagedUiLocalFilePersistenceDirectoryName =
        "local-file-surface-persistence-restart";
    private const string AotManagedUiRecycleBinDirectoryName =
        "recycle-bin-menu-persistence-restart";
    private const string AotManagedUiShellMoveDirectoryName =
        "shell-move-persistence-restart";
    private const string AotManagedUiFilePropertiesDirectoryName =
        "file-properties-read-only";
    private const string AotManagedUiPickerClipboardDirectoryName =
        "picker-clipboard-storage-items-persistence-restart";
    private const string AotManagedUiNativeDropDirectoryName =
        "native-drop-persistence-restart";
    private const string AotManagedUiFileWidgetId = "aot-5b4a-file";
    private const string AotManagedUiSearchWidgetId = "aot-5b4a-search";
    private const string AotManagedUiQuickCaptureWidgetId =
        "aot-5b4b2b1-quick-capture";
    private const string AotManagedUiTodoWidgetId = "aot-5b4b2b2a-todo";
    private const string AotManagedUiTodoStepsWidgetId =
        "aot-5b4b2b2b1-todo-steps";
    private const string AotManagedUiTodoAttachmentsWidgetId =
        "aot-5b4b2b2b2-todo-attachments";
    private const string AotManagedUiGlanceWidgetId =
        "aot-5b4b2c1-glance";
    private const string AotManagedUiWeatherSettingsWidgetId =
        "aot-5b4b2c2a-weather";
    private const string AotManagedUiWeatherSurfaceWidgetId =
        "aot-5b4b2c2b-weather";
    private const string AotManagedUiLocalFileWidgetId = "aot-5b4c1a-file";
    private const string AotManagedUiRecycleBinWidgetId = "aot-5b4c1b1-file";
    private const string AotManagedUiShellMoveWidgetId = "aot-5b4c1b2a-file";
    private const string AotManagedUiFilePropertiesWidgetId =
        "aot-5b4c1b2b-file";
    private const string AotManagedUiPickerClipboardWidgetId =
        "aot-5b4c1c1-file";
    private const string AotManagedUiNativeDropWidgetId =
        "aot-5b4c1c2a-file";
    private const string AotManagedUiBaselineFileWidgetName = "AOT File Fixture";
    private const string AotManagedUiMutatedFileWidgetName =
        "AOT File Persistence Mutated";
    private const string AotManagedUiBaselineTrayIconStyle = "Colorful";
    private const string AotManagedUiMutatedTrayIconStyle = "White";
    private const double AotManagedUiBaselineTextSize = 11.5;
    private const double AotManagedUiMutatedTextSize = 12.5;

    private void StartAotManagedUiSmokeIfRequested()
    {
        string? scenario = Environment.GetEnvironmentVariable(
            AotManagedUiSmokeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(scenario))
        {
            return;
        }

        if (scenario is not AotManagedUiBasicReadOnlyScenario and
            not AotManagedUiDeepSettingsReadOnlyScenario and
            not AotManagedUiSettingsWidgetPersistenceScenario and
            not AotManagedUiQuickCapturePersistenceScenario and
            not AotManagedUiTodoPersistenceScenario and
            not AotManagedUiTodoStepsPersistenceScenario and
            not AotManagedUiTodoAttachmentsPersistenceScenario and
            not AotManagedUiGlancePersistenceScenario and
            not AotManagedUiWeatherSettingsPersistenceScenario and
            not AotManagedUiWeatherSurfacePersistenceScenario and
            not AotManagedUiLocalFilePersistenceScenario and
            not AotManagedUiRecycleBinScenario and
            not AotManagedUiShellMoveScenario and
            not AotManagedUiFilePropertiesScenario and
            not AotManagedUiPickerClipboardScenario and
            not AotManagedUiNativeDropScenario)
        {
            Log($"[AotManagedUiSmoke] Refused unsupported scenario '{scenario}'.");
            return;
        }

        _ = RunAotManagedUiSmokeAsync(scenario);
    }

    private async Task RunAotManagedUiSmokeAsync(string scenario)
    {
        await Task.Yield();

        DeskBoxDataPathService dataPaths = DeskBoxDataPathService.Current;
        string? configuredPreviewRoot = Environment.GetEnvironmentVariable(
            DeskBoxDataPathService.AotPreviewRootEnvironmentVariable);
        if (!dataPaths.IsDevelopmentRoot ||
            string.IsNullOrWhiteSpace(configuredPreviewRoot) ||
            !IsAotManagedUiPathEqual(dataPaths.RootPath, configuredPreviewRoot))
        {
            Log(
                "[AotManagedUiSmoke] RefusedNonPreviewRoot: the managed UI matrix " +
                "requires an explicit isolated Native AOT preview root.");
            return;
        }

        string? persistencePhase = null;
        if (scenario == AotManagedUiSettingsWidgetPersistenceScenario)
        {
            persistencePhase = Environment.GetEnvironmentVariable(
                AotManagedUiPersistencePhaseEnvironmentVariable);
            if (persistencePhase is not AotManagedUiPersistenceMutatePhase and
                not AotManagedUiPersistenceVerifyRestorePhase and
                not AotManagedUiPersistencePostflightPhase)
            {
                Log(
                    $"[AotManagedUiSmoke] Refused unsupported persistence phase " +
                    $"'{persistencePhase}'.");
                return;
            }
        }

        string? quickCapturePersistencePhase = null;
        if (scenario == AotManagedUiQuickCapturePersistenceScenario)
        {
            quickCapturePersistencePhase = Environment.GetEnvironmentVariable(
                AotManagedUiQuickCapturePhaseEnvironmentVariable);
            if (quickCapturePersistencePhase is not AotManagedUiQuickCaptureMutatePhase and
                not AotManagedUiQuickCaptureVerifyDeletePhase and
                not AotManagedUiQuickCapturePostflightPhase)
            {
                Log(
                    $"[AotManagedUiSmoke] Refused unsupported Quick Capture " +
                    $"persistence phase '{quickCapturePersistencePhase}'.");
                return;
            }
        }

        string? todoPersistencePhase = null;
        if (scenario == AotManagedUiTodoPersistenceScenario)
        {
            todoPersistencePhase = Environment.GetEnvironmentVariable(
                AotManagedUiTodoPhaseEnvironmentVariable);
            if (todoPersistencePhase is not AotManagedUiTodoMutatePhase and
                not AotManagedUiTodoVerifyDeletePhase and
                not AotManagedUiTodoPostflightPhase)
            {
                Log(
                    $"[AotManagedUiSmoke] Refused unsupported Todo " +
                    $"persistence phase '{todoPersistencePhase}'.");
                return;
            }
        }

        string? todoStepsPersistencePhase = null;
        if (scenario == AotManagedUiTodoStepsPersistenceScenario)
        {
            todoStepsPersistencePhase = Environment.GetEnvironmentVariable(
                AotManagedUiTodoStepsPhaseEnvironmentVariable);
            if (todoStepsPersistencePhase is not AotManagedUiTodoStepsMutatePhase and
                not AotManagedUiTodoStepsVerifyDeletePhase and
                not AotManagedUiTodoStepsPostflightPhase)
            {
                Log(
                    $"[AotManagedUiSmoke] Refused unsupported Todo steps " +
                    $"persistence phase '{todoStepsPersistencePhase}'.");
                return;
            }
        }

        string? todoAttachmentsPersistencePhase = null;
        if (scenario == AotManagedUiTodoAttachmentsPersistenceScenario)
        {
            todoAttachmentsPersistencePhase = Environment.GetEnvironmentVariable(
                AotManagedUiTodoAttachmentsPhaseEnvironmentVariable);
            if (todoAttachmentsPersistencePhase is not AotManagedUiTodoAttachmentsMutatePhase and
                not AotManagedUiTodoAttachmentsVerifyDeletePhase and
                not AotManagedUiTodoAttachmentsPostflightPhase)
            {
                Log(
                    $"[AotManagedUiSmoke] Refused unsupported Todo attachments " +
                    $"persistence phase '{todoAttachmentsPersistencePhase}'.");
                return;
            }
        }

        string? glancePersistencePhase = null;
        if (scenario == AotManagedUiGlancePersistenceScenario)
        {
            glancePersistencePhase = Environment.GetEnvironmentVariable(
                AotManagedUiGlancePhaseEnvironmentVariable);
            if (glancePersistencePhase is not AotManagedUiGlanceMutatePhase and
                not AotManagedUiGlanceVerifyRestorePhase and
                not AotManagedUiGlancePostflightPhase)
            {
                Log(
                    $"[AotManagedUiSmoke] Refused unsupported Glance " +
                    $"persistence phase '{glancePersistencePhase}'.");
                return;
            }
        }

        string? weatherSettingsPersistencePhase = null;
        if (scenario == AotManagedUiWeatherSettingsPersistenceScenario)
        {
            weatherSettingsPersistencePhase = Environment.GetEnvironmentVariable(
                AotManagedUiWeatherSettingsPhaseEnvironmentVariable);
            if (weatherSettingsPersistencePhase is not
                    AotManagedUiWeatherSettingsMutatePhase and
                not AotManagedUiWeatherSettingsVerifyRestorePhase and
                not AotManagedUiWeatherSettingsPostflightPhase)
            {
                Log(
                    $"[AotManagedUiSmoke] Refused unsupported Weather settings " +
                    $"persistence phase '{weatherSettingsPersistencePhase}'.");
                return;
            }
        }

        string? weatherSurfacePersistencePhase = null;
        if (scenario == AotManagedUiWeatherSurfacePersistenceScenario)
        {
            weatherSurfacePersistencePhase = Environment.GetEnvironmentVariable(
                AotManagedUiWeatherSurfacePhaseEnvironmentVariable);
            if (weatherSurfacePersistencePhase is not
                    AotManagedUiWeatherSurfaceMutatePhase and
                not AotManagedUiWeatherSurfaceVerifyRestorePhase and
                not AotManagedUiWeatherSurfacePostflightPhase)
            {
                Log(
                    $"[AotManagedUiSmoke] Refused unsupported Weather surface " +
                    $"persistence phase '{weatherSurfacePersistencePhase}'.");
                return;
            }
        }

        string? localFilePersistencePhase = null;
        if (scenario == AotManagedUiLocalFilePersistenceScenario)
        {
            localFilePersistencePhase = Environment.GetEnvironmentVariable(
                AotManagedUiLocalFilePhaseEnvironmentVariable);
            if (localFilePersistencePhase is not AotManagedUiLocalFileMutatePhase and
                not AotManagedUiLocalFileVerifyRestorePhase and
                not AotManagedUiLocalFilePostflightPhase)
            {
                Log(
                    $"[AotManagedUiSmoke] Refused unsupported local-file " +
                    $"persistence phase '{localFilePersistencePhase}'.");
                return;
            }
        }

        string? recycleBinPhase = null;
        if (scenario == AotManagedUiRecycleBinScenario)
        {
            recycleBinPhase = Environment.GetEnvironmentVariable(
                AotManagedUiRecycleBinPhaseEnvironmentVariable);
            if (recycleBinPhase is not AotManagedUiRecycleBinMutatePhase and
                not AotManagedUiRecycleBinVerifyRestorePhase and
                not AotManagedUiRecycleBinPostflightPhase and
                not AotManagedUiRecycleBinCompensatePhase)
            {
                Log(
                    $"[AotManagedUiSmoke] Refused unsupported Recycle Bin " +
                    $"phase '{recycleBinPhase}'.");
                return;
            }
        }

        string? shellMovePhase = null;
        if (scenario == AotManagedUiShellMoveScenario)
        {
            shellMovePhase = Environment.GetEnvironmentVariable(
                AotManagedUiShellMovePhaseEnvironmentVariable);
            if (shellMovePhase is not AotManagedUiShellMoveMutatePhase and
                not AotManagedUiShellMoveVerifyRestorePhase and
                not AotManagedUiShellMovePostflightPhase and
                not AotManagedUiShellMoveCompensatePhase)
            {
                Log(
                    $"[AotManagedUiSmoke] Refused unsupported Shell move " +
                    $"phase '{shellMovePhase}'.");
                return;
            }
        }

        string? pickerClipboardPhase = null;
        if (scenario == AotManagedUiPickerClipboardScenario)
        {
            pickerClipboardPhase = Environment.GetEnvironmentVariable(
                AotManagedUiPickerClipboardPhaseEnvironmentVariable);
            if (pickerClipboardPhase is not
                    AotManagedUiPickerClipboardMutatePhase and
                not AotManagedUiPickerClipboardVerifyRestorePhase and
                not AotManagedUiPickerClipboardPostflightPhase)
            {
                Log(
                    $"[AotManagedUiSmoke] Refused unsupported picker/" +
                    $"StorageItems phase '{pickerClipboardPhase}'.");
                return;
            }
        }

        string? nativeDropPhase = null;
        if (scenario == AotManagedUiNativeDropScenario)
        {
            nativeDropPhase = Environment.GetEnvironmentVariable(
                AotManagedUiNativeDropPhaseEnvironmentVariable);
            if (nativeDropPhase is not AotManagedUiNativeDropMutatePhase and
                not AotManagedUiNativeDropVerifyRestorePhase and
                not AotManagedUiNativeDropPostflightPhase)
            {
                Log(
                    $"[AotManagedUiSmoke] Refused unsupported native-drop " +
                    $"phase '{nativeDropPhase}'.");
                return;
            }
        }

        string scenarioDirectoryName = scenario switch
        {
            AotManagedUiBasicReadOnlyScenario =>
                AotManagedUiBasicReadOnlyDirectoryName,
            AotManagedUiDeepSettingsReadOnlyScenario =>
                AotManagedUiDeepSettingsReadOnlyDirectoryName,
            AotManagedUiSettingsWidgetPersistenceScenario =>
                AotManagedUiSettingsWidgetPersistenceDirectoryName,
            AotManagedUiQuickCapturePersistenceScenario =>
                AotManagedUiQuickCapturePersistenceDirectoryName,
            AotManagedUiTodoPersistenceScenario =>
                AotManagedUiTodoPersistenceDirectoryName,
            AotManagedUiTodoStepsPersistenceScenario =>
                AotManagedUiTodoStepsPersistenceDirectoryName,
            AotManagedUiTodoAttachmentsPersistenceScenario =>
                AotManagedUiTodoAttachmentsPersistenceDirectoryName,
            AotManagedUiWeatherSettingsPersistenceScenario =>
                AotManagedUiWeatherSettingsPersistenceDirectoryName,
            AotManagedUiWeatherSurfacePersistenceScenario =>
                AotManagedUiWeatherSurfacePersistenceDirectoryName,
            AotManagedUiLocalFilePersistenceScenario =>
                AotManagedUiLocalFilePersistenceDirectoryName,
            AotManagedUiRecycleBinScenario =>
                AotManagedUiRecycleBinDirectoryName,
            AotManagedUiShellMoveScenario =>
                AotManagedUiShellMoveDirectoryName,
            AotManagedUiFilePropertiesScenario =>
                AotManagedUiFilePropertiesDirectoryName,
            AotManagedUiPickerClipboardScenario =>
                AotManagedUiPickerClipboardDirectoryName,
            AotManagedUiNativeDropScenario =>
                AotManagedUiNativeDropDirectoryName,
            _ => AotManagedUiGlancePersistenceDirectoryName
        };
        string evidenceRoot = Path.GetFullPath(Path.Combine(
            dataPaths.RootPath,
            AotManagedUiSmokeDirectoryName,
            scenarioDirectoryName));
        if (persistencePhase is not null)
        {
            string phaseDirectoryName = persistencePhase switch
            {
                AotManagedUiPersistenceMutatePhase => "mutate",
                AotManagedUiPersistenceVerifyRestorePhase => "verify-restore",
                _ => "postflight"
            };
            evidenceRoot = Path.GetFullPath(Path.Combine(
                evidenceRoot,
                phaseDirectoryName));
        }
        else if (quickCapturePersistencePhase is not null)
        {
            string phaseDirectoryName = quickCapturePersistencePhase switch
            {
                AotManagedUiQuickCaptureMutatePhase => "mutate",
                AotManagedUiQuickCaptureVerifyDeletePhase => "verify-delete",
                _ => "postflight"
            };
            evidenceRoot = Path.GetFullPath(Path.Combine(
                evidenceRoot,
                phaseDirectoryName));
        }
        else if (todoPersistencePhase is not null)
        {
            string phaseDirectoryName = todoPersistencePhase switch
            {
                AotManagedUiTodoMutatePhase => "mutate",
                AotManagedUiTodoVerifyDeletePhase => "verify-delete",
                _ => "postflight"
            };
            evidenceRoot = Path.GetFullPath(Path.Combine(
                evidenceRoot,
                phaseDirectoryName));
        }
        else if (todoStepsPersistencePhase is not null)
        {
            string phaseDirectoryName = todoStepsPersistencePhase switch
            {
                AotManagedUiTodoStepsMutatePhase => "mutate",
                AotManagedUiTodoStepsVerifyDeletePhase => "verify-delete",
                _ => "postflight"
            };
            evidenceRoot = Path.GetFullPath(Path.Combine(
                evidenceRoot,
                phaseDirectoryName));
        }
        else if (todoAttachmentsPersistencePhase is not null)
        {
            string phaseDirectoryName = todoAttachmentsPersistencePhase switch
            {
                AotManagedUiTodoAttachmentsMutatePhase => "mutate",
                AotManagedUiTodoAttachmentsVerifyDeletePhase => "verify-delete",
                _ => "postflight"
            };
            evidenceRoot = Path.GetFullPath(Path.Combine(
                evidenceRoot,
                phaseDirectoryName));
        }
        else if (glancePersistencePhase is not null)
        {
            string phaseDirectoryName = glancePersistencePhase switch
            {
                AotManagedUiGlanceMutatePhase => "mutate",
                AotManagedUiGlanceVerifyRestorePhase => "verify-restore",
                _ => "postflight"
            };
            evidenceRoot = Path.GetFullPath(Path.Combine(
                evidenceRoot,
                phaseDirectoryName));
        }
        else if (weatherSettingsPersistencePhase is not null)
        {
            string phaseDirectoryName = weatherSettingsPersistencePhase switch
            {
                AotManagedUiWeatherSettingsMutatePhase => "mutate",
                AotManagedUiWeatherSettingsVerifyRestorePhase => "verify-restore",
                _ => "postflight"
            };
            evidenceRoot = Path.GetFullPath(Path.Combine(
                evidenceRoot,
                phaseDirectoryName));
        }
        else if (weatherSurfacePersistencePhase is not null)
        {
            string phaseDirectoryName = weatherSurfacePersistencePhase switch
            {
                AotManagedUiWeatherSurfaceMutatePhase => "mutate",
                AotManagedUiWeatherSurfaceVerifyRestorePhase => "verify-restore",
                _ => "postflight"
            };
            evidenceRoot = Path.GetFullPath(Path.Combine(
                evidenceRoot,
                phaseDirectoryName));
        }
        else if (localFilePersistencePhase is not null)
        {
            string phaseDirectoryName = localFilePersistencePhase switch
            {
                AotManagedUiLocalFileMutatePhase => "mutate",
                AotManagedUiLocalFileVerifyRestorePhase => "verify-restore",
                _ => "postflight"
            };
            evidenceRoot = Path.GetFullPath(Path.Combine(
                evidenceRoot,
                phaseDirectoryName));
        }
        else if (recycleBinPhase is not null)
        {
            string phaseDirectoryName = recycleBinPhase switch
            {
                AotManagedUiRecycleBinMutatePhase => "mutate",
                AotManagedUiRecycleBinVerifyRestorePhase => "verify-restore",
                AotManagedUiRecycleBinCompensatePhase => "compensate",
                _ => "postflight"
            };
            evidenceRoot = Path.GetFullPath(Path.Combine(
                evidenceRoot,
                phaseDirectoryName));
        }
        else if (shellMovePhase is not null)
        {
            string phaseDirectoryName = shellMovePhase switch
            {
                AotManagedUiShellMoveMutatePhase => "mutate",
                AotManagedUiShellMoveVerifyRestorePhase => "verify-restore",
                AotManagedUiShellMoveCompensatePhase => "compensate",
                _ => "postflight"
            };
            evidenceRoot = Path.GetFullPath(Path.Combine(
                evidenceRoot,
                phaseDirectoryName));
        }
        else if (pickerClipboardPhase is not null)
        {
            string phaseDirectoryName = pickerClipboardPhase switch
            {
                AotManagedUiPickerClipboardMutatePhase => "mutate",
                AotManagedUiPickerClipboardVerifyRestorePhase =>
                    "verify-restore",
                _ => "postflight"
            };
            evidenceRoot = Path.GetFullPath(Path.Combine(
                evidenceRoot,
                phaseDirectoryName));
        }
        else if (nativeDropPhase is not null)
        {
            string phaseDirectoryName = nativeDropPhase switch
            {
                AotManagedUiNativeDropMutatePhase => "mutate",
                AotManagedUiNativeDropVerifyRestorePhase => "verify-restore",
                _ => "postflight"
            };
            evidenceRoot = Path.GetFullPath(Path.Combine(
                evidenceRoot,
                phaseDirectoryName));
        }
        if (!IsAotManagedUiPathEqualOrInside(dataPaths.RootPath, evidenceRoot) ||
            IsAotManagedUiPathEqual(dataPaths.RootPath, evidenceRoot))
        {
            Log($"[AotManagedUiSmoke] Refused unsafe evidence root '{evidenceRoot}'.");
            return;
        }

        Directory.CreateDirectory(evidenceRoot);
        string resultPath = Path.Combine(evidenceRoot, "result.json");
        var result = new AotManagedUiSmokeResult
        {
            SchemaVersion = 1,
            Scenario = scenario,
            State = "Running",
            StartedAtUtc = DateTimeOffset.UtcNow,
            ProcessId = Environment.ProcessId,
            ExecutablePath = Environment.ProcessPath,
            PreviewDataRoot = dataPaths.RootPath,
            EvidenceRoot = evidenceRoot,
            ResultPath = resultPath,
            IsDynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported,
            Persistence = persistencePhase is null
                ? null
                : new AotManagedUiPersistenceEvidence
                {
                    Phase = persistencePhase,
                    NormalShutdownRequested = true
                },
            QuickCapturePersistence = quickCapturePersistencePhase is null
                ? null
                : new AotManagedUiQuickCapturePersistenceEvidence
                {
                    Phase = quickCapturePersistencePhase,
                    NormalShutdownRequested = true
                },
            TodoPersistence = todoPersistencePhase is null
                ? null
                : new AotManagedUiTodoPersistenceEvidence
                {
                    Phase = todoPersistencePhase,
                    NormalShutdownRequested = true
                },
            TodoStepsPersistence = todoStepsPersistencePhase is null
                ? null
                : new AotManagedUiTodoStepsPersistenceEvidence
                {
                    Phase = todoStepsPersistencePhase,
                    NormalShutdownRequested = true
                },
            TodoAttachmentsPersistence = todoAttachmentsPersistencePhase is null
                ? null
                : new AotManagedUiTodoAttachmentsPersistenceEvidence
                {
                    Phase = todoAttachmentsPersistencePhase,
                    NormalShutdownRequested = true
                },
            GlancePersistence = glancePersistencePhase is null
                ? null
                : new AotManagedUiGlancePersistenceEvidence
                {
                    Phase = glancePersistencePhase,
                    NormalShutdownRequested = true
                },
            WeatherSettingsPersistence = weatherSettingsPersistencePhase is null
                ? null
                : new AotManagedUiWeatherSettingsPersistenceEvidence
                {
                    Phase = weatherSettingsPersistencePhase,
                    NormalShutdownRequested = true
                },
            WeatherSurfacePersistence = weatherSurfacePersistencePhase is null
                ? null
                : new AotManagedUiWeatherSurfacePersistenceEvidence
                {
                    Phase = weatherSurfacePersistencePhase,
                    NormalShutdownRequested = true
                },
            LocalFilePersistence = localFilePersistencePhase is null
                ? null
                : new AotManagedUiLocalFilePersistenceEvidence
                {
                    Phase = localFilePersistencePhase,
                    NormalShutdownRequested = true
                },
            RecycleBin = recycleBinPhase is null
                ? null
                : new AotManagedUiRecycleBinEvidence
                {
                    Phase = recycleBinPhase,
                    NormalShutdownRequested = true
                },
            ShellMove = shellMovePhase is null
                ? null
                : new AotManagedUiShellMoveEvidence
                {
                    Phase = shellMovePhase,
                    NormalShutdownRequested = true
                },
            FileProperties = scenario == AotManagedUiFilePropertiesScenario
                ? new AotManagedUiFilePropertiesEvidence
                {
                    NormalShutdownRequested = true
                }
                : null,
            PickerClipboard = pickerClipboardPhase is null
                ? null
                : new AotManagedUiPickerClipboardEvidence
                {
                    Phase = pickerClipboardPhase,
                    NormalShutdownRequested = true
                },
            NativeDrop = nativeDropPhase is null
                ? null
                : new AotManagedUiNativeDropEvidence
                {
                    Phase = nativeDropPhase,
                    NormalShutdownRequested = true
                }
        };
        WriteAotManagedUiResult(resultPath, result);

        try
        {
            RequireAotManagedUi(
                result,
                !result.IsDynamicCodeSupported,
                "NativeAotRuntime",
                "The managed UI matrix did not run inside a Native AOT process.");

            await CaptureAotManagedUiTrayAndWidgetsAsync(result);
            CaptureAotManagedUiLocales(result);
            if (scenario is AotManagedUiBasicReadOnlyScenario)
            {
                await CaptureAotManagedUiSettingsAsync(result);
                await CaptureAotManagedUiSearchAsync(result);
            }
            else if (scenario == AotManagedUiDeepSettingsReadOnlyScenario)
            {
                await CaptureAotManagedUiDeepSettingsAsync(result);
            }
            else if (scenario == AotManagedUiSettingsWidgetPersistenceScenario)
            {
                await CaptureAotManagedUiPersistenceAsync(
                    result,
                    persistencePhase!);
            }
            else if (scenario == AotManagedUiQuickCapturePersistenceScenario)
            {
                await CaptureAotManagedUiQuickCapturePersistenceAsync(
                    result,
                    quickCapturePersistencePhase!);
            }
            else if (scenario == AotManagedUiTodoPersistenceScenario)
            {
                await CaptureAotManagedUiTodoPersistenceAsync(
                    result,
                    todoPersistencePhase!);
            }
            else if (scenario == AotManagedUiTodoStepsPersistenceScenario)
            {
                await CaptureAotManagedUiTodoStepsPersistenceAsync(
                    result,
                    todoStepsPersistencePhase!);
            }
            else if (scenario == AotManagedUiTodoAttachmentsPersistenceScenario)
            {
                await CaptureAotManagedUiTodoAttachmentsPersistenceAsync(
                    result,
                    todoAttachmentsPersistencePhase!);
            }
            else if (scenario == AotManagedUiWeatherSettingsPersistenceScenario)
            {
                await CaptureAotManagedUiWeatherSettingsPersistenceAsync(
                    result,
                    weatherSettingsPersistencePhase!);
            }
            else if (scenario == AotManagedUiWeatherSurfacePersistenceScenario)
            {
                await CaptureAotManagedUiWeatherSurfacePersistenceAsync(
                    result,
                    weatherSurfacePersistencePhase!);
            }
            else if (scenario == AotManagedUiLocalFilePersistenceScenario)
            {
                await CaptureAotManagedUiLocalFilePersistenceAsync(
                    result,
                    localFilePersistencePhase!);
            }
            else if (scenario == AotManagedUiRecycleBinScenario)
            {
                await CaptureAotManagedUiRecycleBinAsync(
                    result,
                    recycleBinPhase!);
            }
            else if (scenario == AotManagedUiShellMoveScenario)
            {
                await CaptureAotManagedUiShellMoveAsync(
                    result,
                    shellMovePhase!);
            }
            else if (scenario == AotManagedUiFilePropertiesScenario)
            {
                await CaptureAotManagedUiFilePropertiesAsync(result);
            }
            else if (scenario == AotManagedUiPickerClipboardScenario)
            {
                await CaptureAotManagedUiPickerClipboardAsync(
                    result,
                    pickerClipboardPhase!);
            }
            else if (scenario == AotManagedUiNativeDropScenario)
            {
                await CaptureAotManagedUiNativeDropAsync(
                    result,
                    nativeDropPhase!);
            }
            else
            {
                await CaptureAotManagedUiGlancePersistenceAsync(
                    result,
                    glancePersistencePhase!);
            }

            result.Success = true;
            result.State = "Completed";
            result.CompletedAtUtc = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.State = "Failed";
            result.CompletedAtUtc = DateTimeOffset.UtcNow;
            result.Error = ex.ToString();
            Log($"[AotManagedUiSmoke] Failed: {ex}");
        }
        finally
        {
            WriteAotManagedUiResult(resultPath, result);
            if (scenario is AotManagedUiSettingsWidgetPersistenceScenario or
                AotManagedUiQuickCapturePersistenceScenario or
                AotManagedUiTodoPersistenceScenario or
                AotManagedUiTodoStepsPersistenceScenario or
                AotManagedUiTodoAttachmentsPersistenceScenario or
                AotManagedUiGlancePersistenceScenario or
                AotManagedUiWeatherSettingsPersistenceScenario or
                AotManagedUiWeatherSurfacePersistenceScenario or
                AotManagedUiLocalFilePersistenceScenario or
                AotManagedUiRecycleBinScenario or
                AotManagedUiShellMoveScenario or
                AotManagedUiFilePropertiesScenario or
                AotManagedUiPickerClipboardScenario or
                AotManagedUiNativeDropScenario)
            {
                await Task.Delay(100);
                await ShutdownApplicationAsync();
            }
        }
    }

    private async Task CaptureAotManagedUiTrayAndWidgetsAsync(
        AotManagedUiSmokeResult result)
    {
        await Task.Delay(250);

        if (_trayIcon is null || _trayWindow is null)
        {
            throw new InvalidOperationException("The product tray surface is unavailable.");
        }

        result.TrayIconCreated = _trayIcon.IsCreated;
        result.TrayIconWindowHandle = _trayIcon.TrayIcon.WindowHandle.ToInt64();
        result.TrayOwnerWindowHandle =
            WinRT.Interop.WindowNative.GetWindowHandle(_trayWindow).ToInt64();
        RequireAotManagedUi(
            result,
            result.TrayIconCreated &&
            result.TrayIconWindowHandle != 0 &&
            result.TrayOwnerWindowHandle != 0,
            "TrayCreated",
            "The real tray icon or its owner HWND was not created.");

        bool isQuickCapturePersistence =
            result.Scenario == AotManagedUiQuickCapturePersistenceScenario;
        bool isTodoAttachmentsPersistence =
            result.Scenario == AotManagedUiTodoAttachmentsPersistenceScenario;
        bool isTodoPersistence =
            result.Scenario is AotManagedUiTodoPersistenceScenario or
                AotManagedUiTodoStepsPersistenceScenario or
                AotManagedUiTodoAttachmentsPersistenceScenario;
        bool isTodoStepsPersistence =
            result.Scenario == AotManagedUiTodoStepsPersistenceScenario;
        bool isGlancePersistence =
            result.Scenario == AotManagedUiGlancePersistenceScenario;
        bool isWeatherSettingsPersistence =
            result.Scenario == AotManagedUiWeatherSettingsPersistenceScenario;
        bool isWeatherSurfacePersistence =
            result.Scenario == AotManagedUiWeatherSurfacePersistenceScenario;
        bool isLocalFilePersistence =
            result.Scenario == AotManagedUiLocalFilePersistenceScenario;
        bool isRecycleBin =
            result.Scenario == AotManagedUiRecycleBinScenario;
        bool isShellMove =
            result.Scenario == AotManagedUiShellMoveScenario;
        bool isFileProperties =
            result.Scenario == AotManagedUiFilePropertiesScenario;
        bool isPickerClipboard =
            result.Scenario == AotManagedUiPickerClipboardScenario;
        bool isNativeDrop =
            result.Scenario == AotManagedUiNativeDropScenario;
        string ownedPrimaryWidgetId = isQuickCapturePersistence
            ? AotManagedUiQuickCaptureWidgetId
            : isNativeDrop
                ? AotManagedUiNativeDropWidgetId
            : isPickerClipboard
                ? AotManagedUiPickerClipboardWidgetId
            : isFileProperties
                ? AotManagedUiFilePropertiesWidgetId
            : isShellMove
                ? AotManagedUiShellMoveWidgetId
            : isRecycleBin
                ? AotManagedUiRecycleBinWidgetId
            : isLocalFilePersistence
                ? AotManagedUiLocalFileWidgetId
            : isWeatherSurfacePersistence
                ? AotManagedUiWeatherSurfaceWidgetId
            : isWeatherSettingsPersistence
                ? AotManagedUiWeatherSettingsWidgetId
            : isGlancePersistence
                ? AotManagedUiGlanceWidgetId
            : isTodoAttachmentsPersistence
                ? AotManagedUiTodoAttachmentsWidgetId
                : isTodoStepsPersistence
                    ? AotManagedUiTodoStepsWidgetId
                : isTodoPersistence
                    ? AotManagedUiTodoWidgetId
                    : AotManagedUiFileWidgetId;
        WidgetKind ownedPrimaryWidgetKind = isQuickCapturePersistence
            ? WidgetKind.QuickCapture
            : isWeatherSurfacePersistence
                ? WidgetKind.Weather
            : isWeatherSettingsPersistence
                ? WidgetKind.Weather
            : isGlancePersistence
                ? WidgetKind.Glance
            : isTodoPersistence
                ? WidgetKind.Todo
                : WidgetKind.File;
        WidgetConfig[] seededWidgets = SettingsService.Settings.Widgets
            .Where(widget =>
                widget.Id == ownedPrimaryWidgetId ||
                widget.Id == AotManagedUiSearchWidgetId)
            .OrderBy(widget => widget.Id, StringComparer.Ordinal)
            .ToArray();
        RequireAotManagedUi(
            result,
            SettingsService.Settings.Widgets.Count == 2 &&
            seededWidgets.Length == 2 &&
            seededWidgets.Any(widget =>
                widget.Id == ownedPrimaryWidgetId &&
                widget.WidgetKind == ownedPrimaryWidgetKind &&
                widget.IsVisible &&
                !widget.IsDisabled) &&
            seededWidgets.Any(widget =>
                widget.Id == AotManagedUiSearchWidgetId &&
                widget.WidgetKind == WidgetKind.Search &&
                widget.IsVisible &&
                !widget.IsDisabled),
            "SeededWidgetConfiguration",
            "The isolated preview does not contain exactly the two owned widget fixtures.");
        result.SeededWidgetIds = seededWidgets.Select(widget => widget.Id).ToList();

        WidgetManager manager = WidgetManager ??
            throw new InvalidOperationException("WidgetManager is unavailable.");
        DeskBoxWidgetManagerDiagnostic widgetDiagnostic =
            WidgetManager.CreateDiagnosticsSnapshot();
        result.LoadedSurfaceCount = widgetDiagnostic.LoadedSurfaceCount;
        result.VisibleSurfaceCount = widgetDiagnostic.VisibleSurfaceCount;
        result.VisibleWidgetKinds = widgetDiagnostic.Hosts
            .Where(host => host.Visible)
            .Select(host => host.WidgetKind.ToString())
            .OrderBy(kind => kind, StringComparer.Ordinal)
            .ToList();
        bool expectedHostsRestored = isWeatherSettingsPersistence
            ? manager.LoadedSurfaceCount == 1 &&
                widgetDiagnostic.LoadedSurfaceCount == 1 &&
                widgetDiagnostic.VisibleSurfaceCount == 1 &&
                widgetDiagnostic.Hosts.All(host =>
                    host.WidgetKind != WidgetKind.Weather) &&
                widgetDiagnostic.Hosts.Count(host =>
                    host.WidgetKind == WidgetKind.Search && host.Visible) == 1
            : manager.LoadedSurfaceCount == 2 &&
                widgetDiagnostic.LoadedSurfaceCount == 2 &&
                widgetDiagnostic.VisibleSurfaceCount == 2 &&
                widgetDiagnostic.Hosts.Count(host =>
                    host.WidgetKind == ownedPrimaryWidgetKind && host.Visible) == 1 &&
                widgetDiagnostic.Hosts.Count(host =>
                    host.WidgetKind == WidgetKind.Search && host.Visible) == 1;
        RequireAotManagedUi(
            result,
            expectedHostsRestored,
            "SeededWidgetsRestored",
            isWeatherSettingsPersistence
                ? "The local-only Weather settings matrix did not suppress the Weather host."
                : "The real owned primary and Search widget HWNDs were not both restored and visible.");
    }

    private static void CaptureAotManagedUiLocales(AotManagedUiSmokeResult result)
    {
        IReadOnlyList<AotLocaleResourceDiagnostic> localeDiagnostics =
            LocalizationService.CaptureAotSmokeResourceDiagnostics();
        result.Locales = localeDiagnostics
            .Select(locale => new AotManagedUiLocaleEvidence
            {
                Locale = locale.Locale,
                ResourceCount = locale.ResourceCount,
                HasSettingsTitle = locale.HasSettingsTitle,
                HasOpenSettingsAction = locale.HasOpenSettingsAction
            })
            .ToList();
        RequireAotManagedUi(
            result,
            localeDiagnostics.Count == 12 &&
            localeDiagnostics.Select(locale => locale.Locale).Distinct(
                StringComparer.Ordinal).Count() == 12 &&
            localeDiagnostics.All(locale =>
                locale.ResourceCount > 0 &&
                locale.HasSettingsTitle &&
                locale.HasOpenSettingsAction),
            "AllLocaleResourcesLoaded",
            "One or more shipped locale dictionaries could not be loaded or lacks a required UI key.");
    }

    private async Task CaptureAotManagedUiSettingsAsync(AotManagedUiSmokeResult result)
    {
        string[] settingsSections =
        [
            "General",
            "Appearance",
            "FeatureWidgets",
            "Interaction",
            "Maintenance",
            "About"
        ];

        foreach (string sectionTag in settingsSections)
        {
            ShowSettings(sectionTag);
            AotSettingsWindowSnapshot snapshot =
                await WaitForManagedUiSettingsAsync(sectionTag);
            result.SettingsSections.Add(new AotManagedUiSettingsEvidence
            {
                Section = sectionTag,
                WindowHandle = snapshot.WindowHandle,
                IsAppWindowVisible = snapshot.IsAppWindowVisible,
                HasXamlRoot = snapshot.HasXamlRoot,
                ActualWidth = snapshot.ActualWidth,
                ActualHeight = snapshot.ActualHeight,
                Title = snapshot.Title,
                CurrentSection = snapshot.CurrentSection,
                SelectedSection = snapshot.SelectedSection,
                VisibleSections = snapshot.VisibleSections.ToList()
            });
        }

        RequireAotManagedUi(
            result,
            result.SettingsSections.Count == settingsSections.Length,
            "SettingsSectionsCompleted",
            "Not every major settings section completed its real navigation path.");
    }

    private async Task CaptureAotManagedUiDeepSettingsAsync(
        AotManagedUiSmokeResult result)
    {
        ShowSettings("General");
        await WaitForManagedUiSettingsAsync("General");
        SettingsWindow settingsWindow = _settingsWindow ??
            throw new InvalidOperationException("The settings window is unavailable.");
        AotDeepSettingsSnapshot snapshot =
            await settingsWindow.ExerciseAotDeepReadOnlySettingsAsync();
        result.DeepSettings = new AotManagedUiDeepSettingsEvidence
        {
            SearchQuery = snapshot.SearchQuery,
            SearchActivatedSection = snapshot.SearchActivatedSection,
            BreadcrumbParentReturned = snapshot.BreadcrumbParentReturned,
            FileStackRuleCount = snapshot.FileStackRuleCount,
            BackupSnapshotCount = snapshot.BackupSnapshotCount,
            SearchSuggestions = snapshot.SearchSuggestions
                .Select(suggestion => new AotManagedUiSettingsSearchSuggestionEvidence
                {
                    SectionTag = suggestion.SectionTag,
                    Title = suggestion.Title,
                    Breadcrumb = suggestion.Breadcrumb,
                    Description = suggestion.Description,
                    IsPage = suggestion.IsPage
                })
                .ToList(),
            PageTransitions = snapshot.PageTransitions
                .Select(page => new AotManagedUiDeepSettingsPageEvidence
                {
                    Section = page.Section,
                    ExpectedParentTag = page.ExpectedParentTag,
                    ExpectedNavTag = page.ExpectedNavTag,
                    CurrentSection = page.CurrentSection,
                    SelectedNavTag = page.SelectedNavTag,
                    HasXamlRoot = page.HasXamlRoot,
                    ActualWidth = page.ActualWidth,
                    ActualHeight = page.ActualHeight,
                    VisibleSections = page.VisibleSections.ToList(),
                    BreadcrumbHostVisible = page.BreadcrumbHostVisible,
                    BreadcrumbBarVisible = page.BreadcrumbBarVisible,
                    BackButtonVisible = page.BackButtonVisible,
                    BreadcrumbItems = page.BreadcrumbItems
                        .Select(item => new AotManagedUiBreadcrumbEvidence
                        {
                            SectionTag = item.SectionTag,
                            Title = item.Title,
                            Opacity = item.Opacity
                        })
                        .ToList()
                })
                .ToList()
        };

        RequireAotManagedUi(
            result,
            result.DeepSettings.SearchSuggestions.Count > 0 &&
            string.Equals(
                result.DeepSettings.SearchActivatedSection,
                "BackupRestoreSettings",
                StringComparison.Ordinal) &&
            result.DeepSettings.PageTransitions.Count == 25 &&
            result.DeepSettings.BreadcrumbParentReturned &&
            result.DeepSettings.FileStackRuleCount == 1 &&
            result.DeepSettings.BackupSnapshotCount > 0,
            "DeepSettingsCompleted",
            "The deep settings search, page, or breadcrumb matrix is incomplete.");
    }

    private async Task CaptureAotManagedUiPersistenceAsync(
        AotManagedUiSmokeResult result,
        string phase)
    {
        ShowSettings("Appearance");
        await WaitForManagedUiSettingsAsync("Appearance");
        SettingsWindow settingsWindow = _settingsWindow ??
            throw new InvalidOperationException("The settings window is unavailable.");
        SettingsViewModel settingsViewModel = settingsWindow.ViewModel;
        WidgetManager manager = WidgetManager ??
            throw new InvalidOperationException("WidgetManager is unavailable.");
        AotManagedUiPersistenceEvidence evidence = result.Persistence ??
            throw new InvalidOperationException(
                "The persistence phase evidence was not initialized.");

        evidence.Before = CaptureAotManagedUiPersistenceState(
            manager,
            settingsViewModel);
        RequireAotManagedUiPersistenceLiveState(evidence.Before);

        switch (phase)
        {
            case AotManagedUiPersistenceMutatePhase:
                RequireAotManagedUi(
                    result,
                    IsAotManagedUiPersistenceBaseline(evidence.Before),
                    "PersistenceBaselineCaptured",
                    "The first process did not start from the seeded persistence baseline.");
                settingsViewModel.ShowFileExtensions = true;
                settingsViewModel.FileNameLineCount = SettingsService.MinFileNameLineCount;
                settingsViewModel.TextSize = AotManagedUiMutatedTextSize;
                settingsViewModel.SelectedTrayIconStyle =
                    AotManagedUiMutatedTrayIconStyle;
                await manager.ApplyAotPersistenceFileWidgetMutationAsync(
                    AotManagedUiFileWidgetId,
                    AotManagedUiMutatedFileWidgetName,
                    ViewMode.List,
                    positionLocked: true,
                    sizeLocked: true);
                break;

            case AotManagedUiPersistenceVerifyRestorePhase:
                RequireAotManagedUi(
                    result,
                    IsAotManagedUiPersistenceMutation(evidence.Before),
                    "PersistenceRestartVerified",
                    "The second process did not reload every mutated settings and widget field.");
                settingsViewModel.ShowFileExtensions = false;
                settingsViewModel.FileNameLineCount = SettingsService.DefaultFileNameLineCount;
                settingsViewModel.TextSize = AotManagedUiBaselineTextSize;
                settingsViewModel.SelectedTrayIconStyle =
                    AotManagedUiBaselineTrayIconStyle;
                await manager.RestoreAotPersistenceFileWidgetBaselineAsync(
                    AotManagedUiFileWidgetId,
                    AotManagedUiBaselineFileWidgetName,
                    ViewMode.Icon,
                    positionLocked: false,
                    sizeLocked: false);
                break;

            case AotManagedUiPersistencePostflightPhase:
                RequireAotManagedUi(
                    result,
                    IsAotManagedUiPersistenceBaseline(evidence.Before),
                    "PersistencePostflightVerified",
                    "The third process did not reload the restored clean baseline.");
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported persistence phase '{phase}'.");
        }

        evidence.FlushSucceeded = await SettingsService.FlushPendingSaveAsync(
            notifySubscribers: false);
        RequireAotManagedUi(
            result,
            evidence.FlushSucceeded,
            "SettingsPersistenceFlushed",
            "The settings service did not confirm its explicit persistence flush.");
        await Task.Delay(150);
        evidence.After = CaptureAotManagedUiPersistenceState(
            manager,
            settingsViewModel);
        RequireAotManagedUiPersistenceLiveState(evidence.After);

        bool expectedAfterState = phase switch
        {
            AotManagedUiPersistenceMutatePhase =>
                IsAotManagedUiPersistenceMutation(evidence.After),
            _ => IsAotManagedUiPersistenceBaseline(evidence.After)
        };
        RequireAotManagedUi(
            result,
            expectedAfterState,
            phase == AotManagedUiPersistenceMutatePhase
                ? "PersistenceMutationApplied"
                : "PersistenceBaselineRestored",
            "The persistence phase did not reach its expected after-state.");
    }

    private AotManagedUiPersistenceStateEvidence CaptureAotManagedUiPersistenceState(
        WidgetManager manager,
        SettingsViewModel settingsViewModel)
    {
        AppSettings settings = SettingsService.Settings;
        return new AotManagedUiPersistenceStateEvidence
        {
            ShowFileExtensions = settings.ShowFileExtensions,
            FileNameLineCount = settings.FileNameLineCount,
            TextSize = settings.TextSize,
            TrayIconStyle = settings.TrayIconStyle ?? string.Empty,
            ViewModelShowFileExtensions = settingsViewModel.ShowFileExtensions,
            ViewModelFileNameLineCount = settingsViewModel.FileNameLineCount,
            ViewModelTextSize = settingsViewModel.TextSize,
            ViewModelTrayIconStyle = settingsViewModel.SelectedTrayIconStyle,
            FileWidget = MapAotManagedUiPersistenceWidget(
                manager.CaptureAotPersistenceWidgetSnapshot(AotManagedUiFileWidgetId)),
            SearchWidget = MapAotManagedUiPersistenceWidget(
                manager.CaptureAotPersistenceWidgetSnapshot(AotManagedUiSearchWidgetId))
        };
    }

    private static AotManagedUiPersistenceWidgetEvidence MapAotManagedUiPersistenceWidget(
        AotWidgetPersistenceSnapshot snapshot)
    {
        return new AotManagedUiPersistenceWidgetEvidence
        {
            Id = snapshot.Id,
            Name = snapshot.Name,
            WidgetKind = snapshot.WidgetKind,
            ViewMode = snapshot.ViewMode,
            IsVisible = snapshot.IsVisible,
            IsDisabled = snapshot.IsDisabled,
            IsPositionLocked = snapshot.IsPositionLocked,
            IsSizeLocked = snapshot.IsSizeLocked,
            X = snapshot.X,
            Y = snapshot.Y,
            Width = snapshot.Width,
            Height = snapshot.Height,
            PositionAnchor = snapshot.PositionAnchor,
            PositionMarginX = snapshot.PositionMarginX,
            PositionMarginY = snapshot.PositionMarginY,
            PositionMonitorKey = snapshot.PositionMonitorKey,
            PositionMonitorDeviceName = snapshot.PositionMonitorDeviceName,
            PositionMonitorWasPrimary = snapshot.PositionMonitorWasPrimary,
            BoundsCoordinateVersion = snapshot.BoundsCoordinateVersion,
            HasBaselineMetadata = snapshot.HasBaselineMetadata,
            IsLoaded = snapshot.IsLoaded,
            WindowHandle = snapshot.WindowHandle,
            IsHostVisible = snapshot.IsHostVisible,
            HasXamlRoot = snapshot.HasXamlRoot,
            ActualBounds = new AotManagedUiPersistenceBoundsEvidence
            {
                X = snapshot.ActualBounds.X,
                Y = snapshot.ActualBounds.Y,
                Width = snapshot.ActualBounds.Width,
                Height = snapshot.ActualBounds.Height
            },
            ViewModelName = snapshot.ViewModelName,
            ViewModelViewMode = snapshot.ViewModelViewMode,
            ViewModelPositionLocked = snapshot.ViewModelPositionLocked,
            ViewModelSizeLocked = snapshot.ViewModelSizeLocked
        };
    }

    private static void RequireAotManagedUiPersistenceLiveState(
        AotManagedUiPersistenceStateEvidence state)
    {
        AotManagedUiPersistenceWidgetEvidence file = state.FileWidget;
        AotManagedUiPersistenceWidgetEvidence search = state.SearchWidget;
        if (!file.IsLoaded || file.WindowHandle == 0 || !file.IsHostVisible ||
            !file.HasXamlRoot || file.ActualBounds.Width <= 0 ||
            file.ActualBounds.Height <= 0 ||
            !search.IsLoaded || search.WindowHandle == 0 ||
            !search.IsHostVisible || !search.HasXamlRoot ||
            search.ActualBounds.Width <= 0 || search.ActualBounds.Height <= 0 ||
            !string.Equals(file.ViewModelName, file.Name, StringComparison.Ordinal) ||
            !string.Equals(file.ViewModelViewMode, file.ViewMode, StringComparison.Ordinal) ||
            file.ViewModelPositionLocked != file.IsPositionLocked ||
            file.ViewModelSizeLocked != file.IsSizeLocked)
        {
            throw new InvalidOperationException(
                "The fixed File/Search widget configuration is not represented by live loaded HWNDs.");
        }
    }

    private static bool IsAotManagedUiPersistenceBaseline(
        AotManagedUiPersistenceStateEvidence state)
    {
        return !state.ShowFileExtensions &&
            state.FileNameLineCount == SettingsService.DefaultFileNameLineCount &&
            Math.Abs(state.TextSize - AotManagedUiBaselineTextSize) < 0.001 &&
            string.Equals(
                state.TrayIconStyle,
                AotManagedUiBaselineTrayIconStyle,
                StringComparison.Ordinal) &&
            !state.ViewModelShowFileExtensions &&
            state.ViewModelFileNameLineCount == SettingsService.DefaultFileNameLineCount &&
            Math.Abs(state.ViewModelTextSize - AotManagedUiBaselineTextSize) < 0.001 &&
            string.Equals(
                state.ViewModelTrayIconStyle,
                AotManagedUiBaselineTrayIconStyle,
                StringComparison.Ordinal) &&
            IsAotManagedUiPersistenceWidgetState(
                state.FileWidget,
                AotManagedUiFileWidgetId,
                AotManagedUiBaselineFileWidgetName,
                WidgetKind.File,
                ViewMode.Icon,
                positionLocked: false,
                sizeLocked: false,
                hasBaselineMetadata: false) &&
            IsAotManagedUiPersistenceWidgetState(
                state.SearchWidget,
                AotManagedUiSearchWidgetId,
                "AOT Search Fixture",
                WidgetKind.Search,
                ViewMode.Icon,
                positionLocked: false,
                sizeLocked: false,
                hasBaselineMetadata: false);
    }

    private static bool IsAotManagedUiPersistenceMutation(
        AotManagedUiPersistenceStateEvidence state)
    {
        return state.ShowFileExtensions &&
            state.FileNameLineCount == SettingsService.MinFileNameLineCount &&
            Math.Abs(state.TextSize - AotManagedUiMutatedTextSize) < 0.001 &&
            string.Equals(
                state.TrayIconStyle,
                AotManagedUiMutatedTrayIconStyle,
                StringComparison.Ordinal) &&
            state.ViewModelShowFileExtensions &&
            state.ViewModelFileNameLineCount == SettingsService.MinFileNameLineCount &&
            Math.Abs(state.ViewModelTextSize - AotManagedUiMutatedTextSize) < 0.001 &&
            string.Equals(
                state.ViewModelTrayIconStyle,
                AotManagedUiMutatedTrayIconStyle,
                StringComparison.Ordinal) &&
            IsAotManagedUiPersistenceWidgetState(
                state.FileWidget,
                AotManagedUiFileWidgetId,
                AotManagedUiMutatedFileWidgetName,
                WidgetKind.File,
                ViewMode.List,
                positionLocked: true,
                sizeLocked: true,
                hasBaselineMetadata: true) &&
            IsAotManagedUiPersistenceWidgetState(
                state.SearchWidget,
                AotManagedUiSearchWidgetId,
                "AOT Search Fixture",
                WidgetKind.Search,
                ViewMode.Icon,
                positionLocked: false,
                sizeLocked: false,
                hasBaselineMetadata: false);
    }

    private static bool IsAotManagedUiPersistenceWidgetState(
        AotManagedUiPersistenceWidgetEvidence widget,
        string id,
        string name,
        WidgetKind kind,
        ViewMode viewMode,
        bool positionLocked,
        bool sizeLocked,
        bool hasBaselineMetadata)
    {
        return string.Equals(widget.Id, id, StringComparison.Ordinal) &&
            string.Equals(widget.Name, name, StringComparison.Ordinal) &&
            string.Equals(widget.WidgetKind, kind.ToString(), StringComparison.Ordinal) &&
            string.Equals(widget.ViewMode, viewMode.ToString(), StringComparison.Ordinal) &&
            widget.IsVisible &&
            !widget.IsDisabled &&
            widget.IsPositionLocked == positionLocked &&
            widget.IsSizeLocked == sizeLocked &&
            widget.HasBaselineMetadata == hasBaselineMetadata;
    }

    private async Task<AotSettingsWindowSnapshot> WaitForManagedUiSettingsAsync(
        string sectionTag)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        AotSettingsWindowSnapshot? lastSnapshot = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_settingsWindow is { } settingsWindow)
            {
                lastSnapshot = settingsWindow.CaptureAotSmokeSnapshot();
                if (lastSnapshot.WindowHandle != 0 &&
                    lastSnapshot.IsAppWindowVisible &&
                    lastSnapshot.HasXamlRoot &&
                    lastSnapshot.ActualWidth > 0 &&
                    lastSnapshot.ActualHeight > 0 &&
                    !string.IsNullOrWhiteSpace(lastSnapshot.Title) &&
                    string.Equals(lastSnapshot.CurrentSection, sectionTag, StringComparison.Ordinal) &&
                    string.Equals(lastSnapshot.SelectedSection, sectionTag, StringComparison.Ordinal) &&
                    lastSnapshot.VisibleSections.Contains(sectionTag, StringComparer.Ordinal))
                {
                    return lastSnapshot;
                }
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException(
            $"Settings section '{sectionTag}' did not reach its visible loaded state. " +
            $"Last snapshot: {lastSnapshot}.");
    }

    private async Task CaptureAotManagedUiSearchAsync(AotManagedUiSmokeResult result)
    {
        string searchQuery = LocalizationService.T("Search.Action.OpenSettings");
        RequireAotManagedUi(
            result,
            !string.IsNullOrWhiteSpace(searchQuery),
            "LocalizedSearchQuery",
            "The guaranteed localized settings action query is empty.");

        OpenSearchPopupWithQuery(searchQuery);
        AotSearchWindowSnapshot initialSnapshot =
            await WaitForManagedUiSearchAsync(searchQuery);
        SearchPopupWindow popup = _searchPopupWindow ??
            throw new InvalidOperationException("The search popup disappeared before control routing.");
        AotSearchControlExercise exercise = popup.ExerciseAotReadOnlyControls();
        AotSearchWindowSnapshot finalSnapshot = popup.CaptureAotSmokeSnapshot();

        string[] expectedFilterTransitions =
        [
            "All:All",
            "FilesAndFolders:FilesAndFolders",
            "Apps:Apps",
            "Images:Images",
            "Documents:Documents",
            "DeskBox:DeskBox"
        ];
        string[] expectedSortTransitions =
        [
            "Name:True",
            "Name:False",
            "Size:False",
            "Size:True",
            "Date:False",
            "Date:True",
            "Type:True",
            "Type:False"
        ];
        RequireAotManagedUi(
            result,
            exercise.FilterTransitions.SequenceEqual(
                expectedFilterTransitions,
                StringComparer.Ordinal) &&
            exercise.SortTransitions.SequenceEqual(
                expectedSortTransitions,
                StringComparer.Ordinal),
            "SearchControlRoutes",
            "A filter or two-click sort route did not produce the required transition sequence.");
        RequireAotManagedUi(
            result,
            finalSnapshot.WindowHandle != 0 &&
            finalSnapshot.IsAppWindowVisible &&
            finalSnapshot.IsPopupVisible &&
            finalSnapshot.HasXamlRoot &&
            !finalSnapshot.IsSearching &&
            finalSnapshot.HasResults &&
            finalSnapshot.HasOpenSettingsAction &&
            string.Equals(finalSnapshot.ResultFilter, "All", StringComparison.Ordinal) &&
            string.Equals(finalSnapshot.SortColumn, "Relevance", StringComparison.Ordinal) &&
            finalSnapshot.SortAscending,
            "SearchCompleted",
            "The search popup did not remain in a valid read-only completed state.");

        result.Search = new AotManagedUiSearchEvidence
        {
            Query = searchQuery,
            WindowHandle = initialSnapshot.WindowHandle,
            HasXamlRoot = initialSnapshot.HasXamlRoot,
            HasResults = initialSnapshot.HasResults,
            HasCurrentResults = initialSnapshot.HasCurrentResults,
            CurrentResultsCount = initialSnapshot.CurrentResultsCount,
            SelectedTabId = initialSnapshot.SelectedTabId,
            ResultFilterBarVisible = initialSnapshot.IsResultFilterBarVisible,
            SortHeaderRowVisible = initialSnapshot.IsSortHeaderRowVisible,
            HasOpenSettingsAction = initialSnapshot.HasOpenSettingsAction,
            FilterTransitions = exercise.FilterTransitions.ToList(),
            SortTransitions = exercise.SortTransitions.ToList(),
            FinalResultFilter = finalSnapshot.ResultFilter,
            FinalSortColumn = finalSnapshot.SortColumn,
            FinalSortAscending = finalSnapshot.SortAscending
        };
    }

    private async Task<AotSearchWindowSnapshot> WaitForManagedUiSearchAsync(string searchQuery)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        AotSearchWindowSnapshot? lastSnapshot = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_searchPopupWindow is { } popup)
            {
                lastSnapshot = popup.CaptureAotSmokeSnapshot();
                if (lastSnapshot.WindowHandle != 0 &&
                    lastSnapshot.IsAppWindowVisible &&
                    lastSnapshot.IsPopupVisible &&
                    lastSnapshot.HasXamlRoot &&
                    string.Equals(lastSnapshot.TextBoxQuery, searchQuery, StringComparison.Ordinal) &&
                    string.Equals(lastSnapshot.ViewModelQuery, searchQuery, StringComparison.Ordinal) &&
                    !lastSnapshot.IsSearching &&
                    lastSnapshot.HasResults &&
                    lastSnapshot.HasCurrentResults &&
                    lastSnapshot.CurrentResultsCount > 0 &&
                    string.Equals(lastSnapshot.SelectedTabId, "all", StringComparison.Ordinal) &&
                    lastSnapshot.IsResultFilterBarVisible &&
                    lastSnapshot.IsSortHeaderRowVisible &&
                    lastSnapshot.HasOpenSettingsAction)
                {
                    return lastSnapshot;
                }
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException(
            $"The real search window did not complete the guaranteed action query. " +
            $"Last snapshot: {lastSnapshot}.");
    }

    private static void RequireAotManagedUi(
        AotManagedUiSmokeResult result,
        bool condition,
        string step,
        string error)
    {
        if (!condition)
        {
            throw new InvalidOperationException(error);
        }

        result.Steps.Add(step);
    }

    private static void WriteAotManagedUiResult(
        string resultPath,
        AotManagedUiSmokeResult result)
    {
        string temporaryPath = resultPath + ".tmp";
        string json = JsonSerializer.Serialize(
            result,
            AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, resultPath, overwrite: true);
    }

    private static bool IsAotManagedUiPathEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAotManagedUiPathEqualOrInside(string root, string candidate)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class AotManagedUiSmokeResult
{
    public int SchemaVersion { get; set; }
    public string Scenario { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public bool Success { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public int ProcessId { get; set; }
    public string? ExecutablePath { get; set; }
    public string PreviewDataRoot { get; set; } = string.Empty;
    public string EvidenceRoot { get; set; } = string.Empty;
    public string ResultPath { get; set; } = string.Empty;
    public bool IsDynamicCodeSupported { get; set; }
    public bool TrayIconCreated { get; set; }
    public long TrayIconWindowHandle { get; set; }
    public long TrayOwnerWindowHandle { get; set; }
    public List<string> SeededWidgetIds { get; set; } = [];
    public int LoadedSurfaceCount { get; set; }
    public int VisibleSurfaceCount { get; set; }
    public List<string> VisibleWidgetKinds { get; set; } = [];
    public List<AotManagedUiLocaleEvidence> Locales { get; set; } = [];
    public List<AotManagedUiSettingsEvidence> SettingsSections { get; set; } = [];
    public AotManagedUiSearchEvidence? Search { get; set; }
    public AotManagedUiDeepSettingsEvidence? DeepSettings { get; set; }
    public AotManagedUiPersistenceEvidence? Persistence { get; set; }
    public AotManagedUiQuickCapturePersistenceEvidence? QuickCapturePersistence { get; set; }
    public AotManagedUiTodoPersistenceEvidence? TodoPersistence { get; set; }
    public AotManagedUiTodoStepsPersistenceEvidence? TodoStepsPersistence { get; set; }
    public AotManagedUiTodoAttachmentsPersistenceEvidence? TodoAttachmentsPersistence { get; set; }
    public AotManagedUiGlancePersistenceEvidence? GlancePersistence { get; set; }
    public AotManagedUiWeatherSettingsPersistenceEvidence? WeatherSettingsPersistence { get; set; }
    public AotManagedUiWeatherSurfacePersistenceEvidence? WeatherSurfacePersistence { get; set; }
    public AotManagedUiLocalFilePersistenceEvidence? LocalFilePersistence { get; set; }
    public AotManagedUiRecycleBinEvidence? RecycleBin { get; set; }
    public AotManagedUiShellMoveEvidence? ShellMove { get; set; }
    public AotManagedUiFilePropertiesEvidence? FileProperties { get; set; }
    public AotManagedUiPickerClipboardEvidence? PickerClipboard { get; set; }
    public AotManagedUiNativeDropEvidence? NativeDrop { get; set; }
    public AotTodoNotificationSurfaceEvidence? TodoNotificationSurface { get; set; }
    public AotTodoNotificationUserClickEvidence? TodoNotificationUserClick { get; set; }
    public List<string> Steps { get; set; } = [];
    public string? Error { get; set; }
}

internal sealed class AotManagedUiLocaleEvidence
{
    public string Locale { get; set; } = string.Empty;
    public int ResourceCount { get; set; }
    public bool HasSettingsTitle { get; set; }
    public bool HasOpenSettingsAction { get; set; }
}

internal sealed class AotManagedUiSettingsEvidence
{
    public string Section { get; set; } = string.Empty;
    public long WindowHandle { get; set; }
    public bool IsAppWindowVisible { get; set; }
    public bool HasXamlRoot { get; set; }
    public double ActualWidth { get; set; }
    public double ActualHeight { get; set; }
    public string Title { get; set; } = string.Empty;
    public string CurrentSection { get; set; } = string.Empty;
    public string? SelectedSection { get; set; }
    public List<string> VisibleSections { get; set; } = [];
}

internal sealed class AotManagedUiSearchEvidence
{
    public string Query { get; set; } = string.Empty;
    public long WindowHandle { get; set; }
    public bool HasXamlRoot { get; set; }
    public bool HasResults { get; set; }
    public bool HasCurrentResults { get; set; }
    public int CurrentResultsCount { get; set; }
    public string? SelectedTabId { get; set; }
    public bool ResultFilterBarVisible { get; set; }
    public bool SortHeaderRowVisible { get; set; }
    public bool HasOpenSettingsAction { get; set; }
    public List<string> FilterTransitions { get; set; } = [];
    public List<string> SortTransitions { get; set; } = [];
    public string FinalResultFilter { get; set; } = string.Empty;
    public string FinalSortColumn { get; set; } = string.Empty;
    public bool FinalSortAscending { get; set; }
}

internal sealed class AotManagedUiDeepSettingsEvidence
{
    public string SearchQuery { get; set; } = string.Empty;
    public List<AotManagedUiSettingsSearchSuggestionEvidence> SearchSuggestions { get; set; } = [];
    public string SearchActivatedSection { get; set; } = string.Empty;
    public List<AotManagedUiDeepSettingsPageEvidence> PageTransitions { get; set; } = [];
    public bool BreadcrumbParentReturned { get; set; }
    public int FileStackRuleCount { get; set; }
    public int BackupSnapshotCount { get; set; }
}

internal sealed class AotManagedUiSettingsSearchSuggestionEvidence
{
    public string SectionTag { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Breadcrumb { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsPage { get; set; }
}

internal sealed class AotManagedUiDeepSettingsPageEvidence
{
    public string Section { get; set; } = string.Empty;
    public string? ExpectedParentTag { get; set; }
    public string ExpectedNavTag { get; set; } = string.Empty;
    public string CurrentSection { get; set; } = string.Empty;
    public string? SelectedNavTag { get; set; }
    public bool HasXamlRoot { get; set; }
    public double ActualWidth { get; set; }
    public double ActualHeight { get; set; }
    public List<string> VisibleSections { get; set; } = [];
    public bool BreadcrumbHostVisible { get; set; }
    public bool BreadcrumbBarVisible { get; set; }
    public bool BackButtonVisible { get; set; }
    public List<AotManagedUiBreadcrumbEvidence> BreadcrumbItems { get; set; } = [];
}

internal sealed class AotManagedUiBreadcrumbEvidence
{
    public string SectionTag { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public double Opacity { get; set; }
}

internal sealed class AotManagedUiPersistenceEvidence
{
    public string Phase { get; set; } = string.Empty;
    public bool FlushSucceeded { get; set; }
    public bool NormalShutdownRequested { get; set; }
    public AotManagedUiPersistenceStateEvidence Before { get; set; } = new();
    public AotManagedUiPersistenceStateEvidence After { get; set; } = new();
}

internal sealed class AotManagedUiPersistenceStateEvidence
{
    public bool ShowFileExtensions { get; set; }
    public int FileNameLineCount { get; set; }
    public double TextSize { get; set; }
    public string TrayIconStyle { get; set; } = string.Empty;
    public bool ViewModelShowFileExtensions { get; set; }
    public int ViewModelFileNameLineCount { get; set; }
    public double ViewModelTextSize { get; set; }
    public string ViewModelTrayIconStyle { get; set; } = string.Empty;
    public AotManagedUiPersistenceWidgetEvidence FileWidget { get; set; } = new();
    public AotManagedUiPersistenceWidgetEvidence SearchWidget { get; set; } = new();
}

internal sealed class AotManagedUiPersistenceWidgetEvidence
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string WidgetKind { get; set; } = string.Empty;
    public string ViewMode { get; set; } = string.Empty;
    public bool IsVisible { get; set; }
    public bool IsDisabled { get; set; }
    public bool IsPositionLocked { get; set; }
    public bool IsSizeLocked { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string? PositionAnchor { get; set; }
    public double PositionMarginX { get; set; }
    public double PositionMarginY { get; set; }
    public string? PositionMonitorKey { get; set; }
    public string? PositionMonitorDeviceName { get; set; }
    public bool? PositionMonitorWasPrimary { get; set; }
    public int BoundsCoordinateVersion { get; set; }
    public bool HasBaselineMetadata { get; set; }
    public bool IsLoaded { get; set; }
    public long WindowHandle { get; set; }
    public bool IsHostVisible { get; set; }
    public bool HasXamlRoot { get; set; }
    public AotManagedUiPersistenceBoundsEvidence ActualBounds { get; set; } = new();
    public string? ViewModelName { get; set; }
    public string? ViewModelViewMode { get; set; }
    public bool? ViewModelPositionLocked { get; set; }
    public bool? ViewModelSizeLocked { get; set; }
}

internal sealed class AotManagedUiPersistenceBoundsEvidence
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(
    typeof(AotManagedUiSmokeResult),
    TypeInfoPropertyName = "AotManagedUiSmokeResult")]
internal partial class AotManagedUiSmokeJsonContext : JsonSerializerContext
{
}
#endif
