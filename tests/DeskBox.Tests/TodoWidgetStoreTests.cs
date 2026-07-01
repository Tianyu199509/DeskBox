using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class TodoWidgetStoreTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _widgetsDataRoot;

    public TodoWidgetStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        _widgetsDataRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "widgets")).FullName;
    }

    [Fact]
    public async Task LoadAsync_ReturnsEmptyDataWhenStoreDoesNotExist()
    {
        var store = CreateStore("todo-widget");

        var data = await store.LoadAsync();

        Assert.Equal(1, data.Version);
        Assert.Empty(data.Items);
        Assert.EndsWith(Path.Combine("todo-widget", "todo.json"), store.StorePath);
    }

    [Fact]
    public async Task SaveAsync_PersistsAndReloadsItems()
    {
        var store = CreateStore("todo-widget");
        var createdAt = DateTimeOffset.Parse("2026-06-30T00:00:00Z");
        var updatedAt = DateTimeOffset.Parse("2026-06-30T00:10:00Z");
        var dueDate = DateTimeOffset.Parse("2026-07-01T00:00:00Z");

        await store.SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem
                {
                    Id = "first",
                    Text = " first task ",
                    IsCompleted = true,
                    DueDate = dueDate,
                    SortOrder = 1,
                    CreatedAt = createdAt,
                    UpdatedAt = updatedAt
                },
                new TodoItem
                {
                    Id = "second",
                    Text = "second task",
                    SortOrder = 0,
                    CreatedAt = createdAt,
                    UpdatedAt = updatedAt
                }
            ]
        });

        var reloaded = await CreateStore("todo-widget").LoadAsync();

        Assert.Collection(
            reloaded.Items,
            item =>
            {
                Assert.Equal("second", item.Id);
                Assert.Equal("second task", item.Text);
                Assert.False(item.IsCompleted);
                Assert.Equal(0, item.SortOrder);
            },
            item =>
            {
                Assert.Equal("first", item.Id);
                Assert.Equal("first task", item.Text);
                Assert.True(item.IsCompleted);
                Assert.Equal(dueDate, item.DueDate);
                Assert.Equal(1, item.SortOrder);
            });
    }

    [Fact]
    public async Task LoadAsync_ReturnsEmptyDataForInvalidJson()
    {
        var store = CreateStore("todo-widget");
        Directory.CreateDirectory(Path.GetDirectoryName(store.StorePath)!);
        await File.WriteAllTextAsync(store.StorePath, "{ invalid json");

        var data = await store.LoadAsync();

        Assert.Equal(1, data.Version);
        Assert.Empty(data.Items);
    }

    [Fact]
    public async Task SaveAsync_NormalizesItems()
    {
        var store = CreateStore("todo-widget");
        var now = DateTimeOffset.Parse("2026-06-30T00:00:00Z");

        await store.SaveAsync(new TodoWidgetData
        {
            Version = 0,
            Items =
            [
                new TodoItem
                {
                    Id = "duplicate",
                    Text = " keep ",
                    ColorMarker = "RED",
                    SortOrder = -5,
                    CreatedAt = now
                },
                new TodoItem
                {
                    Id = "duplicate",
                    Text = "remove duplicate",
                    SortOrder = 1,
                    CreatedAt = now
                },
                new TodoItem
                {
                    Id = "empty",
                    Text = "   ",
                    SortOrder = 2
                },
                new TodoItem
                {
                    Id = "",
                    Text = "new id",
                    ColorMarker = "blue",
                    SortOrder = 5
                }
            ]
        });

        var data = await store.LoadAsync();

        Assert.Equal(1, data.Version);
        Assert.Equal(2, data.Items.Count);
        Assert.Equal("duplicate", data.Items[0].Id);
        Assert.Equal("keep", data.Items[0].Text);
        Assert.Equal(TodoItem.RedColorMarker, data.Items[0].ColorMarker);
        Assert.Equal(0, data.Items[0].SortOrder);
        Assert.False(string.IsNullOrWhiteSpace(data.Items[1].Id));
        Assert.Equal("new id", data.Items[1].Text);
        Assert.Equal(TodoItem.BlueColorMarker, data.Items[1].ColorMarker);
        Assert.Equal(1, data.Items[1].SortOrder);
        Assert.NotEqual(default, data.Items[1].CreatedAt);
        Assert.NotEqual(default, data.Items[1].UpdatedAt);
    }

    [Fact]
    public async Task StoresAreIsolatedPerWidget()
    {
        await CreateStore("first").SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "first-item", Text = "first" }]
        });
        await CreateStore("second").SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "second-item", Text = "second" }]
        });

        var first = await CreateStore("first").LoadAsync();
        var second = await CreateStore("second").LoadAsync();

        Assert.Equal("first-item", Assert.Single(first.Items).Id);
        Assert.Equal("second-item", Assert.Single(second.Items).Id);
    }

    [Fact]
    public async Task StorePath_SanitizesWidgetIdForDirectoryName()
    {
        var store = CreateStore("todo:widget?bad");
        await store.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Text = "task" }]
        });

        string? directoryName = Path.GetDirectoryName(store.StorePath);
        Assert.NotNull(directoryName);
        string widgetDirectoryName = Path.GetFileName(directoryName);
        Assert.DoesNotContain(':', widgetDirectoryName);
        Assert.DoesNotContain('?', widgetDirectoryName);
        Assert.True(File.Exists(store.StorePath));
    }

    [Fact]
    public async Task SavedJson_UsesCamelCase()
    {
        var store = CreateStore("todo-widget");

        await store.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "item", Text = "task" }]
        });

        string json = await File.ReadAllTextAsync(store.StorePath);
        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.TryGetProperty("version", out _));
        Assert.True(document.RootElement.TryGetProperty("items", out var items));
        Assert.True(items[0].TryGetProperty("isCompleted", out _));
        Assert.True(items[0].TryGetProperty("dueDate", out _));
        Assert.True(items[0].TryGetProperty("colorMarker", out _));
        Assert.False(document.RootElement.TryGetProperty("Version", out _));
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

    private TodoWidgetStore CreateStore(string widgetId)
    {
        return new TodoWidgetStore(_widgetsDataRoot, widgetId);
    }
}
