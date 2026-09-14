using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DSHDesktop;

/// <summary>
/// 让 dsh-market（以及一切通过 dsh CLI 安装插件的界面）在桌面端能正常工作的宿主适配。
///
/// 背景：市场对「宿主环境」有三处硬假设，而桌面端的启动方式恰好都不满足。
///
/// 1. <b>pnpm 的位置</b>。市场的 <c>toolSearchDirs()</c> 只查 PATH、
///    <c>PNPM_HOME</c>、Windows 下的 <c>%LOCALAPPDATA%\pnpm</c> 与
///    <c>%APPDATA%\npm</c>，以及运行中 node 的所在目录——它<b>不读</b>
///    <c>npm prefix -g</c>。本机 pnpm 装在 <c>~/.npmrc</c> 指定的 npm 全局前缀
///    （<c>D:\Develop\nodejs\node_global</c>），正好落在所有搜索面之外，市场因此会
///    误判「未配置」，而它的一键配置又需要 node 目录里有 npm/corepack。
///    桌面端已经会为内置终端算好这个目录，这里把它同样注入 dsh 宿主进程。
///
/// 2. <b>自重启</b>。市场的 <c>detectedSupervisor()</c> 只识别 systemd/launchd/pm2，
///    Windows 上永远返回 null，于是「一键重启」会杀掉宿主并另起一个游离进程——
///    绕开本窗口的进程生命周期，新的鉴权 URL 也不会回到 WebView。桌面端是真正握有
///    进程的监督者，因此按市场文档的做法把 <c>dsh-market.allowRestart</c> 预置为
///    false，让重启由本窗口负责。
///
/// 3. <b>镜像源</b>。市场为 pnpm 子进程补的 registry 只取调用者未指定时的区域镜像，
///    桌面端既然已经探测出可达镜像，就一并注入，避免内网环境下市场能加载目录却装不上。
///
/// 注意：市场还有一个更强的 Desktop 契约（<c>desktopProfiles</c> + <c>desktopPnpm</c>
/// 两个 Cordis Host 服务），但那份契约要求服务在 Loader 条目挂载前注册，只有自带
/// 启动器的宿主（Electron 的 <c>boot(prepare)</c>）能做到；通过 dsh CLI 启动的组合里，
/// 任何 <c>--patch</c>/bundle 行都排在市场那一行之后，市场在 apply 时读不到。
/// </summary>
internal static class MarketSupport
{
    /// <summary>npm 包名，同时用于判断市场是否装在这个 profile 里。</summary>
    public const string MarketPackage = "dshmarket";

    /// <summary>市场在 settings.yaml 里的命名空间（见其 settings.ts）。</summary>
    private const string MarketSettingsNamespace = "dsh-market";

    // ---------------------------------------------------------------- 工具链

    /// <summary>当前 profile 名（与 MainWindow 启动 profile 保持一致）。</summary>
    public static string ActiveProfile { get; set; } = "web";

    /// <summary>DSH 主目录：优先 <c>DSH_HOME</c>，否则 <c>~/.dsh</c>。</summary>
    public static string DshHome()
    {
        var home = Environment.GetEnvironmentVariable("DSH_HOME");
        if (!string.IsNullOrWhiteSpace(home)) return home!;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
    }

    /// <summary>活动 profile 的目录（默认布局 <c>$DSH_HOME/profiles/&lt;name&gt;</c>）。</summary>
    public static string ProfileDir(string? profile = null)
        => Path.Combine(DshHome(), "profiles", profile ?? ActiveProfile);

    /// <summary>settings.yaml 的路径（dsh-settings-file 的默认文档）。</summary>
    public static string SettingsPath() => Path.Combine(DshHome(), "settings.yaml");

    /// <summary>
    /// 需要前置到 PATH 的工具链目录，按可信度排序：市场自己会查的固定目录、
    /// 真实 npm 全局前缀（它不查，但 pnpm 实际在这里），以及带 npm/corepack 的
    /// node 安装目录。
    /// </summary>
    public static List<string> ToolDirs()
    {
        var dirs = new List<string>();

        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            dir = dir!.Trim().TrimEnd('\\', '/');
            if (dir.Length == 0) return;
            foreach (var existing in dirs)
                if (string.Equals(existing, dir, StringComparison.OrdinalIgnoreCase)) return;
            if (Directory.Exists(dir)) dirs.Add(dir);
        }

