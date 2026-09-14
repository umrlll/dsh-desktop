using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DSHDesktop;

/// <summary>
/// 查询 npm registry 上 <c>@deepseek-ai/dsh</c> 的版本，并与当前捆绑版本比较，
/// 用于"检查更新"按钮与启动时的自动检测提示。
/// </summary>
public static class VersionUpdate
{
    private static readonly string[] RegistryUrls = new[]
    {
        "https://registry.npmjs.org/@deepseek-ai/dsh",
        "https://registry.npmmirror.com/@deepseek-ai/dsh",   // 镜像兜底（海外直连不可用时）
    };

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    static VersionUpdate()
    {
        // GitHub API 要求带 User-Agent，否则返回 403
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("DSHDesktop/1.0");
    }

    /// <summary>GitHub 仓库（与包元数据里的 repository 一致），用于取更新说明。</summary>
    private const string GitHubRepo = "deepseek-ai/deepseek-harness";

    /// <summary>某个版本的更新说明。</summary>
    public sealed record ReleaseInfo(string Version, string Tag, string Title, string Body, string Url);

    /// <summary>
    /// 取指定版本的更新说明：优先 GitHub Releases（按 tag 匹配该版本，匹配不到取最新一条），
    /// 失败时退回 npm 版本元数据（只有一句话描述 + 页面链接）。全部失败返回 <c>null</c>。
    /// </summary>
    public static async Task<ReleaseInfo?> FetchReleaseInfoAsync(string version)
    {
        var normalized = version.Trim().TrimStart('v', 'V');
        var fromGitHub = await TryGitHubReleaseAsync(normalized);
        return fromGitHub ?? await TryNpmDescriptionAsync(normalized);
    }

