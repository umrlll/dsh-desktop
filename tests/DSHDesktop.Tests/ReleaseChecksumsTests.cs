using System.Security.Cryptography;
using System.Text;
using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class ReleaseChecksumsTests
{
    [Fact]
    public void CreateAndWrite_ProducesSortedSelfExcludingChecksumDocument()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "z.zip"), "zip");
        Directory.CreateDirectory(Path.Combine(temp.Path, "nested"));
        File.WriteAllText(Path.Combine(temp.Path, "nested", "a.exe"), "exe");
        var output = Path.Combine(temp.Path, "SHA256SUMS.txt");

        var checksums = ReleaseChecksums.Create(temp.Path, output);
        checksums.Write(output);
        var text = File.ReadAllText(output);
        var parsed = ReleaseChecksums.Parse(text);

        Assert.Equal(new[] { "nested/a.exe", "z.zip" }, checksums.Entries.Select(entry => entry.Path));
        Assert.DoesNotContain("SHA256SUMS.txt", text);
        Assert.Empty(parsed.Verify(temp.Path));
    }

    [Fact]
    public void Verify_ReportsTamperedAndMissingReleaseAssets()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var asset = Path.Combine(temp.Path, "portable.zip");
        var missing = Path.Combine(temp.Path, "installer.exe");
        File.WriteAllText(asset, "original");
        File.WriteAllText(missing, "installer");
        var checksums = ReleaseChecksums.Create(temp.Path);
        File.WriteAllText(asset, "tampered");
        File.Delete(missing);

        var issues = checksums.Verify(temp.Path);

        Assert.Contains(issues, issue => issue.Code == "sha256" && issue.Path == "portable.zip");
        Assert.Contains(issues, issue => issue.Code == "missing" && issue.Path == "installer.exe");
    }

    [Fact]
    public void Verify_RejectsPathEscapesAndDuplicateEntries()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "asset.zip"), "asset");
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("asset"))).ToLowerInvariant();
        var checksums = new ReleaseChecksums
        {
            Entries = [
                new ReleaseChecksumEntry("../escape.zip", 0, sha),
                new ReleaseChecksumEntry("asset.zip", 5, sha),
                new ReleaseChecksumEntry("asset.zip", 5, sha),
            ],
        };

        var issues = checksums.Verify(temp.Path);

        Assert.Contains(issues, issue => issue.Code == "invalid-entry" && issue.Path == "../escape.zip");
        Assert.Contains(issues, issue => issue.Code == "invalid-entry" && issue.Path == "asset.zip");
    }
}
