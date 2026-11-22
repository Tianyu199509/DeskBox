using DeskBox.Models;

namespace DeskBox.Services;

public sealed record CustomWidgetCompilerDiagnostic(
    string Code,
    string Message,
    string Path,
    string? SuggestedPrompt = null,
    bool IsBlocking = false);

public sealed record CustomWidgetCompilerResult(
    string NormalizedManifestJson,
    CustomWidgetWorkspaceValidationResult ValidationResult,
    CustomWidgetHealthReport? HealthReport,
    IReadOnlyList<CustomWidgetValidationDiagnostic> HardDiagnostics,
    IReadOnlyList<CustomWidgetCompilerDiagnostic> SoftDiagnostics,
    IReadOnlyList<CustomWidgetCompilerDiagnostic> ActionableSoftDiagnostics,
    IReadOnlyList<CustomWidgetHealthIssue> DeadControlIssues,
    string? NormalizedLogicJs = null)
{
    public CustomWidgetManifest? Manifest => ValidationResult.Manifest;

    public bool HasHardErrors => Manifest is null || HardDiagnostics.Count > 0;

    public bool HasSoftIssues => SoftDiagnostics.Count > 0;

    public bool HasActionableSoftIssues => ActionableSoftDiagnostics.Count > 0;
}

/// <summary>
/// Stateless, single-source-of-truth Custom Widget compiler pipeline shared by Studio AI generation,
/// the Studio Workspace Engine, and headless MCP / CLI tools.
/// </summary>
public static class CustomWidgetCompilerPipeline
{
    public static CustomWidgetCompilerResult Compile(
        string rawManifestJson,
        string? rawLogicJs = null,
        CustomWidgetLocaleCatalog? locales = null,
        string? sampleDataJson = null,
        string? cultureName = null,
        double measuredWidth = 0,
        double measuredHeight = 0,
        CustomWidgetStorePayload? storePayload = null,
        bool runOnInitDryRun = true,
        bool normalizeDom = true,
        bool autoWireCapabilities = true,
        string? targetNodePath = null,
        bool strictAuthoring = true)
    {
        string workingManifestJson = rawManifestJson ?? string.Empty;
        string? workingLogicJs = rawLogicJs;

        if (normalizeDom && !string.IsNullOrWhiteSpace(workingManifestJson))
        {
            var normalizeEngine = new StudioWorkspaceEngine(
                workingManifestJson,
                workingLogicJs,
                locales,
                targetNodePath);
            if (normalizeEngine.HasManifest)
            {
                normalizeEngine.NormalizeDomInPlace();
                workingManifestJson = normalizeEngine.GetManifestJson(indented: true) ?? workingManifestJson;
                workingLogicJs = normalizeEngine.LogicJs ?? workingLogicJs;
            }
        }

        if (autoWireCapabilities && !string.IsNullOrWhiteSpace(workingManifestJson))
        {
            try
            {
                workingManifestJson = CustomWidgetManifestAutoFixer.AutoWireMissingCapabilities(workingManifestJson);
            }
            catch
            {
                // Keep partial/invalid JSON intact so the parser/validator reports exact diagnostics.
            }
        }

        CustomWidgetWorkspaceValidationResult validation = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            workingManifestJson,
            workingLogicJs,
            locales,
            runOnInitDryRun: runOnInitDryRun,
            sampleDataJson: sampleDataJson,
            strictAuthoring: strictAuthoring);

        var hardDiagnostics = validation.Diagnostics
            .Where(static d => d.IsError)
            .ToList();

        var softDiagnostics = new List<CustomWidgetCompilerDiagnostic>();
        var seenSoft = new HashSet<string>(StringComparer.Ordinal);

        foreach (CustomWidgetValidationDiagnostic diag in validation.Diagnostics.Where(static d => !d.IsError))
        {
            string code = string.IsNullOrWhiteSpace(diag.Code) ? "validation.warning" : diag.Code!;
            string key = $"{code}|{diag.Path}|{diag.Message}";
            if (seenSoft.Add(key))
            {
                softDiagnostics.Add(new CustomWidgetCompilerDiagnostic(
                    Code: code,
                    Message: diag.Message,
                    Path: diag.Path,
                    SuggestedPrompt: diag.Message,
                    IsBlocking: false));
            }
        }

