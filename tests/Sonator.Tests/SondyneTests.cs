using Sonator.Services;
using Sonator.Services.Score;
using Sonator.Services.Voice;
using Xunit;

namespace Sonator.Tests;

public sealed class SondyneTests
{
    private const int Rate = 44100;

    // A sung line, a third of a semitone flat: A3, B3, C4, with a little vibrato.
    private static AudioClip Take()
    {
        (double From, double To, double Note)[] notes = [(0.10, 0.70, 57 - 0.3), (0.75, 1.35, 59 - 0.3), (1.40, 2.00, 60 - 0.3)];
        var samples = new float[(int)(2.2 * Rate)];
        var phase = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            var time = i / (double)Rate;
            var current = notes.FirstOrDefault(note => time >= note.From && time < note.To);
            if (current.Note == 0)
            {
                continue;
            }

            var midi = current.Note + 0.1 * Math.Sin(2 * Math.PI * 5 * time);
            phase += 2 * Math.PI * 440 * Math.Pow(2, (midi - 69) / 12) / Rate;
            samples[i] = (float)(0.3 * Math.Sin(phase) + 0.12 * Math.Sin(2 * phase) + 0.06 * Math.Sin(3 * phase));
        }

        return new AudioClip(samples, 1, Rate);
    }

    // The middle pitch heard in a stretch of a clip.
    private static double PitchAt(AudioClip clip, double from, double to)
    {
        var (pitch, _) = VoicePitch.Track(clip.Samples, clip.SampleRate);
        var heard = pitch.Skip((int)(from / VoicePitch.FrameSeconds)).Take((int)((to - from) / VoicePitch.FrameSeconds))
            .Where(value => !double.IsNaN(value)).OrderBy(value => value).ToList();
        return heard[heard.Count / 2];
    }

    [Fact]
    public void TheNotes_AreHeard_AtTheirPitch()
    {
        var take = Sondyne.Analyse(Take());

        Assert.Equal(3, take.Notes.Count);
        Assert.Equal(new[] { 57, 59, 60 }, take.Notes.Select(note => note.NearestNote));
        Assert.InRange(take.Notes[0].SungPitch, 56.6, 56.8);
    }

    [Fact]
    public void ANoteShifted_IsSungThere_AndTheOthersStayAsTheyWere()
    {
        var take = Sondyne.Analyse(Take());
        take.Notes[1].Shift = 2;

        var sung = Sondyne.Render(take);

        Assert.InRange(PitchAt(sung, 0.9, 1.2), 60.5, 60.9); // B3 a third of a semitone flat, now C#4 as flat
        Assert.InRange(PitchAt(sung, 0.2, 0.6), 56.6, 56.8);
        Assert.InRange(PitchAt(sung, 1.5, 1.9), 59.6, 59.8);
        Assert.Equal(take.Clip.FrameCount, sung.FrameCount);
    }

    [Fact]
    public void Centring_BringsANoteToItsSemitone()
    {
        var take = Sondyne.Analyse(Take());
        foreach (var note in take.Notes)
        {
            note.Center = 1;
            note.Drift = 1;
        }

        var sung = Sondyne.Render(take);

        Assert.InRange(PitchAt(sung, 0.2, 0.6), 56.93, 57.07);
        Assert.InRange(PitchAt(sung, 1.5, 1.9), 59.93, 60.07);
    }

    [Fact]
    public void NothingChanged_LeavesTheTakeAsItWas()
    {
        var take = Sondyne.Analyse(Take());

        var sung = Sondyne.Render(take);

        Assert.Equal(take.Clip.Samples, sung.Samples);
    }
}
