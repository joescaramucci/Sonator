using Sonator.Services;
using Xunit;

namespace Sonator.Tests;

public sealed class VstPluginScannerTests
{
    private const string DefaultFolder = @"C:\Common\VST3";
    private const string UserFolder = @"D:\Plugins";

    [Fact]
    public void Scan_MergesBothFoldersWithoutDuplicatesAndOrdersByName()
    {
        var files = new Dictionary<string, IReadOnlyList<(string Name, string FilePath, string Kind)>>
        {
            [DefaultFolder] = new[]
            {
                ("Zebra", @"C:\Common\VST3\Zebra.vst3", "Instrument"),
                ("alpha", @"C:\Common\VST3\alpha.vst3", "Effect")
            },
            [UserFolder] = new[]
            {
                ("Shared", @"C:\Common\VST3\Zebra.vst3", "Instrument"),
                ("Beta", @"D:\Plugins\Beta.vst3", "Instrument")
            }
        };
        var scanner = CreateScanner(files, existing: new[] { DefaultFolder, UserFolder });

        var result = scanner.Scan(DefaultFolder, UserFolder);

        Assert.Equal(new[] { "alpha", "Beta", "Zebra" }, result.Plugins.Select(plugin => plugin.Name).ToArray());
        Assert.Equal(new[] { DefaultFolder, UserFolder }, result.ScannedFolders);
    }

    [Fact]
    public void Scan_UserFolderSameAsDefault_IsScannedOnce()
    {
        var files = new Dictionary<string, IReadOnlyList<(string Name, string FilePath, string Kind)>>
        {
            [DefaultFolder] = new[] { ("Only", @"C:\Common\VST3\Only.vst3", "Effect") }
        };
        var scanner = CreateScanner(files, existing: new[] { DefaultFolder });

        var result = scanner.Scan(DefaultFolder, @"c:\common\vst3");

        Assert.Single(result.ScannedFolders);
        Assert.Single(result.Plugins);
    }

    [Fact]
    public void Scan_MissingDefaultFolder_OnlyScansUserFolder()
    {
        var files = new Dictionary<string, IReadOnlyList<(string Name, string FilePath, string Kind)>>
        {
            [UserFolder] = new[] { ("Beta", @"D:\Plugins\Beta.vst3", "Instrument") }
        };
        var scanner = CreateScanner(files, existing: new[] { UserFolder });

        var result = scanner.Scan(DefaultFolder, UserFolder);

        Assert.Equal(new[] { UserFolder }, result.ScannedFolders);
        Assert.Equal("Beta", Assert.Single(result.Plugins).Name);
    }

    [Fact]
    public void Scan_NoUserFolder_OnlyScansDefaultFolder()
    {
        var files = new Dictionary<string, IReadOnlyList<(string Name, string FilePath, string Kind)>>
        {
            [DefaultFolder] = new[] { ("Only", @"C:\Common\VST3\Only.vst3", "Effect") }
        };
        var scanner = CreateScanner(files, existing: new[] { DefaultFolder });

        var result = scanner.Scan(DefaultFolder, userDirectory: "  ");

        Assert.Equal(new[] { DefaultFolder }, result.ScannedFolders);
    }

    [Fact]
    public void Scan_NothingExists_ReturnsEmptyResult()
    {
        var scanner = CreateScanner(new Dictionary<string, IReadOnlyList<(string Name, string FilePath, string Kind)>>(), existing: Array.Empty<string>());

        var result = scanner.Scan(DefaultFolder, null);

        Assert.Empty(result.ScannedFolders);
        Assert.Empty(result.Plugins);
    }

    private static VstPluginScanner CreateScanner(
        Dictionary<string, IReadOnlyList<(string Name, string FilePath, string Kind)>> files,
        IReadOnlyCollection<string> existing)
    {
        return new VstPluginScanner(
            directory => files.TryGetValue(directory, out var plugins) ? plugins : Array.Empty<(string, string, string)>(),
            directory => existing.Contains(directory, StringComparer.OrdinalIgnoreCase));
    }
}
