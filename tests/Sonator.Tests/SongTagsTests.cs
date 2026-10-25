using System.IO;
using Sonator.Services;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class SongTagsTests
{
    private static readonly byte[] Png =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01,
        0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0xF8, 0xCF, 0xC0, 0xF0, 0x1F, 0x00, 0x05, 0x00, 0x01, 0xFF, 0x89, 0x99, 0x3D, 0x1D, 0x00, 0x00,
        0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
    };

    private static readonly SongTags Tags = new("Demo Pop", "Joe", "Sonator Demos", "Pop", 2026, 3, "Joe S.", "Made with Sonator", Png, "image/png");

    [Theory]
    [InlineData(".wav")]
    [InlineData(".mp3")]
    public void TheTags_AreWrittenIntoTheFile_AndReadBack(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sonator-tags-{Guid.NewGuid():N}{extension}");
        try
        {
            var samples = Enumerable.Range(0, 44100 * 2).Select(i => (float)Math.Sin(i * 0.05) * 0.3f).ToArray();
            if (extension == ".mp3")
            {
                MainViewModel.WriteMp3(path, samples, 44100);
            }
            else
            {
                AudioClipLoader.SaveWav(new AudioClip(samples, 2, 44100), path);
            }

            Tags.Write(path);
            var read = SongTags.Read(path);

            Assert.Equal("Demo Pop", read.Title);
            Assert.Equal("Joe", read.Artist);
            Assert.Equal("Sonator Demos", read.Album);
            Assert.Equal("Pop", read.Genre);
            Assert.Equal(2026, read.Year);
            Assert.Equal(3, read.Track);
            Assert.Equal("Joe S.", read.Composer);
            Assert.Equal("Made with Sonator", read.Comment);
            Assert.Equal(Png, read.Cover);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheSongsTags_AreKeptWithTheProject()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var viewModel = new MainViewModel();
                viewModel.SetSongTags(Tags);
                var document = viewModel.BuildProjectDocument();
                using var other = new MainViewModel();
                other.LoadDocument(document);
                Assert.Equal("Joe", other.SongTags.Artist);
                Assert.Equal(Png, other.SongTags.Cover);
                Assert.True(new SongTags().IsEmpty);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }
}
