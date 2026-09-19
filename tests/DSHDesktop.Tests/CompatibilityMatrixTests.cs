using System.IO;
using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class CompatibilityMatrixTests
{
    [Fact]
    public void RepositoryMatrix_IsValidAndRecordsObservedDevelopmentRuntime()
    {
        var path = Path.Combine(SourceScanner.RepoRoot, "eng", "compatibility.json");
        var matrix = CompatibilityMatrix.Load(path);

        Assert.Empty(matrix.Validate());
        var runtime = Assert.Single(matrix.Combinations);
        Assert.Equal("1.0.0", runtime.DesktopVersion);
        Assert.Equal("0.1.5-alpha.1", runtime.DshVersion);
        Assert.Equal("24.20.0", runtime.NodeVersion);
        Assert.Null(runtime.PnpmVersion);
        Assert.Equal("development", runtime.Status);
        Assert.False(runtime.Publishable);
        Assert.Equal(309, runtime.Evidence.UnitTestCount);
    }

    [Fact]
    public void FloatingVersionAndDuplicateRuntimeId_AreRejected()
    {
        var matrix = CompatibilityMatrix.Parse(MatrixJson(
            firstVersion: "^1.0.0",
            includeDuplicate: true));

        var issues = matrix.Validate();

        Assert.Contains(issues, issue => issue.Code == "exact-version");
        Assert.Contains(issues, issue => issue.Code == "duplicate-runtime-id");
    }

    [Fact]
    public void StableCombination_MustBeVerifiedAndPublishable()
    {
        var matrix = CompatibilityMatrix.Parse(MatrixJson(
            channels: "[\"stable\"]",
            status: "candidate",
            publishable: false));

        var issues = matrix.Validate();

        Assert.Contains(issues, issue => issue.Code == "stable-not-verified");
    }

    [Fact]
    public void PublishableCombination_RequiresPinnedPnpmAndPassingEvidence()
    {
        var matrix = CompatibilityMatrix.Parse(MatrixJson(
            status: "verified",
            publishable: true,
            pnpmVersion: "null",
            fullStartup: "blocked"));

        var issues = matrix.Validate();

        Assert.Contains(issues, issue => issue.Code == "publishable-pnpm");
        Assert.Contains(issues, issue => issue.Code == "publishable-evidence");
    }

    private static string MatrixJson(
        string firstVersion = "1.0.0",
        string channels = "[\"dev\"]",
        string status = "development",
        bool publishable = false,
        string pnpmVersion = "\"10.0.0\"",
        string fullStartup = "passed",
        bool includeDuplicate = false)
    {
        var item = $$"""
          {
            "runtimeId": "runtime-1",
            "desktopVersion": "{{firstVersion}}",
            "dshVersion": "0.1.5-alpha.1",
            "nodeVersion": "24.20.0",
            "pnpmVersion": {{pnpmVersion}},
            "platform": "win32",
            "architecture": "x64",
            "profileSchema": 1,
            "status": "{{status}}",
            "channels": {{channels}},
            "publishable": {{publishable.ToString().ToLowerInvariant()}},
            "evidence": {
              "unitTestCount": 1,
              "releaseBuild": "passed",
              "webViewSmoke": "passed",
              "fullDshStartup": "{{fullStartup}}"
            },
            "notes": []
          }
          """;
        return $$"""
          {
            "schemaVersion": 1,
            "updatedOn": "2026-09-19",
            "defaultChannel": "dev",
            "combinations": [{{item}}{{(includeDuplicate ? "," + item : string.Empty)}}]
          }
          """;
    }
}
