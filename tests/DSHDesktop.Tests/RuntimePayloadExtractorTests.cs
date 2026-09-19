using System.IO.Compression;
using System.Text;
using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class RuntimePayloadExtractorTests
{
    [Fact]
    public async Task ExtractAsync_PublishesCompleteValidArchiveAtomically()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var archive = CreateArchive(temp.Path, [
            ("node/node.exe", "node"),
            ("dsh/node_modules/@deepseek-ai/dsh/package.json", "{\"version\":\"0.1.6\"}"),
        ]);
        var destination = Path.Combine(temp.Path, "runtime");

        var result = await new RuntimePayloadExtractor().ExtractAsync(archive, destination);

        Assert.True(result.Success);
        Assert.Equal("node", File.ReadAllText(Path.Combine(destination, "node", "node.exe")));
        Assert.Equal("{\"version\":\"0.1.6\"}", File.ReadAllText(Path.Combine(
            destination, "dsh", "node_modules", "@deepseek-ai", "dsh", "package.json")));
        Assert.Empty(Directory.GetDirectories(temp.Path, "runtime.extract-*"));
    }

    [Fact]
    public async Task ExtractAsync_RejectsTraversalWithoutPublishingDirectory()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var archive = CreateArchive(temp.Path, [("../outside.txt", "no")]);
        var destination = Path.Combine(temp.Path, "runtime");

        var result = await new RuntimePayloadExtractor().ExtractAsync(archive, destination);

        Assert.Equal(RuntimePayloadExtractionStatus.UnsafeEntry, result.Status);
        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(Path.Combine(temp.Path, "outside.txt")));
    }

    [Fact]
    public async Task ExtractAsync_RejectsDuplicatePathsWithoutPublishingDirectory()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var archive = Path.Combine(temp.Path, "runtime.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "node/node.exe", "one");
            WriteEntry(zip, "NODE/node.exe", "two");
        }

        var result = await new RuntimePayloadExtractor().ExtractAsync(archive, Path.Combine(temp.Path, "runtime"));

        Assert.Equal(RuntimePayloadExtractionStatus.DuplicateEntry, result.Status);
        Assert.Empty(Directory.GetDirectories(temp.Path, "runtime.extract-*"));
    }

    [Fact]
    public async Task ExtractAsync_RejectsArchiveBeyondExpandedSizeLimit()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var archive = CreateArchive(temp.Path, [("runtime.txt", "too long")]);

        var result = await new RuntimePayloadExtractor { MaximumExpandedBytes = 4 }
            .ExtractAsync(archive, Path.Combine(temp.Path, "runtime"));

        Assert.Equal(RuntimePayloadExtractionStatus.ArchiveTooLarge, result.Status);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "runtime")));
    }

    [Fact]
    public async Task ExtractAsync_RejectsExistingDestinationWithoutOverwritingIt()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var archive = CreateArchive(temp.Path, [("runtime.txt", "new")]);
        var destination = Path.Combine(temp.Path, "runtime");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "keep.txt"), "keep");

        var result = await new RuntimePayloadExtractor().ExtractAsync(archive, destination);

        Assert.Equal(RuntimePayloadExtractionStatus.DestinationExists, result.Status);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(destination, "keep.txt")));
    }

    private static string CreateArchive(string root, IReadOnlyList<(string Path, string Contents)> entries)
    {
        var archive = Path.Combine(root, "runtime.zip");
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
        foreach (var entry in entries) WriteEntry(zip, entry.Path, entry.Contents);
        return archive;
    }

    private static void WriteEntry(ZipArchive archive, string path, string contents)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(contents);
    }
}
