using LanShare.Core;
using LanShare.Core.Infrastructure;
using Xunit;

namespace LanShare.Tests;

public sealed class SettingsTests
{
    [Fact]
    public async Task Initialize_WritesDefaults_AndReloadsSavedSettings()
    {
        var dir = TestEnv.NewDataDirectory();
        try
        {
            var log = new FileLogger(Path.Combine(dir, "logs"));
            var service = new SettingsService(dir, log);
            await service.InitializeAsync();

            Assert.Equal(Path.GetFullPath(dir), service.DataDirectory);
            Assert.True(File.Exists(service.SettingsPath));
            Assert.True(Directory.Exists(service.DefaultStorageDirectory));
            Assert.Equal("0.0.0.0", service.Current.BindAddress);
            Assert.Equal(8910, service.Current.Port);
            Assert.Equal(1440, service.Current.DefaultExpiryMinutes);
            Assert.Equal(2L * 1024 * 1024 * 1024, service.Current.MaxUploadBytes);

            var changed = 0;
            service.Changed += (_, _) => Interlocked.Increment(ref changed);

            var updated = service.Current;
            updated.Port = 9234;
            updated.BindAddress = "127.0.0.1";
            updated.MaxUploadBytes = 5; // Normalize clamps to the 1 MiB minimum
            updated.StorageDirectory = Path.Combine(dir, "custom-storage");
            await service.SaveAsync(updated);

            Assert.Equal(9234, service.Current.Port);
            Assert.Equal("127.0.0.1", service.Current.BindAddress);
            Assert.Equal(1024 * 1024, service.Current.MaxUploadBytes);
            Assert.Equal(1, changed);
            Assert.True(Directory.Exists(Path.Combine(dir, "custom-storage")));

            var reloaded = new SettingsService(dir);
            await reloaded.InitializeAsync();
            Assert.Equal(9234, reloaded.Current.Port);
            Assert.Equal("127.0.0.1", reloaded.Current.BindAddress);
            Assert.Equal(Path.Combine(dir, "custom-storage"), reloaded.Current.StorageDirectory);
            Assert.Equal(Path.Combine(dir, "custom-storage"),
                SettingsService.ResolveStorageDirectory(reloaded.Current, dir));
            Assert.Equal(Path.Combine(dir, "storage"),
                SettingsService.ResolveStorageDirectory(new AppSettings(), dir));
        }
        finally
        {
            TestEnv.TryDelete(dir);
        }
    }

    [Fact]
    public async Task CorruptSettingsFile_IsQuarantined_AndDefaultsAreUsed()
    {
        var dir = TestEnv.NewDataDirectory();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            await File.WriteAllTextAsync(path, "{ this is not json !!");
            var service = new SettingsService(dir, new FileLogger(Path.Combine(dir, "logs")));
            await service.InitializeAsync();

            Assert.Equal(8910, service.Current.Port);
            Assert.True(File.Exists(service.SettingsPath));
            Assert.NotEmpty(Directory.GetFiles(dir, "settings.json.broken-*"));
        }
        finally
        {
            TestEnv.TryDelete(dir);
        }
    }

    [Fact]
    public async Task SaveAsync_RejectsAnUnusableStorageDirectory_WithChineseMessage()
    {
        var dir = TestEnv.NewDataDirectory();
        try
        {
            var service = new SettingsService(dir);
            await service.InitializeAsync();

            var settings = service.Current;
            settings.StorageDirectory = "\0invalid";
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(settings));
            Assert.Contains("存储目录", ex.Message);
        }
        finally
        {
            TestEnv.TryDelete(dir);
        }
    }
}
