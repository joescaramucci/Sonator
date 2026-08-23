namespace Sonator.ViewModels;

public sealed class VstPluginViewModel : ViewModelBase
{
    public VstPluginViewModel(string name, string filePath, string kind)
    {
        Name = name;
        FilePath = filePath;
        Kind = kind;
        Badge = kind.Length >= 2 ? kind[..2].ToUpperInvariant() : kind.ToUpperInvariant();
    }

    public string Name { get; }

    public string FilePath { get; }

    public string Kind { get; }

    public string Badge { get; }
}
