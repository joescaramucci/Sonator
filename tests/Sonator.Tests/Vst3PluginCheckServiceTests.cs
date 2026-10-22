using System.IO;
using Sonator.Services.Vst3;
using Xunit;

namespace Sonator.Tests;

public sealed class Vst3PluginCheckServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sonator-check-" + Guid.NewGuid().ToString("N"));

    public Vst3PluginCheckServiceTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Plugin(string name)
    {
        var path = Path.Combine(_folder, name + ".vst3");
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public async Task EachPlugin_IsCheckedOnce_AndTheResultIsRemembered()
    {
        var good = Plugin("Good");
        var bad = Plugin("Bad");
        var runs = 0;
        Task<bool?> Check(string path)
        {
            runs++;
            return Task.FromResult<bool?>(path == good);
        }

        var cache = Path.Combine(_folder, "plugin-check.json");
        var service = new Vst3PluginCheckService(cache, Check);
        Assert.Null(service.KnownResult(good));
        Assert.True(await service.WorksAsync(good));
        Assert.False(await service.WorksAsync(bad));
        Assert.True(await service.WorksAsync(good));
        Assert.Equal(2, runs);

        // A new start of Sonator reads the results without checking again.
        var reopened = new Vst3PluginCheckService(cache, _ => throw new InvalidOperationException("checked again"));
        Assert.True(reopened.KnownResult(good));
        Assert.False(reopened.KnownResult(bad));
    }

    [Fact]
    public async Task AnUpdatedPlugin_IsCheckedAgain()
    {
        var plugin = Plugin("Updated");
        var service = new Vst3PluginCheckService(Path.Combine(_folder, "plugin-check.json"), _ => Task.FromResult<bool?>(false));
        Assert.False(await service.WorksAsync(plugin));

        File.SetLastWriteTimeUtc(plugin, DateTime.UtcNow.AddMinutes(5));

        Assert.Null(service.KnownResult(plugin));
    }

    [Fact]
    public async Task WhenTheCheckCannotRun_ThePluginIsKept_AndNotRemembered()
    {
        var plugin = Plugin("Unknown");
        var service = new Vst3PluginCheckService(Path.Combine(_folder, "plugin-check.json"), _ => Task.FromResult<bool?>(null));

        Assert.Null(await service.WorksAsync(plugin));
        Assert.Null(service.KnownResult(plugin));
    }
}
