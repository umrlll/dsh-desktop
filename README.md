# DSH Desktop

把 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) 的本地 Web UI 装进一个原生
Windows 窗口：自动拉起并托管本地 Harness 服务、内嵌 WebView2 显示界面、集成系统托盘与单实例唤醒，
并自研了 ConPTY 终端、恢复助手与带备份回滚的更新流程。

技术栈：**C# / XAML · WPF + WinForms + WebView2 · .NET 10**。全部为**自持平台逻辑**，不另带
Chromium；桌面壳自身较小，完整发布载荷会另外携带锁定的 Node/DSH 运行时。

---

## 1. 当前状态：自用 / 实验项目，**尚未发布**

请先读这一节，避免误判本项目的成熟度。

| 维度 | 现状 |
|---|---|
| 发布状态 | **没有任何发行版**：无安装器；版本号治理已落地，但尚未建立正式发布通道 |
| 平台 | **仅 Windows x64**（依赖 WebView2 与 WinForms 托盘，未做跨平台抽象） |
| 分发方式 | 完整发布设计为 **Portable 目录/ZIP + 自带 `runtime\` 载荷**；当前 CI 只构筑桌面壳，不是可安装包 |
| 协作入口 | GitHub 仓库已启用 Windows CI：构建、测试、shell-only publish，以及配置证书后的可选签名 |
| 签名 | **仅自签**：签名与时间戳成立，但**不消除 SmartScreen 警告**（见 §3） |
| 定位 | 个人自用/实验；**不建议**在未审阅源码的情况下用于生产或分发 |

已知缺口（尚未实施）：完整 CI 运行时输入、Portable ZIP、安装器、可信发行签名、SBOM 和 Desktop 专用宿主适配。CI 已在可选签名后为 shell 发布目录生成 `SHA256SUMS.txt`；可安装发布物仍待安装器与锁定 runtime 输入。不可变运行时槽、失败回退、版本号治理、兼容矩阵与自签流程
**已经落地**（见 §3、§6、§7）。

---

## 2. 运行前提

| 依赖 | 说明 |
|---|---|
| Windows 10/11 x64 | 仅 x64 验证过 |
| [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) | WPF + WinForms；SDK 用于构建 |
| [WebView2 Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) | Win11 通常已内置；缺失时界面无法显示 |
| Node.js + pnpm | 宿主用于运行 `dsh web` 与插件市场安装；正式发行将锁定并随 Portable/安装器提供 |

---

## 3. 构建与运行

```powershell
# 只构建壳（跳过把 Node/dsh 运行时拷进产物，日常开发用这条）
dotnet build src\DSHDesktop\DSHDesktop.csproj -p:SkipBundleRuntime=true -v minimal

# 只发布桌面壳（CI 当前使用此模式；不冒充完整发行包）
dotnet publish src\DSHDesktop\DSHDesktop.csproj -c Release -p:SkipBundleRuntime=true

# 完整运行时发布必须显式提供锁定来源与精确版本；以下变量应指向已准备的离线输入
dotnet publish src\DSHDesktop\DSHDesktop.csproj -c Release `
  -p:BundleNodeSource="$nodeExe" -p:BundleNodeVersion=24.20.0 `
  -p:BundleDshSource="$dshNodeModules" -p:BundleDshVersion=0.1.5-alpha.1 `
  -p:BundlePnpmSource="$pnpmPrefix" -p:BundlePnpmVersion=11.27.0
```

> **当前 CI 产物不能直接安装，也不是完整 Portable 版本。** CI 使用
> `SkipBundleRuntime=true`，没有携带 Node、DSH 和 pnpm；而且只有配置签名 Secrets 时才执行上传，
> 当前上传清单也仅含 `DSHDesktop.exe` 与 `DSHDesktop.dll`，不足以代表完整 `publish` 目录。
> CI 在这一阶段承担的是编译、测试和发布规则验证，不应把其 artifact 当成正式发行包。

