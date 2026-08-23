using System.Collections.ObjectModel;

namespace Sonator.ViewModels;

public sealed class PianoRollRowViewModel
{
    public PianoRollRowViewModel(int midiNoteNumber, ObservableCollection<PianoRollCellViewModel> cells)
    {
        MidiNoteNumber = midiNoteNumber;
        Cells = cells;
    }

    public int MidiNoteNumber { get; }

    public string NoteLabel => MidiNoteHelper.ToLabel(MidiNoteNumber);

    public ObservableCollection<PianoRollCellViewModel> Cells { get; }
}
