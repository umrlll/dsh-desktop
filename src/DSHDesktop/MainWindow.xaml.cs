using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DSHDesktop.Core;
using Microsoft.Web.WebView2.Core;

namespace DSHDesktop;

public partial class MainWindow : Window
{
    private static readonly IRuntimeManager Runtime = DSHDesktop.Core.RuntimeManager.Default;
    private static readonly IProfileManager Profiles = ProfileManager.Default;
    private readonly IServerHost _serverHost = new ServerHost();
    private readonly IUpdateCoordinator _updates = new UpdateCoordinator();
    private readonly ReleaseSkipStore _releaseSkips = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DSHDesktop",
        "state"));
    private readonly IRecoveryCoordinator _recovery = new RecoveryCoordinator(maximumAutomaticRestarts: 3);
    private readonly TrayController _trayController;
    private int _port;
    private string? _authUrl;
    private Uri? _trustedWebOrigin;
    private ulong? _pendingTrustedNavigationId;
    private ulong? _policyCancelledNavigationId;
    private bool _healthyNavigationCommitted;
    private System.Threading.CancellationTokenSource? _frontendHealthCts;
    private System.Threading.CancellationTokenSource? _releaseUpdateCts;
    private string? _pendingUpdateRuntimeId;
    /// <summary>
    /// 当前进行中的标题栏动作（null = 空闲）："check" / "update" / "restart" / "recover"。
    /// 更新自身的并发状态由 <see cref="_updates"/> 管理；此字段额外协调重启/恢复等 UI 动作，
    /// 避免「更新切换运行时槽的同时用户点重启」这类叠加态（R16）。
    /// </summary>
    private string? _busyMode;

    private bool _tuiStarted;
    private string? _tuiTitle;

    /// <summary>最近的 dsh stderr 行（有上限）。失败时把这段上下文一并写进日志，省掉"再复现一次"。</summary>
    private readonly System.Collections.Generic.Queue<string> _recentStderr = new();

    private const int RecentStderrLines = 20;

    private static readonly Regex DshWebUrlPattern =
        new(@"dsh web:\s*(https?://\S+)", RegexOptions.Compiled);

    /// <summary>启动阶段产生的环境提示，就绪后补进状态栏。</summary>
    private string? _hostNotice;

    /// <summary>
    /// 状态栏当前完整文本（可含多行）。状态栏本身只显示压平后的单行，
    /// 全文留给 ToolTip 与「详情」浮层——那条 pnpm/IPv6 回退说明只在就绪时出现一次，
    /// 被 CharacterEllipsis 截断后就再也没有第二条路径能读到。
    /// </summary>
    private string? _statusFullText;

    /// <summary>pnpm 可用性修复的后台预热任务（只为"不重复起"而持有，结果走日志）。</summary>
    private Task? _pnpmWarmup;

    public MainWindow()
    {
        _trayController = new TrayController(this, new TrayCommands
        {
            IsServerRunning = () => _serverHost.IsRunning,
            CurrentAuthUrl = () => _authUrl,
            StopServerOnExit = () => StopOnClose.IsChecked == true,
            ToggleServer = () => OnToggleServer(this, new RoutedEventArgs()),
            RestartServer = () => OnMenuRestartDesktop(this, new RoutedEventArgs()),
            ReloadRenderer = () => OnMenuReloadRenderer(this, new RoutedEventArgs()),
            OpenExternal = () => OnOpenExternal(this, new RoutedEventArgs()),
            CopyAuthUrl = CopyAuthUrlToClipboard,
            CheckUpdate = () => OnCheckUpdate(this, new RoutedEventArgs()),
            OpenLogs = OpenLogDirectory,
            ExportDiagnostics = () => OnExportDiagnostics(this, new RoutedEventArgs()),
            Exit = Close,
            ClosePopups = () => CloseAllTitlebarPopups(),
            FocusContent = FocusTrayRestoredContent,
            SetStatus = SetStatus,
        });
        InitializeComponent();
        UpdateSafeModeMenu();
        _serverHost.OutputReceived += OnServerOutputReceived;
        _serverHost.Exited += OnServerHostExited;
        Title = "DSH Desktop";
    }

    // ---------- Win11 圆角（与参考图一致） ----------

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch { /* 非 Win11 或系统不支持时忽略 */ }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // 注册第二实例唤醒入口（优化清单 B32）。此前到达的请求由协调器挂起，注册后立即补做。
        ActivationCoordinator.SetHandler(
            () => Dispatcher.BeginInvoke(new Action(ActivateFromSecondInstance)));
        if (Environment.GetEnvironmentVariable("DSHDESKTOP_TUI_AUTOSTART") == "1")
            // 蓄意不等待：窗口已在 Loaded，TUI 由 Dispatcher 在空闲优先级上补显示；
            // 用 `_ =` 显式表达"丢弃返回值"的意图，而不是漏写 await。
            _ = Dispatcher.BeginInvoke(new Action(ShowTui), System.Windows.Threading.DispatcherPriority.Loaded);
        ShowCurrentVersion();   // 标题栏版本先显示，WebView2 初始化失败也不留 "dsh v--"
        _ = TuiPrepTask;        // 预热：npm 全局 bin 目录 + 镜像源探测
        try
        {
            await Web.EnsureCoreWebView2Async(null);
            Web.CoreWebView2.ProcessFailed += OnWebProcessFailed;
            Web.CoreWebView2.NavigationStarting += OnNavigationStarting;
            Web.CoreWebView2.FrameNavigationStarting += OnFrameNavigationStarting;
            Web.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            Web.CoreWebView2.DownloadStarting += OnDownloadStarting;
            Web.CoreWebView2.PermissionRequested += OnPermissionRequested;
            Web.NavigationCompleted += OnNavigationCompleted;
            Web.CoreWebView2.Settings.AreDevToolsEnabled =
                Environment.GetEnvironmentVariable("DSHDESKTOP_ENABLE_DEVTOOLS") == "1"
#if DEBUG
                || true
#endif
                ;
            Web.PreviewMouseDown += (_, __) => ForceFocusIntoWeb();
            await StartAndEmbedAsync();
            ShowCurrentVersion();
            _ = CheckForUpdatesAsync(); // 自动检测新版本（后台进行，不阻塞界面）
            FocusWindowAndWeb();
        }
        catch (Exception ex)
        {
            SetStatus("初始化失败: " + ex.Message);
        }
    }

    private static bool PortInUse(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return false;
        }
        catch { return true; }
    }

    // ---------- 启动并内嵌 ----------

    /// <summary>
    /// 启动/重启 dsh 服务的**唯一对外入口**。它只做一件事：把内核里任何未预期的异常
    /// 收敛为「日志 + 状态栏 + 标题栏错误行」。理由——<see cref="StartAndEmbedCoreAsync"/>
    /// 的准备段（铺种子、探测工具链、写市场策略、拍快照）在改动前没有 try 兜底，一旦抛出就会
    /// 冒到 <c>App.xaml.cs</c> 的 <c>DispatcherUnhandledException</c>，而那里是「弹框 + Shutdown(1)」：
    /// 于是「一次启动失败」被升级成「整个应用退出」。
    /// </summary>
    private async Task StartAndEmbedAsync()
    {
        try
        {
            await StartAndEmbedCoreAsync();
        }
        catch (Exception ex)
        {
            ReportActionFailure("启动 DSH 服务", ex);
        }
    }

    /// <summary>
    /// 动作入口的统一失败出口：记录日志、写状态栏、写标题栏错误行。
    /// 用它把「一次动作失败」与「应用退出」分开——后者是全局未处理异常处理器的行为。
    /// </summary>
    private void ReportActionFailure(string action, Exception ex)
    {
        DesktopLog.Error(action + "失败", ex);
        try
        {
            SetStatus(action + "失败: " + ex.Message);
            SetTitlebarError(action + "失败：" + ex.Message);
        }
        catch (Exception uiEx)
        {
            // 上报本身失败不能再抛：那又会走回"异常升级为退出"那条路（本方法存在的理由）
            DesktopLog.Warn("上报失败信息时出错: " + DesktopLog.Describe(uiEx));
        }
    }

    private async Task StartAndEmbedCoreAsync()
    {
        if (_serverHost.IsRunning) return;

        // Every launch gets a fresh Web trust boundary and health candidate. This prevents a late
        // NavigationCompleted event from a previous child process from committing the new profile.
        CancelFrontendHealthProbe();
        _trustedWebOrigin = null;
        _pendingTrustedNavigationId = null;
        _policyCancelledNavigationId = null;
        _healthyNavigationCommitted = false;

        var profile = Profiles.Active;
        var runtime = Runtime.Resolve();
        var binJs = runtime.DshEntryPath;
        var node = runtime.NodePath;
        DesktopLog.Info("运行时解析: nodeSource=" + runtime.NodeSource
            + " dshSource=" + runtime.DshSource
            + " runtimeId=" + (runtime.RuntimeId ?? "legacy/unverified")
            + " verified=" + runtime.IsVerified
            + " issues=" + (runtime.Issues.Count == 0
                ? "none"
                : string.Join(',', runtime.Issues.Select(issue => issue.Code))));
        DesktopLog.Info("profile 解析: name=" + profile.Name + " mode=" + profile.Mode
            + " directory=" + profile.Directory);
        if (!runtime.CanLaunch && runtime.Issues.Count > 0)
        {
            var issue = runtime.Issues[0];
            SetStatus("运行时不可用：" + issue.Message);
            SetTitlebarError("运行时校验失败，请使用恢复入口或重新安装完整运行时。");
            return;
        }
        if (binJs == null) { SetStatus("未找到 dsh 启动脚本（bin.js）。请先安装 @deepseek-ai/dsh。"); return; }
        if (node == null) { SetStatus("未找到 node.exe，请安装 Node.js。"); return; }

        // 启动前检查归属记录中的动态端口是否仍由本应用上次留下的进程占用。只在 pid、
        // node 进程名和启动时间全部吻合时才允许接管，绝不根据"端口被占"去结束未知进程。
        var leftoverRecord = ServerState.Read(DesktopRecovery.StateRoot, profile.Name);
        if (leftoverRecord is { Port: > 0 } && ResolvePortOwner(leftoverRecord.Port) == PortOwner.OwnLeftover)
        {
            var answer = AppDialog.Show(this, "发现上次遗留的 DSH 服务",
                $"动态端口 {leftoverRecord.Port} 上已有一个上次遗留的 DSH 服务（PID {leftoverRecord.Pid}）。\n\n" +
                "为避免同时运行两个服务：\n" +
                "· 「接管并重启」：结束它，并由系统重新分配端口（推荐）\n" +
                "· 「保留并启动」：保留它，本窗口使用新的动态端口（会同时有两个服务）\n" +
                "· 「取消启动」：这次不启动服务，稍后可从菜单「重启桌面端」重试",
                primary: "接管并重启", secondary: "保留并启动", cancel: "取消启动", warning: true);
            if (answer == AppDialogResult.Cancel)
            {
                SetStatus($"已取消启动（端口 {leftoverRecord.Port} 上仍有上次遗留的服务）。");
                SetTitlebarError("端口被上次遗留的 DSH 服务占用，启动已取消。可从菜单「重启桌面端」重试。");
                return;
            }
            if (answer == AppDialogResult.Primary)
            {
                SetStatus($"正在结束遗留服务（PID {leftoverRecord.Pid}）…");
                var freed = TakeOverLeftover(leftoverRecord.Pid, leftoverRecord.Port);
                ServerState.Clear(DesktopRecovery.StateRoot, profile.Name);
                SetStatus(freed
                    ? "遗留服务已结束，正在申请新的动态端口…"
                    : "遗留进程已退出，但原端口仍被占用；本次将使用新的动态端口。");
            }
            else
            {
                SetStatus($"保留端口 {leftoverRecord.Port} 上的既有服务，本窗口申请新的动态端口。");
            }
        }

        // 让 DSH 在绑定时向 OS 申请临时端口，消除"先探测空闲、再由子进程绑定"之间的竞态。
        _port = 0;

        var psi = new ProcessStartInfo(node)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 工作目录放到 launcher 安装根目录，而不是 @deepseek-ai\dsh\lib，
            // 避免 dsh 进程占住 node_modules 目录导致 npm 更新时 EBUSY。
            WorkingDirectory = runtime.InstallRoot ?? AppContext.BaseDirectory,
        };
        // Always bind the child to the descriptor's home. Safe Mode uses a separate home so the
        // normal home's cordis.patch.yml cannot re-introduce a broken third-party overlay.
        psi.Environment["DSH_HOME"] = profile.DshHome;
        psi.ArgumentList.Add(binJs);
        psi.ArgumentList.Add("--profile");
        psi.ArgumentList.Add(profile.Name);
        if (!Directory.Exists(profile.Directory)
            && !string.IsNullOrWhiteSpace(profile.InitializationTemplate))
        {
            psi.ArgumentList.Add("--from-default-profile");
            psi.ArgumentList.Add(profile.InitializationTemplate);
        }
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(_port.ToString());
        psi.ArgumentList.Add("--no-open");

        // 宿主环境：dsh-market 之类通过 dsh CLI 装插件的界面需要 pnpm/npm/corepack 可见，
        // 而它不读 `npm prefix -g`，只认 PATH 和几个固定目录；同时把可达的镜像源带给它，
        // 并（市场存在且用户未表态时）把它的自重启关掉——本窗口才是进程监督者。
        // 探测本身要起 npm/pnpm 子进程，放线程池上做，别卡住 UI。
        // 首次可能需要铺开内置插件、下载一份内置 pnpm，先给个交代，免得看着像卡住
        SetStatus("正在准备运行环境…");
        string? seedNotice = null;
        var hostEnv = await Task.Run(() =>
        {
            // 随应用发布的插件：只在目标 profile 还不存在时铺开，绝不覆盖用户已有 profile。
            // 必须在 PnpmSupport 之前——后者要读 profile 的 node_modules 元数据。
            seedNotice = Profiles.Initialize(profile).Notice;
            return MarketSupport.HostEnvironment(CachedRegistryOverride(), Environment.GetEnvironmentVariable("PATH"));
        });
        foreach (var pair in hostEnv) psi.Environment[pair.Key] = pair.Value;
        if (profile.Mode != ProfileMode.Safe)
            ApplyMarketRestartPolicy();
        if (seedNotice != null)
            _hostNotice = _hostNotice == null ? seedNotice : _hostNotice + "\n" + seedNotice;

        // pnpm 可用性修复（本机 pnpm 是 Rust 版、连不上 registry 时准备一份 Node 版 + 垫片）
        // 改为**后台预热**：它内部可能跑 `pnpm ping`（30s 超时）、必要时还要联网装一份内置
        // pnpm，原先在这里被 await，于是全部压在 dsh web 启动之前，用户看到的是"点了没反应"。
        // 现在垫片目录已由 MarketSupport.ToolDirs() 无条件前置进子进程 PATH，预热与服务启动
        // 并行进行；用户真正用到 pnpm 是稍后在市场里点安装插件，届时垫片已就位。
        if (profile.Mode != ProfileMode.Safe)
            StartPnpmWarmup();

        // 启动前拍一份 profile 清单快照。只有这次确实起来，它才会被提交为"已知可用"，
        // 因此它天然代表"上一次真的能启动的配置"——失败时才有东西可回滚。
        DesktopRecovery.SnapshotBeforeStart(profile.Directory, profile.Name);

        SetStatus("正在启动 dsh web（由系统分配 loopback 端口）…");
        _recentStderr.Clear();
        var startResult = await _serverHost.StartAsync(
            psi,
            DshWebUrlPattern,
            TimeSpan.FromSeconds(90));
        var hostSnapshot = startResult.Snapshot;
        if (startResult.Outcome == ServerStartOutcome.FailedToStart)
        {
            DesktopLog.Error("启动 dsh 服务失败: " + startResult.Error);
            SetStatus("启动 dsh 服务失败: " + startResult.Error);
            return;
        }

        if (hostSnapshot.ProcessId is { } processId)
            DesktopLog.Info("已启动 dsh: pid=" + processId + " port=" + _port + " cwd=" + psi.WorkingDirectory);

        _authUrl = startResult.Outcome == ServerStartOutcome.Ready ? startResult.Endpoint : null;
        if (_authUrl == null)
        {
            if (startResult.Outcome == ServerStartOutcome.Cancelled) return;
            var failure = startResult.Outcome == ServerStartOutcome.ExitedBeforeReady
                ? StartupFailure.Create(StartupFailure.Kind.BackendExited,
                    ServerHost.FormatExitCode(hostSnapshot.ExitCode))
                : StartupFailure.Create(StartupFailure.Kind.BackendTimeout, "90 秒内没有就绪 URL");
            DesktopLog.Error("[startup/" + failure.Code + "] " + failure.Summary + DescribeRecentStderr());
            SetStatus(failure.Summary);
            return;
        }

        if (!WebViewPolicy.TryCreateTrustedOrigin(_authUrl, out var trustedWebOrigin)
            || trustedWebOrigin == null)
        {
            var failure = StartupFailure.Create(StartupFailure.Kind.UntrustedEndpoint,
                "仅允许 127.0.0.1 的显式端口");
            DesktopLog.Error("[startup/" + failure.Code + "] " + failure.Summary
                + ": " + DesktopLog.RedactUrl(_authUrl));
            SetStatus(failure.Summary);
            SetTitlebarError("已阻止不受信任的 DSH 页面地址。");
            StopServer();
            BtnToggle.Content = "启动 DSH 服务";
            return;
        }

        _trustedWebOrigin = trustedWebOrigin;
        _port = trustedWebOrigin.Port;
        RememberServerOwner();

        DesktopLog.Info("dsh 已就绪: " + DesktopLog.RedactUrl(_authUrl));
        SetStatus($"DSH 后端已就绪(端口 {_port})，正在加载受信任页面…");
        Web.CoreWebView2!.Navigate(_authUrl);
        BtnToggle.Content = "停止 DSH 服务";
    }

    /// <summary>
    /// pnpm 可用性修复的后台预热。与 <c>StartAndEmbedAsync</c> 里的 dsh 启动并行，不再阻塞
    /// 主界面可用时间；上一次还没跑完时不重复起（下一次启动服务时若已结束，允许重试）。
    /// 结果只经日志与状态栏呈现——它是一个"锦上添花"的准备动作，失败不改变任何既有行为。
    /// </summary>
    private void StartPnpmWarmup()
    {
        if (_pnpmWarmup is { IsCompleted: false }) return;
        _pnpmWarmup = Task.Run(() =>
        {
            try
            {
                var notice = PnpmSupport.Prepare(Profiles.Active.Directory).Notice;
                if (string.IsNullOrEmpty(notice)) return;
                DesktopLog.Info("pnpm 预热: " + notice);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // 就绪前并入 _hostNotice（由就绪那一行一起显示）；就绪后直接写状态栏。
                    // 两条路径都在 UI 线程上，不会撕裂 _hostNotice 的读写。
                    if (_serverHost.ReadinessCompleted) SetStatus(notice!);
                    else _hostNotice = _hostNotice == null ? notice : _hostNotice + "\n" + notice;
                }));
            }
            catch (Exception ex)
            {
                // Prepare 自身不抛（内部已兜底）；这里只是不让线程池任务出现未观察异常
                DesktopLog.Warn("pnpm 预热异常: " + DesktopLog.Describe(ex));
            }
        });
    }

    /// <summary>
    /// 已探测出的镜像源；不触发探测（<see cref="TuiRegistryOverride"/> 会同步做网络可达性
    /// 检查，不能在不该阻塞的地方调用）。尚未探测出结果时返回 null——镜像源只是锦上添花，
    /// 市场自己也有区域镜像逻辑。
    /// </summary>
    private static string? CachedRegistryOverride()
    {
        lock (TuiRegistryLock)
        {
            return _tuiRegistryChecked ? _tuiRegistry : null;
        }
    }

    /// <summary>
    /// 市场装着且用户从未表态时，预置 <c>dsh-market.allowRestart: false</c>。
    /// 市场在 Windows 上识别不出监督者（只认 systemd/launchd/pm2），自重启会杀掉宿主并
    /// 另起游离进程——本窗口随即失去对进程的掌控，新的鉴权 URL 也回不到内嵌页面。
    /// </summary>
    private void ApplyMarketRestartPolicy()
    {
        try
        {
            if (!MarketSupport.EnsureMarketRestartDisabled(out var path)) return;
            _hostNotice = $"dsh-market：已预置 allowRestart: false（{path}），插件重启交由本窗口负责。";
        }
        catch (Exception ex)
        {
            _hostNotice = "dsh-market：写入 allowRestart 失败（" + ex.Message + "）。";
        }
    }

    private void OnServerOutputReceived(object? sender, ServerOutputEventArgs e)
    {
        var containsEndpoint = ServerHost.TryExtractEndpoint(DshWebUrlPattern, e.Line, out _);
        var safeLine = containsEndpoint ? DesktopLog.RedactUrl(e.Line) : e.Line;
        if (e.Stream == ServerOutputStream.StandardOutput)
        {
            DesktopLog.Info("[dsh:" + _port + "] " + safeLine);
            return;
        }

        DesktopLog.Warn("[dsh:" + _port + "] " + safeLine);
        RememberStderr(e.Line);
        // 含启动地址 / auth token 的行不写入状态栏，避免 token 泄露到界面。
        if (containsEndpoint || e.Line.Contains("token=", StringComparison.OrdinalIgnoreCase)) return;
        Dispatcher.Invoke(() => StatusText.Text += "\n" + e.Line);
    }

    private void OnServerHostExited(object? sender, ServerExitedEventArgs e)
    {
        if (!e.StopRequested) OnServerExitedUnexpectedly(e.Generation, e.ExitCode);
    }

    /// <summary>
    /// dsh 服务非主动退出时的自愈：等端口释放后重新拉起并重新内嵌。
    /// 连续失败耗尽 Core 恢复策略的自动重启预算后，停下并打开恢复助手。
    /// </summary>
    private void OnServerExitedUnexpectedly(int generation, int? exitCode)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (generation != _serverHost.Generation) return;   // 期间已被手动停止/重启，回调作废
            CancelFrontendHealthProbe();
            BtnToggle.Content = "启动 DSH 服务";
            var failure = StartupFailure.Create(
                StartupFailure.Kind.BackendExited,
                ServerHost.FormatExitCode(exitCode));
            var decision = _recovery.HandleUnexpectedExit(generation, failure);
            if (decision.Directive == RecoveryDirective.Ignore) return;

            DesktopLog.Warn("[recovery/" + failure.Code + "] dsh 服务异常退出: "
                + ServerHost.FormatExitCode(exitCode) + DescribeRecentStderr());
            if (decision.Directive == RecoveryDirective.ShowAssistant)
            {
                SetStatus($"dsh 服务异常退出，已停止自动重启（{decision.MaximumAutomaticRestarts} 次）。");
                await ShowRecoveryAssistantAsync(
                    "dsh 服务连续 " + decision.MaximumAutomaticRestarts + " 次自动重启后仍异常退出。",
                    "最后退出状态: " + ServerHost.FormatExitCode(exitCode)
                    + "\n\n最常见的原因是刚装上的插件与当前 Harness 版本不兼容——"
                    + "dsh 的 Loader 只要有一个 entry 加载失败，整次启动就会中止。"
                    + DescribeRecentStderr());
                return;
            }
            SetStatus($"dsh 服务已退出，正在自动重启（第 {decision.AutomaticRestartCount}/{decision.MaximumAutomaticRestarts} 次）…");
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            if (generation != _serverHost.Generation) return;
            _authUrl = null;
            await StartAndEmbedAsync();
        }));
    }

    // ---------- 窗口按钮 / 快捷键 ----------

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>
    /// 关闭按钮：**收起窗口到托盘**，不再直接退出。
    /// 退出入口只剩托盘菜单「退出」——它调用 <c>Window.Close()</c>，因此仍会经过
    /// <c>Window_Closing</c> 的「更新进行中」确认门；同时也消除了「更新进行中点✕」把
    /// 下载或解包任务在窗口关闭后失去宿主的路径（隐藏窗口不会结束任务）。
    /// </summary>
    private void OnClose(object sender, RoutedEventArgs e) => _trayController.Hide();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        BtnMaximize.Content = maximized ? "\uE923" : "\uE922";
        BtnMaximize.ToolTip = maximized ? "还原" : "最大化";

        // 最小化只进任务栏——托盘行为已移到关闭按钮（见 TrayController.Hide）。
        // 因此最小化时不更新恢复目标，否则从托盘恢复会得到一个最小化的窗口。
        _trayController.RecordWindowState(WindowState);
    }

    private void FocusTrayRestoredContent()
    {
        if (TuiPanel.Visibility == Visibility.Visible)
        {
            TuiView.Focus();
            Keyboard.Focus(TuiView);
        }
        else
        {
            ForceFocusIntoWeb();
        }
    }

    /// <summary>
    /// 第二实例唤醒入口（优化清单 B32）：把已有实例的窗口从三种状态恢复并置前。
    /// ① 已收起到托盘 → 由 <see cref="TrayController.Restore"/> 清状态、隐藏托盘图标并显示窗口；
    /// ② 已最小化 → 恢复为控制器记录的正常/最大化状态，**不得停在最小化**；
    /// ③ 可见但在后台 → 置前。
    /// 对 ②③ 仍需在此单独覆盖。任何异常只记日志，不影响应用。
    ///
    /// **前台限制（如实声明）**：Windows 前台锁会拒绝后台进程抢占前台，<c>Activate()</c>/<c>SetForegroundWindow</c>
    /// 都可能不生效。本实现采用三重规避：二次实例先调 <c>AllowSetForegroundWindow(ASFW_ANY)</c> 代为授权
    /// （见 <see cref="SingleInstanceIpc"/>），首实例再 <c>ShowWindow(SW_RESTORE)</c> + <c>SetForegroundWindow</c>
    /// + Topmost 闪切。<b>仍不保证一定成功</b>——实测结果与限制记入实施记录（t26 §9.4）。
    /// </summary>
    private void ActivateFromSecondInstance()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ActivateFromSecondInstance));
            return;
        }

        try
        {
            if (_trayController.IsHidden) _trayController.Restore();

            if (WindowState == WindowState.Minimized)   // ②：不得停在最小化
            {
                WindowState = _trayController.RestoreState == WindowState.Maximized
                    ? WindowState.Maximized
                    : WindowState.Normal;
            }

            if (!IsVisible) Show();   // 兜底：任何原因不可见时先显示

            // ② 的兜底：WPF 的 WindowState 属性可能与 Win32 实际状态不一致（例如窗口被外部
            // ShowWindow(SW_MINIMIZE) 最小化时，WPF 未必观察到状态变化，赋值 Normal 便成了空操作）。
            // 因此这里按 Win32 实际状态再强制恢复一次，保证「不得停在最小化」。
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero && IsIconic(hwnd))
                ShowWindow(hwnd, SW_RESTORE);

            Activate();               // ③：尝试置前
            // 规避 Windows 前台锁：Topmost 闪切（与 TrayController.Restore 一致）+ SetForegroundWindow。
            Topmost = true;
            Topmost = false;
            if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);

            if (TuiPanel.Visibility == Visibility.Visible)
            {
                TuiView.Focus();
                Keyboard.Focus(TuiView);
            }
            else
            {
                ForceFocusIntoWeb();
            }
        }
        catch (Exception ex)
        {
            DesktopLog.Warn("处理第二实例唤醒请求失败: " + DesktopLog.Describe(ex));
        }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>把当前服务地址（含鉴权 token）复制到剪贴板。</summary>
    private void CopyAuthUrlToClipboard()
    {
        if (string.IsNullOrWhiteSpace(_authUrl))
        {
            SetStatus("服务尚未就绪，暂时没有可复制的地址。");
            return;
        }
        try
        {
            Clipboard.SetText(_authUrl!);
            SetStatus("已复制访问地址。");
        }
        catch (Exception ex)
        {
            // 剪贴板被别的进程占用是常见失败（与状态详情里的复制同源处理）。
            ReportActionFailure("复制访问地址", ex);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F5:
                e.Handled = true;
                ReloadWeb();
                break;
            case Key.F12:
                e.Handled = true;
                ToggleDevTools();
                break;
            case Key.Escape:
                // 仅在确有标题栏浮层打开时才吞掉 Esc，否则会抢走 WebView2 内页面的 Esc。
                if (CloseAllTitlebarPopups()) e.Handled = true;
                break;
        }
    }

    // ---------- 标题栏浮层：集中关闭与互斥 ----------

    /// <summary>
    /// 标题栏浮层的唯一互斥点：任意时刻至多一个浮层打开。
    /// 打开任何浮层前先调用本方法。返回是否确实关闭了某些浮层（供 Esc 判断是否吞键）。
    /// </summary>
    private bool CloseAllTitlebarPopups()
    {
        var anyOpen = false;
        if (VersionPopup.IsOpen) { VersionPopup.IsOpen = false; anyOpen = true; }
        if (UpdatePopup.IsOpen) { UpdatePopup.IsOpen = false; anyOpen = true; }
        if (RestartMenu.IsOpen) { RestartMenu.IsOpen = false; anyOpen = true; }
        if (DevMenu.IsOpen) { DevMenu.IsOpen = false; anyOpen = true; }
        if (StatusMenu.IsOpen) { StatusMenu.IsOpen = false; anyOpen = true; }
        if (StatusDetailPopup.IsOpen) { StatusDetailPopup.IsOpen = false; anyOpen = true; }

        // 锚点展开态必须与实际浮层状态一起复位，否则按钮会永久停在"已展开"外观。
        SetTitleBarTag(BtnVersion, false);
        SetTitleBarTag(BtnRestartMenu, false);
        SetTitleBarTag(BtnDevMenu, false);
        SetTitleBarTag(BtnMore, false);
        SetTitleBarTag(BtnStatusDetail, false);
        return anyOpen;
    }

    /// <summary>
    /// 设置标题栏锚点按钮的展开态标记。TitleIconButton 样式以 Tag="True" 触发高亮
    /// （参考实现用 aria-expanded 高亮，见 extended-styles.ts:347-352）。
    /// 显式 ApplyTemplate：保证展开/收起视觉立刻刷新，不依赖属性失效时机。
    /// </summary>
    private static void SetTitleBarTag(System.Windows.Controls.Button button, bool expanded)
    {
        button.Tag = expanded ? "True" : "False";
        button.ApplyTemplate();

        // 展开态也必须暴露给 UI Automation：Tag 只驱动视觉，读屏软件读不到。
        // ItemStatus 是 WPF 中 aria-expanded 最接近的等价物（挂在元素的可访问性状态串上），
        // 收起路径由同一方法统一写回"已折叠"，不留陈旧状态。
        System.Windows.Automation.AutomationProperties.SetItemStatus(
            button, expanded ? "已展开" : "已折叠");
    }

    private void OnToggleVersionPopup(object sender, RoutedEventArgs e)
    {
        var wasOpen = VersionPopup.IsOpen;
        CloseAllTitlebarPopups();
        if (!wasOpen) CenterPopupOnVersionChip();
        VersionPopup.IsOpen = !wasOpen;
        SetTitleBarTag(BtnVersion, VersionPopup.IsOpen);
    }

    /// <summary>
    /// 把版本浮层水平居中到标题栏的版本胶囊下方。
    ///
    /// <c>Placement="Bottom"</c> 的语义是"浮层左缘与目标左缘对齐"，所以居中只能靠
    /// <c>HorizontalOffset = (胶囊宽 - 浮层宽) / 2</c>；胶囊宽度随版本号长度变化
    /// （"dsh v--" 与 "dsh v0.1.5-alpha.1" 差一倍），所以每次打开都按实际宽度现算，
    /// 而不是在 XAML 里写死偏移量。必须**在 IsOpen = true 之前**赋值，否则首个
    /// 布局回合仍按旧偏移摆放。
    /// </summary>
    private void CenterPopupOnVersionChip()
    {
        try
        {
            var chipWidth = BtnVersion.ActualWidth;
            var popupWidth = VersionPopupSurface.Width;
            if (double.IsNaN(popupWidth) || popupWidth <= 0) return;
            if (double.IsNaN(chipWidth) || chipWidth <= 0) return;
            VersionPopup.HorizontalOffset = (chipWidth - popupWidth) / 2;
        }
        catch (Exception ex)
        {
            // 居中失败只是不好看：退回 XAML 的 0（左缘对齐），绝不能挡住打开浮层。
            DesktopLog.Warn("版本浮层居中失败: " + DesktopLog.Describe(ex));
        }
    }

    private void OnToggleRestartMenu(object sender, RoutedEventArgs e)
    {
        var wasOpen = RestartMenu.IsOpen;
        CloseAllTitlebarPopups();
        RestartMenu.IsOpen = !wasOpen;
        SetTitleBarTag(BtnRestartMenu, RestartMenu.IsOpen);
    }

    private void OnToggleDevMenu(object sender, RoutedEventArgs e)
    {
        var wasOpen = DevMenu.IsOpen;
        CloseAllTitlebarPopups();
        DevMenu.IsOpen = !wasOpen;
        SetTitleBarTag(BtnDevMenu, DevMenu.IsOpen);
    }

    // ---------- 状态栏：详情浮层 + 操作菜单 ----------

    /// <summary>⋯ 菜单：原先常驻的停止/外部打开/退出时停止/日志都收进这里。</summary>
    private void OnToggleStatusMenu(object sender, RoutedEventArgs e)
    {
        var wasOpen = StatusMenu.IsOpen;
        CloseAllTitlebarPopups();
        StatusMenu.IsOpen = !wasOpen;
        SetTitleBarTag(BtnMore, StatusMenu.IsOpen);
    }

    /// <summary>状态详情：完整（可能多行）状态文本，可选中 / Ctrl+C 复制。</summary>
    private void OnToggleStatusDetail(object sender, RoutedEventArgs e)
    {
        var wasOpen = StatusDetailPopup.IsOpen;
        CloseAllTitlebarPopups();
        if (!wasOpen)
        {
            StatusDetailText.Text = _statusFullText ?? StatusText.Text;
            BtnCopyStatus.Content = "复制全部";   // 复位上一次的"已复制/复制失败"
        }
        StatusDetailPopup.IsOpen = !wasOpen;
        SetTitleBarTag(BtnStatusDetail, StatusDetailPopup.IsOpen);
    }

    private void OnCopyStatusDetail(object sender, RoutedEventArgs e)
    {
        var text = string.IsNullOrEmpty(_statusFullText) ? StatusText.Text : _statusFullText;
        try
        {
            Clipboard.SetText(text);
            BtnCopyStatus.Content = "已复制";
        }
        catch (Exception ex)
        {
            // 剪贴板被其它进程占用是常见失败；照常上报，不回退成静默。
            ReportActionFailure("复制状态详情", ex);
            BtnCopyStatus.Content = "复制失败";
        }
    }

    private void OnCloseStatusDetail(object sender, RoutedEventArgs e)
    {
        CloseAllTitlebarPopups();
    }

    // ---------- 重启族菜单动作 ----------

    private void OnMenuReloadRenderer(object sender, RoutedEventArgs e)
    {
        CloseAllTitlebarPopups();
        ReloadWeb();
    }

    /// <summary>
    /// 重启桌面端：停掉本窗口托管的 dsh 子进程并重新拉起、重新内嵌。
    /// 不改「重启/不重启」的用户设置，等价于把当前会话原地重建一次。
    /// </summary>
    private async void OnMenuRestartDesktop(object sender, RoutedEventArgs e)
    {
        CloseAllTitlebarPopups();
        BeginBusy("restart");   // 立即进入 busy：禁用动作与更新族，避免与在途更新/检查叠加
        try
        {
            SetStatus("正在重启 dsh 服务…");
            // RestartServerAsync → StartAndEmbedAsync 内部已经 Navigate(新鉴权 URL)，
            // 这里**不能**再补一次 Reload()：那会在导航在途时中断它，
            // 触发 OnNavigationCompleted(IsSuccess=false, WebErrorStatus=ConnectionAborted)，
            // 状态栏随即显示"网页加载失败(ConnectionAborted)"。
            await RestartServerAsync();
            SetStatus("dsh 服务已重启。");
        }
        catch (Exception ex)
        {
            DesktopLog.Error("重启 dsh 服务失败", ex);
            SetStatus("重启 dsh 服务失败: " + ex.Message);
            SetTitlebarError("重启失败：" + ex.Message);
        }
        finally
        {
            EndBusy();
            FocusWindowAndWeb();
        }
    }

    /// <summary>
    /// Shows the active and rollback runtime slots, then performs a health-gated rollback only
    /// after explicit confirmation. A failed manual rollback immediately restores the original
    /// slot instead of leaving the user on an unverified runtime.
    /// </summary>
    private async void OnMenuManageRuntime(object sender, RoutedEventArgs e)
    {
        CloseAllTitlebarPopups();
        BeginBusy("restart");
        try
        {
            var slots = new RuntimeSlotManager(Runtime.WritableRuntimeRoot);
            var active = slots.ResolveActive(verifyFiles: true);
            var currentId = active.Selection?.ActiveRuntimeId;
            var previousId = active.Selection?.PreviousRuntimeId;
            if (!active.IsReady || string.IsNullOrWhiteSpace(currentId) || string.IsNullOrWhiteSpace(previousId))
            {
                AppDialog.Show(
                    this,
                    "运行时信息",
                    "当前没有可回退的已验证运行时槽。\n\n当前状态：" + active.Status
                        + (string.IsNullOrWhiteSpace(active.Error) ? string.Empty : "\n原因：" + active.Error),
                    primary: "知道了",
                    warning: true);
                return;
            }

            var previous = slots.Resolve(previousId, verifyFiles: true);
            if (!previous.IsReady)
            {
                AppDialog.Show(
                    this,
                    "运行时信息",
                    "上一运行时槽未通过完整性校验，不能回退。\n\n槽：" + previousId
                        + "\n原因：" + (previous.Error ?? previous.Status.ToString()),
                    primary: "知道了",
                    warning: true);
                return;
            }

            var currentVersion = active.Manifest?.DshVersion ?? "未知";
            var previousVersion = previous.Manifest?.DshVersion ?? "未知";
            var answer = AppDialog.Show(
                this,
                "运行时信息与回退",
                "当前槽：" + currentId + "（dsh v" + currentVersion + "）\n"
                    + "可回退槽：" + previousId + "（dsh v" + previousVersion + "）\n\n"
                    + "回退会停止并重启 DSH 服务。若目标槽未通过前端健康验证，将自动恢复当前槽。",
                primary: "回退并重启",
                cancel: "取消",
                warning: true);
            if (answer != AppDialogResult.Primary) return;

            SetStatus("正在回退到上一已验证运行时槽…");
            StopServer();
            var rollback = slots.Rollback();
            if (!rollback.Success)
            {
                var error = "无法回退运行时槽：" + rollback.Error;
                SetStatus(error);
                AppDialog.Show(this, "运行时回退", error, primary: "知道了", warning: true);
                await StartAndEmbedAsync();
                return;
            }

            Runtime.Invalidate();
            ShowCurrentVersion();
            await StartAndEmbedAsync();
            if (await WaitForFrontendHealthAsync(TimeSpan.FromSeconds(50)))
            {
                SetStatus("已回退到 dsh v" + previousVersion + "，运行时槽通过前端健康验证。");
                return;
            }

            StopServer();
            var restore = slots.Rollback();
            Runtime.Invalidate();
            var restoredHealthy = false;
            if (restore.Success)
            {
                ShowCurrentVersion();
                await StartAndEmbedAsync();
                restoredHealthy = await WaitForFrontendHealthAsync(TimeSpan.FromSeconds(50));
            }
            var failure = restore.Success
                ? restoredHealthy
                    ? "回退目标未通过前端健康验证，已恢复原运行时槽。"
                    : "回退目标未通过前端健康验证，且恢复原运行时槽后健康验证仍失败。"
                : "回退目标未通过前端健康验证，恢复原运行时槽失败：" + restore.Error;
            DesktopLog.Error("手动运行时回退失败：" + failure);
            SetStatus(failure);
            AppDialog.Show(this, "运行时回退", failure, primary: "知道了", warning: true);
        }
        catch (Exception ex)
        {
            DesktopLog.Error("运行时管理失败", ex);
            SetStatus("运行时管理失败：" + ex.Message);
            AppDialog.Show(this, "运行时信息", "无法读取或切换运行时槽：" + ex.Message, primary: "知道了", warning: true);
        }
        finally
        {
            EndBusy();
            FocusWindowAndWeb();
        }
    }

    /// <summary>
    /// Switches the supervised child process between the compatibility profile and an isolated
    /// safe profile. The safe profile is initialized from DSH's shipped web template only; it does
    /// not seed or mutate the normal profile and does not start marketplace/pnpm preparation.
    /// </summary>
    private async void OnMenuToggleSafeMode(object sender, RoutedEventArgs e)
    {
        CloseAllTitlebarPopups();
        BeginBusy("restart");
        var previousMode = Profiles.Active.Mode;
        var nextMode = previousMode == ProfileMode.Safe ? ProfileMode.Normal : ProfileMode.Safe;
        try
        {
            SetStatus(nextMode == ProfileMode.Safe
                ? "正在切换到隔离安全模式…"
                : "正在返回正常模式…");
            StopServer();
            Profiles.Activate(nextMode);
            UpdateSafeModeMenu();
            await StartAndEmbedAsync();
            if (_authUrl == null)
                throw new InvalidOperationException("目标 profile 未产生可信启动地址。");
            SetStatus(nextMode == ProfileMode.Safe
                ? "已进入安全模式（desktop-safe）。"
                : "已返回正常模式。");
        }
        catch (Exception ex)
        {
            StopServer();
            Profiles.Activate(previousMode);
            UpdateSafeModeMenu();
            await StartAndEmbedAsync();
            ReportActionFailure("切换安全模式", ex);
        }
        finally
        {
            EndBusy();
            FocusWindowAndWeb();
        }
    }

    private void UpdateSafeModeMenu()
    {
        var safe = Profiles.Active.Mode == ProfileMode.Safe;
        MenuSafeMode.Header = safe ? "退出安全模式" : "切换到安全模式";
        MenuSafeMode.ToolTip = safe
            ? "停止隔离 profile 并返回正常 profile"
            : "使用只含官方核心模板的 desktop-safe profile 重新启动";
        Title = safe ? "DSH Desktop — 安全模式" : "DSH Desktop";
    }

    /// <summary>
    /// 重启到恢复模式：停掉服务并打开恢复助手（诊断/回滚/停用可疑插件）。
    /// 刻意不另起进程：本窗口对 dsh 子进程的掌控是恢复流程的前提（见 ApplyMarketRestartPolicy）。
    /// </summary>
    private async void OnMenuRestartToRecovery(object sender, RoutedEventArgs e)
    {
        CloseAllTitlebarPopups();
        StopServer();
        _recovery.EnterAssistance();
        SetStatus("已停止 dsh 服务，正在打开恢复助手…");
        BeginBusy("recover");   // 期间禁用动作与更新族，但版本浮层保持可用（恢复助手要显示错误行）
        try
        {
            await ShowRecoveryAssistantAsync(
                "已进入恢复模式。",
                "dsh 服务已停止，内嵌页面不会再自动重连。可在下方选择：打开日志目录、"
                + "停用最近安装的插件、回滚上一次快照，或直接重试启动。");
        }
        catch (Exception ex)
        {
            // async void 的异常没有调用方能接；不在这里接就是"弹框 + 退出应用"
            ReportActionFailure("打开恢复助手", ex);
        }
        finally
        {
            EndBusy();
        }
    }

    private void OnMenuToggleDevTools(object sender, RoutedEventArgs e)
    {
        CloseAllTitlebarPopups();
        ToggleDevTools();
    }

    /// <summary>刷新内嵌网页（同 F5）。失败不再静默吞掉。</summary>
    private void ReloadWeb()
    {
        try
        {
            if (Web.CoreWebView2 == null) { SetTitlebarError("页面尚未就绪，无法重载。"); return; }
            Web.CoreWebView2.Reload();
            SetTitlebarError(null);
        }
        catch (Exception ex)
        {
            DesktopLog.Error("重载内嵌页面失败", ex);
            SetStatus("重载内嵌页面失败: " + ex.Message);
            SetTitlebarError("重载失败：" + ex.Message);
        }
    }

    /// <summary>切换开发者工具（同 F12）。失败不再静默吞掉。</summary>
    private void ToggleDevTools()
    {
        try
        {
            if (Web.CoreWebView2 == null) { SetTitlebarError("页面尚未就绪，无法打开开发者工具。"); return; }
            if (!Web.CoreWebView2.Settings.AreDevToolsEnabled)
            {
                SetTitlebarError("正式版默认关闭开发者工具；设置 DSHDESKTOP_ENABLE_DEVTOOLS=1 后重启可启用。");
                return;
            }
            Web.CoreWebView2.OpenDevToolsWindow();
            SetTitlebarError(null);
        }
        catch (Exception ex)
        {
            DesktopLog.Error("打开开发者工具失败", ex);
            SetStatus("打开开发者工具失败: " + ex.Message);
            SetTitlebarError("开发者工具打开失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 标题栏内联错误：让动作失败在用户真正操作的地方可见。
    /// 会自动展开版本浮层承载文案——窗口顶部没有常驻的错误行，
    /// 而版本浮层是唯一锚在身份区、用户此刻正在看的位置。
    /// </summary>
    private void SetTitlebarError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            VersionPopupError.Text = "";
            VersionPopupError.Visibility = Visibility.Collapsed;
            return;
        }

        VersionPopupError.Text = message;
        VersionPopupError.Visibility = Visibility.Visible;
        if (!VersionPopup.IsOpen)
        {
            CloseAllTitlebarPopups();
            VersionPopup.IsOpen = true;
            SetTitleBarTag(BtnVersion, true);   // 浮层由错误提示代开，锚点态必须同步
        }
    }

    private void OnToggleDevTools(object sender, RoutedEventArgs e) => ToggleDevTools();

    // ---------- 集成 TUI（ConPTY 终端） ----------

    private void OnToggleTui(object sender, RoutedEventArgs e)
    {
        if (TuiPanel.Visibility == Visibility.Visible) HideTui();
        else ShowTui();
    }

    private void ShowTui()
    {
        if (!_tuiStarted)
        {
            TuiView.Exited += OnTuiExited;
            TuiView.TitleChanged += OnTuiTitle;
            StartTuiSession();
            _tuiStarted = true;
        }
        UpdateTuiTitle();
        TuiPanel.Visibility = Visibility.Visible;
        // WebView2 是子 HWND，会盖住 WPF 内容，因此显示终端时收起网页。
        Web.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TuiView.Focus();
            Keyboard.Focus(TuiView);
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void HideTui()
    {
        TuiPanel.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Visible;
        FocusWindowAndWeb();
    }

    private void UpdateTuiTitle()
    {
        var suffix = TuiView.IsRunning ? "" : "（已退出）";
        var baseName = "TUI · " + TuiView.ShellName + suffix;
        // 应用通过 OSC 0/2 设置的标题（如 dsh-tui 的会话名）附在后面
        TuiTitle.Text = string.IsNullOrWhiteSpace(_tuiTitle) ? baseName : baseName + " — " + _tuiTitle;
    }

    private void OnTuiTitle(string title)
    {
        _tuiTitle = title;
        Dispatcher.BeginInvoke(new Action(UpdateTuiTitle));
    }

    private void OnTuiExited() => UpdateTuiTitle();

    private void OnTuiClear(object sender, RoutedEventArgs e) => TuiView.ClearScreen();

    private void OnTuiPaste(object sender, RoutedEventArgs e)
    {
        TuiView.Focus();
        if (!TuiView.PasteFromClipboard()) SetStatus("剪贴板里没有可粘贴的文本。");
    }

    private void OnTuiRestart(object sender, RoutedEventArgs e)
    {
        StartTuiSession();
        UpdateTuiTitle();
        TuiView.Focus();
    }

    /// <summary>启动终端会话：注入 PATH 前缀（node / dsh / npm 全局 bin）与必要的环境变量（镜像源）。</summary>
    private void StartTuiSession()
    {
        try { TuiPrepTask.Wait(TimeSpan.FromSeconds(6)); } catch { /* 预热超时就先启动，稍后可点“重启终端” */ }

        _tuiTitle = null;
        var env = TuiExtraEnv();
        TuiView.Start(TuiExtraPath(), env);
        if (env.TryGetValue("pnpm_config_registry", out var registry))
            SetStatus($"TUI 终端已自动使用镜像源 {registry}（registry.npmjs.org 不可达）。");
    }

    /// <summary>
    /// 给集成终端注入的 PATH 前缀（只作用于终端子进程，不改系统环境变量）：
    /// 1) 内置/系统 node 目录；2) 正在运行的 dsh 的 .bin 目录（dsh.cmd / dsh.ps1 垫片）；
    /// 3) npm 全局 bin 目录（pnpm 等全局命令常装在这里，而机器 PATH 可能指向旧前缀）。
    /// 顺序保证终端里的 dsh 用 App 自己的版本，而不是全局旧版本。
    /// </summary>
    private static string? TuiExtraPath()
    {
        var parts = new List<string>();
        var runtime = Runtime.Resolve();

        var node = runtime.NodePath;
        if (node != null)
        {
            var dir = Path.GetDirectoryName(node);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) parts.Add(dir);
        }

        var binJs = runtime.DshEntryPath;
        if (binJs != null)
        {
            // <install>\node_modules\@deepseek-ai\dsh\lib\bin.js -> <install>\node_modules\.bin
            var lib = Path.GetDirectoryName(binJs);
            var pkg = lib == null ? null : Path.GetDirectoryName(lib);
            var scope = pkg == null ? null : Path.GetDirectoryName(pkg);
            var nodeModules = scope == null ? null : Path.GetDirectoryName(scope);
            if (nodeModules != null)
            {
                var binDir = Path.Combine(nodeModules, ".bin");
                if (Directory.Exists(binDir)) parts.Add(binDir);
            }
        }

        var npmGlobal = NpmGlobalBinDir();
        if (npmGlobal != null && !parts.Contains(npmGlobal, StringComparer.OrdinalIgnoreCase))
            parts.Add(npmGlobal);

        // 与 dsh 宿主进程用同一份工具链目录：市场的 pnpm/npm/corepack 探测只看这些位置，
        // 终端里手动执行 `dsh plugin ... add` 也需要它们才找得到 pnpm。
        var merged = new List<string>();
        foreach (var dir in MarketSupport.ToolDirs()) merged.Add(dir);
        foreach (var dir in parts)
            if (!merged.Contains(dir, StringComparer.OrdinalIgnoreCase)) merged.Add(dir);

        return merged.Count > 0 ? string.Join(';', merged) : null;
    }

    private static string? _npmGlobalBinDir;
    private static bool _npmGlobalBinChecked;
    private static readonly object NpmGlobalBinLock = new();

    /// <summary>npm 全局 bin 目录（<c>npm prefix -g</c>），结果缓存；失败返回 null。</summary>
    internal static string? NpmGlobalBinDir()
    {
        lock (NpmGlobalBinLock)
        {
            if (_npmGlobalBinChecked) return _npmGlobalBinDir;
            _npmGlobalBinChecked = true;
            try
            {
                var npm = Runtime.FindNpm();
                if (npm == null) return null;

                var psi = new ProcessStartInfo(npm.NodePath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add(npm.NpmCliPath);
                psi.ArgumentList.Add("prefix");
                psi.ArgumentList.Add("-g");

                using var proc = Process.Start(psi);
                if (proc == null) return null;
                var output = proc.StandardOutput.ReadToEnd();
                if (!proc.WaitForExit(8000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    return null;
                }
                var dir = output.Trim().Split('\n')[0].Trim();
                _npmGlobalBinDir = Directory.Exists(dir) ? dir : null;
            }
            catch
            {
                _npmGlobalBinDir = null;
            }
            return _npmGlobalBinDir;
        }
    }

    // ---------- 集成终端的 npm 镜像源自适应 ----------

    /// <summary>默认源不可达时使用的镜像源。</summary>
    private const string NpmMirrorRegistry = "https://registry.npmmirror.com";

    private static readonly object TuiRegistryLock = new();
    private static bool _tuiRegistryChecked;
    private static string? _tuiRegistry;
    private static Task? _tuiPrepTask;

    /// <summary>启动时预热（npm 全局目录 + 镜像源探测），避免首次打开 TUI 时卡住。</summary>
    private static Task TuiPrepTask =>
        _tuiPrepTask ??= Task.Run(() =>
        {
            NpmGlobalBinDir();
            TuiRegistryOverride();
        });

    /// <summary>
    /// 决定要不要给集成终端注入镜像源。用户自己配置过 registry 就完全不动；
    /// 否则探测 registry.npmjs.org，不可达而镜像可达时返回镜像地址。
    /// 也可用环境变量 DSHDESKTOP_NPM_REGISTRY 强制指定。
    /// </summary>
    private static string? TuiRegistryOverride()
    {
        lock (TuiRegistryLock)
        {
            if (_tuiRegistryChecked) return _tuiRegistry;
            _tuiRegistryChecked = true;
            try
            {
                var manual = Environment.GetEnvironmentVariable("DSHDESKTOP_NPM_REGISTRY");
                if (!string.IsNullOrWhiteSpace(manual)) return _tuiRegistry = manual.Trim();

                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("pnpm_config_registry"))
                    || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("npm_config_registry"))
                    || UserNpmrcSetsRegistry())
                    return _tuiRegistry = null;   // 用户已有明确配置，尊重它

                if (VersionUpdate.IsReachableAsync("https://registry.npmjs.org/-/ping").GetAwaiter().GetResult())
                    return _tuiRegistry = null;   // 默认源可达

                var mirrorOk = VersionUpdate.IsReachableAsync(NpmMirrorRegistry + "/-/ping").GetAwaiter().GetResult();
                return _tuiRegistry = mirrorOk ? NpmMirrorRegistry : null;
            }
            catch
            {
                return _tuiRegistry = null;
            }
        }
    }

    /// <summary>用户的 ~/.npmrc 是否显式配置了 registry。</summary>
    private static bool UserNpmrcSetsRegistry()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".npmrc");
            if (!File.Exists(path)) return false;
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("registry", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('='))
                    return true;
            }
        }
        catch { /* ignore */ }
        return false;
    }

    /// <summary>构造 TUI 子进程的额外环境变量。</summary>
    private static Dictionary<string, string> TuiExtraEnv()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var registry = TuiRegistryOverride();
        if (!string.IsNullOrEmpty(registry))
        {
            env["pnpm_config_registry"] = registry!;
            env["npm_config_registry"] = registry!;
        }

        // 描述这个终端本身的能力：24 位色 + xterm-256color。二者都是真的，
        // 能让 CLI/TUI 走彩色渲染而不是降级成单色。
        // 故意不设置 TERM_PROGRAM：dsh-TUI 一类应用在原生 Windows 上正是不认识
        // TERM_PROGRAM 时才会启用 win32 输入模式（DECSET 9001），而内置终端已经
        // 实现了该模式的按键编码，这是 Windows 上唯一能保留 Shift/Ctrl+Enter 的通道。
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TERM")))
            env["TERM"] = "xterm-256color";
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COLORTERM")))
            env["COLORTERM"] = "truecolor";

        return env;
    }

    // ---------- UI / 焦点 ----------

    private void SetStatus(string text)
    {
        _statusFullText = text;
        try
        {
            // 单行化：多行诊断（就绪行 + _hostNotice）在状态栏里压成一行显示，
            // 全文进 ToolTip + 「详情」浮层（可选中复制）。见 _statusFullText 注释。
            StatusText.Text = text.Replace("\r\n", " · ").Replace('\n', '·');
            StatusText.ToolTip = text;
            var hasDetail = text.Contains('\n') || StatusText.Text.Length > 100;
            BtnStatusDetail.Visibility = hasDetail ? Visibility.Visible : Visibility.Collapsed;
            if (StatusDetailPopup.IsOpen) StatusDetailText.Text = text;
        }
        catch (Exception ex)
        {
            // 详情入口的可见性绝不能反过来打断状态上报（它承载启动失败原因），
            // 例如 SetStatus 早于 InitializeComponent 完成时几个控件还是 null。
            DesktopLog.Warn("更新状态栏失败: " + DesktopLog.Describe(ex));
        }
        // 状态迁移也是证据：此前只活在界面上，关窗即丢
        DesktopLog.Info("[status] " + text.Replace("\r", " ").Replace("\n", " / "));
    }

    /// <summary>记住最近若干行 stderr。</summary>
    private void RememberStderr(string line)
    {
        _recentStderr.Enqueue(line);
        while (_recentStderr.Count > RecentStderrLines) _recentStderr.Dequeue();
    }

    private string DescribeRecentStderr()
    {
        if (_recentStderr.Count == 0) return "\n（dsh 未输出任何 stderr）";
        return "\n--- dsh stderr 最后 " + _recentStderr.Count + " 行 ---\n"
               + string.Join("\n", _recentStderr)
               + "\n--- /stderr ---";
    }

    /// <summary>打开日志目录（不存在就先建）。</summary>
    private void OpenLogDirectory()
    {
        try
        {
            var dir = DesktopLog.Directory ?? DesktopLog.DefaultDirectory();
            System.IO.Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            DesktopLog.Info("已打开日志目录: " + dir);
        }
        catch (Exception ex)
        {
            DesktopLog.Error("打开日志目录失败", ex);
            SetStatus("无法打开日志目录: " + ex.Message);
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => OpenLogDirectory();

    /// <summary>导出诊断 zip（日志 + 系统信息摘要）。打包放线程池，别卡 UI。</summary>
    private async void OnExportDiagnostics(object sender, RoutedEventArgs e)
    {
        try
        {
            var answer = AppDialog.Show(this, "导出诊断",
                "诊断包包含最近的日志与一份系统信息摘要。\n\n"
                + "日志可能含有本机路径、工作区与会话标识；请自行确认后再分享。",
                primary: "选择目录",
                secondary: "导出到日志目录",
                cancel: "取消");
            if (answer == AppDialogResult.Cancel) return;

            var dir = DesktopLog.Directory ?? DesktopLog.DefaultDirectory();
            System.IO.Directory.CreateDirectory(dir);
            if (answer == AppDialogResult.Primary)
            {
                using var picker = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "选择诊断包保存目录",
                    InitialDirectory = dir,
                    ShowNewFolderButton = true,
                };
                if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK
                    || string.IsNullOrWhiteSpace(picker.SelectedPath))
                {
                    SetStatus("已取消选择诊断包保存目录。");
                    return;
                }
                dir = picker.SelectedPath;
            }
            var target = Path.Combine(dir, "dsh-desktop-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
            SetStatus("正在导出诊断包…");
            var result = await Task.Run(() => DesktopLog.ExportDiagnostics(target));
            if (result == null) { SetStatus("导出诊断失败，详见日志。"); return; }
            SetStatus("诊断包已导出: " + result);
            try { Process.Start(new ProcessStartInfo { FileName = Path.GetDirectoryName(result)!, UseShellExecute = true }); } catch { }
        }
        catch (Exception ex)
        {
            DesktopLog.Error("导出诊断异常", ex);
            SetStatus("导出诊断失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 启动反复失败时的恢复助手：不是只留一行文字，而是给出四条真正能出去的出路。
    /// 「禁用最后安装的插件」直接对应"一个坏插件让整个 profile 起不来"这个最常见死法——
    /// dsh 的 Loader 只要有一个 entry 加载失败，整次启动就中止。
    /// </summary>
    private async Task ShowRecoveryAssistantAsync(string headline, string detail)
    {
        try
        {
            var profileDir = Profiles.Active.Directory;
            var profile = Profiles.Active.Name;
            var suspectAnalysis = DesktopRecovery.AnalyzeSuspectBundles(profileDir, profile, _recentStderr);
            var suspects = suspectAnalysis.Select(candidate => candidate.Bundle).ToList();
            var lastGood = DesktopRecovery.LastGoodSummary(profile);
            var canReEnable = DesktopRecovery.HasDisabledRecord(profileDir);

            var text = detail
                + (suspectAnalysis.Count > 0
                    ? "\n\n可疑插件及证据：\n" + DesktopRecovery.DescribeSuspects(suspectAnalysis)
                    : "\n\n没有发现「快照之后新装入」的插件。")
                + (lastGood != null ? "\n已提交的可用状态: " + lastGood : "\n还没有可回滚的可用快照。");

            var dialog = new RecoveryDialog(headline, text,
                canDisable: suspects.Count > 0,
                canRollback: lastGood != null,
                canReEnable: canReEnable);

            var action = dialog.ShowDialog(this);
            DesktopLog.Info("恢复助手选择: " + action);

            switch (action)
            {
                case RecoveryAction.OpenLogs:
                    OpenLogDirectory();
                    break;

                case RecoveryAction.DisableLastPlugin:
                    if (!ConfirmRecoveryMutation(
                        "禁用可疑插件？",
                        "这会从当前 profile 的 bundles 中移除：\n"
                        + string.Join(", ", suspects)
                        + "\n\n程序会先备份 package.json；之后将重启服务。",
                        "禁用并重启"))
                        return;
                    DesktopRecovery.DisableBundles(profileDir, profile, suspects, out var disabled);
                    SetStatus(disabled);
                    await RetryAfterRecoveryAsync();
                    break;

                case RecoveryAction.ReEnable:
                    if (!ConfirmRecoveryMutation(
                        "恢复被禁用的插件？",
                        "这会把上次由 DSH Desktop 禁用的插件重新写入当前 profile 的 bundles，并重启服务。",
                        "恢复并重启"))
                        return;
                    DesktopRecovery.ReEnableBundles(profileDir, out var enabled);
                    SetStatus(enabled);
                    await RetryAfterRecoveryAsync();
                    break;

                case RecoveryAction.Rollback:
                    if (!ConfirmRecoveryMutation(
                        "回滚到可用状态？",
                        "这会用最近一次已验证的启动快照覆盖当前 profile 清单。当前文件会先保存为备份；之后将重启服务。",
                        "回滚并重启"))
                        return;
                    DesktopRecovery.Rollback(profileDir, profile, out var rolled);
                    SetStatus(rolled);
                    await RetryAfterRecoveryAsync();
                    break;

                case RecoveryAction.Retry:
                    await RetryAfterRecoveryAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            DesktopLog.Error("恢复助手异常", ex);
        }
    }

    private bool ConfirmRecoveryMutation(string title, string detail, string primary)
        => AppDialog.Show(this, title, detail, primary, cancel: "取消", warning: true)
           == AppDialogResult.Primary;

    private async Task RetryAfterRecoveryAsync()
    {
        _recovery.PrepareManualRetry();
        StopServer();
        _authUrl = null;
        SetStatus("正在按恢复选择重启 dsh…");
        await StartAndEmbedAsync();
        if (_authUrl != null) BtnToggle.Content = "停止 DSH 服务";
    }

    private void FocusWindowAndWeb()
    {
        Activate();
        Topmost = true; Topmost = false;
        Dispatcher.BeginInvoke(new Action(ForceFocusIntoWeb), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void ForceFocusIntoWeb()
    {
        if (TuiPanel.Visibility == Visibility.Visible) return;
        if (!IsActive) Activate();
        Web.Focus();
        Keyboard.Focus(Web);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var decision = WebViewPolicy.ClassifyNavigation(e.Uri, _trustedWebOrigin);
        if (decision == WebViewPolicy.NavigationDecision.AllowInApp)
        {
            _pendingTrustedNavigationId = e.NavigationId;
            return;
        }

        e.Cancel = true;
        _policyCancelledNavigationId = e.NavigationId;
        if (decision == WebViewPolicy.NavigationDecision.OpenExternal)
        {
            DesktopLog.Info("WebView 外链已交给系统浏览器: " + DesktopLog.RedactUrl(e.Uri));
            OpenExternalUrl(e.Uri);
            return;
        }

        DesktopLog.Warn("WebView 已阻止不受信任的导航: " + DesktopLog.RedactUrl(e.Uri));
        SetTitlebarError("已阻止不受信任的页面在桌面窗口中打开。");
    }

    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (WebViewPolicy.ClassifyNavigation(e.Uri, _trustedWebOrigin, isMainFrame: false)
            == WebViewPolicy.NavigationDecision.AllowInApp)
            return;

        e.Cancel = true;
        DesktopLog.Warn("WebView 已阻止跨 Origin 子框架: " + DesktopLog.RedactUrl(e.Uri));
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Never create another privileged WebView. Same-origin requests reuse the owned window;
        // ordinary external links leave the application through the system browser.
        e.Handled = true;
        var decision = WebViewPolicy.ClassifyNavigation(e.Uri, _trustedWebOrigin);
        if (decision == WebViewPolicy.NavigationDecision.AllowInApp)
        {
            Web.CoreWebView2?.Navigate(e.Uri);
            return;
        }

        if (decision == WebViewPolicy.NavigationDecision.OpenExternal)
        {
            DesktopLog.Info("WebView 新窗口外链已交给系统浏览器: " + DesktopLog.RedactUrl(e.Uri));
            OpenExternalUrl(e.Uri);
            return;
        }

        DesktopLog.Warn("WebView 已阻止未知新窗口: " + DesktopLog.RedactUrl(e.Uri));
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var source = e.DownloadOperation.Uri;
        if (!WebViewPolicy.IsTrustedDownloadSource(source, _trustedWebOrigin))
        {
            e.Cancel = true;
            DesktopLog.Warn("WebView 已阻止非受信任来源下载: " + DesktopLog.RedactUrl(source));
            SetTitlebarError("已阻止非受信任来源的下载。");
            return;
        }

        var fileName = Path.GetFileName(e.ResultFilePath);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "下载文件";
        var answer = AppDialog.Show(this, "允许页面下载文件？",
            $"DSH 页面请求下载：\n{fileName}\n\n文件将保存到 WebView2 选择的下载位置。",
            primary: "允许下载", cancel: "取消", warning: true);
        if (answer == AppDialogResult.Primary)
        {
            DesktopLog.Info("用户允许 WebView 下载: " + fileName);
            return;
        }

        e.Cancel = true;
        DesktopLog.Info("用户取消 WebView 下载: " + fileName);
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        // The Harness UI currently requires no browser permission grants. Clipboard paste through
        // normal keyboard/input events remains available without granting script-level read access.
        e.State = CoreWebView2PermissionState.Deny;
        e.Handled = true;
        DesktopLog.Warn("WebView 权限请求已拒绝: kind=" + e.PermissionKind
            + " uri=" + DesktopLog.RedactUrl(e.Uri));
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_policyCancelledNavigationId == e.NavigationId)
        {
            _policyCancelledNavigationId = null;
            return;
        }

        if (!e.IsSuccess)
        {
            if (_pendingTrustedNavigationId == e.NavigationId) CancelFrontendHealthProbe();
            var failure = StartupFailure.Create(StartupFailure.Kind.NavigationFailed,
                e.WebErrorStatus + " / navId=" + e.NavigationId);
            DesktopLog.Warn("[startup/" + failure.Code + "] " + failure.Summary);
            // 不再提示"可点「重新加载」"——标题栏已无该按钮，重载入口是 F5 或
            // 「重载/重启」菜单里的「重载渲染器」。提示必须指向真实存在的操作。
            SetStatus(failure.Summary + "，可按 F5 或菜单「重载渲染器」重试。");
        }
        else if (!_healthyNavigationCommitted
                 && _pendingTrustedNavigationId == e.NavigationId
                 && WebViewPolicy.IsTrusted(Web.Source?.AbsoluteUri, _trustedWebOrigin)
                 && _serverHost.IsRunning)
        {
            BeginFrontendHealthProbe(e.NavigationId);
        }
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Web.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            ForceFocusIntoWeb();
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void BeginFrontendHealthProbe(ulong navigationId)
    {
        CancelFrontendHealthProbe();
        var cts = new System.Threading.CancellationTokenSource();
        _frontendHealthCts = cts;
        _ = ConfirmFrontendHealthyAsync(navigationId, _serverHost.Generation, cts);
    }

    private async Task ConfirmFrontendHealthyAsync(
        ulong navigationId,
        int serverGeneration,
        System.Threading.CancellationTokenSource cts)
    {
        var token = cts.Token;
        var deadline = Stopwatch.StartNew();
        var lastResult = FrontendHealthProbe.Result.Loading;
        Exception? lastError = null;
        try
        {
            while (deadline.Elapsed < TimeSpan.FromSeconds(45))
            {
                token.ThrowIfCancellationRequested();
                if (serverGeneration != _serverHost.Generation
                    || _pendingTrustedNavigationId != navigationId
                    || _healthyNavigationCommitted
                    || !_serverHost.IsRunning
                    || !WebViewPolicy.IsTrusted(Web.Source?.AbsoluteUri, _trustedWebOrigin))
                    return;

                try
                {
                    var core = Web.CoreWebView2;
                    if (core == null) return;
                    var raw = await core.ExecuteScriptAsync(FrontendHealthProbe.Script);
                    lastResult = FrontendHealthProbe.Parse(raw);
                    lastError = null;
                    if (lastResult == FrontendHealthProbe.Result.Healthy)
                    {
                        // LKG is deliberately committed only after backend readiness, a trusted
                        // successful navigation, and a visible interactive DSH surface all agree.
                        DesktopRecovery.CommitHealthy(Profiles.Active.Name, _port);
                        _healthyNavigationCommitted = true;
                        _pendingTrustedNavigationId = null;
                        _recovery.RecordHealthy();
                        SetStatus($"DSH 已就绪(端口 {_port})." + (_hostNotice == null ? "" : "\n" + _hostNotice));
                        _hostNotice = null;
                        DesktopLog.Info("DSH 前端交互面已就绪，已提交 LKG: origin=" + _trustedWebOrigin);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    // Renderer replacement during startup can make one probe fail transiently. Keep
                    // retrying within the bounded deadline and report only the terminal failure.
                    lastError = ex;
                    lastResult = FrontendHealthProbe.Result.Invalid;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), token);
            }

            var detail = FrontendHealthProbe.Describe(lastResult);
            if (lastError != null) detail += "；" + lastError.Message;
            var failure = StartupFailure.Create(StartupFailure.Kind.FrontendTimeout, detail);
            DesktopLog.Error("[startup/" + failure.Code + "] " + failure.Summary);
            SetStatus(failure.Summary);
            SetTitlebarError("前端启动失败，可重载渲染器或打开恢复助手。");
        }
        catch (OperationCanceledException)
        {
            // A restart, stop, or newer navigation superseded this generation.
        }
        catch (Exception ex)
        {
            var failure = StartupFailure.Create(StartupFailure.Kind.FrontendProbeFailed, ex.Message);
            DesktopLog.Error("[startup/" + failure.Code + "] " + failure.Summary, ex);
            SetStatus(failure.Summary);
            SetTitlebarError("前端健康确认失败，可重载渲染器或打开恢复助手。");
        }
        finally
        {
            if (ReferenceEquals(_frontendHealthCts, cts))
            {
                _frontendHealthCts = null;
                cts.Dispose();
            }
        }
    }

    private void CancelFrontendHealthProbe()
    {
        var cts = _frontendHealthCts;
        _frontendHealthCts = null;
        if (cts == null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        cts.Dispose();
    }

    private async Task<bool> WaitForFrontendHealthAsync(TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            if (_healthyNavigationCommitted) return true;
            if (!_serverHost.IsRunning) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
        return _healthyNavigationCommitted;
    }

    /// <summary>
    /// WebView2 的渲染/GPU 等子进程崩溃。此前这种情况表现为白屏，界面上看不出发生了什么；
    /// 现在把失败种类、退出码与原因记进日志，并尝试自动恢复。
    /// </summary>
    private void OnWebProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        DesktopLog.Error("WebView2 子进程失败: kind=" + e.ProcessFailedKind
            + " reason=" + e.Reason
            + " exitCode=" + e.ExitCode + " (0x" + unchecked((uint)e.ExitCode).ToString("X8") + ")");
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SetStatus("网页渲染进程异常退出(" + e.ProcessFailedKind + ")，正在恢复…");
            try { Web.Reload(); }
            catch (Exception ex) { DesktopLog.Error("恢复渲染进程失败", ex); }
        }));
    }

    private async void OnToggleServer(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_serverHost.IsRunning)
            {
                StopServer();
                BtnToggle.Content = "启动 DSH 服务";
                SetStatus("已停止 DSH 服务。");
                return;                     // 停止分支不抢焦点（与既有行为一致）
            }
            await StartAndEmbedAsync();
        }
        catch (Exception ex)
        {
            // StartAndEmbedAsync 内部已兜底；这里防的是 StopServer/UI 改写等同步段
            ReportActionFailure("启动/停止 DSH 服务", ex);
        }
        FocusWindowAndWeb();
    }

    private void OnReload(object sender, RoutedEventArgs e) => ReloadWeb();

    private void OnOpenExternal(object sender, RoutedEventArgs e)
    {
        var u = _authUrl ?? _serverHost.Endpoint;
        if (string.IsNullOrEmpty(u))
        {
            SetTitlebarError("服务尚未就绪，暂时没有可在浏览器中打开的地址。");
            return;
        }
        try { Process.Start(new ProcessStartInfo { FileName = u, UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    // ---------- 版本检测 / 更新 ----------

    /// <summary>读取实际运行的 dsh 捆绑包版本（来自 package.json）。</summary>
    private static string ReadBundledVersion()
        => Runtime.ReadDshVersion() ?? "未知";

    private void ShowCurrentVersion()
    {
        var runtime = Runtime.Resolve();
        var source = runtime.DshSource switch
        {
            RuntimeSource.Bundled => "内置",
            RuntimeSource.NpxCache => "npx 缓存",
            _ => "未找到",
        };
        var version = Runtime.ReadDshVersion(runtime) ?? "未知";

        // 版本号独立可点；「来源」不再挤进可见文本，改由 ToolTip 与版本浮层承担。
        BtnVersion.Content = $"dsh v{version}";
        BtnVersion.ToolTip = runtime.DshEntryPath == null
            ? "DeepSeek Harness 版本"
            : $"DeepSeek Harness 版本\n来源: {source}\n{runtime.InstallRoot}";
        VersionPopupValue.Text = version;
        // 「来源」在浮层里是独立一列：标签「来源」固定在左列（与「当前版本」同列），
        // 取值放在右列（与版本号同列、左缘必然重合），因此这里只赋值、不带「来源:」前缀。
        // 原先写作 `binJs == null ? $"来源: {source}" : $"来源: {source}"` ——
        // 三元式两个分支完全相同，属无效表达式，已清除。
        VersionPopupSource.Text = source;
    }

    /// <summary>
    /// 更新状态的唯一派生点：由 Core UpdateCoordinator 推出「有可用更新」的呈现。
    /// 状态栏那颗「检查更新」已移除，更新入口统一收敛到标题栏（版本胶囊浮层 + 铃铛），
    /// 因此这里只维护铃铛可见性、版本浮层的「查看更新详情」入口与检查按钮的进行态文案。
    /// </summary>
    private void ApplyUpdateState()
    {
        var update = _updates.Snapshot;
        var res = update.Candidate;
        var available = res != null;

        // busy 的唯一派生：检查/更新期间禁用全部标题栏动作；重启族期间禁用更新族与动作按钮，
        // 但保留版本浮层入口（恢复助手要靠它显示错误行）。
        var mode = _busyMode;
        var updateBusy = update.IsBusy || mode is "check" or "update";
        var actionBusy = mode is "restart" or "recover";

        BtnTui.IsEnabled = !updateBusy && !actionBusy;
        BtnRestartMenu.IsEnabled = !updateBusy && !actionBusy;
        BtnDevMenu.IsEnabled = !updateBusy && !actionBusy;
        BtnVersionCheck.IsEnabled = !updateBusy;
        BtnVersion.IsEnabled = !updateBusy;
        // 只改文案 TextBlock：按钮 Content 是「图标 + 文案」面板，整体改写会把刷新图标一起冲掉。
        BtnVersionCheckText.Text = update.Phase switch
        {
            UpdatePhase.Checking => "检查中…",
            UpdatePhase.Applying => "更新中…",
            _ => "检查更新",
        };

        if (updateBusy)
        {
            // 进行态不改徽标可见性：失败不得抹掉此前已发现的可用更新（R13）。
            return;
        }

        BtnUpdateBell.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        BtnVersionDetails.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        BtnUpdateBell.ToolTip = available ? $"发现新版本 v{res!.Newest}" : "发现新版本";

        UpdatePopup.IsOpen = false;
    }

    /// <summary>
    /// busy 门的唯一入口。检查/更新期间禁用**全部**标题栏动作（R16）；
    /// 重启族动作期间只禁用更新族与动作按钮本身，保留版本浮层（否则「重启到恢复模式」
    /// 打开恢复助手后，其错误行与检查更新入口会自我锁死）。
    /// </summary>
    private void BeginBusy(string mode)
    {
        _busyMode = mode;
        ApplyUpdateState();
    }

    private void EndBusy()
    {
        _busyMode = null;
        ApplyUpdateState();
    }

    /// <summary>清除版本浮层里的错误行（开始新动作时调用）。</summary>
    private void ResetVersionPopupError()
    {
        VersionPopupError.Text = "";
        VersionPopupError.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 检查 DeepSeek Harness 是否有新版本。
    /// <paramref name="manual"/> 为 true 时（用户点击"检查更新"）无论结果都更新状态栏；
    /// 自动检测（false）只在发现新版本时提示。
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual = false)
    {
        if (_busyMode is not null) return;
        if (!_updates.TryBeginCheck(manual)) return;

        try
        {
            BeginBusy("check");               // 单一 busy 门：禁用全部标题栏动作并派生文案
            ResetVersionPopupError();
            var current = ReadBundledVersion();
            VersionUpdate.Result? res;
            if (TryGetSignedReleaseSource(out var signedSource, out _) && signedSource != null)
            {
                var metadata = await FetchSignedReleaseDescriptorAsync(signedSource);
                var descriptor = metadata.Descriptor;
                res = metadata.Success && descriptor != null && IsSignedCandidateCompatible(descriptor)
                    ? new VersionUpdate.Result(
                        current,
                        descriptor.DshVersion,
                        descriptor.DshVersion,
                        VersionUpdate.IsNewer(descriptor.DshVersion, current),
                        string.Equals(descriptor.Channel, "stable", StringComparison.Ordinal))
                    : null;
            }
            else
            {
                res = await VersionUpdate.CheckAsync(current);
            }
            if (res == null)
            {
                _updates.FailCheck("网络不可用或 registry 响应无法解析");
                if (manual)
                {
                    SetStatus("检查更新失败（网络不可用或解析失败）。");
                    SetTitlebarError("检查更新失败：网络不可用或解析失败，可重试。");
                }
                // 刻意不在这里复位：失败不该抹掉此前已发现的可用更新
                //（旧实现在此调用 RestoreUpdateButton，把刚发现的更新状态覆盖掉了）。
                return;
            }

            var skipped = res.Available && _releaseSkips.IsSkipped(res.Newest);
            var candidate = res.Available && !skipped
                ? new UpdateCandidate(res.Current, res.Latest, res.Newest, res.StableUpdate)
                : null;
            _updates.CompleteCheck(candidate);
            ApplyUpdateState();

            if (res.Available && !skipped)
            {
                var channel = res.StableUpdate ? "稳定版" : "预发布版";
                if (manual)
                {
                    SetStatus($"发现{channel} v{res.Newest}，当前 v{res.Current}。");
                    ShowUpdatePopup();
                }
            }
            else if (manual)
            {
                SetStatus(skipped
                    ? "已跳过 v" + res.Newest + "，发现更高版本时会再次提示。"
                    : "DeepSeek Harness 已是最新版本（v" + res.Current + "）。");
            }
        }
        catch (Exception ex)
        {
            _updates.FailCheck(ex.Message);
            throw;
        }
        finally
        {
            EndBusy();                    // 回到「有更新」或「检查更新」，由状态唯一派生
        }
    }

    // ---------- 更新详情浮层（铃铛） ----------

    private string? _releaseUrl;
    private string? _notesLoadedVersion;

    private void OnToggleUpdatePopup(object sender, RoutedEventArgs e)
    {
        var open = !UpdatePopup.IsOpen;
        CloseAllTitlebarPopups();
        if (open) ShowUpdatePopup();
    }

    private void OnCloseUpdatePopup(object sender, RoutedEventArgs e)
    {
        CloseAllTitlebarPopups();
        FocusWindowAndWeb();
    }

    private void OnSkipUpdate(object sender, RoutedEventArgs e)
    {
        CloseAllTitlebarPopups();
        var candidate = _updates.Snapshot.Candidate;
        if (candidate == null || _updates.Snapshot.IsBusy)
        {
            SetStatus("当前没有可跳过的更新。");
            return;
        }

        try
        {
            _releaseSkips.Skip(candidate.Newest);
            if (_updates.TryDismissCandidate(out var dismissed) && dismissed != null)
            {
                ApplyUpdateState();
                SetStatus("已跳过 v" + dismissed.Newest + "，发现更高版本时会再次提示。");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("无法保存跳过版本设置：" + ex.Message);
            AppDialog.Show(this, "DSH 更新", "无法保存跳过版本设置，更新仍会继续提示。", primary: "知道了", warning: true);
        }
        finally
        {
            FocusWindowAndWeb();
        }
    }

    /// <summary>从版本浮层跳转到更新详情浮层。</summary>
    private void OnShowUpdateDetails(object sender, RoutedEventArgs e) => ShowUpdatePopup();

    private void ShowUpdatePopup()
    {
        var res = _updates.Snapshot.Candidate;
        if (res == null)
        {
            // 旧实现在此处静默 return：点击没有任何反馈。改为给出可见提示。
            SetStatus("当前没有可用的更新。");
            SetTitlebarError("当前没有可用的更新，可先「检查更新」。");
            return;
        }

        CloseAllTitlebarPopups();
        var channel = res.StableUpdate ? "稳定版" : "预发布版";
        // 版本号进胶囊、通道进圆点+副标题：标题固定为「发现新版本」，信息层级更清楚。
        PopupVersionChip.Text = "v" + res.Newest;
        PopupChannelDot.Fill = (Brush)FindResource(res.StableUpdate ? "UpdateChannelStableBrush" : "UpdateChannelPreBrush");
        PopupSubtitle.Text = $"当前 dsh v{res.Current} · {channel} · 先下载并验证，再确认安装";
        // The signed release feed is deliberately deployment-owned. A registry result alone
        // must never enable the legacy npm staging path.
        var signedSourceReady = TryGetSignedReleaseSource(out _, out _);
        BtnPopupUpdate.IsEnabled = signedSourceReady;
        BtnPopupUpdate.ToolTip = signedSourceReady
            ? "下载并验证已签名的运行时发布包"
            : "尚未配置受信任的签名更新通道";
        UpdatePopup.IsOpen = true;
        SetTitleBarTag(BtnVersion, true);   // UpdatePopup 锚定 BtnVersion，锚点态同步为展开
        _ = LoadReleaseNotesAsync(res.Newest);
    }

    /// <summary>取该版本的更新说明（GitHub Releases 优先，失败退回 npm 描述）。</summary>
    private async Task LoadReleaseNotesAsync(string version)
    {
        if (_notesLoadedVersion == version) return;
        _notesLoadedVersion = version;

        ShowNotesMessage("正在获取更新说明…");
        BtnPopupBrowser.IsEnabled = false;
        try
        {
            var info = await VersionUpdate.FetchReleaseInfoAsync(version);
            if (info == null)
            {
                _releaseUrl = VersionUpdate.ReleasesPageUrl;
                ShowNotesMessage("未能获取该版本的更新说明。点击下方“在浏览器中查看”打开发布页面。");
                _notesLoadedVersion = null;   // 允许下次打开重试
            }
            else
            {
                _releaseUrl = string.IsNullOrWhiteSpace(info.Url) ? VersionUpdate.ReleasesPageUrl : info.Url;
                var body = info.Body ?? "";
                if (string.IsNullOrWhiteSpace(CleanMarkdown(body)))
                    ShowNotesMessage($"v{version} 未提供文字说明。点击“在浏览器中查看”打开发布页面。");
                else
                    ShowNotesMarkdown(body);
            }
        }
        catch
        {
            _releaseUrl = VersionUpdate.ReleasesPageUrl;
            ShowNotesMessage("获取更新说明失败。点击下方“在浏览器中查看”打开发布页面。");
            _notesLoadedVersion = null;
        }
        finally
        {
            BtnPopupBrowser.IsEnabled = true;
        }
    }

    private void OnOpenReleasePage(object sender, RoutedEventArgs e)
    {
        var url = string.IsNullOrWhiteSpace(_releaseUrl) ? VersionUpdate.ReleasesPageUrl : _releaseUrl!;
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    /// <summary>把 Release 说明的 Markdown 简化为纯文本（去标题符号与粗体/代码标记）。</summary>
    private static string CleanMarkdown(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) line = trimmed.TrimStart('#').TrimStart();
            line = line.Replace("**", "").Replace("`", "");
            sb.AppendLine(line);
        }
        return sb.ToString().Trim();
    }

    // ---------------------------------------------------------------- 更新说明排版

    /// <summary>
    /// 单段提示（加载中 / 取不到说明 / 出错）——说明区里只有一段文字时的形态。
    /// </summary>
    private void ShowNotesMessage(string message)
    {
        PopupNotesPanel.Children.Clear();
        PopupNotesPanel.Children.Add(new TextBlock
        {
            Text = message,
            Style = (Style)FindResource("UpdateNotesBody"),
        });
    }

    /// <summary>
    /// 把 Release 正文排版成块：<c>&lt;h3 id=…&gt;</c>/<c>###</c> → 标题（带品牌色左条）、
    /// <c>- …</c> → 项目符号（悬挂缩进）、<c>---</c> → 分隔线、其余 → 正文；
    /// 行内的 <c>**粗体**</c>、反引号代码、<c>[文字](链接)</c>、<c>@用户</c> 各自成内联。
    ///
    /// 旧实现只把整段文本去 <c>#</c> 与 <c>**</c> 后塞进一个 TextBlock，于是 GitHub 生成的
    /// Release 正文（含 <c>&lt;h3 id="cn-…"&gt;</c> 与 <c>[中文](#cn-…)</c>）原样显示成乱码墙
    /// （2026-09-12 反馈）。锚点链接对桌面浮层没有意义，整行丢弃。
    /// </summary>
    private void ShowNotesMarkdown(string markdown)
    {
        PopupNotesPanel.Children.Clear();
        var text = (markdown ?? "").Replace("\r\n", "\n");

        void AddHeading(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var row = new Grid { Margin = new Thickness(0, 10, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var bar = new Border
            {
                Width = 2,
                Background = (Brush)FindResource("UpdateAccentBrush"),
                CornerRadius = new CornerRadius(1),
                Margin = new Thickness(0, 1, 8, 1),
            };
            Grid.SetColumn(bar, 0);
            row.Children.Add(bar);
            var label = new TextBlock
            {
                Style = (Style)FindResource("UpdateNotesHeading"),
                Margin = new Thickness(0),
            };
            AppendInlines(label.Inlines, value);
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            PopupNotesPanel.Children.Add(row);
        }

        void AddListLine(string marker, string value)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var bullet = new TextBlock { Text = marker, Style = (Style)FindResource("UpdateNotesBullet") };
            Grid.SetColumn(bullet, 0);
            row.Children.Add(bullet);
            var body = new TextBlock
            {
                Style = (Style)FindResource("UpdateNotesBody"),
                Margin = new Thickness(0, 0, 0, 0),
            };
            AppendInlines(body.Inlines, value);
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            PopupNotesPanel.Children.Add(row);
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // 纯锚点链接行（[中文](#cn-…) | [English](#en-…)）在桌面浮层里没有意义
            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^(?:\[[^\]]*\]\(#[^)]*\)\s*(\||·)?\s*)+$"))
                continue;

            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^(-{3,}|\*{3,}|_{3,})$"))
            {
                PopupNotesPanel.Children.Add(new Border
                {
                    Height = 1,
                    Background = (Brush)FindResource("DividerBrush"),
                    Margin = new Thickness(0, 10, 0, 10),
                });
                continue;
            }

            // HTML 标题（GitHub 生成正文的 <h3 id="…">标题</h3>）
            var htmlHeading = System.Text.RegularExpressions.Regex.Match(line, @"<h[1-6][^>]*>(?<t>.*?)</h[1-6]>");
            if (htmlHeading.Success) { AddHeading(StripTags(htmlHeading.Groups["t"].Value)); continue; }

            // Markdown 标题
            if (line.StartsWith('#')) { AddHeading(StripTags(line.TrimStart('#').Trim())); continue; }

            var bullet = System.Text.RegularExpressions.Regex.Match(line, @"^[-*+]\s+(?<t>.+)$");
            if (bullet.Success) { AddListLine("•", StripTags(bullet.Groups["t"].Value)); continue; }

            var ordered = System.Text.RegularExpressions.Regex.Match(line, @"^(?<n>\d{1,3})[.)]\s+(?<t>.+)$");
            if (ordered.Success) { AddListLine(ordered.Groups["n"].Value + ".", StripTags(ordered.Groups["t"].Value)); continue; }

            var paragraph = new TextBlock { Style = (Style)FindResource("UpdateNotesBody") };
            AppendInlines(paragraph.Inlines, StripTags(line));
            PopupNotesPanel.Children.Add(paragraph);
        }
    }

    /// <summary>去掉 HTML 标签（保留标签内的文字），并还原常见实体。</summary>
    private static string StripTags(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var stripped = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", "");
        return stripped
            .Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<")
            .Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'");
    }

    // 行内语法：**粗体** / `代码` / [文字](链接) / @用户。
    private static readonly System.Text.RegularExpressions.Regex InlinePattern = new(
        @"\*\*(?<b>.+?)\*\*|`(?<c>[^`]+)`|\[(?<lt>[^\]]*)\]\((?<lu>[^)]*)\)|(?<at>@[A-Za-z0-9_\-]+)");

    /// <summary>
    /// 行内解析：<c>**粗体**</c> → Bold、<c>`code`</c> → 等宽、<c>[文字](http…)</c> → 可点链接
    /// （锚点链接只保留文字）、<c>@用户</c> → 品牌色。链接一律交给系统浏览器打开。
    /// </summary>
    private void AppendInlines(System.Windows.Documents.InlineCollection target, string text)
    {
        var index = 0;
        foreach (System.Text.RegularExpressions.Match match in InlinePattern.Matches(text))
        {
            if (match.Index > index) target.Add(new System.Windows.Documents.Run(text[index..match.Index]));
            if (match.Groups["b"].Success)
            {
                target.Add(new System.Windows.Documents.Bold(new System.Windows.Documents.Run(match.Groups["b"].Value)));
            }
            else if (match.Groups["c"].Success)
            {
                target.Add(new System.Windows.Documents.Run(match.Groups["c"].Value)
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                    Foreground = (Brush)FindResource("UpdateChipTextBrush"),
                });
            }
            else if (match.Groups["lt"].Success)
            {
                var url = match.Groups["lu"].Value.Trim();
                var label = match.Groups["lt"].Value;
                if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(label))
                    {
                        Foreground = (Brush)FindResource("UpdateAccentBrush"),
                        TextDecorations = null,
                    };
                    var destination = url;   // 捕获局部量，避免闭包引用循环变量
                    link.RequestNavigate += (_, __) => OpenExternalUrl(destination);
                    target.Add(link);
                }
                else if (label.Length > 0)
                {
                    target.Add(new System.Windows.Documents.Run(label));
                }
            }
            else if (match.Groups["at"].Success)
            {
                target.Add(new System.Windows.Documents.Run(match.Groups["at"].Value)
                {
                    Foreground = (Brush)FindResource("UpdateChipTextBrush"),
                });
            }
            index = match.Index + match.Length;
        }
        if (index < text.Length) target.Add(new System.Windows.Documents.Run(text[index..]));
    }

    /// <summary>用系统默认浏览器打开 URL（失败时静默，绝不因外链阻断浮层）。</summary>
    private static void OpenExternalUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        try
        {
            await CheckForUpdatesAsync(manual: true);
        }
        catch (Exception ex)
        {
            ReportActionFailure("检查更新", ex);
        }
        FocusWindowAndWeb();
    }

    /// <summary>停止当前由本窗口启动的 dsh 子进程（含整棵进程树），并等待其退出以释放文件锁。</summary>
    private void StopServer()
    {
        CancelFrontendHealthProbe();
        try
        {
            _serverHost.Stop(TimeSpan.FromSeconds(5));
        }
        finally
        {
            // 我们主动停了服务 → 归属记录随之作废，否则下次启动会把它当"遗留孤儿"提示接管。
            ServerState.Clear(DesktopRecovery.StateRoot, Profiles.Active.Name);
        }
    }

    // ---------- 服务归属：启动时防止同时运行两个服务 ----------

    /// <summary>记下这次托管的子进程归属（pid / 端口 / 启动时刻），供下次启动识别遗留孤儿。</summary>
    private void RememberServerOwner()
    {
        try
        {
            var snapshot = _serverHost.Snapshot;
            if (snapshot.ProcessId is not { } processId || snapshot.StartedAt is not { } startedAt) return;
            ServerState.Write(DesktopRecovery.StateRoot, Profiles.Active.Name,
                new ServerRecord(processId, _port, startedAt));
        }
        catch (Exception ex)
        {
            // 记不下来只影响"下次启动能否识别遗留"，绝不能挡住本次启动。
            DesktopLog.Warn("写入服务归属记录失败: " + DesktopLog.Describe(ex));
        }
    }

    /// <summary>归属记录中动态端口的占用者是谁（空闲 / 自己上次的遗留 / 别人的）。</summary>
    private static PortOwner ResolvePortOwner(int port)
    {
        if (!PortInUse(port)) return PortOwner.Free;
        var record = ServerState.Read(DesktopRecovery.StateRoot, Profiles.Active.Name);
        return ServerState.Classify(port, portInUse: true, record, EvidenceFor(record));
    }

    /// <summary>
    /// 收集归属记录里那个 pid 的存活证据：存在、是 node.exe、启动时刻与记录吻合（±2s）。
    /// 任何一项取不到都返回 false——宁可把"自己的遗留"误判成"别人的"（顺延端口，无害），
    /// 也不能把别人的进程误判成自己的（会被杀）。
    /// </summary>
    private static ProcessEvidence EvidenceFor(ServerRecord? record)
    {
        if (record == null) return new ProcessEvidence(false, false, false);
        try
        {
            using var proc = Process.GetProcessById(record.Pid);
            if (proc.HasExited) return new ProcessEvidence(false, false, false);
            var isNode = string.Equals(proc.ProcessName, "node", StringComparison.OrdinalIgnoreCase);
            var matches = false;
            try
            {
                matches = Math.Abs((proc.StartTime.ToUniversalTime() - record.StartedAt).TotalSeconds) <= 2;
            }
            catch { /* 取不到启动时间 → 不算吻合 */ }
            return new ProcessEvidence(true, isNode, matches);
        }
        catch
        {
            return new ProcessEvidence(false, false, false);
        }
    }

    /// <summary>结束遗留服务并等其记录端口释放；返回端口是否真的空了。</summary>
    private static bool TakeOverLeftover(int pid, int port)
    {
        if (pid > 0)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                DesktopLog.Warn("结束遗留服务失败: " + DesktopLog.Describe(ex));
            }
        }
        for (var i = 0; i < 20 && PortInUse(port); i++) System.Threading.Thread.Sleep(250);
        return !PortInUse(port);
    }

    /// <summary>透明热重载：停止旧进程，用最新版本重新启动并刷新内嵌网页。</summary>
    private async Task RestartServerAsync()
    {
        StopServer();
        _authUrl = null;
        await StartAndEmbedAsync();
    }

    /// <summary>点击"立即更新"：运行更新指令，成功后透明热重载内嵌 dsh。</summary>
    private async Task ApplyUpdateAsync()
    {
        const string signedReleaseRequired =
            "在线更新尚不可用：此安装尚未配置受信任的签名发布通道。请等待正式发行包或联系发布者。";
        if (!TryGetSignedReleaseSource(out var signedSource, out var configurationError)
            || signedSource == null)
        {
            var message = signedReleaseRequired + "（" + (configurationError ?? "configuration-missing") + "）";
            SetStatus(message);
            AppDialog.Show(this, "DSH 更新", message, primary: "知道了", warning: true);
            return;
        }

        // 入口并发门：busy 期间不得二次进入更新。仅靠按钮禁用不足以防住
        // 「更新中再点铃铛重开浮层、再点『立即更新』」这条路径——浮层里的
        // BtnPopupUpdate 不参与标题栏的 IsEnabled 门控。
        if (_busyMode is not null)
        {
            SetStatus("已有动作正在进行，请稍后再试。");
            return;
        }

        if (!_updates.TryBeginApply(out var candidate) || candidate == null)
        {
            SetStatus("没有可更新的版本。");
            return;
        }
        var target = candidate.Newest;

        System.Threading.CancellationTokenSource? releaseUpdateCts = null;
        try
        {
            BeginBusy("update");             // 单一 busy 门：禁用全部标题栏动作并派生文案
            SetStatus("正在停止 DSH 服务以更新…");
            var beforeModels = await ReadModelNamesAsync();   // 更新前快照（尽力而为）
            StopServer();

            SetStatus("正在下载并验证已签名的运行时发布包…");
            releaseUpdateCts = new System.Threading.CancellationTokenSource();
            _releaseUpdateCts = releaseUpdateCts;
            var result = await RunSignedReleaseUpdateAsync(signedSource, releaseUpdateCts.Token);
            if (result.Success)
            {
                if (!string.IsNullOrWhiteSpace(result.Adoption?.MaintenanceWarning))
                    DesktopLog.Warn(result.Adoption.MaintenanceWarning);
                _pendingUpdateRuntimeId = result.Adoption?.RuntimeId;
                Runtime.Invalidate();
                ShowCurrentVersion();
                SetStatus("候选槽已激活，正在执行前端健康验证…");
                await StartAndEmbedAsync();
                var healthy = await WaitForFrontendHealthAsync(TimeSpan.FromSeconds(50));
                if (!healthy)
                {
                    StopServer();
                    var slots = new RuntimeSlotManager(Runtime.WritableRuntimeRoot);
                    var rollback = slots.Rollback();
                    RuntimeSlotQuarantineResult? quarantine = null;
                    if (rollback.Success && result.Adoption?.RuntimeId != null)
                        quarantine = slots.QuarantineInactive(result.Adoption.RuntimeId);
                    Runtime.Invalidate();
                    var rollbackHealthy = false;
                    if (rollback.Success)
                    {
                        await StartAndEmbedAsync();
                        rollbackHealthy = await WaitForFrontendHealthAsync(TimeSpan.FromSeconds(50));
                    }

                    var rollbackDetail = rollback.Success
                        ? rollbackHealthy
                            ? "已回退并恢复上一已验证槽。"
                            : "已切回上一槽，但旧槽未通过前端健康验证。"
                        : "自动回退失败：" + rollback.Error;
                    if (quarantine is { Success: true })
                        rollbackDetail += " 失败候选已移入隔离区。";
                    else if (quarantine is { Success: false })
                        rollbackDetail += " 隔离失败候选时出错：" + quarantine.Error;
                    var error = "新运行时槽未通过前端健康验证。" + rollbackDetail;
                    _pendingUpdateRuntimeId = null;
                    _updates.FailApply(error);
                    ApplyUpdateState();
                    DesktopLog.Error("更新健康门失败: " + error);
                    SetStatus(error);
                    AppDialog.Show(this, "DSH 更新", error, primary: "知道了", warning: true);
                    return;
                }

                var installedVersion = result.Acquisition?.Descriptor?.DshVersion ?? target;
                _pendingUpdateRuntimeId = null;
                _updates.CompleteApply(installedVersion);
                ApplyUpdateState();
                SetStatus("已更新到 v" + installedVersion + "，新运行时槽已通过健康验证。");
                await NotifyNewModelsAsync(beforeModels);   // 更新后自动查找新增模型
            }
            else if (result.Status == RuntimeReleaseUpdateStatus.Cancelled)
            {
                Runtime.Invalidate();
                var message = result.Error ?? (result.Acquisition?.Success == true
                    ? "已下载并验证候选运行时；你选择暂不安装，当前活动槽保持不变。"
                    : "更新已取消，当前活动槽保持不变。");
                _updates.FailApply(message);
                ApplyUpdateState();
                SetStatus(message);
                await StartAndEmbedAsync();
            }
            else
            {
                Runtime.Invalidate();
                var error = result.Error ?? result.Status.ToString();
                _updates.FailApply(error);
                ApplyUpdateState();
                SetStatus("更新失败，活动槽未被候选覆盖：" + error);
                AppDialog.Show(this, "DSH 更新",
                    "更新失败，当前已验证槽保持不变：\n\n" + error,
                    primary: "知道了", warning: true);
                await StartAndEmbedAsync();
            }
        }
        finally
        {
            if (ReferenceEquals(_releaseUpdateCts, releaseUpdateCts))
                _releaseUpdateCts = null;
            releaseUpdateCts?.Dispose();
            if (_updates.Snapshot.Phase == UpdatePhase.Applying)
                _updates.FailApply("更新流程未完成");
            EndBusy();                   // 更新成功→「检查更新」；失败→保留「有更新」以便重试
        }
    }

    private static bool TryGetSignedReleaseSource(
        out RuntimeReleaseSource? source,
        out string? error)
    {
        source = null;
        if (!RuntimeReleaseFeedConfiguration.TryLoadConfigurationFile(
                Environment.GetEnvironmentVariable("DSH_DESKTOP_RELEASE_FEED_CONFIG"),
                out var configuration,
                out error)
            || configuration == null)
        {
            return false;
        }

        try
        {
            source = configuration.ToSource(Environment.GetEnvironmentVariable("DSH_DESKTOP_RELEASE_CHANNEL"));
            return true;
        }
        catch (InvalidDataException)
        {
            error = "configuration-channel";
            return false;
        }
    }

    private async Task<RuntimeReleaseUpdateResult> RunSignedReleaseUpdateAsync(
        RuntimeReleaseSource source,
        System.Threading.CancellationToken cancellationToken)
    {
        var current = Runtime.Resolve();
        if (!current.IsVerified || string.IsNullOrWhiteSpace(current.RuntimeManifestPath))
        {
            return new RuntimeReleaseUpdateResult(
                RuntimeReleaseUpdateStatus.InvalidRequest,
                Error: "当前运行时不具备受验证的宿主身份，拒绝接纳在线候选。");
        }
        RuntimeManifest currentManifest;
        try
        {
            currentManifest = RuntimeManifest.Load(current.RuntimeManifestPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new RuntimeReleaseUpdateResult(
                RuntimeReleaseUpdateStatus.InvalidRequest,
                Error: "无法读取当前受验证运行时身份：" + ex.Message);
        }

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var acquirer = new RuntimeReleaseAcquirer(
            new RuntimeReleaseFeedClient(http),
            new RuntimePayloadExtractor());
        var updater = new RuntimeReleaseUpdater(Runtime.WritableRuntimeRoot, acquirer);
        var acquisition = await updater.AcquireAsync(
            source,
            timeout.Token,
            descriptor =>
            {
                var compatibility = RuntimeReleaseCompatibility.Evaluate(currentManifest, descriptor);
                return compatibility.IsCompatible
                    ? null
                    : "签名候选与当前宿主不兼容：" + string.Join(",", compatibility.Mismatches);
            });
        if (!acquisition.Success)
            return updater.ActivateAcquiredCandidate(acquisition);

        var descriptor = acquisition.Descriptor!;
        SetStatus("候选运行时已下载并验证，等待安装确认…");
        var answer = AppDialog.Show(
            this,
            "安装 DSH 更新",
            "已下载并验证签名候选运行时 v" + descriptor.DshVersion
                + "。安装将切换运行时槽并重载 DSH 服务；失败时会自动回退。",
            primary: "安装更新",
            cancel: "暂不安装");
        if (answer != AppDialogResult.Primary)
        {
            var discarded = updater.DiscardAcquiredCandidate(acquisition);
            return new RuntimeReleaseUpdateResult(
                RuntimeReleaseUpdateStatus.Cancelled,
                acquisition,
                Error: discarded
                    ? "已丢弃已下载的候选运行时，当前活动槽保持不变。"
                    : "用户暂不安装已下载的候选运行时；候选保留在 staging 中等待安全清理。");
        }

        SetStatus("正在安装已验证的候选运行时…");
        return updater.ActivateAcquiredCandidate(acquisition);
    }

    private static async Task<RuntimeReleaseMetadataResult> FetchSignedReleaseDescriptorAsync(
        RuntimeReleaseSource source)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        return await new RuntimeReleaseFeedClient(http).FetchVerifiedDescriptorAsync(
            source.MetadataUri,
            source.TrustedPublicKeys,
            source.Channel);
    }

    private static bool IsSignedCandidateCompatible(RuntimeReleaseDescriptor descriptor)
    {
        var current = Runtime.Resolve();
        if (!current.IsVerified || string.IsNullOrWhiteSpace(current.RuntimeManifestPath)) return false;
        try
        {
            return RuntimeReleaseCompatibility.Evaluate(
                RuntimeManifest.Load(current.RuntimeManifestPath),
                descriptor).IsCompatible;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async void OnApplyUpdate(object sender, RoutedEventArgs e)
    {
        CloseAllTitlebarPopups();
        try
        {
            await ApplyUpdateAsync();
        }
        catch (Exception ex)
        {
            // ApplyUpdateAsync 的 finally 会释放 busy 门；这里只负责让失败可见、不升级为退出
            ReportActionFailure("更新", ex);
        }
        FocusWindowAndWeb();
    }

    /// <summary>在 WebView 里尽力读取 Harness 模型选择器当前渲染的模型（名称）。
    /// 模型 ID 只是 React key、不在 DOM，因此用展示名/标题对比；选择器未渲染时返回空列表。</summary>
    private async Task<List<string>> ReadModelNamesAsync()
    {
        try
        {
            const string js = @"(() => {
                var set = new Set();
                for (var el of document.querySelectorAll('button[role=""menuitemradio""], [data-model-id]')) {
                    var id = el.getAttribute('data-model-id') || el.getAttribute('title') || (el.textContent || '').trim();
                    if (id) set.add(id);
                }
                return { models: Array.from(set) };
            })();";
            var json = await Web.CoreWebView2!.ExecuteScriptAsync(js);
            if (string.IsNullOrWhiteSpace(json) || json == "null") return new List<string>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("models", out var arr) && arr.ValueKind == JsonValueKind.Array)
                return arr.EnumerateArray()
                    .Select(x => x.GetString() ?? "")
                    .Where(s => s.Length > 0)
                    .Distinct()
                    .ToList();
        }
        catch { /* 尽力而为，读不到就当没模型 */ }
        return new List<string>();
    }

    /// <summary>更新后自动对比模型快照，发现新增模型则提示。仅在拿到有效基线(更新前非空)时判定，避免误报。</summary>
    private async Task NotifyNewModelsAsync(IReadOnlyCollection<string> before)
    {
        try
        {
            // 给新页面渲染模型选择器一点时间，多试几次
            List<string> after = new();
            for (var i = 0; i < 6; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                after = await ReadModelNamesAsync();
                if (after.Count > 0) break;
            }
            if (before.Count == 0 || after.Count == 0) return;   // 无有效基线或读不到，不误报

            var newModels = after.Except(before, StringComparer.OrdinalIgnoreCase).ToList();
            if (newModels.Count == 0) return;
            SetStatus("发现新增模型：" + string.Join("、", newModels));
            AppDialog.Show(this, "发现新模型",
                "Harness 已更新，检测到新增模型：\n\n" + string.Join("\n", newModels.Select(m => "• " + m)),
                primary: "知道了");
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// 更新进行中关闭窗口的显式裁决。签名发布只写 staging，但下载/解包任务仍属于
    /// 当前窗口；确认关闭时取消该任务，活动槽不会被原地修改。
    /// </summary>
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busyMode != "update") return;

        var choice = AppDialog.Show(this, "DSH Desktop",
            "更新正在 staging 中进行。确认关闭会取消下载或解包任务，"
            + "且后续校验、槽切换和 staging 清理不会继续执行；当前活动槽不会被修改。\n\n"
            + "建议等更新结束后再关闭。",
            primary: "仍要关闭", cancel: "继续等待", warning: true);
        if (choice != AppDialogResult.Primary)
        {
            e.Cancel = true;
            SetStatus("更新进行中，已取消关闭。更新完成后可再次关闭窗口。");
            return;
        }
        _releaseUpdateCts?.Cancel();
        RollBackPendingUpdateForExit();
        DesktopLog.Warn("更新进行中用户确认关闭：活动槽保持不变，但可能留下未完成 staging，稍后需清理或重试。");
    }

    private void RollBackPendingUpdateForExit()
    {
        var runtimeId = _pendingUpdateRuntimeId;
        if (string.IsNullOrWhiteSpace(runtimeId)) return;
        _pendingUpdateRuntimeId = null;
        try
        {
            StopServer();
            var slots = new RuntimeSlotManager(Runtime.WritableRuntimeRoot);
            var rollback = slots.Rollback();
            var quarantine = rollback.Success
                ? slots.QuarantineInactive(runtimeId)
                : null;
            Runtime.Invalidate();
            DesktopLog.Warn(rollback.Success
                ? "关闭前已回退未经健康验证的候选运行时槽。"
                : "关闭前无法回退未经健康验证的候选运行时槽：" + rollback.Error);
            if (quarantine is { Success: false })
                DesktopLog.Warn("关闭前隔离候选运行时槽失败：" + quarantine.Error);
        }
        catch (Exception ex)
        {
            DesktopLog.Warn("关闭前回退候选运行时槽失败：" + DesktopLog.Describe(ex));
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        CancelFrontendHealthProbe();
        try { CloseAllTitlebarPopups(); } catch { /* ignore */ }
        _trayController.Dispose();
        try { TuiView.Stop(); } catch { /* ignore */ }
        if (StopOnClose.IsChecked == true && _serverHost.IsRunning)
            _serverHost.Stop(TimeSpan.Zero);
    }
}
