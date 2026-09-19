using System;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DSHDesktop.Terminal;

/// <summary>
/// 通过 Windows 伪控制台（ConPTY）托管一个真实的交互式终端进程，
/// 把它的输出喂给 <see cref="TerminalScreen"/>，并把键盘输入写回它的标准输入。
/// </summary>
public sealed class ConPtySession : IDisposable
{
    private readonly TerminalScreen _screen;
    private readonly object _writeLock = new();
    private AnonymousPipeServerStream? _inputPipe;
    private AnonymousPipeServerStream? _outputPipe;
    private IntPtr _hpc = IntPtr.Zero;
    private IntPtr _hProcess = IntPtr.Zero;
    private int _pid;
    private Thread? _reader;
    private volatile bool _disposed;
    private volatile bool _exited;

    /// <summary>收到新的终端输出（在读取线程上触发）。</summary>
    public event Action? OutputAvailable;

    /// <summary>终端进程退出。</summary>
    public event Action? ProcessExited;

    public string ShellPath { get; }
    public string ShellDisplayName { get; }
    public bool HasExited => _exited;

    /// <summary>额外的 PATH 前缀（如内置 node 目录与 dsh 的 .bin 目录），只影响这个终端子进程。</summary>
    public string? ExtraPath { get; }

    public ConPtySession(TerminalScreen screen, string shellPath, string shellDisplayName,
        string? workingDirectory, string? extraPath = null, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        _screen = screen;
        ShellPath = shellPath;
        ShellDisplayName = shellDisplayName;
        ExtraPath = string.IsNullOrWhiteSpace(extraPath) ? null : extraPath;
        Start(workingDirectory, extraEnv);
    }

