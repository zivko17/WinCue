// =============================================================================
// App.xaml.cs — arranque de Secuence.App.
// =============================================================================
// Esta primera versión no tiene controlador ni transporte: solo carga un show
// en un WinCue.Core.CueModel (la misma capa de datos que usa WinCue) y se lo
// pasa a la ventana para pintarlo. Por qué SIN MainController todavía: el
// controlador dispara audio/vídeo con motores que este hito no monta, y la
// tarea es ver la lista, no reproducirla.
//
// Ruta del show: por argumento (como WinCue: el primer argumento que acabe en
// .json y exista), o si no se da ninguno, tests/show-prueba/show-prueba.json
// buscando la raíz del repo hacia arriba desde la carpeta del ejecutable —
// hace falta porque AppContext.BaseDirectory cuelga muy hondo bajo bin\... y
// el show de pruebas vive fuera del árbol de publicación.
//
// Un show que no carga NO debe tirar la ventana: se abre igual, vacía, con el
// motivo en el título. Es la única garantía que pide la verificación de este
// hito ("el proceso sigue vivo varios segundos").
// =============================================================================

using Microsoft.UI.Xaml;
using WinCue.Core;

namespace Secuence.App;

public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var model = new CueModel();
        string? showPath = ResolverRutaDelShow();
        string? error = null;

        if (showPath is not null)
        {
            try
            {
                var cues = CueModel.FromJson(File.ReadAllText(showPath));
                model.ReplaceAll(cues);
            }
            catch (Exception exc) when (exc is FormatException or IOException or UnauthorizedAccessException)
            {
                error = exc.Message;
            }
        }
        else
        {
            error = "No se encontró ningún show (ni por argumento ni el de pruebas por defecto).";
        }

        _window = new MainWindow(model, showPath, error);
        _window.Activate();
    }

    /// <summary>El primer argumento de línea de comandos que sea un .json
    /// existente, o tests/show-prueba/show-prueba.json si no se dio ninguno.</summary>
    private static string? ResolverRutaDelShow()
    {
        string? porArgumento = Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(a => a.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        if (porArgumento is not null)
            return Path.GetFullPath(porArgumento);

        return BuscarShowDePruebaDesdeLaRaizDelRepo();
    }

    /// <summary>Sube desde la carpeta del ejecutable hasta encontrar
    /// WinCueSharp.sln (marca de la raíz del repo) y, si la encuentra,
    /// comprueba si trae tests/show-prueba/show-prueba.json.</summary>
    private static string? BuscarShowDePruebaDesdeLaRaizDelRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "WinCueSharp.sln")))
                continue;

            string candidato = Path.Combine(dir.FullName, "tests", "show-prueba", "show-prueba.json");
            return File.Exists(candidato) ? candidato : null;
        }
        return null;
    }
}
