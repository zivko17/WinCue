// =============================================================================
// App.xaml.cs — arranque de Secuence.App.
// =============================================================================
// Esta tarea AÑADE el motor de audio real y MainController (el mismo
// WinCue.Controller que usa WinCue, ni una línea copiada): construye
// AudioEngine (WinCue.Audio, NAudio puro, sin DLL nativa) y un
// NullVideoEngine (ver ese archivo — el vídeo es el paso siguiente del
// roadmap, no este), arma el MainController con el CueModel y la ventana, y
// conecta el tick de 100 ms que hace correr la secuencia. Al pulsar GO, el
// controlador manda de verdad sobre el AudioEngine: los cues 4.1/4.2 del show
// de pruebas (tonos de 440/880 Hz) SUENAN.
//
// El show se carga con controller.LoadShow(ruta) — NUNCA con
// CueModel.FromJson + ReplaceAll a mano (así arrancaba la primera versión de
// este archivo, del hito 0/1, cuando no había controlador). LoadShow no es
// solo parsear JSON: ShowFileService.LoadShow (WinCue.Controller) resuelve
// las rutas de archivo RELATIVAS del show contra la carpeta donde vive el
// .json (WinCue guarda "audio-440.wav", no la ruta absoluta). Sin pasar por
// ahí, AudioEngine.Play recibe "audio-440.wav" a secas, lo busca relativo al
// directorio de trabajo del PROCESO —que no es el de show-prueba.json— y
// falla con "Archivo no encontrado" aunque el fichero exista al lado del
// show. Esto se detectó verificando esta misma tarea: el show cargaba y
// pintaba bien, pero GO no sonaba.
//
// Ruta del show: por argumento (como WinCue: el primer argumento que acabe en
// .json y exista), o si no se da ninguno, tests/show-prueba/show-prueba.json
// buscando la raíz del repo hacia arriba desde la carpeta del ejecutable —
// hace falta porque AppContext.BaseDirectory cuelga muy hondo bajo bin\... y
// el show de pruebas vive fuera del árbol de publicación.
//
// Un show que no carga (o que no se encuentra) NO debe tirar la ventana: se
// abre igual, vacía, con el motivo en la barra de estado (LoadShow ya llama a
// _view.ShowError/FlashStatus por su cuenta) — motores y controlador SIGUEN
// existiendo, así que un show se puede cargar más tarde. Es la única garantía
// que pide la verificación de este hito ("el proceso sigue vivo varios
// segundos").
//
// La fuente de iconos SÍ debe tirar la ventana si no cargó de verdad
// (IconFont.Comprobar(), llamada aquí antes de construir MainWindow, mismo
// momento que IconFont.InstallFont() en WinCue.App): una tabla con cuadritos
// en vez de iconos es un fallo que hay que ver como excepción con nombre, no
// como una captura de pantalla que alguien tiene que interpretar. Se vuelca
// además a logs/crash-*.log por si esto pasa fuera de una sesión de consola.
// =============================================================================

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinCue.Audio;
using WinCue.Controller;
using WinCue.Core;

namespace Secuence.App;

// CA1001: App no se "dispose-a" — _audio se libera deterministamente en
// window.Closed (ver OnLaunched), el punto de cierre de esta ventana única.
// Mismo motivo y mismo pragma que WinCue.App.App.
#pragma warning disable CA1001
public partial class App : Application
{
#pragma warning restore CA1001
    private Window? _window;
    private AudioEngine? _audio;
    private MainController? _controller;
    private DispatcherQueueTimer? _tick;

    public App() => InitializeComponent();

    /// <summary>Vuelca una excepción a logs/crash-*.log. Mismo motivo que
    /// WinCue.App.App.DumpCrash: un fallo de arranque sin rastro en disco es
    /// imposible de diagnosticar después de cerrar la consola.</summary>
    private static string? DumpCrash(string origin, object exceptionObject)
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(file,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {origin}\r\n\r\n{exceptionObject}\r\n");
            return file;
        }
        catch (Exception) { return null; }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            DumpCrash("AppDomain.UnhandledException", e.ExceptionObject);

        // Antes de construir ninguna ventana: si la fuente de iconos no se
        // aplicó de verdad, mejor una excepción con nombre aquí que una tabla
        // llena de cuadritos (ver IconFont.Comprobar para el porqué exacto).
        IconFont.Comprobar();

        var model = new CueModel();
        var window = new MainWindow(model);
        _window = window;

        // DispatcherQueue.TryEnqueue es el equivalente de WinUI 3 al
        // Dispatcher.BeginInvoke de WPF que usa WinCue.App.App.xaml.cs: remite
        // al hilo de UI los eventos que los motores emiten desde otros hilos
        // (decodificación, hilo de audio, timers).
        var dispatcherQueue = window.DispatcherQueue;
        Action<Action> dispatch = action => dispatcherQueue.TryEnqueue(() => action());

        var settings = SettingsManager.Instance;
        var audio = new AudioEngine(
            dispatch,
            driver: settings.Get("audio_driver", "wasapi"),
            sampleRate: settings.Get("audio_sample_rate", 48000));
        _audio = audio;
        ApplyAudioDevice(settings);

        // NullVideoEngine, NO WinCue.Video: el vídeo es el paso siguiente del
        // roadmap de Secuence (ver NullVideoEngine.cs) — MainController exige
        // un IVideoEngine no nulo, pero esta tarea no reproduce vídeo.
        var video = new NullVideoEngine();

        var scheduler = new TimerScheduler(dispatch);
        var controller = new MainController(model, window, audio, video, scheduler, settings);
        _controller = controller;
        window.Attach(controller);

        string? showPath = ResolverRutaDelShow();
        if (showPath is not null)
            controller.LoadShow(showPath);   // resuelve rutas relativas, puebla la tabla, FlashStatus
        else
            window.FlashStatus("No se encontró ningún show (ni por argumento ni el de pruebas por defecto).");

        // Tick de 100 ms: el mismo intervalo que WinCue.App.App.xaml.cs usa
        // para hacer avanzar countdown/panel de activos.
        var tick = dispatcherQueue.CreateTimer();
        _tick = tick;
        tick.Interval = TimeSpan.FromMilliseconds(100);
        tick.Tick += (_, _) => controller.Tick();
        tick.Start();

        window.Closed += (_, _) =>
        {
            tick.Stop();
            controller.Shutdown();
            audio.Dispose();
        };

        _window.Activate();
    }

    /// <summary>Aplica el dispositivo/driver de settings al motor. Simplificada
    /// respecto a WinCue.App.App.ApplyAudioDevice: Secuence todavía no tiene
    /// pantalla de Ajustes propia, así que solo hay un valor que leer al
    /// arrancar (no un "en caliente" que reaplicar tras guardar).</summary>
    private void ApplyAudioDevice(SettingsManager settings)
    {
        if (_audio is null)
            return;
        string driver = settings.Get("audio_driver", "wasapi");
        string devName = settings.Get("audio_device_name", "");
        string? deviceId = null;
        if (devName.Length > 0 && driver != "asio")
            deviceId = AudioEngine.ListDevices()
                .FirstOrDefault(d => d.Name == devName)?.Id;
        _audio.SetDevice(deviceId, driver,
            asioDriverName: driver == "asio" ? devName : null);
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
