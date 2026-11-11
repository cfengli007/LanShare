# 开发经验（LanShare）

> 提炼自姊妹项目 [JavaDeck](https://github.com/cfengli007/JavaDeck) 的 `docs/DEVELOPMENT.md`，
> 只保留与 LanShare 相关、且**本项目真的会遇到**的部分，并按本项目的实际情况校正过。
> 接口契约见 [CONTRACT.md](CONTRACT.md) / [CONTRACT-UPLOAD.md](CONTRACT-UPLOAD.md)，
> 界面规范见 [UI-CONTRACT.md](UI-CONTRACT.md) 与 [UI-DESIGN.md](UI-DESIGN.md)。

---

## 一、编译：先学会绕开 MSBuild 节点残留

### 症状

对**解决方案 / 多项目**跑 `dotnet build` 或 `dotnet publish`，本机会输出：

```
生成启动时间…
_GetAllRestoreProjectPathItems: 正在确定要还原的项目…
已完成生成项目"…csproj"(Restore 个目标)的操作 - 失败。
生成失败。
    0 个警告
    0 个错误          ← 关键：0 个错误，却失败了
```

注意最后两行——**零错误却报失败**，而且失败点在 `Restore` 目标上，没有任何编译诊断信息。
这说明问题不在代码里。

### 原因

MSBuild 常驻工作节点（node reuse / build server）的状态残留。改过 `Directory.Build.props`、
换过 SDK、或者上一条命令被中断过，节点里的状态就和磁盘对不上，`Restore` 直接判定失败。
（顺带一个反例：有时它表现成「Build succeeded, 0 Errors」但产物没更新，也是同一类状态不一致。）

### 解决：三个开关，缺一不可

```powershell
# 编译
dotnet build <单个 csproj> -m:1 -nodeReuse:false

# 发布：必须再加 --disable-build-servers
dotnet publish <单个 csproj> -m:1 -nodeReuse:false --disable-build-servers
```

| 开关 | 作用 | 少了会怎样 |
|---|---|---|
| `-m:1` | 单进程，不并行 | 多节点状态互相污染 |
| `-nodeReuse:false` | 用完就退出，下次重新起 | 残留节点被复用 |
| `--disable-build-servers` | 关掉持久化的 build server | **`dotnet publish` 仍会在 Restore 阶段失败**（实测：只加前两个不够） |

**必须针对单个 csproj，不要对 `.sln` 跑。**

本项目常用：

```powershell
dotnet build src/LanShare.Core/LanShare.Core.csproj          -m:1 -nodeReuse:false
dotnet build src/LanShare.Server/LanShare.Server.csproj      -m:1 -nodeReuse:false
dotnet build src/LanShare.Desktop/LanShare.Desktop.csproj    -m:1 -nodeReuse:false
dotnet test  tests/LanShare.Tests/LanShare.Tests.csproj      -m:1 -nodeReuse:false
```

`scripts/publish.ps1` 已经把这三个开关加进了它自己的 `dotnet publish` 与 `dotnet test` 调用，
所以用 `scripts\publish.ps1` 打包不会踩这个坑。

彻底清干净还可以 `dotnet build-server shutdown` 再删 `obj/` `bin/`。
**不要**因为解决方案级的「0 错误却失败」就去怀疑代码——先按上面的方式单项目验证一遍。

### 相邻的坑：文件被占用（MSB3021 / MSB3027）

编译时若之前手动启动过 `dist\app\LanShare.exe`（或 `bin\...` 下的那个），会报：

```
error MSB3021: 无法将 …\apphost.exe 复制到 …\LanShare.exe
error MSB3027: The process cannot access the file … because it is being used by another process
```

```powershell
Get-Process LanShare -ErrorAction SilentlyContinue | Stop-Process -Force
# 或者干脆删掉 bin/ 再编译
```

这是**文件占用**，不是编译错误。看到 MSB3021 / MSB3027 先查有没有残留进程，别去改代码。

### 编译顺序

`LanShare.Core` → `LanShare.Server` → `LanShare.Desktop`，测试项目最后。
`Desktop` 同时引用 Core 和 Server，所以改了 Core 的公开 API 必须先编译 Core。

### 重构期间的已知噪音

跨文件重构（例如移除第三方 UI 库）期间，别人的页面可能还残留 `ui:` 前缀的 XAML 引用，
编译会报 `MC3074: 不存在 "ui" 命名空间` 之类的错误。判断原则：

1. 先看报错行号落在**谁的文件**里，只处理自己写作用域内的
2. 别人的文件不要动，也不要在自己文件里加兼容性 hack
3. 自己负责的文件 **0 error** 就算通过，整体编译暂时红是正常的

顺带一条清理顺序的经验（本项目踩过）：移除 UI 库要按
「页面脱离 `ui:` 控件 → 删最后一处 C# `using` → 最后才删 csproj 包引用」推进。
反过来做的话，删包那一刻全仓编译失败，会误判成页面写错了。

## 二、沙箱与受限环境

在受管理的执行环境里开发时，下面这些会**稳定失败**，且都不是产品 bug：

| 现象 | 原因 | 对策 |
|---|---|---|
| `.NET HttpClient` 请求报 `schannel: AcquireCredentialsHandle failed: SEC_E_NO_CREDENTIALS` | SChannel 需要访问命名管道与 LSASS，被沙箱禁止 | 用 `gh` / `curl` / `node` 这类自带 TLS 实现的工具做验证 |
| 写 `%LOCALAPPDATA%`、`C:\Program Files` 失败 | 工作区外的写权限被限制 | 把数据目录指向工作区内的临时路径（`SettingsService` 的构造函数支持传 `dataDirectory`） |
| UAC 弹窗没人点 | 无人值守 | 与提权相关的流程（修改 IP、装进 Program Files）必须由真人在桌面完成 |
| `PrintWindow` 截图三次字节完全相同 | 返回的是缓存表面 | 用 `Graphics.CopyFromScreen` 全屏截图，或直接读控件属性 |

**结论**：在沙箱里能验证的是「逻辑与编译」，不能验证的是「网络连通性、提权、视觉」。后者要如实说明，
不要写成「已修复」。

### 中文控制台编码

PowerShell 把中文输出到管道时可能乱码：

```powershell
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
chcp 65001
```

写文件时同理，统一 UTF-8 无 BOM。

## 三、WPF 的几个必踩项

### 1. `InvariantGlobalization=true` 会让 WPF 绑定崩溃

**症状**：点 CheckBox 卡住，或抛
`InvalidOperationException: Cannot find non-neutral culture related to 'en-us'`。

**原因**：为了减小体积加了 `<InvariantGlobalization>true</InvariantGlobalization>`，
而 WPF 的 `ToggleButton.OnToggle` 内部会解析区域性，失败。

**解决**：不要开这个开关；并在 `App.OnStartup` 的**最前面**固定区域性：

```csharp
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
```

### 2. UI 线程上任何超过 100 ms 的操作都要挪走

JavaDeck 的教训是启动时在 UI 线程递归扫 `C:\` 找 JDK，**37 秒**白屏假死。
LanShare 里对应的风险点是：启动时的分享过期清理、读写 `shares.json`、探测网卡、首次更新检查。

- 全部走 `Task.Run` / 异步 API
- 首次自动检查更新**推迟 8 秒**（让主窗口先画完），不是启动即发请求
- 子进程（`7za.exe`）一定带超时，不要裸 `WaitForExit()`

### 3. 主题切换的三个失效方式

见 [UI-DESIGN.md 第六节](UI-DESIGN.md#六主题切换的三种失效方式)。要点：
绝对 pack URI（`pack://application:,,,/LanShare;component/...`）、原地改 `Source`、已 Show 窗口要重推背景色。

### 4. `WindowStyle=None` 的副作用

| 问题 | 解决 |
|---|---|
| 窗口是直角 | `DwmSetWindowAttribute(hwnd, 33, Round)` |
| 标题栏按钮点不动 | `IsHitTestVisibleInChrome="True"` **且**拖动处理器排除按钮 |
| 最大化盖住任务栏 | 用 `WindowChrome`，不要手工改 `WindowState` |

## 四、打包与分发

### 1. Inno Setup 的提权时序

**核心问题**：Inno 默认的 `SetupLdr.e32` 清单是 `asInvoker`，**先**把文件解到 `%TEMP%\is-*.tmp`、
**后**才申请 UAC。国产安全软件（联想电脑管家内置的火绒引擎）会拦截「非提权进程静默写 Temp」，
报 **Error 5（拒绝访问）**，用户侧表现是「安装程序无法创建临时目录」。

**解决**：把 `SetupLdr.e32` 的 manifest 改成 `requireAdministrator`，让 loader **启动即提权**：

```powershell
mt.exe -inputresource:SetupLdr.e32;#1 -out:ldr.manifest
# 把 level="asInvoker" 改成 level="requireAdministrator"
mt.exe -manifest:ldr.elevated.manifest -outputresource:SetupLdr.e32;#1
```

补丁脚本：`signing/Patch-InnoSetup-Loader.ps1`（需要 Windows SDK 的 `mt.exe`）。
它默认就改项目内的 `signing/InnoSetup6-patched`，普通权限即可；只有指向 `C:\Program Files (x86)\Inno Setup 6`
时才需要管理员。脚本会自己回读验证，输出 `PATCH OK` 才算成功，已是 `requireAdministrator` 时直接跳过。

```powershell
.\signing\Patch-InnoSetup-Loader.ps1 -InnoDir .\signing\InnoSetup6-patched
```

只有三种情况要重跑：换机器后想用系统安装的 Inno、Inno Setup 升级导致副本被覆盖、副本损坏或从别处重拷。

**字节级验证**（比猜快得多，也不需要管理员权限）：

```powershell
$b = [IO.File]::ReadAllBytes("signing\InnoSetup6-patched\SetupLdr.e32")
$a = [Text.Encoding]::ASCII.GetString($b)
$i = $a.IndexOf('requestedExecutionLevel')
$a.Substring($i, 60).Split([char]0)[0]   # 期望 level="requireAdministrator"
```

**编译必须用打过补丁的那份 ISCC**：`signing/InnoSetup6-patched/ISCC.exe`。
用系统装的那份编译，打出来的安装包还是旧的 `asInvoker` loader，白打。

`signing/InnoSetup6-patched/` **随仓库提供**（82 个文件 / 约 32 MB）。这是个有意识的权衡：

- 它只是官方二进制 + 一个改过 manifest 的 `SetupLdr.e32`，**不含源码、不含私钥**（`*.pfx`/`*.cer` 已排除）
- 不进库的话，每个贡献者都要 `robocopy` + 跑 `mt.exe` 打补丁，**极易做漏**；
  而漏掉这个补丁的后果是「别人装你的包报 Error 5」——一种本地测不出来、只有用户才会遇到的故障
- `scripts/publish.ps1` 硬性依赖它，缺失直接构建失败

重建（换机器 / Inno 升级后）：

```powershell
robocopy "C:\Program Files (x86)\Inno Setup 6" signing\InnoSetup6-patched /E
.\signing\Patch-InnoSetup-Loader.ps1 -InnoDir .\signing\InnoSetup6-patched
```

必须**整目录**复制：目录里的 `.issig` 是 Inno 自己的签名校验文件，缺一个 ISCC 就拒绝编译。

**怎么查这份副本的 Inno 版本**：`ISCC.exe` 不写 PE 版本资源，右键文件属性看到的 `FileVersion` 一律是
`0.0.0.0`，别被它误导。版本号只出现在**编译时的横幅**里（`ISCC /?` 的帮助屏不打印它）：

```powershell
# 随便编译一个脚本，看输出第 7 行左右：
& .\signing\InnoSetup6-patched\ISCC.exe some.iss 2>&1 | Select-String "Compiler engine version"
# Compiler engine version: Inno Setup 6.7.1
```

当前副本是 **Inno Setup 6.7.1**。`whatsnew.htm` 里也有版本条目，但版本号是拆散在 HTML 标签里的，
正则抓不可靠（实测抓不到），只能人工看，不要写成脚本解析。

还有一处配套的坑：`.iss` 里必须写 `DisablePrecompiledFileVerifications=setupldr`
（`scripts/installer.iss` 已写）。`SetupLdr.e32` 的 manifest 被改过之后它的 `.issig` 就对不上了，
不关掉这项校验，ISCC 会直接编译失败。

### 2. 不要自己写安装器

JavaDeck 试过：手写一个 `requireAdministrator` 清单 + 自解压 zip 的 WinForms 安装器。
结果是「和整个系统格格不入」——WinForms 的默认样式和 Windows 安装向导完全不同，用户一眼就看出来。

**正确做法**：用 Inno Setup 的标准向导，只打补丁解决提权时序问题。

### 3. 单文件发布的 ILLink 陷阱

想用 `-p:PublishSingleFile=true` 打单文件时，即使 `PublishTrimmed=false` 也会要求
`Microsoft.NET.ILLink.Tasks` 包。离线环境（NuGet 缓存里没有）直接失败：

```
error NU1100: 无法解析 net8.0-windows 的 Microsoft.NET.ILLink.Tasks (>= 8.0.20)
```

**本项目的选择**：`-p:PublishSingleFile=false` + `--self-contained true`，多文件发布后由 Inno 打包成单个 exe。
体积换稳定，不折腾裁剪。

### 3.5 发布目录里必须带什么

`scripts/publish.ps1` 复制进 `dist/app/` 的文件（安装包与绿色版都从这里取）：

| 文件 | 为什么必须有 |
| --- | --- |
| `LanShare.exe` + 自包含运行时 | 程序本体 |
| `Tools/7za.exe` | 多文件打包成 `.7z`，缺了创建分享会直接失败（脚本里有断言） |
| `LanShare.Shell.dll` | Windows 11 一级右键菜单的 `IExplorerCommand` 实现，必须与 exe 同目录 |
| `Licenses/*` | 第三方组件（7-Zip / Bootstrap / Bootstrap Icons）的许可文本 |
| `README.md` | 离线说明书 |
| `docs/nginx.conf.example` | 外网反代示例 |
| `LICENSE` | **本程序自身的 GPL-3.0 全文** |

最后一条容易漏。GPL-3.0 要求分发二进制时附上许可证全文；而且 `AboutWindow` 的「查看许可证」
优先打开 `AppContext.BaseDirectory + "LICENSE"`，没有才回落到 GitHub 页面 —— 少了它就是
「合规缺口 + 按钮永远走回落分支」双输。所以 `publish.ps1` 里对 LICENSE 用了**断言式复制**：
源文件不存在就直接抛错终止构建，不允许静默少文件。

要加新文件进发布目录时，记得同样考虑「缺失时构建失败」而不是静默跳过。

### 4. 版本号只有一个来源

版本号唯一来源是 `Directory.Build.props` 的 `<Version>`：

```xml
<Version>1.1.0</Version>
<InformationalVersion>1.1.0</InformationalVersion>
<RepositoryUrl>https://github.com/cfengli007/LanShare</RepositoryUrl>
```

三个消费方必须一致：

1. 程序集版本 / `AssemblyInformationalVersion` —— 自更新 `SelfUpdater.CurrentVersion` 读它（`+<sha>` 后缀会截掉）
2. `scripts/publish.ps1` 从该文件读 `$version`，用 `ISCC.exe /DAppVersion=$version` 传给 Inno
3. Inno 的 `{#AppVersion}` —— 决定安装包文件名与卸载列表里的版本

Inno 脚本里必须用 `#ifndef AppVersion` 兜底，否则手动编译时报未定义：

```iss
#ifndef AppVersion
  #define AppVersion "1.1.0"   ; 兜底值，必须与 Directory.Build.props 的 <Version> 同步
#endif
```

**发版时只改 `Directory.Build.props`**，然后跑 `publish.ps1`。

### 5. 旧安装器残留的卸载项

早期方案（`scripts/install.ps1` 用户级安装）写的是 `HKCU` 卸载项，Inno 写的是
`HKCU\...\Uninstall\{4F1C2E8A-...}_is1`，两套会并存。需要用管理员权限手动删旧项：

```powershell
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall" | Select-String LanShare
```

## 五、GitHub 集成

### 1. REST API 的未认证限流

```
403 API rate limit exceeded for <IP>
```

限额 **60 次/小时/IP**。更新检查是每台机器各查各的，很容易超。

**替代方案**（都不限流）：

| 用途 | 端点 | 返回 |
|---|---|---|
| 最新版本号 | `GET /releases/latest` | 302，读 `Location` 里的 tag |
| 资产文件名 | `GET /releases/expanded_assets/<tag>` | HTML 片段，正则提取 |
| 更新说明 | `GET /releases.atom` | Atom XML |

实测连续 8 次：API `403×8`，上述三个 `302×8` / `200×8`。

### 2. `/releases/latest` 有边缘缓存

刚发布的新版本可能取不到（GitHub 的 CDN 有几百毫秒到几分钟的延迟）。**必须加 cache-buster**：

```csharp
$"{BaseUrl}/releases/latest?_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
```

项目实测：无参数拿到 `v2.5.0`，带参数才拿到 `v2.5.1`。同时用 `releases.atom`（实时）做交叉校验，
两路取较新的那个，`UpdateCheckResult.Source` 记录最终来自哪一路。

### 3. atom 正则里的坑

`entry` 的 `id` 形如：

```
tag:github.com,2008:Repository/1452775612/v2.5.0
```

仓库 ID 是**一串十进制数字**，正则必须写 `\d+`。从别的项目照抄时极易写成 `d+`（少一个反斜杠），
结果永远匹配不到，表现为「atom 交叉校验永远不生效」。

### 4. 沙箱内 git push 失败

```
fatal: unable to access '...': schannel: AcquireCredentialsHandle failed
```

改用 token 内联推送，**推完立刻清掉**：

```powershell
git remote set-url origin "https://x-access-token:$token@github.com/cfengli007/LanShare.git"
git push origin main
git remote set-url origin 'https://github.com/cfengli007/LanShare'   # 立刻还原
```

## 六、版本号比较

**不能简单拼接数字**。`1.10` 与 `1.9` 按字符串比会得到错误结果，`21.0.13` 与 `21.0.12.1` 也一样。

本项目的 `SelfUpdater.CompareVersion` 做法：

1. 剥掉前缀 `v`/`V` 和后缀 `-pre` / `+sha`
2. 按 `.` 分段，每段取前导数字转 `int`
3. 逐段比较，缺的段补 0

```csharp
// 关键就是"缺段补 0"：1.2 与 1.2.0 必须判等
var xi = i < x.Length ? x[i] : 0;
var yi = i < y.Length ? y[i] : 0;
```

JavaDeck 里用的是「每段固定 4 位十进制拼接」的等价写法（`21.0.13` → `21000130000`），
思路一致：**按段比较，不要让位数串位**。

## 七、Windows 系统集成

### 1. 注册表 WOW64 重定向

查 .NET 运行时，按 64 位视图读 `SOFTWARE\dotnet\...` 可能找不到——32 位安装器登记在
`SOFTWARE\WOW6432Node\dotnet\...`。两个都要查：

```csharp
foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
```

更可靠的判断是查**目录**：`C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.*`。
Inno 的 `[Code]` 里就是目录 + 两条注册表一起验，任一命中即认为已安装。

### 2. 命名管道单实例

LanShare 用命名管道做单实例 + 路径转发（右键多选分享会连续起多个进程）。
要点：

- 转发必须在**线程池**上等（`Task.Run(...).GetAwaiter().GetResult()`）。
  在 UI 线程上直接 `.Result` 会死锁——续体需要一个能跑的 Dispatcher 上下文，而它被自己挡住了。
- 600 ms 去抖合并多选：资源管理器对多选会**每个文件起一个进程**。
- 主实例崩溃后管道失效，要给出「请先在托盘退出旧实例」的明确提示，而不是静默失败。

### 3. 右键菜单（`IExplorerCommand`）

- Windows 11 的一级右键菜单要求实现 `IExplorerCommand`（不是老的 `IContextMenu`），
  通过 `ExplorerCommandHandler` 指向 CLSID，CLSID 的 `InprocServer32` 指向 `LanShare.Shell.dll`。
- `MultiSelectModel=Player` 才能一次传多个路径。
- 纯 `HKCU`，不需要管理员、不需要证书、不需要 MSIX。
- 装完不刷新是正常的：注册表变更不会通知已打开的资源管理器窗口，重启 explorer 或注销重登。
- 安装包是**提权**运行的，直接写 `HKCU` 可能落到管理员账户下。
  所以 `[Run]` 里用 `runasoriginaluser` 再跑一次 `--install-context-menu`。

### 4. 长路径

`app.manifest` 里已开 `longPathAware`。但仍有雷区：

- `Directory.Delete(path, true)` 删不掉 junction，要用 `cmd /c rmdir`
- 目录联接的 reparse point 需要 NT 对象管理器前缀 `\??\`（不是 `\\`）

## 八、网络与 HTTP 细节

### 1. 断点续传依赖 Range 与 ETag

`/d/{id}` 必须正确响应 `Range` 并给出稳定的 `ETag`，否则 IDM / aria2 / 浏览器都续不上。
中文文件名用 RFC 5987 的 `filename*=UTF-8''...` 形式。

### 2. 下载即焚的语义边界

「首次完整下载完成后销毁」——**分段请求本身不能触发销毁**，否则三分片下载会把分享在第 1 片就烧掉。
实现上在响应**完整写完**之后才调 `CompleteDownloadAsync`。

### 3. 上传分片 8 MiB

`UploadSessionService.ChunkSize = 8 * 1024 * 1024`。网页端 `app.js` 的 fallback 也是同一常量。
改一处必须改两处，否则服务端会以 `chunk_too_large` 拒绝。

### 4. Nginx 反代必须关缓冲

```nginx
proxy_request_buffering off;
proxy_buffering off;
client_max_body_size 2g;   # 要 >= 设置里的上传上限
```

不关的话 Nginx 会先把整个大文件缓冲到磁盘再转发，2 GiB 上传会卡在 0% 很久然后 413。

## 九、工程实践

### 1. 先探测环境，再定方案

JavaDeck 开工前先跑诊断脚本，得到的事实直接决定了架构（.NET 运行时已装 → 可以框架依赖；
scoop 里有 5 个 full 版 JDK → 需要变体识别）。**不要凭感觉设计**，先花十分钟把环境的实际情况查清楚。

### 2. 用探针代替猜测

排查主题问题时不猜，直接把实际值 dump 到文件：

```csharp
File.WriteAllText(debugPath, $"args={args} theme={theme} dark={isDark} bg={bgColor}");
```

一眼看出 `theme=Dark dark=True bg=#FF15171C` 但窗口仍是浅色 —— 定位到「资源对但没传播」，
而不是在 XAML 里翻半天。

### 3. 自动化测试要能真正验证

有效的：

- **UI 命中测试**：`WM_NCHITTEST` 返回值判断按钮区域是否被系统拦截
- **窗口主题验证**：反射创建窗口，读 `Background` 颜色
- **字节级清单检查**：读 exe 字节找 `requestedExecutionLevel`
- **网络端点实测**：连发 8 次看是否限流
- **HTTP 端到端**：`node scripts/e2e.mjs`（需要先跑起服务），覆盖 cookie 身份、上传、下载、终止、越权

无效的：

- `PrintWindow` 截图 —— 返回缓存表面，三次结果字节完全相同才发现

### 4. 沙箱限制要如实告诉用户

反复出现的三种说法：

```
我这边沙箱写不了 C:\Users\...\AppData
我这边 SChannel 连不上 HTTPS
我这边 UAC 弹窗没人点
```

这些**不是产品 bug**，要明确区分，并给出用户侧的验证方法，不要含糊成「已修复」。

## 十、常用命令速查

```powershell
# 编译（永远单项目）
dotnet build src/LanShare.Desktop/LanShare.Desktop.csproj -m:1 -nodeReuse:false

# 跑起来
dotnet run --project src/LanShare.Desktop

# 测试
dotnet test tests/LanShare.Tests/LanShare.Tests.csproj -m:1 -nodeReuse:false

# 端到端（需服务已启动）
node scripts/e2e.mjs http://127.0.0.1:8910

# 网页静态预览（不起服务，只看前端）
node scripts/static-preview.mjs          # http://127.0.0.1:8899

# 一键打包（内部已带 -m:1 -nodeReuse:false --disable-build-servers）
pwsh -File .\scripts\publish.ps1

# 只改托管代码时跳过原生编译
pwsh -File .\scripts\publish.ps1 -SkipNative

# 只想验证打包链路（跳过测试与原生编译）
pwsh -File .\scripts\publish.ps1 -SkipTests -SkipNative

# 右键菜单命令行装/卸/查
& '.\dist\app\LanShare.exe' --install-context-menu
& '.\dist\app\LanShare.exe' --check-context-menu    # 已装 0，未装 2

# 用指定页面启动（overview / shares / new / settings）
& '.\dist\app\LanShare.exe' --page=settings

# 从命令行创建分享（右键菜单内部就是用它）
& '.\dist\app\LanShare.exe' --share "D:\file.zip"
```
