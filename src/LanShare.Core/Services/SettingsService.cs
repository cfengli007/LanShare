using System.Text.Json;
using LanShare.Core.Infrastructure;

namespace LanShare.Core;

/// <summary>Persists <see cref="AppSettings"/> to &lt;data&gt;\settings.json using atomic writes.</summary>
public sealed class SettingsService : ISettingsService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileLogger? _log;
    private AppSettings _current = new();

    public SettingsService(string? dataDirectory = null, FileLogger? logger = null)
    {
        DataDirectory = ResolveDataDirectory(dataDirectory);
        _log = logger;
    }

    public string DataDirectory { get; }

    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public string DefaultStorageDirectory => Path.Combine(DataDirectory, "storage");

    public AppSettings Current => Volatile.Read(ref _current).Clone();

    public event EventHandler? Changed;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法创建数据目录“{DataDirectory}”：{ex.Message}", ex);
        }

        AppSettings loaded = new();
        var fileExists = File.Exists(SettingsPath);
        var broken = false;

        if (fileExists)
        {
            try
            {
                var read = await JsonFile.ReadAsync<AppSettings>(SettingsPath, ct).ConfigureAwait(false);
                if (read != null) loaded = read;
            }
            catch (Exception ex)
            {
                broken = true;
                _log?.Error($"读取设置文件失败，将使用默认设置：{SettingsPath}", ex);
                QuarantineSettingsFile();
            }
        }

        var normalized = loaded.Normalize();
        Volatile.Write(ref _current, normalized);

        try
        {
            EnsureStorageDirectory(normalized);
        }
        catch (Exception ex)
        {
            _log?.Warn($"存储目录不可用：{ex.Message}");
        }

        var writeDefaults = !fileExists || broken;
        if (!writeDefaults)
        {
            try
            {
                var originalJson = JsonSerializer.Serialize(loaded, JsonFile.WriteOptions);
                var normalizedJson = JsonSerializer.Serialize(normalized, JsonFile.WriteOptions);
                writeDefaults = !string.Equals(originalJson, normalizedJson, StringComparison.Ordinal);
            }
            catch
            {
                writeDefaults = false;
            }
        }

        if (writeDefaults)
        {
            try
            {
                await JsonFile.WriteAtomicAsync(SettingsPath, normalized, ct).ConfigureAwait(false);
                _log?.Info($"已写入默认设置：{SettingsPath}");
            }
            catch (Exception ex)
            {
                _log?.Warn($"写入默认设置失败（将使用内存中的默认设置）：{ex.Message}");
            }
        }

        _log?.Info($"设置已加载：端口 {normalized.Port}，绑定 {normalized.BindAddress}，存储 {ResolveStorageDirectory(normalized, DataDirectory)}");
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalize();

        try
        {
            EnsureStorageDirectory(normalized);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法使用存储目录：{ex.Message}", ex);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                await JsonFile.WriteAtomicAsync(SettingsPath, normalized, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"保存设置失败：{ex.Message}", ex);
            }

            Volatile.Write(ref _current, normalized);
        }
        finally
        {
            _gate.Release();
        }

        _log?.Info($"设置已保存：端口 {normalized.Port}，绑定 {normalized.BindAddress}");
        RaiseChanged();
    }

    private static string ResolveDataDirectory(string? dataDirectory)
    {
        var directory = string.IsNullOrWhiteSpace(dataDirectory)
            ? LanShareConstants.AppDataDirectory
            : dataDirectory.Trim();
        try { return Path.GetFullPath(directory); }
        catch { return directory; }
    }

    /// <summary>Absolute storage directory for the given settings (configured value or &lt;data&gt;\storage).</summary>
    public static string ResolveStorageDirectory(AppSettings settings, string dataDirectory)
    {
        var configured = settings.StorageDirectory;
        if (string.IsNullOrWhiteSpace(configured))
            return Path.Combine(Path.GetFullPath(dataDirectory), "storage");

        try { return Path.GetFullPath(configured.Trim()); }
        catch { return configured.Trim(); }
    }

    private void EnsureStorageDirectory(AppSettings settings)
    {
        var directory = ResolveStorageDirectory(settings, DataDirectory);
        Directory.CreateDirectory(directory);
    }

    private void QuarantineSettingsFile()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var target = $"{SettingsPath}.broken-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(SettingsPath, target, overwrite: true);
            _log?.Info($"损坏的设置文件已备份为：{target}");
        }
        catch (Exception ex)
        {
            _log?.Warn($"备份损坏的设置文件失败：{ex.Message}");
        }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _log?.Warn($"设置变更事件处理失败：{ex.Message}"); }
    }
}
