using System.Collections.ObjectModel;

namespace Sonator.ViewModels;

public sealed class SongArrangeRowViewModel : ViewModelBase
{
    public SongArrangeRowViewModel(int rowIndex, ObservableCollection<ArrangeCellViewModel> cells)
    {
        RowIndex = rowIndex;
        Cells = cells;

        foreach (var cell in Cells)
        {
            cell.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ArrangeCellViewModel.Label))
                {
                    RaisePropertyChanged(nameof(PatternName));
                }
            };
        }
    }

    public int RowIndex { get; }

    public ObservableCollection<ArrangeCellViewModel> Cells { get; }

    public string PatternName => Cells.FirstOrDefault(cell => !string.IsNullOrWhiteSpace(cell.Label))?.Label ?? string.Empty;

    public string DisplayPatternName
    {
        get => _displayPatternName;
        set => SetProperty(ref _displayPatternName, value);
    }

    public string DisplayBarNumber
    {
        get => _displayBarNumber;
        set => SetProperty(ref _displayBarNumber, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsPointerOver
    {
        get => _isPointerOver;
        set => SetProperty(ref _isPointerOver, value);
    }

    public bool IsDragging
    {
        get => _isDragging;
        set => SetProperty(ref _isDragging, value);
    }

    private string _displayBarNumber = string.Empty;
    private string _displayPatternName = string.Empty;
    private bool _isSelected;
    private bool _isPointerOver;
    private bool _isDragging;
    private bool _isPlaying;

    public bool IsPlaying
    {
        get => _isPlaying;
        set => SetProperty(ref _isPlaying, value);
    }
}
