using System.Collections.ObjectModel;
using System.Windows.Media;
using Sonator.Models;
using Sonator.Services.Effects;

using static Sonator.ViewModels.PatternFactory;

namespace Sonator.ViewModels;

// Converts between live view models and the persisted project documents.
internal static class ProjectDocumentMapper
{
    internal static PatternDocument BuildPatternDocument(PatternViewModel pattern)
    {
        return new PatternDocument(
            pattern.Name,
            pattern.Summary,
            pattern.Slot,
            pattern.Tracks.Select(BuildTrackDocument).ToList());
    }

    internal static TrackDocument BuildTrackDocument(PatternTrackViewModel track)
    {
        return new TrackDocument(
            track.Name,
            track.Instrument,
            track.Channel,
            track.NoteNumber,
            track.Steps.Select(step => new StepDocument(step.Index, step.IsActive, step.MidiNoteNumber, step.Velocity, step.DurationSteps)).ToList(),
            track.ProgramNumber,
            track.MidiNoteEvents
                .Select(note => new MidiNoteEventDocument(note.StepIndex, note.DurationSteps, note.NoteNumber, note.Velocity, note.StartOffset, note.LengthFraction))
                .ToList(),
            track.TrackType,
            track.Source,
            track.Volume,
            track.IsRecordArmed,
            track.TrackColorBrush.Color.ToString(),
            track.PluginFilePath,
            track.AudioClipPath,
            track.InstrumentIconKey,
            BuildEffectDocuments(track.Effects),
            track.PluginState,
            track.PluginEditorState,
            track.SoundName,
            track.MidiControllerEvents.Count == 0
                ? null
                : track.MidiControllerEvents.Select(e => new MidiControllerEventDocument(e.Step, e.Controller, e.Value)).ToList(),
            track.Pan,
            BuildAutomationDocuments(track.VolumeAutomation),
            BuildAutomationDocuments(track.PanAutomation),
            track.BusId,
            track.Sends.Count == 0 ? null : track.Sends.Select(send => new SendDocument(send.Key, send.Value)).ToList(),
            track.AudioClipStartStep,
            track.NoteBlocks.Count == 0 ? null : track.NoteBlocks.Select(block => new NoteBlockDocument(block.StartStep, block.LengthSteps)).ToList());
    }

    private static IReadOnlyList<NoteBlock> Blocks(IEnumerable<NoteBlockDocument>? documents) =>
        documents?.Select(block => new NoteBlock(block.StartStep, block.LengthSteps)).ToList() ?? (IReadOnlyList<NoteBlock>)Array.Empty<NoteBlock>();

    private static IReadOnlyDictionary<Guid, double> Sends(IEnumerable<SendDocument>? documents) =>
        documents?.GroupBy(send => send.Bus).ToDictionary(group => group.Key, group => group.Last().Level)
        ?? new Dictionary<Guid, double>();

    private static List<AutomationPointDocument>? BuildAutomationDocuments(IReadOnlyList<AutomationPoint> points) =>
        points.Count == 0 ? null : points.Select(point => new AutomationPointDocument(point.Step, point.Value)).ToList();

    // In order of step, whatever order the file had them in.
    private static IReadOnlyList<AutomationPoint> AutomationPoints(IEnumerable<AutomationPointDocument>? documents) =>
        documents?.Select(point => new AutomationPoint(point.Step, point.Value)).OrderBy(point => point.Step).ToList()
        ?? (IReadOnlyList<AutomationPoint>)Array.Empty<AutomationPoint>();

    // Null when there are no effects, so projects without any stay as they were.
    internal static List<EffectDocument>? BuildEffectDocuments(IReadOnlyCollection<TrackEffectViewModel> effects) =>
        effects.Count == 0
            ? null
            : effects
                .Select(effect => new EffectDocument(
                    effect.Name, effect.IsEnabled, effect.Values.ToList(), effect.PresetName, effect.IsPresetModified,
                    effect.PluginPath, effect.PluginState, effect.PluginEditorState,
                    effect.IsPlugin ? effect.InstanceId : null))
                .ToList();

