// =============================================================================
// MainWindow.xaml.cs — construye la lista de filas y la vuelca en la tabla.
// =============================================================================
// Sin controlador todavía: la ventana solo LEE el CueModel que le pasa App
// (ver App.xaml.cs) y arma una fila por cue. El día que exista un
// MainController detrás, esto pasa a suscribirse a sus eventos; hoy no hay
// nada que refrescar, así que un ItemsSource fijado una vez basta.
// =============================================================================

using Microsoft.UI.Xaml;
using WinCue.Core;

namespace Secuence.App;

public sealed partial class MainWindow : Window
{
    public MainWindow(CueModel model, string? showPath, string? error)
    {
        InitializeComponent();

        Title = "Secuence";

        var filas = model.Cues.Select(c => new CueRow(c, model)).ToList();
        CueList.ItemsSource = filas;

        EstadoTexto.Text = error is not null
            ? $"No se pudo cargar el show: {error}"
            : $"{showPath} — {filas.Count} cues";
    }
}
