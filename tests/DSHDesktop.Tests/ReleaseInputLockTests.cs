using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class ReleaseInputLockTests
{
    [Fact]
    public void Validate_AcceptsPinnedHttpsArtifacts()
    {
        var lockFile = ValidLock();

        Assert.Empty(lockFile.Validate());
        Assert.Equal(lockFile, ReleaseInputLock.Parse(lockFile.Serialize()));
    }

    [Fact]
    public void Validate_RejectsUnpinnedOrInsecureArtifacts()
    {
        var lockFile = ValidLock() with
        {
            Node = new ReleaseInputArtifact("http://example.invalid/node.zip", new string('A', 64)),
            Dsh = null,
        };

        var issues = lockFile.Validate();

        Assert.Contains(issues, issue => issue.Code == "node-url");
        Assert.Contains(issues, issue => issue.Code == "node-sha256");
        Assert.Contains(issues, issue => issue.Code == "dsh");
    }

    [Fact]
    public void ValidateAgainst_RequiresVerifiedPublishableMatrixCombination()
    {
        var lockFile = ValidLock();
        var matrix = Matrix(lockFile, publishable: true);

        Assert.Empty(lockFile.ValidateAgainst(matrix));
        Assert.Contains(lockFile.ValidateAgainst(Matrix(lockFile, publishable: false)), issue =>
            issue.Code == "compatibility-not-publishable");
    }

    private static ReleaseInputLock ValidLock() => new()
    {
        Channel = "beta",
        RuntimeId = "dsh-0.1.6-alpha.1-win32-x64-node-24.20.0-pnpm-11.27.0",
        DesktopVersion = "1.0.0",
        DshVersion = "0.1.6-alpha.1",
        NodeVersion = "24.20.0",
        PnpmVersion = "11.27.0",
        Platform = "win32",
        Architecture = "x64",
        ProfileSchema = 1,
        Node = Artifact("node"),
        Dsh = Artifact("dsh"),
        Pnpm = Artifact("pnpm"),
    };

    private static ReleaseInputArtifact Artifact(string name)
        => new("https://example.invalid/" + name + ".zip", new string('a', 64));

    private static CompatibilityMatrix Matrix(ReleaseInputLock lockFile, bool publishable) => new()
    {
        SchemaVersion = 1,
        UpdatedOn = "2026-09-20",
        DefaultChannel = "beta",
        Combinations = [new RuntimeCompatibility
        {
            RuntimeId = lockFile.RuntimeId,
            DesktopVersion = lockFile.DesktopVersion,
            DshVersion = lockFile.DshVersion,
            NodeVersion = lockFile.NodeVersion,
            PnpmVersion = lockFile.PnpmVersion,
            Platform = lockFile.Platform,
            Architecture = lockFile.Architecture,
            ProfileSchema = lockFile.ProfileSchema,
            Status = publishable ? "verified" : "candidate",
            Channels = [lockFile.Channel],
            Publishable = publishable,
            Evidence = new CompatibilityEvidence
            {
                UnitTestCount = 1,
                ReleaseBuild = "passed",
                WebViewSmoke = "passed",
                FullDshStartup = "passed",
            },
        }],
    };
}
