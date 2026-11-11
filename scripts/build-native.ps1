#requires -Version 5.1
<#
.SYNOPSIS
  编译 Windows 11 资源管理器右键菜单 COM 组件 LanShare.Shell.dll。
.DESCRIPTION
  通过 vswhere 定位 Visual Studio，调用 cl.exe（/utf-8，避免中文在 CP936 下被误解析）。
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$native = Join-Path $root 'native\LanShare.Shell'
if (-not (Test-Path (Join-Path $native 'explorer_command.cpp'))) {
    throw "找不到原生源码：$native"
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    $vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
}
if (-not (Test-Path $vswhere)) { throw '找不到 vswhere.exe，请安装 Visual Studio（含 C++ 桌面开发工作负载）。' }

$vsdev = & $vswhere -latest -products * -find 'Common7\Tools\VsDevCmd.bat' | Select-Object -First 1
if (-not $vsdev) { throw '找不到 VsDevCmd.bat，请安装 Visual Studio 的“使用 C++ 的桌面开发”工作负载。' }

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $native 'build' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = Join-Path $OutputDirectory 'LanShare.Shell.dll'

$optimization = if ($Configuration -eq 'Debug') { '/Od /Zi' } else { '/O2' }
Push-Location $native
try {
    $command = 'call "' + $vsdev + '" -arch=x64 >nul && cl /nologo /utf-8 /LD /MT /EHsc ' + $optimization +
        ' /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 explorer_command.cpp /link /DEF:LanShare.Shell.def' +
        ' /OUT:"' + $output + '" ole32.lib shell32.lib shlwapi.lib'
    cmd /c $command
    if ($LASTEXITCODE -ne 0) { throw "编译 LanShare.Shell.dll 失败（exit $LASTEXITCODE）。" }
}
finally {
    Pop-Location
}

if (-not (Test-Path $output)) { throw "编译完成但找不到输出：$output" }
Write-Host "LanShare.Shell.dll -> $output" -ForegroundColor Green