未传 `SkipBundleRuntime=true` 时，缺少任一来源或精确版本会在 `Publish` 前直接失败。完整发布每次
从零建立 `obj\bundle-runtime\<Configuration>`，生成并自校验 manifest，再发布到
`runtime\versions\<runtime-id>` 并原子写入 `runtime\active.json`；
不会读取 `D:\nodejs`、随机 npx cache 或源码树下残留的本机 `runtime\`。

运行：直接执行 `.build\DSHDesktop\...\DSHDesktop.exe`（或 `publish\DSHDesktop.exe`）。
首次启动会由 `ProfileManager` 初始化当前 profile 并完成环境探测，随后拉起本地服务并内嵌界面。
在没有既有实例时，使用 `DSHDesktop.exe --safe-mode` 可直接以隔离的 `desktop-safe` profile 启动；
已有实例可在窗口的重启菜单
中切换；安全 profile 首次只从 DSH 自带 `web` 模板初始化，不复制正常 profile 的插件，并使用
独立 `.dsh-desktop-safe` home，避免普通 home 的 patch 重新引入故障插件。
`DSHDesktop.exe --migrate-profile` 会把现有 `web` profile 的可移植配置原子复制到预备的
`desktop` 目录，永不覆盖目标或删除源数据；由于当前 DSH CLI 保留该名称给官方 Electron 宿主，
本地版本在专用宿主适配完成前仍以 `web` 作为正常启动 profile。

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
dotnet build src\DSHDesktop\DSHDesktop.csproj -c Release `
  -p:VersionPrefix=1.2.3 -p:BuildId=202609141245
```

| 注入项 | 影响 |
|---|---|
| `VersionPrefix` | 语义版本；`AssemblyVersion` 取前三段（如 `1.2.0.0`） |
| `BuildId` | `FileVersion` 第四段（如 `1.2.3.202609141245`）与 `InformationalVersion` |
| `VersionSuffix` | 预发布后缀（如 `rc.1`） |

实测：默认构建 → `FileVersion=1.0.0.0`；注入后 → `FileVersion=1.2.3.202609141245`；
两段式 `2.0` → `2.0.0.7`。`ProductVersion` 还会被 SDK 自动追加 git 短 SHA，便于按产物反查提交。

### 发行组合契约

[`eng/compatibility.json`](eng/compatibility.json) 是 Desktop/DSH/Node/pnpm/platform/profile schema
组合的机器可读登记表，并由 [`eng/compatibility.schema.json`](eng/compatibility.schema.json) 与 Core
校验器共同约束。只有精确锁定全部组件、完成真实 DSH 启动证据并标记为 `verified` 的组合才能
设为 `publishable`；当前本机组合仅登记为不可发布的 `dev/development`。

### 代码签名（自签）

```powershell
# 1) 一次性：创建自签证书（写入 .signing\，已被 .gitignore 排除）
powershell -ExecutionPolicy Bypass -File scripts/signing/New-DevSigningCert.ps1 -PfxPath .signing/dev.pfx

# 2) 签名产物
powershell -ExecutionPolicy Bypass -File scripts/signing/Sign-Artifacts.ps1 `
  -PfxPath .signing/dev.pfx -PfxPassword '<密码>' `
  -Path '.build\DSHDesktop\Release\net10.0-windows' -IncludeManagedDlls
