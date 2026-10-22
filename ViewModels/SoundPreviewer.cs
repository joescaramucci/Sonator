using System.Diagnostics;
using System.IO;
using Sonator.Services;
using Sonator.Services.Sounds;
using Sonator.Services.Vst3;

namespace Sonator.ViewModels;

// Plays a sound of the sound browser before it is loaded on a track: its plug-in runs in an instance of its
// own, outside the tracks, takes the sound, and plays a short phrase (or the start of a groove). The instance
// stays for the next preview of the same plug-in, so trying the sounds of one instrument is quick.
internal sealed class SoundPreviewer : IDisposable
{
    private readonly IVst3PluginHost _host;
    private Vst3ModuleHandle? _instrument;
    private string? _instrumentPath;
    private CancellationTokenSource? _playing;

    public SoundPreviewer(IVst3PluginHost host)
    {
        _host = host;
    }

    // Starts playing the sound. Null when it plays, else why it cannot.
    public string? Preview(SoundEntry sound, double tempoBpm)
    {
        Stop();
        if (Instrument(sound.PluginPath, out var error) is not { } instrument)
        {
            return error;
        }

        IReadOnlyList<(double Start, double Length, int NoteNumber, int Velocity)> notes;
        try
        {
            if (sound.Kind == SoundKind.Groove)
            {
                // The first two bars of the groove.
                notes = GrooveReader.Read(sound.PresetPath).Where(note => note.Start < 32).ToList();
            }
            else
            {
                var state = sound.Kind == SoundKind.NksPreset
                    ? NksSoundSource.StateFor(sound.PresetPath, _host.TrySaveState(instrument, 0) ?? Array.Empty<byte>())
                    : File.ReadAllBytes(sound.PresetPath);
                if (!_host.TryLoadState(instrument, 0, state))
                {
                    return $"{sound.PluginName} did not take the sound {sound.Name}";
                }

                notes = Phrase(sound.Category);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or Melanchall.DryWetMidi.Common.MidiException)
        {
            return $"{sound.Name} could not be read";
        }

        var playing = new CancellationTokenSource();
        _playing = playing;
        var secondsPerStep = 60.0 / Math.Max(20, tempoBpm) / 4.0;

        // A sampled instrument (CMI, Emulator, BBC...) reads the new sound's samples in the background after
        // taking it: the phrase waits a moment, so its first notes are not lost.
        var settle = sound.Kind == SoundKind.Groove ? 0.0 : 0.8;
        _player = Task.Run(() => Play(instrument, notes, secondsPerStep, settle, playing.Token));
        return null;
    }

    private Task? _player;

    // Silences the preview playing, if any. Returns once no more notes can be sent, so the instance can go.
    public void Stop()
    {
        _playing?.Cancel();
        _playing = null;
        _player?.Wait(TimeSpan.FromSeconds(1));
        _player = null;
        if (_instrument is not null)
        {
            _host.ReleaseAllNotes(_instrument);
        }
    }

    public void Dispose()
    {
        Stop();
        Unload();
    }

    // The preview's own instance of the plug-in, started if it is not the one running.
    private Vst3ModuleHandle? Instrument(string pluginPath, out string? error)
    {
        error = null;
        if (_instrument is not null && string.Equals(_instrumentPath, pluginPath, StringComparison.OrdinalIgnoreCase))
        {
            return _instrument;
        }

        Unload();
        var handle = _host.TryLoadModule(pluginPath, out var loadError);
        if (handle is null)
        {
            error = $"The plug-in could not be loaded: {loadError}";
            return null;
        }

        if (!_host.HasAudioProcessor(handle)
            || !_host.TryStartAudio(handle, AudioOutputSelection.FromSettings(new AppSettingsService().Load()), out var audioError))
        {
            _host.UnloadModule(handle);
            error = "The plug-in's audio did not start";
            return null;
        }

        (_instrument, _instrumentPath) = (handle, pluginPath);
        return handle;
    }

    private void Unload()
    {
        if (_instrument is null)
        {
            return;
        }

        _host.StopAudio(_instrument);
        _host.UnloadModule(_instrument);
        (_instrument, _instrumentPath) = (null, null);
    }

    // What a sound of the category is tried with, in sixteenth-note steps: a C major arpeggio and chord, an
    // octave down for basses; a kick, snare and hi-hat beat for drum sounds.
    internal static IReadOnlyList<(double Start, double Length, int NoteNumber, int Velocity)> Phrase(string category)
    {
        if (category == SoundCategories.Drums)
        {
            return new (double, double, int, int)[]
            {
                (0, 1, 36, 110), (2, 1, 42, 80), (4, 1, 38, 105), (6, 1, 42, 80),
                (8, 1, 36, 110), (10, 1, 36, 95), (12, 1, 38, 105), (14, 1, 46, 85),
            };
        }

        var root = category == SoundCategories.Bass ? 36 : 48;
        return new (double, double, int, int)[]
        {
            (0, 2, root, 100), (2, 2, root + 4, 95), (4, 2, root + 7, 95), (6, 2, root + 12, 100),
            (8, 8, root, 90), (8, 8, root + 7, 85), (8, 8, root + 12, 90), (8, 8, root + 16, 85),
        };
    }

    // Sends the notes on time from a worker thread; stopping releases what is held.
    private void Play(Vst3ModuleHandle instrument, IReadOnlyList<(double Start, double Length, int NoteNumber, int Velocity)> notes,
        double secondsPerStep, double startSeconds, CancellationToken token)
    {
        var events = notes
            .SelectMany(note => new[]
            {
                (Time: startSeconds + note.Start * secondsPerStep, On: true, note.NoteNumber, note.Velocity),
                (Time: startSeconds + (note.Start + note.Length) * secondsPerStep, On: false, note.NoteNumber, Velocity: 0),
            })
            .OrderBy(e => e.Time)
            .ThenBy(e => e.On) // a note-off before a note-on at the same moment
            .ToList();

        var clock = Stopwatch.StartNew();
        foreach (var e in events)
        {
            while (!token.IsCancellationRequested && clock.Elapsed.TotalSeconds < e.Time)
            {
                Thread.Sleep(2);
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            _host.TrySendNote(instrument, 0, e.NoteNumber, e.Velocity, e.On);
        }
    }
}
