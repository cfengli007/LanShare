using LanShare.Core;
using LanShare.Core.Infrastructure;
using LanShare.Server.Hosting;

namespace LanShare.Server;

/// <summary>
/// Composition root: settings, share store, 7-Zip tool and the Kestrel host. The runtime does not
/// start listening on its own; the shell calls <see cref="IServerHost.StartAsync"/>.
/// </summary>
public sealed class LanShareRuntime : ILanShareRuntime
{
    private readonly FileLogger _log;
    private readonly SettingsService _settings;
    private readonly SevenZipTool _sevenZip;
    private readonly ShareService _shares;
    private readonly UploadSessionService _uploads;
    private readonly ServerHost _server;
    private readonly Timer _cleanupTimer;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private int _cleaning;
    private int _shutdown;
    private bool _disposed;

    private LanShareRuntime(string dataDirectory, FileLogger log, SettingsService settings, SevenZipTool sevenZip,
        ShareService shares, UploadSessionService uploads, ServerHost server)
    {
        DataDirectory = dataDirectory;
        _log = log;
        _settings = settings;
        _sevenZip = sevenZip;
        _shares = shares;
        _uploads = uploads;
        _server = server;
        _cleanupTimer = new Timer(_ => _ = CleanupTickAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string DataDirectory { get; }

    public ISettingsService SettingsService => _settings;

    public IShareService Shares => _shares;

    public IServerHost Server => _server;

    public AppSettings Settings => _settings.Current;

    public SevenZipTool SevenZip => _sevenZip;

    internal static string ResolveDataDirectory(string? dataDirectory)
    {
        var configured = dataDirectory;
        if (string.IsNullOrWhiteSpace(configured))
            configured = Environment.GetEnvironmentVariable("LANSHARE_DATA_DIR");
        if (string.IsNullOrWhiteSpace(configured))
            configured = LanShareConstants.AppDataDirectory;
        return Path.GetFullPath(configured.Trim());
    }

    public static async Task<LanShareRuntime> CreateAsync(string? dataDirectory = null, CancellationToken ct = default)
    {
        var dir = ResolveDataDirectory(dataDirectory);
        Directory.CreateDirectory(dir);

        var logger = new FileLogger(Path.Combine(dir, "logs"));
        logger.Info($"正在初始化 {LanShareConstants.ProductName} 运行时，数据目录：{dir}");

        var settings = new SettingsService(dir, logger);
        try
        {
            await settings.InitializeAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            logger.Error("初始化设置失败。");
            throw;
        }

        var sevenZip = new SevenZipTool(null, logger);
        sevenZip.Refresh();
        if (!sevenZip.IsAvailable)
            logger.Warn("未找到 7-Zip 可执行文件（7za.exe），多文件打包与文件夹分享将不可用。");

        var shares = new ShareService(settings, sevenZip, logger);
        var uploads = new UploadSessionService(settings, shares, logger);
        var server = new ServerHost(settings, shares, uploads, logger);
        var runtime = new LanShareRuntime(dir, logger, settings, sevenZip, shares, uploads, server);
        runtime.StartCleanupTimer();
        logger.Info("运行时初始化完成。");
        return runtime;
    }

    private void StartCleanupTimer()
    {
        var interval = TimeSpan.FromSeconds(10);
        _cleanupTimer.Change(interval, interval);
    }

    private async Task CleanupTickAsync()
    {
        if (Volatile.Read(ref _shutdown) != 0) return;
        if (Interlocked.Exchange(ref _cleaning, 1) == 1) return;
        try
        {
            var cleaned = await _shares.CleanupExpiredAsync(CancellationToken.None).ConfigureAwait(false);
            if (cleaned > 0)
                _log.Info($"过期清理完成，共清理 {cleaned} 个分享。");

            try
            {
                var sessions = await _uploads.CleanupExpiredAsync(CancellationToken.None).ConfigureAwait(false);
                if (sessions > 0)
                    _log.Info($"过期清理完成，共清理 {sessions} 个上传会话。");
            }
            catch (Exception ex)
            {
                _log.Error("过期上传会话清理失败。", ex);
            }
        }
        catch (Exception ex)
        {
            _log.Error("过期分享清理失败。", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _cleaning, 0);
        }
    }

    public async Task ApplySettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        ThrowIfDisposed();

        var normalized = settings.Normalize();

        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previous = _settings.Current;
            var needsRestart = _server.IsRunning &&
                               (!string.Equals(previous.BindAddress, normalized.BindAddress, StringComparison.OrdinalIgnoreCase) ||
                                previous.Port != normalized.Port);

            await _settings.SaveAsync(normalized, ct).ConfigureAwait(false);
            _log.Info($"设置已保存：监听 {normalized.BindAddress}:{normalized.Port}，默认有效期 {normalized.DefaultExpiryMinutes} 分钟。");

            if (needsRestart)
            {
                _log.Info("监听地址或端口发生变化，正在重启服务。");
                await _server.RestartAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0) return;

        try
        {
            await _cleanupTimer.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // best effort
        }

        try
        {
            await _server.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("关闭服务时出错。", ex);
        }

        _log.Info("运行时已关闭。");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await ShutdownAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LanShareRuntime));
    }
}
