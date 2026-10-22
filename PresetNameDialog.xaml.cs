using System.ComponentModel;
using System.Windows;
using Sonator.Services.Effects;

namespace Sonator;

// Asks for the name a preset is saved under. Factory preset names are taken; saving under the name of
// one of the user's own presets replaces it.
public partial class PresetNameDialog : Window, INotifyPropertyChanged
{
    private readonly EffectDefinition _effect;
    private readonly IReadOnlyList<EffectPreset> _userPresets;
    private string _message = string.Empty;
    private bool _confirmedReplace;

    public PresetNameDialog(EffectDefinition effect, string suggestedName, IReadOnlyList<EffectPreset> userPresets)
    {
        _effect = effect;
        _userPresets = userPresets;
        PresetName = suggestedName;
        Heading = $"Save the {effect.Name} settings as a preset";
        InitializeComponent();
        WindowFrame.UseDarkFrame(this);
        DataContext = this;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public string Heading { get; }

    public string PresetName { get; set; }

    public string Message
    {
        get => _message;
        private set
        {
            _message = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Message)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = PresetName.Trim();
        if (name.Length == 0)
        {
            Message = "Type a name for the preset.";
            return;
        }

        if (EffectPresetStore.IsFactoryName(_effect, name))
        {
            Message = $"\"{name}\" is one of Sonator's presets. Choose another name.";
            return;
        }

        // Saving over one of the user's own presets asks once, then a second SAVE replaces it.
        var exists = _userPresets.Any(preset => string.Equals(preset.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (exists && !_confirmedReplace)
        {
            _confirmedReplace = true;
            Message = $"You already have a preset called \"{name}\". Press SAVE again to replace it.";
            return;
        }

        PresetName = name;
        DialogResult = true;
    }
}
