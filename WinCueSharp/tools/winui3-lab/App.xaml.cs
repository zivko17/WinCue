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
        // LAB_SPIKE_TECLADO=1 → el spike de SpikeTeclado.cs en vez del banco de
        // mpv: mide si el PreviewKeyDown de un ANCESTRO le gana la tecla al
        // control con foco (ver la cabecera de ese archivo).
        _window = Environment.GetEnvironmentVariable("LAB_SPIKE_TECLADO") == "1"
            ? new SpikeTecladoWindow()
            : new MainWindow();
        _window.Activate();
    }
}
