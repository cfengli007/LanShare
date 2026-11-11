using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LanShare.Core;
using LanShare.Core.Infrastructure;
using Xunit;

namespace LanShare.Tests;

public sealed class ShareServiceTests
{
    private sealed class Fixture
    {
        public required string DataDirectory { get; init; }
        public required SettingsService Settings { get; init; }
        public required SevenZipTool SevenZip { get; init; }
        public required ShareService Shares { get; init; }
        public required FileLogger Logger { get; init; }
        public string SourceDirectory => Path.Combine(DataDirectory, "source");
        public string SharesJsonPath => Path.Combine(DataDirectory, "shares.json");

        public static async Task<Fixture> CreateAsync(Action<AppSettings>? configure = null)
        {
            var dir = TestEnv.NewDataDirectory();
            var logger = new FileLogger(Path.Combine(dir, "logs"));
            var settings = new SettingsService(dir, logger);
            await settings.InitializeAsync();
            if (configure != null)
            {
                var s = settings.Current;
                configure(s);
                await settings.SaveAsync(s);
            }

            var seven = new SevenZipTool(null, logger);
            seven.Refresh();
            Assert.True(seven.IsAvailable, "测试需要 assets/tools/7za.exe（应已复制到输出目录 Tools）。");
            var shares = new ShareService(settings, seven, logger);
            Directory.CreateDirectory(Path.Combine(dir, "source"));
            return new Fixture
            {
                DataDirectory = dir,
                Settings = settings,
                SevenZip = seven,
                Shares = shares,
                Logger = logger
            };
        }

        public string WriteSource(string relativePath, string content)
        {
            var full = Path.Combine(SourceDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, Encoding.UTF8);
            return full;
        }

        public string WriteTemp(string fileName, byte[] content)
        {
            var dir = Path.Combine(DataDirectory, "incoming");
            Directory.CreateDirectory(dir);
            var full = Path.Combine(dir, fileName);
            File.WriteAllBytes(full, content);
            return full;
        }

        public string Extract(string archive)
        {
            var dir = Path.Combine(DataDirectory, "extract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            SevenZip.ExtractAsync(archive, dir).GetAwaiter().GetResult();
            return dir;
        }
    }

    [Fact]
    public async Task SingleFileReferenceShare_NeverDeletesTheUserFile()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var source = f.WriteSource("报告.txt", "hello lanshare");
            var share = await f.Shares.CreateFromPathsAsync(new[] { source }, "desktop",
                TimeSpan.FromMinutes(30), false, "desktop");

            Assert.False(share.IsUploaded);
            Assert.False(share.IsPackaged);
            Assert.Equal("desktop", share.OwnerId);
            Assert.Equal(Path.GetFullPath(source), Path.GetFullPath(share.DownloadPath!));
            Assert.Empty(share.OwnedPaths);
            Assert.Equal(11, share.TotalBytes);
            Assert.Equal("报告.txt", share.DownloadName);
            Assert.Equal("报告.txt", f.Shares.GetDownloadName(share));
            Assert.Equal(Path.GetFullPath(share.DownloadPath!), Path.GetFullPath(f.Shares.GetDownloadPath(share)!));

            Assert.False(await f.Shares.RevokeAsync(share.Id, "someone-else", true));
            Assert.True(File.Exists(source));

