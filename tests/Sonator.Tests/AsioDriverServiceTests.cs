using Sonator.Services;
using Xunit;

namespace Sonator.Tests;

public sealed class AsioDriverServiceTests
{
    [Fact]
    public void Merge_KeepsOneEntryPerNameAndSortsByName()
    {
        var entries = new (string Name, string? Clsid)[]
        {
            ("SSL ASIO Driver 2", "{B}"),
            ("ASIO4ALL v2", "{A}"),
            ("SSL ASIO Driver 2", "{B}"),
        };

        var drivers = AsioDriverService.Merge(entries);

        Assert.Equal(new[] { "ASIO4ALL v2", "SSL ASIO Driver 2" }, drivers.Select(driver => driver.Name).ToArray());
    }

    [Fact]
    public void Merge_DropsEntriesWithoutClsid()
    {
        var entries = new (string Name, string? Clsid)[]
        {
            ("Broken", null),
            ("Blank", "   "),
            ("Good", "{C}"),
        };

        var driver = Assert.Single(AsioDriverService.Merge(entries));
        Assert.Equal("Good", driver.Name);
        Assert.Equal("{C}", driver.Clsid);
    }
}
