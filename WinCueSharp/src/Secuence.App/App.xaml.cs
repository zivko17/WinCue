// =============================================================================
// App.xaml.cs — arranque de Secuence.App.
// =============================================================================
// Arranca el motor de audio real, el motor de vídeo real (WinCue.Video, con
// mpv — sustituye aquí al NullVideoEngine provisional de la tarea anterior,
// ya borrado) y MainController (el mismo WinCue.Controller que usa WinCue, ni
// una línea copiada), arma el MainController con el CueModel y la ventana, y
// conecta el tick de 100 ms que hace correr la secuencia. Al pulsar GO, el
// controlador manda de verdad sobre el AudioEngine y el VideoEngine: los
// cues de audio SUENAN y un cue de vídeo se ve en la ventana de salida
// (VideoOutputWindow.xaml.cs), a pantalla completa en el monitor de Settings.
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
//
// Y ese volcado cuelga de LOS DOS enganches: el de AppDomain y el de
// Application.UnhandledException (WinUI 3). El segundo es el que ve las
// excepciones del hilo de interfaz —construir un control, un Draw de Win2D,
// un manejador de evento—, que son justo las que tira esta app; con solo el
// primero, un fallo al abrir una pestaña podía no dejar ni una línea escrita.
// =============================================================================

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinCue.Audio;
using WinCue.Controller;
using WinCue.Core;
using WinCue.Remote;
using WinCue.Video;

namespace Secuence.App;

// CA1001: App no se "dispose-a" — _audio/_video/_remote se liberan
// deterministamente en window.Closed (ver OnLaunched), el punto de cierre de
// esta ventana única. Mismo motivo y mismo pragma que WinCue.App.App.
#pragma warning disable CA1001
public partial class App : Application
{
#pragma warning restore CA1001
    private Window? _window;
    private AudioEngine? _audio;
    private VideoEngine? _video;
    private MainController? _controller;
    private RemoteInputService? _remote;
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

        // Y el enganche de WinUI 3, que es por donde salen DE VERDAD las
        // excepciones del hilo de interfaz (construir un control, un
        // Draw de Win2D, un manejador de evento). El de AppDomain de arriba
        // no las ve siempre, así que un fallo al abrir una pestaña se podía
        // quedar sin una sola línea en disco — que es como estar a ciegas
        // cuando el que lo sufre está delante de la máquina y yo no.
        // No se marca Handled: el proceso debe caer igual, pero dejando el
        // rastro escrito.
        UnhandledException += (_, e) =>
            DumpCrash("Application.UnhandledException", e.Exception);

        // Antes de construir ninguna ventana: si la fuente de iconos no se
        // aplicó de verdad, mejor una excepción con nombre aquí que una tabla
        // llena de cuadritos (ver IconFont.Comprobar para el porqué exacto).
        IconFont.Comprobar();
        // Y la MISMA fuente por el camino de Win2D, que es otro cargador: que
        // XAML la pinte no dice nada de si DirectWrite la resuelve.
        IconFont.ComprobarWin2D();

        // Idioma de la interfaz (ui_language): faltaba por completo en esta
        // App.xaml.cs — Loc.T() ya se usa por toda Secuence.App (InspectorPanel,
        // MixerPanel, SettingsPanel…) pero sin esta línea Loc.Current se
        // quedaba siempre en su default de arranque (Es), ignorando settings.json.
        // Mismo sitio y mismo criterio que WinCue.App.App.xaml.cs (antes de
        // construir ninguna ventana: el idioma no puede cambiar a media
        // construcción de MainWindow/InspectorPanel).
        var settings = SettingsManager.Instance;
        Loc.SetFromCode(settings.Get("ui_language", "en"));

        var model = new CueModel();
        var window = new MainWindow(model);
        _window = window;

        // DispatcherQueue.TryEnqueue es el equivalente de WinUI 3 al
        // Dispatcher.BeginInvoke de WPF que usa WinCue.App.App.xaml.cs: remite
        // al hilo de UI los eventos que los motores emiten desde otros hilos
        // (decodificación, hilo de audio, timers).
        var dispatcherQueue = window.DispatcherQueue;
        Action<Action> dispatch = action => dispatcherQueue.TryEnqueue(() => action());

        var audio = new AudioEngine(
            dispatch,
            driver: settings.Get("audio_driver", "wasapi"),
            sampleRate: settings.Get("audio_sample_rate", 48000));
        _audio = audio;
        ApplyAudioDevice(settings);

        // VideoEngine real (WinCue.Video, con mpv): esta tarea rellena el
        // hueco que el NullVideoEngine provisional de la tarea anterior
        // dejaba a propósito (ya borrado — nada lo referenciaba). El provider
        // de HWND arranca en 0, como en WinCue.App.App.xaml.cs: es
        // MainController quien lo fija de verdad más abajo, en su propio
        // constructor, llamando a window.GetVideoOutputHandle()
        // (SetWindowHandleProvider, ver MainController.cs) — la ventana de
        // salida no existe todavía y no tiene por qué existir hasta el primer
        // cue de vídeo real.
        var video = new VideoEngine(() => 0, dispatch);
        _video = video;   // CA2000: el Dispose real llega en window.Closed, más abajo

