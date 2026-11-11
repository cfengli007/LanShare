using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LanShare.Core;
using LanShare.Server;
using Xunit;

namespace LanShare.Tests;

public sealed class ApiTests
{
    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? string.Join("; ", values) : null;

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
        Assert.True(await condition(), "等待条件超时。");
    }

    [Fact]
    public async Task Health_Config_Cookie_And_Static_Ui_Work()
    {
        await using var rt = await TestRuntime.StartAsync();

        var health = await rt.Client.GetAsync("api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var healthJson = await TestRuntime.JsonAsync(health);
        Assert.Equal("ok", healthJson.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(healthJson.GetProperty("version").GetString()));
        Assert.True(healthJson.GetProperty("uptimeSeconds").GetDouble() >= 0);

        var config = await rt.Client.GetAsync("api/config");
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);
        var configJson = await TestRuntime.JsonAsync(config);
        Assert.Equal(LanShareConstants.ProductName, configJson.GetProperty("productName").GetString());
        Assert.True(configJson.GetProperty("maxUploadBytes").GetInt64() > 0);
        Assert.Equal(JsonValueKind.Array, configJson.GetProperty("lanAddresses").ValueKind);

        var stranger = rt.NewClient();
        var shares = await stranger.GetAsync("api/shares");
        Assert.Equal(HttpStatusCode.OK, shares.StatusCode);
        var setCookie = Header(shares, "Set-Cookie");
        Assert.NotNull(setCookie);
        Assert.Contains(LanShareConstants.OwnerCookieName, setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        var sharesJson = await TestRuntime.JsonAsync(shares);
        Assert.Equal(JsonValueKind.Array, sharesJson.GetProperty("shares").ValueKind);

        var home = await rt.Client.GetAsync("");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Equal("text/html", home.Content.Headers.ContentType?.MediaType);
        var html = await home.Content.ReadAsStringAsync();
        Assert.Contains("<", html);
        Assert.NotNull(Header(home, "Set-Cookie"));

        var favicon = await rt.Client.GetAsync("favicon.ico");
        Assert.Equal(HttpStatusCode.OK, favicon.StatusCode);
        Assert.Equal("image/x-icon", favicon.Content.Headers.ContentType?.MediaType);

        var missingApi = await rt.Client.GetAsync("api/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, missingApi.StatusCode);
        Assert.Equal("application/json", missingApi.Content.Headers.ContentType?.MediaType);
        var missingJson = await TestRuntime.JsonAsync(missingApi);
        Assert.Equal("not_found", missingJson.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(missingJson.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Upload_SingleFile_Download_Range_Head_And_OwnerIsolation()
    {
        await using var rt = await TestRuntime.StartAsync();
        var content = Encoding.UTF8.GetBytes("你好，LanShare！0123456789");

        using var form = TestRuntime.Multipart(new[] { ("中文 报告.txt", content) }, 60, false, "季度报告");
        var response = await rt.Client.PostAsync("api/shares", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await TestRuntime.JsonAsync(response);
        var share = json.GetProperty("share");
        var id = share.GetProperty("id").GetString()!;
        Assert.Equal(22, id.Length);
        Assert.False(share.GetProperty("isPackaged").GetBoolean());
        Assert.Equal("季度报告", share.GetProperty("displayName").GetString());
        Assert.Equal("中文 报告.txt", share.GetProperty("files")[0].GetProperty("name").GetString());
        Assert.Equal(content.Length, share.GetProperty("totalBytes").GetInt64());
        Assert.False(share.GetProperty("isExpired").GetBoolean());
        Assert.Contains("/s/" + id, share.GetProperty("internalUrl").GetString());

        var stranger = rt.NewClient();
        var ownerView = await stranger.GetAsync("api/shares/" + id);
        Assert.Equal(HttpStatusCode.NotFound, ownerView.StatusCode);

        var publicView = await stranger.GetAsync("api/public/shares/" + id);
        Assert.Equal(HttpStatusCode.OK, publicView.StatusCode);
        var publicJson = await TestRuntime.JsonAsync(publicView);
        var pub = publicJson.GetProperty("share");
        Assert.Equal(id, pub.GetProperty("id").GetString());
        Assert.Equal("/d/" + id, pub.GetProperty("downloadUrl").GetString());
        Assert.False(pub.TryGetProperty("downloadPath", out _));
        Assert.False(pub.TryGetProperty("ownedPaths", out _));
        Assert.False(pub.TryGetProperty("source", out _));

        var head = await rt.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "d/" + id));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(content.Length, head.Content.Headers.ContentLength);
        var afterHead = await TestRuntime.JsonAsync(await rt.Client.GetAsync("api/shares/" + id));
        Assert.Equal(0, afterHead.GetProperty("share").GetProperty("downloadCount").GetInt32());

        var rangeRequest = new HttpRequestMessage(HttpMethod.Get, "d/" + id);
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 5);
        var range = await rt.Client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal(6, range.Content.Headers.ContentLength);
        Assert.Equal($"bytes 0-5/{content.Length}", range.Content.Headers.ContentRange?.ToString());
        Assert.Equal(content.AsSpan(0, 6).ToArray(), await range.Content.ReadAsByteArrayAsync());

        var download = await rt.Client.GetAsync("d/" + id);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType?.MediaType);
        var disposition = Header(download, "Content-Disposition");
        Assert.NotNull(disposition);
        Assert.Contains("attachment", disposition);
        Assert.Contains("filename*=UTF-8''", disposition);
        Assert.Contains(Uri.EscapeDataString("中文 报告.txt"), disposition);
        Assert.Equal(content, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes", Header(download, "Accept-Ranges"));

        var after = await TestRuntime.JsonAsync(await rt.Client.GetAsync("api/shares/" + id));
        Assert.Equal(2, after.GetProperty("share").GetProperty("downloadCount").GetInt32());

        var page = await stranger.GetAsync("s/" + id);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Upload_MultipleFiles_Produces_A_Real_7z_Archive()
    {
        await using var rt = await TestRuntime.StartAsync();
        var a = Encoding.UTF8.GetBytes("file-a");
        var b = Encoding.UTF8.GetBytes("file-b-longer");

        using var form = TestRuntime.Multipart(new[] { ("a.txt", a), ("b.txt", b) }, 30, false, null);
        var response = await rt.Client.PostAsync("api/shares", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var share = (await TestRuntime.JsonAsync(response)).GetProperty("share");
        var id = share.GetProperty("id").GetString()!;
        Assert.True(share.GetProperty("isPackaged").GetBoolean());
        Assert.Equal("2 个文件", share.GetProperty("displayName").GetString());
        Assert.EndsWith(".7z", share.GetProperty("downloadName").GetString());
        Assert.Equal(2, share.GetProperty("files").GetArrayLength());

        var download = await rt.Client.GetAsync("d/" + id);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/x-7z-compressed", download.Content.Headers.ContentType?.MediaType);
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 32);
        Assert.Equal(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, bytes.Take(6).ToArray());

        var extractDir = Path.Combine(rt.DataDirectory, "extract");
        Directory.CreateDirectory(extractDir);
        var archivePath = Path.Combine(rt.DataDirectory, "download.7z");
        await File.WriteAllBytesAsync(archivePath, bytes);
        await rt.Concrete.SevenZip.ExtractAsync(archivePath, extractDir);
        Assert.Equal("file-a", File.ReadAllText(Path.Combine(extractDir, "a.txt")));
        Assert.Equal("file-b-longer", File.ReadAllText(Path.Combine(extractDir, "b.txt")));
    }

    [Fact]
    public async Task Upload_Validation_And_Size_Limit_Return_Chinese_Errors()
    {
        await using var rt = await TestRuntime.StartAsync(s => { s.MaxUploadBytes = 8; s.DefaultExpiryMinutes = 60; });

        using var noFiles = TestRuntime.Multipart(Array.Empty<(string, byte[])>(), 60);
        var noFilesResponse = await rt.Client.PostAsync("api/shares", noFiles);
        Assert.Equal(HttpStatusCode.BadRequest, noFilesResponse.StatusCode);
        var noFilesJson = await TestRuntime.JsonAsync(noFilesResponse);
        Assert.Equal("bad_request", noFilesJson.GetProperty("code").GetString());
        Assert.Contains("文件", noFilesJson.GetProperty("error").GetString());

        using var badExpiry = TestRuntime.Multipart(new[] { ("x.txt", new byte[] { 1 }) }, 0);
        var badExpiryResponse = await rt.Client.PostAsync("api/shares", badExpiry);
        Assert.Equal(HttpStatusCode.BadRequest, badExpiryResponse.StatusCode);
        Assert.Equal("bad_request", (await TestRuntime.JsonAsync(badExpiryResponse)).GetProperty("code").GetString());

        using var badBurn = TestRuntime.Multipart(new[] { ("x.txt", new byte[] { 1 }) }, 60, null, null);
        badBurn.Add(new StringContent("maybe"), "burn");
        var badBurnResponse = await rt.Client.PostAsync("api/shares", badBurn);
        Assert.Equal(HttpStatusCode.BadRequest, badBurnResponse.StatusCode);

        using var tooLarge = TestRuntime.Multipart(new[] { ("big.bin", new byte[64]) }, 60);
        var tooLargeResponse = await rt.Client.PostAsync("api/shares", tooLarge);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLargeResponse.StatusCode);
        var tooLargeJson = await TestRuntime.JsonAsync(tooLargeResponse);
        Assert.Equal("too_large", tooLargeJson.GetProperty("code").GetString());
        Assert.Contains("过大", tooLargeJson.GetProperty("error").GetString());

        using var notMultipart = new StringContent("{}", Encoding.UTF8, "application/json");
        var notMultipartResponse = await rt.Client.PostAsync("api/shares", notMultipart);
        Assert.Equal(HttpStatusCode.BadRequest, notMultipartResponse.StatusCode);
    }

    [Fact]
    public async Task Revoke_And_BurnAfterDownload_Invalidate_The_Share()
    {
        await using var rt = await TestRuntime.StartAsync();

        using var form = TestRuntime.Multipart(new[] { ("secret.txt", Encoding.UTF8.GetBytes("secret")) }, 60, true);
        var created = await (await rt.Client.PostAsync("api/shares", form)).Content.ReadAsStringAsync();
        using var createdDoc = JsonDocument.Parse(created);
        var id = createdDoc.RootElement.GetProperty("share").GetProperty("id").GetString()!;
        Assert.True(createdDoc.RootElement.GetProperty("share").GetProperty("burnAfterDownload").GetBoolean());

        var nonOwner = rt.NewClient();
        var forbidden = await nonOwner.PostAsync("api/shares/" + id + "/revoke", new StringContent(""));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var download = await rt.Client.GetAsync("d/" + id);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("secret", await download.Content.ReadAsStringAsync());

        await WaitUntilAsync(async () =>
        {
            var response = await rt.Client.GetAsync("api/public/shares/" + id);
            return response.StatusCode == HttpStatusCode.Gone;
        });

        var ownerList = await TestRuntime.JsonAsync(await rt.Client.GetAsync("api/shares"));
        Assert.Equal(0, ownerList.GetProperty("shares").GetArrayLength());
        Assert.Equal(HttpStatusCode.Gone, (await rt.Client.GetAsync("d/" + id)).StatusCode);

        using var secondForm = TestRuntime.Multipart(new[] { ("keep.txt", Encoding.UTF8.GetBytes("keep")) }, 60, false);
        var second = await (await rt.Client.PostAsync("api/shares", secondForm)).Content.ReadAsStringAsync();
        using var secondDoc = JsonDocument.Parse(second);
        var secondId = secondDoc.RootElement.GetProperty("share").GetProperty("id").GetString()!;

        var revoke = await rt.Client.PostAsync("api/shares/" + secondId + "/revoke", new StringContent(""));
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        var revokeJson = await TestRuntime.JsonAsync(revoke);
        Assert.True(revokeJson.GetProperty("ok").GetBoolean());
        Assert.Equal(HttpStatusCode.Gone, (await rt.Client.GetAsync("api/public/shares/" + secondId)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await rt.Client.PostAsync("api/shares/" + secondId + "/revoke", new StringContent(""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rt.Client.GetAsync("api/shares/unknown123")).StatusCode);
    }

    [Fact]
    public async Task Second_Server_On_Same_Port_Fails_With_Chinese_LastError()
    {
        await using var first = await TestRuntime.StartAsync();
        var port = new Uri(first.BaseUrl).Port;
        var dirB = TestEnv.NewDataDirectory();
        var second = await LanShareServer.CreateRuntimeAsync(dirB);
        try
        {
            var settings = second.Settings.Clone();
            settings.BindAddress = "127.0.0.1";
            settings.Port = port;
            await second.ApplySettingsAsync(settings);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => second.Server.StartAsync());
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
            Assert.False(string.IsNullOrWhiteSpace(second.Server.LastError));
            Assert.Contains("端口", second.Server.LastError);
            Assert.False(second.Server.IsRunning);
        }
        finally
        {
            await second.ShutdownAsync();
            await second.DisposeAsync();
            TestEnv.TryDelete(dirB);
        }
    }
}
