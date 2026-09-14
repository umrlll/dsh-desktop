using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DSHDesktop;

/// <summary>
/// 为 dsh 进程树准备一个<b>可用</b>的 pnpm。
///
/// 为什么需要：dsh-market 的「安装」按钮最终走 <c>dsh plugin --profile &lt;p&gt; add …</c>，
/// 而 <c>dsh plugin</c> 会 <c>spawnSync("pnpm", …, { shell: true })</c>——用 PATH 上找到的
/// 那一个。本机的 pnpm 12.3.4 是 Rust 重写版，它的 HTTP 客户端在这台机器上连不上
/// registry（<c>os error 10049</c> / <c>ERR_PNPM_PING_ERROR</c>），Node 版 pnpm 则正常。
/// 表现就是市场里点安装必失败，而手工用 Node 版 pnpm 装同一个插件完全成功。
///
/// 做法：只有当 PATH 上的 pnpm <b>确实不可用</b>（<c>pnpm ping</c> 失败或找不到）时，
/// 才在 <c>%LOCALAPPDATA%\DSHDesktop\pnpm\&lt;major&gt;</c> 里准备一个 Node 版 pnpm，
/// 并写一份垫片目录前置进 dsh 进程树的 PATH。垫片只作用于本应用启动的 dsh 及其子进程，
/// 不改系统环境变量，也不动用户 shell 里的 pnpm。
///
/// 可用 <c>DSHDESKTOP_PNPM</c> 覆盖：<c>off</c> 关闭该行为，<c>on</c> 强制使用内置版，
/// 或直接给出一个 <c>pnpm.cjs</c> 路径。
/// </summary>
internal static class PnpmSupport
{
    /// <summary>默认内置大版本：与这个 profile 的 pnpm store（v11）匹配，且是最后的纯 JS 版。</summary>
    private const int DefaultMajor = 11;

    private static readonly TimeSpan HealthTtl = TimeSpan.FromHours(6);

    /// <summary>
    /// 垫片目录的**稳定路径**（<c>%LOCALAPPDATA%\DSHDesktop\bin</c>，与 <see cref="WriteShims"/>
    /// 的落点一致），并确保目录存在。用于把它**无条件**前置进 dsh 子进程的 PATH：
    /// 预热（<see cref="Prepare"/>）还没跑完时该目录可能仍是空的，但 PATH 的解析发生在用户
    /// 稍后点「安装插件」的那一刻（子进程届时才去 exec pnpm），届时垫片早已写好——
    /// 于是「等 pnpm 探测」不必再压在启动关键路径上。
    ///
    /// 这也是垫片目录的**唯一权威来源**：先前那份"进程内是否已就绪"的静态状态
    /// （只有"已就绪"才进 PATH）正是把 pnpm 探测顶到启动关键路径上的原因，故已移除。
    /// </summary>
    public static string ShimRootDirectory()
    {
        var dir = ShimRoot();
        try { Directory.CreateDirectory(dir); } catch { /* 建不出来只会让垫片不生效，不影响启动 */ }
        return dir;
    }

    /// <summary>一次准备的结果，用于状态栏提示。</summary>
    public readonly record struct Preparation(bool Shimmed, string? ShimDir, string? Notice);