    private static IReadOnlyList<MidiControllerEventViewModel> Controllers(IEnumerable<MidiControllerEventDocument>? documents) =>
        documents?.Select(e => new MidiControllerEventViewModel(e.Step, e.Controller, e.Value)).ToList()
        ?? (IReadOnlyList<MidiControllerEventViewModel>)Array.Empty<MidiControllerEventViewModel>();

    // Effects whose name Sonator does not know (saved by a newer version) are left out. A VST3 effect is kept
    // even when its plug-in is missing here, so the project keeps it for a computer that has it.
    internal static IEnumerable<TrackEffectViewModel> BuildEffects(IEnumerable<EffectDocument>? documents) =>
        (documents ?? Enumerable.Empty<EffectDocument>())
            .Select(document => document.PluginPath is { } pluginPath
                ? TrackEffectViewModel.ForPlugin(pluginPath, document.Name, document.IsEnabled, document.PluginState, document.PluginEditorState, document.PluginInstance)
                : EffectCatalog.FindByName(document.Name) is { } definition
                    ? new TrackEffectViewModel(definition, document.IsEnabled, document.Values, document.Preset, document.PresetModified)
                    : null)
            .OfType<TrackEffectViewModel>();

    internal static void ApplyTrackDocumentToTrack(PatternTrackViewModel targetTrack, TrackDocument sourceTrack)
    {
        targetTrack.Name = sourceTrack.Name;
        targetTrack.Instrument = sourceTrack.Instrument;
        targetTrack.Channel = sourceTrack.Channel;
        targetTrack.NoteNumber = sourceTrack.NoteNumber;
        targetTrack.ProgramNumber = sourceTrack.ProgramNumber;
        targetTrack.TrackType = sourceTrack.TrackType;
        targetTrack.Source = sourceTrack.Source ?? string.Empty;
        targetTrack.Volume = sourceTrack.Volume;
        targetTrack.Pan = sourceTrack.Pan;
        targetTrack.VolumeAutomation = AutomationPoints(sourceTrack.VolumeAutomation);
        targetTrack.PanAutomation = AutomationPoints(sourceTrack.PanAutomation);
        targetTrack.Sends = Sends(sourceTrack.Sends);
        targetTrack.IsRecordArmed = sourceTrack.IsRecordArmed;
        targetTrack.PluginFilePath = sourceTrack.PluginFilePath;
        targetTrack.PluginState = sourceTrack.PluginState;
        targetTrack.PluginEditorState = sourceTrack.PluginEditorState;
        targetTrack.SoundName = sourceTrack.SoundName;
        targetTrack.MidiControllerEvents = Controllers(sourceTrack.MidiControllerEvents);
        targetTrack.AudioClipPath = sourceTrack.AudioClipPath;
        targetTrack.AudioClipStartStep = sourceTrack.AudioClipStartStep;
        targetTrack.InstrumentIconKey = sourceTrack.InstrumentIcon;
        targetTrack.Effects.Clear();
        foreach (var effect in BuildEffects(sourceTrack.Effects))
        {
            // A pasted track's plug-in effects are instances of their own, apart from the copied track's.
            targetTrack.Effects.Add(effect.IsPlugin ? effect.AsNewInstance() : effect);
        }
        targetTrack.TrackColorBrush = CreateTrackColorBrush(sourceTrack.TrackColor);
        targetTrack.NoteBlocks = Blocks(sourceTrack.NoteBlocks);
        targetTrack.MidiNoteEvents = sourceTrack.MidiNoteEvents is null
            ? Array.Empty<MidiNoteEventViewModel>()
            : sourceTrack.MidiNoteEvents
                .Select(note => new MidiNoteEventViewModel(note.StepIndex, note.DurationSteps, note.NoteNumber, note.Velocity, note.StartOffset, note.LengthFraction))
                .ToList();

        var count = Math.Min(targetTrack.Steps.Count, sourceTrack.Steps.Count);
        for (var i = 0; i < count; i++)
        {
            var targetStep = targetTrack.Steps[i];
            var sourceStep = sourceTrack.Steps[i];
            targetStep.IsActive = sourceStep.IsActive;
            targetStep.MidiNoteNumber = sourceStep.MidiNoteNumber;
            targetStep.Velocity = sourceStep.Velocity;
            targetStep.DurationSteps = sourceStep.DurationSteps;
        }
    }

