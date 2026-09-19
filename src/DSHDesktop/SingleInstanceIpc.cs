using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#if DEBUG
using System.Text.RegularExpressions;
#endif

namespace DSHDesktop;

/// <summary>
/// 单实例「激活语义」的命名管道 IPC（优化清单 B32）。
///
/// 背景：单实例互斥（B1）只让二次启动**退出**，用户看到的是一个提示框，已有窗口并不会被唤醒；
/// 参考实现对应的是 Electron 的 <c>second-instance</c> 事件语义（第二次启动会把已有窗口置前）。
/// 本类提供该语义的等价物：
/// - **首实例**（<c>createdNew == true</c>）建立唤醒服务端（命名管道为主通道、命名事件为备用通道），
///   收到唤醒信号后回调 <see cref="ActivationCoordinator.Request"/>；
/// - **二次实例**用 ≤<see cref="ConnectTimeoutMs"/>ms 尝试连接并发送 <see cref="WakeSignal"/>，
///   成功即静默退出；失败由调用方回退到「提示框 + 退出」。
///
/// 边界（措辞按 t35/F3 **方案 ②** 精确化）：
/// - **命名管道**由 <see cref="PipeOptions.CurrentUserOnly"/> **显式**限定为当前用户；
/// - **命名事件**落在**会话命名空间**（`Local\` 语义），**依赖系统默认 DACL，未显式设置 ACL**
///   （t35 曾试方案 ①「显式仅当前用户 ACL」，实测在本环境直接使客户端 `OpenExisting` 被拒、
///   打断了 t26 已验证可用的备用通道，故未采用——详见 `<see cref="ActivationServer"/>` 内注释）；
///   因此「跨用户被拒」只是**基于默认 DACL 的推断，未实测**。不做跨会话唤醒。
/// - 读取有界（≤<see cref="MaxSignalBytes"/> 字节），超长丢弃并关闭该连接；
/// - 实例标识**默认值与 B1 逐字一致**（互斥体 <c>Local\DSHDesktop.SingleInstance</c>）；
///   环境变量 <c>DSH_DESKTOP_INSTANCE_ID</c> **只在 Debug 构建生效**（Release 恒用默认标识，见
///   <see cref="InstanceId"/>，t35/F1-a），Debug 下取值还需通过格式校验（t35/F1-b）；
///   该开关供自动化验证在与真实用户实例隔离的前提下使用。
/// </summary>
internal static class SingleInstanceIpc
{
    /// <summary>
    /// 实例标识覆盖开关。**只在 Debug 构建生效**（Release 恒用 <see cref="DefaultInstanceId"/>）；
    /// 未设置、为空白或格式非法时也使用 <see cref="DefaultInstanceId"/>。
    /// </summary>
    internal const string InstanceIdEnvVar = "DSH_DESKTOP_INSTANCE_ID";

    /// <summary>默认实例标识——与 B1 的互斥体名保持一致（默认行为不得改变）。</summary>
    internal const string DefaultInstanceId = "DSHDesktop.SingleInstance";

    /// <summary>唤醒信号载荷（ASCII 8 字节，远小于 <see cref="MaxSignalBytes"/>）。</summary>
    internal const string WakeSignal = "ACTIVATE";

    /// <summary>单次唤醒请求的有界读取上限（字节）。</summary>
    internal const int MaxSignalBytes = 64;

    /// <summary>二次实例连接已有实例的超时（毫秒）。</summary>
    internal const int ConnectTimeoutMs = 1000;

    /// <summary>服务端单次读取的超时（毫秒）：客户端挂起不得拖住监听循环。</summary>
    private const int ReadTimeoutMs = 500;

    private static IDisposable? _server;

    /// <summary>ASFW_ANY：允许任意进程在下次 SetForegroundWindow 调用中取得前台权限。</summary>
    private const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

#if DEBUG
    /// <summary>Debug 侧允许的实例标识：<c>^[A-Za-z0-9._-]{1,64}$</c>（Release 不读该变量）。</summary>
    private static readonly Regex InstanceIdPattern = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.Compiled);
