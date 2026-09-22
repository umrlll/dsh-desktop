using System.Text;
using System.Text.Json;

namespace DSHDesktop.Core;

/// <summary>
/// Persists versions a user has explicitly dismissed. A malformed or unavailable state file never
/// hides an update: it is treated as empty, and writes use atomic replacement.
/// </summary>
public sealed class ReleaseSkipStore
{
    public const string FileName = "skipped-releases.json";
    private const int SchemaVersion = 1;
    private const int MaximumVersionLength = 128;

    private readonly string _path;

    public ReleaseSkipStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _path = Path.Combine(Path.GetFullPath(stateDirectory), FileName);
    }

    public bool IsSkipped(string version)
    {
        var normalized = NormalizeVersion(version);
        return Read().Versions.Contains(normalized, StringComparer.Ordinal);
    }

    public void Skip(string version)
    {
        var normalized = NormalizeVersion(version);
        var state = Read();
        var versions = state.Versions
            .Append(normalized)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Write(new ReleaseSkipState(SchemaVersion, versions));
    }

    private ReleaseSkipState Read()
    {
        try
        {
            if (!File.Exists(_path)) return new ReleaseSkipState(SchemaVersion, []);
            var state = JsonSerializer.Deserialize<ReleaseSkipState>(File.ReadAllText(_path));
            if (state?.SchemaVersion != SchemaVersion || state.Versions == null) return new ReleaseSkipState(SchemaVersion, []);
            return new ReleaseSkipState(
                SchemaVersion,
                state.Versions
                    .Where(IsValidVersion)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ReleaseSkipState(SchemaVersion, []);
        }
    }

    private void Write(ReleaseSkipState state)
    {
        var parent = Path.GetDirectoryName(_path)
            ?? throw new InvalidDataException("Release skip state path has no parent directory.");
        Directory.CreateDirectory(parent);
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }) + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { /* best effort */ }
        }
    }

    private static string NormalizeVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var normalized = version.Trim();
        if (!IsValidVersion(normalized))
            throw new ArgumentException("Release version must be a short, single-line value.", nameof(version));
        return normalized;
    }

    private static bool IsValidVersion(string value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaximumVersionLength
            && value.All(character => !char.IsControl(character));

    private sealed record ReleaseSkipState(int SchemaVersion, IReadOnlyList<string> Versions);
}
