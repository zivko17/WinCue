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

public partial class App : Application
{
    private MainController? _controller;
    private AudioEngine? _audio;
    private VideoEngine? _video;
    private DispatcherTimer? _tick;
    private DispatcherTimer? _autosaveTimer;
    private string _autosavePath = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Action<Action> dispatch = action => Dispatcher.BeginInvoke(action);
        var settings = SettingsManager.Instance;
        var model = new CueModel();
        var window = new MainWindow();
        MainWindow = window;

        // 5a: dispositivo por defecto; la selección por nombre llega con el
        // diálogo de settings (fase 5c)
        _audio = new AudioEngine(
            dispatch,
            driver: settings.Get("audio_driver", "wasapi"),
            sampleRate: settings.Get("audio_sample_rate", 48000));

        _video = new VideoEngine(() => 0, dispatch);   // el controlador fija el provider

        var scheduler = new TimerScheduler(dispatch);
        _controller = new MainController(model, window, _audio, _video, scheduler, settings);
        window.Attach(model, _controller, settings, applyEngineSettings: () =>
        {
            // Reconfigurar el dispositivo de audio en caliente tras guardar settings
            string driver = settings.Get("audio_driver", "wasapi");
            string devName = settings.Get("audio_device_name", "");
            string? deviceId = null;
            if (devName.Length > 0 && driver != "asio")
                deviceId = AudioEngine.ListDevices()
                    .FirstOrDefault(d => d.Name == devName)?.Id;
            _audio.SetDevice(deviceId, driver,
                asioDriverName: driver == "asio" ? devName : null);
            _controller.OnMasterVolume(settings.Get("master_volume", 100));
        });

        // Tick de 100 ms para el panel de activos y el countdown
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _tick.Tick += (_, _) => _controller.Tick();
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

        // Recuperación de autoguardado (cierre inesperado en la sesión anterior)
        _autosavePath = Path.Combine(AppContext.BaseDirectory, "autosave.json");
        bool recovered = false;
        if (File.Exists(_autosavePath) && new FileInfo(_autosavePath).Length > 10)
        {
            if (MessageBox.Show(window,
                    "Se encontró un autoguardado de una sesión que no se cerró " +
                    "correctamente.\n\n¿Recuperar ese show?",
                    "WinCue — Recuperación", MessageBoxButton.YesNo,
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
            _controller.LoadShow(showPath);

        // Autoguardado cada 60 s
        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _autosaveTimer.Tick += (_, _) => _controller.Autosave(_autosavePath);
        _autosaveTimer.Start();

        // Arranque de humo: cerrar solo (verificación sin operador)
        if (e.Args.Contains("--smoke"))
        {
            var quit = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            quit.Tick += (_, _) => Shutdown(0);
            quit.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tick?.Stop();
        _autosaveTimer?.Stop();
        _controller?.Shutdown();
        _audio?.Dispose();
        _video?.Dispose();
        // Cierre limpio: el autoguardado ya no es necesario
        try { if (_autosavePath.Length > 0) File.Delete(_autosavePath); }
        catch (IOException) { }
        base.OnExit(e);
    }
}
