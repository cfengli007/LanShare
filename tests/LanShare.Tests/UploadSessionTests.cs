using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace LanShare.Tests;

public sealed class UploadSessionTests
{
    private static ByteArrayContent Octet(byte[] data)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    private static StringContent Json(object value)
        => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? string.Join("; ", values) : null;

    [Fact]
    public async Task Chunked_Upload_Resume_And_Complete_Create_A_Packaged_Share()
    {
        await using var rt = await TestRuntime.StartAsync();
        var file0 = Encoding.UTF8.GetBytes("0123456789");
        var file1 = Encoding.UTF8.GetBytes("abcdefghijklmno");
        var create = await rt.Client.PostAsync("api/uploads", Json(new
        {
            files = new[]
            {
                new { name = "a.txt", length = file0.Length },
                new { name = "sub\\b.bin", length = file1.Length }
            },
            expiryMinutes = 60,
            burn = false,
            title = "分片测试"
        }));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await TestRuntime.JsonAsync(create);
        var uploadId = created.GetProperty("uploadId").GetString()!;
        Assert.Equal(22, uploadId.Length);
        Assert.Equal(8 * 1024 * 1024, created.GetProperty("chunkSize").GetInt64());
        Assert.Equal(2, created.GetProperty("files").GetArrayLength());
        Assert.Equal("b.bin", created.GetProperty("files")[1].GetProperty("name").GetString());

        var first = await rt.Client.PutAsync("api/uploads/" + uploadId + "/0?offset=0",
            Octet(file0[..4]));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstJson = await TestRuntime.JsonAsync(first);
        Assert.Equal(4, firstJson.GetProperty("received").GetInt64());
        Assert.False(firstJson.GetProperty("complete").GetBoolean());

        var resume = await TestRuntime.JsonAsync(await rt.Client.GetAsync("api/uploads/" + uploadId));
        Assert.Equal(4, resume.GetProperty("files")[0].GetProperty("received").GetInt64());

        var mismatch = await rt.Client.PutAsync("api/uploads/" + uploadId + "/0?offset=99",
            Octet(file0[4..]));
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        var mismatchJson = await TestRuntime.JsonAsync(mismatch);
        Assert.Equal("offset_mismatch", mismatchJson.GetProperty("code").GetString());
        Assert.Equal(4, mismatchJson.GetProperty("received").GetInt64());

        var rest = await rt.Client.PutAsync("api/uploads/" + uploadId + "/0?offset=4",
            Octet(file0[4..]));
        Assert.Equal(HttpStatusCode.OK, rest.StatusCode);
        var restJson = await TestRuntime.JsonAsync(rest);
        Assert.Equal(10, restJson.GetProperty("received").GetInt64());
        Assert.True(restJson.GetProperty("complete").GetBoolean());

        var second = await rt.Client.PutAsync("api/uploads/" + uploadId + "/1?offset=0",
            Octet(file1));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True((await TestRuntime.JsonAsync(second)).GetProperty("complete").GetBoolean());

        var complete = await rt.Client.PostAsync("api/uploads/" + uploadId + "/complete", new StringContent(""));
        Assert.Equal(HttpStatusCode.Created, complete.StatusCode);
        var share = (await TestRuntime.JsonAsync(complete)).GetProperty("share");
        var shareId = share.GetProperty("id").GetString()!;
        Assert.True(share.GetProperty("isPackaged").GetBoolean());
        Assert.Equal(25, share.GetProperty("totalBytes").GetInt64());
        Assert.Equal(2, share.GetProperty("files").GetArrayLength());

        var download = await rt.Client.GetAsync("d/" + shareId);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/x-7z-compressed", download.Content.Headers.ContentType?.MediaType);
        var archive = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, archive.Take(6).ToArray());

        Assert.Equal(HttpStatusCode.NotFound, (await rt.Client.GetAsync("api/uploads/" + uploadId)).StatusCode);
    }

    [Fact]
    public async Task Download_Supports_Strong_ETag_IfNoneMatch_And_Unsatisfiable_Range()
    {
        await using var rt = await TestRuntime.StartAsync();
        var bytes = Encoding.UTF8.GetBytes("0123456789");
        using var form = TestRuntime.Multipart(new[] { ("range.txt", bytes) }, 60);
        var created = await rt.Client.PostAsync("api/shares", form);
        var id = (await TestRuntime.JsonAsync(created)).GetProperty("share").GetProperty("id").GetString()!;

        var full = await rt.Client.GetAsync("d/" + id);
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        var etag = full.Headers.ETag?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(etag));
        Assert.Equal("bytes", Header(full, "Accept-Ranges"));
        Assert.Equal(bytes, await full.Content.ReadAsByteArrayAsync());

        using var conditional = new HttpRequestMessage(HttpMethod.Get, "d/" + id);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var notModified = await rt.Client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Empty(await notModified.Content.ReadAsByteArrayAsync());

        using var badRange = new HttpRequestMessage(HttpMethod.Get, "d/" + id);
        badRange.Headers.Range = new RangeHeaderValue(bytes.Length + 5, null);
        var unsatisfiable = await rt.Client.SendAsync(badRange);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, unsatisfiable.StatusCode);
        Assert.Equal("bytes */" + bytes.Length, unsatisfiable.Content.Headers.ContentRange?.ToString());

        using var partialRequest = new HttpRequestMessage(HttpMethod.Get, "d/" + id);
        partialRequest.Headers.Range = new RangeHeaderValue(2, 5);
        var partial = await rt.Client.SendAsync(partialRequest);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("bytes 2-5/10", partial.Content.Headers.ContentRange?.ToString());
        Assert.Equal(bytes[2..6], await partial.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Burn_Share_Survives_Partial_Range_And_Is_Destroyed_After_Full_Download()
    {
        await using var rt = await TestRuntime.StartAsync();
        var bytes = Encoding.UTF8.GetBytes("burn-me-please");
        using var form = TestRuntime.Multipart(new[] { ("burn.txt", bytes) }, 60, true);
        var created = await rt.Client.PostAsync("api/shares", form);
        var id = (await TestRuntime.JsonAsync(created)).GetProperty("share").GetProperty("id").GetString()!;

        using var partialRequest = new HttpRequestMessage(HttpMethod.Get, "d/" + id);
        partialRequest.Headers.Range = new RangeHeaderValue(0, 2);
        var partial = await rt.Client.SendAsync(partialRequest);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);

        var publicAfterPartial = await TestRuntime.JsonAsync(await rt.Client.GetAsync("api/public/shares/" + id));
        var shareAfterPartial = publicAfterPartial.GetProperty("share");
        Assert.False(shareAfterPartial.GetProperty("isRevoked").GetBoolean());
        Assert.Equal(1, shareAfterPartial.GetProperty("downloadCount").GetInt32());

        var head = await rt.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "d/" + id));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        var publicAfterHead = await TestRuntime.JsonAsync(await rt.Client.GetAsync("api/public/shares/" + id));
        Assert.Equal(1, publicAfterHead.GetProperty("share").GetProperty("downloadCount").GetInt32());

        var full = await rt.Client.GetAsync("d/" + id);
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal(bytes, await full.Content.ReadAsByteArrayAsync());

        var gone = await rt.Client.GetAsync("api/public/shares/" + id);
        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);
    }
}
