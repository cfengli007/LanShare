# 把自签名代码签名证书导入本机「受信任的根证书颁发机构」。
# ⚠️ 需要管理员权限；只应用于自己的开发机，不要批量部署到用户机器。
param(
    [string]$CerPath = (Join-Path $PSScriptRoot 'LanShare-CodeSign.cer')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $CerPath)) { throw ('证书不存在: ' + $CerPath + '，请先运行 New-CodeSigningCert.ps1') }

Import-Certificate -FilePath $CerPath -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Write-Host ('已导入受信任根: ' + $CerPath) -ForegroundColor Green