    /// <summary>应用目录下属于自己的数据根。</summary>
    private static string AppDataRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSHDesktop");
        return root;
    }

    private static string ShimRoot() => Path.Combine(AppDataRoot(), "bin");
    private static string InstallRoot(int major) => Path.Combine(AppDataRoot(), "pnpm", "v" + major);
    private static string HealthFile() => Path.Combine(AppDataRoot(), "pnpm-health.json");

    /// <summary>
    /// 决定这次启动用哪个 pnpm，必要时准备内置版并生成垫片。
    /// 任何一步失败都只是"不启用"，绝不抛出、绝不阻断启动。
    /// </summary>
    public static Preparation Prepare(string profileDir)
    {
        try
        {
            var mode = (Environment.GetEnvironmentVariable("DSHDESKTOP_PNPM") ?? string.Empty).Trim();

            if (mode.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                ClearShims();
                return new Preparation(false, null, null);
            }

            // 显式给出 pnpm.cjs / pnpm.mjs 路径
            if (mode.Length > 0 && !mode.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(mode))
                {
                    ClearShims();
                    return new Preparation(false, null, $"DSHDESKTOP_PNPM 指向的文件不存在：{mode}");
                }
                var shim = WriteShims(mode);
                return new Preparation(true, shim, "已按 DSHDESKTOP_PNPM 指定的 pnpm 生成垫片。");
            }

            var forced = mode.Equals("on", StringComparison.OrdinalIgnoreCase);
            if (!forced)
            {
                // 本机 pnpm 可用就不要多事——垫片只用来修"不可用"这一种情况。
                // 但**必须清掉上次留下的垫片**：垫片目录现在无条件前置在子进程 PATH 上
                // （见 ShimRootDirectory），陈旧垫片会盖住本机这份健康的 pnpm。
                var health = MachinePnpmHealthy();
                if (health != false)
                {
                    ClearShims();
                    return new Preparation(false, null, null);
                }
            }

            // 由新到旧尝试，并且**装完必须自己验一次能不能连上 registry**：
            // 只把真正可用的那个拿去当垫片，否则等于把市场的失败换了个报错。
            var attempted = new List<string>();
            foreach (var major in Candidates(RequestedMajor(profileDir)))
            {
                var entry = EnsureLocalPnpm(major, out var installNotice);
                if (entry == null)
                {
                    attempted.Add($"{major}: {installNotice}");
                    continue;
                }
                if (!PnpmEntryReachesRegistry(entry))
                {
                    attempted.Add($"{major}: 连不上 registry");
                    continue;
                }

                var shimDir = WriteShims(entry);
                return new Preparation(true, shimDir, BuildNotice(major, entry));
            }

            ClearShims();
            return new Preparation(false, null,
                "内置 pnpm 均不可用（" + string.Join("；", attempted) + "）。");
        }
        catch (Exception ex)
        {
            ClearShims();
            return new Preparation(false, null, "准备内置 pnpm 失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 删除本应用写的垫片（只删自己认识的文件名）。用在"本次不启用垫片"的分支：
    /// 垫片目录已无条件前置进子进程 PATH，若不清掉上一次留下的陈旧垫片，它会盖住
    /// 本机那份健康的 pnpm——正好与"只在本机 pnpm 不可用时才垫片"的意图相反。
    /// </summary>
    private static void ClearShims()
    {
        try
        {
            var dir = ShimRoot();
            if (!Directory.Exists(dir)) return;
            foreach (var name in new[] { "pnpm.cmd", "pn.cmd", "pnpx.cmd" })
            {
                var path = Path.Combine(dir, name);
                try { if (File.Exists(path)) File.Delete(path); } catch { /* 单个删不掉不影响其余 */ }
            }
        }
        catch { /* 清理失败只影响本次选择，不影响启动 */ }
    }

    /// <summary>
    /// 内置 pnpm 的候选大版本，由新到旧。首选来自 <c>DSHDESKTOP_PNPM_VERSION</c> 或 profile 的
    /// store 版本，然后逐级下降——原生版（12.x）在 DNS 只有 AAAA、本机却没有可用 IPv6 的网络里
    /// 连不上 registry，而 Node 版会自动回退到 IPv4，所以"最新"不等于"可用"。
    /// </summary>
    internal static IEnumerable<int> Candidates(int preferred)
    {
        var yielded = new List<int>();
        for (var major = preferred; major >= 9 && yielded.Count < 3; major--)
        {
            yielded.Add(major);
            yield return major;
        }
    }

    /// <summary>说明这次为什么换了 pnpm；能识别出 IPv6 缺失时直接点明，免得下次又去追版本。</summary>
    private static string BuildNotice(int major, string entry)
    {
        var tail = HasUsableIpv6()
            ? "本机 pnpm 无法访问 registry"
            : "本机没有可用的 IPv6 公网地址，而原生版 pnpm（12.x）连不上只解析出 AAAA 的 registry；Node 版会自动回退到 IPv4";
        return $"{tail}，已改用内置 Node 版 pnpm {major}（{entry}）。";
    }

    /// <summary>本机是否存在可用的公网 IPv6（有地址且有默认路由）。</summary>
    internal static bool HasUsableIpv6()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                var routed = false;
                foreach (var gateway in props.GatewayAddresses)
                {
                    if (gateway.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) continue;
                    if (gateway.Address.Equals(System.Net.IPAddress.IPv6Any)) continue;
                    routed = true;
                    break;
                }
                if (!routed) continue;
                foreach (var unicast in props.UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) continue;
                    if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) continue;
                    return true;
                }
            }
        }
        catch
        {
            // 读不出来就当作"有"，避免把原因说错
            return true;
        }
        return false;
    }

    /// <summary>用即将交付的那个 pnpm 条目做一次真实请求，确认它在这台机器上真的能到 registry。</summary>
    private static bool PnpmEntryReachesRegistry(string entry)
    {
        var node = MainWindow.FindNode();
        if (node == null) return false;
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = MarketSupport.PrependToPath(Environment.GetEnvironmentVariable("PATH")),
        };
        var (code, _) = RunCapture(node, new[] { entry, "ping" }, 40_000, env);
        return code == 0;
    }

    private static int RequestedMajor(string profileDir)
    {
        var raw = (Environment.GetEnvironmentVariable("DSHDESKTOP_PNPM_VERSION") ?? string.Empty).Trim();
        if (int.TryParse(raw, out var forced) && forced >= 8 && forced <= 20) return forced;
        return StoreMajor(profileDir);
    }

    /// <summary>
    /// 从 profile 的 <c>node_modules/.modules.yaml</c> 的 storeDir 推断该用哪个 pnpm 大版本。
    ///
    /// 这个数字不能随便取：node_modules 是某个具体的 store 链接出来的，pnpm 大版本与 store
    /// 版本不匹配时会直接拒绝一切安装与卸载（<c>ERR_PNPM_UNEXPECTED_STORE</c>，本项目已经
    /// 踩过一次：用 pnpm 10 动 v11 的 store）。所以优先跟随 profile 已有的 store 版本。
    /// </summary>
    internal static int StoreMajor(string profileDir)
    {
        try
        {
            var file = Path.Combine(profileDir, "node_modules", ".modules.yaml");
            if (!File.Exists(file)) return DefaultMajor;
            var match = System.Text.RegularExpressions.Regex.Match(
                File.ReadAllText(file), @"store[\\/]v(?<major>\d{1,2})\b");
            if (!match.Success) return DefaultMajor;
            var major = int.Parse(match.Groups["major"].Value);
            return major is >= 8 and <= 20 ? major : DefaultMajor;
        }
        catch
        {
            return DefaultMajor;
        }
    }

    // ---------------------------------------------------------------- 健康判断

    /// <summary>
    /// 该路径是否落在本应用的垫片目录内。垫片是<b>本应用的产物</b>，不能当作"本机的 pnpm"
    /// 参与健康判断，否则会自指（见 <see cref="ProbePath"/>）。
    /// </summary>
    private static bool IsShimPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var root = ShimRoot().TrimEnd('\\', '/');
            var full = Path.GetFullPath(path!).TrimEnd('\\', '/');
            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                   || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;   // 路径非法就当作"不是垫片"，宁可多探一次
        }
    }

    /// <summary>
    /// 健康探针专用的 PATH：与注入 dsh 的那份（<see cref="MarketSupport.PrependToPath"/>）
    /// 同源，但**剔除本应用自己的垫片目录**。
    ///
    /// 为什么必须剔除：探针要回答的是「<b>本机</b>那份 pnpm 能不能连上 registry」。若探针 PATH
    /// 里还带着垫片目录，第一次 <see cref="Prepare"/> 写完垫片之后，第二次探针解析到的就是
    /// 这个垫片本身 → 命中缓存判「本机健康」→ 走"本机没问题"分支执行 <see cref="ClearShims"/>
    /// → 垫片被自己删掉，dsh 与市场又回到那个连不上 registry 的原生 pnpm。
    ///
    /// 2026-09-12 实测就是这个状态：<c>%LOCALAPPDATA%\DSHDesktop\bin</c> 是空目录，而
    /// pnpm-health.json 仍写着 <c>bin\pnpm.cmd, healthy: true</c>（两个文件 mtime 同秒），
    /// 结果市场里的安装全部失败在 pnpm 这一步。
    /// </summary>
    private static string ProbePath()
    {
        var parts = new List<string>();
        foreach (var dir in MarketSupport.ToolDirs())
        {
            if (IsShimPath(dir)) continue;
            parts.Add(dir);
        }
        parts.Add(Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        return string.Join(';', parts);
    }

    /// <summary>
    /// <b>本机</b> PATH 上的 pnpm 能不能真正连上 registry。null 表示"探不出来"——此时按可用
    /// 处理，不去替换用户的 pnpm。
    ///
    /// 探测用的 PATH 与将要注入 dsh 的那份同源，但剔除了本应用自己的垫片（<see cref="ProbePath"/>）：
    /// 只有本机那份 pnpm 才代表"用户环境能不能装插件"，而垫片是我们要不要启用它的<b>结论</b>。
    /// </summary>
    private static bool? MachinePnpmHealthy()
    {
        var probePath = ProbePath();
        var probeEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = probePath,
        };
        var exe = ResolveOnPath("pnpm", probePath);
        var cached = ReadHealthCache();
        if (cached != null
            && !IsShimPath(cached.Pnpm)                     // 垫片的体检结果不算本机的
            && File.Exists(cached.Pnpm ?? string.Empty)      // 文件被删/挪走即失效
            && string.Equals(cached.Pnpm ?? string.Empty, exe ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && DateTimeOffset.UtcNow - cached.CheckedAt < HealthTtl)
        {
            return cached.Healthy;
        }

        if (exe == null)
        {
            // 找不到 pnpm 就是"不可用"；此时缓存路径为空，下次仍会重新解析
            WriteHealthCache(null, false);
            return false;
        }

        // ping 是一个真实请求：能连通才说明这个 pnpm 的 HTTP 栈在本机可用。
        // 只看版本号探不出这类故障——连不上 registry 的 pnpm 12 照样能打印版本号。
        // 注意这里必须用剔除垫片后的 PATH：否则 ping 的其实是自己的垫片。
        var (code, _) = RunCapture("cmd.exe", new[] { "/d", "/s", "/c", "pnpm ping" }, 30_000, probeEnv);
        var healthy = code == 0;
        WriteHealthCache(exe, healthy);
        return healthy;
    }

    private sealed record HealthCache(DateTimeOffset CheckedAt, string? Pnpm, bool Healthy);

    private static HealthCache? ReadHealthCache()
    {
        try
        {
            var file = HealthFile();
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (!root.TryGetProperty("checkedAt", out var at) || !DateTimeOffset.TryParse(at.GetString(), out var when))
                return null;
            var healthy = root.TryGetProperty("healthy", out var h) && h.ValueKind == JsonValueKind.True;
            var pnpm = root.TryGetProperty("pnpm", out var p) ? p.GetString() : null;
            return new HealthCache(when, pnpm, healthy);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteHealthCache(string? pnpm, bool healthy)
    {
        try
        {
            Directory.CreateDirectory(AppDataRoot());
            var json = JsonSerializer.Serialize(new
            {
                checkedAt = DateTimeOffset.UtcNow.ToString("o"),
                pnpm,
                healthy,
            });
            File.WriteAllText(HealthFile(), json, new UTF8Encoding(false));
        }
        catch
        {
            // 缓存写不进去只影响性能，不影响判断
        }
    }

    // ---------------------------------------------------------------- 安装内置 pnpm

    /// <summary>确保本地有一份 Node 版 pnpm，返回其 pnpm.cjs 路径；失败返回 null 并给出原因。</summary>
    private static string? EnsureLocalPnpm(int major, out string? notice)
    {
        notice = null;
        var root = InstallRoot(major);
        var binDir = Path.Combine(root, "node_modules", "pnpm", "bin");
        foreach (var candidate in new[] { "pnpm.cjs", "pnpm.mjs" })
        {
            var entry = Path.Combine(binDir, candidate);
            if (File.Exists(entry)) return entry;
        }

        var npm = MainWindow.FindNpm();
        if (npm == null)
        {
            notice = "未找到 npm，无法准备内置 pnpm（本机 pnpm 又不可用）。";
            return null;
        }

        Directory.CreateDirectory(root);
        var args = new List<string>
        {
            npm.Value.NpmCli, "install", $"pnpm@{major}",
            "--prefix", root,
            "--no-audit", "--no-fund", "--no-save", "--loglevel=error",
        };
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = MarketSupport.PrependToPath(Environment.GetEnvironmentVariable("PATH")),
            ["npm_config_cache"] = Path.Combine(AppDataRoot(), "npm-cache"),
        };
        var (code, output) = RunCapture(npm.Value.Node, args, 2 * 60_000, env);

        foreach (var candidate in new[] { "pnpm.cjs", "pnpm.mjs" })
        {
            var entry = Path.Combine(binDir, candidate);
            if (File.Exists(entry))
            {
                notice = $"已为 dsh 准备内置 Node 版 pnpm {major}（本机 pnpm 连不上 registry）。";
                return entry;
            }
        }

        var tail = output.Length > 300 ? output[^300..] : output;
        notice = $"准备内置 pnpm {major} 失败（exit={code}）：{tail.Trim()}";
        return null;
    }

    // ---------------------------------------------------------------- 垫片

    /// <summary>写 pnpm/pnpx/pn 垫片并返回目录；垫片只调用 Node 入口，不依赖 shell 扩展名解析。</summary>
    private static string? WriteShims(string pnpmEntry)
    {
        var node = MainWindow.FindNode();
        if (node == null) return null;

        var dir = ShimRoot();
        Directory.CreateDirectory(dir);

        var pnpmBin = Path.GetDirectoryName(pnpmEntry) ?? string.Empty;
        var pairs = new List<(string Name, string Target)>
        {
            ("pnpm.cmd", pnpmEntry),
            ("pn.cmd", pnpmEntry),
        };
        foreach (var candidate in new[] { "pnpx.cjs", "pnpx.mjs" })
        {
            var pnpx = Path.Combine(pnpmBin, candidate);
            if (!File.Exists(pnpx)) continue;
            pairs.Add(("pnpx.cmd", pnpx));
            break;
        }

        foreach (var (name, target) in pairs) WriteShim(Path.Combine(dir, name), node, target);
        return dir;
    }

    private static void WriteShim(string path, string node, string target)
    {
        // %* 原样转发参数；node 与目标路径都加引号，避免空格路径被拆开
        var content = "@ECHO off\r\n\"" + node + "\" \"" + target + "\" %*\r\n";
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == content) return;
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        catch
        {
            // 垫片写不出去只会让本次不启用（由调用方校验文件是否存在）
        }
    }

    // ---------------------------------------------------------------- 进程辅助

    private static string? ResolveOnPath(string name, string? pathOverride = null)
    {
        var path = pathOverride ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';');
        foreach (var raw in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length == 0) continue;
            foreach (var ext in exts)
            {
                if (ext.Length == 0) continue;
                try
                {
                    var file = Path.Combine(dir, name + ext.ToLowerInvariant());
                    if (File.Exists(file)) return file;
                    file = Path.Combine(dir, name + ext.ToUpperInvariant());
                    if (File.Exists(file)) return file;
                }
                catch { /* 非法路径项忽略 */ }
            }
        }
        return null;
    }

    private static (int Code, string Output) RunCapture(
        string file, IReadOnlyList<string> args, int timeoutMs,
        IReadOnlyDictionary<string, string>? env = null)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            if (env != null)
                foreach (var pair in env) psi.Environment[pair.Key] = pair.Value;

            using var proc = Process.Start(psi);
            if (proc == null) return (-1, "无法启动进程。");
            // 两个流都必须异步读：只读一个再读另一个会在缓冲区填满时死锁
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (-1, "超时。");
            }
            return (proc.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
