using System.Diagnostics;
using System.Text.Json;
using DeskBox.Services;
using Xunit;

namespace DeskBox.Tests;

public sealed class CustomWidgetScriptEngineTests
{
    [Fact]
    public void NativeEngine_IsAvailable_AndSupportsEs2023AndAsyncMicrotasks()
    {
        bool available = CustomWidgetScriptEngine.IsNativeEngineAvailable(out string? reason);
        Assert.True(available, reason ?? "deskbox_qjs native module should be available.");

        const string source = """
            export async function computeSummary(ctx) {
              const items = [10, 20, 30, 40];
              const last = items.at(-1);
              const unique = new Set([...items, 20]);
              const map = new Map([['currency', ctx.settings.baseCurrency ?? 'CNY']]);
              await Promise.resolve();
              ctx.state.total = String(items.reduce((a, b) => a + b, 0));
              ctx.computed.lastItem = last;
              ctx.computed.uniqueCount = unique.size;
              ctx.computed.currency = map.get('currency');
            }
            """;

        CustomWidgetScriptCompileResult compile = CustomWidgetScriptEngine.Compile(source);
        Assert.True(compile.IsSuccess, compile.ErrorMessage);
        Assert.NotNull(compile.Compiled);
        Assert.Contains("computeSummary", compile.Compiled!.ExportedFunctions);

        CustomWidgetScriptExecutionResult exec = CustomWidgetScriptEngine.Execute(
            compile.Compiled,
            "computeSummary",
            new CustomWidgetScriptExecutionInput
            {
                Settings = new Dictionary<string, string> { ["baseCurrency"] = "USD" }
            });

        Assert.True(exec.IsSuccess, exec.ErrorMessage);
        Assert.True(exec.Invoked);
        Assert.True(exec.StateChanged);
        Assert.Equal("100", exec.StateSet["total"]);
        Assert.Equal("40", CustomWidgetScriptEngine.ResolveComputedToken("lastItem", exec.Computed));
        Assert.Equal("4", CustomWidgetScriptEngine.ResolveComputedToken("uniqueCount", exec.Computed));
        Assert.Equal("USD", CustomWidgetScriptEngine.ResolveComputedToken("currency", exec.Computed));
    }

    [Fact]
    public void PrecompiledBytecode_ExecutesStatelessly_WithoutCrossCallPollution_OrPrototypeTampering()
    {
        const string source = """
            let moduleCounter = 0;

            export function mutateAndTamper(ctx) {
              moduleCounter += 1;
              globalThis.__leakedSecret = 'leaked';
              Object.prototype.hacked = 'evil';
              JSON.stringify = () => '{"invoked":false}';
              ctx.state.counter = String(moduleCounter);
              ctx.computed.nested = {
                badge: `N-${moduleCounter}`,
                rows: [
                  { id: 'r1', code: 'USD', value: '7.21' },
                  { id: 'r2', code: 'EUR', value: '7.85' }
                ]
              };
            }
            """;

        CustomWidgetScriptCompileResult compile = CustomWidgetScriptEngine.Compile(source);
        Assert.True(compile.IsSuccess, compile.ErrorMessage);

        CustomWidgetScriptExecutionResult first = CustomWidgetScriptEngine.Execute(
            compile.Compiled!,
            "mutateAndTamper",
            new CustomWidgetScriptExecutionInput());

        CustomWidgetScriptExecutionResult second = CustomWidgetScriptEngine.Execute(
            compile.Compiled!,
            "mutateAndTamper",
            new CustomWidgetScriptExecutionInput());

        Assert.True(first.IsSuccess, first.ErrorMessage);
        Assert.True(second.IsSuccess, second.ErrorMessage);
        // Fresh JSContext per call means module-level mutable variable resets to 0 every time,
        // and tampering with JSON.stringify / Object.prototype cannot corrupt the host bridge.
        Assert.Equal("1", first.StateSet["counter"]);
        Assert.Equal("1", second.StateSet["counter"]);
        Assert.Equal("N-1", CustomWidgetScriptEngine.ResolveComputedToken("nested.badge", second.Computed));
        Assert.Equal("7.85", CustomWidgetScriptEngine.ResolveComputedToken("nested.rows[1].value", second.Computed));
        Assert.Equal("7.85", CustomWidgetScriptEngine.ResolveComputedToken("nested.rows[-1].value", second.Computed));
        Assert.Equal("2", CustomWidgetScriptEngine.ResolveComputedToken("nested.rows.length", second.Computed));

        IReadOnlyList<CustomWidgetRecord> projected = CustomWidgetScriptEngine.ProjectComputedCollection(
            "$computed.nested.rows",
            second.Computed);
        Assert.Equal(2, projected.Count);
        Assert.Equal("r1", projected[0].Id);
        Assert.Equal("USD", projected[0].GetField("code"));
        Assert.Equal("7.85", projected[1].GetField("value"));
    }

