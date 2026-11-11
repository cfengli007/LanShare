#requires -Version 5.1
<#
.SYNOPSIS
  构建 LanShare 发布包：自包含程序、右键菜单组件、安装包、绿色版。

.DESCRIPTION
  产物（全部在 dist\ 下）：
    dist\app                                自包含程序目录（可直接运行，含 Shell 右键菜单组件）
    dist\LanShare-Setup-<版本>-win-x64.exe  安装包（推荐，装到 Program Files，需要 UAC）
    dist\LanShare-<版本>-win-x64.zip        绿色版压缩包（解压即用）
    dist\*.sha256                           校验值

  构建机需要：.NET 8 SDK、signing\InnoSetup6-patched（仓库内已带）、
              Visual Studio C++ 生成工具（编译 LanShare.Shell.dll）。
  目标机无需安装 .NET 8；安装包需要一次 UAC 提权。

  版本号只有一个来源：Directory.Build.props 的 <Version>。
  本脚本会在打包前后各断言一次「三处版本一致」，不一致直接失败：
    ① Directory.Build.props      <Version>
    ② 程序集 InformationalVersion（自更新 SelfUpdater.CurrentVersion 读的就是它）
    ③ Inno Setup 的 /DAppVersion（安装包文件名 + 卸载列表里的版本）

.NOTES
  ⚠️ 安装包必须用 signing\InnoSetup6-patched\ISCC.exe 编译，不能用系统装的 Inno Setup 6。
     那份副本的 SetupLdr.e32 manifest 已改成 requireAdministrator，loader 启动即提权、
     之后才创建临时目录 —— 否则联想电脑管家内置火绒引擎会拦截非提权进程写 %TEMP%，
     安装时报 Error 5。详见 README 的「为什么用打过补丁的 Inno Setup」。
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [switch]$SkipNative,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$publishDir = Join-Path $dist 'app'

# ── 版本号：唯一来源 Directory.Build.props ──────────────────────────────────
$props = [xml](Get-Content (Join-Path $root 'Directory.Build.props'))
$version = $props.Project.PropertyGroup.Version | Select-Object -First 1
if (-not $version) { throw 'Directory.Build.props 里没有找到 <Version>。' }
$version = "$version".Trim()

$setupName = "LanShare-Setup-$version-$Runtime"
$packageName = "LanShare-$version-$Runtime"
$setupExe = Join-Path $dist "$setupName.exe"
$portableZip = Join-Path $dist "$packageName.zip"

Write-Host "== LanShare $version ($Runtime, $Configuration) ==" -ForegroundColor Cyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '找不到 dotnet。请安装 .NET 8 SDK 并重新打开终端。'
}

# ── 0. 前置校验：Inno 必须用打过补丁的那份 ──────────────────────────────────
$iscc = Join-Path $root "signing\InnoSetup6-patched\ISCC.exe"
$patchedLdr = Join-Path $root "signing\InnoSetup6-patched\SetupLdr.e32"
if (-not $SkipInstaller) {
    if (-not (Test-Path -LiteralPath $iscc)) {
        throw ("找不到打过补丁的 Inno Setup 编译器：`n  $iscc`n" +
               "请把 Inno Setup 6 复制到 signing\InnoSetup6-patched 并运行 " +
               "signing\Patch-InnoSetup-Loader.ps1 打补丁。`n" +
               "⚠️ 不要改用 C:\Program Files (x86)\Inno Setup 6\ISCC.exe —— 那份 loader 是 asInvoker，" +
               "在装有联想电脑管家的机器上安装会因写 %TEMP% 被火绒引擎拦截而报 Error 5。")
    }
    if (-not (Test-Path -LiteralPath $patchedLdr)) {
        throw "signing\InnoSetup6-patched\SetupLdr.e32 缺失，这份 Inno 副本不完整。"
    }
    Write-Host "  编译器  $iscc" -ForegroundColor DarkGray
}

