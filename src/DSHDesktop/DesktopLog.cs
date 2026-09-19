using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DSHDesktop.Core;

namespace DSHDesktop;

/// <summary>日志级别，数值越大越严重；阈值以下的记录不落盘。</summary>
internal enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>
/// 桌面端落地日志。设计目标（对齐 anywhere-labs/dsh-desktop 的 desktop-logging 笔记）：
///
/// <list type="bullet">
/// <item>按天分文件：<c>dsh-YYYY-MM-DD.log</c>（全量）与 <c>dsh-YYYY-MM-DD.error.log</c>（Warn 及以上）。</item>
/// <item><b>同一天内跨重启续写同一个文件</b>——重启不再产生新分段，历史保留更完整。</item>
/// <item>单文件超过 10MB 才轮转为 <c>.1</c>/<c>.2</c>/<c>.3</c>；日志目录超过 200MB 时删最旧的自己人文件。</item>
/// <item>启动时清理 7 天前的日志，并写一行启动头（版本 / 系统 / .NET / 运行时刻）。</item>
/// <item>所有落盘内容先过通用密钥脱敏（sk- 密钥、长 hex/base64、Bearer/Basic、Cookie、具名字段、URL 凭据）。</item>
/// <item><b>任何失败都降级到脱敏 stderr，绝不抛出、绝不阻塞启动</b>——日志系统自己坏掉不能拖垮应用。</item>
/// </list>
///
/// 之所以存在：dsh 子进程的 stdout/stderr 此前只被追加到一个界面标签上，关窗即丢；
/// 而"装完插件整个 profile 起不来"这类问题的全部证据都在那几行里。
/// </summary>
internal static class DesktopLog
{
    /// <summary>单个日志文件的上限，超过即轮转。</summary>
    private const long MaxFileBytes = 10L * 1024 * 1024;

    /// <summary>日志目录总量上限，超过即删最旧的自己人文件。</summary>
    private const long MaxDirBytes = 200L * 1024 * 1024;

    /// <summary>启动时清理超过这个天数的日志。</summary>
    private const int RetainDays = 7;

    /// <summary>每个日期保留的轮转分段数（.1 .2 .3）。</summary>
    private const int MaxSegments = 3;

    /// <summary>写多少条后复查一次目录总量，避免每行都去 stat 目录。</summary>
    private const int DirCheckEvery = 200;

    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static string? _dir;
    private static bool _degraded;
    private static int _writesSinceDirCheck;
    private static bool _initialized;

    /// <summary>当前阈值；低于它的记录被丢弃。</summary>
    public static LogLevel Threshold { get; set; } = LogLevel.Info;

    /// <summary>日志目录；未初始化或初始化失败时为 null。</summary>
    public static string? Directory => _dir;

    /// <summary>日志系统是否已降级（初始化或写入失败）。</summary>
    public static bool Degraded => _degraded;

