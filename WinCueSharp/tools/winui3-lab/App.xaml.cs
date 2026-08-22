// =============================================================================
// App.xaml.cs — arranque del banco de pruebas del hito 0.
// =============================================================================

using Microsoft.UI.Xaml;

namespace WinUi3Lab;

public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
