using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
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
using Microsoft.Web.WebView2.Core;
using WinForms = System.Windows.Forms;

namespace DSHDesktop;

public partial class MainWindow : Window
{
    private const int DefaultPort = 3080;

    private Process? _serverProc;
    private bool _startedByUs;
    private int _port = DefaultPort;
    private string? _authUrl;
    private TaskCompletionSource<string> _urlTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _updateChecked;
    private VersionUpdate.Result? _lastUpdate;

    /// <summary>
    /// 检查更新的并发门（检查与更新共享）。旧实现只拦自动检查，手动检查可并发，
    /// 于是「检查中…」与「更新中…」两处文案会互相覆盖、finally 还会错误放行按钮。
    /// </summary>
    private bool _checkingForUpdates;

    /// <summary>
    /// 当前进行中的标题栏动作（null = 空闲）："check" / "update" / "restart" / "recover"。
    /// 与 <see cref="_checkingForUpdates"/> 一起构成 busy 门，避免「更新写入 node_modules 的同时
    /// 用户点重启、StopServer 把进程杀掉」这类叠加态（R16）。
    /// </summary>
    private string? _busyMode;

    private bool _tuiStarted;
    private string? _tuiTitle;

    /// <summary>主动停止中的标记：为真时 dsh 子进程退出不再触发自动重启。</summary>
    private volatile bool _stoppingServer;

    /// <summary>进程代数：每次主动停止/启动递增，用来丢弃过期的退出回调。</summary>
    private int _serverEpoch;

    /// <summary>自动重启次数（成功启动后归零）。</summary>
    private int _autoRestarts;

    private const int MaxAutoRestarts = 3;

    /// <summary>最近的 dsh stderr 行（有上限）。失败时把这段上下文一并写进日志，省掉"再复现一次"。</summary>
    private readonly System.Collections.Generic.Queue<string> _recentStderr = new();

    private const int RecentStderrLines = 20;

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
        InitializeComponent();
        Title = "DSH Desktop";
        MarketSupport.ActiveProfile = "web";   // 与 StartAndEmbedAsync 启动的 profile 一致
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
            Web.NavigationCompleted += OnNavigationCompleted;
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

    // ---------- 定位 dsh 启动脚本 / node ----------
    // 优先使用与主程序捆绑的自包含运行时（runtime\），实现“点开即用、不依赖系统 node/npx”。
    // 找不到捆绑目录时才回退到外部安装（开发调试用）。

    private static string BundleDir =>
        Path.Combine(AppContext.BaseDirectory, "runtime");

    private static string? BundledNodeExe()
    {
        var f = Path.Combine(BundleDir, "node", "node.exe");
        return File.Exists(f) ? f : null;
    }

    private static string? BundledDshBinJs()
    {
        var f = Path.Combine(BundleDir, "dsh", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        return File.Exists(f) ? f : null;
    }

    private static string? DshNodeBinJs()
    {
        var bundled = BundledDshBinJs();
        if (bundled != null) return bundled;
        var npx = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"npm-cache\_npx\1e7f6d9597241db0");
        var bin = Path.Combine(npx, @"node_modules\@deepseek-ai\dsh\lib\bin.js");
        return File.Exists(bin) ? bin : null;
    }

    internal static string? FindNode()
    {
        var bundled = BundledNodeExe();
        if (bundled != null) return bundled;
        var envNode = Environment.GetEnvironmentVariable("DSH_NODE");
        if (!string.IsNullOrEmpty(envNode) && File.Exists(envNode)) return envNode;
        foreach (var p in new[] { @"D:\nodejs\node.exe", @"C:\Program Files\nodejs\node.exe", @"C:\nodejs\node.exe" })
            if (File.Exists(p)) return p;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { var f = Path.Combine(dir, "node.exe"); if (File.Exists(f)) return f; } catch { /* ignore */ }
        }
        return null;
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
        if (_startedByUs && _serverProc != null && !_serverProc.HasExited) return;

        var binJs = DshNodeBinJs();
        var node = FindNode();
        if (binJs == null) { SetStatus("未找到 dsh 启动脚本（bin.js）。请先安装 @deepseek-ai/dsh。"); return; }
        if (node == null) { SetStatus("未找到 node.exe，请安装 Node.js。"); return; }

        // 启动前先看默认端口上是不是**本窗口上次遗留的** dsh 服务。旧逻辑对"端口被占"
        // 一律顺延（_port++），于是孤儿服务还在跑、这边又起一个——正是"多运行服务"。
        // 现在只有确认为自己的遗留（记录吻合 + pid 存活 + 是 node + 启动时刻吻合）才提示，
        // 别人的占用照旧顺延，不做任何破坏性动作。
        if (ResolvePortOwner(DefaultPort) == PortOwner.OwnLeftover)
        {
            var record = ServerState.Read(DesktopRecovery.StateRoot, MarketSupport.ActiveProfile);
            var answer = AppDialog.Show(this, "发现上次遗留的 DSH 服务",
                $"默认端口 {DefaultPort} 上已有一个上次遗留的 DSH 服务（PID {record?.Pid}）。\n\n" +
                "为避免同时运行两个服务：\n" +
                "· 「接管并重启」：结束它，并由本窗口在同一端口重新启动（推荐）\n" +
                "· 「顺延端口」：保留它，本窗口改用其他端口（会同时有两个服务）\n" +
                "· 「取消启动」：这次不启动服务，稍后可从菜单「重启桌面端」重试",
                primary: "接管并重启", secondary: "顺延端口", cancel: "取消启动", warning: true);
            if (answer == AppDialogResult.Cancel)
            {
                SetStatus($"已取消启动（端口 {DefaultPort} 上仍有上次遗留的服务）。");
                SetTitlebarError("端口被上次遗留的 DSH 服务占用，启动已取消。可从菜单「重启桌面端」重试。");
                return;
            }
            if (answer == AppDialogResult.Primary)
            {
                SetStatus($"正在结束遗留服务（PID {record?.Pid}）…");
                var freed = TakeOverLeftover(record?.Pid ?? 0);
                ServerState.Clear(DesktopRecovery.StateRoot, MarketSupport.ActiveProfile);
                SetStatus(freed
                    ? $"遗留服务已结束，正在端口 {DefaultPort} 重新启动…"
                    : "遗留服务已结束，但端口仍被占用，将顺延端口。");
            }
            else
            {
                SetStatus($"保留端口 {DefaultPort} 上的既有服务，本窗口顺延端口（将同时运行两个服务）。");
            }
        }

