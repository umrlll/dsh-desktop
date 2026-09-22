using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class ReleaseSkipStoreTests
{
    [Fact]
    public void Skip_PersistsExactVersionAcrossStoreInstances()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var store = new ReleaseSkipStore(temp.Path);

        store.Skip(" 0.1.7-alpha.1 ");

        Assert.True(new ReleaseSkipStore(temp.Path).IsSkipped("0.1.7-alpha.1"));
        Assert.False(new ReleaseSkipStore(temp.Path).IsSkipped("0.1.8-alpha.1"));
    }

    [Fact]
    public void IsSkipped_TreatsMalformedStateAsEmpty()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, ReleaseSkipStore.FileName), "not-json");

        Assert.False(new ReleaseSkipStore(temp.Path).IsSkipped("0.1.7-alpha.1"));
    }

    [Fact]
    public void Skip_RejectsControlCharacters()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();

        Assert.Throws<ArgumentException>(() => new ReleaseSkipStore(temp.Path).Skip("0.1.7\nalpha"));
    }
}
