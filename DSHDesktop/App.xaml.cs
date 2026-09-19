using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DSHDesktop.Core;

namespace DSHDesktop;

public partial class App : System.Windows.Application
{
    /// <summary>单实例互斥体；进程存活期持有，不主动 Dispose（优化清单 B1）。</summary>
    private Mutex? _instanceMutex;

    /// <summary>
    /// 日志必须在任何窗口之前就位——启动期失败（找不到 node、WebView2 初始化失败）
    /// 同样需要留下证据。三个全局兜底把"程序莫名消失"变成日志里的一条记录。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        DesktopLog.Initialize();
        DesktopLog.Info("App.OnStartup args=[" + string.Join(' ', e.Args) + "]");

        if (e.Args.Any(argument =>
                string.Equals(argument, "--safe-mode", StringComparison.OrdinalIgnoreCase)))
        {
            ProfileManager.Default.Activate(ProfileMode.Safe);
            DesktopLog.Warn("已按 --safe-mode 请求选择隔离 profile：desktop-safe。");
        }

        // 单实例互斥（优化清单 B1）：两个实例会各自顺延端口、各自对同一 profile 拍快照/铺种子，
        // 并把 DesktopRecovery 的 pending 目录交替删改。此处必须在创建窗口、触碰 WebView2 之前拦截。
        //
        // fail-open 的作用域（t35/F2 收敛）：**只包住 `new Mutex` 构造**——它的目的仅是
        // 「互斥机制自身故障不得阻止应用启动」。二次实例分支与首实例建立监听都**不在**这个 catch 里，
        // 否则二次实例分支里任何异常都会被 fail-open 吞掉，`Environment.Exit(0)` 将不可达
        // （与被修复的 B1 缺陷同型）。
        bool createdNew;
        try
        {
            // 实例标识（t35/F1）：**默认路径与 B1 逐字一致**（Local\DSHDesktop.SingleInstance）；
            // 环境变量 DSH_DESKTOP_INSTANCE_ID **只在 Debug 构建生效**（Release 恒用默认标识），
            // Debug 侧取值还要通过格式校验，非法值回退默认并 Warn——见 SingleInstanceIpc.InstanceId。
            _instanceMutex = new Mutex(true, SingleInstanceIpc.MutexName, out createdNew);
        }
        catch (Exception ex)
        {
            DesktopLog.Warn("单实例互斥检查失败，按放行处理（视本进程为首实例）: " + DesktopLog.Describe(ex));
            createdNew = true;   // fail-open：互斥机制故障不得阻止启动
        }

