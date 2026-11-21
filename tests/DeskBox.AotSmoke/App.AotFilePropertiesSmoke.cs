#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using System.Security.Cryptography;
using DeskBox.Controls.WidgetContents;
using DeskBox.Services;

namespace DeskBox;

public partial class App
{
    private async Task CaptureAotManagedUiFilePropertiesAsync(
        AotManagedUiSmokeResult result)
    {
        AotManagedUiFilePropertiesEvidence evidence = result.FileProperties ??
            throw new InvalidOperationException(
                "The file Properties evidence container is unavailable.");
        AotFilePropertiesFixturePaths paths =
            AotFilePropertiesFixture.GetOwnedPaths(
                DeskBoxDataPathService.Current);
        WidgetManager manager = WidgetManager ??
            throw new InvalidOperationException("WidgetManager is unavailable.");
        AotLocalFileSurfaceHost host =
            await manager.GetAotLocalFileSurfaceHostAsync(
                AotFilePropertiesFixture.OwnedWidgetId);
        AotLocalFileSurfaceSnapshot surface =
            await host.Surface.WaitForAotLocalFileSurfaceAsync(
                paths.WidgetRoot,
                [paths.TargetName],
                expectAtMappedRoot: true);

        evidence.RunId = paths.RunId;
        evidence.TargetName = paths.TargetName;
        evidence.TargetPath = paths.TargetPath;
        evidence.TargetLengthBefore = new FileInfo(paths.TargetPath).Length;
        evidence.TargetSha256Before = HashAotFilePropertiesFile(
            paths.TargetPath);
        evidence.Surface = MapAotLocalFileSurface(surface);
        evidence.HostWindowHandle = host.WindowHandle;
        evidence.HostHasXamlRoot = host.HasXamlRoot;
        evidence.HostVisible = host.Visible;

        RequireAotManagedUi(
            result,
            host.WindowHandle != 0 &&
            host.HasXamlRoot &&
            host.Visible &&
            surface.Items.Count == 1 &&
            string.Equals(
                surface.Items[0].Name,
                paths.TargetName,
                StringComparison.Ordinal) &&
            IsAotManagedUiPathEqual(
                surface.Items[0].Path,
                paths.TargetPath) &&
            evidence.TargetLengthBefore > 0 &&
            !string.IsNullOrWhiteSpace(evidence.TargetSha256Before),
            "FilePropertiesOwnedBaselineVerified",
            "The real owned File Widget target was not loaded with a non-zero host HWND.");

        AotFilePropertiesMenuInvocationSnapshot menu =
            await host.Surface.InvokeAotFilePropertiesAsync(
                paths.TargetName);
        evidence.Menu = MapAotFilePropertiesMenu(menu);

        RequireAotManagedUi(
            result,
            menu.AutomationInvoked &&
            menu.PropertiesEnabled &&
            menu.PropertiesIndex >= 0 &&
            menu.MenuItemCount > menu.PropertiesIndex &&
            !string.IsNullOrWhiteSpace(menu.PropertiesText) &&
            string.IsNullOrEmpty(menu.FeedbackKey) &&
            string.IsNullOrEmpty(menu.FeedbackSeverity) &&
            string.IsNullOrEmpty(menu.FeedbackMessage) &&
            menu.HostWindowHandle == host.WindowHandle &&
            string.Equals(
                menu.TargetName,
                paths.TargetName,
                StringComparison.Ordinal) &&
            IsAotManagedUiPathEqual(
                menu.TargetPath,
                paths.TargetPath) &&
            menu.Items.Count(item => item.IsProperties) == 1 &&
            menu.Items.Single(item => item.IsProperties).Index ==
                menu.PropertiesIndex,
            "FilePropertiesMenuInvoked",
            "The real product Properties menu did not invoke the exact owned target without product feedback errors.");

        RequireAotManagedUi(
            result,
            menu.Invocation.ResultRecorded &&
            menu.Invocation.Invoked &&
            string.IsNullOrEmpty(menu.Invocation.Error) &&
            menu.Invocation.OwnerWindowHandle == host.WindowHandle &&
            IsAotManagedUiPathEqual(
                menu.Invocation.TargetPath,
                paths.TargetPath) &&
            menu.Invocation.ReturnedAtUtc is not null &&
            menu.Invocation.ReturnedAtUtc >= menu.Invocation.StartedAtUtc,
            "FilePropertiesInvocationVerified",
            "SHObjectProperties did not receive the exact path and real File Widget owner or did not return success.");

        RequireAotManagedUi(
            result,
            menu.Dialog.WindowHandle != 0 &&
            menu.Dialog.ExpectedOwnerWindowHandle == host.WindowHandle &&
            menu.Dialog.ExpectedOwner.WindowHandle == host.WindowHandle &&
            menu.Dialog.ExpectedOwner.IsWindow &&
            menu.Dialog.DirectOwnerWindowHandle != 0 &&
            menu.Dialog.DirectOwnerWindowHandle != menu.Dialog.WindowHandle &&
            menu.Dialog.DirectOwner.IsWindow &&
            menu.Dialog.DirectOwner.WindowHandle ==
                menu.Dialog.DirectOwnerWindowHandle &&
            menu.Dialog.RootOwnerWindowHandle != 0 &&
            menu.Dialog.RootOwnerWindowHandle != menu.Dialog.WindowHandle &&
            menu.Dialog.RootOwner.IsWindow &&
            menu.Dialog.RootOwner.WindowHandle ==
                menu.Dialog.RootOwnerWindowHandle &&
            menu.Dialog.WindowThreadId != 0 &&
            menu.Dialog.ProcessId != 0 &&
            string.Equals(
                menu.Dialog.ClassName,
                "#32770",
                StringComparison.OrdinalIgnoreCase) &&
            menu.Dialog.Title.Contains(
                paths.TargetName,
                StringComparison.OrdinalIgnoreCase) &&
            menu.Dialog.VisibleBeforeClose,
            "FilePropertiesDialogObserved",
            "The real system Properties dialog did not expose the unique target title and valid observed owner windows.");

        RequireAotManagedUi(
            result,
            menu.Dialog.ClosePosted &&
            menu.Dialog.WindowDestroyedAfterClose &&
            menu.Dialog.ClosedAtUtc is not null &&
            menu.Dialog.ClosedAtUtc >= menu.Dialog.ObservedAtUtc &&
            menu.RemainingMatchingDialogCount == 0,
            "FilePropertiesDialogClosed",
            "The owned system Properties dialog was not closed cleanly or remained visible.");

        evidence.TargetExistsAfter = File.Exists(paths.TargetPath);
        evidence.TargetLengthAfter = evidence.TargetExistsAfter
            ? new FileInfo(paths.TargetPath).Length
            : -1;
        evidence.TargetSha256After = evidence.TargetExistsAfter
            ? HashAotFilePropertiesFile(paths.TargetPath)
            : string.Empty;
        RequireAotManagedUi(
            result,
            evidence.TargetExistsAfter &&
            evidence.TargetLengthAfter == evidence.TargetLengthBefore &&
            string.Equals(
                evidence.TargetSha256After,
                evidence.TargetSha256Before,
                StringComparison.OrdinalIgnoreCase),
            "FilePropertiesPostflightVerified",
            "The read-only Properties operation changed or removed the owned target.");
    }

