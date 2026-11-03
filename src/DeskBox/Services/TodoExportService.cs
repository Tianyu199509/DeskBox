using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Services;

public enum TodoExportStatusScope
{
    All,
    CompletedOnly,
    ActiveOnly
}

public enum TodoExportDatePreset
{
    AllTime,
    ThisWeek,
    LastWeek,
    ThisMonth,
    LastMonth,
    ThisQuarter,
    ThisYear,
    CustomRange
}

public enum TodoExportDateBasis
{
    ActiveInPeriod,
    CompletedAt,
    CreatedAt,
    DueDate
}

public enum TodoExportFormat
{
    Xlsx,
    Csv
}

public sealed record TodoExportOptions
{
    public TodoExportStatusScope StatusScope { get; init; } = TodoExportStatusScope.All;

    public TodoExportDatePreset DatePreset { get; init; } = TodoExportDatePreset.AllTime;

    public TodoExportDateBasis DateBasis { get; init; } = TodoExportDateBasis.ActiveInPeriod;

    public DateOnly? CustomStartDate { get; init; }

    public DateOnly? CustomEndDate { get; init; }

    public IReadOnlySet<string>? SelectedColors { get; init; }

    public TodoExportFormat Format { get; init; } = TodoExportFormat.Xlsx;

    public TodoExportDateBasis EffectiveDateBasis =>
        StatusScope == TodoExportStatusScope.CompletedOnly
            ? TodoExportDateBasis.CompletedAt
            : DateBasis;
}

public sealed record TodoExportPreview(
    IReadOnlyList<TodoItem> Items,
    int TotalCount,
    int CompletedCount,
    int ActiveCount,
    int ImportantCount,
    int OverdueCount,
    DateOnly? RangeStart,
    DateOnly? RangeEnd);

public static class TodoExportService
{
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ContentTypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";