#endif

    /// <summary>
    /// 当前实例标识（用于拼接互斥体名/管道名/事件名）。
    /// - **Release：恒返回 <see cref="DefaultInstanceId"/>** —— <see cref="InstanceIdEnvVar"/> 只在 Debug 生效，
    ///   以免发布版存在可用的「单实例绕过」通道（t35/F1-a）。
    /// - **Debug：允许覆盖**（团队取证使用 Debug 产物），但取值必须匹配 <c>^[A-Za-z0-9._-]{1,64}$</c>；
    ///   含 <c>\</c>、<c>/</c>、超长等**非法值一律回退默认并 Warn**（t35/F1-b）——
    ///   因为非法名会让 <c>new Mutex</c> 抛 <c>ArgumentException</c>，被 App 的 fail-open 吞掉后
    ///   **单实例守卫会整体失效**；因此非法值绝不允许进入名称拼接。
    /// </summary>
    internal static string InstanceId
    {
        get
        {
#if DEBUG
            var raw = Environment.GetEnvironmentVariable(InstanceIdEnvVar);
            if (string.IsNullOrWhiteSpace(raw)) return DefaultInstanceId;
            var candidate = raw.Trim();
            if (InstanceIdPattern.IsMatch(candidate)) return candidate;
            DesktopLog.Warn("DSH_DESKTOP_INSTANCE_ID 取值非法，已回退默认实例标识（Debug 专属开关）: "
                + SummarizeRejected(candidate));
            return DefaultInstanceId;
#else
            // Release：忽略 DSH_DESKTOP_INSTANCE_ID（该开关只在 Debug 构建生效，见类文档 F1-a）。
            return DefaultInstanceId;
#endif
        }
    }

#if DEBUG
    /// <summary>被拒取值的摘要（只记长度与可打印前缀，避免把任意长/含控制字符的原文写进日志）。</summary>
    private static string SummarizeRejected(string value)
    {
        var head = new StringBuilder();
        foreach (var ch in value)
        {
            if (head.Length >= 16) break;
            head.Append(ch >= ' ' && ch <= '~' ? ch : '?');
        }
        return "len=" + value.Length + " head=\"" + head + "\"";
    }
