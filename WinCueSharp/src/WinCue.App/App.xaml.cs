// =============================================================================
// App.xaml.cs — Raíz de composición (equivale a main.py)
// =============================================================================
// Construye modelo → vista → motores → controlador y arranca maximizada.
// Todos los eventos de motores/scheduler se remiten al hilo de UI vía
// Dispatcher (el equivalente de las señales queued de Qt).
//
// Argumentos:
//   <ruta.json>  — carga ese show al arrancar
//   --smoke      — arranque de humo: se cierra solo a los 3 s (verificación)
// =============================================================================

using System.IO;
using System.Windows;
using System.Windows.Threading;
using WinCue.Audio;
using WinCue.Controller;
using WinCue.Core;
using WinCue.Video;

namespace WinCue.App;

// CA1001: Application no se "dispose-a" — los motores (_audio/_video/_remote)
// se liberan deterministamente en OnExit, el punto de cierre de WPF.
#pragma warning disable CA1001
public partial class App : Application
{
#pragma warning restore CA1001
    /// <summary>true en arranque --smoke: sin diálogos modales (colgarían la
    /// verificación desatendida).</summary>
    public static bool SmokeMode { get; private set; }

    private MainController? _controller;
    private AudioEngine? _audio;
    private VideoEngine? _video;
    private RemoteInputService? _remote;
    private StressRunner? _stress;
    private DispatcherTimer? _tick;
    private DispatcherTimer? _autosaveTimer;
    private string _autosavePath = "";

