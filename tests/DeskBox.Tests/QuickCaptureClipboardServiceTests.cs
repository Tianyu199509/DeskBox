using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class QuickCaptureClipboardServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _settingsRoot;
    private readonly string _storeRoot;

    public QuickCaptureClipboardServiceTests()
    {
        DeskBoxClipboardWriteScope.ClearForTesting();
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        _settingsRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "settings")).FullName;
        _storeRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "quick-capture")).FullName;
    }

    [Fact]
    public async Task CaptureCurrentForTestingAsync_DoesNotRecordWhenDisabled()
    {
        var settingsService = await CreateLoadedSettingsServiceAsync();
        var quickCaptureService = CreateQuickCaptureService();
        var reader = new FakeClipboardReader { Text = "disabled text" };
        using var service = new QuickCaptureClipboardService(settingsService, quickCaptureService, reader);

        await service.CaptureCurrentForTestingAsync();

        var data = await quickCaptureService.GetDataAsync();
        Assert.Empty(data.RecentItems);
        var diagnostics = service.GetDiagnostics();
        Assert.False(diagnostics.IsRecording);
        Assert.Equal("disabled:quick-capture-off", diagnostics.LastReason);
    }

    [Fact]
    public async Task CaptureCurrentForTestingAsync_RecordsTextWhenEnabled()
    {
        var settingsService = await CreateLoadedSettingsServiceAsync();
        EnableClipboardCapture(settingsService);
        var quickCaptureService = CreateQuickCaptureService();
        var reader = new FakeClipboardReader { Text = "captured text" };
        using var service = new QuickCaptureClipboardService(settingsService, quickCaptureService, reader);

        await service.CaptureCurrentForTestingAsync();

        var data = await quickCaptureService.GetDataAsync();
        var item = Assert.Single(data.RecentItems);
        Assert.Equal("captured text", item.Body);
        var diagnostics = service.GetDiagnostics();
        Assert.True(diagnostics.IsRecording);
        Assert.NotNull(diagnostics.LastCapturedAt);
        Assert.Equal("captured:Text", diagnostics.LastReason);
    }

    [Fact]
    public async Task CaptureCurrentForTestingAsync_RecordsImageWhenEnabled()
    {
        var settingsService = await CreateLoadedSettingsServiceAsync();
        EnableClipboardCapture(settingsService);
        var quickCaptureService = CreateQuickCaptureService();
        var reader = new FakeClipboardReader { ImagePngBytes = [1, 2, 3, 4] };
        using var service = new QuickCaptureClipboardService(settingsService, quickCaptureService, reader);

        await service.CaptureCurrentForTestingAsync();

        var data = await quickCaptureService.GetDataAsync();
        var item = Assert.Single(data.RecentItems);
        Assert.Equal(QuickCaptureItemType.Image, item.Type);
        Assert.True(File.Exists(item.ImagePath));
    }

    [Fact]
    public async Task CaptureCurrentForTestingAsync_IgnoresDeskBoxWrittenText()
    {
        var settingsService = await CreateLoadedSettingsServiceAsync();
        EnableClipboardCapture(settingsService);
        var quickCaptureService = CreateQuickCaptureService();
        var reader = new FakeClipboardReader { Text = "copied inside DeskBox" };
        using var service = new QuickCaptureClipboardService(settingsService, quickCaptureService, reader);

        DeskBoxClipboardWriteScope.MarkWrite(text: "copied inside DeskBox");
        await service.CaptureCurrentForTestingAsync();

        var data = await quickCaptureService.GetDataAsync();
        Assert.Empty(data.RecentItems);
        Assert.Equal("ignored:deskbox-write", service.GetDiagnostics().LastReason);
    }

    [Fact]
    public async Task CaptureCurrentForTestingAsync_IgnoresDeskBoxWrittenImage()
    {
        var settingsService = await CreateLoadedSettingsServiceAsync();
        EnableClipboardCapture(settingsService);
        var quickCaptureService = CreateQuickCaptureService();
        var reader = new FakeClipboardReader { ImagePngBytes = [1, 2, 3, 4] };
        using var service = new QuickCaptureClipboardService(settingsService, quickCaptureService, reader);

        DeskBoxClipboardWriteScope.MarkWrite(hasImage: true);
        await service.CaptureCurrentForTestingAsync();

        var data = await quickCaptureService.GetDataAsync();
        Assert.Empty(data.RecentItems);
        Assert.Equal("ignored:deskbox-write", service.GetDiagnostics().LastReason);
    }

    [Fact]
    public async Task CaptureCurrentForTestingAsync_IgnoresOversizedText()
    {
        var settingsService = await CreateLoadedSettingsServiceAsync();
        EnableClipboardCapture(settingsService);
        var quickCaptureService = CreateQuickCaptureService();
        var reader = new FakeClipboardReader
        {
            Text = new string('a', QuickCaptureClipboardService.MaxClipboardTextCharacters + 1)
        };
        using var service = new QuickCaptureClipboardService(settingsService, quickCaptureService, reader);

        await service.CaptureCurrentForTestingAsync();

        var data = await quickCaptureService.GetDataAsync();
        Assert.Empty(data.RecentItems);
        Assert.Equal("ignored:text-too-large", service.GetDiagnostics().LastReason);
    }

    [Fact]
    public async Task CaptureCurrentForTestingAsync_IgnoresOversizedImage()
    {
        var settingsService = await CreateLoadedSettingsServiceAsync();
        EnableClipboardCapture(settingsService);
        var quickCaptureService = CreateQuickCaptureService();
        var reader = new FakeClipboardReader
        {
            ImagePngBytes = new byte[QuickCaptureClipboardService.MaxClipboardImageBytes + 1]
        };
        using var service = new QuickCaptureClipboardService(settingsService, quickCaptureService, reader);

        await service.CaptureCurrentForTestingAsync();

        var data = await quickCaptureService.GetDataAsync();
        Assert.Empty(data.RecentItems);
        Assert.Equal("ignored:image-too-large", service.GetDiagnostics().LastReason);
    }

    [Fact]
    public async Task CaptureCurrentForTestingAsync_IgnoresDeskBoxWrittenPathText()
    {
        var settingsService = await CreateLoadedSettingsServiceAsync();
        EnableClipboardCapture(settingsService);
        var quickCaptureService = CreateQuickCaptureService();
        string filePath = Path.Combine(_tempRoot, "note.txt");
        var reader = new FakeClipboardReader { Text = filePath };
        using var service = new QuickCaptureClipboardService(settingsService, quickCaptureService, reader);

        DeskBoxClipboardWriteScope.MarkWrite(paths: [filePath]);
        await service.CaptureCurrentForTestingAsync();

        var data = await quickCaptureService.GetDataAsync();
        Assert.Empty(data.RecentItems);
    }

    [Fact]
    public async Task CaptureCurrentForTestingAsync_IgnoresImageWhenImageRecordingDisabled()
    {
        var settingsService = await CreateLoadedSettingsServiceAsync();
        EnableClipboardCapture(settingsService);
        settingsService.Settings.QuickCaptureImageClipboardEnabled = false;
        var quickCaptureService = CreateQuickCaptureService();
        var reader = new FakeClipboardReader { ImagePngBytes = [1, 2, 3, 4] };
        using var service = new QuickCaptureClipboardService(settingsService, quickCaptureService, reader);

        await service.CaptureCurrentForTestingAsync();

        var data = await quickCaptureService.GetDataAsync();
        Assert.Empty(data.RecentItems);
    }

    [Fact]
    public async Task Refresh_SubscribesAndCapturesWhenContentChanges()
    {
        var settingsService = await CreateLoadedSettingsServiceAsync();
        EnableClipboardCapture(settingsService);
        var quickCaptureService = CreateQuickCaptureService();
        var reader = new FakeClipboardReader { Text = "event text" };
        using var service = new QuickCaptureClipboardService(settingsService, quickCaptureService, reader);

        service.Refresh();
        reader.Text = "event text changed";
        reader.RaiseContentChanged();
        await Task.Delay(100);

        var data = await quickCaptureService.GetDataAsync();
        Assert.Contains(data.RecentItems, item => item.Body == "event text changed");
    }

    [Fact]
    public async Task CaptureCurrentForTestingAsync_IgnoresUnsupportedContent()
    {
        var settingsService = await CreateLoadedSettingsServiceAsync();
        EnableClipboardCapture(settingsService);
        var quickCaptureService = CreateQuickCaptureService();
        var reader = new FakeClipboardReader();
        using var service = new QuickCaptureClipboardService(settingsService, quickCaptureService, reader);

        await service.CaptureCurrentForTestingAsync();

        var data = await quickCaptureService.GetDataAsync();
        Assert.Empty(data.RecentItems);
    }

    private async Task<SettingsService> CreateLoadedSettingsServiceAsync()
    {
        var settingsService = new SettingsService(_settingsRoot);
        await settingsService.LoadAsync();
        return settingsService;
    }

    private QuickCaptureService CreateQuickCaptureService()
    {
        return new QuickCaptureService(new QuickCaptureStore(_storeRoot));
    }

    private static void EnableClipboardCapture(SettingsService settingsService)
    {
        settingsService.Settings.QuickCaptureEnabled = true;
        settingsService.Settings.QuickCaptureClipboardEnabled = true;
        settingsService.Settings.QuickCaptureImageClipboardEnabled = true;
        settingsService.Settings.HasConfirmedQuickCaptureClipboardNotice = true;
    }

    public void Dispose()
    {
        DeskBoxClipboardWriteScope.ClearForTesting();
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private sealed class FakeClipboardReader : IQuickCaptureClipboardReader
    {
        public event EventHandler<object>? ContentChanged;

        public string? Text { get; set; }

        public byte[]? ImagePngBytes { get; set; }

        public Task<QuickCaptureClipboardContent?> ReadContentAsync()
        {
            if (ImagePngBytes is { Length: > 0 })
            {
                return Task.FromResult<QuickCaptureClipboardContent?>(
                    QuickCaptureClipboardContent.FromImage(ImagePngBytes));
            }

            return Task.FromResult(
                string.IsNullOrWhiteSpace(Text)
                    ? null
                    : QuickCaptureClipboardContent.FromText(Text));
        }

        public void RaiseContentChanged()
        {
            ContentChanged?.Invoke(this, new object());
        }
    }
}
