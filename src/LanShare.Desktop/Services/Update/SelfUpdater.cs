using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using LanShare.Core;

namespace LanShare.Desktop.Services.Update;

/// <summary>GitHub Release 摘要（从 atom 源与 expanded_assets 拼出来，不走 REST API）。</summary>
public sealed class GitHubRelease
{
    public string TagName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime? PublishedAt { get; set; }
    public string HtmlUrl { get; set; } = "";
    public List<ReleaseAsset> Assets { get; set; } = new();
}

/// <summary>单个发布资产（安装包 / 绿色版）。</summary>
public sealed class ReleaseAsset
{
    public string Name { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>一次更新检查的完整结果。AppUpdateWindow 与 UpdateService 共用。</summary>
public sealed class UpdateCheckResult
{
    public bool HasUpdate { get; set; }
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public GitHubRelease? Release { get; set; }

    /// <summary>选中的安装包（.exe，名字含 Setup）。资产列表拿不到时是约定名兜底拼出来的。</summary>
    public ReleaseAsset? Installer { get; set; }

    /// <summary>非 null 表示这次检查失败（网络不可达 / 仓库无 Release / 超时）。</summary>
    public string? Error { get; set; }

    /// <summary>"redirect" 或 "atom"，表示最新 tag 是从哪一路取到的，便于排障。</summary>
    public string Source { get; set; } = "";
}

/// <summary>
/// 应用自更新检查（只读 GitHub Releases，不依赖 REST API）。
///
/// ── 为什么不用 REST API ────────────────────────────────────────────────
/// 未认证的 api.github.com 限流 60 次/小时/IP，而更新检查是每台装了本软件的机器各查各的，
/// 很容易 403。下面三条路径都是网页端点，不参与 API 配额：
///
///   1) GET /releases/latest?_=&lt;ms&gt;          → 302，从 Location 解析 tag
///   2) GET /releases/expanded_assets/&lt;tag&gt;    → HTML 片段，正则取资产文件名
///   3) GET /releases.atom?_=&lt;ms&gt;             → Atom 源，实时，含标题/时间/更新说明
///
/// ── 两个必须保留的坑 ──────────────────────────────────────────────────
/// ① cache-buster：/releases/latest 前面有边缘缓存，刚发版时可能仍返回上一个 tag
///    （实测无参数拿到 v2.5.0，带 ?_=&lt;epoch-ms&gt; 才拿到 v2.5.1）。所以三处都带。
/// ② atom 交叉校验：即使带了 cache-buster 也可能读到旧值，atom 源是实时的，
///    两个都拿、取较新的那个，Source 字段记录最终来自哪一路。
///
/// ── 正则注意 ─────────────────────────────────────────────────────────
/// atom 里 entry 的 id 形如 tag:github.com,2008:Repository/1452775612/v2.5.0，
/// 仓库 ID 是「一串十进制数字」，所以必须用 \d+ 而不是照抄时极易写坏的 d+。
/// </summary>
public static class SelfUpdater
{
    public const string Owner = "cfengli007";
    public const string Repo = "LanShare";

    /// <summary>资产名约定，与 scripts/publish.ps1 的输出保持一致。</summary>
    public const string InstallerNamePattern = "LanShare-Setup-{0}-win-x64.exe";

    /// <summary>绿色版资产名约定。</summary>
    public const string PortableNamePattern = "LanShare-{0}-win-x64.zip";

    public static string RepoUrl => $"https://github.com/{Owner}/{Repo}";
    private static string BaseUrl => $"https://github.com/{Owner}/{Repo}";

    private const string UserAgent = "LanShare-SelfUpdater/1.0";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // 手动处理 302：要的是 Location 里的 tag，自动跟随后就拿不到了。
            AllowAutoRedirect = false,
        };
        var h = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return h;
    }

    private static string CacheBuster() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