        // 最前：pnpm 垫片目录（稳定路径，可能尚未写好——垫片是后台预热的产物）。
        // 这里**不要求**"已经就绪"：目录无条件前置，用户稍后在市场里点安装时
        // 垫片早已由预热写好；这样 pnpm 探测就不必阻塞 dsh web 的启动。
        // dsh plugin → pnpm 与市场的 pnpm 探测都会优先命中它。
        Add(PnpmSupport.ShimRootDirectory());

        var pnpmHome = Environment.GetEnvironmentVariable("PNPM_HOME");
        Add(pnpmHome);
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pnpm"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));

        // 市场的搜索面里没有这一项：npm 全局前缀（pnpm 实际安装位置）
        try { Add(MainWindow.NpmGlobalBinDir()); } catch { /* 探测失败不影响其它目录 */ }

        // 带 npm/corepack 的 node 安装目录——市场的一键配置要用它们
        try
        {
            var npm = MainWindow.FindNpm();
            if (npm != null) Add(Path.GetDirectoryName(npm.Value.Node));
        }
        catch { /* 同上 */ }

        return dirs;
    }

    /// <summary>把工具链目录前置到给定 PATH 前面（不改动进程自身的环境）。</summary>
    public static string PrependToPath(string? currentPath)
    {
        var parts = ToolDirs();
        parts.Add(currentPath ?? string.Empty);
        return string.Join(';', parts);
    }

    /// <summary>
    /// dsh 宿主进程应继承的额外环境变量：工具链 PATH、可达的 npm 镜像源，
    /// 以及一个供插件识别的桌面端标记。
    /// </summary>
    public static Dictionary<string, string> HostEnvironment(string? mirrorRegistry, string? currentPath)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = PrependToPath(currentPath),
            ["DSHDESKTOP"] = "1",
            ["DSHDESKTOP_PROFILE"] = ActiveProfile,
            ["DSHDESKTOP_PROFILE_DIR"] = ProfileDir(),
        };

        // 桌面端探测出的镜像源同样交给宿主：市场的 pnpm 探测/安装与 dsh plugin 都读它。
        if (!string.IsNullOrWhiteSpace(mirrorRegistry))
        {
            env["npm_config_registry"] = mirrorRegistry!;
            env["pnpm_config_registry"] = mirrorRegistry!;
        }

        if (Version.TryParse(
                typeof(MarketSupport).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                out var version))
        {
            env["DSHDESKTOP_VERSION"] = version.ToString(3);
        }

        return env;
    }

    // ---------------------------------------------------------------- 自重启开关

    /// <summary>该 profile 的 package.json 是否把市场列为依赖。</summary>
    public static bool MarketInstalled(string profileDir)
    {
        try
        {
            var manifest = Path.Combine(profileDir, "package.json");
            if (!File.Exists(manifest)) return false;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
            return doc.RootElement.TryGetProperty("dependencies", out var deps)
                   && deps.ValueKind == System.Text.Json.JsonValueKind.Object
                   && deps.TryGetProperty(MarketPackage, out _);
        }
        catch
        {
            return false;   // 读不懂就当作没装，宁可不动用户的设置
        }
    }

    /// <summary>
    /// 市场装着、且用户从未表态时，把 <c>dsh-market.allowRestart</c> 预置为 false。
    ///
    /// 只做「补一段配置」这一种最小改动：文档里已经有 <c>dsh-market:</c> 段就完全不动，
    /// 用户自己写下的 true 会被尊重。返回是否真的写了。
    /// </summary>
    public static bool EnsureMarketRestartDisabled(out string settingsPath)
    {
        settingsPath = SettingsPath();

        if (!MarketInstalled(ProfileDir())) return false;

        string text;
        try
        {
            text = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : string.Empty;
        }
        catch
        {
            return false;
        }

        if (HasTopLevelNamespace(text, MarketSettingsNamespace)) return false;

        var buffer = new StringBuilder(text);
        if (buffer.Length > 0 && buffer[^1] != '\n') buffer.Append('\n');
        buffer.Append(MarketSettingsNamespace).Append(':').Append('\n');
        buffer.Append("  # 桌面端握有 dsh 进程生命周期，重启由窗口负责；市场自行重启会\n");
        buffer.Append("  # 另起游离进程并让界面失去鉴权 URL。在设置页改回 true 可恢复。\n");
        buffer.Append("  allowRestart: false\n");

        try
        {
            File.WriteAllText(settingsPath, buffer.ToString(), new UTF8Encoding(false));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>settings.yaml 是否已有该顶层命名空间（避免覆盖用户显式配置）。</summary>
    private static bool HasTopLevelNamespace(string text, string name)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0 || line[0] == ' ' || line[0] == '\t' || line[0] == '#') continue;
            if (line.StartsWith(name + ":", StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
