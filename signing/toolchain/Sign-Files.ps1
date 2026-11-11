# 用本机的 LanShare 自签名证书给指定文件签名。
# 用法： .\Sign-Files.ps1 -Path ..\..\dist\LanShare-Setup-1.1.0-win-x64.exe
param(
    [Parameter(Mandatory=$true)][string[]]$Path,
    [string]$Subject = 'CN=LanShare Self-Signed Code Signing'
)

$ErrorActionPreference = 'Stop'
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $Subject } | Select-Object -First 1
if (-not $cert) { throw '代码签名证书不存在，请先运行 New-CodeSigningCert.ps1' }

foreach ($f in $Path) {
    if (-not (Test-Path -LiteralPath $f)) { Write-Warning ('跳过不存在: ' + $f); continue }
    Set-AuthenticodeSignature -FilePath $f -Certificate $cert | Out-Null
    $sig = Get-AuthenticodeSignature $f
    if ($sig.Status -ne 'Valid') { throw ('签名失败 ' + $f + ' 状态 ' + $sig.Status) }
    Write-Host ('SIGNED ' + $f + ' -> ' + $sig.Status) -ForegroundColor Green
}
