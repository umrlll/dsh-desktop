using System.Text.RegularExpressions;

namespace DSHDesktop.Core;

public sealed record PluginRecoveryEvidence(string Code, string Detail);

public sealed record PluginRecoveryCandidate(
    string Bundle,
    int LoaderIndex,
    IReadOnlyList<PluginRecoveryEvidence> Evidence);

/// <summary>
/// Read-only evidence collector for recovery UIs. It never edits a profile or runs a plugin: it
/// correlates loader order, the last-known-good bundle set, pnpm lockfile, installed package
/// directory, and recent stderr to make an explicit repair decision easier to audit.
/// </summary>
public static class PluginRecoveryDiagnostics
{
    private static readonly Regex SafePackageName = new(
        @"^(?:@[a-z0-9][a-z0-9._-]*/)?[a-z0-9][a-z0-9._-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> ProtectedBundles = new(StringComparer.OrdinalIgnoreCase)
    {
        "@deepseek-ai/dsh-base",
        "@deepseek-ai/dsh-web-app",
    };

    public static IReadOnlyList<PluginRecoveryCandidate> Analyze(
        string profileDirectory,
        IEnumerable<string>? currentBundles,
        IEnumerable<string>? lastHealthyBundles,
        IEnumerable<string>? recentStderr = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        var current = NormalizeBundles(currentBundles);
        var baseline = new HashSet<string>(NormalizeBundles(lastHealthyBundles), StringComparer.OrdinalIgnoreCase);
        var stderr = (recentStderr ?? []).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        var added = current.Where(bundle => !baseline.Contains(bundle)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = current
            .Where(bundle => !ProtectedBundles.Contains(bundle) && added.Contains(bundle))
            .ToArray();
        if (candidates.Length == 0)
            candidates = current.Where(bundle => !ProtectedBundles.Contains(bundle)).TakeLast(1).ToArray();

        return candidates.Select(bundle => new PluginRecoveryCandidate(
            bundle,
            current.FindIndex(value => string.Equals(value, bundle, StringComparison.OrdinalIgnoreCase)),
            CollectEvidence(profileDirectory, bundle, added.Contains(bundle), stderr))).ToArray();
    }

    private static IReadOnlyList<PluginRecoveryEvidence> CollectEvidence(
        string profileDirectory,
        string bundle,
        bool addedSinceHealthy,
        IReadOnlyList<string> stderr)
    {
        var evidence = new List<PluginRecoveryEvidence>();
        evidence.Add(new PluginRecoveryEvidence(
            addedSinceHealthy ? "bundle-added-after-healthy" : "bundle-last-loader-entry",
            addedSinceHealthy
                ? "该 bundle 不在最近一次已知可用快照中。"
                : "没有新增 bundle，按 loader 中最后一个非核心条目回退。"));
        if (LockfileMentions(profileDirectory, bundle))
            evidence.Add(new PluginRecoveryEvidence("lockfile-reference", "pnpm-lock.yaml 包含该 bundle。"));
        if (InstalledPackageExists(profileDirectory, bundle))
            evidence.Add(new PluginRecoveryEvidence("installed-package", "profile node_modules 中存在该 bundle。"));
        if (stderr.Any(line => line.Contains(bundle, StringComparison.OrdinalIgnoreCase)))
            evidence.Add(new PluginRecoveryEvidence("recent-stderr-reference", "最近的 stderr 提到了该 bundle。"));
        return evidence;
    }

    private static List<string> NormalizeBundles(IEnumerable<string>? bundles)
        => (bundles ?? [])
            .Where(bundle => !string.IsNullOrWhiteSpace(bundle))
            .Select(bundle => bundle.Trim())
            .Where(bundle => SafePackageName.IsMatch(bundle))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool LockfileMentions(string profileDirectory, string bundle)
    {
        try
        {
            var lockfile = Path.Combine(profileDirectory, "pnpm-lock.yaml");
            return File.Exists(lockfile) && File.ReadLines(lockfile)
                .Any(line => line.Contains(bundle, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool InstalledPackageExists(string profileDirectory, string bundle)
    {
        if (!SafePackageName.IsMatch(bundle)) return false;
        try
        {
            var packagePath = Path.Combine([profileDirectory, "node_modules", .. bundle.Split('/')]);
            return Directory.Exists(packagePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