    /// <summary>
    /// 当前程序版本。优先读 AssemblyInformationalVersion（Directory.Build.props 注入，
    /// 可能带 "+&lt;sha&gt;" 后缀，截掉），回退 AssemblyName.Version。
    /// </summary>
    public static string CurrentVersion
    {
        get
        {
            try
            {
                var asm = typeof(SelfUpdater).Assembly;
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info))
                {
                    var plus = info.IndexOf('+');
                    var core = (plus >= 0 ? info[..plus] : info).Trim();
                    if (core.Length > 0) return core;
                }

                var v = asm.GetName().Version;
                if (v is not null) return $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch
            {
                // 读版本号失败不该影响任何事。
            }
            return "0.0.0";
        }
    }

    public static Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default) => CheckAsync(null, ct);

    /// <summary>检查最新版本。<paramref name="progress"/> 用来回报当前阶段（可选）。</summary>
    public static async Task<UpdateCheckResult> CheckAsync(Action<string>? progress, CancellationToken ct = default)
    {
        var result = new UpdateCheckResult { CurrentVersion = CurrentVersion };

        try
        {
            progress?.Invoke("正在获取最新版本号…");
            var tag = await GetLatestTagAsync(ct);
            if (string.IsNullOrWhiteSpace(tag))
            {
                result.Error = "无法获取最新版本号（网络不可达，或仓库还没有发布过 Release）";
                return result;
            }

            // 交叉校验：atom 是实时的，若它给出更新的 tag 就采用它。
            progress?.Invoke("正在校验发布源…");
            var atomTag = await GetLatestTagFromAtomAsync(ct);
            if (!string.IsNullOrWhiteSpace(atomTag) &&
                CompareVersion(NormalizeTag(atomTag!), NormalizeTag(tag!)) > 0)
            {
                tag = atomTag;
                result.Source = "atom";
            }
            else
            {
                result.Source = "redirect";
            }

            result.LatestVersion = NormalizeTag(tag!);
            result.Release = new GitHubRelease
            {
                TagName = tag!,
                Name = LanShareConstants.EnglishName + " " + result.LatestVersion,
                HtmlUrl = $"{BaseUrl}/releases/tag/{tag}",
            };

            progress?.Invoke("正在读取发布资产…");
            try { await FillAssetsAsync(tag!, result.Release, ct); }
            catch (OperationCanceledException) { throw; }
            catch { /* 资产列表拿不到就走兜底名 */ }

            try { await FillFromAtomAsync(result.Release, ct); }
            catch (OperationCanceledException) { throw; }
            catch { /* 更新说明拿不到不影响升级 */ }

            // 资产列表为空（GitHub 改版 / 请求被拦）时按约定名兜底，保证下载按钮可用。
            if (result.Release.Assets.Count == 0)
            {
                var guessName = string.Format(InstallerNamePattern, result.LatestVersion);
                result.Release.Assets.Add(new ReleaseAsset
                {
                    Name = guessName,
                    DownloadUrl = $"{BaseUrl}/releases/download/{tag}/{Uri.EscapeDataString(guessName)}",
                });
            }

            result.Installer = SelectInstaller(result.Release.Assets);
            result.HasUpdate = CompareVersion(result.LatestVersion, CurrentVersion) > 0;
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Error = "检查已取消或超时";
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            return result;
        }
    }

    /// <summary>测试入口：暴露安装包挑选规则。</summary>
    public static ReleaseAsset? SelectInstallerForTest(IReadOnlyList<ReleaseAsset> assets) => SelectInstaller(assets);

    /// <summary>优先选 .exe 且名字含 Setup 的资产；退而求其次选任意 .exe，再退 .zip。</summary>
    private static ReleaseAsset? SelectInstaller(IReadOnlyList<ReleaseAsset> assets)
    {
        if (assets.Count == 0) return null;

        var setup = assets.FirstOrDefault(a =>
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            a.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase));
        if (setup is not null) return setup;

        return assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
               ?? assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 第一路：/releases/latest 的 302 Location 里带 tag。
    /// AllowAutoRedirect=false，所以必须自己看 Location；
    /// 万一没有 302 而是直接 200，再从 RequestMessage.RequestUri 兜一次。
    /// </summary>
    private static async Task<string?> GetLatestTagAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/releases/latest?_={CacheBuster()}");
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        var location = resp.Headers.Location?.ToString();
        if (!string.IsNullOrEmpty(location))
        {
            var fromLocation = ParseTagFromUrl(location);
            if (fromLocation is not null) return fromLocation;
        }

        if (resp.StatusCode == HttpStatusCode.OK && resp.RequestMessage?.RequestUri is not null)
        {
            var m2 = Regex.Match(resp.RequestMessage.RequestUri.ToString(), @"/releases/tag/([^/?#]+)");
            if (m2.Success) return Uri.UnescapeDataString(m2.Groups[1].Value);
        }

        return null;
    }

    /// <summary>
    /// 第二路：atom 源第一条 entry 的 tag（实时，没有边缘缓存）。
    /// entry 的 id 形如 tag:github.com,2008:Repository/&lt;纯数字仓库ID&gt;/v2.5.0。
    /// </summary>
    private static async Task<string?> GetLatestTagFromAtomAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync($"{BaseUrl}/releases.atom?_={CacheBuster()}", ct);
            if (!resp.IsSuccessStatusCode) return null;

            var xml = await resp.Content.ReadAsStringAsync(ct);
            var m = Regex.Match(xml, @"<id>[^<]*Repository/\d+/([^/<\s]+)</id>");
            return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从任意 GitHub URL 里抠出 tag：/releases/tag/&lt;tag&gt;。
    /// 302 的 Location、以及 200 时的 RequestUri 都走这一个解析。
    /// </summary>
    public static string? ParseTagFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = Regex.Match(url, @"/releases/tag/([^/?#]+)");
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
    }

    /// <summary>
    /// 从 atom 源 XML 里取最新 tag：第一条 entry 的 id 末段。
    /// id 形如 tag:github.com,2008:Repository/&lt;纯数字仓库ID&gt;/v2.5.0。
    /// </summary>
    public static string? ReadAtomLatestTag(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        var m = Regex.Match(xml, @"<id>[^<]*Repository/\d+/([^/<\s]+)</id>");
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
    }

    /// <summary>
    /// 从 expanded_assets 的 HTML 片段里抽资产名。
    /// 页面结构：&lt;a href="/owner/repo/releases/download/&lt;tag&gt;/&lt;文件名&gt;"&gt;
    /// </summary>
    public static List<ReleaseAsset> ParseAssetsFromHtml(string html, string tag)
    {
        var list = new List<ReleaseAsset>();
        if (string.IsNullOrWhiteSpace(html)) return list;

        foreach (Match m in Regex.Matches(html, @"href=""[^""]*?/releases/download/[^""/]+/([^""]+)"""))
        {
            var name = Uri.UnescapeDataString(m.Groups[1].Value).Split('?')[0];
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (list.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))) continue;

            list.Add(new ReleaseAsset
            {
                Name = name,
                DownloadUrl = $"{BaseUrl}/releases/download/{tag}/{Uri.EscapeDataString(name)}",
            });
        }

        return list;
    }

    /// <summary>第三路：/releases/expanded_assets/&lt;tag&gt; 的 HTML 片段，正则取资产名。</summary>
    private static async Task FillAssetsAsync(string tag, GitHubRelease rel, CancellationToken ct)
    {
        using var resp = await Http.GetAsync($"{BaseUrl}/releases/expanded_assets/{tag}?_={CacheBuster()}", ct);
        if (!resp.IsSuccessStatusCode) return;

        var html = await resp.Content.ReadAsStringAsync(ct);
        foreach (var asset in ParseAssetsFromHtml(html, tag)) rel.Assets.Add(asset);
    }

    /// <summary>
    /// 从 atom 源补标题 / 发布时间 / 更新说明（转成纯文本）。
    /// 解析逻辑放在 <see cref="FillFromAtomText"/> 里，纯函数便于单测。
    /// </summary>
    private static async Task FillFromAtomAsync(GitHubRelease rel, CancellationToken ct)
    {
        using var resp = await Http.GetAsync($"{BaseUrl}/releases.atom?_={CacheBuster()}", ct);
        if (!resp.IsSuccessStatusCode) return;

        var xml = await resp.Content.ReadAsStringAsync(ct);
        FillFromAtomText(rel, xml);
    }

    /// <summary>把 atom XML 里与 rel.TagName 匹配的那条 entry 的标题/时间/正文填进 rel。</summary>
    public static void FillFromAtomText(GitHubRelease rel, string xml)
    {
        if (rel is null || string.IsNullOrWhiteSpace(xml)) return;

        foreach (Match entry in Regex.Matches(xml, @"<entry>([\s\S]*?)</entry>"))
        {
            var block = entry.Groups[1].Value;
            var idm = Regex.Match(block, @"<id>[^<]*/([^/<\s]+)</id>");
            if (!idm.Success) continue;
            if (NormalizeTag(idm.Groups[1].Value) != NormalizeTag(rel.TagName)) continue;

            var title = Regex.Match(block, @"<title>([\s\S]*?)</title>");
            if (title.Success) rel.Name = WebUtility.HtmlDecode(title.Groups[1].Value.Trim());

            var updated = Regex.Match(block, @"<updated>([^<]+)</updated>");
            if (updated.Success && DateTime.TryParse(updated.Groups[1].Value, out var dt)) rel.PublishedAt = dt;

            var content = Regex.Match(block, @"<content[^>]*>([\s\S]*?)</content>");
            if (content.Success) rel.Body = StripHtml(WebUtility.HtmlDecode(content.Groups[1].Value));

            return;
        }
    }

    /// <summary>把 GitHub 渲染后的 HTML 更新说明压成纯文本（测试用，也便于别处复用）。</summary>
    public static string ToPlainText(string html) => StripHtml(html ?? "");

    /// <summary>把 GitHub 渲染后的 HTML 更新说明压成便于阅读的纯文本。</summary>
    private static string StripHtml(string html)
    {
        var s = html;
        s = Regex.Replace(s, @"<h[1-6][^>]*>", "\n## ");
        s = Regex.Replace(s, @"</h[1-6]>", "\n");
        s = Regex.Replace(s, @"<li[^>]*>", "\n- ");
        s = Regex.Replace(s, @"</li>", "");
        s = Regex.Replace(s, @"<br\s*/?>", "\n");
        s = Regex.Replace(s, @"</p>", "\n\n");
        s = Regex.Replace(s, @"<code[^>]*>|</code>", "`");
        s = Regex.Replace(s, @"</?strong[^>]*>", "**");
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"[ \t]+\n", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    private static string NormalizeTag(string tag) => (tag ?? "").Trim().TrimStart('v', 'V');

    /// <summary>按点分段比较版本号；非数字段忽略，段数不同按 0 补齐。返回 -1 / 0 / 1。</summary>
    public static int CompareVersion(string a, string b)
    {
        static int[] Parse(string s)
        {
            var core = (s ?? "").Trim().TrimStart('v', 'V').Split('-', '+')[0];
            var parts = core.Split('.');
            var r = new int[parts.Length];
            for (var i = 0; i < parts.Length; i++)
                int.TryParse(new string(parts[i].TakeWhile(char.IsDigit).ToArray()), out r[i]);
            return r;
        }

        var x = Parse(a);
        var y = Parse(b);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var xi = i < x.Length ? x[i] : 0;
            var yi = i < y.Length ? y[i] : 0;
            if (xi != yi) return xi > yi ? 1 : -1;
        }
        return 0;
    }

    /// <summary>
    /// 下载安装包到 %TEMP%\LanShareUpdate\&lt;name&gt;。
    /// 这里用独立 HttpClient 且 AllowAutoRedirect=true —— releases/download 会 302 到
    /// objects.githubusercontent.com，跟着走才是真实文件。
    /// </summary>
    public static async Task<string> DownloadInstallerAsync(
        ReleaseAsset asset, IProgress<(long done, long total)>? progress, CancellationToken ct)
    {
        if (asset is null) throw new ArgumentNullException(nameof(asset));
        if (string.IsNullOrWhiteSpace(asset.DownloadUrl))
            throw new InvalidOperationException("安装包下载地址为空。");

        var dir = Path.Combine(Path.GetTempPath(), "LanShareUpdate");
        Directory.CreateDirectory(dir);

        // 清掉上一次的残留（这个目录只留一份安装包）。
        try
        {
            foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
        }
        catch
        {
            // 删不掉就留着，后面按名字覆盖。
        }

        var dest = Path.Combine(dir, asset.Name);

        using var follow = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AllowAutoRedirect = true,
        })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        follow.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        using var resp = await follow.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? asset.Size;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);

        var buffer = new byte[1 << 20];
        long done = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            progress?.Report((done, total));
        }

        // 服务端没给 Content-Length 时补一发「完成」，让进度条走到底。
        progress?.Report((done, total > 0 ? total : done));
        return dest;
    }

    /// <summary>打开一个 URL（发布页 / 手动下载）。失败不抛。</summary>
    public static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* 打不开浏览器不该崩 */ }
    }
}