```

需要 `signtool`（来自 Windows SDK：`winget install --id Microsoft.WindowsSDK.10.0.26100 -e`）。
**自签的边界**：签名与 RFC3161 时间戳都成立，但**不会**消除 SmartScreen 警告，也不会让别的
机器默认信任——详见 [`scripts/signing/README.md`](scripts/signing/README.md)。

---

## 4. 测试

```powershell
dotnet test tests\DSHDesktop.Tests\DSHDesktop.Tests.csproj
```

共 **192 个 `[Fact]` + 20 个 `[Theory]`（97 条 `[InlineData]` + 6 条 MemberData）= 295 个用例**，当前全绿。
测试工程通过项目引用验证纯 `net10.0` 的 `DSHDesktop.Core`；仍属于 Windows 壳、但只依赖 BCL 的
`TerminalScreen` / `DesktopLog` / `VersionUpdate` / WebView 安全与启动健康策略继续用源码链接测试。

Windows + WebView2 真实渲染冒烟（需要交互式桌面会话）：

```powershell
dotnet run --project tests\DSHDesktop.WebViewSmoke\DSHDesktop.WebViewSmoke.csproj -c Release
```

> 受限环境提示：测试宿主需要打开父进程句柄，在禁止进程句柄操作的沙箱下会以
> `Win32Exception (5): 拒绝访问` 中止——这是环境限制，不是测试问题。

---

## 5. 目录结构

```
src/                    产品源码
  DSHDesktop/             WPF + WebView2 桌面壳
    App.xaml(.cs)            启动、单实例互斥、全局异常出口
    MainWindow.xaml(.cs)     主窗口组合根：标题栏、WebView、TUI 与用户动作接线
    SingleInstanceIpc.cs     命名管道 + 命名事件双通道的「二次启动唤醒」
    DesktopRecovery.cs       last-known-good 快照的提交换入与回滚
    DesktopLog.cs            有界日志、脱敏、诊断包导出
    PnpmSupport.cs           pnpm 探测与垫片；MarketSupport.cs 管插件市场重启策略
    TrayController.cs        托盘图标、菜单命令、窗口显隐状态与资源释放
    Terminal/                自研 ConPTY 终端（会话 / 屏幕模型 / 渲染视图）
  DSHDesktop.Core/        纯 net10.0 核心（服务、运行时槽/profile、更新/恢复状态机）
tests/                  自动化验证
  DSHDesktop.Tests/       xUnit 单元与结构测试
  DSHDesktop.WebViewSmoke/ 真实 WebView2 导航与交互面冒烟宿主
tools/
  DSHDesktop.RuntimeTool/ manifest 生成/复验、运行时槽激活与发行 SHA256SUMS 工具
eng/                    发布兼容矩阵及其 JSON Schema
scripts/signing/        自签代码签名脚本与说明
.github/workflows/      CI（构建 + 测试 + 可选签名）
DSH.slnx               按 src/tests/tools 分组的解决方案文件
Directory.Build.targets 版本号与产品元数据的单一来源
```

> 本仓库只包含**桌面壳本体**（源码、测试、构建与签名配置）。
> 下列目录**不在版本控制内**，仅存在于本地工作区：
> `docs/`（设计/审计台账）、`plugin/`（本机维护的插件与图标工具）、
> `.dsh/skills/`（本机配套技能）、`src/DSHDesktop/Properties/PublishProfiles/`（IDE 生成的本机发布配置）。

---

## 6. 主要组件一览

| 能力 | 实现 | 备注 |
|---|---|---|
| 单实例 | `App.xaml.cs` + `SingleInstanceIpc.cs` | 二次启动**唤醒**已有实例而非另开窗口 |
| 服务托管 | `src/DSHDesktop.Core/ServerHost.cs` + `MainWindow.xaml.cs` | 动态端口、进程生命周期、完整健康门与失败统一出口 |
| 运行时槽 | Core `RuntimeManifest` + `RuntimeSlotManager` + RuntimeTool | 精确版本、逐文件 SHA-256、原子活动指针、一个已验证回退槽与保守清理模型 |
| 终端 | `Terminal/ConPtySession.cs` + `TerminalScreen.cs` + `TerminalView.cs` | 自研，不依赖 `node-pty`；支持同步输出、备用屏、鼠标编码 |
| 更新 | Core `UpdateCoordinator` + `RuntimeUpdateStager` + `VersionUpdate.cs` | 新槽 staging、manifest 复验、前端健康门、原子切换/回退、失败候选隔离与过期 staging 清理 |
| 恢复 | Core `RecoveryCoordinator` + `DesktopRecovery.cs` + 恢复助手 | Core 管自动重启/人工接管策略；壳层管理快照、插件禁用与回滚 |
| 托盘 | `TrayController.cs` + `TrayMenu.cs` | 控制器管理显隐状态、动态菜单、图标句柄和确定性释放 |
| 日志 | `DesktopLog.cs` | 配额清理 + 脱敏（长 hex/base64 会被整体遮蔽）+ 诊断 zip |

---

## 7. CI 产物与发布实施顺序

补齐可安装版本不会改变既定架构，也不会打乱 M3 的运行时治理计划。发布链按以下门禁顺序推进，
每一步通过后才进入下一步：

| 阶段 | 状态 | 实施内容 | 通过标准 |
|---|---|---|---|
| CI 验证基线 | 已有 | Windows 构建、295 个测试、shell-only publish、可选自签 | 主分支构建与测试全绿 |
| M3 收口 | 进行中 | Desktop 专用宿主适配；把 staging 对系统 npm 的依赖改为锁定下载器；已验签下载、安全原子解包、manifest 身份复验和候选槽接纳已形成单一更新入口，待配置发布信任根、端点和 UI 接线 | 独立 profile、更新、健康失败回退均可验证 |
| M5-A Portable | 待实施 | CI 获取经过兼容矩阵批准的 Node/DSH/pnpm 输入；生成 `win-x64` 完整发布目录和 Portable ZIP；上传整个载荷 | 干净 Windows 10/11 解压即可首次启动，不读取构建机路径 |
| M5-B 安装器 | 待实施 | 首选 Inno Setup 生成按用户安装的 `Setup.exe`；统一 Desktop、runtime manifest 与安装器版本身份 | 静默安装、覆盖升级、失败回退、卸载全绿；默认保留用户数据 |
| M5-C 正式发布 | 待实施 | Authenticode 可信签名、RFC3161 时间戳、`SHA256SUMS.txt`、SBOM、NOTICE 与发行说明 | 所有发布资产可验证，安装态 WebView2/后端健康冒烟通过 |

发布门的简化顺序为：

```text
锁定并验证运行时 → 完整 Portable ZIP → 干净系统启动验证
                    → 安装器 → 安装/升级/回退/卸载验证 → 正式发布
