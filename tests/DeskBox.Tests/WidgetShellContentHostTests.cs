using DeskBox.Contracts;
using DeskBox.Controls;
using DeskBox.Models;
using Microsoft.UI.Xaml;

namespace DeskBox.Tests;

public sealed class WidgetShellContentHostTests
{
    [Fact]
    public async Task SetContentAsync_InitializesSetsAndAppliesAppearance()
    {
        var calls = new List<string>();
        var content = new TestWidgetContent("first", WidgetKind.Todo, calls);
        var host = new WidgetShellContentHost(setContent: c => calls.Add($"set:{c.WidgetId}"));

        await host.SetContentAsync(content);

        Assert.Equal(content, host.CurrentContent);
        Assert.Equal(
        [
            "initialize:first",
            "set:first",
            "appearance:first"
        ], calls);
    }

    [Fact]
    public async Task SetContentAsync_DeactivatesPreviousContentWhenReplacing()
    {
        var calls = new List<string>();
        var first = new TestWidgetContent("first", WidgetKind.File, calls);
        var second = new TestWidgetContent("second", WidgetKind.Todo, calls);
        var host = new WidgetShellContentHost(setContent: c => calls.Add($"set:{c.WidgetId}"));

        await host.SetContentAsync(first);
        await host.SetContentAsync(second);

        Assert.Equal(second, host.CurrentContent);
        Assert.Equal(
        [
            "initialize:first",
            "set:first",
            "appearance:first",
            "initialize:second",
            "deactivate:first",
            "set:second",
            "appearance:second"
        ], calls);
    }

    [Fact]
    public async Task RefreshAndActivationCallbacks_ForwardToCurrentContent()
    {
        var calls = new List<string>();
        var content = new TestWidgetContent("first", WidgetKind.QuickCapture, calls);
        var host = new WidgetShellContentHost(setContent: _ => { });
        await host.SetContentAsync(content);
        calls.Clear();

        await host.RefreshAsync();
        host.OnActivated();
        host.OnDeactivated();
        host.ApplyAppearance();

        Assert.Equal(
        [
            "refresh:first",
            "activate:first",
            "deactivate:first",
            "appearance:first"
        ], calls);
    }

    private sealed class TestWidgetContent : IWidgetContent
    {
        private readonly List<string> _calls;

        public TestWidgetContent(string id, WidgetKind widgetKind, List<string> calls)
        {
            _calls = calls;
            Config = new WidgetConfig
            {
                Id = id,
                Name = id,
                WidgetKind = widgetKind
            };
        }

        public WidgetConfig Config { get; }

        public string WidgetId => Config.Id;

        public WidgetKind WidgetKind => Config.WidgetKind;

        public FrameworkElement View => throw new NotSupportedException("Tests do not instantiate WinUI views.");

        public Task InitializeAsync()
        {
            _calls.Add($"initialize:{WidgetId}");
            return Task.CompletedTask;
        }

        public Task RefreshAsync()
        {
            _calls.Add($"refresh:{WidgetId}");
            return Task.CompletedTask;
        }

        public void ApplyAppearance()
        {
            _calls.Add($"appearance:{WidgetId}");
        }

        public void OnActivated()
        {
            _calls.Add($"activate:{WidgetId}");
        }

        public void OnDeactivated()
        {
            _calls.Add($"deactivate:{WidgetId}");
        }
    }
}
