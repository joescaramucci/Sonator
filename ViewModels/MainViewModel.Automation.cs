using System.ComponentModel;
using System.Diagnostics;
using Sonator.Services;
using Sonator.Services.Vst3;

namespace Sonator.ViewModels;

// ---- Automation of the plug-ins' and effects' knobs, and automation written by hand while the song plays ----
public sealed partial class MainViewModel
{
    private readonly AutomationRecorder _automationRecorder = new();
    private readonly Dictionary<(PatternTrackViewModel Track, AutomatedParameter Parameter), double> _appliedParameters = new();
    private readonly HashSet<PatternTrackViewModel> _automationWatched = new();
    private readonly Dictionary<Vst3ModuleHandle, Dictionary<uint, string>> _parameterTitles = new();
    private bool _applyingAutomation;
    private bool _isAutomationWriteEnabled;

    // While on, what is moved as the song plays is written into the tracks' automation: a fader, a pan knob, a
    // knob of Sonator's effects, a knob in a plug-in's window.
    public bool IsAutomationWriteEnabled
    {
        get => _isAutomationWriteEnabled;
        set
        {
            if (SetProperty(ref _isAutomationWriteEnabled, value))
            {
                StatusMessage = value
                    ? "Automation write: what you move while the song plays (faders, pan, effect and plug-in knobs) is written into the tracks"
                    : "Automation write off: the automation only plays";
            }
        }
    }

    private bool IsWritingAutomation => _isAutomationWriteEnabled && IsPlaying;

    // The knob last turned, and on which track: the Automate menu offers its lane first.
    public (PatternTrackViewModel Track, AutomatedParameter Parameter)? LastTouchedParameter { get; private set; }

    // The control last moved, a knob or a track's fader or pan, for MIDI Learn.
    public (PatternTrackViewModel Track, AutomationTarget Target, AutomatedParameter? Parameter)? LastTouchedControl { get; private set; }

    // About every 15 ms: what the plug-ins' windows turned, the passes written, and the automation played where
    // the song is.
    internal void RunAutomation()
    {
        var step = PatternStepOf(IsPlaying ? _engine.LivePosition : CurrentStepIndex);
        WatchTracksForAutomation();
        ReadPluginEdits();
        _automationRecorder.Commit(Stopwatch.GetTimestamp(), all: !IsPlaying, CaptureUndoStateIfNeeded);
        ApplyAutomationAt(step);
        ShowShownParameterValues();
    }

    private void WatchTracksForAutomation()
    {
        foreach (var track in Tracks)
        {
            if (_automationWatched.Add(track))
            {
                track.PropertyChanged += OnAutomatedTrackChanged;
                track.EffectsChanged += OnAutomatedEffectChanged;
            }
        }

        if (_automationWatched.Count > Tracks.Count)
        {
            var current = Tracks.ToHashSet();
            foreach (var gone in _automationWatched.Where(track => !current.Contains(track)).ToList())
            {
                gone.PropertyChanged -= OnAutomatedTrackChanged;
                gone.EffectsChanged -= OnAutomatedEffectChanged;
                _automationWatched.Remove(gone);
            }

            foreach (var key in _appliedParameters.Keys.Where(key => !current.Contains(key.Track)).ToList())
            {
                _appliedParameters.Remove(key);
            }
        }
    }

