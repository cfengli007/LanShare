# 将 Inno Setup 的 SetupLdr.e32 manifest 的 executionLevel 从 asInvoker 改成 requireAdministrator。
#
# ┌─ 为什么需要这个补丁 ──────────────────────────────────────────────────┐
# │ 部分国产安全软件（联想电脑管家内置的火绒引擎）会拦截「非提权进程        │
# │ 静默写 %TEMP%」，报 Error 5。Inno 默认的 SetupLdr.e32 清单是 asInvoker：│
# │ loader 先把文件解到 %TEMP%，然后才申请 UAC —— 正好撞上拦截。          │
# │ 改成 requireAdministrator 后，loader 启动即弹 UAC，提权成功之后才      │
# │ 创建临时目录，绕开这一拦截。                                           │
# └───────────────────────────────────────────────────────────────────────┘
#
# 本脚本不是每次构建都要跑：项目里 signing\InnoSetup6-patched 已经是打过补丁的副本
# （scripts\publish.ps1 直接用那份 ISCC.exe 编译）。只有这几种情况才需要重跑：
#   • 第一次搭环境，把 Inno Setup 6 复制到 signing\InnoSetup6-patched 之后
#   • Inno Setup 升级（副本被覆盖，补丁丢失）
#   • signing\InnoSetup6-patched 损坏或从别处重新拷贝
#
# 用法（改项目内的副本通常普通权限就够；改 Program Files 下的需要管理员）：
#   .\signing\Patch-InnoSetup-Loader.ps1
#   .\signing\Patch-InnoSetup-Loader.ps1 -InnoDir .\signing\InnoSetup6-patched
#
# 幂等：已经打过补丁时直接跳过并返回成功。
param(
    [string]$InnoDir = (Join-Path $PSScriptRoot 'InnoSetup6-patched')
)

$ErrorActionPreference = 'Stop'

# 定位 mt.exe（Windows SDK，优先 x64）
$mt = $null
$kitRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
if (Test-Path $kitRoot) {
    $mt = Get-ChildItem -Path $kitRoot -Recurse -Filter mt.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $mt) { throw '未找到 mt.exe，请安装 Windows SDK（含 Manifest Tool）。' }
Write-Host ('mt.exe = ' + $mt)

$ldr = Join-Path $InnoDir 'SetupLdr.e32'
if (-not (Test-Path -LiteralPath $ldr)) { throw ('SetupLdr.e32 不存在: ' + $ldr) }

$work = Join-Path $InnoDir '_patchwork'
if (-not (Test-Path $work)) { New-Item -ItemType Directory -Path $work | Out-Null }
$mf  = Join-Path $work 'setupldr.manifest'
$mfe = Join-Path $work 'setupldr.elevated.manifest'
$mfv = Join-Path $work 'verify.manifest'

# 1. 提取
$arg1 = '-inputresource:$ldr;#1'
$arg2 = '-out:$mf'
$p = Start-Process -FilePath $mt -ArgumentList $arg1, $arg2 -Wait -PassThru -NoNewWindow
if ($p.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $mf)) { throw '提取 manifest 失败' }

$m = Get-Content -Raw -LiteralPath $mf
# 单引号字符串里双引号无需转义，正好用来匹配 manifest 里的 level="..."
$levelBefore = [regex]::Match($m, 'level="([^"]+)"').Groups[1].Value
Write-Host ('提取成功，当前 level = ' + $levelBefore)

if ($levelBefore -eq 'requireAdministrator') {
    Write-Host '已经是 requireAdministrator，无需修改。'
    Remove-Item -LiteralPath $mf, $mfe, $mfv -Force -ErrorAction SilentlyContinue
    exit 0
}

# 2. 替换
$m2 = $m.Replace('level="asInvoker"', 'level="requireAdministrator"')
Set-Content -LiteralPath $mfe -Value $m2 -NoNewline -Encoding UTF8

# 3. 嵌入
$arg3 = '-manifest:$mfe'
$arg4 = '-outputresource:$ldr;#1'
$p2 = Start-Process -FilePath $mt -ArgumentList $arg3, $arg4 -Wait -PassThru -NoNewWindow
if ($p2.ExitCode -ne 0) { throw '嵌入 manifest 失败' }

# 4. 回读验证
$p3 = Start-Process -FilePath $mt -ArgumentList $arg1, ('-out:' + $mfv) -Wait -PassThru -NoNewWindow
$mv = Get-Content -Raw -LiteralPath $mfv
$levelAfter = [regex]::Match($mv, 'level="([^"]+)"').Groups[1].Value
Write-Host ('修改完成，新 level = ' + $levelAfter)

# 清理
Remove-Item -LiteralPath $mf, $mfe, $mfv -Force -ErrorAction SilentlyContinue
if ($levelAfter -ne 'requireAdministrator') { throw ('验证失败：manifest 仍是 ' + $levelAfter) }
Write-Host 'PATCH OK' -ForegroundColor Green
