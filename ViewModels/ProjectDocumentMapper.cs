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
            track.Effects.Count == 0
                ? null
                : track.Effects
                    .Select(effect => new EffectDocument(effect.Name, effect.IsEnabled, effect.Values.ToList(), effect.PresetName, effect.IsPresetModified))
                    .ToList(),
            track.PluginState,
            track.PluginEditorState,
            track.SoundName,
            track.MidiControllerEvents.Count == 0
                ? null
                : track.MidiControllerEvents.Select(e => new MidiControllerEventDocument(e.Step, e.Controller, e.Value)).ToList());
    }

    private static IReadOnlyList<MidiControllerEventViewModel> Controllers(IEnumerable<MidiControllerEventDocument>? documents) =>
        documents?.Select(e => new MidiControllerEventViewModel(e.Step, e.Controller, e.Value)).ToList()
        ?? (IReadOnlyList<MidiControllerEventViewModel>)Array.Empty<MidiControllerEventViewModel>();

    // Effects whose name Sonator does not know (saved by a newer version) are left out.
    internal static IEnumerable<TrackEffectViewModel> BuildEffects(IEnumerable<EffectDocument>? documents) =>
        (documents ?? Enumerable.Empty<EffectDocument>())
            .Select(document => (Document: document, Definition: EffectCatalog.FindByName(document.Name)))
            .Where(item => item.Definition is not null)
            .Select(item => new TrackEffectViewModel(item.Definition!, item.Document.IsEnabled, item.Document.Values, item.Document.Preset, item.Document.PresetModified));

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
        targetTrack.IsRecordArmed = sourceTrack.IsRecordArmed;
        targetTrack.PluginFilePath = sourceTrack.PluginFilePath;
        targetTrack.PluginState = sourceTrack.PluginState;
        targetTrack.PluginEditorState = sourceTrack.PluginEditorState;
        targetTrack.SoundName = sourceTrack.SoundName;
        targetTrack.MidiControllerEvents = Controllers(sourceTrack.MidiControllerEvents);
        targetTrack.AudioClipPath = sourceTrack.AudioClipPath;
        targetTrack.InstrumentIconKey = sourceTrack.InstrumentIcon;
        targetTrack.Effects.Clear();
        foreach (var effect in BuildEffects(sourceTrack.Effects))
        {
            targetTrack.Effects.Add(effect);
        }
        targetTrack.TrackColorBrush = CreateTrackColorBrush(sourceTrack.TrackColor);
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
            PluginFilePath = trackDocument.PluginFilePath,
            PluginState = trackDocument.PluginState,
            PluginEditorState = trackDocument.PluginEditorState,
            SoundName = trackDocument.SoundName,
            MidiControllerEvents = Controllers(trackDocument.MidiControllerEvents),
            AudioClipPath = trackDocument.AudioClipPath,
            InstrumentIconKey = trackDocument.InstrumentIcon
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
