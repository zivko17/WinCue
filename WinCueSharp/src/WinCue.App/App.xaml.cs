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

        // Cargar show pasado por argumento
        string? showPath = e.Args.FirstOrDefault(a => a.EndsWith(".json",
            StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        if (showPath is not null)
            _controller.LoadShow(showPath);

        window.Show();

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
        _controller?.Shutdown();
        _audio?.Dispose();
        _video?.Dispose();
        base.OnExit(e);
    }
}
