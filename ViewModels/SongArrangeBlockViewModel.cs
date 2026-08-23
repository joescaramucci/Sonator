namespace Sonator.ViewModels;

public sealed class SongArrangeBlockViewModel : ViewModelBase
{
    public SongArrangeBlockViewModel(int row, int column, int rowSpan, int columnSpan, string label, string detail, string accent)
    {
        Row = row;
        Column = column;
        RowSpan = rowSpan;
        ColumnSpan = columnSpan;
        Label = label;
        Detail = detail;
        Accent = accent;
    }

    public int Row { get; }

    public int Column { get; }

    public int RowSpan { get; }

    public int ColumnSpan { get; }

    public string Label { get; }

    public string Detail { get; }

    public string Accent { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private bool _isSelected;
}
