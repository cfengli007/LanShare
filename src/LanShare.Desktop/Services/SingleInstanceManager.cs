using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace LanShare.Desktop.Services;

public sealed class PipeRequest
{
    public List<string> Paths { get; set; } = new();
    public bool Activate { get; set; }
}

/// <summary>Single-instance guard: a named Mutex plus a per-user named pipe used to forward --share paths.</summary>
public sealed class SingleInstanceManager : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _mutexName;
    private readonly string _pipeName;
    private Mutex? _mutex;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private int _disposed;

    public SingleInstanceManager()
    {
        var suffix = GetUserSuffix();
        _mutexName = "Local\\LanShare.Desktop." + suffix;
        _pipeName = "LanShare.Pipe." + suffix;
    }

    public bool TryAcquirePrimary()
    {
        try
        {
            _mutex = new Mutex(false, _mutexName, out var createdNew);
            if (createdNew) return true;
            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        catch
        {
            // If the mutex cannot be created, do not block the app from starting.
            return true;
        }
    }

    public void StartServer(Action<PipeRequest> onRequest)
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _serverTask = Task.Run(() => ServerLoopAsync(onRequest, _cts.Token));
    }

    private async Task ServerLoopAsync(Action<PipeRequest> onRequest, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);
                if (string.IsNullOrWhiteSpace(line)) continue;

                PipeRequest? request = null;
                try { request = JsonSerializer.Deserialize<PipeRequest>(line, JsonOptions); }
                catch { }
                if (request is not null)
                {
                    try { onRequest(request); }
                    catch { }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (ct.IsCancellationRequested) break;
                try { await Task.Delay(100, ct); } catch { break; }
            }
        }
    }

    public async Task<bool> ForwardAsync(IReadOnlyList<string> paths, bool activate)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(2500));
            await client.ConnectAsync(timeout.Token);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            var payload = JsonSerializer.Serialize(new PipeRequest { Paths = paths.ToList(), Activate = activate }, JsonOptions);
            await writer.WriteLineAsync(payload);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts?.Cancel(); } catch { }
        try { _serverTask?.Wait(TimeSpan.FromMilliseconds(800)); } catch { }
        try { _cts?.Dispose(); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
    }

    private static string GetUserSuffix()
    {
        try
        {
            var user = WindowsIdentity.GetCurrent().User?.Value;
            if (!string.IsNullOrWhiteSpace(user)) return Sanitize(user);
        }
        catch { }
        return Sanitize(Environment.UserName);
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            sb.Append(ch == '\\' || ch == '/' || invalid.Contains(ch) ? '_' : ch);
        var result = sb.ToString();
        return string.IsNullOrWhiteSpace(result) ? "default" : result;
    }
}
