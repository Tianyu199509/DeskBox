namespace DeskBox.Models;

/// <summary>
/// Structural and authoring diagnostic codes. Structural manifest defects (<see cref="StrayViewPropertiesAtManifestRoot"/>,
/// <see cref="ConflictingCollectionAndChildren"/>, <see cref="RootViewConditionalWhen"/>, and
/// <see cref="UnclosedInterpolationExpression"/>) are emitted with <c>IsError: true</c> directly by the v1 parser/validators;
/// <see cref="PromoteToErrors"/> remains an idempotent helper that also promotes script authoring warnings during Studio/MCP compilation.
/// </summary>
public static class CustomWidgetStrictAuthoringRules
{
    public const string StrayViewPropertiesAtManifestRoot = "StrayViewPropertiesAtManifestRoot";
    public const string ConflictingCollectionAndChildren = "ConflictingCollectionAndChildren";
    public const string RootViewConditionalWhen = "RootViewConditionalWhen";
    public const string DeadStateSwitchBranch = "DeadStateSwitchBranch";
    public const string ScriptHandlerRuntimeError = "ScriptHandlerRuntimeError";
    public const string ScriptActionArgsKeyMismatch = "ScriptActionArgsKeyMismatch";
    public const string UnclosedInterpolationExpression = "UnclosedInterpolationExpression";
    public const string SecretInterpolationForbiddenInViewOrState = CustomWidgetDiagnosticCodes.SecretInterpolationForbiddenInViewOrState;

    public static bool IsStrictAuthoringCode(string? code) =>
        code is StrayViewPropertiesAtManifestRoot
            or ConflictingCollectionAndChildren
            or RootViewConditionalWhen
            or DeadStateSwitchBranch
            or ScriptHandlerRuntimeError
            or ScriptActionArgsKeyMismatch
            or UnclosedInterpolationExpression
            or SecretInterpolationForbiddenInViewOrState;

    public static List<CustomWidgetValidationDiagnostic> PromoteToErrors(
        IReadOnlyList<CustomWidgetValidationDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var promoted = new List<CustomWidgetValidationDiagnostic>(diagnostics.Count);
        foreach (CustomWidgetValidationDiagnostic diag in diagnostics)
        {
            promoted.Add(!diag.IsError && IsStrictAuthoringCode(diag.Code)
                ? diag with { IsError = true }
                : diag);
        }

        return promoted;
    }
}
