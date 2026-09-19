using System.Text.Json;
using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class ReleaseSbomTests
{
    [Fact]
    public void CreateAndWrite_ProducesDeterministicFileInventoryAndExcludesItself()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "DSHDesktop.dll"), "desktop");
        var output = Path.Combine(temp.Path, "DSHDesktop.spdx.json");

        var sbom = ReleaseSbom.Create(temp.Path, Options(), output);
        sbom.Write(output);

        Assert.Single(sbom.Files);
        Assert.Equal("./DSHDesktop.dll", sbom.Files[0].FileName);
        Assert.DoesNotContain(sbom.Files, file => file.FileName.EndsWith("spdx.json", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal("SPDX-2.3", document.RootElement.GetProperty("spdxVersion").GetString());
        Assert.Equal("DSH Desktop", document.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void Create_ListsNuGetPackagesFromDependencyManifestInOrdinalOrder()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "app.deps.json"), """
        { "libraries": {
          "Z.Package/2.0.0": { "type": "package" },
          "A.Package/1.0.0": { "type": "package" },
          "Project/1.0.0": { "type": "project" }
        }}
        """);

        var sbom = ReleaseSbom.Create(temp.Path, Options());

        Assert.Equal(new[] { "DSH Desktop", "A.Package", "Z.Package" }, sbom.Packages.Select(package => package.Name));
        Assert.Equal(new[] { "1.0.0", "2.0.0" }, sbom.Packages.Skip(1).Select(package => package.VersionInfo));
    }

    [Fact]
    public void Create_RejectsUnsafeDocumentNamespace()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();

        Assert.Throws<InvalidDataException>(() => ReleaseSbom.Create(temp.Path,
            Options() with { DocumentNamespace = new Uri("http://example.invalid/release") }));
    }

    private static ReleaseSbomOptions Options() => new(
        "DSH Desktop",
        "1.0.0",
        new Uri("https://example.invalid/dsh-desktop/1.0.0"),
        new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero));
}