# ── 1. 清理 ─────────────────────────────────────────────────────────────────
if (Test-Path $dist) {
    Write-Host "清理 $dist" -ForegroundColor DarkGray
    Remove-Item -LiteralPath $dist -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

# ── 2. 原生 COM 组件（Windows 11 一级右键菜单）─────────────────────────────
$shellDll = Join-Path $root 'native\LanShare.Shell\build\LanShare.Shell.dll'
if (-not $SkipNative) {
    Write-Host '== 编译 LanShare.Shell.dll ==' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'build-native.ps1') -Configuration $Configuration
    if (-not (Test-Path $shellDll)) { throw "原生组件未生成：$shellDll" }
}
elseif (-not (Test-Path $shellDll)) {
    throw "缺少 LanShare.Shell.dll（$shellDll），请先运行 scripts\build-native.ps1。"
}

# ── 3. 测试（可选）──────────────────────────────────────────────────────────
if (-not $SkipTests) {
    Write-Host '== dotnet test ==' -ForegroundColor Cyan
    & dotnet test (Join-Path $root 'LanShare.sln') -c $Configuration --nologo -m:1 -nodeReuse:false --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw "测试失败（exit $LASTEXITCODE）。" }
}

# ── 4. 发布主程序 ───────────────────────────────────────────────────────────
Write-Host '== dotnet publish ==' -ForegroundColor Cyan
$publishArgs = @(
    'publish', (Join-Path $root 'src\LanShare.Desktop\LanShare.Desktop.csproj'),
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:GenerateDocumentationFile=false',
    '-o', $publishDir,
    '--nologo',
    # 本机（以及很多 CI）会有 MSBuild 常驻节点残留，导致解决方案/多项目还原出现
    # 「生成失败 / 0 个错误」这种没有真实报错的失败。强制单节点 + 不复用 + 禁用
    # 构建服务器可以稳定绕过。不要去掉这三项。
    '-m:1',
    '-nodeReuse:false',
    '--disable-build-servers'
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "发布失败（exit $LASTEXITCODE）。" }

$exe = Join-Path $publishDir 'LanShare.exe'
if (-not (Test-Path $exe)) { throw "发布目录缺少 LanShare.exe：$publishDir" }
if (-not (Test-Path (Join-Path $publishDir 'Tools\7za.exe'))) {
    throw '发布目录缺少 Tools\7za.exe，请检查 LanShare.Desktop.csproj 的 Content 配置。'
}

# ── 5. 版本一致性断言（②）───────────────────────────────────────────────────
# 自更新读的是 AssemblyInformationalVersion，它被写进 Win32 版本资源的 ProductVersion。
# 直接读产物，才能证明「用户机器上 SelfUpdater.CurrentVersion 拿到的就是这个版本号」。
$productVersion = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
if (-not $productVersion) { throw "读不到 $exe 的 ProductVersion（InformationalVersion 没被注入？）" }
# InformationalVersion 可能带 "+<sha>" 后缀（CI / SourceLink），自更新那边会截掉，这里也截掉再比。
$asmVersion = ($productVersion -split '\+')[0].Trim()
$fileVersion = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion

Write-Host "  版本自检： props=$version  assembly=$asmVersion  file=$fileVersion" -ForegroundColor DarkGray
if ($asmVersion -ne $version) {
    throw ("版本号不一致！`n" +
           "  Directory.Build.props <Version>      = $version`n" +
           "  程序集 InformationalVersion (产物内) = $asmVersion`n" +
           "三处版本必须一致，否则自更新会认为自己永远是最新版（或反复提示更新）。`n" +
           "请检查 Directory.Build.props 的 <Version> 与 <InformationalVersion>。")
}

# ── 6. 右键菜单组件：IExplorerCommand 实现 DLL，必须与 LanShare.exe 同目录 ──
Write-Host '== 复制右键菜单组件 ==' -ForegroundColor Cyan
$shellTarget = Join-Path $publishDir 'LanShare.Shell.dll'
Copy-Item -LiteralPath $shellDll -Destination $shellTarget -Force
if (-not (Test-Path $shellTarget)) { throw "右键菜单组件未复制到发布目录：$shellTarget" }

# 附加说明文档（安装包与绿色版都会带上）
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $publishDir -Force

