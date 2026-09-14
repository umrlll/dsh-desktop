# 代码签名（自签）

本目录提供**自签**代码签名的完整链路：生成证书 → 签名产物 → 验证。CI 侧见
[`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) 的 `sign` 步骤。

> ⚠️ **先清楚自签能做到什么、做不到什么**
>
> | 能做到 | 做不到 |
> |---|---|
> | 产物带 Authenticode 签名，可被 `signtool verify` 解析 | ❌ **消除 SmartScreen「未知发布者」警告** |
> | 签名带 RFC3161 时间戳，**证书过期后签名依然有效** | ❌ 让别人的机器默认信任（除非对方导入本证书） |
> | 可追溯「哪个构建、哪个版本」（FileVersion / ProductVersion） | ❌ 替代正式 CA 证书（DigiCert / Sectigo 等）的信任链 |
>
> 本项目当前状态：**仅自签**。要消除警告需要购买正式 CA 的代码签名证书。

## 1. 本机一次性设置

```powershell
# 创建自签证书（写入 CurrentUser\My，并导出 .signing\dev.pfx + .signing\dev.cer）
powershell -ExecutionPolicy Bypass -File scripts/signing/New-DevSigningCert.ps1 `
    -PfxPath .signing/dev.pfx -Years 3
# 脚本会打印 PFX 密码；请立即保存到密码管理器
```

`.signing/` 已在 `.gitignore` 中，**PFX 与密码绝不入库**。

## 2. 签名产物

```powershell
# PFX 模式（适合 CI 与首次验证）
powershell -ExecutionPolicy Bypass -File scripts/signing/Sign-Artifacts.ps1 `
    -PfxPath .signing/dev.pfx -PfxPassword '<密码>' `
    -Path 'DSHDesktop\.build\DSHDesktop\Release\net10.0-windows' -IncludeManagedDlls

# 或按指纹用证书存储（私钥不落地，适合长期开发机）
powershell -ExecutionPolicy Bypass -File scripts/signing/Sign-Artifacts.ps1 `
    -Thumbprint <指纹> -Path <产物目录>
```

参数说明：

| 参数 | 作用 |
|---|---|
| `-IncludeManagedDlls` | 追加签名 `DSHDesktop.dll`；**不加则只签主程序 exe** |
| `-NoTimestamp` | 跳过 RFC3161 时间戳（离线环境用；此时签名会随证书过期而失效） |
| `-TimestampUrl` | 默认 `http://timestamp.digicert.com`，可换 sectigo/globalsign |
| `-SignToolPath` | 显式指定 signtool；脚本默认自动搜索 SDK 目录（含非系统盘的 `E:\Windows Kits`） |

**signtool 前置条件**：来自 Windows SDK。

```powershell
winget install --id Microsoft.WindowsSDK.10.0.26100 -e
```

## 3. 验证签名（实测结果）

```powershell
Get-AuthenticodeSignature <产物>          # 看 Status / SignerCertificate / TimeStamperCertificate
signtool verify /pa /v <产物>             # 严格验证
```

**本项目实测（2026-09-14，Windows 11 26200）**：

| 检查项 | 结果 |
|---|---|
| `signtool sign /fd SHA256` | ✅ `Successfully signed`（exe 与 dll 均通过） |
| 时间戳 | ✅ `CN=DigiCert SHA256 RSA4096 Timestamp Responder 2026 1` |
| `Get-AuthenticodeSignature` | `Status = UnknownError`，`SignerCertificate` 为本证书 |
| `signtool verify /pa` | ❌ `A certificate chain processed, but terminated in a root certificate which is not trusted` |
| 把证书导入 `CurrentUser\TrustedPeople` 后 | ⚠️ **仍为 `UnknownError`** —— 自签根未被信任，仅此一步不够 |

⇒ **结论**：签名与时间戳都成立（可用 `signtool verify` 看到签名者与时间戳），但要让
Windows 报 `Valid`，必须让自签根被信任（把 `.cer` 装进「受信任的根证书颁发机构」），
或改用正式 CA 证书。**自签不解决 SmartScreen 警告**。

## 4. CI 用法（GitHub Secrets）

在仓库 Settings → Secrets and variables → Actions 添加：

| Secret | 内容 |
|---|---|
| `SIGNING_PFX_BASE64` | `[Convert]::ToBase64String([IO.File]::ReadAllBytes('.signing/dev.pfx'))` 的结果 |
| `SIGNING_PFX_PASSWORD` | 上面的 PFX 密码 |

未配置这两个 Secret 时，CI 的签名步骤会**自动跳过**（不会失败），产物保持未签名。

生成 base64 的命令：

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes('.signing/dev.pfx')) | Set-Clipboard
```

## 5. 脚本文件的编码约定（重要）

本目录的 `.ps1` **必须保存为 UTF-8 with BOM**。原因：Windows PowerShell 5.1 在无 BOM
时按系统 ANSI 代码页读取脚本，脚本里的中文注释会被解成乱码并导致解析失败。CI 里有一步
`guard-ps1-bom` 会强制检查这件事。若你用编辑器改过脚本后 CI 报 BOM 缺失，重跑：

```powershell
# 给 scripts\**\*.ps1 补 BOM
$bom = [byte[]](0xEF,0xBB,0xBF)
Get-ChildItem scripts -Recurse -Filter *.ps1 | ForEach-Object {
    $b = [IO.File]::ReadAllBytes($_.FullName)
    if (-not ($b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)) {
        [IO.File]::WriteAllBytes($_.FullName, ($bom + $b))
    }
}
```
