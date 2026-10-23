using System.IO;
using Sonator.Models;
using Sonator.Services;
using Xunit;

namespace Sonator.Tests;

public sealed class ProjectAudioFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonator-projaudio-" + Guid.NewGuid().ToString("N"));

    public ProjectAudioFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Clip(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        return path;
    }

    private static ProjectDocument Song(params string?[] clips) => new(
        "1.4", 120, 8, "4/4", null, null, 0, 15,
        new List<PatternDocument>
        {
            new("Pattern 1", "", "01", clips.Select((clip, i) =>
                new TrackDocument($"Track {i + 1}", "Audio clip", 1, 60, new List<StepDocument>(), AudioClipPath: clip)).ToList()),
        },
        new List<ArrangeRowDocument>(), new List<string[]>());

    [Fact]
    public void Saving_CopiesTheClipsBesideTheProject_AndNamesThemRelatively()
    {
        var library = Path.Combine(_root, "library");
        var clip = Clip(library, "take.wav");
        var project = Path.Combine(_root, "songs", "My song.sonator.json");

        var packed = ProjectAudioFiles.Pack(Song(clip, null), project, out var moved);

        var copy = Path.Combine(_root, "songs", "My song Audio", "take.wav");
        Assert.True(File.Exists(copy));
        Assert.Equal(copy, moved[clip]);
        Assert.Equal(Path.Combine("My song Audio", "take.wav"), packed.Patterns[0].Tracks[0].AudioClipPath);
        Assert.Null(packed.Patterns[0].Tracks[1].AudioClipPath);
        Assert.True(File.Exists(clip)); // the original stays where it was
    }

    [Fact]
    public void AProjectMovedWithItsAudioFolder_FindsItsClips()
    {
        var clip = Clip(Path.Combine(_root, "library"), "take.wav");
        var project = Path.Combine(_root, "songs", "Song.sonator.json");
        var packed = ProjectAudioFiles.Pack(Song(clip), project, out _);

        var movedProject = Path.Combine(_root, "usb", "Song.sonator.json");
        Directory.CreateDirectory(Path.Combine(_root, "usb"));
        Directory.Move(ProjectAudioFiles.FolderFor(project), ProjectAudioFiles.FolderFor(movedProject));

        var opened = ProjectAudioFiles.Unpack(packed, movedProject);
        var path = opened.Patterns[0].Tracks[0].AudioClipPath!;
        Assert.True(File.Exists(path));
        Assert.StartsWith(Path.Combine(_root, "usb"), path);
    }

    [Fact]
    public void SavingAgain_CopiesNothingTwice_AndOldProjectsOpenAsTheyWere()
    {
        var clip = Clip(Path.Combine(_root, "library"), "take.wav");
        var project = Path.Combine(_root, "Song.sonator.json");
        var once = ProjectAudioFiles.Unpack(ProjectAudioFiles.Pack(Song(clip), project, out _), project);

        ProjectAudioFiles.Pack(once, project, out var movedAgain);
        Assert.Empty(movedAgain);

        // A project saved before had full paths: they are left as they are.
        var old = Song(clip);
        Assert.Equal(clip, ProjectAudioFiles.Unpack(old, project).Patterns[0].Tracks[0].AudioClipPath);
    }

    [Fact]
    public void ClipsNoLongerPlayed_AreTheUnusedOnes()
    {
        var project = Path.Combine(_root, "Song.sonator.json");
        var folder = ProjectAudioFiles.FolderFor(project);
        var kept = Clip(folder, "kept.wav");
        var undone = Clip(folder, "undo.wav");
        var stale = Clip(folder, "stale.wav");

        var unused = ProjectAudioFiles.Unused(project, new[] { Song(kept), Song(undone) });

        Assert.Equal(new[] { stale }, unused);
    }
}
