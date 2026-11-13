using DesktopSqlStudio.Core;
using Xunit;

namespace DesktopSqlStudio.Tests;

public class BatchSplitterTests
{
    [Fact]
    public void SplitsOnGoLines()
    {
        var batches = BatchSplitter.Split("SELECT 1\nGO\nSELECT 2\ngo\nSELECT 3");
        Assert.Equal(new[] { "SELECT 1", "SELECT 2", "SELECT 3" }, batches);
    }

    [Fact]
    public void IgnoresEmptyBatches()
    {
        Assert.Empty(BatchSplitter.Split("GO\n\nGO\n"));
    }

    [Fact]
    public void GoInsideBlockCommentIsNotASeparator()
    {
        var batches = BatchSplitter.Split("SELECT 1 /*\nGO\n*/ + 1");
        Assert.Single(batches);
    }

    [Fact]
    public void GoAsPartOfAnIdentifierIsNotASeparator()
    {
        var batches = BatchSplitter.Split("SELECT * FROM Goods\nGOTO label");
        Assert.Single(batches);
    }

    [Fact]
    public void HandlesWindowsLineEndings()
    {
        var batches = BatchSplitter.Split("SELECT 1\r\nGO\r\nSELECT 2");
        Assert.Equal(2, batches.Count);
    }
}
