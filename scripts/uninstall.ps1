#requires -Version 5.1
<#
.SYNOPSIS
  卸载当前用户的 LanShare。
.PARAMETER RemoveData
  同时删除 %LOCALAPPDATA%\LanShare（设置、分享记录、上传/归档文件、日志）。不可恢复。
.PARAMETER InstallDir
  安装目录，默认 %LOCALAPPDATA%\Programs\LanShare。
#>
[CmdletBinding()]
param(
    [switch]$RemoveData,
    [string]$InstallDir
)

$ErrorActionPreference = 'Stop'
if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\LanShare' }

Get-Process -Name LanShare -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

$keys = @(
    'HKCU:\Software\Classes\*\shell\LanShare',
    'HKCU:\Software\Classes\Directory\shell\LanShare',
    'HKCU:\Software\Classes\Directory\Background\shell\LanShare',
    'HKCU:\Software\Classes\CLSID\{D4A7C1E2-9B3F-4E6A-8C21-7F5B0D9A2E31}'
)
foreach ($key in $keys) {
    if (Test-Path -LiteralPath $key) {
        Remove-Item -LiteralPath $key -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "已删除右键菜单 $key" -ForegroundColor DarkGray
    }
}
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'LanShare' -ErrorAction SilentlyContinue

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\内网快传 LanShare.lnk'
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) '内网快传 LanShare.lnk'
Remove-Item -LiteralPath $startMenu -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $desktop -Force -ErrorAction SilentlyContinue

if (Test-Path -LiteralPath $InstallDir) {
    Remove-Item -LiteralPath $InstallDir -Recurse -Force
    Write-Host "已删除 $InstallDir" -ForegroundColor Green
}

$dataDir = Join-Path $env:LOCALAPPDATA 'LanShare'
if ($RemoveData) {
    if (Test-Path -LiteralPath $dataDir) {
        Remove-Item -LiteralPath $dataDir -Recurse -Force
        Write-Host "已删除数据目录 $dataDir" -ForegroundColor Yellow
    }
} elseif (Test-Path -LiteralPath $dataDir) {
    Write-Host "数据保留在 $dataDir（彻底删除请加 -RemoveData）" -ForegroundColor Yellow
}

Write-Host '卸载完成。' -ForegroundColor Green