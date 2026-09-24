using System.Windows;

namespace ClipBar.Setup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark, Wpf.Ui.Controls.WindowBackdropType.Mica, false);
        base.OnStartup(e);
    }
}
