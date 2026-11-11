#requires -Version 5.1
<#
.SYNOPSIS
  将 LanShare 安装到当前用户，无需管理员权限。
.PARAMETER Source
  包含 LanShare.exe 的目录。默认使用本脚本旁边的 app\。
.PARAMETER InstallDir
  安装目录，默认 %LOCALAPPDATA%\Programs\LanShare。
.PARAMETER NoDesktopShortcut
  不创建桌面快捷方式。
.PARAMETER NoLaunch
  安装完成后不启动。
#>
[CmdletBinding()]
param(
    [string]$Source,
    [string]$InstallDir,
    [switch]$NoDesktopShortcut,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
if (-not $Source) { $Source = Join-Path $PSScriptRoot 'app' }
if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\LanShare' }

$sourceExe = Join-Path $Source 'LanShare.exe'
if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw "找不到 $sourceExe。请把 install.ps1 放在包含 app\ 的分发包根目录，或用 -Source 指定发布目录。"
}

$running = Get-Process -Name LanShare -ErrorAction SilentlyContinue
if ($running) {
    $pids = ($running | ForEach-Object { $_.Id }) -join ', '
    throw "LanShare 正在运行（PID: $pids）。请先从托盘退出，再安装。"
}

Write-Host "安装到 $InstallDir" -ForegroundColor Cyan
if (Test-Path -LiteralPath $InstallDir) {
    Remove-Item -LiteralPath $InstallDir -Recurse -Force
}
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item -Path (Join-Path $Source '*') -Destination $InstallDir -Recurse -Force

$exe = Join-Path $InstallDir 'LanShare.exe'
$shell = New-Object -ComObject WScript.Shell

function New-Shortcut([string]$Path, [string]$Target, [string]$WorkingDirectory) {
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $Target
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.IconLocation = "$Target,0"
    $shortcut.Description = '内网快传 LanShare'
    $shortcut.Save()
}

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
New-Shortcut (Join-Path $startMenu '内网快传 LanShare.lnk') $exe $InstallDir

if (-not $NoDesktopShortcut) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    New-Shortcut (Join-Path $desktop '内网快传 LanShare.lnk') $exe $InstallDir
}

Write-Host '已创建开始菜单快捷方式' -ForegroundColor Green
if (-not $NoDesktopShortcut) { Write-Host '已创建桌面快捷方式' -ForegroundColor Green }

# 右键菜单：纯 HKCU 注册表，指向随包分发的 LanShare.Shell.dll（IExplorerCommand）
$clsid = '{D4A7C1E2-9B3F-4E6A-8C21-7F5B0D9A2E31}'
$shellDll = Join-Path $InstallDir 'LanShare.Shell.dll'
if (Test-Path -LiteralPath $shellDll) {
    $verbs = @(
        'Software\Classes\*\shell\LanShare',
        'Software\Classes\Directory\shell\LanShare',
        'Software\Classes\Directory\Background\shell\LanShare'
    )
    foreach ($verb in $verbs) {
        $key = 'HKCU:\' + $verb
        if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key -Recurse -Force }
        New-Item -Path $key -Force | Out-Null
        New-ItemProperty -LiteralPath $key -Name 'MUIVerb' -Value '用内网快传分享' -PropertyType String -Force | Out-Null
        New-ItemProperty -LiteralPath $key -Name 'Icon' -Value ($exe + ',0') -PropertyType String -Force | Out-Null
        New-ItemProperty -LiteralPath $key -Name 'ExplorerCommandHandler' -Value $clsid -PropertyType String -Force | Out-Null
        New-ItemProperty -LiteralPath $key -Name 'MultiSelectModel' -Value 'Player' -PropertyType String -Force | Out-Null
    }

    $clsidKey = 'HKCU:\Software\Classes\CLSID\' + $clsid
    if (Test-Path -LiteralPath $clsidKey) { Remove-Item -LiteralPath $clsidKey -Recurse -Force }
    New-Item -Path $clsidKey -Force | Out-Null
    New-ItemProperty -LiteralPath $clsidKey -Name '(default)' -Value 'LanShare Explorer Command' -PropertyType String -Force | Out-Null
    $inproc = $clsidKey + '\InprocServer32'
    New-Item -Path $inproc -Force | Out-Null
    New-ItemProperty -LiteralPath $inproc -Name '(default)' -Value $shellDll -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $inproc -Name 'ThreadingModel' -Value 'Apartment' -PropertyType String -Force | Out-Null
    Write-Host '已注册资源管理器右键菜单（重启资源管理器后生效）' -ForegroundColor Green
} else {
    Write-Warning "缺少 $shellDll，跳过右键菜单注册。"
}

Write-Host "程序：$exe"
Write-Host '提示：在 LanShare 设置里可重装右键菜单、放行防火墙。'

if (-not $NoLaunch) {
    Start-Process -FilePath $exe -WorkingDirectory $InstallDir
}