# LICENSE 必须随二进制分发：本项目是 GPL-3.0，分发二进制时须附许可证全文，
# 否则是合规缺口。AboutWindow 的「查看许可证」也优先读程序目录下的 LICENSE，
# 不放这里那个按钮就永远只能回落到 GitHub 页面。
$licenseSource = Join-Path $root 'LICENSE'
if (-not (Test-Path $licenseSource)) { throw "缺少 LICENSE 文件：$licenseSource" }
Copy-Item -LiteralPath $licenseSource -Destination (Join-Path $publishDir 'LICENSE') -Force

$docsOut = Join-Path $publishDir 'docs'
New-Item -ItemType Directory -Path $docsOut -Force | Out-Null
foreach ($doc in @('nginx.conf.example')) {
    $srcDoc = Join-Path $root "docs\$doc"
    if (Test-Path $srcDoc) { Copy-Item -LiteralPath $srcDoc -Destination $docsOut -Force }
}

# ── 7. Inno Setup 安装包（③ 版本号经 /DAppVersion 传入）────────────────────
if (-not $SkipInstaller) {
    Write-Host '== 生成安装包 ==' -ForegroundColor Cyan
    & $iscc "/DAppVersion=$version" (Join-Path $PSScriptRoot 'installer.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败（exit $LASTEXITCODE）。" }
    if (-not (Test-Path $setupExe)) {
        throw "安装包未生成：$setupExe（检查 installer.iss 的 OutputBaseFilename 与版本号）"
    }

    # 产物名里必须带正确版本号 —— 「三处一致」的最后一道闸。
    if ((Split-Path $setupExe -Leaf) -notmatch [regex]::Escape($version)) {
        throw "安装包文件名里没有版本号 $version：$setupExe"
    }

    $hash = Get-FileHash -LiteralPath $setupExe -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $($hash.Path | Split-Path -Leaf)" |
        Set-Content -LiteralPath "$setupExe.sha256" -Encoding ASCII
    Write-Host "安装包 SHA256: $($hash.Hash)" -ForegroundColor Green
}

# ── 8. 绿色版压缩包 ─────────────────────────────────────────────────────────
if (-not $NoZip) {
    Write-Host '== 压缩绿色版 ==' -ForegroundColor Cyan
    # 用 "$publishDir\*" 而不是 "$publishDir"：让压缩包里直接是文件，
    # 而不是多一层 app\ 目录（便携包解压即用）。
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $portableZip -CompressionLevel Optimal -Force
    $hash = Get-FileHash -LiteralPath $portableZip -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $($hash.Path | Split-Path -Leaf)" |
        Set-Content -LiteralPath "$portableZip.sha256" -Encoding ASCII
}

# ── 9. 产物清单 ─────────────────────────────────────────────────────────────
$sizeMb = [math]::Round(((Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)

Write-Host ''
Write-Host "完成：LanShare $version" -ForegroundColor Green
Write-Host ''
Write-Host '产物清单：' -ForegroundColor Cyan
Write-Host ("  程序目录  {0}  ({1} MB)" -f $publishDir, $sizeMb)

$artifacts = @()
if (Test-Path $setupExe) { $artifacts += Get-Item $setupExe }
if (Test-Path $portableZip) { $artifacts += Get-Item $portableZip }
foreach ($a in $artifacts) {
    $mb = [math]::Round($a.Length / 1MB, 1)
    Write-Host ("  {0,-46} {1,8} MB" -f $a.Name, $mb)
}

Write-Host ''
Write-Host '校验值：' -ForegroundColor Cyan
Get-ChildItem $dist -Filter '*.sha256' -File | ForEach-Object {
    Write-Host ("  {0}  {1}" -f ($_.Name -replace '\.sha256$', ''), (Get-Content -Raw -LiteralPath $_.FullName).Trim())
}

if (Test-Path $setupExe) {
    Write-Host ''
    Write-Host '发布到 GitHub Releases 时的资产名（必须与自更新约定一致）：' -ForegroundColor DarkGray
    Write-Host ("  {0}" -f (Split-Path $setupExe -Leaf)) -ForegroundColor DarkGray
    if (Test-Path $portableZip) { Write-Host ("  {0}" -f (Split-Path $portableZip -Leaf)) -ForegroundColor DarkGray }
    Write-Host '  ⚠️ 文件名不能改：SelfUpdater 靠 LanShare-Setup-<version>-win-x64.exe 这个约定找安装包。' -ForegroundColor DarkYellow
}
