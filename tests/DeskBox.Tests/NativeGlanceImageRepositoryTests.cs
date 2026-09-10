extern alias GlancePkg;

using Repository = GlancePkg::DeskBox.GlancePackage.Services.GlanceImageRepository;
using Service = GlancePkg::DeskBox.Services.GlanceImageService;
using Data = GlancePkg::DeskBox.Models.GlanceWidgetData;
using Source = GlancePkg::DeskBox.Models.GlanceBackgroundSource;

namespace DeskBox.Tests;

public sealed class NativeGlanceImageRepositoryTests
{
    [Fact]
    public async Task InstancesSharePreparationAndCancellingOneWaitDoesNotCancelTheOther()
    {
        string root = Directory.CreateTempSubdirectory("glance-shared-cache").FullName;
        try
        {
            using var client = new HttpClient();
            var service = new Service(root, client, () => false);
            var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int starts = 0;
            CancellationToken importToken = default;
            using var repository = new Repository(service, ct =>
            {
                starts++;
                importToken = ct;
                return prepared.Task;
            });
            using var firstLifetime = new CancellationTokenSource();
            var settings = new Data { BackgroundSource = Source.Bing };
            var first = repository.GetAvailableAsync(settings, firstLifetime.Token);
            var second = repository.GetAvailableAsync(settings, CancellationToken.None);
            firstLifetime.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.False(importToken.IsCancellationRequested);
            Assert.False(second.IsCompleted);
            prepared.SetResult();
            Assert.Empty(await second);
            Assert.Empty(await repository.RefreshOnlineAsync(settings, CancellationToken.None));
            Assert.Equal(1, starts);
            repository.Dispose();
            Assert.True(importToken.IsCancellationRequested);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LocalPicturesDoNotWaitForUnrelatedOnlineCacheMigration()
    {
        string root = Directory.CreateTempSubdirectory("glance-local-cache").FullName;
        try
        {
            using var client = new HttpClient();
            int starts = 0;
            using var repository = new Repository(new Service(root, client, () => false), _ =>
            {
                starts++;
                return Task.CompletedTask;
            });
            var result = await repository.GetAvailableAsync(new Data { BackgroundSource = Source.LocalFiles }, CancellationToken.None);
            Assert.Empty(result);
            Assert.Equal(0, starts);
        }
        finally { Directory.Delete(root, true); }
    }
}