        CustomWidgetHealthReport? healthReport = null;
        IReadOnlyList<CustomWidgetHealthIssue> deadControls = Array.Empty<CustomWidgetHealthIssue>();

        if (validation.Manifest is not null)
        {
            deadControls = CustomWidgetHealthAnalyzer.DetectDeadControlsAndUnreadMutations(
                validation.Manifest,
                cultureName,
                workingLogicJs);

            healthReport = CustomWidgetHealthAnalyzer.AnalyzeHealth(
                workingManifestJson,
                validation.Manifest,
                storePayload,
                measuredWidth: measuredWidth,
                measuredHeight: measuredHeight,
                cultureName: cultureName,
                logicJs: workingLogicJs);

            string rootPath = string.IsNullOrWhiteSpace(validation.Manifest.View.Id)
                ? "$.root"
                : $"#{validation.Manifest.View.Id}";

            foreach (CustomWidgetHealthIssue issue in healthReport.Issues)
            {
                string key = $"{issue.Code}|{issue.Message}";
                if (seenSoft.Add(key))
                {
                    softDiagnostics.Add(new CustomWidgetCompilerDiagnostic(
                        Code: issue.Code,
                        Message: issue.Message,
                        Path: rootPath,
                        SuggestedPrompt: issue.SuggestedPrompt,
                        IsBlocking: false));
                }
            }

            foreach (CustomWidgetHealthIssue dead in deadControls)
            {
                string key = $"{dead.Code}|{dead.Message}";
                if (seenSoft.Add(key))
                {
                    softDiagnostics.Add(new CustomWidgetCompilerDiagnostic(
                        Code: dead.Code,
                        Message: dead.Message,
                        Path: rootPath,
                        SuggestedPrompt: dead.SuggestedPrompt,
                        IsBlocking: false));
                }
            }
        }

        bool hasDeadControl = softDiagnostics.Any(static s =>
            string.Equals(s.Code, "interaction.dead_button", StringComparison.Ordinal) ||
            string.Equals(s.Code, "interaction.invalid_system_control", StringComparison.Ordinal));

        var actionableSoft = softDiagnostics
            .Where(s => IsActionableSoftDiagnosticCode(s.Code, hasDeadControl))
            .ToList();

        return new CustomWidgetCompilerResult(
            NormalizedManifestJson: workingManifestJson,
            ValidationResult: validation,
            HealthReport: healthReport,
            HardDiagnostics: hardDiagnostics,
            SoftDiagnostics: softDiagnostics,
            ActionableSoftDiagnostics: actionableSoft,
            DeadControlIssues: deadControls,
            NormalizedLogicJs: workingLogicJs);
    }

    private static bool IsActionableSoftDiagnosticCode(string? code, bool hasDeadControl)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        if (string.Equals(code, "interaction.unread_state", StringComparison.Ordinal))
        {
            // Standalone unread_state without an accompanying dead button is advisory only.
            return hasDeadControl;
        }

        return code is
            "ScriptSampleDataRuntimeError" or
            "ScriptSampleDataZeroEffect" or
            "ScriptHandlerRuntimeError" or
            "ScriptActionArgsKeyMismatch" or
            "ScriptNonFiniteStateValue" or
            "ScriptComputedUninitialized" or
            "DirtyEvaluatedInterpolation" or
            "UnclosedInterpolationExpression" or
            "interaction.dead_button" or
            "interaction.invalid_system_control" or
            "script.invalid_ctx_sources" or
            "script.state_computed_mismatch" or
            "layout.vertical_overflow" or
            "layout.cjk_column_squeeze" or
            "layout.empty_container" or
            "view.unconditional_loading_text" or
            "view.cold_start_split_state" or
            "view.sample_data_incomplete_state";
    }
}
