<#
.SYNOPSIS
    用自签证书对已构建的产物做 Authenticode 签名，并输出签名结果。

.DESCRIPTION
    支持的输入二选一：
      -Thumbprint <指纹>     使用 CurrentUser\My 中的证书（私钥不落地）
      -PfxPath <路径>         使用 PFX + -PfxPassword（适合 CI；PFX 用完即删）

    签名使用 SHA256 摘要 + RFC3161 时间戳。**时间戳需要网络**；离线时用 -NoTimestamp。

    默认只签名主程序 DSHDesktop.exe。用 -IncludeManagedDlls 追加 DSHDesktop.dll；
    第三方 dll（WebView2 / 运行时）**不改动**。

.PARAMETER Path
    产物目录（如 Release\net10.0-windows 或 publish）。

.EXAMPLE
    pwsh scripts/signing/Sign-Artifacts.ps1 -Thumbprint 6F9915C9... -Path Release\net10.0-windows
.EXAMPLE
    pwsh scripts/signing/Sign-Artifacts.ps1 -PfxPath .signing/dev.pfx -PfxPassword 'x' -Path publish -IncludeManagedDlls
#>
[CmdletBinding(DefaultParameterSetName = 'Store')]
param(
    [Parameter(Mandatory = $true)]
    [string] $Path,

    [Parameter(Mandatory = $true, ParameterSetName = 'Store')]
    [string] $Thumbprint,

    [Parameter(Mandatory = $true, ParameterSetName = 'Pfx')]
    [string] $PfxPath,

    [Parameter(ParameterSetName = 'Pfx')]
    [string] $PfxPassword,

    [switch] $IncludeManagedDlls,
    [switch] $NoTimestamp,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [string] $SignToolPath
)

$ErrorActionPreference = 'Stop'

function Find-SignTool {
    param([string] $Explicit)
    if ($Explicit) {
        if (-not (Test-Path -LiteralPath $Explicit)) { throw "指定的 signtool 不存在: $Explicit" }
        return (Resolve-Path -LiteralPath $Explicit).Path
    }
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $roots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin",
        "${env:ProgramFiles(x86)}\Windows Kits\8.1\bin",
        # 本机实测：SDK 被安装到 E: 盘（注册表 InstallationFolder = E:\Windows Kits\10\）
        'E:\Windows Kits\10\bin',
        'D:\Windows Kits\10\bin'
    ) | Where-Object { $_ -and (Test-Path $_) }

    # 排除 ClickOnce 自带的旧版 signtool，优先用 SDK 里 x64 的那份
    $found = foreach ($root in $roots) {
        Get-ChildItem $root -Recurse -Filter signtool.exe -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' -and $_.FullName -notmatch 'ClickOnce' }
    }
    $found = @($found) | Sort-Object FullName -Descending
    if ($found.Count -eq 0) {
        throw "未找到 signtool.exe。请安装 Windows SDK（winget install Microsoft.WindowsSDK.10.0.26100）或用 -SignToolPath 指定。"
    }
    return $found[0].FullName
}

$signtool = Find-SignTool -Explicit $SignToolPath
Write-Host "signtool : $signtool" -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $Path)) { throw "产物路径不存在: $Path" }
$target = (Resolve-Path -LiteralPath $Path).Path

# 组装待签名清单
$files = @()
$exe = Join-Path $target 'DSHDesktop.exe'
if (Test-Path -LiteralPath $exe) { $files += Get-Item -LiteralPath $exe }
if ($IncludeManagedDlls) {
    $dll = Join-Path $target 'DSHDesktop.dll'
    if (Test-Path -LiteralPath $dll) { $files += Get-Item -LiteralPath $dll }
}
if ($files.Count -eq 0) { throw "在 $target 下没有找到 DSHDesktop.exe（是否需要先构建/发布？）" }

# 证书参数
$certArgs = @()
if ($PSCmdlet.ParameterSetName -eq 'Store') {
    $c = Get-ChildItem 'Cert:\CurrentUser\My' -CodeSigningCert -ErrorAction SilentlyContinue |
         Where-Object { $_.Thumbprint -eq $Thumbprint }
    if (-not $c) { throw "CurrentUser\My 中找不到指纹为 $Thumbprint 的代码签名证书" }
    $certArgs = @('/sha1', $Thumbprint)
    $subject = $c.Subject
} else {
    if (-not (Test-Path -LiteralPath $PfxPath)) { throw "PFX 不存在: $PfxPath" }
    $certArgs = @('/f', (Resolve-Path -LiteralPath $PfxPath).Path)
    if ($PfxPassword) { $certArgs += @('/p', $PfxPassword) }
    $subject = "(PFX) $PfxPath"
}

Write-Host "证书     : $subject" -ForegroundColor Cyan
Write-Host "待签名   : $($files.Count) 个文件" -ForegroundColor Cyan

$results = @()
foreach ($f in $files) {
    Write-Host ""
    Write-Host "==> 签名 $($f.Name)" -ForegroundColor White

    $args = @('sign', '/fd', 'SHA256', '/v') + $certArgs
    if (-not $NoTimestamp) { $args += @('/tr', $TimestampUrl, '/td', 'SHA256') }
    $args += $f.FullName

    & $signtool @args
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        throw "signtool 失败（退出码 $code）：$($f.FullName)。离线环境请加 -NoTimestamp 重试。"
    }

    $sig = Get-AuthenticodeSignature -LiteralPath $f.FullName
    $vi = (Get-Item -LiteralPath $f.FullName).VersionInfo
    $results += [pscustomobject]@{
        文件           = $f.Name
        签名状态       = $sig.Status
        签名者         = $sig.SignerCertificate.Subject
        时间戳         = if ($sig.TimeStamperCertificate) { '已签名' } else { '无' }
        FileVersion    = $vi.FileVersion
        ProductVersion = $vi.ProductVersion
    }
}

Write-Host ""
Write-Host "签名结果汇总：" -ForegroundColor Green
$results | Format-Table -AutoSize

Write-Host "说明：签名状态 Valid/UnknownError 反映的是**本机是否信任该证书**；" -ForegroundColor DarkGray
Write-Host "      自签证书未导入受信任存储时显示 UnknownError，但签名本身是有效的。" -ForegroundColor DarkGray
Write-Host "      导入信任：Import-Certificate -FilePath <cer> -CertStoreLocation Cert:\CurrentUser\TrustedPeople" -ForegroundColor DarkGray

return $results
