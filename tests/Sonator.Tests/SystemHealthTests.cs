using Sonator.Services;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class SystemHealthTests
{
    [Theory]
    [InlineData(75, 100, 0, 0.25)]   // idle for three quarters of the kernel time, no user time
    [InlineData(20, 60, 40, 0.8)]    // 100 ticks in all, 20 of them idle
    [InlineData(0, 0, 0, 0)]         // no time passed
    public void SystemCpu_IsTheShareOfTimeNotIdle(long idle, long kernel, long user, double expected)
    {
        Assert.Equal(expected, SystemHealthMonitor.CpuShare(idle, kernel, user), 3);
    }

    [Fact]
    public void ProcessCpu_IsSharedOverAllCores()
    {
        // One full core out of eight for a second is 12.5%.
        Assert.Equal(0.125, SystemHealthMonitor.ProcessShare(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 8), 3);
        Assert.Equal(0, SystemHealthMonitor.ProcessShare(TimeSpan.FromSeconds(1), TimeSpan.Zero, 8));
    }

    [Fact]
    public void Monitor_ReadsPlausibleValues()
    {
        var monitor = new SystemHealthMonitor();
        Thread.Sleep(50);
        var sample = monitor.Sample();

        Assert.InRange(sample.SystemCpu, 0, 1);
        Assert.InRange(sample.SonatorCpu, 0, 1);
        Assert.InRange(sample.SystemMemoryLoad, 0.01, 1);
        Assert.True(sample.SonatorMemoryBytes > 0);
    }

    [Theory]
    [InlineData(0.2, "#FF4FB87B")]
    [InlineData(0.7, "#FFE6C45B")]
    [InlineData(0.9, "#FFD94C4C")]
    public void Meter_TurnsYellowThenRedAsTheLoadRises(double load, string colour)
    {
        Assert.Equal(colour, SystemHealthViewModel.BrushFor(load).ToString());
    }

    [Fact]
    public void SystemMemory_IsShownAsUsedOverInstalledGigabytes()
    {
        // 11 GB used of 15.9 GB usable, which is sold as 16 GB.
        var expected = string.Format("{0:0.0}/16 GB", 11.0);
        Assert.Equal(expected, SystemHealthViewModel.FormatUsedOfTotal(11L << 30, (long)(15.9 * (1L << 30))));
    }

    [Fact]
    public void Monitor_ReadsTheInstalledMemory()
    {
        var sample = new SystemHealthMonitor().Sample();

        Assert.True(sample.SystemMemoryTotalBytes > 0);
        Assert.InRange(sample.SystemMemoryUsedBytes, 1, sample.SystemMemoryTotalBytes);
    }

    [Fact]
    public void Memory_IsShownInMegabytesThenGigabytes()
    {
        Assert.Equal("512 MB", SystemHealthViewModel.FormatBytes(512L << 20));
        Assert.Equal(1.5.ToString("0.0") + " GB", SystemHealthViewModel.FormatBytes(3L << 29));
    }
}
