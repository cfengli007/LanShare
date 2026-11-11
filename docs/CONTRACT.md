# LanShare 内网快传 — 团队接口契约（v1，Lead 冻结）

产品：Windows 原生 WPF 应用 + 内嵌 Kestrel Web 服务。解决方案根：`D:\project-dsh\work\lanshare`。
UI 库：桌面端 WPF-UI 4.3.0（MIT）+ H.NotifyIcon.Wpf（托盘）；网页端 Bootstrap 5.3.3 + Bootstrap Icons（已本地化到 wwwroot/vendor，禁止 CDN）。
目标框架 net8.0 / net8.0-windows。所有中文文案使用中文。

## 项目与职责（写作用域）
- `src/LanShare.Core` + `src/LanShare.Server`（除 wwwroot）+ `tests/LanShare.Tests`：backend
- `src/LanShare.Server/wwwroot`：web（只写这个目录）
- `src/LanShare.Desktop`：desktop
- 根目录 csproj/sln、scripts、assets、docs、最终集成修复：Lead
- 禁止修改别人的作用域；需要契约变更先给 Lead 发消息。

## 已冻结的 Core 契约
见 `src/LanShare.Core/AppSettings.cs`、`Models.cs`、`Services.cs`、`NetworkInfo.cs`。不要修改其中公开 API 的名称/签名；可追加，但必须先通知 Lead。backend 实现：
- `ISettingsService`（settings.json，数据目录 %LOCALAPPDATA%\LanShare）
- `IShareService`（shares.json + 文件存储 + 过期清理 + 7z 打包 + burn-after-download）
- `IServerHost`（Kestrel 生命周期）
- `ILanShareRuntime`（组合根）
- 在 `LanShare.Server.LanShareServer.CreateRuntimeAsync()` 返回真实 runtime（当前是 NotImplementedException 桩）。

## Web API（backend 实现，web 按此调用；JSON 默认 camelCase）

Cookie 身份：任何 HTML/API 请求若没有 `lanshare_uid`，服务端生成 GUID 并 `Set-Cookie: lanshare_uid=...; Path=/; HttpOnly; SameSite=Lax; Max-Age=31536000`。非 Secure（内网 HTTP；外网由 Nginx 反代）。网页不需要自己存 owner id，凭 cookie 调 API。

### GET /api/health
200 `{ "status":"ok", "version":"1.0.0", "serverTime":"ISO8601", "uptimeSeconds":123 }`

### GET /api/config
200
```json
{
  "maxUploadBytes": 2147483648,
  "defaultExpiryMinutes": 1440,
  "burnAfterDownload": false,
  "port": 8910,
  "internalBaseUrl": "http://192.168.1.10:8910",
  "externalBaseUrl": "https://share.example.com",
  "productName": "内网快传",
  "version": "1.0.0",
  "lanAddresses": [{"interfaceName":"以太网","address":"192.168.1.10","description":"Realtek ..."}]
}
```

### GET /api/shares
仅返回当前 cookie 所有者的分享（桌面端创建的 owner=desktop，网络不可见），按 CreatedAt 倒序；包含已过期但未撤销的记录（`isExpired=true`），不包含已撤销记录。
200 `{ "shares": [ShareDto...] }`

ShareDto：
```json
{
  "id":"AbCd...", "displayName":"report.pdf", "downloadName":"report.pdf",
  "totalBytes":12345, "createdAt":"ISO", "expiresAt":"ISO",
  "burnAfterDownload":false, "isExpired":false, "isRevoked":false,
  "isPackaged":false, "isUploaded":true, "downloadCount":0, "source":"web",
  "internalUrl":"http://192.168.1.10:8910/s/AbCd...",
  "externalUrl":"https://share.example.com/s/AbCd...",
  "files":[{"name":"report.pdf","length":12345}]
}
```