    internal static ArrangeRowDocument BuildArrangeRowDocument(ArrangeRowViewModel row)
    {
        return new ArrangeRowDocument(
            row.TrackName,
            row.Instrument,
            row.Cells.Select(cell => new ArrangeCellDocument(
                cell.Label,
                cell.Detail,
                ((SolidColorBrush)cell.Accent).Color.ToString())).ToList());
    }

    internal static bool IsLegacyProject(string version)
    {
        return version == "1.0" || version == "1.1";
    }

    internal static PatternViewModel ApplyPatternDocument(PatternDocument patternDocument)
    {
        var tracks = new ObservableCollection<PatternTrackViewModel>(
            patternDocument.Tracks.Select(trackDocument => ApplyTrackDocument(trackDocument)));
        return new PatternViewModel(patternDocument.Name, patternDocument.Summary, patternDocument.Slot, tracks);
    }

    internal static PatternTrackViewModel ApplyTrackDocument(TrackDocument trackDocument)
    {
        var steps = new ObservableCollection<StepViewModel>();
        foreach (var stepDocument in trackDocument.Steps)
        {
            steps.Add(new StepViewModel(stepDocument.Index, stepDocument.IsActive)
            {
                MidiNoteNumber = stepDocument.MidiNoteNumber,
                Velocity = stepDocument.Velocity,
                DurationSteps = stepDocument.DurationSteps
            });
        }

        var track = new PatternTrackViewModel(
            trackDocument.Name,
            trackDocument.Instrument,
            trackDocument.Channel,
            trackDocument.NoteNumber,
            steps,
            trackDocument.ProgramNumber,
            trackDocument.MidiNoteEvents?
                .Select(note => new MidiNoteEventViewModel(note.StepIndex, note.DurationSteps, note.NoteNumber, note.Velocity, note.StartOffset, note.LengthFraction))
                .ToList(),
            trackDocument.TrackType,
            trackDocument.Source,
            trackDocument.Volume,
            trackDocument.IsRecordArmed,
            CreateTrackColorBrush(trackDocument.TrackColor))
        {
            Pan = trackDocument.Pan,
            VolumeAutomation = AutomationPoints(trackDocument.VolumeAutomation),
            PanAutomation = AutomationPoints(trackDocument.PanAutomation),
            BusId = trackDocument.BusId ?? Guid.NewGuid(),
            Sends = Sends(trackDocument.Sends),
            PluginFilePath = trackDocument.PluginFilePath,
            PluginState = trackDocument.PluginState,
            PluginEditorState = trackDocument.PluginEditorState,
            SoundName = trackDocument.SoundName,
            MidiControllerEvents = Controllers(trackDocument.MidiControllerEvents),
            AudioClipPath = trackDocument.AudioClipPath,
            AudioClipStartStep = Math.Max(0, trackDocument.AudioClipStartStep),
            InstrumentIconKey = trackDocument.InstrumentIcon,
            NoteBlocks = Blocks(trackDocument.NoteBlocks)
        };
        foreach (var effect in BuildEffects(trackDocument.Effects))
        {
            track.Effects.Add(effect);
        }

        return track;
    }

    internal static ArrangeRowViewModel ApplyArrangeRowDocument(ArrangeRowDocument arrangeDocument)
    {
        var cells = new ObservableCollection<ArrangeCellViewModel>(
            arrangeDocument.Cells.Select((cellDocument, index) => new ArrangeCellViewModel(
                index + 1,
                cellDocument.Label,
                cellDocument.Detail,
                (SolidColorBrush)new BrushConverter().ConvertFromString(cellDocument.Accent)!)));

        return new ArrangeRowViewModel(arrangeDocument.TrackName, arrangeDocument.Instrument, cells);
    }
}
