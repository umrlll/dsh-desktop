using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class RuntimeManifestTests
{
    private static readonly RuntimeManifestIdentity Identity = new(
        "dsh-test-win-x64",
        "1.0.0",
        "0.1.5-alpha.1",
        "24.20.0",
        "11.27.0",
        "win32",
        "x64",
        ProfileSchema: 1);

    [Fact]
    public void CreateWriteLoadAndVerify_RoundTripsDeterministically()
    {
        using var temp = new TempDirectory();
        Write(temp.Path, "node/node.exe", "node");
        Write(temp.Path, "dsh/package.json", "dsh");

        var manifest = RuntimeManifest.Create(temp.Path, Identity);
        var output = Path.Combine(temp.Path, RuntimeManifest.FileName);
        manifest.Write(output);
        var loaded = RuntimeManifest.Load(output);

        Assert.Equal(
            new[] { "dsh/package.json", "node/node.exe" },
            loaded.Files.Select(file => file.Path));
        Assert.All(loaded.Files, file => Assert.Matches("^[0-9a-f]{64}$", file.Sha256));
        Assert.Empty(loaded.Verify(temp.Path));
    }

    [Fact]
    public void Verify_ReportsTamperingAndUnexpectedFiles()
    {
        using var temp = new TempDirectory();
        Write(temp.Path, "node/node.exe", "before");
        var manifest = RuntimeManifest.Create(temp.Path, Identity);

        Write(temp.Path, "node/node.exe", "after-content-is-longer");
        Write(temp.Path, "injected.dll", "unexpected");
        var issues = manifest.Verify(temp.Path);

        Assert.Contains(issues, issue => issue.Code == "size-mismatch");
        Assert.Contains(issues, issue => issue.Code == "hash-mismatch");
        Assert.Contains(issues, issue => issue.Code == "unexpected-file" && issue.Path == "injected.dll");
    }

    [Fact]
    public void Verify_RejectsPathTraversalWithoutReadingOutsideRoot()
    {
        using var temp = new TempDirectory();
        Write(temp.Path, "inside.txt", "inside");
        var manifest = new RuntimeManifest
        {
            RuntimeId = Identity.RuntimeId,
            DesktopVersion = Identity.DesktopVersion,
            DshVersion = Identity.DshVersion,
            NodeVersion = Identity.NodeVersion,
            PnpmVersion = Identity.PnpmVersion,
            Platform = Identity.Platform,
            Architecture = Identity.Architecture,
            ProfileSchema = Identity.ProfileSchema,
            Files = [new RuntimeManifestFile("../outside.txt", 1, new string('0', 64))],
        };

        var issues = manifest.Verify(temp.Path);

        Assert.Contains(issues, issue => issue.Code == "unsafe-path");
    }

    [Fact]
    public void Create_RejectsFloatingComponentVersion()
    {
        using var temp = new TempDirectory();
        Write(temp.Path, "node/node.exe", "node");
        var floating = Identity with { PnpmVersion = "11.x" };

        var error = Assert.Throws<InvalidDataException>(
            () => RuntimeManifest.Create(temp.Path, floating));

        Assert.Contains("exact-version", error.Message);
    }

    private static void Write(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "dsh-runtime-manifest-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