    private static readonly UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding s_utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    public static async Task<IReadOnlyList<TodoItem>> LoadPrimaryTodoItemsAsync(
        string dataDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory) || !Directory.Exists(dataDirectory))
        {
            return [];
        }

        string widgetsRoot = Path.Combine(dataDirectory, "widgets");
        if (!Directory.Exists(widgetsRoot))
        {
            return [];
        }

        string? widgetId = await TryResolvePrimaryTodoWidgetIdAsync(dataDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(widgetId))
        {
            // Fallback: locate the first widget directory that contains todo.json.
            foreach (string candidateDir in Directory.EnumerateDirectories(widgetsRoot))
            {
                if (File.Exists(Path.Combine(candidateDir, "todo.json")))
                {
                    widgetId = Path.GetFileName(candidateDir);
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(widgetId))
        {
            return [];
        }

        var store = new TodoWidgetStore(widgetsRoot, widgetId);
        TodoWidgetData snapshot = await store.LoadReadOnlySnapshotAsync().ConfigureAwait(false);
        return snapshot.Items
            .Where(item => !item.IsDeleted)
            .ToArray();
    }

    public static (DateOnly? Start, DateOnly? End) ResolveDateRange(
        TodoExportOptions options,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(options);
        DateOnly today = DateOnly.FromDateTime(now.ToLocalTime().Date);

        return options.DatePreset switch
        {
            TodoExportDatePreset.AllTime => (null, null),
            TodoExportDatePreset.ThisWeek => GetWeekRange(today, weekOffset: 0),
            TodoExportDatePreset.LastWeek => GetWeekRange(today, weekOffset: -1),
            TodoExportDatePreset.ThisMonth => GetMonthRange(today.Year, today.Month),
            TodoExportDatePreset.LastMonth => GetPreviousMonthRange(today),
            TodoExportDatePreset.ThisQuarter => GetQuarterRange(today),
            TodoExportDatePreset.ThisYear => (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31)),
            TodoExportDatePreset.CustomRange => NormalizeCustomRange(options.CustomStartDate, options.CustomEndDate, today),
            _ => (null, null)
        };
    }

    public static TodoExportPreview BuildPreview(
        IEnumerable<TodoItem> sourceItems,
        TodoExportOptions options,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sourceItems);
        ArgumentNullException.ThrowIfNull(options);

        (DateOnly? rangeStart, DateOnly? rangeEnd) = ResolveDateRange(options, now);
        TodoExportDateBasis effectiveBasis = options.EffectiveDateBasis;
        bool filterByColor = options.SelectedColors is { Count: > 0 };

        var matched = new List<TodoItem>();
        foreach (TodoItem item in sourceItems)
        {
            if (item.IsDeleted || string.IsNullOrWhiteSpace(item.Text))
            {
                continue;
            }

            if (options.StatusScope == TodoExportStatusScope.CompletedOnly && !item.IsCompleted)
            {
                continue;
            }

            if (options.StatusScope == TodoExportStatusScope.ActiveOnly && item.IsCompleted)
            {
                continue;
            }

            if (filterByColor)
            {
                string? normalizedColor = TodoItem.NormalizeColorMarker(item.ColorMarker);
                if (normalizedColor is null || !options.SelectedColors!.Contains(normalizedColor))
                {
                    continue;
                }
            }

            if (rangeStart is { } start && rangeEnd is { } end &&
                !MatchesDateRange(item, effectiveBasis, start, end))
            {
                continue;
            }

            matched.Add(item);
        }

        List<TodoItem> ordered = OrderItemsForExport(matched, options.StatusScope);
        int completed = 0;
        int active = 0;
        int important = 0;
        int overdue = 0;

        foreach (TodoItem item in ordered)
        {
            if (item.IsCompleted)
            {
                completed++;
            }
            else
            {
                active++;
                if (IsItemOverdue(item, now))
                {
                    overdue++;
                }
            }

            if (item.IsImportant)
            {
                important++;
            }
        }

        return new TodoExportPreview(
            ordered,
            ordered.Count,
            completed,
            active,
            important,
            overdue,
            rangeStart,
            rangeEnd);
    }

    public static string BuildSuggestedFileName(
        TodoExportOptions options,
        LocalizationService localizationService,
        DateTimeOffset now,
        bool isSelectionSubset = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(localizationService);

        string prefix = SanitizeFileSegment(localizationService.T("Todo.Export.FilePrefix"));
        if (string.IsNullOrWhiteSpace(prefix))
        {
            prefix = "DeskBox_Todo";
        }

        DateTime localNow = now.ToLocalTime().DateTime;
        if (isSelectionSubset)
        {
            string selectedTag = SanitizeFileSegment(localizationService.T("Todo.Export.Scope.SelectedShort"));
            return $"{prefix}_{selectedTag}_{localNow:yyyyMMdd}";
        }

        string scopePart = options.StatusScope switch
        {
            TodoExportStatusScope.CompletedOnly => SanitizeFileSegment(localizationService.T("Todo.Export.Scope.Completed")),
            TodoExportStatusScope.ActiveOnly => SanitizeFileSegment(localizationService.T("Todo.Export.Scope.Active")),
            _ => SanitizeFileSegment(localizationService.T("Todo.Export.Scope.All"))
        };

        (DateOnly? start, DateOnly? end) = ResolveDateRange(options, now);
        string datePart = options.DatePreset switch
        {
            TodoExportDatePreset.ThisMonth when start is { } s => $"{s:yyyy-MM}",
            TodoExportDatePreset.LastMonth when start is { } s => $"{s:yyyy-MM}",
            TodoExportDatePreset.ThisYear when start is { } s => $"{s.Year}",
            _ when start is { } s && end is { } e => $"{s:yyyyMMdd}-{e:yyyyMMdd}",
            _ => $"{localNow:yyyyMMdd}"
        };

        return $"{prefix}_{scopePart}_{datePart}";
    }

    public static async Task ExportToFileAsync(
        string destinationPath,
        IReadOnlyList<TodoItem> items,
        TodoExportOptions options,
        LocalizationService localizationService,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(localizationService);

        string fullPath = Path.GetFullPath(destinationPath);
        TodoExportFormat effectiveFormat = string.Equals(
            Path.GetExtension(fullPath),
            ".csv",
            StringComparison.OrdinalIgnoreCase)
            ? TodoExportFormat.Csv
            : string.Equals(Path.GetExtension(fullPath), ".xlsx", StringComparison.OrdinalIgnoreCase)
                ? TodoExportFormat.Xlsx
                : options.Format;

        string tempPath = TodoExportFileOps.CreateStagedExportPath(fullPath);

        try
        {
            await using (var fileStream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16384,
                useAsync: true))
            {
                if (effectiveFormat == TodoExportFormat.Csv)
                {
                    await WriteCsvAsync(fileStream, items, localizationService, now, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    WriteXlsx(fileStream, items, options, localizationService, now);
                    await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            TodoExportFileOps.CommitStagedExport(tempPath, fullPath);
        }
        finally
        {
            TodoExportFileOps.TryDeleteStagedExport(tempPath);
        }
    }

    public static void WriteXlsx(
        Stream output,
        IReadOnlyList<TodoItem> items,
        TodoExportOptions options,
        LocalizationService localizationService,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(localizationService);

        string detailsSheetName = SanitizeSheetName(
            localizationService.T("Todo.Export.Sheet.Details"),
            "Tasks");
        string summarySheetName = SanitizeSheetName(
            localizationService.T("Todo.Export.Sheet.Summary"),
            "Summary");
        if (string.Equals(detailsSheetName, summarySheetName, StringComparison.OrdinalIgnoreCase))
        {
            summarySheetName = SanitizeSheetName($"{summarySheetName}_2", "Summary_2");
        }

        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        WriteXmlEntry(archive, "[Content_Types].xml", WriteContentTypesXml);
        WriteXmlEntry(archive, "_rels/.rels", WriteRootRelationshipsXml);
        WriteXmlEntry(archive, "xl/workbook.xml", writer =>
            WriteWorkbookXml(writer, detailsSheetName, summarySheetName));
        WriteXmlEntry(archive, "xl/_rels/workbook.xml.rels", WriteWorkbookRelationshipsXml);
        WriteXmlEntry(archive, "xl/styles.xml", WriteStylesXml);
        WriteXmlEntry(archive, "xl/worksheets/sheet1.xml", writer =>
            WriteDetailsSheetXml(writer, items, localizationService, now));
        WriteXmlEntry(archive, "xl/worksheets/sheet2.xml", writer =>
            WriteSummarySheetXml(writer, items, options, localizationService, now));
    }

    public static async Task WriteCsvAsync(
        Stream output,
        IReadOnlyList<TodoItem> items,
        LocalizationService localizationService,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(localizationService);

        await using var writer = new StreamWriter(output, s_utf8WithBom, bufferSize: 8192, leaveOpen: true);

        string[] headers = GetDetailColumnHeaders(localizationService);
        await writer.WriteLineAsync(JoinCsvRow(headers, sanitizeFormulas: false)).ConfigureAwait(false);

        string completedLabel = localizationService.T("Todo.Export.Value.Completed");
        string activeLabel = localizationService.T("Todo.Export.Value.Active");
        string importantLabel = localizationService.T("Todo.Export.Value.ImportantStar");
        string overdueLabel = localizationService.T("Todo.Due.OverdueSuffix");

        foreach (TodoItem item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string[] row =
            [
                NormalizeNewLines(item.Text.Trim()),
                item.IsCompleted ? completedLabel : activeLabel,
                item.IsImportant ? importantLabel : string.Empty,
                FormatColorName(item.ColorMarker, localizationService),
                FormatDueDate(item),
                FormatOverdueAndRecurrence(item, now, overdueLabel, localizationService),
                FormatStepsCell(item.Steps),
                NormalizeNewLines(item.Notes?.Trim() ?? string.Empty),
                FormatTimestamp(item.CreatedAt),
                item.IsCompleted ? FormatTimestamp(item.CompletedAt ?? item.UpdatedAt) : string.Empty,
                FormatAttachmentsCell(item.Attachments)
            ];

            await writer.WriteLineAsync(JoinCsvRow(row, sanitizeFormulas: true)).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static bool IsItemOverdue(TodoItem item, DateTimeOffset now)
    {
        if (item.IsCompleted || item.DueDate is not { } dueDate)
        {
            return false;
        }

        if (!item.HasDueTime)
        {
            return dueDate.ToLocalTime().Date < now.ToLocalTime().Date;
        }

        return dueDate < now;
    }

    private static bool MatchesDateRange(
        TodoItem item,
        TodoExportDateBasis basis,
        DateOnly start,
        DateOnly end)
    {
        return basis switch
        {
            TodoExportDateBasis.CompletedAt =>
                item.IsCompleted &&
                IsDateWithin((item.CompletedAt ?? item.UpdatedAt).ToLocalTime().Date, start, end),
            TodoExportDateBasis.CreatedAt =>
                IsDateWithin(item.CreatedAt.ToLocalTime().Date, start, end),
            TodoExportDateBasis.DueDate =>
                item.DueDate is { } due &&
                IsDateWithin(due.ToLocalTime().Date, start, end),
            _ =>
                IsDateWithin(item.CreatedAt.ToLocalTime().Date, start, end) ||
                (item.DueDate is { } dueAny && IsDateWithin(dueAny.ToLocalTime().Date, start, end)) ||
                (item.IsCompleted && IsDateWithin((item.CompletedAt ?? item.UpdatedAt).ToLocalTime().Date, start, end))
        };
    }

    private static bool IsDateWithin(DateTime localDate, DateOnly start, DateOnly end)
    {
        DateOnly candidate = DateOnly.FromDateTime(localDate);
        return candidate >= start && candidate <= end;
    }

    private static List<TodoItem> OrderItemsForExport(
        List<TodoItem> items,
        TodoExportStatusScope scope)
    {
        if (scope == TodoExportStatusScope.CompletedOnly)
        {
            return items
                .OrderByDescending(item => item.CompletedAt ?? item.UpdatedAt)
                .ThenBy(item => item.SortOrder)
                .ToList();
        }

        return items
            .OrderBy(item => item.IsCompleted ? 1 : 0)
            .ThenBy(item => item.IsCompleted ? 0 : item.SortOrder)
            .ThenByDescending(item => item.IsCompleted ? (item.CompletedAt ?? item.UpdatedAt) : DateTimeOffset.MinValue)
            .ToList();
    }

    private static (DateOnly Start, DateOnly End) GetWeekRange(DateOnly today, int weekOffset)
    {
        int diffFromMonday = ((int)today.DayOfWeek + 6) % 7;
        DateOnly monday = today.AddDays(-diffFromMonday + (weekOffset * 7));
        return (monday, monday.AddDays(6));
    }

    private static (DateOnly Start, DateOnly End) GetMonthRange(int year, int month)
    {
        var start = new DateOnly(year, month, 1);
        var end = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        return (start, end);
    }

    private static (DateOnly Start, DateOnly End) GetPreviousMonthRange(DateOnly today)
    {
        DateOnly prev = today.AddMonths(-1);
        return GetMonthRange(prev.Year, prev.Month);
    }

    private static (DateOnly Start, DateOnly End) GetQuarterRange(DateOnly today)
    {
        int quarterStartMonth = (((today.Month - 1) / 3) * 3) + 1;
        var start = new DateOnly(today.Year, quarterStartMonth, 1);
        DateOnly endMonth = start.AddMonths(2);
        var end = new DateOnly(endMonth.Year, endMonth.Month, DateTime.DaysInMonth(endMonth.Year, endMonth.Month));
        return (start, end);
    }

    private static (DateOnly Start, DateOnly End) NormalizeCustomRange(
        DateOnly? start,
        DateOnly? end,
        DateOnly fallbackToday)
    {
        DateOnly resolvedStart = start ?? new DateOnly(fallbackToday.Year, fallbackToday.Month, 1);
        DateOnly resolvedEnd = end ?? fallbackToday;
        return resolvedStart <= resolvedEnd
            ? (resolvedStart, resolvedEnd)
            : (resolvedEnd, resolvedStart);
    }

    private static string[] GetDetailColumnHeaders(LocalizationService loc) =>
    [
        loc.T("Todo.Export.Col.Title"),
        loc.T("Todo.Export.Col.Status"),
        loc.T("Todo.Export.Col.Important"),
        loc.T("Todo.Export.Col.ColorTag"),
        loc.T("Todo.Export.Col.DueDate"),
        loc.T("Todo.Export.Col.OverdueOrRepeat"),
        loc.T("Todo.Export.Col.Steps"),
        loc.T("Todo.Export.Col.Notes"),
        loc.T("Todo.Export.Col.CreatedAt"),
        loc.T("Todo.Export.Col.CompletedAt"),
        loc.T("Todo.Export.Col.Attachments")
    ];

    private static string FormatColorName(string? colorMarker, LocalizationService loc)
    {
        string? normalized = TodoItem.NormalizeColorMarker(colorMarker);
        if (normalized is null)
        {
            return string.Empty;
        }

        return loc.T(TodoItem.GetColorMarkerLocalizationKey(normalized));
    }

    private static int GetColorCellStyleIndex(string? colorMarker)
    {
        return TodoItem.NormalizeColorMarker(colorMarker) switch
        {
            TodoItem.RedColorMarker => 10,
            TodoItem.OrangeColorMarker => 11,
            TodoItem.YellowColorMarker => 12,
            TodoItem.GreenColorMarker => 13,
            TodoItem.BlueColorMarker => 14,
            TodoItem.PurpleColorMarker => 15,
            TodoItem.TealColorMarker => 16,
            TodoItem.PinkColorMarker => 17,
            _ => 2
        };
    }

    private static string FormatDueDate(TodoItem item)
    {
        if (item.DueDate is not { } due)
        {
            return string.Empty;
        }

        DateTimeOffset local = due.ToLocalTime();
        return item.HasDueTime
            ? local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string FormatOverdueAndRecurrence(
        TodoItem item,
        DateTimeOffset now,
        string overdueLabel,
        LocalizationService loc)
    {
        bool overdue = IsItemOverdue(item, now);
        string recurrence = FormatRecurrence(item.Recurrence, loc);

        if (overdue && !string.IsNullOrEmpty(recurrence))
        {
            return $"{overdueLabel} · {recurrence}";
        }

        return overdue ? overdueLabel : recurrence;
    }

    private static string FormatRecurrence(TodoRecurrence? recurrence, LocalizationService loc)
    {
        if (recurrence is null)
        {
            return string.Empty;
        }

        return TodoRecurrenceMode.Normalize(recurrence.Mode) switch
        {
            TodoRecurrenceMode.Daily => loc.T("Todo.Recurrence.Daily"),
            TodoRecurrenceMode.Weekdays => loc.T("Todo.Recurrence.Weekdays"),
            TodoRecurrenceMode.Weekly => loc.T("Todo.Recurrence.Weekly"),
            TodoRecurrenceMode.Monthly => loc.T("Todo.Recurrence.Monthly"),
            _ => string.Empty
        };
    }

    private static string FormatStepsCell(List<TodoStep>? steps)
    {
        if (steps is not { Count: > 0 })
        {
            return string.Empty;
        }

        int done = steps.Count(s => s.IsCompleted);
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"{done}/{steps.Count}");

        foreach (TodoStep step in steps.OrderBy(s => s.SortOrder))
        {
            string stepText = step.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(stepText))
            {
                continue;
            }

            builder.Append('\n');
            builder.Append(step.IsCompleted ? "[✓] " : "[ ] ");
            builder.Append(NormalizeSingleLine(stepText));
        }

        return builder.ToString();
    }

    private static string FormatAttachmentsCell(List<TodoAttachment>? attachments)
    {
        if (attachments is not { Count: > 0 })
        {
            return string.Empty;
        }

        return string.Join(
            "\n",
            attachments
                .Select(a => !string.IsNullOrWhiteSpace(a.DisplayName)
                    ? a.DisplayName.Trim()
                    : Path.GetFileName(a.FilePath))
                .Where(name => !string.IsNullOrWhiteSpace(name)));
    }

    private static void WriteXmlEntry(
        ZipArchive archive,
        string entryPath,
        Action<XmlWriter> writeAction)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = s_utf8NoBom,
            Indent = false,
            OmitXmlDeclaration = false,
            CloseOutput = false
        });
        writer.WriteStartDocument(standalone: true);
        writeAction(writer);
        writer.WriteEndDocument();
        writer.Flush();
    }

    private static void WriteContentTypesXml(XmlWriter writer)
    {
        writer.WriteStartElement("Types", ContentTypesNamespace);
        WriteDefaultContentType(writer, "rels", "application/vnd.openxmlformats-package.relationships+xml");
        WriteDefaultContentType(writer, "xml", "application/xml");
        WriteOverrideContentType(writer, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        WriteOverrideContentType(writer, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
        WriteOverrideContentType(writer, "/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
        WriteOverrideContentType(writer, "/xl/worksheets/sheet2.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
        writer.WriteEndElement();
    }

    private static void WriteDefaultContentType(XmlWriter writer, string extension, string contentType)
    {
        writer.WriteStartElement("Default", ContentTypesNamespace);
        writer.WriteAttributeString("Extension", extension);
        writer.WriteAttributeString("ContentType", contentType);
        writer.WriteEndElement();
    }

    private static void WriteOverrideContentType(XmlWriter writer, string partName, string contentType)
    {
        writer.WriteStartElement("Override", ContentTypesNamespace);
        writer.WriteAttributeString("PartName", partName);
        writer.WriteAttributeString("ContentType", contentType);
        writer.WriteEndElement();
    }

    private static void WriteRootRelationshipsXml(XmlWriter writer)
    {
        writer.WriteStartElement("Relationships", PackageRelationshipsNamespace);
        writer.WriteStartElement("Relationship", PackageRelationshipsNamespace);
        writer.WriteAttributeString("Id", "rId1");
        writer.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument");
        writer.WriteAttributeString("Target", "xl/workbook.xml");
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteWorkbookXml(XmlWriter writer, string detailsSheetName, string summarySheetName)
    {
        writer.WriteStartElement("workbook", SpreadsheetNamespace);
        writer.WriteAttributeString("xmlns", "r", null, RelationshipsNamespace);

        writer.WriteStartElement("sheets", SpreadsheetNamespace);

        writer.WriteStartElement("sheet", SpreadsheetNamespace);
        writer.WriteAttributeString("name", detailsSheetName);
        writer.WriteAttributeString("sheetId", "1");
        writer.WriteAttributeString("id", RelationshipsNamespace, "rId1");
        writer.WriteEndElement();

        writer.WriteStartElement("sheet", SpreadsheetNamespace);
        writer.WriteAttributeString("name", summarySheetName);
        writer.WriteAttributeString("sheetId", "2");
        writer.WriteAttributeString("id", RelationshipsNamespace, "rId2");
        writer.WriteEndElement();

        writer.WriteEndElement(); // sheets
        writer.WriteEndElement(); // workbook
    }

    private static void WriteWorkbookRelationshipsXml(XmlWriter writer)
    {
        writer.WriteStartElement("Relationships", PackageRelationshipsNamespace);
        WritePackageRelationship(writer, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", "worksheets/sheet1.xml");
        WritePackageRelationship(writer, "rId2", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", "worksheets/sheet2.xml");
        WritePackageRelationship(writer, "rId3", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml");
        writer.WriteEndElement();
    }

    private static void WritePackageRelationship(XmlWriter writer, string id, string type, string target)
    {
        writer.WriteStartElement("Relationship", PackageRelationshipsNamespace);
        writer.WriteAttributeString("Id", id);
        writer.WriteAttributeString("Type", type);
        writer.WriteAttributeString("Target", target);
        writer.WriteEndElement();
    }

    private static void WriteStylesXml(XmlWriter writer)
    {
        writer.WriteStartElement("styleSheet", SpreadsheetNamespace);

        // Fonts (0..6)
        writer.WriteStartElement("fonts", SpreadsheetNamespace);
        writer.WriteAttributeString("count", "7");
        WriteFont(writer, sizePt: "10.5", rgb: "FF1F2329", bold: false); // 0: Normal
        WriteFont(writer, sizePt: "10.5", rgb: "FF1F2329", bold: true);  // 1: Header Bold
        WriteFont(writer, sizePt: "10.5", rgb: "FF646A73", bold: false); // 2: Completed Muted
        WriteFont(writer, sizePt: "10.5", rgb: "FFD97706", bold: true);  // 3: Important Star Gold
        WriteFont(writer, sizePt: "10.5", rgb: "FFDC2626", bold: true);  // 4: Overdue Red
        WriteFont(writer, sizePt: "13", rgb: "FF1F2329", bold: true);    // 5: Report Title
        WriteFont(writer, sizePt: "11", rgb: "FF1D4ED8", bold: true);    // 6: Section Header
        writer.WriteEndElement();

        // Fills (0=none, 1=gray125 mandatory per OpenXML spec, 2..11=custom solid fills)
        writer.WriteStartElement("fills", SpreadsheetNamespace);
        writer.WriteAttributeString("count", "12");
        WritePatternFill(writer, "none", null);           // 0
        WritePatternFill(writer, "gray125", null);        // 1
        WritePatternFill(writer, "solid", "FFEEF3FA");    // 2: Table Header
        WritePatternFill(writer, "solid", "FFF5F7FA");    // 3: Section Banner
        WritePatternFill(writer, "solid", "FFFDECEC");    // 4: Red
        WritePatternFill(writer, "solid", "FFFEF3E7");    // 5: Orange
        WritePatternFill(writer, "solid", "FFFEFBE8");    // 6: Yellow
        WritePatternFill(writer, "solid", "FFECF8F0");    // 7: Green
        WritePatternFill(writer, "solid", "FFEBF3FC");    // 8: Blue
        WritePatternFill(writer, "solid", "FFF4EEFC");    // 9: Purple
        WritePatternFill(writer, "solid", "FFE8F8F6");    // 10: Teal
        WritePatternFill(writer, "solid", "FFFCEEF5");    // 11: Pink
        writer.WriteEndElement();

        // Borders (0=none, 1=thin light slate border)
        writer.WriteStartElement("borders", SpreadsheetNamespace);
        writer.WriteAttributeString("count", "2");
        writer.WriteStartElement("border", SpreadsheetNamespace);
        writer.WriteElementString("left", SpreadsheetNamespace, string.Empty);
        writer.WriteElementString("right", SpreadsheetNamespace, string.Empty);
        writer.WriteElementString("top", SpreadsheetNamespace, string.Empty);
        writer.WriteElementString("bottom", SpreadsheetNamespace, string.Empty);
        writer.WriteElementString("diagonal", SpreadsheetNamespace, string.Empty);
        writer.WriteEndElement();

        writer.WriteStartElement("border", SpreadsheetNamespace);
        WriteThinBorderEdge(writer, "left", "FFE2E8F0");
        WriteThinBorderEdge(writer, "right", "FFE2E8F0");
        WriteThinBorderEdge(writer, "top", "FFE2E8F0");
        WriteThinBorderEdge(writer, "bottom", "FFE2E8F0");
        writer.WriteElementString("diagonal", SpreadsheetNamespace, string.Empty);
        writer.WriteEndElement();
        writer.WriteEndElement();

        // cellStyleXfs
        writer.WriteStartElement("cellStyleXfs", SpreadsheetNamespace);
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("xf", SpreadsheetNamespace);
        writer.WriteAttributeString("numFmtId", "0");
        writer.WriteAttributeString("fontId", "0");
        writer.WriteAttributeString("fillId", "0");
        writer.WriteAttributeString("borderId", "0");
        writer.WriteEndElement();
        writer.WriteEndElement();

        // cellXfs (0..17)
        writer.WriteStartElement("cellXfs", SpreadsheetNamespace);
        writer.WriteAttributeString("count", "18");
        WriteCellXf(writer, fontId: 0, fillId: 0, borderId: 1, horizontal: "left", wrapText: false);   // 0: Default left
        WriteCellXf(writer, fontId: 0, fillId: 0, borderId: 1, horizontal: "left", wrapText: true);    // 1: Wrap left
        WriteCellXf(writer, fontId: 0, fillId: 0, borderId: 1, horizontal: "center", wrapText: false); // 2: Centered
        WriteCellXf(writer, fontId: 1, fillId: 2, borderId: 1, horizontal: "center", wrapText: false); // 3: Header centered
        WriteCellXf(writer, fontId: 1, fillId: 2, borderId: 1, horizontal: "left", wrapText: false);   // 4: Header left
        WriteCellXf(writer, fontId: 2, fillId: 0, borderId: 1, horizontal: "center", wrapText: false); // 5: Completed muted
        WriteCellXf(writer, fontId: 3, fillId: 0, borderId: 1, horizontal: "center", wrapText: false); // 6: Important gold
        WriteCellXf(writer, fontId: 4, fillId: 0, borderId: 1, horizontal: "center", wrapText: false); // 7: Overdue red
        WriteCellXf(writer, fontId: 5, fillId: 0, borderId: 0, horizontal: "left", wrapText: false);   // 8: Report title
        WriteCellXf(writer, fontId: 6, fillId: 3, borderId: 1, horizontal: "left", wrapText: false);   // 9: Section header
        for (int colorFill = 4; colorFill <= 11; colorFill++)
        {
            WriteCellXf(writer, fontId: 0, fillId: colorFill, borderId: 1, horizontal: "center", wrapText: false); // 10..17
        }
        writer.WriteEndElement();

        // cellStyles
        writer.WriteStartElement("cellStyles", SpreadsheetNamespace);
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("cellStyle", SpreadsheetNamespace);
        writer.WriteAttributeString("name", "Normal");
        writer.WriteAttributeString("xfId", "0");
        writer.WriteAttributeString("builtinId", "0");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteEndElement(); // styleSheet
    }

    private static void WriteFont(XmlWriter writer, string sizePt, string rgb, bool bold)
    {
        writer.WriteStartElement("font", SpreadsheetNamespace);
        if (bold)
        {
            writer.WriteStartElement("b", SpreadsheetNamespace);
            writer.WriteEndElement();
        }

        writer.WriteStartElement("sz", SpreadsheetNamespace);
        writer.WriteAttributeString("val", sizePt);
        writer.WriteEndElement();

        writer.WriteStartElement("color", SpreadsheetNamespace);
        writer.WriteAttributeString("rgb", rgb);
        writer.WriteEndElement();

        writer.WriteStartElement("name", SpreadsheetNamespace);
        writer.WriteAttributeString("val", "Microsoft YaHei UI");
        writer.WriteEndElement();

        writer.WriteEndElement();
    }

    private static void WritePatternFill(XmlWriter writer, string patternType, string? fgRgb)
    {
        writer.WriteStartElement("fill", SpreadsheetNamespace);
        writer.WriteStartElement("patternFill", SpreadsheetNamespace);
        writer.WriteAttributeString("patternType", patternType);
        if (!string.IsNullOrEmpty(fgRgb))
        {
            writer.WriteStartElement("fgColor", SpreadsheetNamespace);
            writer.WriteAttributeString("rgb", fgRgb);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteThinBorderEdge(XmlWriter writer, string edgeName, string rgb)
    {
        writer.WriteStartElement(edgeName, SpreadsheetNamespace);
        writer.WriteAttributeString("style", "thin");
        writer.WriteStartElement("color", SpreadsheetNamespace);
        writer.WriteAttributeString("rgb", rgb);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteCellXf(
        XmlWriter writer,
        int fontId,
        int fillId,
        int borderId,
        string horizontal,
        bool wrapText)
    {
        writer.WriteStartElement("xf", SpreadsheetNamespace);
        writer.WriteAttributeString("numFmtId", "0");
        writer.WriteAttributeString("fontId", fontId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("fillId", fillId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("borderId", borderId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("xfId", "0");
        writer.WriteAttributeString("applyFont", "1");
        writer.WriteAttributeString("applyFill", fillId > 0 ? "1" : "0");
        writer.WriteAttributeString("applyBorder", borderId > 0 ? "1" : "0");
        writer.WriteAttributeString("applyAlignment", "1");

        writer.WriteStartElement("alignment", SpreadsheetNamespace);
        writer.WriteAttributeString("horizontal", horizontal);
        writer.WriteAttributeString("vertical", "center");
        if (wrapText)
        {
            writer.WriteAttributeString("wrapText", "1");
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteDetailsSheetXml(
        XmlWriter writer,
        IReadOnlyList<TodoItem> items,
        LocalizationService loc,
        DateTimeOffset now)
    {
        writer.WriteStartElement("worksheet", SpreadsheetNamespace);

        // Freeze top header row
        writer.WriteStartElement("sheetViews", SpreadsheetNamespace);
        writer.WriteStartElement("sheetView", SpreadsheetNamespace);
        writer.WriteAttributeString("workbookViewId", "0");
        writer.WriteStartElement("pane", SpreadsheetNamespace);
        writer.WriteAttributeString("ySplit", "1");
        writer.WriteAttributeString("topLeftCell", "A2");
        writer.WriteAttributeString("activePane", "bottomLeft");
        writer.WriteAttributeString("state", "frozen");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("sheetFormatPr", SpreadsheetNamespace);
        writer.WriteAttributeString("defaultRowHeight", "21");
        writer.WriteEndElement();

        // Pre-tuned column widths for WPS & Excel
        double[] widths = [40, 12, 12, 13, 19, 16, 30, 34, 19, 19, 24];
        writer.WriteStartElement("cols", SpreadsheetNamespace);
        for (int i = 0; i < widths.Length; i++)
        {
            writer.WriteStartElement("col", SpreadsheetNamespace);
            string colNum = (i + 1).ToString(CultureInfo.InvariantCulture);
            writer.WriteAttributeString("min", colNum);
            writer.WriteAttributeString("max", colNum);
            writer.WriteAttributeString("width", widths[i].ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("customWidth", "1");
            writer.WriteEndElement();
        }
        writer.WriteEndElement();

        writer.WriteStartElement("sheetData", SpreadsheetNamespace);

        // Row 1: Header
        string[] headers = GetDetailColumnHeaders(loc);
        writer.WriteStartElement("row", SpreadsheetNamespace);
        writer.WriteAttributeString("r", "1");
        writer.WriteAttributeString("ht", "26");
        writer.WriteAttributeString("customHeight", "1");
        for (int c = 0; c < headers.Length; c++)
        {
            int headerStyle = (c is 0 or 6 or 7 or 10) ? 4 : 3;
            WriteStringCell(writer, GetCellReference(c + 1, 1), headers[c], headerStyle);
        }
        writer.WriteEndElement();

        string completedLabel = loc.T("Todo.Export.Value.Completed");
        string activeLabel = loc.T("Todo.Export.Value.Active");
        string importantLabel = loc.T("Todo.Export.Value.ImportantStar");
        string overdueLabel = loc.T("Todo.Due.OverdueSuffix");

        for (int index = 0; index < items.Count; index++)
        {
            TodoItem item = items[index];
            int rowNumber = index + 2;
            bool overdue = IsItemOverdue(item, now);

            writer.WriteStartElement("row", SpreadsheetNamespace);
            writer.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));

            WriteStringCell(writer, GetCellReference(1, rowNumber), NormalizeNewLines(item.Text.Trim()), styleIndex: 1);
            WriteStringCell(
                writer,
                GetCellReference(2, rowNumber),
                item.IsCompleted ? completedLabel : activeLabel,
                styleIndex: item.IsCompleted ? 5 : 2);
            WriteStringCell(
                writer,
                GetCellReference(3, rowNumber),
                item.IsImportant ? importantLabel : string.Empty,
                styleIndex: item.IsImportant ? 6 : 2);
            WriteStringCell(
                writer,
                GetCellReference(4, rowNumber),
                FormatColorName(item.ColorMarker, loc),
                styleIndex: GetColorCellStyleIndex(item.ColorMarker));
            WriteStringCell(
                writer,
                GetCellReference(5, rowNumber),
                FormatDueDate(item),
                styleIndex: overdue ? 7 : 2);
            WriteStringCell(
                writer,
                GetCellReference(6, rowNumber),
                FormatOverdueAndRecurrence(item, now, overdueLabel, loc),
                styleIndex: overdue ? 7 : 2);
            WriteStringCell(
                writer,
                GetCellReference(7, rowNumber),
                FormatStepsCell(item.Steps),
                styleIndex: 1);
            WriteStringCell(
                writer,
                GetCellReference(8, rowNumber),
                NormalizeNewLines(item.Notes?.Trim() ?? string.Empty),
                styleIndex: 1);
            WriteStringCell(
                writer,
                GetCellReference(9, rowNumber),
                FormatTimestamp(item.CreatedAt),
                styleIndex: 2);
            WriteStringCell(
                writer,
                GetCellReference(10, rowNumber),
                item.IsCompleted ? FormatTimestamp(item.CompletedAt ?? item.UpdatedAt) : string.Empty,
                styleIndex: 2);
            WriteStringCell(
                writer,
                GetCellReference(11, rowNumber),
                FormatAttachmentsCell(item.Attachments),
                styleIndex: 1);

            writer.WriteEndElement(); // row
        }

        writer.WriteEndElement(); // sheetData

        int lastRow = Math.Max(1, items.Count + 1);
        writer.WriteStartElement("autoFilter", SpreadsheetNamespace);
        writer.WriteAttributeString("ref", $"A1:K{lastRow.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteEndElement();

        writer.WriteEndElement(); // worksheet
    }

    private static void WriteSummarySheetXml(
        XmlWriter writer,
        IReadOnlyList<TodoItem> items,
        TodoExportOptions options,
        LocalizationService loc,
        DateTimeOffset now)
    {
        writer.WriteStartElement("worksheet", SpreadsheetNamespace);

        writer.WriteStartElement("sheetViews", SpreadsheetNamespace);
        writer.WriteStartElement("sheetView", SpreadsheetNamespace);
        writer.WriteAttributeString("workbookViewId", "0");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("sheetFormatPr", SpreadsheetNamespace);
        writer.WriteAttributeString("defaultRowHeight", "22");
        writer.WriteEndElement();

        double[] widths = [26, 20, 16, 16, 16];
        writer.WriteStartElement("cols", SpreadsheetNamespace);
        for (int i = 0; i < widths.Length; i++)
        {
            writer.WriteStartElement("col", SpreadsheetNamespace);
            string colNum = (i + 1).ToString(CultureInfo.InvariantCulture);
            writer.WriteAttributeString("min", colNum);
            writer.WriteAttributeString("max", colNum);
            writer.WriteAttributeString("width", widths[i].ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("customWidth", "1");
            writer.WriteEndElement();
        }
        writer.WriteEndElement();

        writer.WriteStartElement("sheetData", SpreadsheetNamespace);

        int row = 1;

        // Title
        writer.WriteStartElement("row", SpreadsheetNamespace);
        writer.WriteAttributeString("r", row.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("ht", "30");
        writer.WriteAttributeString("customHeight", "1");
        WriteStringCell(writer, $"A{row}", loc.T("Todo.Export.Summary.ReportTitle"), styleIndex: 8);
        writer.WriteEndElement();
        row += 2;

        // Section 1: Overview
        WriteSectionBannerRow(writer, row++, loc.T("Todo.Export.Summary.OverviewSection"), columnCount: 2);

        int total = items.Count;
        int completed = items.Count(i => i.IsCompleted);
        int active = total - completed;
        int important = items.Count(i => i.IsImportant);
        int overdue = items.Count(i => IsItemOverdue(i, now));
        int totalSteps = items.Sum(i => i.Steps?.Count ?? 0);
        int completedSteps = items.Sum(i => i.Steps?.Count(s => s.IsCompleted) ?? 0);
        string completionRate = total > 0
            ? $"{(completed * 100.0 / total):0.0}%"
            : "0.0%";

        (DateOnly? rangeStart, DateOnly? rangeEnd) = ResolveDateRange(options, now);
        string rangeDisplay = rangeStart is { } rs && rangeEnd is { } re
            ? $"{rs:yyyy-MM-dd} ~ {re:yyyy-MM-dd}"
            : loc.T("Todo.Export.DateRange.AllTime");

        WriteKeyValueStringRow(writer, row++, loc.T("Todo.Export.Summary.ExportedAt"), FormatTimestamp(now));
        WriteKeyValueStringRow(writer, row++, loc.T("Todo.Export.Summary.DateScope"), rangeDisplay);
        WriteKeyValueNumberRow(writer, row++, loc.T("Todo.Export.Summary.TotalTasks"), total);
        WriteKeyValueNumberRow(writer, row++, loc.T("Todo.Export.Summary.CompletedTasks"), completed);
        WriteKeyValueNumberRow(writer, row++, loc.T("Todo.Export.Summary.ActiveTasks"), active);
        WriteKeyValueStringRow(writer, row++, loc.T("Todo.Export.Summary.CompletionRate"), completionRate);
        WriteKeyValueNumberRow(writer, row++, loc.T("Todo.Export.Summary.ImportantTasks"), important);
        WriteKeyValueNumberRow(writer, row++, loc.T("Todo.Export.Summary.OverdueTasks"), overdue);
        if (totalSteps > 0)
        {
            WriteKeyValueStringRow(writer, row++, loc.T("Todo.Export.Summary.SubstepsProgress"), $"{completedSteps} / {totalSteps}");
        }

        row++;

        // Section 2: By Color Marker
        WriteSectionBannerRow(writer, row++, loc.T("Todo.Export.Summary.ByColorSection"), columnCount: 5);
        writer.WriteStartElement("row", SpreadsheetNamespace);
        writer.WriteAttributeString("r", row.ToString(CultureInfo.InvariantCulture));
        WriteStringCell(writer, $"A{row}", loc.T("Todo.Export.Col.ColorTag"), styleIndex: 4);
        WriteStringCell(writer, $"B{row}", loc.T("Todo.Export.Summary.TotalTasks"), styleIndex: 3);
        WriteStringCell(writer, $"C{row}", loc.T("Todo.Export.Value.Completed"), styleIndex: 3);
        WriteStringCell(writer, $"D{row}", loc.T("Todo.Export.Value.Active"), styleIndex: 3);
        WriteStringCell(writer, $"E{row}", loc.T("Todo.Export.Summary.CompletionRate"), styleIndex: 3);
        writer.WriteEndElement();
        row++;

        string?[] colorBuckets = [.. TodoItem.SupportedColorMarkers, null];
        foreach (string? color in colorBuckets)
        {
            TodoItem[] bucket = items
                .Where(i => string.Equals(TodoItem.NormalizeColorMarker(i.ColorMarker), color, StringComparison.Ordinal))
                .ToArray();
            if (bucket.Length == 0)
            {
                continue;
            }

            int bucketDone = bucket.Count(i => i.IsCompleted);
            int bucketActive = bucket.Length - bucketDone;
            string bucketRate = $"{(bucketDone * 100.0 / bucket.Length):0.0}%";
            string colorLabel = color is null
                ? loc.T("Todo.Color.None")
                : loc.T(TodoItem.GetColorMarkerLocalizationKey(color));
            int colorStyle = color is null ? 0 : GetColorCellStyleIndex(color);

            writer.WriteStartElement("row", SpreadsheetNamespace);
            writer.WriteAttributeString("r", row.ToString(CultureInfo.InvariantCulture));
            WriteStringCell(writer, $"A{row}", colorLabel, styleIndex: colorStyle);
            WriteNumberCell(writer, $"B{row}", bucket.Length, styleIndex: 2);
            WriteNumberCell(writer, $"C{row}", bucketDone, styleIndex: 2);
            WriteNumberCell(writer, $"D{row}", bucketActive, styleIndex: 2);
            WriteStringCell(writer, $"E{row}", bucketRate, styleIndex: 2);
            writer.WriteEndElement();
            row++;
        }

        row++;

        // Section 3: Monthly Workload Trend
        WriteSectionBannerRow(writer, row++, loc.T("Todo.Export.Summary.ByMonthSection"), columnCount: 4);
        writer.WriteStartElement("row", SpreadsheetNamespace);
        writer.WriteAttributeString("r", row.ToString(CultureInfo.InvariantCulture));
        WriteStringCell(writer, $"A{row}", loc.T("Todo.Export.Summary.MonthColumn"), styleIndex: 4);
        WriteStringCell(writer, $"B{row}", loc.T("Todo.Export.Summary.CreatedCount"), styleIndex: 3);
        WriteStringCell(writer, $"C{row}", loc.T("Todo.Export.Summary.CompletedCount"), styleIndex: 3);
        WriteStringCell(writer, $"D{row}", loc.T("Todo.Export.Summary.ImportantCompletedCount"), styleIndex: 3);
        writer.WriteEndElement();
        row++;

        var months = new SortedSet<string>(StringComparer.Ordinal);
        var createdByMonth = new Dictionary<string, int>(StringComparer.Ordinal);
        var completedByMonth = new Dictionary<string, int>(StringComparer.Ordinal);
        var importantDoneByMonth = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (TodoItem item in items)
        {
            string createdMonth = item.CreatedAt.ToLocalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture);
            months.Add(createdMonth);
            createdByMonth[createdMonth] = createdByMonth.GetValueOrDefault(createdMonth) + 1;

            if (item.IsCompleted)
            {
                string doneMonth = (item.CompletedAt ?? item.UpdatedAt).ToLocalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture);
                months.Add(doneMonth);
                completedByMonth[doneMonth] = completedByMonth.GetValueOrDefault(doneMonth) + 1;
                if (item.IsImportant)
                {
                    importantDoneByMonth[doneMonth] = importantDoneByMonth.GetValueOrDefault(doneMonth) + 1;
                }
            }
        }

        foreach (string month in months)
        {
            writer.WriteStartElement("row", SpreadsheetNamespace);
            writer.WriteAttributeString("r", row.ToString(CultureInfo.InvariantCulture));
            WriteStringCell(writer, $"A{row}", month, styleIndex: 2);
            WriteNumberCell(writer, $"B{row}", createdByMonth.GetValueOrDefault(month), styleIndex: 2);
            WriteNumberCell(writer, $"C{row}", completedByMonth.GetValueOrDefault(month), styleIndex: 2);
            WriteNumberCell(writer, $"D{row}", importantDoneByMonth.GetValueOrDefault(month), styleIndex: 2);
            writer.WriteEndElement();
            row++;
        }

        writer.WriteEndElement(); // sheetData
        writer.WriteEndElement(); // worksheet
    }

    private static void WriteSectionBannerRow(XmlWriter writer, int row, string title, int columnCount)
    {
        writer.WriteStartElement("row", SpreadsheetNamespace);
        writer.WriteAttributeString("r", row.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("ht", "25");
        writer.WriteAttributeString("customHeight", "1");
        WriteStringCell(writer, GetCellReference(1, row), title, styleIndex: 9);
        for (int col = 2; col <= columnCount; col++)
        {
            WriteStringCell(writer, GetCellReference(col, row), string.Empty, styleIndex: 9);
        }
        writer.WriteEndElement();
    }

    private static void WriteKeyValueStringRow(XmlWriter writer, int row, string label, string value)
    {
        writer.WriteStartElement("row", SpreadsheetNamespace);
        writer.WriteAttributeString("r", row.ToString(CultureInfo.InvariantCulture));
        WriteStringCell(writer, $"A{row}", label, styleIndex: 0);
        WriteStringCell(writer, $"B{row}", value, styleIndex: 2);
        writer.WriteEndElement();
    }

    private static void WriteKeyValueNumberRow(XmlWriter writer, int row, string label, int value)
    {
        writer.WriteStartElement("row", SpreadsheetNamespace);
        writer.WriteAttributeString("r", row.ToString(CultureInfo.InvariantCulture));
        WriteStringCell(writer, $"A{row}", label, styleIndex: 0);
        WriteNumberCell(writer, $"B{row}", value, styleIndex: 2);
        writer.WriteEndElement();
    }

    private static void WriteStringCell(XmlWriter writer, string cellRef, string? value, int styleIndex)
    {
        string sanitized = SanitizeXmlText(value);
        writer.WriteStartElement("c", SpreadsheetNamespace);
        writer.WriteAttributeString("r", cellRef);
        writer.WriteAttributeString("s", styleIndex.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("t", "inlineStr");
        writer.WriteStartElement("is", SpreadsheetNamespace);
        writer.WriteStartElement("t", SpreadsheetNamespace);
        if (NeedsXmlSpacePreserve(sanitized))
        {
            writer.WriteAttributeString("xml", "space", null, "preserve");
        }

        writer.WriteString(sanitized);
        writer.WriteEndElement(); // t
        writer.WriteEndElement(); // is
        writer.WriteEndElement(); // c
    }

    private static void WriteNumberCell(XmlWriter writer, string cellRef, int value, int styleIndex)
    {
        writer.WriteStartElement("c", SpreadsheetNamespace);
        writer.WriteAttributeString("r", cellRef);
        writer.WriteAttributeString("s", styleIndex.ToString(CultureInfo.InvariantCulture));
        writer.WriteStartElement("v", SpreadsheetNamespace);
        writer.WriteString(value.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static string GetCellReference(int oneBasedColumn, int oneBasedRow)
    {
        string colName = string.Empty;
        int col = oneBasedColumn;
        while (col > 0)
        {
            int modulo = (col - 1) % 26;
            colName = (char)('A' + modulo) + colName;
            col = (col - modulo) / 26;
        }

        return string.Concat(colName, oneBasedRow.ToString(CultureInfo.InvariantCulture));
    }

    private static bool NeedsXmlSpacePreserve(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        return char.IsWhiteSpace(text[0]) ||
               char.IsWhiteSpace(text[^1]) ||
               text.Contains('\n', StringComparison.Ordinal) ||
               text.Contains('\r', StringComparison.Ordinal) ||
               text.Contains('\t', StringComparison.Ordinal);
    }

    private static string SanitizeXmlText(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            char ch = input[i];
            if (ch == '\t' || ch == '\n' || ch == '\r' ||
                (ch >= 0x20 && ch <= 0xD7FF) ||
                (ch >= 0xE000 && ch <= 0xFFFD))
            {
                builder.Append(ch);
            }
            else if (char.IsHighSurrogate(ch) && i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
            {
                builder.Append(ch);
                builder.Append(input[++i]);
            }
        }

        return builder.ToString();
    }

    private static string SanitizeSheetName(string? candidate, string fallback)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return fallback;
        }

        ReadOnlySpan<char> forbidden = [':', '\\', '/', '?', '*', '[', ']'];
        var builder = new StringBuilder(candidate.Length);
        foreach (char ch in candidate.Trim())
        {
            if (!forbidden.Contains(ch))
            {
                builder.Append(ch);
            }
        }

        string cleaned = builder.ToString().Trim('\'');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return fallback;
        }

        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    private static string SanitizeFileSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return string.Empty;
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(segment.Length);
        foreach (char ch in segment.Trim())
        {
            if (Array.IndexOf(invalid, ch) < 0 && !char.IsWhiteSpace(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static string NormalizeNewLines(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
                   .Replace('\r', '\n');
    }

    private static string NormalizeSingleLine(string text)
    {
        return string.Join(
            " ",
            text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string JoinCsvRow(IReadOnlyList<string> fields, bool sanitizeFormulas)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(EscapeCsvField(fields[i], sanitizeFormulas));
        }

        return builder.ToString();
    }

    private static string EscapeCsvField(string? raw, bool sanitizeFormulas)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        string value = raw;
        if (sanitizeFormulas && IsCsvFormulaTrigger(value))
        {
            // Prefix tab inside quotes so Excel/WPS will not interpret leading -, +, =, @ as a broken formula (#NAME?).
            value = "\t" + value;
        }

        bool mustQuote = value.IndexOfAny([',', '"', '\r', '\n', '\t']) >= 0 ||
                         char.IsWhiteSpace(value[0]) ||
                         char.IsWhiteSpace(value[^1]);
        if (!mustQuote)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static bool IsCsvFormulaTrigger(string value)
    {
        foreach (char ch in value)
        {
            if (ch == ' ')
            {
                continue;
            }

            return ch is '=' or '+' or '-' or '@';
        }

        return false;
    }

    private static async Task<string?> TryResolvePrimaryTodoWidgetIdAsync(
        string dataDirectory,
        CancellationToken cancellationToken)
    {
        string layoutPath = Path.Combine(dataDirectory, "widget-layout.json");
        string settingsPath = Path.Combine(dataDirectory, "settings.json");

        try
        {
            JsonArray? widgets = null;
            HashSet<string>? deletedIds = null;

            if (File.Exists(settingsPath))
            {
                byte[] settingsBytes = await File.ReadAllBytesAsync(settingsPath, cancellationToken)
                    .ConfigureAwait(false);
                JsonNode? settingsNode = JsonNode.Parse(settingsBytes);
                if (settingsNode?["deletedWidgetIds"] is JsonArray deletedArray)
                {
                    deletedIds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (JsonNode? entry in deletedArray)
                    {
                        if (entry is JsonValue val && val.TryGetValue(out string? deletedId) && !string.IsNullOrEmpty(deletedId))
                        {
                            deletedIds.Add(deletedId);
                        }
                    }
                }

                widgets = settingsNode?["widgets"] as JsonArray;
            }

            if (File.Exists(layoutPath))
            {
                byte[] layoutBytes = await File.ReadAllBytesAsync(layoutPath, cancellationToken)
                    .ConfigureAwait(false);
                JsonArray? layoutWidgets = JsonNode.Parse(layoutBytes)?["layout"]?["widgets"] as JsonArray;
                if (layoutWidgets is not null)
                {
                    widgets = layoutWidgets;
                }
            }

            if (widgets is null)
            {
                return null;
            }

            foreach (JsonNode? node in widgets)
            {
                if (node is not JsonObject obj ||
                    obj["id"] is not JsonValue idVal ||
                    !idVal.TryGetValue(out string? id) ||
                    string.IsNullOrWhiteSpace(id) ||
                    (deletedIds is not null && deletedIds.Contains(id)))
                {
                    continue;
                }

                if (obj["widgetKind"] is JsonValue kindVal)
                {
                    if ((kindVal.TryGetValue(out string? kindStr) &&
                         string.Equals(kindStr, nameof(WidgetKind.Todo), StringComparison.OrdinalIgnoreCase)) ||
                        (kindVal.TryGetValue(out int kindInt) && kindInt == (int)WidgetKind.Todo))
                    {
                        return id;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Log($"[TodoExport] Could not resolve primary Todo widget id: {ex.Message}");
        }

        return null;
    }
}