    [Fact]
    public void StateDiff_CollectionsCrud_I18n_SecretIsolation_AndCopyGate_WorkAsExpected()
    {
        const string source = """
            export function runAction(ctx) {
              // Secret keys must never be visible in ctx.settings
              ctx.computed.sawApiKey = Object.prototype.hasOwnProperty.call(ctx.settings, 'apiKey');
              ctx.computed.city = ctx.settings.city;

              // State set + delete
              ctx.state.keepSame = 'same';
              ctx.state.updated = String(Number(ctx.state.updated || '0') + Number(ctx.event.arg || '1'));
              delete ctx.state.obsolete;

              // Collections CRUD
              ctx.collections.append('logs', { id: 'c2', title: 'Second', amount: 25 });
              ctx.collections.update('logs', 'c1', { title: 'First Updated' });
              ctx.collections.remove('logs', item => item.id === 'drop-me');

              // i18n formatting with placeholder & fallback
              ctx.computed.greeting = ctx.t('hello_fmt', 'Hi {0}', 'DeskBox');
              ctx.computed.fallbackText = ctx.t('missing_key', 'Default {0}', 42);

              // UI intents
              ctx.ui.toast('Saved', 'success');
              ctx.computed.copied = ctx.ui.copy('copied-value');
              ctx.ui.refreshSource('fx');
            }
            """;

        CustomWidgetScriptCompileResult compile = CustomWidgetScriptEngine.Compile(source);
        Assert.True(compile.IsSuccess, compile.ErrorMessage);

        var initialLogs = new List<CustomWidgetRecord>
        {
            new()
            {
                Id = "c1",
                Fields = new Dictionary<string, string> { ["title"] = "First", ["amount"] = "10" }
            },
            new()
            {
                Id = "drop-me",
                Fields = new Dictionary<string, string> { ["title"] = "Obsolete", ["amount"] = "0" }
            }
        };

        var baseInput = new CustomWidgetScriptExecutionInput
        {
            State = new Dictionary<string, string>
            {
                ["keepSame"] = "same",
                ["updated"] = "5",
                ["obsolete"] = "old"
            },
            Collections = new Dictionary<string, List<CustomWidgetRecord>>
            {
                ["logs"] = initialLogs
            },
            Settings = new Dictionary<string, string>
            {
                ["city"] = "Shanghai",
                ["apiKey"] = "sk-super-secret",
                ["__trustedTargets"] = "internal"
            },
            SecretKeysToExclude = new HashSet<string>(StringComparer.Ordinal) { "apiKey" },
            I18n = new Dictionary<string, string>
            {
                ["hello_fmt"] = "你好，{0}！"
            },
            Event = new CustomWidgetScriptEventPayload(Arg: "7"),
            AllowCopy = false
        };

        // Background hook: AllowCopy = false blocks clipboard copy
        CustomWidgetScriptExecutionResult bgExec = CustomWidgetScriptEngine.Execute(
            compile.Compiled!,
            "runAction",
            baseInput);

        Assert.True(bgExec.IsSuccess, bgExec.ErrorMessage);
        Assert.Equal("false", CustomWidgetScriptEngine.ResolveComputedToken("sawApiKey", bgExec.Computed));
        Assert.Equal("Shanghai", CustomWidgetScriptEngine.ResolveComputedToken("city", bgExec.Computed));
        Assert.Equal("false", CustomWidgetScriptEngine.ResolveComputedToken("copied", bgExec.Computed));
        Assert.DoesNotContain(bgExec.UiOps, op => op.Type == "copy");
        Assert.True(bgExec.StateChanged);
        Assert.False(bgExec.StateSet.ContainsKey("keepSame"));
        Assert.Equal("12", bgExec.StateSet["updated"]);
        Assert.Contains("obsolete", bgExec.StateRemoved);
        Assert.Equal("你好，DeskBox！", CustomWidgetScriptEngine.ResolveComputedToken("greeting", bgExec.Computed));
        Assert.Equal("Default 42", CustomWidgetScriptEngine.ResolveComputedToken("fallbackText", bgExec.Computed));

        List<CustomWidgetRecord> updatedLogs = bgExec.DirtyCollections["logs"];
        Assert.Equal(2, updatedLogs.Count);
        Assert.Equal("c1", updatedLogs[0].Id);
        Assert.Equal("First Updated", updatedLogs[0].GetField("title"));
        Assert.Equal("c2", updatedLogs[1].Id);
        Assert.Equal("25", updatedLogs[1].GetField("amount"));

        // Interactive action: AllowCopy = true permits clipboard copy
        CustomWidgetScriptExecutionResult userExec = CustomWidgetScriptEngine.Execute(
            compile.Compiled!,
            "runAction",
            new CustomWidgetScriptExecutionInput
            {
                AllowCopy = true,
                Event = new CustomWidgetScriptEventPayload(Arg: "1")
            });

        Assert.True(userExec.IsSuccess, userExec.ErrorMessage);
        Assert.Equal("true", CustomWidgetScriptEngine.ResolveComputedToken("copied", userExec.Computed));
        Assert.Contains(userExec.UiOps, op => op.Type == "copy" && op.Text == "copied-value");
        Assert.Contains(userExec.UiOps, op => op.Type == "toast" && op.Message == "Saved" && op.Tone == "success");
        Assert.Contains(userExec.UiOps, op => op.Type == "refreshSource" && op.SourceId == "fx");
    }