    private void OnAutomatedTrackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not PatternTrackViewModel track || _applyingAutomation
            || e.PropertyName is not (nameof(PatternTrackViewModel.Volume) or nameof(PatternTrackViewModel.Pan)))
        {
            return;
        }

        var target = e.PropertyName == nameof(PatternTrackViewModel.Volume) ? AutomationTarget.Volume : AutomationTarget.Pan;
        LastTouchedControl = (track, target, null);
        if (IsWritingAutomation)
        {
            _automationRecorder.Record(track, target, null, PatternStepOf(_engine.LivePosition), target == AutomationTarget.Volume ? track.Volume : track.Pan, Stopwatch.GetTimestamp());
        }
    }

    // A knob of one of Sonator's effects turned by hand.
    private void OnAutomatedEffectChanged(object? sender, TrackEffectChange change)
    {
        if (_applyingAutomation || sender is not PatternTrackViewModel track
            || change is not { Effect: { IsPlugin: false } effect, ParameterIndex: >= 0 } || change.ParameterIndex >= effect.Parameters.Count)
        {
            return;
        }

        var knob = effect.Parameters[change.ParameterIndex];
        var parameter = new AutomatedParameter(effect.InstanceId, (uint)change.ParameterIndex, $"{effect.Name} · {knob.Name}");
        Touched(track, parameter, knob.Position);
    }

    private void Touched(PatternTrackViewModel track, AutomatedParameter parameter, double value)
    {
        LastTouchedParameter = (track, parameter);
        LastTouchedControl = (track, AutomationTarget.Parameter, parameter);
        if (IsWritingAutomation)
        {
            _automationRecorder.Record(track, AutomationTarget.Parameter, parameter, PatternStepOf(_engine.LivePosition), value, Stopwatch.GetTimestamp());
        }
    }

    // The knobs turned in the windows of the tracks' plug-ins, instruments and effects.
    private void ReadPluginEdits()
    {
        foreach (var track in Tracks)
        {
            if (_trackInstruments.RunningFor(track) is { } instrument)
            {
                ReadEdits(track, instrument, null);
            }

            foreach (var effect in track.Effects)
            {
                if (_trackEffects.LoadedPluginModule(effect) is { } module)
                {
                    ReadEdits(track, module, effect);
                }
            }
        }
    }

    private void ReadEdits(PatternTrackViewModel track, Vst3ModuleHandle module, TrackEffectViewModel? effect)
    {
        foreach (var edit in _liveInstrumentHost.TakeParameterEdits(module))
        {
            if (edit.Kind != Vst3EditKind.Value)
            {
                continue;
            }

            var parameter = new AutomatedParameter(effect?.InstanceId, edit.ParameterId,
                ParameterName(module, edit.ParameterId, effect?.Name ?? track.PluginName ?? track.Name));

            // The plug-in telling back a value the automation has just set is not a hand on the knob.
            if (_appliedParameters.TryGetValue((track, parameter), out var applied) && Math.Abs(applied - edit.Value) < 1e-6)
            {
                continue;
            }

            Touched(track, parameter, edit.Value);
        }
    }

    // "EZdrummer 3 · Kick volume": the plug-in's (or effect's) name and the parameter's title, read once.
    private string ParameterName(Vst3ModuleHandle module, uint parameterId, string owner)
    {
        if (!_parameterTitles.TryGetValue(module, out var titles))
        {
            titles = _liveInstrumentHost.GetParameters(module)
                .GroupBy(parameter => parameter.Id)
                .ToDictionary(group => group.Key, group => group.First().Title);
            _parameterTitles[module] = titles;
        }

        return titles.TryGetValue(parameterId, out var title) && !string.IsNullOrWhiteSpace(title)
            ? $"{owner} · {title}"
            : $"{owner} · parameter {parameterId}";
    }

    // Moves the track's knobs to their lanes where the song is, those the hand is moving apart.
    private void ApplyParameterAutomation(PatternTrackViewModel track, double step)
    {
        foreach (var lane in track.ParameterAutomation)
        {
            if (_automationRecorder.IsWriting(track, AutomationTarget.Parameter, lane.Target)
                || Automation.ValueAt(lane.Points, step) is not { } value)
            {
                continue;
            }

            var key = (track, lane.Target);
            if (_appliedParameters.TryGetValue(key, out var last) && Math.Abs(last - value) < 1e-4)
            {
                continue;
            }

            if (SetAutomatedParameter(track, lane.Target, value))
            {
                _appliedParameters[key] = value;
            }
        }
    }

    // Sets a knob, of an effect of Sonator's (its travel), an effect plug-in or the instrument. False when it is
    // not there, or its plug-in is not running yet.
    private bool SetAutomatedParameter(PatternTrackViewModel track, AutomatedParameter parameter, double value)
    {
        if (parameter.Effect is { } id)
        {
            var effect = track.Effects.FirstOrDefault(item => item.InstanceId == id);
            if (effect is null)
            {
                return false;
            }

            if (!effect.IsPlugin)
            {
                if (parameter.Parameter >= effect.Parameters.Count)
                {
                    return false;
                }

                _applyingAutomation = true;
                try
                {
                    effect.Parameters[(int)parameter.Parameter].Position = value;
                }
                finally
                {
                    _applyingAutomation = false;
                }

                return true;
            }

            return _trackEffects.LoadedPluginModule(effect) is { } module && _liveInstrumentHost.SetParameterValue(module, parameter.Parameter, value);
        }

        return _trackInstruments.RunningFor(track) is { } instrument && _liveInstrumentHost.SetParameterValue(instrument, parameter.Parameter, value);
    }

    // Where the knob of each parameter lane shown is now, for its dashed line.
    private void ShowShownParameterValues()
    {
        foreach (var track in Tracks)
        {
            if (track is { ShownAutomation: AutomationTarget.Parameter, ShownParameter: { } parameter }
                && CurrentParameterValue(track, parameter) is { } value)
            {
                track.ShownParameterValue = value;
            }
        }
    }

    private double? CurrentParameterValue(PatternTrackViewModel track, AutomatedParameter parameter)
    {
        if (parameter.Effect is { } id)
        {
            var effect = track.Effects.FirstOrDefault(item => item.InstanceId == id);
            return effect switch
            {
                { IsPlugin: false } when parameter.Parameter < effect.Parameters.Count => effect.Parameters[(int)parameter.Parameter].Position,
                { IsPlugin: true } when _trackEffects.LoadedPluginModule(effect) is { } module => _liveInstrumentHost.GetParameterValue(module, parameter.Parameter),
                _ => null,
            };
        }

        return _trackInstruments.RunningFor(track) is { } instrument ? _liveInstrumentHost.GetParameterValue(instrument, parameter.Parameter) : null;
    }

    // ---- MIDI Learn ----

    private volatile bool _isMidiLearning;
    private IReadOnlyList<MidiMapping> _midiMappings = Array.Empty<MidiMapping>();

    // While on, the next controller moved on the MIDI keyboard is assigned to the control last moved in Sonator.
    public bool IsMidiLearning
    {
        get => _isMidiLearning;
        set
        {
            if (_isMidiLearning == value)
            {
                return;
            }

            _isMidiLearning = value;
            RaisePropertyChanged();
            if (value)
            {
                StatusMessage = LastTouchedControl is { } control
                    ? $"MIDI Learn: move a knob, fader or pedal on the MIDI keyboard to assign it to {ControlName(control.Track, control.Target, control.Parameter)}"
                    : "MIDI Learn: first move the control in Sonator (a fader, a pan or a knob), then a knob on the MIDI keyboard";
            }
        }
    }

    // The controllers assigned to controls of Sonator's.
    public IReadOnlyList<MidiMapping> MidiMappings
    {
        get => _midiMappings;
        private set => SetProperty(ref _midiMappings, value);
    }

    // Read on the MIDI thread: the list is replaced, never changed.
    private MidiMapping? MappingFor(MidiControllerMessage controller)
    {
        foreach (var mapping in _midiMappings)
        {
            if (mapping.Channel == controller.Channel && mapping.Controller == controller.Controller)
            {
                return mapping;
            }
        }

        return null;
    }

    private static string ControlName(PatternTrackViewModel track, AutomationTarget target, AutomatedParameter? parameter) => target switch
    {
        AutomationTarget.Volume => $"{track.Name} volume",
        AutomationTarget.Pan => $"{track.Name} pan",
        _ => $"{track.Name} · {parameter?.Name}",
    };

    private PatternTrackViewModel? TrackById(Guid id) =>
        Tracks.Concat(Tracks.SelectMany(track => track.KitPieces)).FirstOrDefault(track => track.BusId == id);

    // On the UI thread: learns the controller, or moves the control it is assigned to.
    internal void OnMappedController(MidiControllerMessage controller)
    {
        if (_isMidiLearning)
        {
            Learn(controller);
            return;
        }

        if (MappingFor(controller) is not { } mapping || TrackById(mapping.Track) is not { } track)
        {
            return;
        }

        switch (mapping.Target)
        {
            case AutomationTarget.Volume:
                track.Volume = controller.Value;
                break;
            case AutomationTarget.Pan:
                track.Pan = controller.Value * 2 - 1;
                break;
            case AutomationTarget.Parameter when mapping.Parameter is { } parameter:
                if (SetAutomatedParameter(track, parameter, controller.Value))
                {
                    Touched(track, parameter, controller.Value);
                }

                break;
        }
    }

    private void Learn(MidiControllerMessage controller)
    {
        if (LastTouchedControl is not { } control)
        {
            StatusMessage = "MIDI Learn: first move the control in Sonator (a fader, a pan or a knob), then a knob on the MIDI keyboard";
            return;
        }

        CaptureUndoStateIfNeeded();
        var name = ControlName(control.Track, control.Target, control.Parameter);
        var mapping = new MidiMapping(controller.Channel, controller.Controller, control.Track.BusId, control.Target, control.Parameter, name);
        MidiMappings = _midiMappings
            .Where(other => !(other.Channel == mapping.Channel && other.Controller == mapping.Controller))
            .Append(mapping)
            .ToList();
        IsMidiLearning = false;
        StatusMessage = $"MIDI Learn: {mapping.Describe()}";
    }

    public void ForgetMidiMapping(MidiMapping mapping)
    {
        CaptureUndoStateIfNeeded();
        MidiMappings = _midiMappings.Where(other => other != mapping).ToList();
        StatusMessage = $"Forgotten: {mapping.Describe()}";
    }

    public void ForgetAllMidiMappings()
    {
        if (_midiMappings.Count == 0)
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        MidiMappings = Array.Empty<MidiMapping>();
        StatusMessage = "Every MIDI assignment forgotten";
    }

    private List<Models.MidiMappingDocument>? MidiMappingDocuments() =>
        _midiMappings.Count == 0
            ? null
            : _midiMappings.Select(mapping => new Models.MidiMappingDocument(
                mapping.Channel, mapping.Controller, mapping.Track, mapping.Target.ToString(),
                mapping.Parameter?.Effect, mapping.Parameter?.Parameter, mapping.Name)).ToList();

    private void RestoreMidiMappings(IEnumerable<Models.MidiMappingDocument>? documents)
    {
        MidiMappings = documents?
            .Where(document => Enum.TryParse<AutomationTarget>(document.Target, out _))
            .Select(document =>
            {
                var target = Enum.Parse<AutomationTarget>(document.Target);
                var parameter = target == AutomationTarget.Parameter && document.Parameter is { } id
                    ? new AutomatedParameter(document.Effect, id, document.Name)
                    : null;
                return new MidiMapping(document.Channel, document.Controller, document.Track, target, parameter, document.Name);
            })
            .ToList()
            ?? (IReadOnlyList<MidiMapping>)Array.Empty<MidiMapping>();
    }

    // The tracks an effect of a track can take its sidechain from: none, the other tracks, and the pieces of their
    // drum kits that have played (the kick, to duck the bass under it).
    public IReadOnlyList<SidechainChoice> SidechainChoices(PatternTrackViewModel forTrack)
    {
        var choices = new List<SidechainChoice> { new(Guid.Empty, "none") };
        foreach (var track in Tracks.Where(track => !ReferenceEquals(track, forTrack)))
        {
            choices.Add(new SidechainChoice(track.BusId, track.Name));
            choices.AddRange(track.KitPieces
                .Where(piece => piece.IsHeard && !ReferenceEquals(piece, forTrack))
                .Select(piece => new SidechainChoice(piece.BusId, $"{track.Name} · {piece.Name}")));
        }

        return choices;
    }

    // Shows a parameter's lane over the track's row in the timeline.
    public void ShowParameterAutomation(PatternTrackViewModel? track, AutomatedParameter parameter)
    {
        if (track is null)
        {
            return;
        }

        track.ShownParameter = parameter;
        track.ShownAutomation = AutomationTarget.Parameter;
        if (CurrentParameterValue(track, parameter) is { } value)
        {
            track.ShownParameterValue = value;
        }

        StatusMessage = $"{track.Name}: {parameter.Name} automation. Click to add a point, drag to move it, right-click to remove it; or turn the knob with AUTO WRITE on while the song plays";
    }

    // The knobs of a track's effect of Sonator's, or of its effect plug-in or instrument (Effect null), that can be
    // automated, by name. A plug-in is loaded (the instrument started) to read them.
    public IReadOnlyList<AutomatedParameter> AutomatableParameters(PatternTrackViewModel track, TrackEffectViewModel? effect)
    {
        if (effect is { IsPlugin: false })
        {
            return effect.Parameters
                .Select(knob => new AutomatedParameter(effect.InstanceId, (uint)knob.Index, $"{effect.Name} · {knob.Name}"))
                .ToList();
        }

        var module = effect is null ? EnsureTrackInstrument(track) : _trackEffects.PluginModuleFor(effect);
        if (module is null)
        {
            return Array.Empty<AutomatedParameter>();
        }

        var owner = effect?.Name ?? track.PluginName ?? track.Name;
        return _liveInstrumentHost.GetParameters(module)
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Title))
            .Select(parameter => new AutomatedParameter(effect?.InstanceId, parameter.Id, $"{owner} · {parameter.Title}"))
            .Distinct()
            .ToList();
    }
}