### POST /api/shares
multipart/form-data：
- `files`：1..N 个文件（可重复字段）
- `expiryMinutes`：可选 int，1..43200；缺省用设置默认值
- `burn`：可选 `true`/`false`
- `title`：可选，展示名（否则单文件用文件名、多文件用 “N 个文件”）
成功：201 `{ "share": ShareDto }`。文件写入服务端存储；多文件自动 7z 打包（ShareDto.isPackaged=true，files 保留原文件名列表），单文件直接提供原文件下载。打包使用 7-Zip 的仅存储模式（`-mx=0`，Copy，不压缩），只为合并成单一下载文件。
错误：400 参数、413 超过上限、500 服务端错误，格式 `{ "error":"中文消息", "code":"bad_request|too_large|..." }`。
上传大小上限由设置 `MaxUploadBytes` 控制；需同时限制请求体和逐文件流式检查。

### GET /api/shares/{id}
仅 owner 可见（cookie 匹配）；200 `{ "share": ShareDto }`；404 unknown/not owner；410 expired/revoked。

### POST /api/shares/{id}/revoke
仅 owner；200 `{ "ok":true }`；403 非 owner；404 unknown；410 已失效。撤销时删除该分享的上传文件/生成的压缩包；桌面引用型分享只删除生成的缓存，不删用户原文件。

### GET /api/public/shares/{id}
无需 owner。可用时 200 `{ "share": { id, displayName, downloadName, totalBytes, createdAt, expiresAt, burnAfterDownload, downloadCount, isExpired, isRevoked, isPackaged, files:[...] } }`。
404 unknown；410 expired/revoked。该接口不能泄露服务端路径。

### GET /s/{id}
返回 share.html（前端根据 location.pathname 取 id，调 public API）。

### GET /d/{id}
直接下载（支持 Range）。404 unknown；410 expired/revoked；下载前 RegisterDownloadAsync，响应结束后 CompleteDownloadAsync（burn 分享首次下载完成后销毁，之后 410）。
Content-Disposition attachment，文件名为 `downloadName`，中文用 RFC5987 `filename*=UTF-8''...`。归档 Content-Type `application/x-7z-compressed`，其他 `application/octet-stream`。

