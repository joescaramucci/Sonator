using System.Windows;

namespace Sonator;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        HelpBadge.Install(); // the "?" with each window's help
        base.OnStartup(e);
    }
}