            Assert.True(await f.Shares.RevokeAsync(share.Id, "desktop", true));
            Assert.True(File.Exists(source));
            var reloaded = await f.Shares.GetAsync(share.Id);
            Assert.NotNull(reloaded);
            Assert.True(reloaded!.IsRevoked);
            Assert.Null(reloaded.DownloadPath);
            Assert.Empty(await f.Shares.GetAllAsync());
            Assert.Empty(await f.Shares.GetByOwnerAsync("desktop"));
        }
        finally
        {
            TestEnv.TryDelete(f.DataDirectory);
        }
    }

    [Fact]
    public async Task DirectoryShare_PacksTopFolderName_AndRevokeDeletesOnlyTheArchive()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var root = Path.Combine(f.SourceDirectory, "资料");
            Directory.CreateDirectory(Path.Combine(root, "子目录"));
            File.WriteAllText(Path.Combine(root, "根.txt"), "root");
            File.WriteAllText(Path.Combine(root, "子目录", "嵌套.txt"), "nested");

            var share = await f.Shares.CreateFromPathsAsync(new[] { root }, "desktop",
                TimeSpan.FromMinutes(30), false, "desktop");

            Assert.True(share.IsPackaged);
            Assert.EndsWith(".7z", share.DownloadName);
            Assert.Single(share.Files);
            Assert.Equal("资料", share.Files[0].Name);
            Assert.True(share.TotalBytes > 0);
            Assert.True(File.Exists(share.DownloadPath));

            var extract = f.Extract(share.DownloadPath!);
            Assert.Equal("root", File.ReadAllText(Path.Combine(extract, "资料", "根.txt")));
            Assert.Equal("nested", File.ReadAllText(Path.Combine(extract, "资料", "子目录", "嵌套.txt")));

            var shareStorage = Path.GetDirectoryName(share.DownloadPath!)!;
            Assert.True(Directory.Exists(shareStorage));
            Assert.True(await f.Shares.RevokeAsync(share.Id, "desktop", true));
            Assert.False(Directory.Exists(shareStorage));
            Assert.True(Directory.Exists(root));
            Assert.True(File.Exists(Path.Combine(root, "根.txt")));
        }
        finally
        {
            TestEnv.TryDelete(f.DataDirectory);
        }
    }

    [Fact]
    public async Task MultiplePaths_WithDuplicateNames_ArePackedWithUniqueEntries()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var one = f.WriteSource(Path.Combine("一", "report.txt"), "one");
            var two = f.WriteSource(Path.Combine("二", "report.txt"), "two");
            var share = await f.Shares.CreateFromPathsAsync(new[] { one, two }, "desktop",
                TimeSpan.FromMinutes(30), false, "desktop");

            Assert.True(share.IsPackaged);
            Assert.Equal(2, share.Files.Count);
            var names = share.Files.Select(x => x.Name).ToList();
            Assert.Equal(2, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());

            var extract = f.Extract(share.DownloadPath!);
            var contents = Directory.GetFiles(extract, "*.txt", SearchOption.AllDirectories)
                .Select(File.ReadAllText).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { "one", "two" }, contents);
        }
        finally
        {
            TestEnv.TryDelete(f.DataDirectory);
        }
    }

    [Fact]
    public async Task UploadedFiles_AreMovedIntoStorage_AndSingleUploadIsDirect()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var temp = f.WriteTemp("tmp1.upload", Encoding.UTF8.GetBytes("uploaded"));
            var upload = new UploadedFile(temp, "上传文件.txt", 8);
            var share = await f.Shares.CreateFromUploadsAsync(new[] { upload }, "owner-a",
                TimeSpan.FromMinutes(15), false, "");

            Assert.True(share.IsUploaded);
            Assert.False(share.IsPackaged);
            Assert.Equal("web", share.Source);
            Assert.False(File.Exists(temp));
            Assert.True(File.Exists(share.DownloadPath));
            Assert.Contains(Path.Combine("storage", share.Id), share.DownloadPath);
            Assert.Single(share.OwnedPaths);
            Assert.Equal("上传文件.txt", share.DownloadName);
            Assert.Equal("uploaded", File.ReadAllText(share.DownloadPath!));

            Assert.True(await f.Shares.RevokeAsync(share.Id, "owner-a", true));
            Assert.False(File.Exists(share.DownloadPath!));
        }
        finally
        {
            TestEnv.TryDelete(f.DataDirectory);
        }
    }

    [Fact]
    public async Task BurnAfterDownload_DestroysUploadedPayload()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var temp = f.WriteTemp("burn.upload", Encoding.UTF8.GetBytes("burn me"));
            var share = await f.Shares.CreateFromUploadsAsync(
                new[] { new UploadedFile(temp, "burn.txt", 7) }, "owner", TimeSpan.FromMinutes(15), true, "");

            var registered = await f.Shares.RegisterDownloadAsync(share.Id);
            Assert.NotNull(registered);
            Assert.Equal(1, registered!.DownloadCount);

            await f.Shares.CompleteDownloadAsync(share.Id);

            var after = await f.Shares.GetAsync(share.Id);
            Assert.NotNull(after);
            Assert.True(after!.IsRevoked);
            Assert.Null(after.DownloadPath);
            Assert.False(File.Exists(share.DownloadPath!));
            Assert.Null(await f.Shares.RegisterDownloadAsync(share.Id));
        }
        finally
        {
            TestEnv.TryDelete(f.DataDirectory);
        }
    }

    [Fact]
    public async Task OversizedUpload_IsRejectedBeforeMovingFiles()
    {
        var f = await Fixture.CreateAsync(s => s.MaxUploadBytes = 1024 * 1024);
        try
        {
            var temp = f.WriteTemp("big.upload", new byte[1024 * 1024 + 10]);
            var ex = await Assert.ThrowsAsync<ShareTooLargeException>(() =>
                f.Shares.CreateFromUploadsAsync(new[] { new UploadedFile(temp, "big.bin", new FileInfo(temp).Length) },
                    "owner", TimeSpan.FromMinutes(10), false, ""));
            Assert.Equal(1024 * 1024, ex.LimitBytes);
            Assert.True(ex.ActualBytes > ex.LimitBytes);
            Assert.True(File.Exists(temp));
        }
        finally
        {
            TestEnv.TryDelete(f.DataDirectory);
        }
    }

    [Fact]
    public async Task CleanupExpired_DeletesUploadedPayloads_ButNotReferencedUserFiles()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var uploadedTemp = f.WriteTemp("expire.upload", Encoding.UTF8.GetBytes("expire"));
            var uploaded = await f.Shares.CreateFromUploadsAsync(
                new[] { new UploadedFile(uploadedTemp, "expire.txt", 6) }, "owner",
                TimeSpan.FromMinutes(5), false, "");
            var uploadStorage = Path.GetDirectoryName(uploaded.DownloadPath!)!;

            var referencedFile = f.WriteSource("keep.txt", "keep me");
            var referenced = await f.Shares.CreateFromPathsAsync(new[] { referencedFile }, "desktop",
                TimeSpan.FromMinutes(5), false, "desktop");

            var json = JsonNode.Parse(await File.ReadAllTextAsync(f.SharesJsonPath))!.AsArray();
            foreach (var node in json)
            {
                node!["expiresAt"] = DateTimeOffset.UtcNow.AddHours(-2).ToString("O");
            }
            await File.WriteAllTextAsync(f.SharesJsonPath, json.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            var reloaded = new ShareService(f.Settings, f.SevenZip, f.Logger);
            var cleaned = await reloaded.CleanupExpiredAsync();

            Assert.Equal(1, cleaned);
            Assert.False(Directory.Exists(uploadStorage));
            Assert.True(File.Exists(referencedFile));
            var uploadedAfter = await reloaded.GetAsync(uploaded.Id);
            Assert.True(uploadedAfter!.IsRevoked);
            var referencedAfter = await reloaded.GetAsync(referenced.Id);
            Assert.NotNull(referencedAfter);
            Assert.False(referencedAfter!.IsRevoked);
            Assert.True(referencedAfter.IsExpired);
            Assert.NotNull(reloaded.GetDownloadPath(referencedAfter));
        }
        finally
        {
            TestEnv.TryDelete(f.DataDirectory);
        }
    }

    [Fact]
    public async Task OwnerFiltering_And_GetAll_ExcludeRevoked_AndSortByCreatedAtDescending()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var a1 = f.WriteSource("a1.txt", "a1");
            var a2 = f.WriteSource("a2.txt", "a2");
            var b1 = f.WriteSource("b1.txt", "b1");
            var shareA1 = await f.Shares.CreateFromPathsAsync(new[] { a1 }, "owner-a", TimeSpan.FromMinutes(10), false, "web");
            await Task.Delay(20);
            var shareA2 = await f.Shares.CreateFromPathsAsync(new[] { a2 }, "owner-a", TimeSpan.FromMinutes(10), false, "web");
            var shareB = await f.Shares.CreateFromPathsAsync(new[] { b1 }, "owner-b", TimeSpan.FromMinutes(10), false, "web");

            var all = await f.Shares.GetAllAsync();
            Assert.Equal(3, all.Count);
            Assert.Equal(shareA2.Id, all[0].Id);

            var ownerA = await f.Shares.GetByOwnerAsync("owner-a");
            Assert.Equal(2, ownerA.Count);
            Assert.Equal(shareA2.Id, ownerA[0].Id);
            Assert.Equal(shareA1.Id, ownerA[1].Id);
            Assert.Single(await f.Shares.GetByOwnerAsync("owner-b"));

            await f.Shares.RevokeAsync(shareB.Id, "owner-b", true);
            Assert.Empty(await f.Shares.GetByOwnerAsync("owner-b"));
        }
        finally
        {
            TestEnv.TryDelete(f.DataDirectory);
        }
    }
}