        // COMPOSITOR GPU (Ajustes → Vídeo → «Compositor GPU»): con él, cada
        // cue de vídeo pinta en un NODO del grafo de su salida en vez de en un
        // HWND propio — y de ahí sale la opacidad real entre pistas, que en
        // Secuence no ha existido nunca. El interruptor ya estaba en Ajustes
        // desde la tarea del canvas, pero nadie leía el ajuste: esta línea es
        // la que faltaba (mismo reparto que WinCue.App.App.xaml.cs).
        //
        // LA GUARDA DE ABAJO — POR QUÉ EXISTE, Y POR QUÉ NO SE PUEDE PERDER
        // OTRA VEZ (ya se perdió dos):
        //
        // Este método NO se llama solo al arrancar: cuelga de
        // window.ApplyEngineSettings, que MainWindow.OnSettingsClick invoca
        // CADA VEZ que el operador pulsa Guardar en Ajustes, haya tocado lo
        // que haya tocado (el volumen master, el dispositivo MIDI, la ruta de
        // ffmpeg…). Y el valor de fábrica de video_compositor es FALSE.
        //
        // Sin guarda, la rama de apagado de más abajo se ejecutaba en CADA
        // guardado, con la configuración de fábrica y sin que nadie hubiera
        // tocado el interruptor. Esa rama llama a DiscardCompositorPipelines,
        // que NO limpia solo pipelines de compositor: termina TODOS los
        // MpvPlayer (_players incluye la ruta clásica por HWND, más HAP y
        // ProRes). O sea que pulsar Guardar CORTABA A NEGRO TODO EL VÍDEO AL
        // AIRE. En un teatro, en mitad de una función. Medido y descrito en
        // auditoria-interfaz.md, hallazgo S-72.
        //
        // La guarda ya existió una vez —acotada a los cambios de modo reales,
        // pero dentro de VideoEngine.SetGpuLayerSurfaceProvider— y se PERDIÓ
        // al mover la limpieza a la llamada explícita DiscardCompositorPipelines
        // (commit 52dd0f5d, "la limpieza de pools la pide Secuence"). Vive
        // aquí, en el anfitrión, porque es el anfitrión quien sabe si sus
        // superficies han muerto; el motor no puede adivinarlo (ver la
        // cabecera de VideoEngine.DiscardCompositorPipelines).
        //
        // Lo que NO entra en la guarda: SharePresentSurfaces y el proveedor de
        // superficies se fijan en TODOS los caminos, también en el arranque y
        // también cuando el modo no ha cambiado: dejarlos dentro del "if"
        // volvería a dejar el motor sin proveedor después de un ciclo — que es
        // el otro fallo, el simétrico.
        //
        // Y NO SON GRATIS, aunque lo parezcan. Aquí decía que «son idempotentes
        // y baratos» y es FALSO: SetGpuLayerSurfaceProvider no es una simple
        // asignación — llama a ReleaseFrozenSurfaces() y pone _gpuFailed = false
        // en CADA invocación (VideoEngine.cs:273-281). O sea que cada Guardar de
        // Ajustes suelta las superficies CONGELADAS (las del saliente de un
        // crossfade en marcha) y rearma el aviso de fallo de GPU. Es
        // PREEXISTENTE —esta tanda no lo toca— y queda escrito para que nadie
        // lo dé por inocuo: es lo siguiente que hay que medir de este camino.
        bool? modoCompositorAplicado = null;   // null = todavía no se aplicó ninguno
        void ApplyCompositorMode()
        {
            bool on = settings.Get("video_compositor", false);
            // ¿Es esto un APAGADO DE VERDAD? Solo si el modo que ya estaba
            // aplicado era el encendido. En el arranque (modoAplicado == null)
            // no hay nada que soltar todavía: ni compositores, ni espejo, ni
            // players — pero el proveedor y SharePresentSurfaces de abajo sí se
            // aplican igual, que es lo que el arranque necesita.
            bool seAcabaDeApagar = modoCompositorAplicado == true && !on;
            modoCompositorAplicado = on;

            // Las superficies de PRESENTACIÓN de los pipelines propios (HAP,
            // ProRes) tienen que nacer COMPARTIDAS porque estas capas las abren
            // en OTRO device (D3D11). Se pide aquí, junto al proveedor, porque
            // es exactamente la misma decisión: quien instala capas de
            // Secuence es quien necesita el handle. WinCue no lo pide nunca y
            // sus superficies se siguen asignando igual que siempre — ver
            // VideoEngine.SharePresentSurfaces.
            video.SharePresentSurfaces = on;
            video.SetGpuLayerSurfaceProvider(on
                ? (screen, layer, slotSeq) =>
                    window.ObtenerSuperficieCompositor(screen, layer, slotSeq)
                : null);
            // APAGARLO NO ES SOLO QUITAR EL PROVEEDOR. Aquí, a diferencia de
            // WinCue, las superficies de capa MUEREN con el compositor de su
            // salida (allí son elementos WPF que sobreviven), así que hay que
            // decírselo al motor: sin esto, sus slots se quedan apuntando a
            // capas muertas y, al volver a encender el compositor, el cue
            // sale a una capa que ya no pinta — salida en NEGRO permanente.
            // Es una llamada explícita a propósito: el motor no puede
            // adivinar cuál de los dos productos le está hablando, y
            // hacérselo automáticamente pararía cues al aire en WinCue (ver
            // VideoEngine.DiscardCompositorPipelines).
            //
            // Y el swapchain de cada salida está sobre el HWND hijo que la
            // ruta clásica va a volver a darle a mpv como wid, así que
            // también hay que soltarlo.
            if (seAcabaDeApagar)
            {
                // ORDEN OBLIGATORIO, y la primera línea no es cosmética: el
                // ESPEJO DEL PGM se suelta ANTES QUE EL RESTO DE ESTE BLOQUE.
                // (No «antes que nada», como decía: para cuando se llega aquí,
                // el SetGpuLayerSurfaceProvider de más arriba ya ha ejecutado
                // su ReleaseFrozenSurfaces. Lo que este orden protege es la
                // carrera con DiscardCompositorPipelines, que es la de abajo.)
                //
                // Mientras el espejo siga enganchado, el hilo del compositor
                // anfitrión —un hilo que va
                // A LA SALA— recorre una raíz que en modo LIENZO lleva
                // instantáneas de nodos de OTRAS salidas, y la recorre hasta
                // medio segundo más (el tic del PGM).
                // DiscardCompositorPipelines dispone justo esas fuentes: sin
                // esta línea, ese hilo entra en
                // SharedSurfaceSource.TryAcquireReadOnly, pasa el chequeo de
                // _disposed por delante de la liberación —los dos locks son
                // distintos y no hay nada que los serialice— y muestrea un SRV
                // ya liberado. AccessViolation o DXGI_ERROR_DEVICE_REMOVED en
                // el hilo que presenta en sala, con canvas + 2 salidas + PGM
                // en LIENZO.
                //
                // Cerrar la aplicación siempre fue seguro porque
                // LiveMonitorControl.Dispose() ya suelta el espejo lo primero;
                // lo que no pasaba por ahí era apagarlo EN CALIENTE.
                window.SoltarEspejoPgm();
                video.DiscardCompositorPipelines();
                window.LiberarCompositoresGpu();
            }
        }
        ApplyCompositorMode();

