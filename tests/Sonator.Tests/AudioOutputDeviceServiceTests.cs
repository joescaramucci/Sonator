using Sonator.Services;
using Xunit;

namespace Sonator.Tests;

public sealed class AudioOutputDeviceServiceTests
{
    [Fact]
    public void Parse_ReadsIdAndNamePerLine()
    {
        const string listing = "{0.0.0.00000000}.{abc}\tSSL 2\n{0.0.0.00000000}.{def}\tSpeakers (Realtek)\n";

        var devices = AudioOutputDeviceService.Parse(listing);

        Assert.Equal(2, devices.Count);
        Assert.Equal(new AudioOutputDevice("{0.0.0.00000000}.{abc}", "SSL 2"), devices[0]);
        Assert.Equal("Speakers (Realtek)", devices[1].Name);
    }

    [Fact]
    public void Parse_IgnoresTerminatorAndMalformedLines()
    {
        // The native buffer ends with a null character, and a line without a tab is not a device.
        const string listing = "{id}\tOutput\n\0";
        const string withNoise = "garbage line\n{id2}\tSecond\n\0";

        Assert.Single(AudioOutputDeviceService.Parse(listing));
        Assert.Equal(new[] { "Second" }, AudioOutputDeviceService.Parse(withNoise).Select(device => device.Name).ToArray());
    }

    [Fact]
    public void Parse_EmptyListing_ReturnsNoDevices()
    {
        Assert.Empty(AudioOutputDeviceService.Parse(string.Empty));
        Assert.Empty(AudioOutputDeviceService.Parse("\0"));
    }
}