#endif

    /// <summary>互斥体名。默认值为 <c>Local\DSHDesktop.SingleInstance</c>（与 B1 完全一致）。</summary>
    internal static string MutexName => @"Local\" + InstanceId;

    /// <summary>命名管道名。</summary>
    internal static string PipeName => InstanceId + ".activate";

    /// <summary>
    /// 命名事件名（**备用唤醒通道**）。存在理由：某些受限环境（如受策略约束的沙箱/容器）
    /// 会拒绝**打开**命名管道（实测 `UnauthorizedAccessException: Access to the path is denied`），
    /// 但允许命名内核对象。管道失败时改用它，仍然属于「先唤醒、失败才回退」的既有语义，
    /// 且不改变任何默认行为（未收到事件时行为与之前完全一致）。
    /// </summary>
    internal static string EventName => InstanceId + ".activate.event";

    /// <summary>
    /// 二次实例：尝试唤醒已在运行的实例。**总预算 ≤ <see cref="ConnectTimeoutMs"/>ms**，
    /// 任何异常都返回 <c>false</c>（由调用方回退到提示框），绝不抛出、绝不挂起。
    /// </summary>
    internal static bool TryActivateExisting(int timeoutMs = ConnectTimeoutMs)
    {
        // 前台锁规避（提交给首实例）：ASFW_ANY 允许**任意**进程在下一次
        // SetForegroundWindow 调用中取得前台权限。二次实例通常是用户刚启动的进程，
        // 由它代为授权，首实例才有机会把窗口真正置前（否则 SetForegroundWindow 可能被拒）。
        // 失败不影响唤醒本身。
        try { AllowSetForegroundWindow(ASFW_ANY); } catch { /* ignore */ }

        // 主通道：命名管道。
        try
        {
            using var client = new NamedPipeClientStream(
                ".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            var payload = Encoding.UTF8.GetBytes(WakeSignal);
            client.Write(payload, 0, payload.Length);
            client.Flush();
            DesktopLog.Info("已通过命名管道唤醒已有实例。");
            return true;
        }
        catch (Exception ex)
        {
            DesktopLog.Warn("命名管道唤醒失败（改用命名事件通道）: " + DesktopLog.Describe(ex));
        }

        // 备用通道：命名事件（受限环境可能禁止打开命名管道）。
        try
        {
            using var evt = EventWaitHandle.OpenExisting(EventName);
            evt.Set();
            DesktopLog.Info("已通过命名事件唤醒已有实例。");
            return true;
        }
        catch (Exception ex)
        {
            DesktopLog.Warn("唤醒已有实例失败（两个通道都不可用，将回退到提示框）: " + DesktopLog.Describe(ex));
            return false;
        }
    }

    /// <summary>
    /// 首实例：启动唤醒监听。失败只记日志并返回 <c>null</c>，**不得影响首实例启动或使其退出**。
    /// </summary>
    internal static IDisposable? StartServer(Action onActivate)
    {
        try
        {
            if (_server != null) return _server;
            var server = new ActivationServer(onActivate);
            server.Start();
            _server = server;
            DesktopLog.Info("已启动第二实例唤醒监听（pipe=" + PipeName + "）。");
            return server;
        }
        catch (Exception ex)
        {
            DesktopLog.Warn("启动第二实例唤醒监听失败（不影响本次启动）: " + DesktopLog.Describe(ex));
            return null;
        }
    }

    /// <summary>监听循环：单次异常不得终止监听。</summary>
    private sealed class ActivationServer : IDisposable
    {
        private readonly Action _onActivate;
        private readonly CancellationTokenSource _cts = new();
        private EventWaitHandle? _eventHandle;

        internal ActivationServer(Action onActivate) => _onActivate = onActivate;

        internal void Start()
        {
            // 备用通道（命名事件）先就绪：受限环境禁止打开命名管道时它是唯一的唤醒路径。
            //
            // t35/F3 结论（选了**方案 ②：措辞精确化**，不设显式 ACL）：曾实现方案 ①（为事件传入
            // EventWaitHandleSecurity，仅授予当前用户 SID），但**实测在本环境直接打断备用通道**——
            // 同用户客户端 `EventWaitHandle.OpenExisting` 返回 `Access to the path '…activate.event'
            // is denied`（t26 默认 DACL 下是通的），即 ① 使「上一次已验证可用的唤醒能力」在本环境回归。
            // 由于 ACL 与沙箱令牌的交互无法在本环境进一步验证，且 F3 是 low 级、契约允许二选一，
            // 故不再设置显式 ACL，改为把类文档措辞改精确（见类文档与 §24.10 相关说明）。
            try
            {
                _eventHandle = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
                _ = Task.Run(() => EventLoop(_cts.Token));
            }
            catch (Exception ex)
            {
                DesktopLog.Warn("建立命名事件唤醒通道失败（仅用命名管道）: " + DesktopLog.Describe(ex));
            }
            _ = Task.Run(() => LoopAsync(_cts.Token));
        }

        /// <summary>命名事件等待循环（单次异常不得终止）。</summary>
        private void EventLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_eventHandle != null && _eventHandle.WaitOne(250))
                    {
                        DesktopLog.Info("收到第二实例唤醒信号（命名事件）。");
                        try { _onActivate(); }
                        catch (Exception ex) { DesktopLog.Warn("派发唤醒信号失败: " + DesktopLog.Describe(ex)); }
                    }
                }
                catch (Exception ex)
                {
                    DesktopLog.Warn("命名事件唤醒通道出错（继续监听）: " + DesktopLog.Describe(ex));
                    try { Thread.Sleep(200); } catch { break; }
                }
            }
        }

        private async Task LoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    var payload = await ReadBoundedAsync(server, token).ConfigureAwait(false);
                    if (payload == null)
                    {
                        DesktopLog.Warn("唤醒请求超长（>" + MaxSignalBytes + " 字节），已丢弃该连接。");
                    }
                    else if (payload.Length > 0)
                    {
                        var text = Encoding.UTF8.GetString(payload).Trim();
                        if (text == WakeSignal)
                        {
                            DesktopLog.Info("收到第二实例唤醒信号。");
                            try { _onActivate(); }
                            catch (Exception ex) { DesktopLog.Warn("派发唤醒信号失败: " + DesktopLog.Describe(ex)); }
                        }
                        else
                        {
                            DesktopLog.Warn("收到无法识别的唤醒载荷，已忽略。");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    DesktopLog.Warn("唤醒监听处理出错（继续监听）: " + DesktopLog.Describe(ex));
                    try { await Task.Delay(200, token).ConfigureAwait(false); }
                    catch { break; }
                }
                finally
                {
                    try { server?.Dispose(); } catch { /* ignore */ }
                }
            }
        }

        /// <summary>有界读取：读满上限即判超长（返回 null 表示丢弃该连接）。</summary>
        private static async Task<byte[]?> ReadBoundedAsync(NamedPipeServerStream server, CancellationToken token)
        {
            var buffer = new byte[MaxSignalBytes];
            var total = 0;
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            readTimeout.CancelAfter(ReadTimeoutMs);
            try
            {
                while (total < buffer.Length)
                {
                    var read = await server
                        .ReadAsync(buffer.AsMemory(total, buffer.Length - total), readTimeout.Token)
                        .ConfigureAwait(false);
                    if (read <= 0) break;
                    total += read;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // 读超时：按已读内容处理（空载荷视为无操作）。
            }

            if (total >= buffer.Length) return null;
            return buffer[..total];
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { /* ignore */ }
            try { _eventHandle?.Dispose(); } catch { /* ignore */ }
            try { _cts.Dispose(); } catch { /* ignore */ }
            _server = null;
        }
    }
}