        var scheduler = new TimerScheduler(dispatch);
        var controller = new MainController(model, window, audio, video, scheduler, settings);
        _controller = controller;
        window.Attach(controller);

        // Fase 6 (disparo remoto): mismo servicio que WinCue.App.App.xaml.cs
        // — WinCue.Remote.RemoteInputService, extraído a un proyecto aparte
        // el primer día de esta tarea precisamente para este momento. Escucha
        // OSC/MIDI según settings.json y remite cada mensaje al controlador
        // por el mismo delegado `dispatch` que usan audio y vídeo. Su cabecera
        // es explícita: "nada de aquí puede tirar el show" — todos los
        // errores (puerto ocupado, dispositivo MIDI ausente…) salen por el
        // evento Status, nunca por una excepción, y aquí van a la barra de
        // estado igual que en WinCue.
        var remote = new RemoteInputService(controller, model, settings, dispatch);
        _remote = remote;
        remote.Status += msg => dispatch(() => window.FlashStatus(msg));

        // Ajustes → Audio + Red/Control: reconfigura el dispositivo de audio
        // Y recarga los listeners OSC/MIDI en caliente tras guardar
        // (MainWindow.OnSettingsClick invoca ApplyEngineSettings) — mismo
        // reparto de responsabilidades que WinCue.App.App.xaml.cs
        // (window.Attach(..., applyEngineSettings: () => { ApplyAudioDevice(...); _remote.Apply(); ... })).
        window.ApplyEngineSettings = () =>
        {
            ApplyAudioDevice(settings);
            remote.Apply();
            ApplyCompositorMode();    // modo compositor en caliente
        };
        remote.Apply();

        string? showPath = ResolverRutaDelShow();
        if (showPath is not null)
        {
            controller.LoadShow(showPath);   // resuelve rutas relativas, puebla la tabla, FlashStatus
            window.SetCurrentShowPath(showPath);   // Guardar (Ctrl+S) escribe aquí sin preguntar
        }
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
            remote.Dispose();
            controller.Shutdown();
            window.ShutdownMonitors();   // previos PGM/PVW: su propio mpv + HwndHijo
            audio.Dispose();
            video.Dispose();
            window.CloseVideoOutput();
        };

        _window.Activate();
    }

    /// <summary>Aplica el dispositivo/driver de settings al motor — al
    /// arrancar Y en caliente tras guardar Ajustes (window.ApplyEngineSettings,
    /// más arriba). Idéntica a WinCue.App.App.ApplyAudioDevice: ya no hay
    /// simplificación posible ahora que Secuence también tiene ventana de
    /// Ajustes (ver SettingsPanel.cs).</summary>
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
