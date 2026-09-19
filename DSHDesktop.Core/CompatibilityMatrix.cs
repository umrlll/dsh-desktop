using System.Text.Json;
using System.Text.RegularExpressions;

namespace DSHDesktop.Core;

public sealed class CompatibilityEvidence
{
    public int UnitTestCount { get; init; }
    public string ReleaseBuild { get; init; } = string.Empty;
    public string WebViewSmoke { get; init; } = string.Empty;
    public string FullDshStartup { get; init; } = string.Empty;
}

public sealed class RuntimeCompatibility
{
    private List<string> _channels = [];
    private CompatibilityEvidence _evidence = new();
    private List<string> _notes = [];

    public string RuntimeId { get; init; } = string.Empty;
    public string DesktopVersion { get; init; } = string.Empty;
    public string DshVersion { get; init; } = string.Empty;
    public string NodeVersion { get; init; } = string.Empty;
    public string? PnpmVersion { get; init; }
    public string Platform { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public int ProfileSchema { get; init; }
    public string Status { get; init; } = string.Empty;
    public List<string> Channels { get => _channels; init => _channels = value ?? []; }
    public bool Publishable { get; init; }
    public CompatibilityEvidence Evidence { get => _evidence; init => _evidence = value ?? new(); }
    public List<string> Notes { get => _notes; init => _notes = value ?? []; }
}

public sealed record CompatibilityIssue(string Code, string Message, string? RuntimeId = null);

/// <summary>
/// Machine-readable release combinations. A combination is publishable only after every runtime
/// component is pinned and the full launch evidence has passed; development discovery fallbacks
/// must never silently become a stable release declaration.
/// </summary>
public sealed class CompatibilityMatrix
{
    private List<RuntimeCompatibility> _combinations = [];

    private static readonly Regex ExactVersion = new(
        @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> AllowedChannels =
        new(StringComparer.Ordinal) { "stable", "beta", "dev" };

    private static readonly HashSet<string> AllowedStatuses =
        new(StringComparer.Ordinal) { "development", "candidate", "verified", "blocked", "retired" };

    private static readonly HashSet<string> AllowedPlatforms =
        new(StringComparer.Ordinal) { "win32", "darwin", "linux" };

    private static readonly HashSet<string> AllowedArchitectures =
        new(StringComparer.Ordinal) { "x64", "arm64" };

    public int SchemaVersion { get; init; }
    public string UpdatedOn { get; init; } = string.Empty;
    public string DefaultChannel { get; init; } = string.Empty;
    public List<RuntimeCompatibility> Combinations
    {
        get => _combinations;
        init => _combinations = value ?? [];
    }

    public static CompatibilityMatrix Load(string path)
        => Parse(File.ReadAllText(path));

    public static CompatibilityMatrix Parse(string json)
        => JsonSerializer.Deserialize<CompatibilityMatrix>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        }) ?? throw new InvalidDataException("兼容矩阵为空。");

    public void EnsureValid()
    {
        var issues = Validate();
        if (issues.Count == 0) return;
        throw new InvalidDataException(string.Join(
            Environment.NewLine,
            issues.Select(issue => issue.Code + ": " + issue.Message)));
    }

    public IReadOnlyList<CompatibilityIssue> Validate()
    {
        var issues = new List<CompatibilityIssue>();
        if (SchemaVersion != 1)
            issues.Add(new("matrix-schema", "schemaVersion 必须为 1。"));
        if (!DateOnly.TryParseExact(UpdatedOn, "yyyy-MM-dd", out _))
            issues.Add(new("updated-on", "updatedOn 必须是 yyyy-MM-dd。"));
        if (!AllowedChannels.Contains(DefaultChannel))
            issues.Add(new("default-channel", "defaultChannel 必须是 stable、beta 或 dev。"));
        if (Combinations.Count == 0)
            issues.Add(new("empty-matrix", "兼容矩阵至少需要一个组合。"));

        var runtimeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var combination in Combinations)
        {
            var id = combination.RuntimeId;
            void Add(string code, string message) => issues.Add(new(code, message, id));

            if (string.IsNullOrWhiteSpace(id)) Add("runtime-id", "runtimeId 不能为空。");
            else if (!runtimeIds.Add(id)) Add("duplicate-runtime-id", "runtimeId 必须唯一。");

            ValidateVersion(combination.DesktopVersion, "desktopVersion", Add);
            ValidateVersion(combination.DshVersion, "dshVersion", Add);
            ValidateVersion(combination.NodeVersion, "nodeVersion", Add);
            if (combination.PnpmVersion != null)
                ValidateVersion(combination.PnpmVersion, "pnpmVersion", Add);

            if (!AllowedPlatforms.Contains(combination.Platform))
                Add("platform", "platform 必须是 win32、darwin 或 linux。");
            if (!AllowedArchitectures.Contains(combination.Architecture))
                Add("architecture", "architecture 必须是 x64 或 arm64。");
            if (combination.ProfileSchema < 1)
                Add("profile-schema", "profileSchema 必须大于等于 1。");
            if (!AllowedStatuses.Contains(combination.Status))
                Add("status", "status 必须是 development、candidate、verified、blocked 或 retired。");
            if (combination.Channels.Count == 0)
                Add("channels", "每个组合至少属于一个发布通道。");
            foreach (var channel in combination.Channels)
                if (!AllowedChannels.Contains(channel)) Add("channel", "未知发布通道: " + channel);

            if (combination.Channels.Contains("stable", StringComparer.Ordinal)
                && (combination.Status != "verified" || !combination.Publishable))
            {
                Add("stable-not-verified", "stable 组合必须同时为 verified 且 publishable。");
            }

            if (combination.Publishable)
            {
                if (combination.Status != "verified")
                    Add("publishable-status", "publishable 组合必须为 verified。");
                if (string.IsNullOrWhiteSpace(combination.PnpmVersion))
                    Add("publishable-pnpm", "publishable 组合必须锁定 pnpmVersion。");
                if (combination.Evidence.UnitTestCount <= 0
                    || combination.Evidence.ReleaseBuild != "passed"
                    || combination.Evidence.WebViewSmoke != "passed"
                    || combination.Evidence.FullDshStartup != "passed")
                {
                    Add("publishable-evidence", "publishable 组合必须具备完整通过的构建、WebView 和真实 DSH 启动证据。");
                }
            }
        }

        if (AllowedChannels.Contains(DefaultChannel)
            && !Combinations.Any(combination => combination.Channels.Contains(DefaultChannel, StringComparer.Ordinal)))
        {
            issues.Add(new("default-channel-empty", "defaultChannel 至少需要一个对应组合。"));
        }

        return issues;
    }

    private static void ValidateVersion(
        string value,
        string field,
        Action<string, string> add)
    {
        if (!ExactVersion.IsMatch(value ?? string.Empty))
            add("exact-version", field + " 必须是精确版本，不能使用范围或通配符。");
    }
}