    private static async Task<ReleaseInfo?> TryGitHubReleaseAsync(string version)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(
                $"https://api.github.com/repos/{GitHubRepo}/releases?per_page=30"));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            JsonElement? matched = null;
            JsonElement? newest = null;
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                newest ??= release;
                var tag = release.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? "" : "";
                if (tag.TrimStart('v', 'V').Contains(version, StringComparison.OrdinalIgnoreCase))
                {
                    matched = release;
                    break;
                }
            }

            var chosen = matched ?? newest;
            if (chosen == null) return null;

            string Get(string name) =>
                chosen.Value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? "" : "";

            var tagName = Get("tag_name");
            var title = Get("name");
            if (string.IsNullOrWhiteSpace(title)) title = tagName;
            return new ReleaseInfo(version, tagName, title, Get("body").Trim(), Get("html_url"));
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ReleaseInfo?> TryNpmDescriptionAsync(string version)
    {
        foreach (var prefix in new[] { "https://registry.npmjs.org/@deepseek-ai/dsh/", "https://registry.npmmirror.com/@deepseek-ai/dsh/" })
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(prefix + Uri.EscapeDataString(version)));
                var description = doc.RootElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString() ?? "" : "";
                return new ReleaseInfo(version, version, $"v{version}", description.Trim(),
                    $"https://www.npmjs.com/package/@deepseek-ai/dsh/v/{version}");
            }
            catch
            {
                // 换下一个源
            }
        }
        return null;
    }

    /// <summary>发布页面（取不到具体说明时的兜底链接）。</summary>
    public static string ReleasesPageUrl => $"https://github.com/{GitHubRepo}/releases";

    /// <summary>探测某个 URL 是否可达（用于决定集成终端是否需要改用镜像源）。</summary>
    public static async Task<bool> IsReachableAsync(string url, int timeoutMs = 2500)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return (int)response.StatusCode < 500;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>一次版本检查的结果。</summary>
    public sealed record Result(
        string Current,     // 当前捆绑版本
        string Latest,      // npm dist-tag "latest"（稳定版）
        string Newest,      // 所有已发布版本中最新的一个（含预发布）
        bool Available,     // 是否存在比当前更新的版本
        bool StableUpdate   // 更新是否来自稳定通道（latest > current）
    );

    /// <summary>
    /// 查询 registry 并返回最新版本信息。
    /// 网络失败、解析失败或当前版本无法解析时返回 <c>null</c>（按"未发现更新"处理）。
    /// </summary>
    public static async Task<Result?> CheckAsync(string current)
    {
        string? currentNormalized = TryNormalizeVersion(current);
        if (currentNormalized == null) return null;

        foreach (var url in RegistryUrls)
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                var root = doc.RootElement;

                var versions = new List<string>();
                if (root.TryGetProperty("versions", out var v) && v.ValueKind == JsonValueKind.Object)
                    versions.AddRange(v.EnumerateObject().Select(p => p.Name));

                string latestStable = "";
                if (root.TryGetProperty("dist-tags", out var tags) && tags.ValueKind == JsonValueKind.Object &&
                    tags.TryGetProperty("latest", out var lt) && lt.ValueKind == JsonValueKind.String)
                    latestStable = lt.GetString() ?? "";

                string newest = currentNormalized;
                foreach (var ver in versions)
                    if (CompareVersions(ver, newest) > 0) newest = ver;

                bool available = IsNewer(newest, currentNormalized);
                bool stable = IsNewer(latestStable, currentNormalized);
                return new Result(currentNormalized, latestStable, newest, available, stable);
            }
            catch
            {
                // 该源不可用/解析失败，尝试下一个镜像
            }
        }
        return null;
    }

    /// <summary>
    /// 把版本串归一化为可比较形式；无法解析时返回 null。
    ///
    /// 必须<b>保留预发布后缀</b>（只丢构建元数据 <c>+…</c>）：先前这里用 <c>Split('-')[0]</c>
    /// 把预发布段整段砍掉，于是当前版本 <c>0.1.5-alpha.1</c> 被归一化成正式版 <c>0.1.5</c>，
    /// 再与 <c>0.1.5-rc.2</c> 比较就落进"预发布 &lt; 正式版"分支，得到"已是最新版本"——
    /// 停在任一 <c>0.1.5</c> 预发布上的宿主从此再也收不到更新提示。
    ///
    /// 2026-09-12 实测就是这个症状：应用日志一直打印"已是最新版本（v0.1.5）"，
    /// 而 registry 上已经有 <c>0.1.5-rc.2</c>（<c>next</c> 标签）。
    /// </summary>
    private static string? TryNormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var s = version.Trim().TrimStart('v');
        var noBuild = s.Split('+')[0];
        var dash = noBuild.IndexOf('-');
        var core = dash >= 0 ? noBuild[..dash] : noBuild;
        var pre = dash >= 0 ? noBuild[dash..] : "";   // 连 '-' 一起保留
        var parts = core.Split('.');
        if (parts.Length < 2) return null;
        if (parts.Length == 2) parts = new[] { parts[0], parts[1], "0" };
        if (!parts.All(p => int.TryParse(p, out _))) return null;
        return string.Join('.', parts) + pre;
    }

    /// <summary>
    /// 已发布的 <paramref name="candidate"/> 是否比当前版本 <paramref name="current"/> 更新。
    ///
    /// 两侧都过同一套归一化（<see cref="TryNormalizeVersion"/>），因此"当前版本带预发布后缀"
    /// 这类判定有唯一的可测入口——2026-09-12 的 bug 正是丢掉了这个后缀。
    /// 任一版本无法解析时返回 false（按"没有更新"处理，不做猜测）。
    /// </summary>
    public static bool IsNewer(string candidate, string current)
    {
        var currentNormalized = TryNormalizeVersion(current);
        var candidateNormalized = TryNormalizeVersion(candidate);
        if (currentNormalized == null || candidateNormalized == null) return false;
        return CompareVersions(candidateNormalized, currentNormalized) > 0;
    }

    /// <summary>按 semver 优先级比较两个版本串：a&gt;b 返回正值，a&lt;b 返回负值，相等返回 0。</summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = Parse(a);
        var pb = Parse(b);

        if (pa.Major != pb.Major) return pa.Major.CompareTo(pb.Major);
        if (pa.Minor != pb.Minor) return pa.Minor.CompareTo(pb.Minor);
        if (pa.Patch != pb.Patch) return pa.Patch.CompareTo(pb.Patch);

        bool aPre = pa.Pre.Count > 0;
        bool bPre = pb.Pre.Count > 0;
        if (!aPre && !bPre) return 0;
        if (!aPre) return 1;   // 正式版 > 预发布
        if (!bPre) return -1;

        int count = Math.Min(pa.Pre.Count, pb.Pre.Count);
        for (int i = 0; i < count; i++)
        {
            int cmp = ComparePrereleaseId(pa.Pre[i], pb.Pre[i]);
            if (cmp != 0) return cmp;
        }
        return pa.Pre.Count.CompareTo(pb.Pre.Count);
    }

    private readonly record struct ParsedVersion(long Major, long Minor, long Patch, List<string> Pre);

    private static ParsedVersion Parse(string version)
    {
        var s = version.Trim().TrimStart('v');
        var noBuild = s.Split('+')[0];
        var dash = noBuild.IndexOf('-');
        var core = dash >= 0 ? noBuild[..dash] : noBuild;
        var pre = dash >= 0 ? noBuild[(dash + 1)..] : "";

        var parts = core.Split('.');
        long major = parts.Length > 0 && long.TryParse(parts[0], out var ma) ? ma : 0;
        long minor = parts.Length > 1 && long.TryParse(parts[1], out var mi) ? mi : 0;
        long patch = parts.Length > 2 && long.TryParse(parts[2], out var p) ? p : 0;

        var preList = pre.Length == 0
            ? new List<string>()
            : pre.Split('.').ToList();

        return new ParsedVersion(major, minor, patch, preList);
    }

    private static int ComparePrereleaseId(string a, string b)
    {
        bool aNum = long.TryParse(a, out var ai);
        bool bNum = long.TryParse(b, out var bi);

        if (aNum && bNum) return ai.CompareTo(bi);
        if (aNum) return -1;   // 数字标识符 < 字母标识符
        if (bNum) return 1;
        return string.CompareOrdinal(a, b);
    }
}
