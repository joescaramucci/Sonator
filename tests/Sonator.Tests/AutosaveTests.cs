using System.IO;
using Sonator.Models;
using Sonator.Services;
using Xunit;

namespace Sonator.Tests;

public sealed class AutosaveTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sonator-autosave-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static ProjectDocument Song(double tempo) =>
        new("1", tempo, 4, "4/4", null, null, 0, 63, new List<PatternDocument>(), new List<ArrangeRowDocument>(), new List<string[]>());

    private ProjectAutosave Session(string name, params string[] running) =>
        new(_folder, name, session => running.Contains(session));

    [Fact]
    public void AnAutosaveLeftByASessionThatStopped_IsOfferedWithItsProject()
    {
        Session("1-100").Write(Song(97), @"C:\Songs\demo.sonator.json");

        var found = Session("2-200").Find();

        Assert.NotNull(found);
        Assert.Equal(@"C:\Songs\demo.sonator.json", found!.ProjectPath);
        Assert.Equal(97, Session("2-200").Load(found).Tempo);
    }

    [Fact]
    public void ASessionStillRunning_KeepsItsAutosaveToItself()
    {
        Session("1-100").Write(Song(97), null);

        Assert.Null(Session("2-200", "1-100").Find()); // another Sonator open at the same time
        Assert.Null(Session("1-100").Find()); // its own
    }

    [Fact]
    public void ClosingProperly_LeavesNothingToRecover()
    {
        var session = Session("1-100");
        session.Write(Song(97), null);
        session.Clear();

        Assert.Null(Session("2-200").Find());
    }

    [Fact]
    public void ARecoveredAutosave_BecomesTheNewSessionsOwn()
    {
        Session("1-100").Write(Song(97), null);
        var next = Session("2-200");
        next.Adopt(next.Find()!);

        Assert.Null(Session("3-300", "2-200").Find()); // the new session still runs: nothing is offered twice
        next.Clear();
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public void ADiscardedAutosave_IsGone()
    {
        Session("1-100").Write(Song(97), null);
        var next = Session("2-200");
        next.Discard(next.Find()!);

        Assert.Null(next.Find());
    }

    [Fact]
    public void TheInterval_DefaultsToFiveMinutes()
    {
        Assert.Equal(5, ProjectAutosave.MinutesFrom(new AppSettings()));
        Assert.Equal(0, ProjectAutosave.MinutesFrom(new AppSettings { AutosaveMinutes = 0 }));
        Assert.Equal(3, ProjectAutosave.MinutesFrom(new AppSettings { AutosaveMinutes = 3 }));
        Assert.Equal(5, ProjectAutosave.MinutesFrom(new AppSettings { AutosaveMinutes = 7 })); // not a choice
    }
}