    /// <summary>
    /// 初始化日志目录、清理过期文件、写启动头。可安全重复调用。
    /// 失败时降级：<see cref="Degraded"/> 置真，后续写入走 stderr，不抛出。
    /// </summary>
    public static void Initialize(string? dirOverride = null)
    {
        lock (Gate)
        {
            if (_initialized && _dir != null) return;
            try
            {
                var dir = dirOverride ?? DefaultDirectory();
                System.IO.Directory.CreateDirectory(dir);
                _dir = dir;
                PurgeExpired(dir);
                EnforceDirectoryCap(dir);
                MaintainDiagnosticArchives(dir);
                _initialized = true;
                Write(LogLevel.Info, "=== DSH Desktop 启动 " + BuildHeader() + " ===");
            }
            catch (Exception ex)
            {
                Degrade("初始化失败: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// 默认日志目录：<c>%LOCALAPPDATA%\DSHDesktop\logs</c>。
    /// 可用 <c>DSH_DESKTOP_LOG_DIR</c> 覆盖（便携部署与自动化验证用）。
    /// </summary>
    public static string DefaultDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable("DSH_DESKTOP_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
        return Path.Combine(root, "DSHDesktop", "logs");
    }

    private static string BuildHeader()
    {
        var asm = typeof(DesktopLog).Assembly.GetName().Version?.ToString() ?? "?";
        return string.Join(" | ",
            "app=" + asm,
            "os=" + Environment.OSVersion.VersionString,
            "framework=" + Environment.Version,
            "pid=" + Environment.ProcessId.ToString());
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);

    /// <summary>记录一条错误；可附带异常（含类型、消息与堆栈）。</summary>
    public static void Error(string message, Exception? ex = null)
        => Write(LogLevel.Error, ex == null ? message : message + " :: " + Describe(ex));

    /// <summary>把异常展开成一行可检索的文本（类型 + 消息 + 首帧）。</summary>
    public static string Describe(Exception ex)
    {
        var first = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
        return ex.GetType().Name + ": " + ex.Message + (first == null ? "" : " @ " + first);
    }

    /// <summary>
    /// 按 <paramref name="level"/> 写一条记录。低于阈值直接丢弃。
    /// 全流程 try/catch：日志写入自身失败只降级，绝不向上抛。
    /// </summary>
    public static void Write(LogLevel level, string message)
    {
        if (level < Threshold) return;
        var line = Format(level, message);
        lock (Gate)
        {
            if (_degraded || _dir == null)
            {
                DegradeWrite(line);
                return;
            }
            try
            {
                var now = DateTime.Now;
                // 续写：不再因为"今天的文件已经存在"就轮转，否则每次重启都会把
                // 上一次运行的日志挤到 .1 去。分段只由体积触发（见 Append）。
                Append(DayFile(_dir, now), line);
                if (level >= LogLevel.Warn) Append(DayErrorFile(_dir, now), line);
                if (++_writesSinceDirCheck >= DirCheckEvery)
                {
                    _writesSinceDirCheck = 0;
                    EnforceDirectoryCap(_dir);
                }
            }
            catch (Exception ex)
            {
                Degrade("写入失败: " + ex.Message);
                DegradeWrite(line);
            }
        }
    }

    private static string Format(LogLevel level, string message)
    {
        var tag = level switch
        {
            LogLevel.Debug => "DEBUG",
            LogLevel.Warn => "WARN ",
            LogLevel.Error => "ERROR",
            _ => "INFO ",
        };
        var body = Mask(message ?? "");
        // 单条记录也要有上限，避免某个插件刷出一行几百 MB
        if (body.Length > 64 * 1024) body = body[..(64 * 1024)] + "…(截断)";
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + tag + "] " + body;
    }

    // ---------- 文件与轮转 ----------

    private static string DayFile(string dir, DateTime now) => Path.Combine(dir, "dsh-" + now.ToString("yyyy-MM-dd") + ".log");
    private static string DayErrorFile(string dir, DateTime now) => Path.Combine(dir, "dsh-" + now.ToString("yyyy-MM-dd") + ".error.log");

    private static void Append(string path, string line)
    {
        var bytes = Utf8NoBom.GetByteCount(line) + 1;
        if (File.Exists(path) && new FileInfo(path).Length + bytes > MaxFileBytes) Rotate(path);
        File.AppendAllText(path, line + Environment.NewLine, Utf8NoBom);
    }

    /// <summary>把 path 轮转成 .1，原来的 .1→.2，最多保留 MaxSegments 段。</summary>
    private static void Rotate(string path)
    {
        try
        {
            for (var i = MaxSegments; i >= 1; i--)
            {
                var from = i == 1 ? path : path + "." + (i - 1);
                var to = path + "." + i;
                if (!File.Exists(from)) continue;
                if (File.Exists(to)) File.Delete(to);
                File.Move(from, to);
            }
        }
        catch (Exception ex)
        {
            Degrade("轮转失败(" + Path.GetFileName(path) + "): " + ex.Message);
        }
    }

    /// <summary>只认自己人的文件名，避免误删用户放进日志目录的东西。</summary>
    private static bool IsOwnedLogName(string name)
        => name.StartsWith("dsh-", StringComparison.OrdinalIgnoreCase)
           && (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
               || Regex.IsMatch(name, @"\.log\.\d+$", RegexOptions.IgnoreCase));

    private static void PurgeExpired(string dir)
    {
        var cutoff = DateTime.Now.AddDays(-RetainDays);
        foreach (var file in SafeFiles(dir))
        {
            try
            {
                if (!IsOwnedLogName(Path.GetFileName(file))) continue;
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
            catch { /* 单个文件删不掉不影响其余 */ }
        }
    }

    /// <summary>目录总量超限时，按最后写入时间从旧到新删自己人文件（至少留一个）。</summary>
    private static void EnforceDirectoryCap(string dir)
    {
        try
        {
            var owned = SafeFiles(dir)
                .Where(f => IsOwnedLogName(Path.GetFileName(f)))
                .Select(f => new FileInfo(f))
                .Where(f => f.Exists)
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();
            var total = owned.Sum(f => f.Length);
            if (total <= MaxDirBytes || owned.Count <= 1) return;
            foreach (var file in owned)
            {
                if (total <= MaxDirBytes || owned.Count <= 1) break;
                if (owned.IndexOf(file) == owned.Count - 1) break; // 永远保留最新的那个
                try { total -= file.Length; file.Delete(); } catch { }
            }
        }
        catch (Exception ex)
        {
            Degrade("清理目录失败: " + ex.Message);
        }
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return System.IO.Directory.GetFiles(dir); }
        catch { return Array.Empty<string>(); }
    }

    private static void MaintainDiagnosticArchives(string dir)
    {
        try
        {
            var result = DiagnosticArchiveRetention.Prune(dir);
            if (!result.Success)
                Warn("Diagnostic archive maintenance did not finish: " + string.Join(", ", result.Errors.Take(3)));
        }
        catch (Exception ex)
        {
            DegradeWrite("Diagnostic archive maintenance failed: " + ex.Message);
        }
    }

    // ---------- 降级 ----------

    private static void Degrade(string reason)
    {
        _degraded = true;
        try { Console.Error.WriteLine("[DSHDesktop/log] " + Mask(reason)); } catch { }
    }

    private static void DegradeWrite(string line)
    {
        try { Console.Error.WriteLine("[DSHDesktop/log] " + line); } catch { }
    }

    // ---------- 导出诊断 ----------

    /// <summary>
    /// 把最近的日志与一份系统信息摘要打成一个 zip。返回 null 表示失败（已降级记录）。
    /// 打包在调用方线程外做（见 MainWindow 的调用点），避免阻塞 UI。
    /// </summary>
    public static string? ExportDiagnostics(string targetZip, long maxBytes = 50L * 1024 * 1024)
    {
        try
        {
            var dir = _dir;
            if (dir == null || !System.IO.Directory.Exists(dir)) return null;
            var files = SafeFiles(dir)
                .Where(f => IsOwnedLogName(Path.GetFileName(f)))
                .Select(f => new FileInfo(f))
                .Where(f => f.Exists)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            var staging = Path.Combine(Path.GetTempPath(), "dsh-desktop-diag-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(staging);
            try
            {
                var taken = new List<string>();
                long budget = maxBytes;
                foreach (var f in files)
                {
                    if (budget - f.Length < 0) break;
                    budget -= f.Length;
                    var dest = Path.Combine(staging, f.Name);
                    try { f.CopyTo(dest, overwrite: true); taken.Add(f.Name); } catch { }
                }

                File.WriteAllText(Path.Combine(staging, "system-info.txt"), SystemInfo(taken, files.Count), Utf8NoBom);

                var parent = Path.GetDirectoryName(targetZip);
                if (!string.IsNullOrEmpty(parent)) System.IO.Directory.CreateDirectory(parent);
                if (File.Exists(targetZip)) File.Delete(targetZip);
                ZipFile.CreateFromDirectory(staging, targetZip, CompressionLevel.Optimal, includeBaseDirectory: false);
                return targetZip;
            }
            finally
            {
                try { System.IO.Directory.Delete(staging, recursive: true); } catch { }
            }
        }
        catch (Exception ex)
        {
            Error("导出诊断失败", ex);
            return null;
        }
    }

    private static string SystemInfo(IReadOnlyCollection<string> included, int available)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DSH Desktop 诊断包");
        sb.AppendLine("生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("应用版本: " + (typeof(DesktopLog).Assembly.GetName().Version?.ToString() ?? "?"));
        sb.AppendLine("操作系统: " + Environment.OSVersion.VersionString);
        sb.AppendLine("运行时: " + Environment.Version + " (" + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + ")");
        sb.AppendLine("进程架构: " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
        sb.AppendLine("机器名: " + Environment.MachineName);
        sb.AppendLine("日志条数: 收录 " + included.Count + " / 共 " + available);
        if (included.Count > 0) sb.AppendLine("收录文件: " + string.Join(", ", included));
        sb.AppendLine();
        sb.AppendLine("注意: 日志可能包含本机路径、工作区与会话标识；请自行确认后再分享。");
        return sb.ToString();
    }

    // ---------- 脱敏 ----------

    /// <summary>一条脱敏规则：正则 + 替换文本（$1 形式可保留字段名，读日志时不丢上下文）。</summary>
    private readonly record struct MaskRule(Regex Pattern, string Replacement);

    private static readonly MaskRule[] Maskers =
    {
        // sk- / 常见厂商前缀密钥
        new(new Regex(@"\b(sk|pk|rk|api|key|token)[-_][A-Za-z0-9_\-]{8,}\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase), "[已脱敏]"),
        // Authorization: Bearer/Basic xxx —— 保留 scheme，只遮凭据
        new(new Regex(@"\b(bearer|basic)\s+[A-Za-z0-9\-._~+/=]{8,}",
            RegexOptions.Compiled | RegexOptions.IgnoreCase), "$1 [已脱敏]"),
        // Cookie / Set-Cookie 整行值
        new(new Regex(@"(?<=set-cookie\s*[:=]\s*)[^\r\n]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase), "[已脱敏]"),
        new(new Regex(@"(?<=cookie\s*[:=]\s*)[^\r\n]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase), "[已脱敏]"),
        // JSON 形态 "apiKey":"..." —— 用变长后行断言锚在值上，保留字段名，只遮值。
        // （此前写成「前瞻 + 消费」两段都要求冒号，实际匹配不上，值会原样落盘。）
        new(new Regex(@"(?<=""(?:password|passwd|secret|api[_-]?key|access[_-]?token|refresh[_-]?token|authorization|client[_-]?secret)""\s*:\s*"")[^""]*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase), "[已脱敏]"),
        // 非 JSON 形态 password=xxx / secret: xxx —— 保留字段名与分隔符
        new(new Regex(@"\b(password|passwd|secret|api[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret)\b(\s*[:=]\s*)(""[^""]*""|'[^']*'|[^\s,;&]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase), "$1$2[已脱敏]"),
        // URL 里的 user:pass@
        new(new Regex(@"(?<=://)[^/\s:@]+:[^/\s@]+(?=@)",
            RegexOptions.Compiled), "[已脱敏]"),
        // URL 查询参数里的敏感值（保留参数名）
        new(new Regex(@"(?<=[?&])(token|access_token|api_key|apikey|key|password|secret|signature|sig)=[^&\s]*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase), "$1=[已脱敏]"),
        // 长 hex / 长 base64 串，通常就是 token 或摘要
        new(new Regex(@"\b[A-Fa-f0-9]{32,}\b", RegexOptions.Compiled), "[已脱敏]"),
        new(new Regex(@"\b[A-Za-z0-9+/]{40,}={0,2}\b", RegexOptions.Compiled), "[已脱敏]"),
    };

    /// <summary>
    /// 通用密钥脱敏。宁可多遮一点，也不要把凭据写进磁盘。
    /// 例外：<c>dsh web:</c> 那行本身是启动地址（含一次性 token），由 <see cref="RedactUrl"/> 单独处理。
    /// </summary>
    public static string Mask(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var result = text;
        foreach (var rule in Maskers)
        {
            try { result = rule.Pattern.Replace(result, rule.Replacement); }
            catch { /* 单条规则失败不影响其余 */ }
        }
        return result;
    }

    /// <summary>启动地址行只留端口，去掉 token（写日志与状态栏都用它）。</summary>
    public static string RedactUrl(string url)
    {
        try
        {
            var i = url.IndexOf("token=", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return url;
            // 分隔符取决于去掉尾部分隔符后 head 里是否已有查询串，否则会产出 ".../&token="
            var head = url[..i].TrimEnd('?', '&');
            var sep = head.Contains('?') ? "&" : "?";
            return head + sep + "token=[已脱敏]";
        }
        catch { return "[已脱敏地址]"; }
    }
}
