using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class TodoExportTests : IDisposable
{
    private static readonly XNamespace s_ssNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private readonly string _tempRoot;

    public TodoExportTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.TodoExportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void BuildPreview_CompletedOnlyWithThisMonth_LocksToCompletedAtAndFallsBackToUpdatedAt()
    {
        var now = LocalTime(2026, 11, 15, 10, 0, 0);
        TodoItem[] items =
        [
            new TodoItem
            {
                Id = "nov-done",
                Text = "整理 11 月复盘报告",
                IsCompleted = true,
                IsImportant = true,
                ColorMarker = TodoItem.RedColorMarker,
                CreatedAt = LocalTime(2026, 10, 25, 9, 0, 0),
                CompletedAt = LocalTime(2026, 11, 5, 18, 30, 0)
            },
            new TodoItem
            {
                Id = "legacy-done-no-completedat",
                Text = "历史无 CompletedAt 任务",
                IsCompleted = true,
                CreatedAt = LocalTime(2026, 9, 10, 9, 0, 0),
                CompletedAt = null,
                UpdatedAt = LocalTime(2026, 11, 8, 12, 0, 0)
            },
            new TodoItem
            {
                Id = "oct-done",
                Text = "10 月已完成任务",
                IsCompleted = true,
                CreatedAt = LocalTime(2026, 10, 1, 9, 0, 0),
                CompletedAt = LocalTime(2026, 10, 28, 17, 0, 0)
            },
            new TodoItem
            {
                Id = "nov-active",
                Text = "11 月进行中任务",
                IsCompleted = false,
                CreatedAt = LocalTime(2026, 11, 10, 9, 0, 0)
            }
        ];

        var options = new TodoExportOptions
        {
            StatusScope = TodoExportStatusScope.CompletedOnly,
            DatePreset = TodoExportDatePreset.ThisMonth,
            // Even if DateBasis was CreatedAt, CompletedOnly locks EffectiveDateBasis to CompletedAt.
            DateBasis = TodoExportDateBasis.CreatedAt
        };

        Assert.Equal(TodoExportDateBasis.CompletedAt, options.EffectiveDateBasis);

        TodoExportPreview preview = TodoExportService.BuildPreview(items, options, now);

        Assert.Equal(2, preview.TotalCount);
        Assert.Equal(2, preview.CompletedCount);
        Assert.Equal(0, preview.ActiveCount);
        Assert.Equal(1, preview.ImportantCount);
        Assert.Equal(new DateOnly(2026, 11, 1), preview.RangeStart);
        Assert.Equal(new DateOnly(2026, 11, 30), preview.RangeEnd);
        Assert.Equal(["legacy-done-no-completedat", "nov-done"], preview.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void BuildPreview_FiltersByMultipleColorMarkersAndCustomDateRange()
    {
        var now = LocalTime(2026, 11, 15, 10, 0, 0);
        TodoItem[] items =
        [
            new TodoItem
            {
                Id = "red-in-range",
                Text = "红标任务",
                ColorMarker = TodoItem.RedColorMarker,
                CreatedAt = LocalTime(2026, 11, 3, 9, 0, 0)
            },
            new TodoItem
            {
                Id = "blue-in-range",
                Text = "蓝标任务",
                ColorMarker = TodoItem.BlueColorMarker,
                CreatedAt = LocalTime(2026, 11, 6, 9, 0, 0)
            },
            new TodoItem
            {
                Id = "green-in-range",
                Text = "绿标任务",
                ColorMarker = TodoItem.GreenColorMarker,
                CreatedAt = LocalTime(2026, 11, 6, 9, 0, 0)
            },
            new TodoItem
            {
                Id = "red-out-of-range",
                Text = "早期红标任务",
                ColorMarker = TodoItem.RedColorMarker,
                CreatedAt = LocalTime(2026, 9, 1, 9, 0, 0)
            }
        ];

        var options = new TodoExportOptions
        {
            StatusScope = TodoExportStatusScope.All,
            DatePreset = TodoExportDatePreset.CustomRange,
            DateBasis = TodoExportDateBasis.CreatedAt,
            // Reversed custom dates should auto-normalize cleanly.
            CustomStartDate = new DateOnly(2026, 11, 10),
            CustomEndDate = new DateOnly(2026, 11, 1),
            SelectedColors = new HashSet<string>([TodoItem.RedColorMarker, TodoItem.BlueColorMarker], StringComparer.Ordinal)
        };

        TodoExportPreview preview = TodoExportService.BuildPreview(items, options, now);

        Assert.Equal(2, preview.TotalCount);
        Assert.Equal(["red-in-range", "blue-in-range"], preview.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task ExportToFileAsync_Xlsx_ProducesValidDualSheetWorkbookWithColorNamesAndFormulaSafety()
    {
        LocalizationService loc = CreateChineseLocalizationService();
        var now = LocalTime(2026, 11, 15, 14, 0, 0);

        TodoItem[] items =
        [
            new TodoItem
            {
                Id = "active-overdue",
                Text = "- 核对采购台账\r\n补充发票附件",
                IsCompleted = false,
                IsImportant = true,
                ColorMarker = TodoItem.RedColorMarker,
                DueDate = LocalTime(2026, 11, 10, 0, 0, 0),
                HasDueTime = false,
                Recurrence = new TodoRecurrence { Mode = TodoRecurrenceMode.Weekly },
                Steps =
                [
                    new TodoStep { Text = "收集各部门清单", IsCompleted = true, SortOrder = 0 },
                    new TodoStep { Text = "财务复核签字", IsCompleted = false, SortOrder = 1 }
                ],
                Notes = "=SUM(A1:A5) 备注原文保留",
                Attachments =
                [
                    new TodoAttachment { DisplayName = "Q4采购汇总.xlsx", FilePath = @"C:\Docs\Q4采购汇总.xlsx" }
                ],
                CreatedAt = LocalTime(2026, 11, 1, 9, 30, 0)
            },
            new TodoItem
            {
                Id = "completed-timed",
                Text = "完成周会汇报",
                IsCompleted = true,
                IsImportant = false,
                ColorMarker = TodoItem.BlueColorMarker,
                DueDate = LocalTime(2026, 11, 12, 16, 30, 0),
                HasDueTime = true,
                CreatedAt = LocalTime(2026, 11, 11, 10, 0, 0),
                CompletedAt = LocalTime(2026, 11, 12, 15, 45, 0)
            }
        ];

        string xlsxPath = Path.Combine(_tempRoot, "todo_report.xlsx");
        var options = new TodoExportOptions { Format = TodoExportFormat.Xlsx };

        await TodoExportService.ExportToFileAsync(xlsxPath, items, options, loc, now);

        Assert.True(File.Exists(xlsxPath));
        using ZipArchive archive = ZipFile.OpenRead(xlsxPath);

        string[] entryNames = archive.Entries.Select(e => e.FullName).ToArray();
        Assert.Contains("[Content_Types].xml", entryNames);
        Assert.Contains("_rels/.rels", entryNames);
        Assert.Contains("xl/workbook.xml", entryNames);
        Assert.Contains("xl/_rels/workbook.xml.rels", entryNames);
        Assert.Contains("xl/styles.xml", entryNames);
        Assert.Contains("xl/worksheets/sheet1.xml", entryNames);
        Assert.Contains("xl/worksheets/sheet2.xml", entryNames);

        // Verify mandatory OpenXML fills[0]=none and fills[1]=gray125 in styles.xml
        XDocument stylesDoc = LoadXmlEntry(archive, "xl/styles.xml");
        XElement[] patternFills = stylesDoc.Descendants(s_ssNs + "patternFill").ToArray();
        Assert.True(patternFills.Length >= 12);
        Assert.Equal("none", patternFills[0].Attribute("patternType")?.Value);
        Assert.Equal("gray125", patternFills[1].Attribute("patternType")?.Value);

        // Verify dual sheet names in workbook.xml
        XDocument workbookDoc = LoadXmlEntry(archive, "xl/workbook.xml");
        string[] sheetNames = workbookDoc
            .Descendants(s_ssNs + "sheet")
            .Select(s => s.Attribute("name")?.Value ?? string.Empty)
            .ToArray();
        Assert.Equal(["任务明细", "统计概览"], sheetNames);

        // Verify sheet1.xml (Details)
        XDocument sheet1Doc = LoadXmlEntry(archive, "xl/worksheets/sheet1.xml");
        XElement? frozenPane = sheet1Doc.Descendants(s_ssNs + "pane").FirstOrDefault();
        Assert.NotNull(frozenPane);
        Assert.Equal("frozen", frozenPane!.Attribute("state")?.Value);
        Assert.Equal("1", frozenPane.Attribute("ySplit")?.Value);

        XElement? autoFilter = sheet1Doc.Descendants(s_ssNs + "autoFilter").FirstOrDefault();
        Assert.NotNull(autoFilter);
        Assert.Equal("A1:K3", autoFilter!.Attribute("ref")?.Value);

        // Leading '-' and '=' must remain inlineStr text (never formula <f>) and multiline preserves space
        Assert.Empty(sheet1Doc.Descendants(s_ssNs + "f"));
        Assert.Equal("- 核对采购台账\n补充发票附件", ReadCellText(sheet1Doc, "A2"));
        Assert.Equal("进行中", ReadCellText(sheet1Doc, "B2"));
        Assert.Equal("★ 重要", ReadCellText(sheet1Doc, "C2"));
        Assert.Equal("红色", ReadCellText(sheet1Doc, "D2"));
        Assert.Equal("2026-11-10", ReadCellText(sheet1Doc, "E2")); // All-day omits HH:mm
        Assert.Equal("已逾期 · 每周", ReadCellText(sheet1Doc, "F2"));
        Assert.Contains("1/2\n[✓] 收集各部门清单\n[ ] 财务复核签字", ReadCellText(sheet1Doc, "G2"), StringComparison.Ordinal);
        Assert.Equal("=SUM(A1:A5) 备注原文保留", ReadCellText(sheet1Doc, "H2"));
        Assert.Equal("Q4采购汇总.xlsx", ReadCellText(sheet1Doc, "K2"));

        Assert.Equal("已完成", ReadCellText(sheet1Doc, "B3"));
        Assert.Equal("蓝色", ReadCellText(sheet1Doc, "D3"));
        Assert.Equal("2026-11-12 16:30", ReadCellText(sheet1Doc, "E3")); // Timed includes HH:mm
        Assert.Equal("2026-11-12 15:45", ReadCellText(sheet1Doc, "J3"));

        // Verify sheet2.xml (Summary)
        XDocument sheet2Doc = LoadXmlEntry(archive, "xl/worksheets/sheet2.xml");
        string allSummaryText = string.Join(" | ", sheet2Doc.Descendants(s_ssNs + "t").Select(t => t.Value));
        Assert.Contains("一、整体概览", allSummaryText, StringComparison.Ordinal);
        Assert.Contains("二、按颜色分类统计", allSummaryText, StringComparison.Ordinal);
        Assert.Contains("三、按月份工作量趋势", allSummaryText, StringComparison.Ordinal);
        Assert.Contains("50.0%", allSummaryText, StringComparison.Ordinal);
        Assert.Contains("2026-11", allSummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportToFileAsync_Csv_WritesUtf8BomAndSanitizesLeadingFormulaCharacters()
    {
        LocalizationService loc = CreateChineseLocalizationService();
        var now = LocalTime(2026, 11, 15, 14, 0, 0);

        TodoItem[] items =
        [
            new TodoItem
            {
                Id = "csv-1",
                Text = "- 整理周报, 含\"重点\"项目",
                IsCompleted = true,
                ColorMarker = TodoItem.GreenColorMarker,
                CreatedAt = LocalTime(2026, 11, 10, 9, 0, 0),
                CompletedAt = LocalTime(2026, 11, 12, 18, 0, 0)
            }
        ];

        string csvPath = Path.Combine(_tempRoot, "todo_report.csv");
        await TodoExportService.ExportToFileAsync(
            csvPath,
            items,
            new TodoExportOptions { Format = TodoExportFormat.Csv },
            loc,
            now);

        byte[] rawBytes = await File.ReadAllBytesAsync(csvPath);
        Assert.True(rawBytes.Length > 3);
        Assert.Equal(0xEF, rawBytes[0]);
        Assert.Equal(0xBB, rawBytes[1]);
        Assert.Equal(0xBF, rawBytes[2]);

        string content = Encoding.UTF8.GetString(rawBytes, 3, rawBytes.Length - 3);
        Assert.Contains("任务标题,状态,重要星标,分类标签,截止时间", content, StringComparison.Ordinal);
        // Leading '-' in CSV is tab-prefixed inside quotes so Excel/WPS will not evaluate it as a broken formula
        Assert.Contains("\"\t- 整理周报, 含\"\"重点\"\"项目\"", content, StringComparison.Ordinal);
        Assert.Contains(",绿色,", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadPrimaryTodoItemsAsync_SkipsDeletedWidgetsAndLoadsActiveTodoStore()
    {
        string widgetsRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "widgets")).FullName;

        var deletedStore = new TodoWidgetStore(widgetsRoot, "deleted-todo");
        await deletedStore.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "old", Text = "已删除格子的旧数据" }]
        });

        var activeStore = new TodoWidgetStore(widgetsRoot, "active-todo");
        await activeStore.SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem { Id = "live-1", Text = "当前主待办任务" },
                new TodoItem { Id = "tombstone-1", Text = "软删除任务", IsDeleted = true }
            ]
        });

        await File.WriteAllTextAsync(
            Path.Combine(_tempRoot, "settings.json"),
            """
            {
              "deletedWidgetIds": ["deleted-todo"],
              "widgets": [
                { "id": "deleted-todo", "widgetKind": "Todo" },
                { "id": "active-todo", "widgetKind": "Todo" }
              ]
            }
            """);

        IReadOnlyList<TodoItem> loaded = await TodoExportService.LoadPrimaryTodoItemsAsync(_tempRoot);

        TodoItem single = Assert.Single(loaded);
        Assert.Equal("live-1", single.Id);
        Assert.Equal("当前主待办任务", single.Text);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static DateTimeOffset LocalTime(int year, int month, int day, int hour, int minute, int second = 0) =>
        new(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local));

    private static LocalizationService CreateChineseLocalizationService()
    {
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.TodoExportTests",
            $"settings-{Guid.NewGuid():N}.json");
        var settingsService = new SettingsService(settingsPath);
        settingsService.Settings.Language = "zh-CN";
        return new LocalizationService(settingsService);
    }

    private static XDocument LoadXmlEntry(ZipArchive archive, string entryPath)
    {
        ZipArchiveEntry? entry = archive.GetEntry(entryPath);
        Assert.NotNull(entry);
        using Stream stream = entry!.Open();
        return XDocument.Load(stream);
    }

    private static string ReadCellText(XDocument worksheetDoc, string cellRef)
    {
        XElement? cell = worksheetDoc
            .Descendants(s_ssNs + "c")
            .FirstOrDefault(c => string.Equals(c.Attribute("r")?.Value, cellRef, StringComparison.Ordinal));
        Assert.NotNull(cell);
        return cell!.Descendants(s_ssNs + "t").FirstOrDefault()?.Value ?? string.Empty;
    }
}
