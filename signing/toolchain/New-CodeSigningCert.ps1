# 生成自签名代码签名证书，导出 .pfx（含私钥）与 .cer（公钥）。
#
# 用途：给 LanShare.exe 与安装包签名，减少 Windows SmartScreen / 国产安全软件的拦截。
# ⚠️ 自签名证书只对本机有效（需导入「受信任的根证书颁发机构」后才不提示未知发布者）；
#    要分发给别人请购买正式代码签名证书，或用 SignPath 之类的免费开源方案。
# ⚠️ .pfx 含私钥，已在 .gitignore 排除，绝不要提交。
param(
    [string]$Subject = 'CN=LanShare Self-Signed Code Signing',
    [string]$Password = '123456',
    [string]$OutDir = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
$pfxPath = Join-Path $OutDir 'LanShare-CodeSign.pfx'
$cerPath = Join-Path $OutDir 'LanShare-CodeSign.cer'
$pfxPassword = ConvertTo-SecureString -String $Password -Force -AsPlainText

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $Subject } | Select-Object -First 1

if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject `
        -FriendlyName 'LanShare Code Signing' -KeyExportPolicy Exportable `
        -KeyAlgorithm RSA -KeyLength 3072 -NotAfter (Get-Date).AddYears(10) `
        -CertStoreLocation Cert:\CurrentUser\My
    Write-Host ('CREATED ' + $cert.Thumbprint)
} else {
    Write-Host ('REUSED ' + $cert.Thumbprint)
}

if (-not (Test-Path -LiteralPath $pfxPath)) {
    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $pfxPassword | Out-Null
    Write-Host ('PFX_EXPORTED ' + $pfxPath)
} else {
    Write-Host ('PFX_EXISTS ' + $pfxPath)
}

Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null
Write-Host ('CER_EXPORTED ' + $cerPath)
Write-Host ('THUMBPRINT=' + $cert.Thumbprint)
Write-Host ('NOT_AFTER=' + $cert.NotAfter)
Write-Host ''
Write-Host '下一步：导入受信任根（需管理员）后签名才不提示未知发布者：' -ForegroundColor Cyan
Write-Host '  Start-Process powershell -Verb RunAs -ArgumentList ''-File'', "' + $PSScriptRoot + '\Trust-Root.ps1"'
