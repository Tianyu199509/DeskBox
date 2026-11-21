using System.Reflection;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Dispatching;
using Xunit;

namespace DeskBox.Tests;

public sealed class CustomWidgetCapabilityHostTests
{
    [Fact]
    public void CapabilityHost_RegistersAll15UniqueProviders()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-caphost-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var config = new WidgetConfig
            {
                Id = "cap-host-15",
                WidgetKind = WidgetKind.Custom,
                Name = "Cap Host Test",
            };
            var store = new CustomWidgetDataStore(config.Id, tempRoot);
            using var vm = new CustomWidgetViewModel(
                config,
                TestServices.CreateLocalizationService(),
                store,
                dispatcherQueue: null,
                isPreviewOnly: true);

            IReadOnlyList<ICustomWidgetCapabilityProvider> providers = vm.CapabilityHost.Providers;
            Assert.Equal(15, providers.Count);

            string[] providerIds = providers.Select(p => p.ProviderId).ToArray();
            Assert.All(providerIds, id => Assert.False(string.IsNullOrWhiteSpace(id)));
            Assert.Equal(15, providerIds.Distinct(StringComparer.Ordinal).Count());

            string[] expectedIds =
            [
                "systemMetrics",
                "pomodoro",
                "countdown",
                "calendar",
                "weather",
                "deviceState",
                "clipboard",
                "storageDrives",
                "music",
                "todo",
                "clock",
                "aiMonitor",
                "network",
                "fileText",
                "command",
            ];
            Assert.Equal(
                expectedIds.OrderBy(id => id, StringComparer.Ordinal),
                providerIds.OrderBy(id => id, StringComparer.Ordinal));

            Assert.All(providers, provider =>
            {
                Assert.NotEmpty(provider.CapabilityIds);
                Assert.All(provider.CapabilityIds, capId => Assert.False(string.IsNullOrWhiteSpace(capId)));
            });
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void PlainTextPreviewWidget_PreservesLazySystemMetrics_AndDisposeIsIdempotent()
    {
        const string plainTextManifest = """
        {
          "schemaVersion": 1,
          "id": "deskbox.test.plaintext",
          "name": "Plain Text",
          "defaultSize": { "width": 260, "height": 150 },
          "sources": [],
          "root": "main",
          "elements": {
            "main": {
              "type": "Stack",
              "children": ["greeting_text"]
            },
            "greeting_text": { "type": "Text", "text": "Hello DeskBox" }
          }
        }
        """;

        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-caphost-lazy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var config = new WidgetConfig
            {
                Id = "cap-host-lazy",
                WidgetKind = WidgetKind.Custom,
                Name = "Plain Text Lazy Check",
            };
            var store = new CustomWidgetDataStore(config.Id, tempRoot);
            using var vm = new CustomWidgetViewModel(
                config,
                TestServices.CreateLocalizationService(),
                store,
                dispatcherQueue: null,
                isPreviewOnly: true);

            vm.ApplyPreviewBundle(plainTextManifest, null, null, preserveRuntimeData: false);

            Assert.DoesNotContain(
                vm.ManifestInternal.Sources,
                s => string.Equals(s.Type, "systemMetrics", StringComparison.OrdinalIgnoreCase));

            FieldInfo? subscribedField = typeof(SystemMetricsCapabilityProvider).GetField(
                "_isSubscribedToSystemMetrics",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(subscribedField);
            Assert.False((bool)subscribedField!.GetValue(vm.CapabilityHost.SystemMetrics)!);

            if (CustomWidgetSystemMetricsSource.Shared.ActiveSubscriberCount == 0)
            {
                Assert.False(CustomWidgetSystemMetricsSource.Shared.HasOpenGpuSamplers);
            }

            vm.Dispose();
            vm.Dispose();

            Assert.False((bool)subscribedField.GetValue(vm.CapabilityHost.SystemMetrics)!);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void CustomWidgetViewModel_DeclaresZeroTimersAndZeroSnapshotFields()
    {
        const BindingFlags DeclaredFields =
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.DeclaredOnly;

        FieldInfo[] fields = typeof(CustomWidgetViewModel).GetFields(DeclaredFields);

        Assert.Empty(fields.Where(f => f.FieldType == typeof(DispatcherQueueTimer)));
        Assert.Empty(fields.Where(f => f.FieldType.Name.EndsWith("Snapshot", StringComparison.Ordinal)));
    }
}
