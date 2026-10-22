using System.IO;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class VstPluginListRulesTests
{
    private static readonly (string Name, string FilePath, string Kind)[] Scanned =
    {
        ("Zeta", @"C:\VST3\Zeta.vst3", "VST3"),
        ("Alpha", @"C:\VST3\Alpha.vst3", "VST3"),
        ("Broken", @"C:\VST3\Broken.vst3", "VST3"),
    };

    [Fact]
    public void RemovedPlugins_AreLeftOut_WhateverTheCase()
    {
        var list = VstPluginListRules.Arrange(Scanned, Array.Empty<string>(), new[] { @"c:\vst3\broken.VST3" });

        Assert.Equal(new[] { "Alpha", "Zeta" }, list.Select(plugin => plugin.Name));
    }

    [Fact]
    public void AddedFiles_JoinTheList_OnceAndSorted_WhenTheyExist()
    {
        var added = Path.Combine(Path.GetTempPath(), "Middle-" + Guid.NewGuid().ToString("N") + ".vst3");
        File.WriteAllText(added, string.Empty);
        try
        {
            var list = VstPluginListRules.Arrange(
                Scanned,
                new[] { added, added, @"C:\does-not-exist\Gone.vst3", @"C:\VST3\Alpha.vst3" },
                Array.Empty<string>());

            Assert.Equal(4, list.Count);
            Assert.Equal("Alpha", list[0].Name);
            Assert.Contains(list, plugin => plugin.FilePath == added);
            Assert.DoesNotContain(list, plugin => plugin.Name == "Gone");
        }
        finally
        {
            File.Delete(added);
        }
    }
}
