using System.Net;
using System.Net.Sockets;
using LanShare.Core;
using LanShare.Core.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LanShare.Server.Hosting;

/// <summary>Owns the Kestrel host. Start/stop are serialized; failures are recorded in <see cref="LastError"/>.</summary>
internal sealed class ServerHost : IServerHost, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IShareService _shares;
    private readonly FileLogger _log;
    private readonly RequestPipeline _pipeline;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();

    private IWebHost? _host;
    private volatile bool _running;
    private volatile string? _lastError;
    private volatile string[] _urls = Array.Empty<string>();
    private DateTimeOffset _startedAt;

    public ServerHost(ISettingsService settings, IShareService shares, UploadSessionService uploads, FileLogger log)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _shares = shares ?? throw new ArgumentNullException(nameof(shares));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _pipeline = new RequestPipeline(settings, shares, uploads, log, LanShareServer.Version, GetUptime);
    }

    public bool IsRunning => _running;

    public string? LastError => _lastError;

    public IReadOnlyList<string> Urls => _urls;

    public double UptimeSeconds => _running ? Math.Max(0, (DateTimeOffset.UtcNow - _startedAt).TotalSeconds) : 0;

    public event EventHandler? StateChanged;

    private double GetUptime() => UptimeSeconds;

    public async Task StartAsync(CancellationToken ct = default)
    {
        Exception? failure = null;
        var changed = false;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_running) return;

            var settings = _settings.Current;
            IWebHost host;
            try
            {
                host = BuildHost(settings);
            }
            catch (Exception ex)
            {
                _lastError = $"服务器配置无效：{ex.Message}";
                _log.Error("构建 Web 服务器失败。", ex);
                failure = ex;
                changed = true;
                host = null!;
            }

            if (host != null)
            {
                try
                {
                    await host.StartAsync(ct).ConfigureAwait(false);
                    _host = host;
                    _running = true;
                    _lastError = null;
                    _startedAt = DateTimeOffset.UtcNow;
                    _urls = ResolveUrls(host, settings);
                    _log.Info($"服务已启动，监听 {settings.BindAddress}:{settings.Port}，地址：{string.Join(", ", _urls)}");
                    changed = true;
                }
                catch (Exception ex)
                {
                    try { host.Dispose(); } catch { }
                    _lastError = DescribeStartFailure(ex, settings);
                    _log.Error(_lastError, ex);
                    failure = ex;
                    changed = true;
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed) RaiseStateChanged();
        if (failure != null)
        {
            throw new InvalidOperationException(_lastError ?? "服务器启动失败。", failure);
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        IWebHost? host = null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            host = _host;
            _host = null;
            _running = false;
            _urls = Array.Empty<string>();
            _startedAt = default;
            if (host != null)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await host.StopAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Warn($"停止 Web 服务器时发生异常：{ex.Message}");
                }
                finally
                {
                    try { host.Dispose(); } catch { }
                }
                _log.Info("服务已停止。");
            }
        }
        finally
        {
            _gate.Release();
        }

        if (host != null) RaiseStateChanged();
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        await StopAsync(ct).ConfigureAwait(false);
        await StartAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
        try { _gate.Dispose(); } catch { }
    }

    private IWebHost BuildHost(AppSettings settings)
    {
        return new WebHostBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
            .UseKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Limits.MaxRequestBodySize = null;
                ConfigureBinding(options, settings);
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Warning);
                logging.AddProvider(new FileLoggerProvider(_log, LogLevel.Warning));
            })
            .Configure(app => app.Run(_pipeline.HandleAsync))
            .Build();
    }

    private static void ConfigureBinding(KestrelServerOptions options, AppSettings settings)
    {
        var port = settings.Port;
        var bind = (settings.BindAddress ?? "").Trim();

        if (bind.Length == 0 || bind is "0.0.0.0" or "*" or "any")
        {
            options.ListenAnyIP(port);
            return;
        }

        if (bind.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            options.ListenLocalhost(port);
            return;
        }

        if (bind.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            bind.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(bind, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            {
                throw new InvalidOperationException($"监听地址“{bind}”无效，请填写 IP 地址，例如 0.0.0.0 或 192.168.1.10。");
            }

            bind = uri.Host;
        }

        if (IPAddress.TryParse(bind, out var address))
        {
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                options.ListenAnyIP(port);
                return;
            }

            options.Listen(address, port);
            return;
        }

        throw new InvalidOperationException($"监听地址“{bind}”无效，请填写 0.0.0.0、localhost 或本机 IP 地址。");
    }

    private static string DescribeStartFailure(Exception exception, AppSettings settings)
    {
        var socket = FindSocketException(exception);
        if (socket?.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return $"端口 {settings.Port} 已被其他程序占用，服务无法启动。请在设置中更换端口后重试。";
        }

        if (socket?.SocketErrorCode == SocketError.AccessDenied)
        {
            return $"没有权限监听端口 {settings.Port}。请改用 1024 以上的端口。";
        }

        if (socket != null)
        {
            return $"无法在 {settings.BindAddress}:{settings.Port} 启动服务：{socket.Message}";
        }

        return $"服务启动失败：{exception.Message}";
    }

    private static SocketException? FindSocketException(Exception? exception)
    {
        while (exception != null)
        {
            if (exception is SocketException socket) return socket;
            exception = exception.InnerException;
        }
        return null;
    }

    private static string[] ResolveUrls(IWebHost host, AppSettings settings)
    {
        var urls = new List<string>();
        var port = settings.Port;
        var bind = (settings.BindAddress ?? "").Trim();

        if (bind.Length == 0 || bind is "0.0.0.0" or "*" or "any")
        {
            urls.Add($"http://localhost:{port}");
            var primary = NetworkInfo.GetPrimaryIPv4();
            if (!string.IsNullOrWhiteSpace(primary) && primary != "127.0.0.1")
            {
                urls.Add($"http://{primary}:{port}");
            }
        }
        else if (bind.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            urls.Add($"http://localhost:{port}");
        }
        else
        {
            var value = bind;
            if (bind.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                bind.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(bind, UriKind.Absolute, out var uri)) value = uri.Host;
            }
            urls.Add($"http://{value}:{port}");
        }

        try
        {
            var feature = host.ServerFeatures.Get<IServerAddressesFeature>();
            if (feature != null)
            {
                foreach (var address in feature.Addresses)
                {
                    if (!string.IsNullOrWhiteSpace(address) &&
                        !urls.Contains(address, StringComparer.OrdinalIgnoreCase))
                    {
                        urls.Add(address);
                    }
                }
            }
        }
        catch
        {
            // Address discovery is best-effort; the computed URLs remain usable.
        }

        return urls.ToArray();
    }

    private void RaiseStateChanged()
    {
        EventHandler? handlers;
        lock (_stateGate) handlers = StateChanged;
        if (handlers == null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception ex) { _log.Error("处理服务状态变更事件时发生异常。", ex); }
        }
    }
}
