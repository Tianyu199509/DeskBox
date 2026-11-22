#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using System.Globalization;
using DeskBox.Models;
using DeskBox.Views;

namespace DeskBox.Services;

public sealed partial class WidgetManager
{
    private const string AotPersistenceOwnedFileWidgetId = "aot-5b4a-file";
    private const string AotPersistenceBaselineXMetadataKey = "Aot5B4B2ABaselineX";
    private const string AotPersistenceBaselineYMetadataKey = "Aot5B4B2ABaselineY";
    private const string AotPersistenceBaselineWidthMetadataKey = "Aot5B4B2ABaselineWidth";
    private const string AotPersistenceBaselineHeightMetadataKey = "Aot5B4B2ABaselineHeight";

    internal async Task ApplyAotPersistenceFileWidgetMutationAsync(
        string widgetId,
        string name,
        ViewMode viewMode,
        bool positionLocked,
        bool sizeLocked)
    {
        FileWidgetSession session = GetAotPersistenceFileSession(widgetId);
        WidgetConfig config = session.ViewModel.Config;
        AotPersistenceSmokePhysicalBounds baseline =
            session.Host.CaptureAotPersistenceSmokeBounds();
        StoreAotPersistenceBaseline(config, baseline);

        ApplyAotPersistenceTitle(session, name);
        ApplyAotPersistenceViewMode(session, viewMode);
        var requested = new AotPersistenceSmokePhysicalBounds(
            baseline.X + 24,
            baseline.Y + 20,
            baseline.Width + 40,
            baseline.Height + 32);
        session.Host.ApplyAotPersistenceSmokeBounds(requested);
        if (!SetWidgetPositionLocked(widgetId, positionLocked) ||
            !SetWidgetSizeLocked(widgetId, sizeLocked))
        {
            throw new InvalidOperationException(
                $"The owned file widget '{widgetId}' could not update its lock state.");
        }
        _settingsService.UpdateWidget(config, notifySubscribers: false);
        await Task.Yield();
    }

    internal async Task RestoreAotPersistenceFileWidgetBaselineAsync(
        string widgetId,
        string name,
        ViewMode viewMode,
        bool positionLocked,
        bool sizeLocked)
    {
        FileWidgetSession session = GetAotPersistenceFileSession(widgetId);
        WidgetConfig config = session.ViewModel.Config;
        AotPersistenceSmokePhysicalBounds baseline = ReadAotPersistenceBaseline(config);

        if (!SetWidgetPositionLocked(widgetId, positionLocked) ||
            !SetWidgetSizeLocked(widgetId, sizeLocked))
        {
            throw new InvalidOperationException(
                $"The owned file widget '{widgetId}' could not restore its lock state.");
        }

        session.Host.ApplyAotPersistenceSmokeBounds(baseline);
        ApplyAotPersistenceTitle(session, name);
        ApplyAotPersistenceViewMode(session, viewMode);
        RemoveAotPersistenceBaseline(config);
        _settingsService.UpdateWidget(config, notifySubscribers: false);
        await Task.Yield();
    }

    internal AotWidgetPersistenceSnapshot CaptureAotPersistenceWidgetSnapshot(
        string widgetId)
    {
        WidgetConfig config = FindConfig(widgetId) ??
            throw new InvalidOperationException(
                $"The owned widget configuration '{widgetId}' is unavailable.");
        IDesktopWidgetWindow? host = GetLoadedDesktopWindows().SingleOrDefault(window =>
            string.Equals(
                window.Identity.WidgetId,
                widgetId,
                StringComparison.Ordinal));
        FileWidgetSession? fileSession = _fileWidgets.TryGetValue(widgetId, out var loadedFile)
            ? loadedFile
            : null;
        Windows.Foundation.Rect actualBounds = host?.AnimationBounds ?? default;
        if (host is WidgetWindowBase widgetWindow)
        {
            AotPersistenceSmokePhysicalBounds physical =
                widgetWindow.CaptureAotPersistenceSmokeBounds();
            actualBounds = new Windows.Foundation.Rect(
                physical.X,
                physical.Y,
                physical.Width,
                physical.Height);
        }

        return new AotWidgetPersistenceSnapshot(
            config.Id,
            config.Name,
            config.WidgetKind.ToString(),
            config.ViewMode.ToString(),
            config.IsVisible,
            config.IsDisabled,
            config.IsPositionLocked,
            config.IsSizeLocked,
            config.X,
            config.Y,
            config.Width,
            config.Height,
            config.PositionAnchor,
            config.PositionMarginX,
            config.PositionMarginY,
            config.PositionMonitorKey,
            config.PositionMonitorDeviceName,
            config.PositionMonitorWasPrimary,
            config.BoundsCoordinateVersion,
            HasAotPersistenceBaseline(config),
            host is not null,
            host?.WindowHandle.ToInt64() ?? 0,
            host?.Visible == true,
            host?.WindowContentRoot?.XamlRoot is not null,
            new AotWidgetPersistenceBoundsSnapshot(
                actualBounds.X,
                actualBounds.Y,
                actualBounds.Width,
                actualBounds.Height),
            fileSession?.ViewModel.Name,
            fileSession?.ViewModel.ViewMode.ToString(),
            fileSession?.ViewModel.IsPositionLocked,
            fileSession?.ViewModel.IsSizeLocked);
    }

