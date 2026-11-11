using System.IO;
using System.Text.Json;
using LanShare.Core;
using LanShare.Core.Infrastructure;

namespace LanShare.Desktop.Services.Update;

/// <summary>
/// 更新检查的用户设置，持久化在 %LOCALAPPDATA%\LanShare\update.json。
///
/// 只用 Core 里现成的 <see cref="JsonFile"/>（原子写 + Web 命名的 JSON 选项），
/// 不自己造一套序列化，避免和 settings.json / shares.json 的风格分叉。
///
/// 说明：
///   • Enabled / IntervalHours 只影响「自动检查」；手动点「检查更新」永远真的发请求。
///   • SkippedVersion 记录用户明确跳过的版本，自动检查命中它就安静跳过。
///   • 所有读写都不抛异常 —— 配置文件坏了就当默认值，绝不能因此让程序起不来。
/// </summary>
public sealed class UpdateSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>自动检查间隔（小时）。允许 6 / 12 / 24 / 72 / 168。</summary>
    public int IntervalHours { get; set; } = 24;

    public DateTime? LastCheck { get; set; }

    /// <summary>用户选择「跳过这个版本」时记录；自动检查不再提示它。</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>可选项，UI 的「检查频率」子菜单直接绑定这个数组。</summary>
    public static readonly int[] AllowedIntervals = { 6, 12, 24, 72, 168 };

    public static string SettingsPath =>
        Path.Combine(LanShareConstants.AppDataDirectory, "update.json");

    /// <summary>读设置。文件缺失/损坏/无权限一律返回默认值。</summary>
    public static UpdateSettings Load()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path)) return new UpdateSettings();

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new UpdateSettings();

            var loaded = JsonSerializer.Deserialize<UpdateSettings>(json, JsonFile.ReadOptions);
            return (loaded ?? new UpdateSettings()).Normalize();
        }
        catch
        {
            return new UpdateSettings();
        }
    }

    /// <summary>写设置。失败返回 false 且不抛（调用方最多记一条日志）。</summary>
    public bool Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // JsonFile.WriteAtomicAsync 是异步的；这里在 UI 线程之外同步等待没有问题，
            // 但为了避免 .Result 死锁，直接自己写一份「临时文件 + Move」的等价实现。
            var fullPath = Path.GetFullPath(SettingsPath);
            var temp = Path.Combine(dir!, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonFile.WriteOptions));
            File.Move(temp, fullPath, overwrite: true);
            SaveError = null;
            return true;
        }
        catch (Exception ex)
        {
            SaveError = ex.Message;
            return false;
        }
    }

    /// <summary>最近一次写盘失败的原因（成功时为 null）。</summary>
    public string? SaveError { get; private set; }

    /// <summary>把越界值收敛到合理范围。</summary>
    public UpdateSettings Normalize()
    {
        if (IntervalHours <= 0) IntervalHours = 24;

        // 不在允许列表里的值（老版本配置 / 手改过的文件）就近取合法值。
        if (Array.IndexOf(AllowedIntervals, IntervalHours) < 0)
        {
            var nearest = AllowedIntervals[0];
            var best = int.MaxValue;
            foreach (var v in AllowedIntervals)
            {
                var d = Math.Abs(v - IntervalHours);
                if (d < best) { best = d; nearest = v; }
            }
            IntervalHours = nearest;
        }

        if (SkippedVersion is not null)
        {
            SkippedVersion = SkippedVersion.Trim();
            if (SkippedVersion.Length == 0) SkippedVersion = null;
        }

        return this;
    }

    /// <summary>按间隔判断现在是否该自动检查了。</summary>
    public bool IsDue(DateTime nowUtc)
    {
        if (!Enabled) return false;
        if (LastCheck is null) return true;
        return nowUtc - LastCheck.Value.ToUniversalTime() >= TimeSpan.FromHours(IntervalHours);
    }
}