    /// <summary>Vuelca una excepción a logs/crash-*.log (una app de directo no
    /// puede morir en silencio: sin esto, un fallo de UI dejaba la ventana
    /// muerta, el proceso zombi y CERO rastro para diagnosticar).</summary>
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
        catch (Exception) { return null; }   // sin disco no hay volcado, pero no re-reventar
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // La app muere cuando muere la ventana principal — sin esto, cualquier
        // ventana secundaria OCULTA (salidas de vídeo) mantenía un proceso
        // zombi tras cerrarse/morir la principal.
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // Red de seguridad global: volcar SIEMPRE a logs/crash-*.log.
        // UI: se intenta continuar (en directo, un fallo puntual de UI no debe
        // tirar el show); no-UI: es fatal, al menos queda el volcado.
        DispatcherUnhandledException += (_, args) =>
        {
            string? file = DumpCrash("DispatcherUnhandledException (hilo de UI)", args.Exception);
            args.Handled = true;
            if (!SmokeMode)
                (MainWindow as MainWindow)?.FlashStatus(Loc.T(
                    $"{Icons.Warning} ERROR INTERNO — detalle en {Path.GetFileName(file ?? "logs/")} (la app intenta continuar)",
                    $"{Icons.Warning} INTERNAL ERROR — details in {Path.GetFileName(file ?? "logs/")} (the app tries to continue)"));
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            DumpCrash("AppDomain.UnhandledException (fatal, hilo no-UI)", args.ExceptionObject);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DumpCrash("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        // Paleta central ANTES de construir la ventana: el XAML la consume con
        // {DynamicResource ...} y no existe hasta que se publica aquí.
        Theme.Install(Resources);
        // Fuente de iconos, por el mismo motivo y en el mismo momento: los
        // glifos viven en el Área de Uso Privado y sin esto saldrían en blanco.
        Icons.InstallFont();

        Action<Action> dispatch = action => Dispatcher.BeginInvoke(action);
        var settings = SettingsManager.Instance;
        // Idioma de la UI (ES/EN) ANTES de construir la ventana.
        // Default "en": inglés por defecto (el usuario cambia a Español en
        // Configuración → Interfaz si lo prefiere).
        Loc.SetFromCode(settings.Get("ui_language", "en"));
        var model = new CueModel();
        var window = new MainWindow();
        MainWindow = window;

        _audio = new AudioEngine(
            dispatch,
            driver: settings.Get("audio_driver", "wasapi"),
            sampleRate: settings.Get("audio_sample_rate", 48000));
        // VU de PROGRAMA: loopback del dispositivo donde se puede y mezcla
        // interna donde no (ASIO, o loopback caído). El strip MASTER dice cuál.
        _audio.StartProgramMeter();
        _audio.ProgramMeterSourceChanged += _ => window.SetAudioSourceInfo(
            VuSourceText(_audio.ProgramMeterSource), _audio.OutputDescription);
        _audio.OutputDescriptionChanged += _ => window.SetAudioSourceInfo(
            VuSourceText(_audio.ProgramMeterSource), _audio.OutputDescription);
        // VU de AUDICIÓN por loopback (solo si su dispositivo ≠ principal).
        // Los medidores se abren FUERA del hilo de UI: el estado llega por
        // evento, no se puede leer justo después de pedirlo.
        _audio.AuditionMeterActiveChanged += active => window.SetAuditionVuLoopback(active);
        _audio.SetAuditionMeterDevice(settings.Get("audition_device_name", ""));
        _audio.AuditionLevelReport += (l, r) => window.UpdateAuditionVu(l, r);
        // El dispositivo/driver de settings se aplica AQUÍ y no solo al guardar
        // Ajustes (antes el arranque ignoraba audio_device_name por completo:
        // había que abrir y guardar Ajustes para que la salida elegida valiera).
        ApplyAudioDevice(settings);

        _video = new VideoEngine(() => 0, dispatch);   // el controlador fija el provider

        // RELOJ MAESTRO del motor HAP propio: el elapsed del stream de audio
        // espejo del cue (id negativo del controlador), o null si no suena.
        // OJO hilos: se consulta desde el hilo de presentación del player —
        // GetElapsedThreadSafe lee un mapa concurrente + Volatile.Read (no
        // toca el estado del main thread del AudioEngine).
        _video.SetHapClockProvider(cueId =>
            _audio.GetElapsedThreadSafe(MainController.VideoAudioId(cueId)));

        var scheduler = new TimerScheduler(dispatch);
        _controller = new MainController(model, window, _audio, _video, scheduler, settings);

        // Fase 6: disparo remoto (OSC UDP + MIDI IN) según settings → Red
        _remote = new RemoteInputService(_controller, model, settings, dispatch);
        _remote.Status += msg => dispatch(() => window.FlashStatus(msg));

        // Compositor GPU (Settings → Vídeo): opacidad real entre capas
        void ApplyCompositorMode()
        {
            _video.SetGpuLayerSurfaceProvider(
                settings.Get("video_compositor", false)
                    ? (screen, layer, cueId) =>
                        window.GetCompositorSurface(screen, layer, cueId)
                    : null);
        }
        ApplyCompositorMode();

        window.Attach(model, _controller, settings, applyEngineSettings: () =>
        {
            // Reconfigurar el dispositivo de audio en caliente tras guardar settings
            ApplyAudioDevice(settings);
            // El medidor de audición sigue al dispositivo de audición nuevo
            // (SetAuditionMeterDevice avisa por AuditionMeterActiveChanged)
            _audio.SetAuditionMeterDevice(settings.Get("audition_device_name", ""));
            _controller.OnMasterVolume(settings.Get("master_volume", 100));
            _remote.Apply();          // OSC/MIDI IN en caliente
            ApplyCompositorMode();    // modo compositor en caliente
        });
        _remote.Apply();
        // Estado inicial del strip MASTER (los eventos solo avisan de CAMBIOS,
        // y con ASIO la vía del VU nace ya en «mezcla interna» sin cambiar)
        window.SetAudioSourceInfo(
            VuSourceText(_audio.ProgramMeterSource), _audio.OutputDescription);

        // Tick de 100 ms para el panel de activos y el countdown
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _tick.Tick += (_, _) =>
        {
            _controller.Tick();
            // La captura loopback puede caerse (dispositivo desenchufado) y
            // recuperarse sola por su reintento: reevaluar la vía del VU es
            // leer un bool, y así el strip MASTER nunca miente.
            _audio.RefreshProgramMeterSource();
        };
        _tick.Start();

        // Log del show (logs/show-YYYYMMDD.log junto al ejecutable)
        string logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        _controller.ShowLog += line =>
        {
            try
            {
                Directory.CreateDirectory(logDir);
                File.AppendAllText(
                    Path.Combine(logDir, $"show-{DateTime.Now:yyyyMMdd}.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
            }
            catch (IOException) { /* el log nunca debe tirar el show */ }
        };

        window.Show();

        // Modo estrés desatendido (auditoría 04): --stress <show.json> [--stress-loops N]
        int stressIdx = Array.IndexOf(e.Args, "--stress");
        string? stressShow = stressIdx >= 0 && stressIdx + 1 < e.Args.Length
            ? e.Args[stressIdx + 1] : null;
        int loopsIdx = Array.IndexOf(e.Args, "--stress-loops");
        int stressLoops = loopsIdx >= 0 && loopsIdx + 1 < e.Args.Length
            && int.TryParse(e.Args[loopsIdx + 1], out int n) ? n : 0;

        // Recuperación de autoguardado (cierre inesperado en la sesión anterior).
        // En modo --smoke/--stress NO se pregunta: un diálogo modal colgaría la
        // ejecución desatendida.
        bool smokeMode = e.Args.Contains("--smoke") || stressShow is not null;
        SmokeMode = smokeMode;
        _autosavePath = Path.Combine(AppContext.BaseDirectory, "autosave.json");
        bool recovered = false;
        if (!smokeMode && File.Exists(_autosavePath) && new FileInfo(_autosavePath).Length > 10)
        {
            if (MessageBox.Show(window,
                    Loc.T(
                        "Se encontró un autoguardado de una sesión que no se cerró " +
                        "correctamente.\n\n¿Recuperar ese show?",
                        "An autosave was found from a session that didn't close " +
                        "properly.\n\nRecover that show?"),
                    Loc.T("WinCue — Recuperación", "WinCue — Recovery"), MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _controller.LoadShow(_autosavePath);
                recovered = true;
            }
        }

        // Cargar show pasado por argumento (si no se recuperó un autosave)
        string? showPath = e.Args.FirstOrDefault(a => a.EndsWith(".json",
            StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        if (!recovered && showPath is not null)
        {
            _controller.LoadShow(showPath);
            window.SetCurrentShowPath(showPath);
        }

        // Estrés: abrir la salida 1 (el compositor necesita ventana activa),
        // cargar el show de tortura y arrancar el runner
        if (stressShow is not null && File.Exists(stressShow))
        {
            _controller.LoadShow(Path.GetFullPath(stressShow));
            window.ShowVideoOutput(0);
            _stress = new StressRunner(_controller, model, stressLoops);
            _stress.Start();
        }

        // Autoguardado cada 60 s (NO en estrés: ensuciaría autosave.json con el
        // show de tortura y metería I/O periódica en las métricas)
        if (stressShow is null)
        {
            _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _autosaveTimer.Tick += (_, _) => _controller.Autosave(_autosavePath);
            _autosaveTimer.Start();
        }

        // Arranque de humo: cerrar solo (verificación sin operador).
        // OJO: solo --smoke — el modo estrés comparte la supresión de diálogos
        // (SmokeMode) pero debe seguir corriendo horas.
        if (e.Args.Contains("--smoke"))
        {
            var quit = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            quit.Tick += (_, _) => Shutdown(0);
            quit.Start();
        }
    }

    /// <summary>Aplica al motor el dispositivo/driver de settings. Con driver
    /// "asio", audio_device_name guarda el NOMBRE DEL DRIVER ASIO (entradas
    /// «[ASIO] …» del desplegable); con WASAPI, el nombre del dispositivo.</summary>
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

    /// <summary>Texto del origen del VU de PROGRAMA para el strip MASTER.</summary>
    private static string VuSourceText(ProgramMeterSource source) =>
        source == ProgramMeterSource.DeviceLoopback
            ? Loc.T("VU: loopback del dispositivo", "VU: device loopback")
            : Loc.T("VU: mezcla interna", "VU: internal mix");

    protected override void OnExit(ExitEventArgs e)
    {
        _tick?.Stop();
        _autosaveTimer?.Stop();
        _remote?.Dispose();
        _controller?.Shutdown();
        _audio?.Dispose();
        _video?.Dispose();
        // Cierre limpio: el autoguardado ya no es necesario
        try { if (_autosavePath.Length > 0) File.Delete(_autosavePath); }
        catch (IOException) { }
        base.OnExit(e);
    }
}
