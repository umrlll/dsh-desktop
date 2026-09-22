using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class RuntimeReleaseCompatibilityTests
{
    [Fact]
    public void Evaluate_AllowsRuntimeVersionChangesButRejectsHostContractChanges()
    {
        var current = Manifest("1.0.0", "win32", "x64", 1);
        var compatible = Descriptor("1.0.0", "win32", "x64", 1) with
        {
            DshVersion = "0.2.0",
            NodeVersion = "25.0.0",
            PnpmVersion = "12.0.0",
        };
        var incompatible = Descriptor("1.1.0", "win32", "arm64", 2);

        var accepted = RuntimeReleaseCompatibility.Evaluate(current, compatible);
        var rejected = RuntimeReleaseCompatibility.Evaluate(current, incompatible);

        Assert.True(accepted.IsCompatible);
        Assert.Empty(accepted.Mismatches);
        Assert.False(rejected.IsCompatible);
        Assert.Equal(new[] { "desktop-version", "architecture", "profile-schema" }, rejected.Mismatches);
    }

    private static RuntimeManifest Manifest(string desktop, string platform, string architecture, int profileSchema) => new()
    {
        RuntimeId = "runtime-current",
        DesktopVersion = desktop,
        DshVersion = "0.1.0",
        NodeVersion = "24.0.0",
        PnpmVersion = "11.0.0",
        Platform = platform,
        Architecture = architecture,
        ProfileSchema = profileSchema,
    };

    private static RuntimeReleaseDescriptor Descriptor(string desktop, string platform, string architecture, int profileSchema) => new(
        SchemaVersion: 1,
        KeyId: "release-2026",
        Channel: "beta",
        RuntimeId: "runtime-next",
        DesktopVersion: desktop,
        DshVersion: "0.1.1",
        NodeVersion: "24.1.0",
        PnpmVersion: "11.1.0",
        Platform: platform,
        Architecture: architecture,
        ProfileSchema: profileSchema,
        PayloadUrl: "https://updates.example.invalid/runtime.zip",
        PayloadSha256: new string('a', 64),
        IssuedAtUtc: DateTimeOffset.Parse("2026-09-19T00:00:00Z"),
        ExpiresAtUtc: DateTimeOffset.Parse("2026-09-26T00:00:00Z"));
}
