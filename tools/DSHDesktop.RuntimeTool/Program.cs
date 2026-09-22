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
                "checksums" => Checksums(options),
                "portable" => Portable(options),
                "verify-portable" => VerifyPortable(options),
                "validate-input-lock" => ValidateInputLock(options),
                "sbom" => Sbom(options),
                "sign-release" => SignRelease(options),
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

    private static int Checksums(IReadOnlyDictionary<string, string> options)
    {
        var root = Required(options, "root");
        var output = Required(options, "output");
        var checksums = ReleaseChecksums.Create(root, output);
        checksums.Write(output);
        var issues = ReleaseChecksums.Parse(File.ReadAllText(output)).Verify(root);
        if (issues.Count > 0)
            throw new InvalidDataException("Release checksum verification failed: " + string.Join(
                ", ", issues.Select(issue => issue.Code + "(" + issue.Path + ")")));
        Console.WriteLine($"runtime-manifest: wrote {checksums.Entries.Count} release checksums to {output}");
        return 0;
    }

    private static int SignRelease(IReadOnlyDictionary<string, string> options)
    {
        var descriptorPath = Required(options, "descriptor");
        var privateKeyPath = Required(options, "private-key");
        var output = Path.GetFullPath(Required(options, "output"));
        var descriptor = RuntimeReleasePublisher.ParseDescriptor(File.ReadAllText(descriptorPath));
        var release = RuntimeReleasePublisher.Sign(descriptor, File.ReadAllText(privateKeyPath));
        var parent = Path.GetDirectoryName(output)
            ?? throw new InvalidDataException("Signed release output has no parent directory.");
        Directory.CreateDirectory(parent);
        var temporary = output + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, RuntimeReleaseTrust.Serialize(release));
            File.Move(temporary, output, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { /* best effort */ }
        }
        Console.WriteLine("runtime-manifest: signed release metadata to " + output);
        return 0;
    }

    private static int Sbom(IReadOnlyDictionary<string, string> options)
    {
        var root = Required(options, "root");
        var output = Path.GetFullPath(Required(options, "output"));
        var created = DateTimeOffset.Parse(
            Required(options, "created-at"),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
        var optionsModel = new ReleaseSbomOptions(
            Required(options, "name"),
            Required(options, "version"),
            new Uri(Required(options, "namespace"), UriKind.Absolute),
            created);
        var sbom = ReleaseSbom.Create(root, optionsModel, output);
        sbom.Write(output);
        Console.WriteLine($"runtime-manifest: wrote SPDX SBOM for {sbom.Files.Count} files to {output}");
        return 0;
    }

    private static int Portable(IReadOnlyDictionary<string, string> options)
    {
        var result = PortableReleaseArchive.Create(Required(options, "root"), Required(options, "output"));
        if (!result.Success)
            throw new InvalidDataException("Portable archive was not created: " + (result.Error ?? result.Status.ToString()));
        Console.WriteLine($"runtime-manifest: wrote Portable ZIP with {result.FileCount} files to {result.OutputPath}");
        return 0;
    }

    private static int VerifyPortable(IReadOnlyDictionary<string, string> options)
    {
        var result = PortableReleaseArchive.Validate(
            Required(options, "root"),
            Optional(options, "expected-desktop-version"));
        if (!result.Success)
            throw new InvalidDataException("Portable publish root is invalid: " + (result.Error ?? result.Status.ToString()));
        Console.WriteLine($"runtime-manifest: portable publish root verified ({result.FileCount} files)");
        return 0;
    }

    private static int ValidateInputLock(IReadOnlyDictionary<string, string> options)
    {
        var lockFile = ReleaseInputLock.Parse(File.ReadAllText(Required(options, "input")));
        var matrix = CompatibilityMatrix.Load(Required(options, "matrix"));
        lockFile.EnsureValidAgainst(matrix);
        Console.WriteLine("runtime-manifest: release input lock validation passed");
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

    private static string? Optional(IReadOnlyDictionary<string, string> options, string name)
        => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static string DescribeIssues(IEnumerable<RuntimeManifestIssue> issues)
        => string.Join("；", issues.Select(issue => issue.Code + (issue.Path == null ? "" : "(" + issue.Path + ")")));

    private static int Usage()
    {
        Console.Error.WriteLine("usage: runtime-tool generate|verify|activate|checksums|portable|verify-portable|validate-input-lock|sbom|sign-release --name value ...");
        return 2;
    }
}
