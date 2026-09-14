# DSH Desktop

把 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) 的本地 Web UI 装进一个原生
Windows 窗口：自动拉起并托管本地 Harness 服务、内嵌 WebView2 显示界面、集成系统托盘与单实例唤醒，
并自研了 ConPTY 终端、恢复助手与带备份回滚的更新流程。

技术栈：**C# / XAML · WPF + WinForms + WebView2 · .NET 10**。全部为**自持平台逻辑**，不打包
Chromium、不打包 Node；自有二进制体积约 **0.55 MB / 6 文件**（不含 WebView2 托管 dll）。

---

## 1. 当前状态：自用 / 实验项目，**尚未发布**

请先读这一节，避免误判本项目的成熟度。

| 维度 | 现状 |
|---|---|
| 发布状态 | **没有任何发行版**：无安装器、无版本线（版本号治理已落地，可注入） |
| 平台 | **仅 Windows x64**（依赖 WebView2 与 WinForms 托盘，未做跨平台抽象） |
| 分发方式 | **直接部署目录**（`dotnet publish` 产物 + 自带 `runtime\` 载荷），不是安装包 |
| 协作入口 | 本仓库为**本地检出**；已含 CI 工作流（push 后生效），不带远端 |
| 签名 | **仅自签**：签名与时间戳成立，但**不消除 SmartScreen 警告**（见 §3） |
| 定位 | 个人自用/实验；**不建议**在未审阅源码的情况下用于生产或分发 |

已知缺口（尚未实施）：安装器与差分更新、`SHA256SUMS`、安全模式、`DSHDesktop.Core` 抽离。
版本号治理与自签签名**已落地**（见 §3）。

---

## 2. 运行前提

| 依赖 | 说明 |
|---|---|
| Windows 10/11 x64 | 仅 x64 验证过 |
| [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) | WPF + WinForms；SDK 用于构建 |
| [WebView2 Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) | Win11 通常已内置；缺失时界面无法显示 |
| Node.js + pnpm | 宿主用于运行 `dsh web` 与插件市场安装；节点路径见 §7 已知问题 |

---

## 3. 构建与运行

```powershell
# 只构建壳（跳过把 Node/dsh 运行时拷进产物，日常开发用这条）
dotnet build DSHDesktop\DSHDesktop.csproj -p:SkipBundleRuntime=true -v minimal

# 构建并打包运行时载荷（产物落在 publish\，体积约 300 MB+）
dotnet publish DSHDesktop\DSHDesktop.csproj -c Release
```

运行：直接执行 `DSHDesktop\bin\...\DSHDesktop.exe`（或 `publish\DSHDesktop.exe`）。
首次启动会为当前 profile 做初始化（写入 `ProfileSeed` / 环境探测），随后拉起本地服务并内嵌界面。

> **不要用解决方案级命令**：`dotnet build DSH.slnx` / `dotnet restore DSH.slnx` 在当前状态下
> **静默失败**（0 错误 0 警告但 exit=1，属既存问题）。请始终按**单个 csproj** 构建与测试。

### 空值处理约定（`Nullable` 已全程开启）

构建必须保持**零警告**；`!`（null-forgiving）只在**能一句话说清依据**时使用，并在该行或上一行写明理由。

| 场景 | 约定 |
|---|---|
| 参数是**构造出来的路径**（`Path.Combine` 的结果、或经 `File.Exists` 校验的绝对文件路径） | 可用 `!` —— 它必然含父目录；须就近注明依据（例："由 `Path.Combine(root, asset)` 构造，必然含父目录"） |
| 值来自**外部/不可控来源**（`FindNode()`、`DshNodeBinJs()`、`Process.Start`、环境变量、配置） | **必须显式判空**并给出失败分支，不得用 `!` 糊掉 |
| 同一段推导在文件内多处出现 | 用**同一种**写法，避免同一件事两种风格（这是本仓库曾出现过的可读性问题） |

**不要用 `!` 消除告警**——它只压制编译器，不改变运行期行为；`Process.Start` 那类返回 null 的调用若被 `!` 掉，异常会冒到全局未处理出口（弹框 + `Shutdown(1)`）。

### 版本号治理

版本与产品元数据的**单一来源**是仓库根的 [`Directory.Build.targets`](Directory.Build.targets)，
默认 `1.0.0`（本地构建确定性，不依赖 git 或 CI）。发布时从外部注入：

```powershell
dotnet build DSHDesktop\DSHDesktop.csproj -c Release `
  -p:VersionPrefix=1.2.3 -p:BuildId=202609141245
```

| 注入项 | 影响 |
|---|---|
| `VersionPrefix` | 语义版本；`AssemblyVersion` 取前三段（如 `1.2.0.0`） |
| `BuildId` | `FileVersion` 第四段（如 `1.2.3.202609141245`）与 `InformationalVersion` |
| `VersionSuffix` | 预发布后缀（如 `rc.1`） |

实测：默认构建 → `FileVersion=1.0.0.0`；注入后 → `FileVersion=1.2.3.202609141245`；
两段式 `2.0` → `2.0.0.7`。`ProductVersion` 还会被 SDK 自动追加 git 短 SHA，便于按产物反查提交。

### 代码签名（自签）

