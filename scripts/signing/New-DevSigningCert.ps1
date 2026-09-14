<#
.SYNOPSIS
    创建用于本地/CI 代码签名的自签证书，并导出 PFX 与 CER。

.DESCRIPTION
    自签证书只解决「产物确实有签名、可追溯、可被显式信任」这一层；它**不会**让
    Windows SmartScreen 停止警告，也不会让别的机器默认信任。要消除警告需要正式
    CA 证书，或让使用者手动把本证书导入「受信任的根/受信任的人」。

    证书写入 CurrentUser\My（便于 signtool 按指纹签名），并导出：
      - <name>.pfx  含私钥，**机密**（.gitignore 已排除 *.pfx 与 .signing/）
      - <name>.cer  仅公钥，可安全分发，供他人导入以信任签名

.PARAMETER Password
    PFX 密码（明文）。省略时脚本生成 32 字符随机密码并打印，请自行保存。
    刻意用明文而非 SecureString：SecureString 无法跨进程传递（`-File` 传参会退化成
    字符串而报类型转换错误），而 CI 中密码本来就来自环境变量/Secrets。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/signing/New-DevSigningCert.ps1 -OutDir .signing
#>
[CmdletBinding()]
param(
    [string] $Subject = 'CN=DSH Desktop Dev (self-signed)',
    [string] $PfxPath = '.signing/dsh-desktop-dev.pfx',
    [string] $Password,
    [int] $Years = 3,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$outDir = Split-Path -Parent $PfxPath
if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

if ((Test-Path $PfxPath) -and -not $Force) {
    throw "已存在 $PfxPath；如需覆盖请加 -Force（注意：旧证书签出的产物将无法由新证书验证）"
}

$plain = $Password
if (-not $plain) {
    $chars = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789'.ToCharArray()
    $buf = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($buf)
    $plain = -join ($buf | ForEach-Object { $chars[$_ % $chars.Length] })
}
# 显式类型标注（而非仅靠 ConvertTo-SecureString 的返回值推断）：
# 实测在 powershell.exe 5.1 子进程里，仅用赋值会让 -Password 仍以 String 传入而报
# ParameterBindingException，故在此钉死类型，并在调用处再做一次强制转换。
[securestring] $securePw = ConvertTo-SecureString -String $plain -AsPlainText -Force

Write-Host "创建自签代码签名证书：$Subject" -ForegroundColor Cyan

# -Type Custom + 显式 EKU 1.3.6.1.5.5.7.3.3（Code Signing），
# 比 -Type CodeSigningCert 更不容易在个别环境下缺失 EKU 而被 signtool 拒绝。
$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject $Subject `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyUsage DigitalSignature `
    -KeyUsageProperty Sign `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -NotAfter (Get-Date).AddYears($Years) `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')

$pfxFull = [IO.Path]::GetFullPath((Join-Path (Get-Location) $PfxPath))
$cerFull = [IO.Path]::ChangeExtension($pfxFull, '.cer')

Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $pfxFull -Password ([securestring]$securePw) | Out-Null
Export-Certificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $cerFull -Type CERT | Out-Null

Write-Host ""
Write-Host "证书已创建并导出：" -ForegroundColor Green
Write-Host ("  指纹 Thumbprint : {0}" -f $cert.Thumbprint)
Write-Host ("  Subject         : {0}" -f $cert.Subject)
Write-Host ("  有效期至        : {0:yyyy-MM-dd}（{1} 年）" -f $cert.NotAfter, $Years)
Write-Host ("  EKU             : {0}" -f (($cert.EnhancedKeyUsageList | ForEach-Object { $_.FriendlyName }) -join ', '))
Write-Host ("  PFX（含私钥）   : {0}" -f $pfxFull)
Write-Host ("  CER（公钥）     : {0}" -f $cerFull)
if ($plain) {
    Write-Host ""
    Write-Host ("  PFX 密码（仅本次显示，请立即保存）: {0}" -f $plain) -ForegroundColor Yellow
}
Write-Host ""
Write-Host "下一步：" -ForegroundColor Yellow
Write-Host "  1) 签名  : pwsh scripts/signing/Sign-Artifacts.ps1 -PfxPath <pfx> -PfxPassword <pw> -Path <产物目录>"
Write-Host "  2) 本机信任（消除「未知发布者」）："
Write-Host "     Import-Certificate -FilePath <cer> -CertStoreLocation Cert:\CurrentUser\TrustedPeople"
Write-Host "  3) CI 签名：PFX 与密码存为 GitHub Secrets，见 scripts/signing/README.md"
Write-Host ""
Write-Host "!! PFX 与密码是机密，切勿提交入库 !!" -ForegroundColor Red