    private FileWidgetSession GetAotPersistenceFileSession(string widgetId)
    {
        if (!string.Equals(
                widgetId,
                AotPersistenceOwnedFileWidgetId,
                StringComparison.Ordinal) ||
            !_fileWidgets.TryGetValue(widgetId, out FileWidgetSession? session))
        {
            throw new InvalidOperationException(
                $"The owned file widget session '{widgetId}' is unavailable.");
        }

        return session;
    }

    private void ApplyAotPersistenceTitle(FileWidgetSession session, string name)
    {
        session.ViewModel.Name = name;
        session.ViewModel.Config.Name = name;
        session.ViewModel.Config.IsDefaultTitle = false;
        _settingsService.UpdateWidget(session.ViewModel.Config, notifySubscribers: false);
    }

    private static void ApplyAotPersistenceViewMode(
        FileWidgetSession session,
        ViewMode viewMode)
    {
        if (session.ViewModel.ViewMode != viewMode)
        {
            session.ViewModel.ToggleViewMode();
        }

        if (session.ViewModel.ViewMode != viewMode ||
            session.ViewModel.Config.ViewMode != viewMode)
        {
            throw new InvalidOperationException(
                $"The file widget view mode did not reach '{viewMode}'.");
        }
    }

    private static void StoreAotPersistenceBaseline(
        WidgetConfig config,
        AotPersistenceSmokePhysicalBounds bounds)
    {
        config.Metadata[AotPersistenceBaselineXMetadataKey] =
            bounds.X.ToString(CultureInfo.InvariantCulture);
        config.Metadata[AotPersistenceBaselineYMetadataKey] =
            bounds.Y.ToString(CultureInfo.InvariantCulture);
        config.Metadata[AotPersistenceBaselineWidthMetadataKey] =
            bounds.Width.ToString(CultureInfo.InvariantCulture);
        config.Metadata[AotPersistenceBaselineHeightMetadataKey] =
            bounds.Height.ToString(CultureInfo.InvariantCulture);
    }

    private static AotPersistenceSmokePhysicalBounds ReadAotPersistenceBaseline(
        WidgetConfig config)
    {
        return new AotPersistenceSmokePhysicalBounds(
            ReadAotPersistenceBaselineValue(config, AotPersistenceBaselineXMetadataKey),
            ReadAotPersistenceBaselineValue(config, AotPersistenceBaselineYMetadataKey),
            ReadAotPersistenceBaselineValue(config, AotPersistenceBaselineWidthMetadataKey),
            ReadAotPersistenceBaselineValue(config, AotPersistenceBaselineHeightMetadataKey));
    }

    private static int ReadAotPersistenceBaselineValue(
        WidgetConfig config,
        string key)
    {
        if (!config.Metadata.TryGetValue(key, out string? value) ||
            !int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsed))
        {
            throw new InvalidOperationException(
                $"The owned widget is missing baseline metadata '{key}'.");
        }

        return parsed;
    }

    private static bool HasAotPersistenceBaseline(WidgetConfig config)
    {
        return config.Metadata.ContainsKey(AotPersistenceBaselineXMetadataKey) &&
            config.Metadata.ContainsKey(AotPersistenceBaselineYMetadataKey) &&
            config.Metadata.ContainsKey(AotPersistenceBaselineWidthMetadataKey) &&
            config.Metadata.ContainsKey(AotPersistenceBaselineHeightMetadataKey);
    }

    private static void RemoveAotPersistenceBaseline(WidgetConfig config)
    {
        config.Metadata.Remove(AotPersistenceBaselineXMetadataKey);
        config.Metadata.Remove(AotPersistenceBaselineYMetadataKey);
        config.Metadata.Remove(AotPersistenceBaselineWidthMetadataKey);
        config.Metadata.Remove(AotPersistenceBaselineHeightMetadataKey);
    }
}

internal sealed record AotWidgetPersistenceSnapshot(
    string Id,
    string Name,
    string WidgetKind,
    string ViewMode,
    bool IsVisible,
    bool IsDisabled,
    bool IsPositionLocked,
    bool IsSizeLocked,
    double X,
    double Y,
    double Width,
    double Height,
    string? PositionAnchor,
    double PositionMarginX,
    double PositionMarginY,
    string? PositionMonitorKey,
    string? PositionMonitorDeviceName,
    bool? PositionMonitorWasPrimary,
    int BoundsCoordinateVersion,
    bool HasBaselineMetadata,
    bool IsLoaded,
    long WindowHandle,
    bool IsHostVisible,
    bool HasXamlRoot,
    AotWidgetPersistenceBoundsSnapshot ActualBounds,
    string? ViewModelName,
    string? ViewModelViewMode,
    bool? ViewModelPositionLocked,
    bool? ViewModelSizeLocked);

internal sealed record AotWidgetPersistenceBoundsSnapshot(
    double X,
    double Y,
    double Width,
    double Height);
#endif
