using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using LanShare.Core;
using LanShare.Server;

namespace LanShare.Tests;

/// <summary>Every test gets an isolated data directory under the test output folder (never %LOCALAPPDATA%).</summary>
internal static class TestEnv
{
    public static string NewDataDirectory()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "test-temp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
            // best effort only; test-temp lives under the output folder
        }
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

internal sealed class TestRuntime : IAsyncDisposable
{
    private readonly HttpClientHandler _handler;

    private TestRuntime(ILanShareRuntime runtime, HttpClientHandler handler, HttpClient client, string dataDirectory,
        string baseUrl)
    {
        Runtime = runtime;
        _handler = handler;
        Client = client;
        DataDirectory = dataDirectory;
        BaseUrl = baseUrl;
    }

    public ILanShareRuntime Runtime { get; }
    public LanShareRuntime Concrete => (LanShareRuntime)Runtime;
    public HttpClient Client { get; }
    public string DataDirectory { get; }
    public string BaseUrl { get; }

    public static async Task<TestRuntime> StartAsync(Action<AppSettings>? configure = null)
    {
        var dir = TestEnv.NewDataDirectory();
        var runtime = await LanShareServer.CreateRuntimeAsync(dir);
        var settings = runtime.Settings.Clone();
        settings.BindAddress = "127.0.0.1";
        settings.Port = TestEnv.FreePort();
        configure?.Invoke(settings);
        await runtime.ApplySettingsAsync(settings);
        await runtime.Server.StartAsync();

        var baseUrl = runtime.Server.Urls.FirstOrDefault() ?? $"http://127.0.0.1:{settings.Port}";
        var cookies = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            AllowAutoRedirect = false
        };
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        return new TestRuntime(runtime, handler, client, dir, baseUrl.TrimEnd('/'));
    }

    public HttpClient NewClient()
    {
        var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(BaseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public static MultipartFormDataContent Multipart((string Name, byte[] Data)[] files, int? expiryMinutes = 60,
        bool? burn = null, string? title = null)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, data) in files)
        {
            var content = new ByteArrayContent(data);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, "files", name);
        }

        if (expiryMinutes.HasValue)
            form.Add(new StringContent(expiryMinutes.Value.ToString()), "expiryMinutes");
        if (burn.HasValue)
            form.Add(new StringContent(burn.Value ? "true" : "false"), "burn");
        if (title != null)
            form.Add(new StringContent(title), "title");
        return form;
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        _handler.Dispose();
        try
        {
            await Runtime.ShutdownAsync();
        }
        catch
        {
            // ignore shutdown errors during cleanup
        }
        await Runtime.DisposeAsync();
        TestEnv.TryDelete(DataDirectory);
    }
}
