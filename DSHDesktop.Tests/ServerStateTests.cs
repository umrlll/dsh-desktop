using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class ServerStateTests
{
    [Fact]
    public void WriteAndRead_RoundTripsRecord()
    {
        using var sandbox = new TemporaryDirectory();
        var expected = new ServerRecord(321, 54321, new DateTimeOffset(2026, 9, 19, 10, 11, 12, TimeSpan.Zero));

        ServerState.Write(sandbox.Path, "desktop", expected);

        Assert.Equal(expected, ServerState.Read(sandbox.Path, "desktop"));
    }

    [Fact]
    public void Read_ReturnsNullForCorruptRecord()
    {
        using var sandbox = new TemporaryDirectory();
        var path = ServerState.PathFor(sandbox.Path, "desktop");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not-json");

        Assert.Null(ServerState.Read(sandbox.Path, "desktop"));
    }

    [Fact]
    public void Clear_RemovesExistingRecord()
    {
        using var sandbox = new TemporaryDirectory();
        ServerState.Write(sandbox.Path, "desktop", new ServerRecord(12, 34567, DateTimeOffset.UtcNow));

        ServerState.Clear(sandbox.Path, "desktop");

        Assert.Null(ServerState.Read(sandbox.Path, "desktop"));
    }

    [Fact]
    public void Read_ReturnsNullWhenRecordDoesNotExist()
    {
        using var sandbox = new TemporaryDirectory();
        Assert.Null(ServerState.Read(sandbox.Path, "desktop"));
    }

    [Theory]
    [InlineData(false, true, true, true, true, PortOwner.Free)]
    [InlineData(true, true, true, true, true, PortOwner.OwnLeftover)]
    [InlineData(true, false, true, true, true, PortOwner.Foreign)]
    [InlineData(true, true, false, true, true, PortOwner.Foreign)]
    [InlineData(true, true, true, false, true, PortOwner.Foreign)]
    public void Classify_UsesConservativeOwnershipEvidence(
        bool portInUse,
        bool matchingPort,
        bool processExists,
        bool isNode,
        bool startTimeMatches,
        PortOwner expected)
    {
        const int port = 41234;
        var record = new ServerRecord(99, matchingPort ? port : port + 1, DateTimeOffset.UtcNow);
        var evidence = new ProcessEvidence(processExists, isNode, startTimeMatches);

        Assert.Equal(expected, ServerState.Classify(port, portInUse, record, evidence));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dshdesktop-core-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup should not hide the assertion result.
            }
        }
    }
}
