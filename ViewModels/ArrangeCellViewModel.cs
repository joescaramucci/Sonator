using System.Windows.Media;

namespace Sonator.ViewModels;

public sealed class ArrangeCellViewModel : ViewModelBase
{
    public ArrangeCellViewModel(int barIndex, string label, string detail, Brush accent)
    {
        BarIndex = barIndex;
        Label = label;
        Detail = detail;
        Accent = accent;
    }

    public int BarIndex { get; }

    private string _label = string.Empty;

    public string Label
    {
        get => _label;
        set
        {
            var wasEmpty = string.IsNullOrWhiteSpace(_label);
            if (SetProperty(ref _label, value))
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    Accent = CreateEmptyAccent();
                }
                else if (wasEmpty)
                {
                    Accent = CreateSectionAccent(BarIndex);
                }

                RaisePropertyChanged(nameof(IsSectionStart));
                RaisePropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public string DisplayLabel
    {
        get => _displayLabel;
        set => SetProperty(ref _displayLabel, value);
    }

    private string _detail = string.Empty;
    private string _displayLabel = string.Empty;

    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    private Brush _accent = new SolidColorBrush(Color.FromRgb(36, 36, 36));

    public Brush Accent
    {
        get => _accent;
        set => SetProperty(ref _accent, value);
    }

    public bool IsSectionStart => !string.IsNullOrWhiteSpace(Label);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Label);

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private bool _isSelected;

    public bool IsPlayhead
    {
        get => _isPlayhead;
        set => SetProperty(ref _isPlayhead, value);
    }
    private bool _isPlayhead;

    private static Brush CreateEmptyAccent()
    {
        return new SolidColorBrush(Color.FromRgb(36, 36, 36));
    }

    private static Brush CreateSectionAccent(int barIndex)
    {
        var palette = new[]
        {
            Color.FromRgb(146, 211, 110),
            Color.FromRgb(118, 185, 255),
            Color.FromRgb(247, 196, 107),
            Color.FromRgb(255, 141, 161)
        };

        return new SolidColorBrush(palette[((barIndex - 1) / 4) % palette.Length]);
    }
}