```powershell
# 1) 一次性：创建自签证书（写入 .signing\，已被 .gitignore 排除）
powershell -ExecutionPolicy Bypass -File scripts/signing/New-DevSigningCert.ps1 -PfxPath .signing/dev.pfx

# 2) 签名产物
powershell -ExecutionPolicy Bypass -File scripts/signing/Sign-Artifacts.ps1 `
  -PfxPath .signing/dev.pfx -PfxPassword '<密码>' `
  -Path 'DSHDesktop\.build\DSHDesktop\Release\net10.0-windows' -IncludeManagedDlls
```

需要 `signtool`（来自 Windows SDK：`winget install --id Microsoft.WindowsSDK.10.0.26100 -e`）。
**自签的边界**：签名与 RFC3161 时间戳都成立，但**不会**消除 SmartScreen 警告，也不会让别的
机器默认信任——详见 [`scripts/signing/README.md`](scripts/signing/README.md)。

---

## 4. 测试

```powershell
dotnet test DSHDesktop.Tests\DSHDesktop.Tests.csproj
```

共 **60 个 `[Fact]` + 6 个 `[Theory]`（31 条 `[InlineData]`）= 91 个用例**，当前全绿。
测试工程用 `<Compile Include="..\DSHDesktop\...">` **链接同一份源码**（非副本）编译为纯 `net10.0`
程序集，因此 `TerminalScreen` / `DesktopLog` / `VersionUpdate` 的可测性是被持续验证的。

> 受限环境提示：测试宿主需要打开父进程句柄，在禁止进程句柄操作的沙箱下会以
> `Win32Exception (5): 拒绝访问` 中止——这是环境限制，不是测试问题。

---

## 5. 目录结构

```
DSHDesktop/            应用源码（WPF 壳）
  App.xaml(.cs)          启动、单实例互斥、全局异常出口
  MainWindow.xaml(.cs)   主窗口：服务托管、更新编排、托盘、诊断导出
  SingleInstanceIpc.cs   命名管道 + 命名事件双通道的「二次启动唤醒」
  DesktopRecovery.cs     last-known-good 快照的提交换入与回滚
  UpdateBackup.cs        更新前备份 / 失败回滚（窄覆盖，见其类文档）
  DesktopLog.cs          有界日志、脱敏、诊断包导出
  PnpmSupport.cs         pnpm 探测与垫片       MarketSupport.cs  插件市场重启策略
  VersionUpdate.cs       版本比较与更新探测     ProfileSeed.cs    profile 初始化
  ServerState.cs         端口归属与状态         TrayMenu.cs       托盘菜单
  Terminal/              自研 ConPTY 终端（会话 / 屏幕模型 / 渲染视图）
DSHDesktop.Tests/      测试工程（xunit，链接 DSHDesktop 源码）
DSH.slnx               解决方案文件（当前解决方案级命令不可用，见 §3）
Directory.Build.targets 版本号与产品元数据的单一来源
scripts/signing/       自签代码签名脚本与说明
.github/workflows/     CI（构建 + 测试 + 可选签名）
```

> 本仓库只包含**桌面壳本体**（源码、测试、构建与签名配置）。
> 下列目录**不在版本控制内**，仅存在于本地工作区：
> `docs/`（设计/审计台账）、`plugin/`（本机维护的插件与图标工具）、
> `.dsh/skills/`（本机配套技能）、`DSHDesktop/Properties/PublishProfiles/`（IDE 生成的本机发布配置）。

---

## 6. 主要组件一览

| 能力 | 实现 | 备注 |
|---|---|---|
| 单实例 | `App.xaml.cs` + `SingleInstanceIpc.cs` | 二次启动**唤醒**已有实例而非另开窗口 |
| 服务托管 | `MainWindow.xaml.cs` `StartAndEmbedCoreAsync` | 端口顺延、就绪判定、失败统一出口 |
| 终端 | `Terminal/ConPtySession.cs` + `TerminalScreen.cs` + `TerminalView.cs` | 自研，不依赖 `node-pty`；支持同步输出、备用屏、鼠标编码 |
| 更新 | `VersionUpdate.cs` + `UpdateBackup.cs` | 双 registry 探测 + 备份/回滚；**窄覆盖**见类文档 |
| 恢复 | `DesktopRecovery.cs` + 恢复助手 | 快照提交换入采用「改名中间态 → 移入 → 失败回滚」 |
| 日志 | `DesktopLog.cs` | 配额清理 + 脱敏（长 hex/base64 会被整体遮蔽）+ 诊断 zip |

---

## 7. 已知问题（诚实清单）

1. **硬编码绝对路径**：`MainWindow.xaml.cs` 与 `DSHDesktop.csproj` 内联了 `D:\nodejs` 与
   `npm-cache\_npx\<hash>`；换机器需自行调整或走候选链。
2. **无启动健康门**：拿到 URL 即提交 last-known-good，不等 `NavigationCompleted` 成功。
3. **解决方案级命令不可用**（见 §3）。
4. **诊断包不受日志配额管理**，且没有保留份数上限。
5. **`.bak-<时间戳>` 快照无上限无清理**。
6. `MainWindow.xaml.cs` 已超 2,300 行，属 god class，拆分计划（抽 `ServerHost` /
   `UpdateCoordinator` / `TrayController`）尚未启动。

---

## 8. 许可

本项目以 **MIT License** 发布，见 [LICENSE](LICENSE)；第三方组件与商标声明见 [NOTICE.md](NOTICE.md)。
DeepSeek Harness 上游代码未被修改，以固定版本原样运行，遵循其自身许可。
