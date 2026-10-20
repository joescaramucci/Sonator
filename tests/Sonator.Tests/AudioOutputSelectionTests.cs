using Sonator.Services;
using Xunit;

namespace Sonator.Tests;

public sealed class AudioOutputSelectionTests
{
    [Fact]
    public void FromSettings_NoBackendSaved_UsesWasapi()
    {
        var selection = AudioOutputSelection.FromSettings(new AppSettings { AudioOutputDeviceId = "{endpoint}" });

        Assert.False(selection.UseAsio);
        Assert.Equal("{endpoint}", selection.WasapiDeviceId);
    }

    [Fact]
    public void FromSettings_AsioSaved_UsesAsioWithSavedDriver()
    {
        var selection = AudioOutputSelection.FromSettings(new AppSettings
        {
            AudioBackend = "ASIO",
            AsioDriverClsid = "{C0E4A803-D72F-4B6E-89B1-903E90F1F71B}",
        });

        Assert.True(selection.UseAsio);
        Assert.Equal("{C0E4A803-D72F-4B6E-89B1-903E90F1F71B}", selection.AsioDriverClsid);
    }

    [Theory]
    [InlineData("WASAPI")]
    [InlineData("asio")]
    [InlineData("")]
    public void FromSettings_OnlyExactAsioNameSelectsAsio(string backend)
    {
        // Matching is exact on purpose: a hand-edited or unknown value must not switch backends silently.
        var selection = AudioOutputSelection.FromSettings(new AppSettings { AudioBackend = backend });

        Assert.Equal(backend == "ASIO", selection.UseAsio);
    }
}