    /// <summary>挑一个可用的 shell：优先 PowerShell 7，其次 Windows PowerShell，最后 cmd。</summary>
    public static (string Path, string Name) FindShell()
    {
        var candidates = new (string Path, string Name)[]
        {
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"PowerShell\7\pwsh.exe"), "PowerShell 7"),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\pwsh.exe"), "PowerShell 7"),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"), "Windows PowerShell"),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), "命令提示符"),
        };
        foreach (var (path, name) in candidates)
            if (File.Exists(path)) return (path, name);
        return (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), "命令提示符");
    }

    private void Start(string? workingDirectory, IReadOnlyDictionary<string, string>? extraEnv)
    {
        _inputPipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        _outputPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);

        var size = new COORD { X = (short)Math.Clamp(_screen.Columns, 2, 500), Y = (short)Math.Clamp(_screen.Rows, 2, 300) };
        var hr = CreatePseudoConsole(size, _inputPipe.ClientSafePipeHandle.DangerousGetHandle(),
            _outputPipe.ClientSafePipeHandle.DangerousGetHandle(), 0, out _hpc);
        if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole 失败");

        var startup = new STARTUPINFOEX();
        startup.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        var attrSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
        var attrList = Marshal.AllocHGlobal(attrSize);
        try
        {
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList 失败");
            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    _hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute 失败");
            startup.lpAttributeList = attrList;

            // PowerShell 默认受本机执行策略约束（本机为 AllSigned），会让 npm.ps1 / pnpm.ps1 等
            // 命令解析脚本直接报 SecurityError，导致集成终端里 npm/node 工具链不可用。
            // 这里只对本进程放行（-ExecutionPolicy Bypass 不修改任何机器级设置）。
            var commandLine = new StringBuilder("\"" + ShellPath + "\"");
            if (IsPowerShell(ShellPath)) commandLine.Append(" -NoLogo -ExecutionPolicy Bypass");
            const uint flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT;
            // 只有需要注入额外 PATH / 环境变量时才自建环境块，避免影响其它行为
            var envBlock = ExtraPath == null && extraEnv == null
                ? IntPtr.Zero
                : BuildEnvironmentBlock(ExtraPath, extraEnv);
            try
            {
                // bInheritHandles 必须为 false：伪控制台通过属性附加，子进程的标准句柄由
                // 伪控制台接管；若允许继承，父进程自己的控制台句柄会被子进程沿用，
                // 输出就不会进入伪控制台（在带控制台的父进程里会直接漏到父控制台）。
                if (!CreateProcessW(ShellPath, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags,
                        envBlock, workingDirectory, ref startup, out var pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess 失败");

                _hProcess = pi.hProcess;
                _pid = pi.dwProcessId;
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            }
            finally
            {
                if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
            }
        }
        finally
        {
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
        }

        _inputPipe.DisposeLocalCopyOfClientHandle();
        _outputPipe.DisposeLocalCopyOfClientHandle();

        // 终端对应用查询（DA1/DECRQM/DSR/OSC 等）的应答要写回子进程的标准输入，
        // 否则 TUI 的能力探测只能按超时降级。
        _screen.ResponseReady += OnScreenResponse;

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ConPTY reader" };
        _reader.Start();
    }

    private void OnScreenResponse(string sequence) => Write(sequence);

    /// <summary>
    /// 复制当前进程环境，给 PATH 加上前缀并覆盖/追加额外变量，返回 Unicode 环境块（用完后需 FreeHGlobal）。
    /// </summary>
    private static IntPtr BuildEnvironmentBlock(string? extraPath, IReadOnlyDictionary<string, string>? extraEnv)
    {
        var sb = new StringBuilder();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key || key.Length == 0) continue;
            var value = entry.Value as string ?? "";
            if (extraPath != null && key.Equals("PATH", StringComparison.OrdinalIgnoreCase))
                value = extraPath + ";" + value;
            if (extraEnv != null && extraEnv.TryGetValue(key, out var overridden)) value = overridden;
            written.Add(key);
            sb.Append(key).Append('=').Append(value).Append('\0');
        }
        if (extraEnv != null)
        {
            foreach (var pair in extraEnv)
            {
                if (written.Contains(pair.Key)) continue;
                sb.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
            }
        }
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    private static bool IsPowerShell(string shellPath)
    {
        var name = Path.GetFileName(shellPath);
        return name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>原始（已解码、未解析）终端输出，调试用。</summary>
    internal event Action<string>? RawOutput;

    private void ReadLoop()
    {
        var buffer = new byte[8192];
        var chars = new char[8192];
        var decoder = Encoding.UTF8.GetDecoder();
        try
        {
            while (!_disposed)
            {
                int read;
                try { read = _outputPipe!.Read(buffer, 0, buffer.Length); }
                catch (Exception) { break; }
                if (read <= 0) break;

                var count = decoder.GetChars(buffer, 0, read, chars, 0);
                if (count > 0)
                {
                    var chunk = new string(chars, 0, count);
                    try { RawOutput?.Invoke(chunk); } catch { /* ignore */ }
                    _screen.Feed(chunk);
                    OutputAvailable?.Invoke();
                }
            }
        }
        catch (Exception) { /* 读取失败按进程结束处理 */ }
        finally
        {
            if (!_exited)
            {
                _exited = true;
                try { ProcessExited?.Invoke(); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>把键盘输入写进终端进程。</summary>
    public void Write(string text)
    {
        if (_disposed || string.IsNullOrEmpty(text) || _inputPipe == null) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            lock (_writeLock)
            {
                _inputPipe.Write(bytes, 0, bytes.Length);
                _inputPipe.Flush();
            }
        }
        catch (Exception) { /* 进程已退出 */ }
    }

    /// <summary>同步终端尺寸到伪控制台。</summary>
    public void Resize(int columns, int rows)
    {
        if (_disposed || _hpc == IntPtr.Zero) return;
        var size = new COORD { X = (short)Math.Clamp(columns, 2, 500), Y = (short)Math.Clamp(rows, 2, 300) };
        try { ResizePseudoConsole(_hpc, size); } catch (Exception) { /* ignore */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _screen.ResponseReady -= OnScreenResponse; } catch { /* ignore */ }

        if (_pid > 0)
        {
            try
            {
                using var proc = Process.GetProcessById(_pid);
                if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            }
            catch { /* ignore */ }
        }

        if (_hpc != IntPtr.Zero)
        {
            try { ClosePseudoConsole(_hpc); } catch { /* ignore */ }
            _hpc = IntPtr.Zero;
        }

        try { _inputPipe?.Dispose(); } catch { /* ignore */ }
        try { _outputPipe?.Dispose(); } catch { /* ignore */ }
        _inputPipe = null;
        _outputPipe = null;

        if (_hProcess != IntPtr.Zero)
        {
            try { CloseHandle(_hProcess); } catch { /* ignore */ }
            _hProcess = IntPtr.Zero;
        }
    }

    // ------------------------------------------------------------------ P/Invoke

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