```

不在 Portable 完整性验证之前封装安装器，避免把缺失运行时或不可复现输入隐藏在 `Setup.exe` 中。
ConPTY、WPF/WebView2 壳、不可变运行时槽和 Core 状态机保持不变。Avalonia 仍属于 Windows 稳定版之后、
且 macOS/Linux 需求达到明确阈值时的独立评估项；即使未来迁移，新壳也应复用 Core、运行时 manifest、
兼容矩阵和发布门禁，不回退已经建立的可靠性能力。

---

## 8. 已知问题（诚实清单）

1. **CI 尚未配置锁定运行时来源**：流水线当前只发布 `SkipBundleRuntime=true` 的壳；完整发布门已经
   fail-closed，但仍需由可复现下载/仓库缓存提供 Node、DSH 与 pnpm 输入。
2. **安装态健康门尚未进入 CI**：单元测试与独立真实 WebView2 冒烟已通过，仍缺安装器中的完整冒烟。
3. **解决方案级命令不可用**（见 §3）。
4. **诊断包不受日志配额管理**，且没有保留份数上限。
5. **`.bak-<时间戳>` 快照无上限无清理**。
6. `MainWindow.xaml.cs` 仍然偏大；M2 协调器与 `TrayController` 已抽离，但标题栏弹层、
   WebView 事件适配、TUI 动作和更新流程的壳层副作用仍集中在窗口文件中。
7. 更新已不再修改活动槽，但 staging 安装目前仍依赖系统 npm；正式发行还需把包下载器纳入锁定运行时，
   并配置发布信任根、签名元数据来源和经验证载荷的解包器。Core 已具备 ECDSA 元数据验签、
   有界流式下载与载荷 SHA-256 校验，
   但没有受信任密钥与正式下载源时不会宣称 stable 更新通道可用。

---

## 9. 许可

本项目以 **MIT License** 发布，见 [LICENSE](LICENSE)；第三方组件与商标声明见 [NOTICE.md](NOTICE.md)。
DeepSeek Harness 上游代码未被修改，以固定版本原样运行，遵循其自身许可。