    private static AotManagedUiFilePropertiesMenuEvidence
        MapAotFilePropertiesMenu(
            AotFilePropertiesMenuInvocationSnapshot menu)
    {
        return new AotManagedUiFilePropertiesMenuEvidence
        {
            TargetName = menu.TargetName,
            TargetPath = menu.TargetPath,
            HostWindowHandle = menu.HostWindowHandle,
            MenuItemCount = menu.MenuItemCount,
            PropertiesIndex = menu.PropertiesIndex,
            PropertiesText = menu.PropertiesText,
            PropertiesEnabled = menu.PropertiesEnabled,
            AutomationInvoked = menu.AutomationInvoked,
            FeedbackKey = menu.FeedbackKey,
            FeedbackSeverity = menu.FeedbackSeverity,
            FeedbackMessage = menu.FeedbackMessage,
            RemainingMatchingDialogCount =
                menu.RemainingMatchingDialogCount,
            Invocation = new AotManagedUiFilePropertiesInvocationEvidence
            {
                OwnerWindowHandle = menu.Invocation.OwnerWindowHandle,
                TargetPath = menu.Invocation.TargetPath,
                Invoked = menu.Invocation.Invoked,
                ResultRecorded = menu.Invocation.ResultRecorded,
                Error = menu.Invocation.Error,
                StartedAtUtc = menu.Invocation.StartedAtUtc,
                ReturnedAtUtc = menu.Invocation.ReturnedAtUtc
            },
            Dialog = new AotManagedUiFilePropertiesDialogEvidence
            {
                WindowHandle = menu.Dialog.WindowHandle,
                DirectOwnerWindowHandle =
                    menu.Dialog.DirectOwnerWindowHandle,
                RootOwnerWindowHandle =
                    menu.Dialog.RootOwnerWindowHandle,
                ExpectedOwnerWindowHandle =
                    menu.Dialog.ExpectedOwnerWindowHandle,
                WindowThreadId = menu.Dialog.WindowThreadId,
                ProcessId = menu.Dialog.ProcessId,
                ClassName = menu.Dialog.ClassName,
                Title = menu.Dialog.Title,
                VisibleBeforeClose = menu.Dialog.VisibleBeforeClose,
                ClosePosted = menu.Dialog.ClosePosted,
                WindowDestroyedAfterClose =
                    menu.Dialog.WindowDestroyedAfterClose,
                ObservedAtUtc = menu.Dialog.ObservedAtUtc,
                ClosedAtUtc = menu.Dialog.ClosedAtUtc,
                ExpectedOwner = MapAotFilePropertiesObservedWindow(
                    menu.Dialog.ExpectedOwner),
                DirectOwner = MapAotFilePropertiesObservedWindow(
                    menu.Dialog.DirectOwner),
                RootOwner = MapAotFilePropertiesObservedWindow(
                    menu.Dialog.RootOwner)
            },
            Items = menu.Items
                .Select(item => new AotManagedUiFilePropertiesMenuItemEvidence
                {
                    Index = item.Index,
                    ItemType = item.ItemType,
                    Text = item.Text,
                    IsEnabled = item.IsEnabled,
                    IsProperties = item.IsProperties
                })
                .ToList()
        };
    }