        // 选一个空闲端口（默认 3080；被占用则顺延）
        _port = DefaultPort;
        for (var i = 0; i < 30 && PortInUse(_port); i++) _port++;
        if (PortInUse(_port)) { SetStatus("找不到可用端口。"); return; }
        if (_port != DefaultPort)
            DesktopLog.Info($"默认端口 {DefaultPort} 被占用（非本窗口遗留），改用 {_port}");

        var psi = new ProcessStartInfo(node)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 工作目录放到 launcher 安装根目录，而不是 @deepseek-ai\dsh\lib，
            // 避免 dsh 进程占住 node_modules 目录导致 npm 更新时 EBUSY。
            WorkingDirectory = LauncherInstallDir() ?? AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add(binJs);
        psi.ArgumentList.Add("web");
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
            seedNotice = ProfileSeed.Apply(MarketSupport.DshHome(), MarketSupport.ActiveProfile).Notice;
            return MarketSupport.HostEnvironment(CachedRegistryOverride(), Environment.GetEnvironmentVariable("PATH"));
        });
        foreach (var pair in hostEnv) psi.Environment[pair.Key] = pair.Value;
        ApplyMarketRestartPolicy();
        if (seedNotice != null)
            _hostNotice = _hostNotice == null ? seedNotice : _hostNotice + "\n" + seedNotice;

        // pnpm 可用性修复（本机 pnpm 是 Rust 版、连不上 registry 时准备一份 Node 版 + 垫片）
        // 改为**后台预热**：它内部可能跑 `pnpm ping`（30s 超时）、必要时还要联网装一份内置
        // pnpm，原先在这里被 await，于是全部压在 dsh web 启动之前，用户看到的是"点了没反应"。
        // 现在垫片目录已由 MarketSupport.ToolDirs() 无条件前置进子进程 PATH，预热与服务启动
        // 并行进行；用户真正用到 pnpm 是稍后在市场里点安装插件，届时垫片已就位。
        StartPnpmWarmup();

        // 启动前拍一份 profile 清单快照。只有这次确实起来，它才会被提交为"已知可用"，
        // 因此它天然代表"上一次真的能启动的配置"——失败时才有东西可回滚。
        DesktopRecovery.SnapshotBeforeStart(MarketSupport.ProfileDir(), MarketSupport.ActiveProfile);

        SetStatus("正在启动 dsh web（端口 " + _port + "）…");
        try
        {
            _serverProc = Process.Start(psi);
            // Process.Start 的返回类型是 Process?（UseShellExecute=false 且启动失败时可能为 null）。
            // 显式挡住：否则下面 8 处解引用会抛 NullReferenceException，而这个异常会一路冒到全局
            // 未处理出口（App.xaml.cs 的 DispatcherUnhandledException → 弹框 + Shutdown(1)），
            // 即"点一次启动服务失败"等于"整个应用退出"。返回前记日志并给出可执行的状态文案。
            if (_serverProc == null)
            {
                DesktopLog.Error("启动 dsh 服务失败: Process.Start 返回 null（命令行或工作目录无效）");
                SetStatus("启动 dsh 服务失败：进程未能创建，请检查 dsh 与工作目录。");
                return;
            }

            _startedByUs = true;
            RememberServerOwner();
        }
        catch (Exception ex)
        {
            DesktopLog.Error("启动 dsh 服务失败", ex);
            SetStatus("启动 dsh 服务失败: " + ex.Message);
            return;
        }

        DesktopLog.Info("已启动 dsh: pid=" + _serverProc.Id + " port=" + _port + " cwd=" + psi.WorkingDirectory);
        _recentStderr.Clear();

        var urlMatch = new Regex(@"dsh web:\s*(https?://\S+)", RegexOptions.Compiled);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _urlTcs = tcs;
        _serverProc.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            // 启动地址那行带一次性 token，落盘前去凭据
            DesktopLog.Info("[dsh:" + _port + "] " + (urlMatch.IsMatch(e.Data) ? DesktopLog.RedactUrl(e.Data) : e.Data));
            TrySetUrl(urlMatch, e.Data, tcs);
        };
        _serverProc.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            DesktopLog.Warn("[dsh:" + _port + "] " + (urlMatch.IsMatch(e.Data) ? DesktopLog.RedactUrl(e.Data) : e.Data));
            RememberStderr(e.Data);
            TrySetUrl(urlMatch, e.Data, tcs);
            // 含启动地址 / auth token 的行不写入状态栏，避免 token 泄露到界面
            if (urlMatch.IsMatch(e.Data) || e.Data.Contains("token=", StringComparison.OrdinalIgnoreCase)) return;
            Dispatcher.Invoke(() => StatusText.Text += "\n" + e.Data);
        };
        _serverProc.EnableRaisingEvents = true;
        var epoch = _serverEpoch;
        _serverProc.Exited += (_, _) =>
        {
            if (!tcs.Task.IsCompleted)
                tcs.TrySetResult("");
            // 主动停止（窗口关闭 / 停止按钮 / 重启）不算异常退出
            if (!_stoppingServer) OnServerExitedUnexpectedly(epoch);
        };
        _serverProc.BeginOutputReadLine();
        _serverProc.BeginErrorReadLine();

        await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(90)));
        _authUrl = tcs.Task.IsCompleted && !string.IsNullOrEmpty(tcs.Task.Result) ? tcs.Task.Result : null;

        if (_authUrl == null)
        {
            var why = _serverProc.HasExited
                ? "dsh web 已退出(" + ExitCodeText(_serverProc) + ")，请手动运行 dsh web。"
                : "等待 dsh 就绪超时(端口 " + _port + ")";
            DesktopLog.Error("启动失败: " + why + DescribeRecentStderr());
            SetStatus(why);
            return;
        }

        DesktopLog.Info("dsh 已就绪: " + DesktopLog.RedactUrl(_authUrl));
        DesktopRecovery.CommitHealthy(MarketSupport.ActiveProfile, _port);
        SetStatus($"DSH 已就绪(端口 {_port})." + (_hostNotice == null ? "" : "\n" + _hostNotice));
        _hostNotice = null;
        _autoRestarts = 0;
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
                var notice = PnpmSupport.Prepare(MarketSupport.ProfileDir()).Notice;
                if (string.IsNullOrEmpty(notice)) return;
                DesktopLog.Info("pnpm 预热: " + notice);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // 就绪前并入 _hostNotice（由就绪那一行一起显示）；就绪后直接写状态栏。
                    // 两条路径都在 UI 线程上，不会撕裂 _hostNotice 的读写。
                    if (_urlTcs.Task.IsCompleted) SetStatus(notice!);
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

    /// <summary>
    /// dsh 服务非主动退出时的自愈：等端口释放后重新拉起并重新内嵌。
    /// 连续失败 <see cref="MaxAutoRestarts"/> 次后停下并提示手动启动。
    /// </summary>
    private void OnServerExitedUnexpectedly(int epoch)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (epoch != _serverEpoch) return;   // 期间已被手动停止/重启，回调作废
            BtnToggle.Content = "启动 DSH 服务";
            DesktopLog.Warn("dsh 服务异常退出: " + ExitCodeText(_serverProc) + DescribeRecentStderr());
            if (_autoRestarts >= MaxAutoRestarts)
            {
                SetStatus($"dsh 服务异常退出，已停止自动重启（{MaxAutoRestarts} 次）。");
                await ShowRecoveryAssistantAsync(
                    "dsh 服务连续 " + MaxAutoRestarts + " 次异常退出。",
                    "最后退出状态: " + ExitCodeText(_serverProc)
                    + "\n\n最常见的原因是刚装上的插件与当前 Harness 版本不兼容——"
                    + "dsh 的 Loader 只要有一个 entry 加载失败，整次启动就会中止。"
                    + DescribeRecentStderr());
                return;
            }
            _autoRestarts++;
            SetStatus($"dsh 服务已退出，正在自动重启（第 {_autoRestarts}/{MaxAutoRestarts} 次）…");
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            if (epoch != _serverEpoch) return;
            _startedByUs = false;
            _authUrl = null;
            await StartAndEmbedAsync();
        }));
    }

    private static void TrySetUrl(Regex re, string line, TaskCompletionSource<string> tcs)
    {
        var m = re.Match(line);
        if (m.Success) tcs.TrySetResult(m.Groups[1].Value);
    }

    // ---------- 窗口按钮 / 快捷键 ----------

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>
    /// 关闭按钮：**收起窗口到托盘**，不再直接退出。
    /// 退出入口只剩托盘菜单「退出」——它调用 <c>Window.Close()</c>，因此仍会经过
    /// <c>Window_Closing</c> 的「更新进行中」确认门；同时也消除了「更新进行中点✕」把
    /// npm 变成无人回收的孤儿进程这条路径（隐藏窗口不会结束进程）。
    /// </summary>
    private void OnClose(object sender, RoutedEventArgs e) => HideToTray();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        BtnMaximize.Content = maximized ? "\uE923" : "\uE922";
        BtnMaximize.ToolTip = maximized ? "还原" : "最大化";

        // 最小化只进任务栏——托盘行为已移到关闭按钮（见 HideToTray）。
        // 因此最小化时不更新恢复目标，否则从托盘恢复会得到一个最小化的窗口。
        if (WindowState != WindowState.Minimized) _restoreState = WindowState;
    }

    // ---------- 收起到托盘（关闭按钮的行为） ----------

    private WinForms.NotifyIcon? _tray;
    private WinForms.ToolStripMenuItem? _trayToggleItem;
    private WinForms.ToolStripMenuItem? _trayCopyUrlItem;
    private WinForms.ToolStripMenuItem? _trayExitItem;
    private bool _hiddenToTray;
    private bool _trayTipShown;
    private WindowState _restoreState = WindowState.Normal;

    /// <summary>
    /// 收起窗口并在通知区域显示托盘图标（关闭按钮的行为）。
    ///
    /// **安全性**：只有在托盘图标确实显示成功之后才隐藏窗口。否则窗口会消失、
    /// 而用户没有任何入口把它找回来（恢复与退出的唯一入口都在托盘菜单里）。
    /// </summary>
    private void HideToTray()
    {
        if (_hiddenToTray) return;

        try { CloseAllTitlebarPopups(); } catch { /* ignore */ }

        try { EnsureTrayIcon(); }
        catch (Exception ex) { DesktopLog.Error("创建托盘图标失败，已取消收起到托盘", ex); }

        if (_tray == null)
        {
            SetStatus("托盘图标不可用，已取消收起到托盘（窗口保持打开）。");
            return;
        }

        try { _tray.Visible = true; }
        catch (Exception ex)
        {
            DesktopLog.Error("显示托盘图标失败，已取消收起到托盘", ex);
            SetStatus("托盘图标不可用，已取消收起到托盘（窗口保持打开）。");
            return;
        }

        _hiddenToTray = true;
        Hide();

        if (!_trayTipShown)
        {
            _trayTipShown = true;
            try
            {
                _tray.ShowBalloonTip(2500, "DSH Desktop",
                    "已收起到托盘，点击托盘图标可恢复窗口；退出请用托盘菜单「退出」。",
                    WinForms.ToolTipIcon.Info);
            }
            catch { /* 气泡提示失败不影响功能 */ }
        }
    }

    /// <summary>从托盘恢复窗口。</summary>
    private void RestoreFromTray()
    {
        if (!_hiddenToTray) return;
        _hiddenToTray = false;
        if (_tray != null) _tray.Visible = false;

        Show();
        WindowState = _restoreState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;

        Dispatcher.BeginInvoke(new Action(() =>
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
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// 第二实例唤醒入口（优化清单 B32）：把已有实例的窗口从三种状态恢复并置前。
    /// ① 已收起到托盘（<c>_hiddenToTray</c>）→ 走 <see cref="RestoreFromTray"/>（它会清标志并隐藏托盘图标）；
    /// ② 已最小化 → 恢复为 <c>_restoreState</c>（或 Normal），**不得停在最小化**；
    /// ③ 可见但在后台 → 置前。
    /// 注意：<see cref="RestoreFromTray"/> 开头是 <c>if (!_hiddenToTray) return;</c>，
    /// 对 ②③ 是空操作，因此三态必须在此单独覆盖。任何异常只记日志，不影响应用。
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
            if (_hiddenToTray) RestoreFromTray();   // ①：内部已清 _hiddenToTray、隐藏托盘图标并 Show()

            if (WindowState == WindowState.Minimized)   // ②：不得停在最小化
            {
                WindowState = _restoreState == WindowState.Maximized
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
            // 规避 Windows 前台锁：Topmost 闪切（与 RestoreFromTray 一致）+ SetForegroundWindow。
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

    private void EnsureTrayIcon()
    {
        if (_tray != null) return;

        // 托盘菜单＝窗口收起后唯一可达的操作面，所以把"运行期真正会用到的操作"都放进来：
        // 唤出窗口 / 启停服务 / 重启服务 / 重载界面 / 外部浏览器 / 复制访问地址 / 检查更新 /
        // 日志与诊断 / 退出。深色主题与图标字形见 TrayMenu.cs（WinForms 默认是浅色系统外观）。
        var menu = TrayMenu.Create();

        menu.Items.Add(TrayMenu.Item("显示主窗口", 0xE8A7, RestoreFromTray, "把主窗口带回前台"));

        menu.Items.Add(TrayMenu.Separator());
        _trayToggleItem = TrayMenu.Item("停止 DSH 服务", 0xE71A, () => OnToggleServer(this, new RoutedEventArgs()),
            "启动或停止本窗口托管的 dsh web");
        menu.Items.Add(_trayToggleItem);
        menu.Items.Add(TrayMenu.Item("重启服务", 0xE72C, () => OnMenuRestartDesktop(this, new RoutedEventArgs()),
            "停掉托管的 dsh 并重新拉起、重新内嵌（等价于原地重建一次会话）"));
        menu.Items.Add(TrayMenu.Item("重载界面", 0xE895, () => OnMenuReloadRenderer(this, new RoutedEventArgs()),
            "重新加载内嵌页面，不重启服务"));

        menu.Items.Add(TrayMenu.Separator());
        menu.Items.Add(TrayMenu.Item("在浏览器中打开", 0xE774, () => OnOpenExternal(this, new RoutedEventArgs()),
            "用系统默认浏览器打开当前服务地址"));
        _trayCopyUrlItem = TrayMenu.Item("复制访问地址", 0xE8C8, CopyAuthUrlToClipboard,
            "把带鉴权 token 的地址复制到剪贴板（服务未就绪时不可用）");
        menu.Items.Add(_trayCopyUrlItem);
        menu.Items.Add(TrayMenu.Item("检查更新", 0xEA8F, () => OnCheckUpdate(this, new RoutedEventArgs()),
            "查询 DSH 是否有新版本"));

        menu.Items.Add(TrayMenu.Separator());
        menu.Items.Add(TrayMenu.Item("打开日志目录", 0xE7C3, OpenLogDirectory, "启动失败时先看这里"));
        menu.Items.Add(TrayMenu.Item("导出诊断…", 0xE896, () => OnExportDiagnostics(this, new RoutedEventArgs()),
            "打包最近日志与系统信息摘要"));

        menu.Items.Add(TrayMenu.Separator());
        _trayExitItem = TrayMenu.Item("退出", 0xE7E8, Close,
            StopOnClose.IsChecked == true ? "退出并停止服务" : "退出（保留服务继续运行）");
        menu.Items.Add(_trayExitItem);

        // 每次展开都按当前状态刷新动态项：启停文案/图标、复制地址是否可用。
        menu.Opening += (_, __) => SyncTrayMenu();

        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "DSH Desktop",
            ContextMenuStrip = menu,
            Visible = false,
        };
        // 单击 / 双击都恢复窗口（右键留给菜单）
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) RestoreFromTray();
        };
        _tray.DoubleClick += (_, __) => RestoreFromTray();
    }

    /// <summary>每次托盘菜单展开时刷新随状态变化的项（启停文案/图标、复制地址可用性、退出提示）。</summary>
    private void SyncTrayMenu()
    {
        try
        {
            var running = _startedByUs && _serverProc != null && !_serverProc.HasExited;
            if (_trayToggleItem != null)
            {
                TrayMenu.Restyle(_trayToggleItem,
                    running ? "停止 DSH 服务" : "启动 DSH 服务",
                    running ? 0xE71Au : 0xE768u);
            }
            if (_trayCopyUrlItem != null)
            {
                var ready = !string.IsNullOrWhiteSpace(_authUrl);
                _trayCopyUrlItem.Enabled = ready;
                _trayCopyUrlItem.ToolTipText = ready
                    ? "把带鉴权 token 的地址复制到剪贴板"
                    : "服务尚未就绪，稍后再试";
            }
            if (_trayExitItem != null)
            {
                _trayExitItem.ToolTipText = StopOnClose.IsChecked == true
                    ? "退出并停止服务"
                    : "退出（保留服务继续运行）";
            }
        }
        catch (Exception ex)
        {
            // 刷新失败只影响提示文案，不能让菜单打不开。
            DesktopLog.Warn("刷新托盘菜单失败: " + DesktopLog.Describe(ex));
        }
    }

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

    /// <summary>托盘图标：DSH.ico 全部是 PNG 压缩帧，System.Drawing.Icon(path,size) 会渲染成空白，
    /// 所以自己挑最接近的帧、解码 PNG 再生成 HICON。</summary>
    private System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "DSH.ico");
            if (File.Exists(path))
            {
                var size = Math.Max(16, WinForms.SystemInformation.SmallIconSize.Width);
                using var bitmap = LoadIcoFrame(path, size);
                if (bitmap != null)
                {
                    _trayIconHandle = bitmap.GetHicon();
                    return System.Drawing.Icon.FromHandle(_trayIconHandle);
                }
            }
        }
        catch { /* 回退到系统默认图标 */ }
        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>从 .ico 文件里挑出尺寸最接近 <paramref name="size"/> 的帧并解码成位图。</summary>
    private static System.Drawing.Bitmap? LoadIcoFrame(string path, int size)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 6) return null;
        var count = BitConverter.ToUInt16(bytes, 4);
        var bestOffset = -1;
        var bestLength = 0;
        var bestDiff = int.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + i * 16;
            if (entry + 16 > bytes.Length) break;
            var width = bytes[entry] == 0 ? 256 : bytes[entry];
            var length = (int)BitConverter.ToUInt32(bytes, entry + 8);
            var offset = (int)BitConverter.ToUInt32(bytes, entry + 12);
            if (length <= 0 || offset < 0 || offset + length > bytes.Length) continue;
            var diff = Math.Abs(width - size);
            if (diff >= bestDiff) continue;
            bestDiff = diff;
            bestOffset = offset;
            bestLength = length;
        }
        if (bestOffset < 0) return null;
        using var stream = new MemoryStream(bytes, bestOffset, bestLength);
        return new System.Drawing.Bitmap(stream);
    }

    private void DisposeTrayIcon()
    {
        var tray = _tray;
        _tray = null;
        if (tray != null)
        {
            try
            {
                tray.Visible = false;
                tray.Icon = null;
                tray.Dispose();
            }
            catch { /* ignore */ }
        }
        if (_trayIconHandle != IntPtr.Zero)
        {
            try { DestroyIcon(_trayIconHandle); } catch { /* ignore */ }
            _trayIconHandle = IntPtr.Zero;
        }
    }

    private IntPtr _trayIconHandle = IntPtr.Zero;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

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
    /// 重启到恢复模式：停掉服务并打开恢复助手（诊断/回滚/停用可疑插件）。
    /// 刻意不另起进程：本窗口对 dsh 子进程的掌控是恢复流程的前提（见 ApplyMarketRestartPolicy）。
    /// </summary>
    private async void OnMenuRestartToRecovery(object sender, RoutedEventArgs e)
    {
        CloseAllTitlebarPopups();
        StopServer();
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

        var node = FindNode();
        if (node != null)
        {
            var dir = Path.GetDirectoryName(node);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) parts.Add(dir);
        }

        var binJs = DshNodeBinJs();
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
                var npm = FindNpm();
                if (npm == null) return null;

                var psi = new ProcessStartInfo(npm.Value.Node)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add(npm.Value.NpmCli);
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

    /// <summary>退出码同时记有符号十进制与十六进制位型——Windows 上崩溃码多为 NTSTATUS（如 0xC0000005）。</summary>
    private static string ExitCodeText(Process? p)
    {
        try
        {
            if (p == null) return "exit=(无进程)";
            if (!p.HasExited) return "exit=未退出";
            var code = p.ExitCode;
            return "exit=" + code + " (0x" + unchecked((uint)code).ToString("X8") + ")";
        }
        catch { return "exit=不可用"; }
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
                primary: "继续导出", cancel: "取消");
            if (answer != AppDialogResult.Primary) return;

            var dir = DesktopLog.Directory ?? DesktopLog.DefaultDirectory();
            System.IO.Directory.CreateDirectory(dir);
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
            var profileDir = MarketSupport.ProfileDir();
            var profile = MarketSupport.ActiveProfile;
            var suspects = DesktopRecovery.FindSuspectBundles(profileDir, profile);
            var lastGood = DesktopRecovery.LastGoodSummary(profile);
            var canReEnable = DesktopRecovery.HasDisabledRecord(profileDir);

            var text = detail
                + (suspects.Count > 0
                    ? "\n\n可疑插件: " + string.Join(", ", suspects)
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
                    DesktopRecovery.DisableBundles(profileDir, profile, suspects, out var disabled);
                    SetStatus(disabled);
                    await RetryAfterRecoveryAsync();
                    break;

                case RecoveryAction.ReEnable:
                    DesktopRecovery.ReEnableBundles(profileDir, out var enabled);
                    SetStatus(enabled);
                    await RetryAfterRecoveryAsync();
                    break;

                case RecoveryAction.Rollback:
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

    private async Task RetryAfterRecoveryAsync()
    {
        _autoRestarts = 0;
        _startedByUs = false;
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

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            DesktopLog.Warn("网页导航失败: status=" + e.WebErrorStatus + " navId=" + e.NavigationId);
            // 不再提示"可点「重新加载」"——标题栏已无该按钮，重载入口是 F5 或
            // 「重载/重启」菜单里的「重载渲染器」。提示必须指向真实存在的操作。
            SetStatus("网页加载失败(" + e.WebErrorStatus + ")，可按 F5 或菜单「重载渲染器」重试。");
        }
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Web.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            ForceFocusIntoWeb();
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
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
            if (_startedByUs && _serverProc != null && !_serverProc.HasExited)
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
        var u = _authUrl ?? (_urlTcs.Task.IsCompleted ? _urlTcs.Task.Result : null);
        if (string.IsNullOrEmpty(u)) u = "http://127.0.0.1:" + _port;
        try { Process.Start(new ProcessStartInfo { FileName = u, UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    // ---------- 版本检测 / 更新 ----------

    /// <summary>读取实际运行的 dsh 捆绑包版本（来自 package.json）。</summary>
    private static string ReadBundledVersion()
    {
        var binJs = DshNodeBinJs();
        if (binJs == null) return "未知";
        try
        {
            // binJs 是 File.Exists 通过后的绝对文件路径（见 DshNodeBinJs），必然含父目录
            var pkg = Path.Combine(Path.GetDirectoryName(binJs)!, "..", "package.json");
            if (File.Exists(pkg))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pkg));
                if (doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString() ?? "未知";
            }
        }
        catch { /* ignore */ }
        return "未知";
    }

    private void ShowCurrentVersion()
    {
        var bundled = BundledDshBinJs() != null;
        var binJs = DshNodeBinJs();
        var source = bundled ? "内置" : binJs != null ? "npx 缓存" : "未找到";
        var version = ReadBundledVersion();

        // 版本号独立可点；「来源」不再挤进可见文本，改由 ToolTip 与版本浮层承担。
        BtnVersion.Content = $"dsh v{version}";
        BtnVersion.ToolTip = binJs == null
            ? "DeepSeek Harness 版本"
            : $"DeepSeek Harness 版本\n来源: {source}\n{LauncherInstallDir()}";
        VersionPopupValue.Text = version;
        // 「来源」在浮层里是独立一列：标签「来源」固定在左列（与「当前版本」同列），
        // 取值放在右列（与版本号同列、左缘必然重合），因此这里只赋值、不带「来源:」前缀。
        // 原先写作 `binJs == null ? $"来源: {source}" : $"来源: {source}"` ——
        // 三元式两个分支完全相同，属无效表达式，已清除。
        VersionPopupSource.Text = source;
    }

    /// <summary>
    /// 更新状态的唯一派生点：由 <see cref="_lastUpdate"/> 推出「有可用更新」的呈现。
    /// 状态栏那颗「检查更新」已移除，更新入口统一收敛到标题栏（版本胶囊浮层 + 铃铛），
    /// 因此这里只维护铃铛可见性、版本浮层的「查看更新详情」入口与检查按钮的进行态文案。
    /// </summary>
    private void ApplyUpdateState()
    {
        var res = _lastUpdate;
        var available = res != null && res.Available;

        // busy 的唯一派生：检查/更新期间禁用全部标题栏动作；重启族期间禁用更新族与动作按钮，
        // 但保留版本浮层入口（恢复助手要靠它显示错误行）。
        var mode = _busyMode;
        var updateBusy = mode is "check" or "update";
        var actionBusy = mode is "restart" or "recover";

        BtnTui.IsEnabled = !updateBusy && !actionBusy;
        BtnRestartMenu.IsEnabled = !updateBusy && !actionBusy;
        BtnDevMenu.IsEnabled = !updateBusy && !actionBusy;
        BtnVersionCheck.IsEnabled = !updateBusy;
        BtnVersion.IsEnabled = !updateBusy;
        // 只改文案 TextBlock：按钮 Content 是「图标 + 文案」面板，整体改写会把刷新图标一起冲掉。
        BtnVersionCheckText.Text = updateBusy ? (_updateBusyText ?? "检查更新") : "检查更新";

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
        _checkingForUpdates = mode is "check" or "update";
        ApplyUpdateState();
    }

    private void EndBusy()
    {
        _busyMode = null;
        _checkingForUpdates = false;
        _updateBusyText = null;
        ApplyUpdateState();
    }

    /// <summary>进行态文案（"检查中…" / "更新中…"），由 <see cref="ApplyUpdateState"/> 单一消费。</summary>
    private string? _updateBusyText;

    /// <summary>清除版本浮层里的错误行（开始新动作时调用）。</summary>
    private void ResetVersionPopupError()
    {
        VersionPopupError.Text = "";
        VersionPopupError.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 返回当前实际运行 dsh 的安装根目录（含 package.json 与 node_modules），
    /// 也就是执行 npm 更新应写入的目录。优先桌面内置 runtime，否则回退到 npx 缓存。
    /// </summary>
    private static string? LauncherInstallDir()
    {
        var binJs = DshNodeBinJs();
        if (binJs == null) return null;
        // 沿路径逐级上溯取安装根。<c>DshNodeBinJs()</c> 只返回两个候选（内置 runtime 或
        // npx 缓存）下的绝对文件路径，最浅也远深于 5 层，所以 Path.GetDirectoryName 不可能
        // 返回 null；这里与 ToolDirs() 采用同一种写法（`!`），不引入空的判空分支。
        var lib = Path.GetDirectoryName(binJs)!;        // ...\lib
        var pkg = Path.GetDirectoryName(lib)!;          // ...\@deepseek-ai\dsh
        var scope = Path.GetDirectoryName(pkg)!;        // ...\node_modules\@deepseek-ai
        var nm = Path.GetDirectoryName(scope)!;         // ...\node_modules
        return Path.GetDirectoryName(nm)!;              // <install root>（含 package.json 与 node_modules）
    }

    /// <summary>
    /// 检查 DeepSeek Harness 是否有新版本。
    /// <paramref name="manual"/> 为 true 时（用户点击"检查更新"）无论结果都更新状态栏；
    /// 自动检测（false）只在发现新版本时提示。
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual = false)
    {
        if (_updateChecked && !manual) return;        // 自动检测每会话只跑一次
        if (_checkingForUpdates) return;              // 检查/更新共享的并发门：手动连点不再并发
        _updateBusyText = "检查中…";
        BeginBusy("check");               // 单一 busy 门：禁用全部标题栏动作并派生文案
        ResetVersionPopupError();
        var current = ReadBundledVersion();

        try
        {
            var res = await VersionUpdate.CheckAsync(current);
            if (res == null)
            {
                if (manual)
                {
                    SetStatus("检查更新失败（网络不可用或解析失败）。");
                    SetTitlebarError("检查更新失败：网络不可用或解析失败，可重试。");
                }
                // 刻意不在这里复位：失败不该抹掉此前已发现的可用更新
                //（旧实现在此调用 RestoreUpdateButton，把刚发现的更新状态覆盖掉了）。
                return;
            }

            if (res.Available) _lastUpdate = res;      // 只在确有可用更新时推进状态
            _updateChecked = true;
            ApplyUpdateState();

            if (res.Available)
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
                _lastUpdate = null;
                ApplyUpdateState();
                SetStatus("DeepSeek Harness 已是最新版本（v" + res.Current + "）。");
            }
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

    /// <summary>从版本浮层跳转到更新详情浮层。</summary>
    private void OnShowUpdateDetails(object sender, RoutedEventArgs e) => ShowUpdatePopup();

    private void ShowUpdatePopup()
    {
        var res = _lastUpdate;
        if (res == null || !res.Available)
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
        PopupSubtitle.Text = $"当前 dsh v{res.Current} · {channel} · 点击“立即更新”升级内置 Harness 并热重载";
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
        _stoppingServer = true;
        _serverEpoch++;   // 让在途的异常退出回调作废
        try
        {
            if (_startedByUs && _serverProc != null && !_serverProc.HasExited)
            {
                try { _serverProc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                try { _serverProc.WaitForExit(5000); } catch { /* ignore */ }
            }
            _startedByUs = false;
        }
        finally
        {
            _stoppingServer = false;
            // 我们主动停了服务 → 归属记录随之作废，否则下次启动会把它当"遗留孤儿"提示接管。
            ServerState.Clear(DesktopRecovery.StateRoot, MarketSupport.ActiveProfile);
        }
    }

    // ---------- 服务归属：启动时防止同时运行两个服务 ----------

    /// <summary>记下这次托管的子进程归属（pid / 端口 / 启动时刻），供下次启动识别遗留孤儿。</summary>
    private void RememberServerOwner()
    {
        try
        {
            if (_serverProc == null) return;
            ServerState.Write(DesktopRecovery.StateRoot, MarketSupport.ActiveProfile,
                new ServerRecord(_serverProc.Id, _port, _serverProc.StartTime.ToUniversalTime()));
        }
        catch (Exception ex)
        {
            // 记不下来只影响"下次启动能否识别遗留"，绝不能挡住本次启动。
            DesktopLog.Warn("写入服务归属记录失败: " + DesktopLog.Describe(ex));
        }
    }

    /// <summary>默认端口上的占用者是谁（空闲 / 自己上次的遗留 / 别人的）。</summary>
    private static PortOwner ResolvePortOwner(int port)
    {
        if (!PortInUse(port)) return PortOwner.Free;
        var record = ServerState.Read(DesktopRecovery.StateRoot, MarketSupport.ActiveProfile);
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

    /// <summary>结束遗留服务并等端口释放；返回端口是否真的空了。</summary>
    private static bool TakeOverLeftover(int pid)
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
        for (var i = 0; i < 20 && PortInUse(DefaultPort); i++) System.Threading.Thread.Sleep(250);
        return !PortInUse(DefaultPort);
    }

    /// <summary>透明热重载：停止旧进程，用最新版本重新启动并刷新内嵌网页。</summary>
    private async Task RestartServerAsync()
    {
        StopServer();
        _authUrl = null;
        await StartAndEmbedAsync();
    }

    /// <summary>定位可用的 npm CLI（优先系统 Node 自带的 npm，其次是 dsh 所用 node 旁的 npm）。</summary>
    internal static (string Node, string NpmCli)? FindNpm()
    {
        foreach (var dir in new[] { @"D:\nodejs", @"C:\Program Files\nodejs", @"C:\nodejs" })
        {
            var node = Path.Combine(dir, "node.exe");
            var cli = Path.Combine(dir, "node_modules", "npm", "bin", "npm-cli.js");
            if (File.Exists(node) && File.Exists(cli)) return (node, cli);
        }
        var node2 = FindNode();
        if (node2 != null)
        {
            // node2 是 File.Exists 通过后的绝对文件路径（见 FindNode），必然含父目录
            var cli2 = Path.Combine(Path.GetDirectoryName(node2)!, "node_modules", "npm", "bin", "npm-cli.js");
            if (File.Exists(cli2)) return (node2, cli2);
        }
        return null;
    }

    /// <summary>在当前实际运行的 dsh 安装目录执行 npm install，把 @deepseek-ai/dsh 升到指定版本。
    /// 遇到 EBUSY/EPERM（文件被占用）会自动重试几次。</summary>
    private static async Task<(bool Success, string Version, string Error)> RunUpdateAsync(string targetVersion)
    {
        var npm = FindNpm();
        var installDir = LauncherInstallDir();
        if (npm == null) return (false, targetVersion, "未找到 npm（需系统 Node.js）。");
        if (installDir == null || !File.Exists(Path.Combine(installDir, "package.json")))
            return (false, targetVersion, "未找到正在运行的 dsh 安装目录（package.json）。");

        var psi = new ProcessStartInfo(npm.Value.Node)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = installDir,
        };
        psi.ArgumentList.Add(npm.Value.NpmCli);
        psi.ArgumentList.Add("install");
        psi.ArgumentList.Add("@deepseek-ai/dsh@" + targetVersion);
        psi.ArgumentList.Add("--no-audit");
        psi.ArgumentList.Add("--no-fund");
        psi.ArgumentList.Add("--no-update-notifier");

        async Task<(bool Ok, string Out)> RunOnce()
        {
            var proc = Process.Start(psi);
            if (proc == null) return (false, "无法启动 npm 进程。");
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            var exitTask = proc.WaitForExitAsync();
            if (await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromMinutes(5))) != exitTask)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (false, "更新超时（超过 5 分钟）。");
            }
            var output = await outTask;
            var err = await errTask;
            return (proc.ExitCode == 0, (output + "\n" + err).Trim());
        }

        string lastError = "";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var (ok, detail) = await RunOnce();
                if (ok) return (true, targetVersion, "");
                lastError = detail;
                if (!IsLockError(detail)) return (false, targetVersion, detail);   // 非占用错误，直接失败
                await Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 1 : 2));
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                if (!IsLockError(ex.Message)) return (false, targetVersion, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
        return (false, targetVersion, lastError);
    }

    private static bool IsLockError(string text)
        => text.Contains("EBUSY", StringComparison.OrdinalIgnoreCase)
        || text.Contains("EPERM", StringComparison.OrdinalIgnoreCase)
        || text.Contains("resource busy or locked", StringComparison.OrdinalIgnoreCase);

    /// <summary>点击"立即更新"：运行更新指令，成功后透明热重载内嵌 dsh。</summary>
    private async Task ApplyUpdateAsync()
    {
        // 入口并发门：busy 期间不得二次进入更新。仅靠按钮禁用不足以防住
        // 「更新中再点铃铛重开浮层、再点『立即更新』」这条路径——浮层里的
        // BtnPopupUpdate 不参与标题栏的 IsEnabled 门控。
        if (_busyMode is not null)
        {
            SetStatus("已有动作正在进行，请稍后再试。");
            return;
        }

        var target = _lastUpdate?.Newest;
        if (string.IsNullOrEmpty(target))
        {
            SetStatus("没有可更新的版本。");
            return;
        }

        _updateBusyText = "更新中…";
        BeginBusy("update");             // 单一 busy 门：禁用全部标题栏动作并派生文案
        try
        {
            SetStatus("正在停止 DSH 服务以更新…");
            var beforeModels = await ReadModelNamesAsync();   // 更新前快照（尽力而为）
            StopServer();   // 先停止，释放正在加载的文件，避免 npm 覆盖失败

            // 方案 C（优化清单 B5）：更新前备份关键资产。位置固定为
            // 「StopServer() 释放文件锁之后、RunUpdateAsync()（就地 npm install）之前」。
            // fail-safe：备份失败不静默继续——给出可见反馈并中止本次更新。
            var installDir = LauncherInstallDir();
            UpdateBackupResult? backup = null;
            if (installDir != null)
            {
                SetStatus("正在备份安装目录关键资产…");
                backup = UpdateBackup.Create(installDir);
                if (!backup.Success)
                {
                    DesktopLog.Error("更新前备份失败，已中止更新: " + backup.Message);
                    SetStatus("更新前备份失败，已中止更新。");
                    AppDialog.Show(this, "DSH 更新",
                        "更新前备份失败，已中止本次更新（避免更新失败后无法恢复）：\n\n"
                        + backup.Message,
                        primary: "知道了", warning: true);
                    await StartAndEmbedAsync();   // 备份失败中止：尽量把服务恢复回来
                    return;
                }
                DesktopLog.Info("更新前备份已建立（失败时用于回滚）: " + backup.Root);
                if (backup.Missing.Count > 0)
                    DesktopLog.Warn("更新备份不包含以下资产（本机更新前本就不存在，不参与回滚）: "
                        + string.Join(", ", backup.Missing));
            }

            var result = await RunUpdateAsync(target);
            if (result.Success)
            {
                if (installDir != null) UpdateBackup.Discard(installDir);   // 成功路径结束前删除备份
                _lastUpdate = null;               // 已升级，可用更新状态作废
                ApplyUpdateState();
                ShowCurrentVersion();
                SetStatus("已更新到 v" + result.Version + "，正在热重载…");
                await RestartServerAsync();
                SetStatus("已更新到 v" + result.Version + "，DSH 服务已通过热重载恢复。");
                await NotifyNewModelsAsync(beforeModels);   // 更新后自动查找新增模型
            }
            else
            {
                // 复位「更新中…」但保留已有可用更新状态，以便重试
                ApplyUpdateState();
                var hint = IsLockError(result.Error)
                    ? "\n\n（文件被占用，可能仍有 dsh/node 进程在运行。已自动重试；若仍失败，请关闭其他 dsh/node 实例后重试。）"
                    : "";

                // 方案 C：失败即回滚。触发条件 = RunUpdateAsync 返回 Success=false
                // （npm 非零退出、5 分钟超时、无法启动 npm，或占用类错误重试 3 次仍失败）。
                var rollbackText = "未建立备份，未回滚";
                var rollbackNote = "";
                if (backup is { Success: true })
                {
                    var restore = UpdateBackup.Restore(installDir!);
                    if (restore.Success)
                    {
                        DesktopLog.Warn("更新失败，已回滚到更新前备份: " + restore.Root);
                        // 如实限定回滚范围（t14）：只回滚 dsh CLI 包、package.json 与锁文件；
                        // 传递依赖树（node_modules 下 dsh 之外的包）不在备份内，未回滚。
                        rollbackText = "已回滚 dsh CLI 包、package.json 与锁文件";
                        rollbackNote = "\n\n已回滚 dsh CLI 包、package.json 与锁文件；"
                            + "传递依赖树与其它 node_modules 内容未回滚，可能需要重跑一次更新以恢复一致。";
                    }
                    else
                    {
                        DesktopLog.Error("更新失败且回滚失败，需人工介入；备份仍在 " + restore.Root + " —— " + restore.Message);
                        rollbackText = "回滚失败，需人工介入";
                        rollbackNote = "\n\n回滚失败，需人工介入：备份仍在\n" + restore.Root
                            + "\n请手工把其中内容还原回安装目录。";
                    }
                }

                SetStatus("更新失败（" + rollbackText + "）：" + result.Error);
                AppDialog.Show(this, "DSH 更新",
                    "更新失败：\n\n" + result.Error + hint + rollbackNote,
                    primary: "知道了", warning: true);
                await StartAndEmbedAsync();           // 尽量恢复服务
            }
        }
        finally
        {
            EndBusy();                   // 更新成功→「检查更新」；失败→保留「有更新」以便重试
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
    /// 更新进行中关闭窗口的显式裁决（优化清单 B3）。更新走的是就地 npm install，进程句柄是
    /// RunUpdateAsync 的局部变量；关窗**不会**中止 npm——它仍会在后台继续改写安装目录，
    /// 而应用退出后重试与结果判定都不会再执行。这里把这件事变成用户知情的决策，并留下证据。
    /// </summary>
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busyMode != "update") return;

        var choice = AppDialog.Show(this, "DSH Desktop",
            "更新正在进行中。现在关闭不会中止更新——它仍会在后台继续改写安装目录，"
            + "且失败后将无人处理（重试与结果判定也不会再执行）。\n\n"
            + "建议等更新结束后再关闭。",
            primary: "仍要关闭", cancel: "继续等待", warning: true);
        if (choice != AppDialogResult.Primary)
        {
            e.Cancel = true;
            SetStatus("更新进行中，已取消关闭。更新完成后可再次关闭窗口。");
            return;
        }
        DesktopLog.Warn("更新进行中用户确认关闭：安装可能未完成，下次启动后请复核/重新执行更新。");
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        try { CloseAllTitlebarPopups(); } catch { /* ignore */ }
        DisposeTrayIcon();
        try { TuiView.Stop(); } catch { /* ignore */ }
        if (StopOnClose.IsChecked == true && _startedByUs && _serverProc != null && !_serverProc.HasExited)
        {
            _stoppingServer = true;
            _serverEpoch++;
            try { _serverProc.Kill(entireProcessTree: true); } catch { /* ignore */ }
        }
    }
}
