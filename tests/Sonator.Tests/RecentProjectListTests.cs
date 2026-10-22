using Sonator.Services;
using Xunit;

namespace Sonator.Tests;

public sealed class RecentProjectListTests
{
    [Fact]
    public void TheLatestProject_GoesOnTop_Once()
    {
        var recent = new List<string> { @"E:\a.sonator.json", @"E:\b.sonator.json", @"E:\c.sonator.json" };

        var updated = RecentProjectList.WithLatest(recent, @"e:\B.sonator.json");

        Assert.Equal(new[] { @"e:\B.sonator.json", @"E:\a.sonator.json", @"E:\c.sonator.json" }, updated);
    }

    [Fact]
    public void TheList_KeepsTheTenLatest()
    {
        var recent = Enumerable.Range(1, 10).Select(i => $@"E:\song{i}.sonator.json").ToList();

        var updated = RecentProjectList.WithLatest(recent, @"E:\new.sonator.json");

        Assert.Equal(RecentProjectList.MaxCount, updated.Count);
        Assert.Equal(@"E:\new.sonator.json", updated[0]);
        Assert.DoesNotContain(@"E:\song10.sonator.json", updated);
    }
}