    [Fact]
    public void InfiniteLoop_IsAbortedByCpuInterruptWithinBudget()
    {
        const string source = """
            export function spinForever() {
              while (true) {}
            }
            """;

        CustomWidgetScriptCompileResult compile = CustomWidgetScriptEngine.Compile(source);
        Assert.True(compile.IsSuccess, compile.ErrorMessage);

        var sw = Stopwatch.StartNew();
        CustomWidgetScriptExecutionResult exec = CustomWidgetScriptEngine.Execute(
            compile.Compiled!,
            "spinForever",
            new CustomWidgetScriptExecutionInput { TimeoutMs = 40 });
        sw.Stop();

        Assert.False(exec.IsSuccess);
        Assert.Equal(CustomWidgetScriptStatus.Timeout, exec.Status);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"Expected fast interrupt, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void MemoryBomb_IsStoppedBy4MbSandboxLimit()
    {
        const string source = """
            export function allocateHugeString() {
              return 'x'.repeat(6 * 1024 * 1024);
            }

            export function allocateHugeBuffer() {
              return new Uint8Array(6 * 1024 * 1024).length;
            }
            """;

        CustomWidgetScriptCompileResult compile = CustomWidgetScriptEngine.Compile(source);
        Assert.True(compile.IsSuccess, compile.ErrorMessage);

        CustomWidgetScriptExecutionResult strExec = CustomWidgetScriptEngine.Execute(
            compile.Compiled!,
            "allocateHugeString",
            new CustomWidgetScriptExecutionInput());

        Assert.False(strExec.IsSuccess);
        Assert.Equal(CustomWidgetScriptStatus.OutOfMemory, strExec.Status);

        CustomWidgetScriptExecutionResult bufExec = CustomWidgetScriptEngine.Execute(
            compile.Compiled!,
            "allocateHugeBuffer",
            new CustomWidgetScriptExecutionInput());

        Assert.False(bufExec.IsSuccess);
        Assert.Equal(CustomWidgetScriptStatus.OutOfMemory, bufExec.Status);
    }

    [Fact]
    public void AirgappedSandbox_BlocksExternalImports_HasZeroNetworkOrOsGlobals_AndReportsLineNumbers()
    {
        // 1. External import blocked at compile/instantiate time
        CustomWidgetScriptCompileResult badImport = CustomWidgetScriptEngine.Compile(
            "import fs from 'fs';\nexport function onInit(ctx) {}");
        Assert.False(badImport.IsSuccess);

        // 2. Syntax error reports line number
        CustomWidgetScriptCompileResult syntaxErr = CustomWidgetScriptEngine.Compile(
            "export function ok() {}\nconst x = (\n;");
        Assert.False(syntaxErr.IsSuccess);
        Assert.NotNull(syntaxErr.ErrorLine);
        Assert.True(syntaxErr.ErrorLine >= 2);

        // 3. No fetch / XMLHttpRequest / WebSocket / process / require globals, and runtime error includes line number
        const string probeSource = """
            export function probeGlobals(ctx) {
              ctx.computed.hasFetch = typeof globalThis.fetch !== 'undefined';
              ctx.computed.hasXhr = typeof globalThis.XMLHttpRequest !== 'undefined';
              ctx.computed.hasWs = typeof globalThis.WebSocket !== 'undefined';
              ctx.computed.hasProcess = typeof globalThis.process !== 'undefined';
              ctx.computed.hasStd = typeof globalThis.std !== 'undefined';
              ctx.computed.hasOs = typeof globalThis.os !== 'undefined';
            }

            export function crashAtKnownLine() {
              const a = null;
              return a.nonExistentProperty;
            }
            """;

        CustomWidgetScriptCompileResult compile = CustomWidgetScriptEngine.Compile(probeSource);
        Assert.True(compile.IsSuccess, compile.ErrorMessage);

        CustomWidgetScriptExecutionResult probe = CustomWidgetScriptEngine.Execute(
            compile.Compiled!,
            "probeGlobals",
            new CustomWidgetScriptExecutionInput());
        Assert.True(probe.IsSuccess, probe.ErrorMessage);
        Assert.Equal("false", CustomWidgetScriptEngine.ResolveComputedToken("hasFetch", probe.Computed));
        Assert.Equal("false", CustomWidgetScriptEngine.ResolveComputedToken("hasXhr", probe.Computed));
        Assert.Equal("false", CustomWidgetScriptEngine.ResolveComputedToken("hasWs", probe.Computed));
        Assert.Equal("false", CustomWidgetScriptEngine.ResolveComputedToken("hasProcess", probe.Computed));
        Assert.Equal("false", CustomWidgetScriptEngine.ResolveComputedToken("hasStd", probe.Computed));
        Assert.Equal("false", CustomWidgetScriptEngine.ResolveComputedToken("hasOs", probe.Computed));

        CustomWidgetScriptExecutionResult crash = CustomWidgetScriptEngine.Execute(
            compile.Compiled!,
            "crashAtKnownLine",
            new CustomWidgetScriptExecutionInput());
        Assert.False(crash.IsSuccess);
        Assert.Equal(CustomWidgetScriptStatus.Error, crash.Status);
        Assert.Equal(12, crash.ErrorLine);
    }
}