/// <summary>
/// 首实例内的激活请求协调器：把管道线程收到的请求派发到 UI 线程。
/// **主窗口尚未创建时请求按「待处理」记下，窗口注册处理者后立即补做，绝不丢弃。**
/// </summary>
internal static class ActivationCoordinator
{
    private static readonly object Gate = new();
    private static Action? _handler;
    private static bool _pending;

    /// <summary>由主窗口在就绪时注册（UI 线程入口）。若此前已有待处理请求，注册后立即补做。</summary>
    internal static void SetHandler(Action handler)
    {
        var runPending = false;
        lock (Gate)
        {
            _handler = handler;
            if (_pending)
            {
                _pending = false;
                runPending = true;
            }
        }
        if (runPending)
        {
            DesktopLog.Info("补做此前挂起的第二实例唤醒请求（主窗口已就绪）。");
            Invoke();
        }
    }

    /// <summary>唤醒请求入口（可能来自管道线程）。</summary>
    internal static void Request()
    {
        var pending = false;
        lock (Gate)
        {
            if (_handler == null)
            {
                _pending = true;
                pending = true;
            }
        }
        if (pending)
        {
            DesktopLog.Info("主窗口尚未就绪，唤醒请求已挂起待补做。");
            return;
        }
        Invoke();
    }

    private static void Invoke()
    {
        Action? handler;
        lock (Gate) { handler = _handler; }
        if (handler == null) return;
        try { handler(); }
        catch (Exception ex) { DesktopLog.Warn("派发唤醒请求失败: " + DesktopLog.Describe(ex)); }
    }
}
