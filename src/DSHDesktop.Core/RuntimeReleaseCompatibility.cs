namespace DSHDesktop.Core;

public sealed record RuntimeReleaseCompatibilityResult(
    bool IsCompatible,
    IReadOnlyList<string> Mismatches);

/// <summary>
/// Evaluates whether a signed release descriptor can replace a currently verified runtime slot.
/// DSH, Node, and pnpm are intentionally allowed to change; the Desktop shell contract, target
/// platform, architecture, and profile schema must remain exact matches.
/// </summary>
public static class RuntimeReleaseCompatibility
{
    public static RuntimeReleaseCompatibilityResult Evaluate(
        RuntimeManifest current,
        RuntimeReleaseDescriptor candidate)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(candidate);
        var mismatches = new List<string>();
        AddMismatch(mismatches, "desktop-version", current.DesktopVersion, candidate.DesktopVersion);
        AddMismatch(mismatches, "platform", current.Platform, candidate.Platform);
        AddMismatch(mismatches, "architecture", current.Architecture, candidate.Architecture);
        if (current.ProfileSchema != candidate.ProfileSchema)
            mismatches.Add("profile-schema");
        return new RuntimeReleaseCompatibilityResult(mismatches.Count == 0, mismatches);
    }

    private static void AddMismatch(List<string> mismatches, string name, string left, string right)
    {
        if (!string.Equals(left, right, StringComparison.Ordinal)) mismatches.Add(name);
    }
}