        if (!createdNew)
        {
            // 二次实例：唤醒/回退提示全部放在**内层** try/catch 中；任何异常只记 Warn，
            // **不得**阻断下面的无条件终止（t35/F2）。
            try
            {
                DesktopLog.Warn("检测到已有 DSH Desktop 实例在运行。");

                // 先尝试唤醒已有实例（命名管道优先，≤1000ms；受限环境自动改走命名事件），
                // **成功则静默退出**；失败才回退到「提示框 + 退出」这条既有路径。
                if (SingleInstanceIpc.TryActivateExisting())
                {
                    DesktopLog.Info("已向已有实例发送唤醒请求，本次启动静默退出。");
                }
                else
                {
                    DesktopLog.Warn("未能在预算内唤醒已有实例，回退到提示框。");
                    try
                    {
                        // 文案修正（t26，reviewer low-a）：首实例可能正**收起到托盘**，
                        // 此时并不存在「已打开的窗口」可切换，因此不再那样写。
                        AppDialog.Show(null, "DSH Desktop",
                            "DSH Desktop 已在运行，但本次未能唤醒它的窗口。\n\n"
                            + "如果那个窗口已收起到系统托盘，请点击任务栏通知区域的 DSH Desktop 图标"
                            + "（可能被折叠在「隐藏的图标」里）恢复窗口。\n"
                            + "要结束那个实例，请用它的托盘菜单「退出」。",
                            primary: "知道了");
                    }
                    catch { /* 提示失败不影响退出 */ }
                }
            }
            catch (Exception ex)
            {
                DesktopLog.Warn("二次实例唤醒流程出错（仍继续终止进程）: " + DesktopLog.Describe(ex));
            }

            // ⚠ 终止**无条件到达**（t35/F2）：无论唤醒成功、失败还是抛异常，都必须在这里终止进程。
            // 理由（均已用 WPF 源码与实机日志证实）：
            //   (1) 绝不能写 `StartupUri = null`（原实现如此，是 B1 的实际缺陷）：
            //       Application.StartupUri 的 setter 第一句就是
            //       ArgumentNullException.ThrowIfNull(value)（dotnet/wpf Application.cs:1035），
            //       赋 null 必抛 ANE("value")；该异常会被 fail-open 吞掉，
            //       紧随其后的 Shutdown(0) 因此永远执行不到。
            //   (2) 就算 Shutdown(0) 执行了，也拦不住主窗口：框架随后调用的 DoStartup()
            //       （同文件 :1529-1585）只判断 `StartupUri != null`，完全不检查 IsShuttingDown，
            //       而 App.xaml 声明了 StartupUri，于是主窗口照建、WebView2 照碰。
            //       ——「只发信号就 return」正是这条陷阱，故这里不允许 return 了事。
            //   (3) 唯一能取消默认启动导航的 Application/StartupEventArgs.PerformDefaultAction
            //       在 .NET 10 WPF 中是 internal，外部代码无法设置（编译期 CS1061 已证实）。
            // 故此处用 Environment.Exit 确定性终止：此时尚未创建任何窗口/未触碰 WebView2，
            // 互斥体由操作系统在进程终止时释放，无需手工清理。
            DesktopLog.Warn("二次实例终止进程（Environment.Exit 0）。");
            Environment.Exit(0);
            return;
        }

        // 首实例：**只有 createdNew == true 才建立唤醒监听**（见 SingleInstanceIpc 契约）。
        // 建立失败只记日志，绝不影响首实例启动或使其退出。
        SingleInstanceIpc.StartServer(ActivationCoordinator.Request);

        // 迁移只允许首实例执行，避免二次实例在已有服务读 profile 时并发复制。
        if (e.Args.Any(argument =>
                string.Equals(argument, "--migrate-profile", StringComparison.OrdinalIgnoreCase)))
        {
            var migration = ProfileManager.Default.MigrateLegacyToDesktop();
            DesktopLog.Info("profile 迁移请求: status=" + migration.Status
                + " source=" + migration.SourceDirectory
                + " target=" + migration.TargetDirectory
                + " files=" + string.Join(',', migration.CopiedFiles ?? Array.Empty<string>()));
            try
            {
                AppDialog.Show(
                    null,
                    "DSH Desktop — Profile 迁移",
                    (migration.Notice ?? migration.Status.ToString())
                    + "\n\n当前版本仍继续使用 web profile；完成专用 Desktop 宿主适配后才会切换，"
                    + "不会让尚未恢复依赖的 profile 参与启动。",
                    primary: "知道了",
                    warning: migration.Status is ProfileMigrationStatus.Failed
                        or ProfileMigrationStatus.SourceInvalid);
            }
            catch { /* 迁移结果已写入日志，提示失败不影响正常启动 */ }
        }

        DispatcherUnhandledException += (_, args) =>
        {
            // 先把证据落盘，再受控退出：原实现遇到这种异常是无声崩掉，日志里什么都没有。
            DesktopLog.Error("UI 线程未处理异常", args.Exception);
            args.Handled = true;
            try
            {
                AppDialog.Show(null, "DSH Desktop — 遇到未处理的错误",
                    "DSH Desktop 遇到未处理的错误，已记录日志并即将退出。\n\n"
                    + DesktopLog.Describe(args.Exception)
                    + "\n\n日志目录：\n" + (DesktopLog.Directory ?? "(不可用)"),
                    primary: "知道了", warning: true);
            }
            catch { /* 提示失败不影响退出 */ }
            Shutdown(1);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            DesktopLog.Error("非 UI 线程未处理异常（进程即将终止）", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DesktopLog.Error("未观察的任务异常", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
    }
}
