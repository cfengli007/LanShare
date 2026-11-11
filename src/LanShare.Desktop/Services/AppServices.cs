using System.IO;
using System.Windows;
using System.Windows.Threading;
using LanShare.Core;
using LanShare.Server;

namespace LanShare.Desktop.Services;

public sealed record SnackbarMessage(string Title, string Message, bool IsError = false, bool IsSuccess = false);

/// <summary>UI-facing facade over the frozen Core/Server runtime. All events are raised on the UI thread.</summary>
public sealed class AppServices
{
    private static readonly Lazy<AppServices> LazyInstance = new(() => new AppServices());
    public static AppServices Instance => LazyInstance.Value;

    private readonly AppSettings _fallbackSettings = new();
    private readonly object _pendingGate = new();
    private readonly List<string> _pendingPaths = new();
    private readonly SemaphoreSlim _shareCreationGate = new(1, 1);
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly System.Threading.Timer _shareDebounceTimer;
    private ShareRecord? _pendingShareResult;
    private bool _isShuttingDown;
    private int _shutdownStarted;

    private AppServices()
    {
        _shareDebounceTimer = new System.Threading.Timer(OnShareDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    public ILanShareRuntime? Runtime { get; private set; }
    public string? StartupError { get; private set; }
    public bool IsInitialized { get; private set; }
    public bool IsRuntimeAvailable => Runtime is not null;
    public bool IsServerRunning => Runtime?.Server.IsRunning == true;
    public string DataDirectory => Runtime?.SettingsService.DataDirectory ?? LanShareConstants.AppDataDirectory;
    public string DefaultStorageDirectory => Runtime?.SettingsService.DefaultStorageDirectory
                                             ?? Path.Combine(LanShareConstants.AppDataDirectory, "storage");

    public event EventHandler? StateChanged;
    public event EventHandler? SharesChanged;
    public event EventHandler? SettingsChanged;
    public event EventHandler<SnackbarMessage>? SnackbarRequested;
    public event Action? AutoShareStarting;
    public event Action<ShareRecord>? AutoShareCreated;
    public event Action<ShareRecord>? ShareResultAvailable;
    public event Action<ShareRecord>? ShareCreatedNotification;
    public event Action<Type>? NavigationRequested;

    public AppSettings SettingsSnapshot
    {
        get
        {
            try
            {
                var current = Runtime?.SettingsService.Current ?? Runtime?.Settings ?? _fallbackSettings;
                return current.Clone();
            }
            catch
            {
                return _fallbackSettings.Clone();
            }
        }
    }

    public string InternalBase
    {
        get
        {
            try { return LinkBuilder.InternalBase(SettingsSnapshot); }
            catch { return $"http://127.0.0.1:{AppSettings.DefaultPort}"; }
        }
    }

    public string ExternalBase
    {
        get
        {
            try { return LinkBuilder.ExternalBase(SettingsSnapshot); }
            catch { return InternalBase; }
        }
    }

    public string WebUrl => InternalBase.TrimEnd('/') + "/";
    public string ExternalWebUrl => ExternalBase.TrimEnd('/') + "/";

    public string ServerStatusText => Runtime is null ? "服务未启动" : Runtime.Server.IsRunning ? "服务运行中" : "服务已停止";

    public string ServerDetailText
    {
        get
        {
            if (Runtime is null)
                return string.IsNullOrWhiteSpace(StartupError) ? "后端服务尚未初始化。" : StartupError!;
            if (Runtime.Server.IsRunning)
                return $"监听 {Runtime.Settings.BindAddress}:{Runtime.Settings.Port}，局域网内可直接访问。";
            return Runtime.Server.LastError ?? StartupError ?? "服务没有在运行，请尝试重启服务。";
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _initGate.WaitAsync(ct);
        try
        {
            if (Runtime is not null) return;
            if (IsInitialized) return;
            IsInitialized = true;
            try
            {
                var runtime = await LanShareServer.CreateRuntimeAsync(ct);
                Runtime = runtime;
                runtime.Server.StateChanged += OnServerStateChanged;
                runtime.Shares.Changed += OnSharesChanged;
                runtime.SettingsService.Changed += OnSettingsServiceChanged;
                StartupError = null;
                try
                {
                    await runtime.Server.StartAsync(ct);
                }
                catch (Exception ex)
                {
                    StartupError = ex.Message;
                }
            }
            catch (Exception ex)
            {
                Runtime = null;
                StartupError = ex.Message;
            }
            RaiseStateChanged();
        }
        finally
        {
            _initGate.Release();
        }
    }

    public async Task<bool> RetryInitializeAsync()
    {
        if (Runtime is not null)
        {
            try
            {
                await Runtime.Server.StartAsync();
                StartupError = null;
                RaiseStateChanged();
                return true;
            }
            catch (Exception ex)
            {
                StartupError = ex.Message;
                RaiseStateChanged();
                return false;
            }
        }

        IsInitialized = false;
        await InitializeAsync();
        return Runtime is not null;
    }

    public async Task RestartServerAsync()
    {
        if (Runtime is null)
        {
            await RetryInitializeAsync();
            return;
        }

        try
        {
            await Runtime.Server.RestartAsync();
            StartupError = null;
        }
        catch (Exception ex)
        {
            StartupError = ex.Message;
        }
        RaiseStateChanged();
    }

    public async Task<IReadOnlyList<ShareRecord>> LoadSharesAsync(CancellationToken ct = default)
    {
        if (Runtime is null) return Array.Empty<ShareRecord>();
        try
        {
            var all = await Runtime.Shares.GetAllAsync(ct);
            return all.OrderByDescending(s => s.CreatedAt).ToList();
        }
        catch
        {
            return Array.Empty<ShareRecord>();
        }
    }

    public async Task<ShareRecord> CreateShareAsync(IReadOnlyList<string> paths, int expiryMinutes, bool burn, CancellationToken ct = default)
    {
        var runtime = Runtime ?? throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(StartupError) ? "服务尚未就绪，请稍后重试。" : StartupError!);
        if (paths.Count == 0) throw new ArgumentException("请至少选择一个文件或文件夹。", nameof(paths));
        if (expiryMinutes < 1) expiryMinutes = 1;

        var record = await runtime.Shares.CreateFromPathsAsync(
            paths, LanShareConstants.DesktopOwnerId, TimeSpan.FromMinutes(expiryMinutes), burn, "desktop", ct);
        RaiseSharesChanged();
        return record;
    }

    public async Task<bool> RevokeAsync(string shareId)
    {
        var runtime = Runtime ?? throw new InvalidOperationException("服务尚未就绪，无法终止分享。");
        var removed = await runtime.Shares.RevokeAsync(shareId, null, false);
        RaiseSharesChanged();
        return removed;
    }

    public async Task ApplySettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        var runtime = Runtime ?? throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(StartupError) ? "服务尚未就绪，无法保存设置。" : StartupError!);
        var normalized = settings.Normalize();
        await runtime.ApplySettingsAsync(normalized, ct);
        StartupError = null;
        OnUi(() =>
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            StateChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public string InternalShareUrl(ShareRecord share) => LinkBuilder.ShareUrl(InternalBase, share.Id);
    public string ExternalShareUrl(ShareRecord share) => LinkBuilder.ShareUrl(ExternalBase, share.Id);
    public string InternalDownloadUrl(ShareRecord share) => LinkBuilder.DownloadUrl(InternalBase, share.Id);

    public void RequestNavigation(Type pageType)
    {
        OnUi(() => NavigationRequested?.Invoke(pageType));
    }

    /// <summary>Queue paths from Explorer --share invocations. Requests within 600 ms are merged.</summary>
    public void EnqueueShareRequest(IEnumerable<string> paths)
    {
        var cleaned = new List<string>();
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                var full = Path.GetFullPath(raw.Trim().Trim('"'));
                if (!File.Exists(full) && !Directory.Exists(full)) continue;
                if (!cleaned.Contains(full, StringComparer.OrdinalIgnoreCase)) cleaned.Add(full);
            }
            catch
            {
                // Ignore malformed command-line paths.
            }
        }

        if (cleaned.Count == 0)
        {
            ReportError("无法分享", "没有找到可访问的文件或文件夹。");
            return;
        }

        lock (_pendingGate)
        {
            foreach (var path in cleaned)
            {
                if (!_pendingPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    _pendingPaths.Add(path);
            }
        }

        try { _shareDebounceTimer.Change(600, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
        OnUi(() => AutoShareStarting?.Invoke());
    }

    public void PublishShareResult(ShareRecord record)
    {
        OnUi(() =>
        {
            PublishShareResultCore(record);
        });
    }

    public ShareRecord? TakePendingShareResult() => Interlocked.Exchange(ref _pendingShareResult, null);

    public void NotifyShareCreated(ShareRecord record)
    {
        if (!SettingsSnapshot.NotifyOnShareCreated) return;
        OnUi(() =>
        {
            AutoShareCreated?.Invoke(record);
            if (SettingsSnapshot.NotifyOnShareCreated)
                ShareCreatedNotification?.Invoke(record);
        });
    }

    private void PublishShareResultCore(ShareRecord record)
    {
        _pendingShareResult = record;
        ShareResultAvailable?.Invoke(record);
    }

    private void OnShareDebounceElapsed(object? state)
    {
        if (_isShuttingDown) return;
        _ = ProcessAutoShareAsync();
    }

    private async Task ProcessAutoShareAsync()
    {
        List<string> batch;
        lock (_pendingGate)
        {
            if (_pendingPaths.Count == 0) return;
            batch = _pendingPaths.ToList();
            _pendingPaths.Clear();
        }

        await _shareCreationGate.WaitAsync();
        try
        {
            if (_isShuttingDown) return;
            if (Runtime is null)
            {
                ReportError("无法自动分享", StartupError ?? "服务尚未初始化完成。");
                return;
            }

            var settings = SettingsSnapshot;
            var expiry = Math.Clamp(settings.DefaultExpiryMinutes, 1, 60 * 24 * 30);
            var record = await CreateShareAsync(batch, expiry, settings.BurnAfterDownloadByDefault);
            OnUi(() =>
            {
                AutoShareCreated?.Invoke(record);
                PublishShareResultCore(record);
                if (SettingsSnapshot.NotifyOnShareCreated)
                    ShareCreatedNotification?.Invoke(record);
                SnackbarRequested?.Invoke(this, new SnackbarMessage(
                    "分享已创建", $"{record.DisplayName} 已生成下载链接。", IsSuccess: true));
            });
        }
        catch (Exception ex)
        {
            ReportError("自动分享失败", ex.Message);
        }
        finally
        {
            _shareCreationGate.Release();
        }
    }

    public void ReportError(string title, string message)
    {
        ErrorLog.Write("ReportError " + title, new InvalidOperationException(message));
        OnUi(() => SnackbarRequested?.Invoke(this, new SnackbarMessage(title, message, IsError: true)));
    }

    public void ShowInfo(string title, string message) =>
        OnUi(() => SnackbarRequested?.Invoke(this, new SnackbarMessage(title, message)));

    public void ShowSuccess(string title, string message) =>
        OnUi(() => SnackbarRequested?.Invoke(this, new SnackbarMessage(title, message, IsSuccess: true)));

    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0) return;
        _isShuttingDown = true;
        try { _shareDebounceTimer.Dispose(); } catch { }

        var runtime = Runtime;
        Runtime = null;
        if (runtime is null) return;

        try { runtime.Server.StateChanged -= OnServerStateChanged; } catch { }
        try { runtime.Shares.Changed -= OnSharesChanged; } catch { }
        try { runtime.SettingsService.Changed -= OnSettingsServiceChanged; } catch { }

        try { await runtime.ShutdownAsync(); } catch { }
        try { await runtime.DisposeAsync(); } catch { }
    }

    private void OnServerStateChanged(object? sender, EventArgs e) => RaiseStateChanged();
    private void OnSharesChanged(object? sender, EventArgs e) => RaiseSharesChanged();
    private void OnSettingsServiceChanged(object? sender, EventArgs e) =>
        OnUi(() => SettingsChanged?.Invoke(this, EventArgs.Empty));

    private void RaiseStateChanged() => OnUi(() => StateChanged?.Invoke(this, EventArgs.Empty));
    private void RaiseSharesChanged() => OnUi(() => SharesChanged?.Invoke(this, EventArgs.Empty));

    public static void OnUi(Action action)
    {
        if (action is null) return;
        var app = Application.Current;
        if (app is null)
        {
            action();
            return;
        }
        if (app.Dispatcher.CheckAccess()) action();
        else app.Dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }
}