### 其他
- `GET /` → index.html；`GET /favicon.ico` → 图标。
- 静态资源从嵌入资源提供（wwwroot 编入程序集），支持 content-type、ETag/Last-Modified 可选，无目录浏览。
- 未知 /api/* 返回 404 JSON。
- 下载链接 token 用加密随机 16 bytes base64url（22 字符）；分享 id 不含路径分隔符。
- 所有时间 UTC ISO8601，前端本地化显示。

## 后端实现要求
- Kestrel 绑定 `BindAddress:Port`；0.0.0.0 表示全部；端口冲突时 `IServerHost.LastError` 给出中文原因并触发 StateChanged。
- 日志：%LOCALAPPDATA%\LanShare\logs\server-YYYYMMDD.log；保留最近 7 天。
- shares.json 原子写入（临时文件 + File.Replace/Move），锁内读写；启动加载并清理过期项。
- 上传临时目录 %LOCALAPPDATA%\LanShare\storage\.tmp；成功移入 `<storage>/<shareId>/`；失败清理。
- 7-Zip：优先 `AppContext.BaseDirectory\Tools\7za.exe`（已随仓库 assets/tools/7za.exe 提供，desktop csproj 会复制），其次 `C:\Program Files\7-Zip\7z.exe`，再次 PATH 上的 7za/7z；不可用时返回明确错误。归档放存储目录，名为 `<safe-display-name>.7z`，以 `-mx=0` 仅存储方式创建，保证大文件/多文件打包速度。
- 多文件引用型（桌面右键多个文件）：创建 7z 到存储目录；不要复制/删除原文件。单文件：DownloadPath 直接指向原文件，OwnedPaths 为空。
- 文件夹分享：7z 打包整个文件夹（包含顶层文件夹名）。单个文件夹按“多文件”处理。
- 过期清理后台每 10 秒执行；服务停止时释放。
- 要提供可测试的 `LanShareServer.CreateRuntimeAsync()`；单元/集成测试放 tests/LanShare.Tests。
- 需要 NuGet 包可用（联网正常）。不要引入付费组件。

## 桌面端要求（WPF-UI + H.NotifyIcon）
- 程序名 `LanShare`，窗口中文标题“内网快传 LanShare”；主窗口 FluentWindow，Mica，左侧 NavigationView：概览 / 我的分享 / 新建分享 / 设置。
- 概览：服务状态、内网地址（可复制）、外网地址、打开网页、新建分享、最近分享。
- 我的分享：当前所有分享（含桌面端和网页端；desktop 可见全部），显示名称/大小/过期倒计时/下载次数/来源；操作：复制内网链接、复制外网链接、打开下载页、终止分享（确认后删除文件）。
- 新建分享：选择文件（多选）或文件夹，过期时间（10 分钟/1 小时/6 小时/24 小时/7 天/自定义），burn 开关，生成按钮；生成中用进度环；生成后显示两个 URL、复制、打开、在资源管理器中显示。
- 设置：绑定 IP（下拉：0.0.0.0 + 各网卡 IPv4）、端口、内网域名前缀、外网域名前缀、默认有效期、默认 burn、上传上限 MB、存储目录、开机自启、通知开关、安装/卸载右键菜单、防火墙放行、修改本机 IP；保存后持久化并在需要时重启服务。
- “修改 IP”对话框：列出网卡，支持 DHCP/静态 IP（IP、掩码/前缀、网关），调用 netsh 以管理员权限执行（runas）；同时提供打开 ncpa.cpl。
- 托盘：显示/隐藏主窗口、新建分享、打开网页、复制内网地址、设置、退出；关闭主窗口默认最小化到托盘。
- 右键菜单：HKCU\Software\Classes\*\shell\LanShare、Directory\shell、Directory\Background\shell；命令 ""<exe>" --share "%1""（背景用 "%V"）；MultiSelectModel=Player；图标 exe。卸载时删除。
- 单实例 + 命名管道：第二实例把 `--share <path>` 转发给主实例后退出；主实例 600ms 去抖合并多次选中文件，然后自动按默认设置创建分享（单文件直接分享，多文件/文件夹 7z）并跳到“新建分享”结果视图/通知。
- 通过 `LanShare.Server.LanShareServer.CreateRuntimeAsync()` 获取 `ILanShareRuntime` 并启动 Server。所有服务错误用 WPF-UI Snackbar/InfoBar 显示。
- 必须使用 app.ico 作为 ApplicationIcon/托盘图标；assets 里已有 app.ico。
- 设置修改流程：复制当前 Settings → 修改 → `runtime.ApplySettingsAsync(newSettings)`；UI 更新。
- 关闭窗口/退出：停止 runtime；避免进程残留。
- 不要引入需要许可证的 UI 库；不要修改 Core/Server 契约。

## 网页端要求（wwwroot）
- `index.html`：品牌 + 服务状态；拖拽/选择文件；文件列表（名称、大小、移除）；过期时间选择（10 分钟/1 小时/6 小时/24 小时/7 天/自定义分钟）；burn 开关；上传按钮 + 进度条（XMLHttpRequest progress）；结果卡片（内网/外网链接 + 复制 + 打开）；“我的分享”列表（API GET /api/shares）含倒计时、下载次数、查看、复制、终止（确认）；移动端适配；无 CDN。
- `share.html`：从路径取 id，GET /api/public/shares/{id}；文件名/大小/文件列表/过期倒计时/burn 提示/下载按钮/复制链接；过期或撤销显示友好状态；支持 Range 下载由浏览器处理。
- 文案中文；使用 Bootstrap 5.3.3 + Bootstrap Icons；可加自定义 app.css 但不重做设计系统；深色/浅色跟随系统可选，默认浅色渐变背景 + 卡片。
- 只能写 `src/LanShare.Server/wwwroot/**`。本地测试时若后端未就绪可先静态预览；后端就绪后必须实测上传/下载/撤销全链路。
- 不要把任何 token/owner 信息写死在页面。

## 验证命令
- 构建：`dotnet build LanShare.sln -c Debug`
- 测试：`dotnet test LanShare.sln -c Debug`
- 后端启动测试由 Lead 负责；desktop 可用 `dotnet run --project src/LanShare.Desktop` 调试。
