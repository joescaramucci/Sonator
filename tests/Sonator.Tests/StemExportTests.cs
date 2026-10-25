using System.IO;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class StemExportTests
{
    [Fact]
    public void EachStemThatSounds_IsAWavNamedAfterItsTrack_InOrder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"sonator-stems-{Guid.NewGuid():N}");
        try
        {
            var sound = Enumerable.Range(0, 2000).Select(i => (float)Math.Sin(i * 0.05) * 0.5f).ToArray();
            var stems = new List<(ulong Key, float[] Samples)>
            {
                (1000, sound),
                (2000, new float[2000]), // silent: left out
                (3000, sound),
                (1, sound), // a bus
                (4000, sound),
            };
            var names = new Dictionary<ulong, string> { [1000] = "Drums", [3000] = "Drums", [1] = "Bus 1", [4000] = "Bass/Synth" };

            MainViewModel.WriteStems(folder, stems, names, 48000);

            var files = Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(name => name).ToList();
            Assert.Equal(new[] { "01 Drums.wav", "02 Drums.wav", "03 Bus 1.wav", "04 Bass_Synth.wav" }, files);
            Assert.True(new FileInfo(Path.Combine(folder, "01 Drums.wav")).Length > 1000 * 2 * 3);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