    private static AotManagedUiFilePropertiesObservedWindowEvidence
        MapAotFilePropertiesObservedWindow(
            AotFilePropertiesObservedWindowSnapshot window)
    {
        return new AotManagedUiFilePropertiesObservedWindowEvidence
        {
            WindowHandle = window.WindowHandle,
            IsWindow = window.IsWindow,
            Visible = window.Visible,
            WindowThreadId = window.WindowThreadId,
            ProcessId = window.ProcessId,
            ClassName = window.ClassName,
            Title = window.Title
        };
    }

    private static string HashAotFilePropertiesFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

internal sealed class AotManagedUiFilePropertiesEvidence
{
    public bool NormalShutdownRequested { get; set; }
    public string RunId { get; set; } = string.Empty;
    public string TargetName { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public long TargetLengthBefore { get; set; }
    public string TargetSha256Before { get; set; } = string.Empty;
    public long HostWindowHandle { get; set; }
    public bool HostHasXamlRoot { get; set; }
    public bool HostVisible { get; set; }
    public AotManagedUiLocalFileSurfaceEvidence Surface { get; set; } = new();
    public AotManagedUiFilePropertiesMenuEvidence Menu { get; set; } = new();
    public bool TargetExistsAfter { get; set; }
    public long TargetLengthAfter { get; set; }
    public string TargetSha256After { get; set; } = string.Empty;
}

internal sealed class AotManagedUiFilePropertiesMenuEvidence
{
    public string TargetName { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public long HostWindowHandle { get; set; }
    public int MenuItemCount { get; set; }
    public int PropertiesIndex { get; set; }
    public string PropertiesText { get; set; } = string.Empty;
    public bool PropertiesEnabled { get; set; }
    public bool AutomationInvoked { get; set; }
    public string FeedbackKey { get; set; } = string.Empty;
    public string FeedbackSeverity { get; set; } = string.Empty;
    public string FeedbackMessage { get; set; } = string.Empty;
    public int RemainingMatchingDialogCount { get; set; }
    public AotManagedUiFilePropertiesInvocationEvidence Invocation { get; set; } = new();
    public AotManagedUiFilePropertiesDialogEvidence Dialog { get; set; } = new();
    public List<AotManagedUiFilePropertiesMenuItemEvidence> Items { get; set; } = [];
}

internal sealed class AotManagedUiFilePropertiesInvocationEvidence
{
    public long OwnerWindowHandle { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    public bool Invoked { get; set; }
    public bool ResultRecorded { get; set; }
    public string Error { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? ReturnedAtUtc { get; set; }
}

internal sealed class AotManagedUiFilePropertiesDialogEvidence
{
    public long WindowHandle { get; set; }
    public long DirectOwnerWindowHandle { get; set; }
    public long RootOwnerWindowHandle { get; set; }
    public long ExpectedOwnerWindowHandle { get; set; }
    public uint WindowThreadId { get; set; }
    public uint ProcessId { get; set; }
    public string ClassName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool VisibleBeforeClose { get; set; }
    public bool ClosePosted { get; set; }
    public bool WindowDestroyedAfterClose { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public DateTimeOffset? ClosedAtUtc { get; set; }
    public AotManagedUiFilePropertiesObservedWindowEvidence ExpectedOwner { get; set; } = new();
    public AotManagedUiFilePropertiesObservedWindowEvidence DirectOwner { get; set; } = new();
    public AotManagedUiFilePropertiesObservedWindowEvidence RootOwner { get; set; } = new();
}

internal sealed class AotManagedUiFilePropertiesObservedWindowEvidence
{
    public long WindowHandle { get; set; }
    public bool IsWindow { get; set; }
    public bool Visible { get; set; }
    public uint WindowThreadId { get; set; }
    public uint ProcessId { get; set; }
    public string ClassName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
}

internal sealed class AotManagedUiFilePropertiesMenuItemEvidence
{
    public int Index { get; set; }
    public string ItemType { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool IsProperties { get; set; }
}
#endif
