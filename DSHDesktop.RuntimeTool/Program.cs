using System.Diagnostics;
using System.Text.Json;
using DSHDesktop.Core;

namespace DSHDesktop.RuntimeTool;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) return Usage();
            var command = args[0];
            var options = ParseOptions(args.Skip(1).ToArray());
            return command switch
            {
                "generate" => Generate(options),
                "verify" => Verify(options),
                "activate" => Activate(options),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("runtime-manifest: " + ex.Message);
            return 1;
        }
    }

    private static int Generate(IReadOnlyDictionary<string, string> options)
    {
        var root = Required(options, "root");
        var output = Required(options, "output");
        var identity = new RuntimeManifestIdentity(
            Required(options, "runtime-id"),
            Required(options, "desktop-version"),
            Required(options, "dsh-version"),
            Required(options, "node-version"),
            Required(options, "pnpm-version"),
            Required(options, "platform"),
            Required(options, "architecture"),
            int.Parse(Required(options, "profile-schema")));

        VerifyComponentVersions(root, identity);
        var manifest = RuntimeManifest.Create(root, identity);
        manifest.Write(output);
        var issues = RuntimeManifest.Load(output).Verify(root);
        if (issues.Count > 0) throw new InvalidDataException(DescribeIssues(issues));
        Console.WriteLine($"runtime-manifest: wrote {manifest.Files.Count} files to {output}");
        return 0;
    }

    private static int Verify(IReadOnlyDictionary<string, string> options)
    {
        var root = Required(options, "root");
        var manifestPath = Required(options, "manifest");
        var issues = RuntimeManifest.Load(manifestPath).Verify(root);
        if (issues.Count > 0) throw new InvalidDataException(DescribeIssues(issues));
        Console.WriteLine("runtime-manifest: verification passed");
        return 0;
    }

    private static int Activate(IReadOnlyDictionary<string, string> options)
    {
        var runtimeRoot = Required(options, "runtime-root");
        var runtimeId = Required(options, "runtime-id");
        var result = new RuntimeSlotManager(runtimeRoot).Activate(runtimeId);
        if (!result.Success) throw new InvalidDataException(result.Error ?? "运行时槽激活失败。");
        Console.WriteLine("runtime-manifest: activated " + runtimeId);
        return 0;
    }

    private static void VerifyComponentVersions(string root, RuntimeManifestIdentity identity)
    {
        var node = Path.Combine(root, "node", OperatingSystem.IsWindows() ? "node.exe" : "node");
        var actualNode = RunVersion(node).TrimStart('v');
        RequireVersion("Node", identity.NodeVersion, actualNode);

        var dshManifest = Path.Combine(root, "dsh", "node_modules", "@deepseek-ai", "dsh", "package.json");
        RequireVersion("DSH", identity.DshVersion, PackageVersion(dshManifest));

        var pnpmManifest = Path.Combine(root, "pnpm", "node_modules", "pnpm", "package.json");
        RequireVersion("pnpm", identity.PnpmVersion, PackageVersion(pnpmManifest));
    }

    private static string RunVersion(string executable)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("Node 运行时不存在。", executable);
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--version");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 Node 版本检查。");
        if (!process.WaitForExit(10_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException("Node 版本检查超时。");
        }
        var output = process.StandardOutput.ReadToEnd().Trim();
        if (process.ExitCode != 0 || output.Length == 0)
            throw new InvalidDataException("Node 版本检查失败: " + process.StandardError.ReadToEnd().Trim());
        return output;
    }

    private static string PackageVersion(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("包 manifest 不存在。", path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("version", out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException("包 manifest 缺少 version: " + path);
        return value.GetString()!;
    }

    private static void RequireVersion(string component, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidDataException($"{component} 版本不匹配：声明 {expected}，实际 {actual}。");
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("参数必须是 --name value 成对形式。");
            options.Add(args[i][2..], args[i + 1]);
        }
        return options;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string name)
        => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("缺少参数 --" + name);

    private static string DescribeIssues(IEnumerable<RuntimeManifestIssue> issues)
        => string.Join("；", issues.Select(issue => issue.Code + (issue.Path == null ? "" : "(" + issue.Path + ")")));

    private static int Usage()
    {
        Console.Error.WriteLine("usage: runtime-tool generate|verify|activate --name value ...");
        return 2;
    }
}
