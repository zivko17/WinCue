// =============================================================================
// VideoEngine.cs — Motor de Vídeo con libmpv (port de video_engine.py)
// =============================================================================
//
// MÁQUINA DE ESTADOS POR CUE
//   IDLE → LOADING → LOADED → PLAYING → STOPPED / ERROR
//   LOADING con playWhenReady=true equivale al "WAITING_PLAY" interno de
//   Python: al terminar la verificación se reproduce automáticamente.
//
// SALIDA ÚNICA
//   Un solo destino de vídeo = un solo mpv activo. Al arrancar un vídeo se
//   TERMINAN los demás players (gotcha resuelto en Python: command('stop')
//   con keep-open dejaba el último frame congelado superpuesto al siguiente).
//
// SUPERFICIE ABSTRAÍDA
//   El engine no conoce WPF: recibe un Func<nint> que devuelve el HWND de
//   destino en el momento de reproducir (en WPF: un HwndHost; en Python era
//   VideoContainer.get_hwnd()). Si devuelve 0 y no hay vo forzado, es error
//   (mismo "Caso 1" del spec Python). La fase compositor D3D11 sustituirá
//   esto por una superficie de textura sin tocar la API pública.
//
// MODO DEGRADADO
//   Si libmpv-2.dll no está disponible, todos los cues VIDEO pasan a ERROR
//   con mensaje informativo y el resto del programa sigue operativo.
// =============================================================================

using WinCue.Core;
using WinCue.Video.Containers;
using WinCue.Video.Hap;
using WinCue.Video.ProRes;

namespace WinCue.Video;

public sealed class VideoEngine : IVideoEngine, IDisposable
{
    private const string MpvNotFoundMsg =
        "libmpv no encontrado. Coloca libmpv-2.dll junto al ejecutable o define " +
        "WINCUE_LIBMPV. El resto del sistema (AUDIO, WAIT, SCRIPT) sigue operativo.";

    /// <summary>Formatos de imagen (mpv los muestra como fotogramas fijos).</summary>
    public static readonly IReadOnlySet<string> ImageFormats =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

    /// <summary>Formatos de vídeo soportados + imágenes.</summary>
    public static readonly IReadOnlySet<string> SupportedFormats =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mov", ".avi", ".mkv", ".webm",
            ".prores", ".mxf", ".m4v", ".ts", ".mts",
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp",
        };

    private static readonly string[] VoFallbacks = { "gpu", "gpu-next", "null" };

    // ------------------------------------------------------------------
    // Eventos (entregados vía dispatcher — en WPF, el hilo de UI)
    // ------------------------------------------------------------------

    public event Action<int, MediaCueState>? StateChanged;
    public event Action<int, double>? LatencyReport;      // (cueId, ms)
    public event Action<int, string>? ErrorOccurred;
    public event Action<int>? FrameReady;                 // primer frame visible

    // ------------------------------------------------------------------
    // Estado interno (main thread)
    // ------------------------------------------------------------------

    private readonly Action<Action>? _dispatch;
    private Func<nint> _getWindowHandle;
    private Func<int, int, nint>? _getLayerHandle;        // superficie por (salida, pista)
    private readonly string? _forcedVo;                   // tests: "null"
    private readonly bool _nullAudio;                     // tests: ao=null

    private readonly Dictionary<int, MediaCueState> _states = new();
    private readonly Dictionary<int, string> _files = new();          // verificados
    private readonly Dictionary<int, MpvPlayer> _players = new();
    private readonly Dictionary<int, (int Screen, int Layer)> _playerSlots = new();

    // Compositor GPU: provider de superficies de la ventana de salida
    // (screen, layer, slotSeq) → control persistente con D3DImage
    private Func<int, int, int, IVideoLayerSurface>? _gpuSurfaces;
    private bool _gpuFailed;   // el driver no pudo → no reintentar cada GO

    /// <summary>POOL de pipelines GPU persistentes (auditoría 05): crear y
    /// destruir el pipeline por cue fugaba la VRAM del ciclo entero (interop
    /// + dirty-rects de D3DImage). Cada slot vive toda la sesión; por cue
    /// solo se hace Attach/Detach del mpv_render_context.</summary>
    private sealed class GpuSlot
    {
        public required MpvGpuRenderer Pipeline;
        public required List<IVideoLayerSurface> Sinks;
        public required int Screen;   // -1 = canvas (el cue vive en todas)
        public required int Layer;
        public required string Spec;  // firma tamaño+vistas (reutilización exacta)
        public int? BusyCueId;
        /// <summary>CONGELADO: el cue murió dentro de un crossfade y su
        /// superficie sigue en pantalla con el último fotograma. El slot NO
        /// está libre (repartirlo pisaría ese fotograma).</summary>
        public int? FrozenCueId;
    }
    private readonly List<GpuSlot> _gpuPool = new();
    private int _gpuSlotSeq;

    /// <summary>MOTOR HAP PROPIO (paso 3): players sin mpv de los cues
    /// enrutados con UseHapEngine. Conviven con _players en los diccionarios
    /// de estado; cada uno posee su slot del pool HAP mientras suena.</summary>
    private readonly Dictionary<int, HapCuePlayer> _hapPlayers = new();

    /// <summary>Pool de pipelines HAP persistentes por (salida, pista, spec)
    /// — misma regla que _gpuPool (auditoría 05): el pipeline (devices D3D +
    /// superficies + binding del D3DImage) vive toda la sesión; por cue solo
    /// se Assign/Release el sink y se crea/libera la sesión de texturas.</summary>
    private sealed class HapSlot
    {
        public required HapGpuPipeline Pipeline;
        public required IVideoLayerSurface Sink;
        public required int Screen;
        public required int Layer;
        public required string Spec;   // "WxH" de la superficie
        public int? BusyCueId;
        /// <summary>CONGELADO (misma semántica que en GpuSlot): la superficie
        /// conserva el último fotograma hasta que el cruce acabe.</summary>
        public int? FrozenCueId;
    }
    private readonly List<HapSlot> _hapPool = new();

    /// <summary>MOTOR ProRes PROPIO (paso 2 de ProRes): players sin mpv de los
    /// cues enrutados con UseProResEngine. Conviven con _players y _hapPlayers
    /// en los diccionarios de estado; cada uno posee su slot mientras suena.</summary>
    private readonly Dictionary<int, ProResCuePlayer> _proresPlayers = new();

    /// <summary>Pool de pipelines ProRes persistentes, misma regla que _hapPool
    /// (auditoría 05). CON UN AÑADIDO: el slot cachea también la SESIÓN (el ring
    /// de texturas de plano), y la spec de esas texturas depende del FORMATO DE
    /// PÍXEL además del tamaño — 3 planos con croma a media anchura en 4:2:2 de
    /// 10 bits frente a 4 planos completos en 4444 de 12. Reutilizarla cuando
    /// coincide evita crear y destruir hasta 190 MB de texturas por cue (un
    /// frame 4K 4444 son 63 MB × 3 slots del ring); cuando NO coincide se
    /// reconstruye, que es lo único correcto.</summary>
    private sealed class ProResSlot
    {
        public required ProResGpuPipeline Pipeline;
        public required IVideoLayerSurface Sink;
        public required int Screen;
        public required int Layer;
        public required string Spec;   // "WxH" de la SUPERFICIE (clave del pool)
        public ProResVideoSession? Session;
        public string SessionSpec = "";   // "WxH|layout" del VÍDEO (clave de la sesión)
        public int? BusyCueId;
        /// <summary>CONGELADO (misma semántica que en GpuSlot y HapSlot).</summary>
        public int? FrozenCueId;
    }
    private readonly List<ProResSlot> _proresPool = new();

    /// <summary>Reloj inyectado por la App: elapsed del stream de AUDIO del
    /// cue (id de vídeo real), o null si no suena. Se consulta desde el hilo
    /// de presentación del player → debe ser thread-safe. Lo comparten las TRES
    /// rutas con audio espejo: motor HAP, motor ProRes y el PLL del híbrido.</summary>
    private Func<int, double?>? _hapClock;

    /// <summary>GO INSTANTÁNEO: player del cue en STANDBY ya creado y PAUSADO
    /// en su primer frame; el Play con parámetros equivalentes solo despausa.
    /// Su slot del pool queda BusyCueId=cueId (la promoción no lo toca).</summary>
    private sealed record StandbyPrep(int CueId, string Path, VideoPlayParams Params,
        MpvPlayer Player);
    private StandbyPrep? _standby;
    // Red de seguridad del crossfade en la misma pista: si el saliente no
    // tiene fin propio (loop), se termina al acabar el fundido
    private readonly Dictionary<int, System.Threading.Timer> _handoverTimers = new();

    /// <summary>CONGELAR EL ÚLTIMO FOTOGRAMA DEL SALIENTE. Si un vídeo llega a
    /// su fin natural mientras un crossfade sigue vivo, su player y su audio
    /// terminan como siempre, pero la superficie del pool NO se oculta: se
    /// queda con el último fotograma pintado hasta que el cruce acabe (antes
    /// desaparecía de golpe y la mezcla caía sobre NEGRO — luma medida 46 con
    /// las mesetas en 66/197). El registro garantiza la liberación por todos
    /// los caminos; ver VisualFreezeRegistry.</summary>
    private readonly VisualFreezeRegistry _freeze = new(Trace);
    /// <summary>Plazo (Environment.TickCount64) hasta el que cada saliente
    /// está en handover — lo fija el GO del entrante con fundido. Es la
    /// ÚNICA condición del congelado: si el saliente muere antes de ese
    /// plazo, su superficie se queda. Da igual QUIÉN lo mate (fin de archivo,
    /// fin de la duración declarada del cue o Stop del propio cue): en los
    /// tres casos hay un cruce vivo encima que necesita su imagen.</summary>
    private readonly Dictionary<int, long> _handoverDeadlines = new();
    /// <summary>Timer propio de cada congelado (no depende del de handover,
    /// que TerminatePlayer desmonta al morir el saliente).</summary>
    private readonly Dictionary<int, System.Threading.Timer> _thawTimers = new();
    private readonly Dictionary<int, bool> _playWhenReady = new();
    private readonly Dictionary<int, VideoPlayParams> _playParams = new();
    private readonly Dictionary<int, double> _volumes = new();
    private readonly Dictionary<int, AudioEqParams?> _eqs = new();
    private readonly Dictionary<int, System.Threading.Timer> _fadeTimers = new();

    /// <summary>PLL de la RUTA HÍBRIDA (ExternalAudio): un timer por cue que
    /// disciplina el time-pos de mpv (mudo, aid=no) al reloj del audio espejo
    /// (_hapClock). La lógica de decisión vive en MpvSyncPll (pura, testeable);
    /// aquí solo el tick: leer relojes → aplicar {speed, hard-seek}.</summary>
    private sealed class PllRunner : IDisposable
    {
        public readonly MpvSyncPll Pll = new();
        public System.Threading.Timer? Timer;
        public double Speed = 1.0;            // última speed escrita en mpv
        public double? CachedDuration;        // duración efectiva (con trims)
        public void Dispose() => Timer?.Dispose();
    }
    private readonly Dictionary<int, PllRunner> _syncPlls = new();
    private const int PllTickMs = 400;

    private double _master = 1.0;
    private bool _disposed;

    // ------------------------------------------------------------------
    // Construcción
    // ------------------------------------------------------------------

    /// <param name="getWindowHandle">Devuelve el HWND de destino en el momento
    /// de reproducir. Debe ser válido DESPUÉS de que la ventana se muestre.</param>
    /// <param name="dispatch">Remite callbacks al main thread (null → inline).</param>
    /// <param name="forcedVo">Solo tests: fuerza un vo concreto ("null").</param>
    /// <param name="nullAudio">Solo tests: ao=null (sin dispositivo de audio).</param>
    public VideoEngine(
        Func<nint> getWindowHandle,
        Action<Action>? dispatch = null,
        string? forcedVo = null,
        bool nullAudio = false)
    {
        _getWindowHandle = getWindowHandle;
        _dispatch = dispatch;
        _forcedVo = forcedVo;
        _nullAudio = nullAudio;
    }

    /// <summary>Cambia el destino de render (p.ej. ventana de salida en otro
    /// monitor). Solo afecta a los vídeos que se inicien a partir de ahora.</summary>
    public void SetWindowHandleProvider(Func<nint> getWindowHandle) =>
        _getWindowHandle = getWindowHandle;

    /// <summary>Proveedor de superficie POR (SALIDA, PISTA): cada vídeo
    /// renderiza en la de su (Screen, Layer); las pistas superiores tapan
    /// a las inferiores dentro de cada salida.</summary>
    public void SetLayerHandleProvider(Func<int, int, nint>? provider) =>
        _getLayerHandle = provider;

    /// <summary>Las superficies de PRESENTACIÓN de los pipelines propios (HAP y
    /// ProRes) nacen COMPARTIDAS, con handle que se pueda abrir desde otro
    /// device. POR DEFECTO false, y ese defecto es el contrato:
    ///
    ///   · WinCue no la pone NUNCA. Hospeda esa superficie en su propio device
    ///     D3D9Ex (D3DImage) y no necesita handle ninguno, así que su
    ///     asignación de recurso se queda EXACTAMENTE donde estaba —la llamada
    ///     a CreateRenderTarget sin <c>pSharedHandle</c>, byte a byte la de
    ///     siempre— y no hereda un modo de fallo nuevo: en un entorno donde el
    ///     render target compartido no se pudiera crear, el constructor
    ///     lanzaría y HAP/ProRes caerían a mpv en silencio. WinCue está en
    ///     producción y no cambia de comportamiento.
    ///   · Secuence SÍ la pone, al instalar su proveedor de superficies (ver
    ///     Secuence.App.App.xaml.cs): sus capas abren la superficie en D3D11
    ///     con OpenSharedResource y sin handle no hay puente.
    ///
    /// SE LEE AL CONSTRUIR el pipeline, así que solo afecta a los que se creen
    /// a partir de ahora — que es lo correcto: el pool se descarta al cambiar
    /// de modo (<see cref="DiscardCompositorPipelines"/>). Si el handle no se
    /// pidió y alguien lo reclama, <c>PresentSharedHandle</c> devuelve 0 y el
    /// consumidor falla CLARO (SharedSurfaceSource rechaza el handle 0 y la
    /// capa relanza), nunca en silencio.</summary>
    public bool SharePresentSurfaces { get; set; }

    /// <summary>Activa el COMPOSITOR GPU (render API → superficies D3D con
    /// opacidad real; una superficie POR CUE, entregada por (salida, pista,
    /// cueId)). null = volver al modo clásico HWND.</summary>
    public void SetGpuLayerSurfaceProvider(Func<int, int, int, IVideoLayerSurface>? provider)
    {
        // Cambio de modo del compositor: las superficies congeladas cuelgan de
        // las viejas — soltarlas ANTES de cambiar de proveedor (si no, el slot
        // quedaría reservado para siempre por un cue que ya no existe).
        ReleaseFrozenSurfaces();
        _gpuSurfaces = provider;
        _gpuFailed = false;
    }

    /// <summary>Tira los pipelines del compositor y desengancha sus
    /// superficies: el pool se queda VACÍO y el siguiente cue construye slot
    /// y superficie nuevos.
    ///
    /// QUIÉN DEBE LLAMARLO, Y POR QUÉ NO LO HACE
    /// <see cref="SetGpuLayerSurfaceProvider"/> POR SU CUENTA — no lo muevas
    /// ahí «para simplificar»:
    ///
    /// El pool guarda los sinks que el anfitrión entregó al crear cada
    /// pipeline; ese cableado (SetSurface / SetSharedHandle / SetFrameCopier)
    /// ocurre UNA vez, al crearlo. Si el anfitrión DESTRUYE esas superficies
    /// al apagar su compositor, los slots quedan apuntando a objetos muertos:
    /// al volver a encenderlo, AcquireGpuSlot reutiliza el slot viejo, no
    /// vuelve a pedir superficie, y el cue sale a una capa que ya no pinta —
    /// salida en NEGRO permanente, sin que _gpuFailed llegue a marcarse.
    ///
    /// Pero eso depende del PRODUCTO, y el motor no puede adivinarlo:
    ///   · WinCue: sus sinks son CompositorLayerControl, elementos WPF que
    ///     siguen vivos al cambiar de modo. Los slots viejos seguirían
    ///     funcionando, y tirar los pipelines aquí PARARÍA cues que están al
    ///     aire. WinCue está en producción y no cambia de comportamiento.
    ///   · Secuence: sus sinks son nodos de un grafo que muere con el
    ///     compositor de su salida. Ahí sí hay que llamar a esto, justo antes
    ///     (o después) de soltar los compositores.
    ///
    /// Quien sabe si sus superficies han muerto es el anfitrión, no el motor.
    /// Por eso es una llamada explícita y no un efecto secundario.
    ///
    /// ══ EL ESTADO NO SE TOCA, Y ES UNA DECISIÓN, NO UN OLVIDO ══
    ///
    /// Esto MATA los players, así que los cues de vídeo que estuvieran al aire
    /// se quedan marcados como PLAYING SIN PLAYER DETRÁS hasta que el operador
    /// les dé Stop a mano. Sí: la lista de cues dice que algo suena y no suena
    /// nada. Es feo y es sabido.
    ///
    /// LA ALTERNATIVA OBVIA ES PEOR, Y ESTÁ MEDIDA. Publicar
    /// <c>Stopped</c> aquí (o llamar a <see cref="Stop"/>) le entrega al
    /// anfitrión EXACTAMENTE la misma señal que un fin de reproducción, y el
    /// controlador hace con ella lo que hace siempre: <c>OnVideoStateChanged</c>
    /// → <c>OnActionEnd</c> → **encadenar el AUTO_FOLLOW**. O sea que desmarcar
    /// una casilla de Ajustes → Vídeo DISPARARÍA EL SIGUIENTE CUE. En un teatro
    /// eso es sonido y vídeo saliendo a sala sin que nadie haya dado el GO, y no
    /// se puede poder. Una etiqueta equivocada confunde al operador; un GO no
    /// pedido lo deja en evidencia delante del público.
    ///
    /// ASÍ QUE NO LO «ARREGLES» POR AHÍ. Si algún día molesta de verdad, lo que
    /// hace falta es un camino que el CONTROLADOR trate como PARADA MANUAL —lo
    /// que él mismo hace en EndCue, que saca el cue de <c>_running</c> ANTES de
    /// llamar al motor, y por eso una parada del operador no encadena—, no una
    /// señal más del motor. Eso ya no cabe aquí dentro: el motor no conoce
    /// <c>_running</c> ni debe conocerlo.
    ///
    /// ══ EL FUNDIDO EN MARCHA TAMPOCO SE DEJA VIVO ══
    ///
    /// Que el ESTADO no se toque no significa que todo lo demás pueda seguir
    /// corriendo. Un <c>FadeVolume(..., stopAfter: true)</c> en curso es un
    /// <see cref="System.Threading.Timer"/> que, al llegar a su fin, llama él
    /// solo a <see cref="Stop"/> — y Stop sí publica <c>Stopped</c>. Si ese
    /// fundido sobrevive a este método, la puerta de atrás que se acaba de
    /// describir arriba sigue abierta: el compositor se apaga limpio, pero
    /// unos segundos después el propio fundido dispara el AUTO_FOLLOW por su
    /// cuenta. Por eso este método cancela el fundido de cada cue (mismo
    /// <c>CancelFade</c> que usan <see cref="Stop"/> y <see cref="Cleanup"/>,
    /// que tampoco lo dejan vivo) justo antes de terminar su player: cancelar
    /// no publica nada, así que el estado sigue intacto tal y como se explica
    /// arriba.</summary>
    public void DiscardCompositorPipelines()
    {
        // Los players PRIMERO: los motores propios tienen hilos vivos usando
        // el device D3D de su slot, y destruir el pipeline con esos hilos
        // dentro es un acceso a recursos liberados (misma regla que Cleanup).
        //
        // TerminatePlayer y NO Stop(): Stop publica el estado, y publicar el
        // estado encadena el AUTO_FOLLOW del cue. Ver la cabecera — esto se
        // probó, se midió la consecuencia y se revirtió a propósito.
        //
        // CancelFade ANTES de TerminatePlayer, mismo orden que Stop(): un
        // FadeVolume(..., stopAfter: true) en marcha llama a Stop(cueId) al
        // llegar a su fin, y Stop SÍ publica Stopped — con eso bastaría para
        // encadenar el AUTO_FOLLOW aunque el player ya no exista. No es un
        // efecto secundario nuevo: es la misma fuga de estado que el resto de
        // este método existe para evitar, solo que con retardo.
        foreach (int cueId in _players.Keys.Concat(_hapPlayers.Keys)
                     .Concat(_proresPlayers.Keys).ToList())
        {
            CancelFade(cueId);
            TerminatePlayer(cueId);
        }
        DiscardGpuPools();
    }

    /// <summary>RELOJ MAESTRO del motor HAP: la App inyecta un provider que
    /// devuelve el elapsed (s desde su trim) del stream de audio espejo del
    /// cue, o null si no suena. OJO hilos: se llama desde el hilo de
    /// presentación del player — el provider debe ser thread-safe.</summary>
    public void SetHapClockProvider(Func<int, double?>? provider) =>
        _hapClock = provider;

    public static bool MpvAvailable => MpvPlayer.IsAvailable;

    public static List<string> ListSupportedFormats() =>
        SupportedFormats.OrderBy(x => x, StringComparer.Ordinal).ToList();

    // ------------------------------------------------------------------
    // API pública — carga / reproducción (main thread)
    // ------------------------------------------------------------------

    /// <summary>Traza de diagnóstico del ciclo de vida GPU (attach/detach/
    /// release/play). Solo escribe si WINCUE_MPV_LOG está definido — mismo
    /// interruptor que los logs de mpv, para correlacionar por timestamp.</summary>
    internal static void Trace(string msg)
    {
        string? dir = Environment.GetEnvironmentVariable("WINCUE_MPV_LOG");
        if (string.IsNullOrEmpty(dir))
            return;
        try
        {
            File.AppendAllText(Path.Combine(dir, "engine-trace.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n");
        }
        catch (Exception) { }
    }

    /// <summary>Inicia verificación asíncrona del archivo (→ LOADING).</summary>
    public void Load(int cueId, string filePath)
    {
        if (!MpvAvailable) { EmitError(cueId, MpvNotFoundMsg); return; }
        if (string.IsNullOrEmpty(filePath)) { EmitError(cueId, "Ruta de archivo vacía."); return; }

        var state = GetState(cueId);
        if (state is MediaCueState.Loading or MediaCueState.Loaded or MediaCueState.Playing)
            return;

        SetState(cueId, MediaCueState.Loading);
        _playWhenReady[cueId] = false;
        LaunchVerifier(cueId, filePath);
    }

    /// <summary>Pre-verifica el siguiente cue mientras el actual reproduce.</summary>
    public void PreloadNext(int cueId, string filePath) => Load(cueId, filePath);

    public MediaCueState GetState(int cueId) =>
        _states.TryGetValue(cueId, out var s) ? s : MediaCueState.Idle;

    /// <summary>Reproduce el cue. Autosuficiente: verifica el archivo si hace falta.
    /// Árbol de estados idéntico al Python:
    ///   PLAYING → no-op · LOADING → playWhenReady · LOADED+caché → directo ·
    ///   resto → re-verificación y play al terminar.</summary>
    /// <summary>Clamps defensivos (idénticos en Play y PrepareStandby: la
    /// equivalencia de parámetros del GO instantáneo depende de ello).</summary>
    private static VideoPlayParams NormalizeParams(VideoPlayParams p) => p with
    {
        Volume = Math.Clamp(p.Volume, 0.0, 1.0),
        Scale = Math.Max(1.0, p.Scale),
        TrimStart = Math.Max(0.0, p.TrimStart),
        TrimEnd = Math.Max(0.0, p.TrimEnd),
        Rate = Math.Max(0.1, p.Rate),
        PlayCount = Math.Max(1, p.PlayCount),
    };

    /// <summary>Igualdad de parámetros con CanvasOutputs por CONTENIDO (el
    /// record compara la lista por referencia).</summary>
    private static bool ParamsEquivalent(VideoPlayParams a, VideoPlayParams b)
    {
        if ((a with { CanvasOutputs = null }) != (b with { CanvasOutputs = null }))
            return false;
        var ca = a.CanvasOutputs;
        var cb = b.CanvasOutputs;
        if ((ca?.Count ?? 0) != (cb?.Count ?? 0))
            return false;
        if (ca is not null && cb is not null)
            for (int i = 0; i < ca.Count; i++)
                if (ca[i] != cb[i])
                    return false;
        return true;
    }

    public void Play(int cueId, string filePath, VideoPlayParams? p = null)
    {
        p ??= new VideoPlayParams();
        _playParams[cueId] = NormalizeParams(p);

        if (!MpvAvailable) { EmitError(cueId, MpvNotFoundMsg); return; }
        if (string.IsNullOrEmpty(filePath))
        {
            EmitError(cueId, "No hay archivo de vídeo asignado a este cue.");
            return;
        }

        switch (GetState(cueId))
        {
            case MediaCueState.Playing:
                return;

            case MediaCueState.Loading:
                _playWhenReady[cueId] = true;
                return;

            case MediaCueState.Loaded when _files.ContainsKey(cueId):
                StartPlayback(cueId);
                return;
        }

        // IDLE / STOPPED / ERROR / LOADED-sin-caché → re-verificar
        SetState(cueId, MediaCueState.Loading);
        _playWhenReady[cueId] = true;
        LaunchVerifier(cueId, filePath);
    }

    /// <summary>Detiene la reproducción del cue. TERMINA el player mpv (no solo
    /// stop): garantiza salida limpia y un único vídeo por destino.
    /// El estado se publica ANTES de destruir el player (el Dispose puede
    /// tardar ~100 ms y dejaría una ventana IsPlaying=false/estado=Playing).</summary>
    public void Stop(int cueId)
    {
        CancelFade(cueId);
        if (_players.ContainsKey(cueId) || GetState(cueId) != MediaCueState.Stopped)
            SetState(cueId, MediaCueState.Stopped);
        TerminatePlayer(cueId);
    }

    public void StopAll()
    {
        foreach (int cueId in _players.Keys.Concat(_hapPlayers.Keys)
                     .Concat(_proresPlayers.Keys).ToList())
            if (GetState(cueId) == MediaCueState.Playing)
                Stop(cueId);
        // STOP ALL / PANIC / corte duro: no queda cruce que servir — ninguna
        // superficie congelada debe sobrevivir a un barrido general.
        ReleaseFrozenSurfaces();
    }

    /// <summary>Libera las superficies CONGELADAS (último fotograma del
    /// saliente de un crossfade): la del cue dado, o TODAS con -1. Es el
    /// camino explícito de STOP ALL, PANIC, fin/recarga de show y cambio de
    /// modo del compositor — sin él una liberación diferida podría no llegar
    /// nunca y dejar el pool sin superficies (auditoría 05).</summary>
    public void ReleaseFrozenSurfaces(int cueId = -1)
    {
        if (cueId >= 0)
        {
            _freeze.Release(cueId);
            return;
        }
        _freeze.ReleaseAll();
    }

    /// <summary>Congelados vivos (diagnóstico; los tests lo usan para probar
    /// que ningún camino de cierre deja slots reservados).</summary>
    public int FrozenSurfaceCount => _freeze.Count;

    /// <summary>Cues que reproduce ahora mismo el MOTOR HAP propio.</summary>
    public int HapEnginePlayerCount => _hapPlayers.Count;

    /// <summary>Cues que reproduce ahora mismo el MOTOR ProRes propio. Es la
    /// única forma de comprobar DESDE FUERA si un cue entró de verdad por el
    /// motor propio o si cayó a mpv — lo usan los tests del fallback y el
    /// diagnóstico del soak.</summary>
    public int ProResEnginePlayerCount => _proresPlayers.Count;

    public bool IsPlaying(int cueId) =>
        GetState(cueId) == MediaCueState.Playing &&
        (_players.ContainsKey(cueId) || _hapPlayers.ContainsKey(cueId)
         || _proresPlayers.ContainsKey(cueId));

    public bool AnyPlaying => _states.Values.Any(s => s == MediaCueState.Playing);

    /// <summary>Duración EFECTIVA en s (con el trim aplicado; 0 si mpv aún no
    /// la conoce). Coherente con GetElapsed: ambos son relativos al recorte.</summary>
    public double GetDuration(int cueId)
    {
        // Motores propios: la duración efectiva (trim aplicado) sale del
        // demuxer, no de mpv — se conoce desde el primer instante del cue
        if (_hapPlayers.TryGetValue(cueId, out var hap))
            return hap.GetDuration();
        if (_proresPlayers.TryGetValue(cueId, out var pr))
            return pr.GetDuration();
        if (!_players.TryGetValue(cueId, out var pl))
            return 0.0;
        double d = pl.GetPropertyDouble("duration") ?? 0.0;
        var p = _playParams.GetValueOrDefault(cueId);
        if (p is null)
            return d;
        double end = p.TrimEnd > 0
            ? (d > 0 ? Math.Min(p.TrimEnd, d) : p.TrimEnd)
            : d;
        return Math.Max(0.0, end - p.TrimStart);
    }

    /// <summary>Segundos reproducidos DESDE el punto de entrada (time-pos de
    /// mpv es absoluto en el archivo; con trim_start se resta el offset —
    /// mismas semánticas que el motor de audio).</summary>
    public double GetElapsed(int cueId)
    {
        // Motor HAP: el player cuenta desde su punto de entrada (trim) y por
        // pasada — misma semántica que mpv con loop-file
        if (_hapPlayers.TryGetValue(cueId, out var hap))
            return hap.GetElapsed();
        if (_proresPlayers.TryGetValue(cueId, out var pr))
            return pr.GetElapsed();
        if (!_players.TryGetValue(cueId, out var pl))
            return 0.0;
        var p = _playParams.GetValueOrDefault(cueId);
        // Reloj CONTINUO: time-pos observada (push, no bloquea el core) +
        // extrapolación de pared entre pushes — mpv publica a ráfagas y la
        // consulta síncrona se atascaba con el core saturado (p. ej. HAP SW).
        double t = pl.ObservedTimePosSmooth(p?.Rate ?? 1.0)
            ?? pl.GetPropertyDouble("time-pos") ?? 0.0;
        return Math.Max(0.0, t - (p?.TrimStart ?? 0.0));
    }

    // ------------------------------------------------------------------
    // Volumen / fade / pausa — control estilo QLab
    // ------------------------------------------------------------------

    /// <summary>Volumen master global (0..1); reajusta todos los players activos.</summary>
    public void SetMasterVolume(double volume)
    {
        _master = Math.Clamp(volume, 0.0, 1.0);
        foreach (var (cueId, player) in _players)
            TrySetVolume(player, _volumes.GetValueOrDefault(cueId, 1.0));
    }

    /// <summary>Fija el volumen (0..1) de un vídeo en reproducción.</summary>
    public void SetVolume(int cueId, double volume)
    {
        double v = Math.Clamp(volume, 0.0, 1.0);
        _volumes[cueId] = v;
        if (_players.TryGetValue(cueId, out var player))
            TrySetVolume(player, v);
    }

    /// <summary>EQ/compresor del cue vía la cadena af de mpv (paridad con el
    /// motor de audio). Se guarda por cue: aplica al player actual EN VIVO y
    /// a los futuros (arranque normal o standby promovido).</summary>
    public void SetEq(int cueId, AudioEqParams? eq)
    {
        _eqs[cueId] = eq;
        if (_players.TryGetValue(cueId, out var player))
            TryApplyEq(player, eq);
    }

    private static void TryApplyEq(MpvPlayer player, AudioEqParams? eq)
    {
        try { player.SetProperty("af", MpvAfChain.Build(eq)); }
        catch (Exception) { /* sin pista de audio / propiedad no crítica */ }
    }

    private void TrySetVolume(MpvPlayer player, double v)
    {
        try { player.SetProperty("volume", Math.Clamp(v * 100.0, 0.0, 100.0) * _master); }
        catch (Exception) { }
    }

    /// <summary>Funde el volumen hacia target (0..1) en duration s.
    /// Devuelve true si el cue estaba en reproducción.</summary>
    public bool FadeVolume(int cueId, double target, double duration, bool stopAfter = false)
    {
        // Motor HAP: el vídeo no lleva audio (va aparte, por el AudioEngine
        // espejo del controlador) — aquí solo se honra el stopAfter; el
        // fundido audible/visual lo hacen el audio espejo y la opacidad.
        if (_hapPlayers.ContainsKey(cueId) || _proresPlayers.ContainsKey(cueId))
        {
            CancelFade(cueId);
            if (duration <= 0.0)
            {
                if (stopAfter)
                    Stop(cueId);
                return true;
            }
            if (stopAfter)
            {
                _fadeTimers[cueId] = new System.Threading.Timer(_ => Post(() =>
                {
                    CancelFade(cueId);
                    Stop(cueId);
                }), null, (int)(duration * 1000), System.Threading.Timeout.Infinite);
            }
            return true;
        }

        if (!_players.ContainsKey(cueId))
            return false;

        CancelFade(cueId);
        target = Math.Clamp(target, 0.0, 1.0);
        double start = _volumes.GetValueOrDefault(cueId, 1.0);

        if (duration <= 0.0)
        {
            SetVolume(cueId, target);
            if (stopAfter) Stop(cueId);
            return true;
        }

        const int intervalMs = 30;
        int steps = Math.Max(1, (int)(duration * 1000 / intervalMs));
        int i = 0;

        var timer = new System.Threading.Timer(_ =>
        {
            int step = Interlocked.Increment(ref i);
            double frac = Math.Min(1.0, (double)step / steps);
            double vol = start + (target - start) * frac;
            // mpv es thread-safe: se puede fijar la propiedad desde el timer;
            // el resto de la gestión se remite al main thread al terminar.
            _volumes[cueId] = vol;
            if (_players.TryGetValue(cueId, out var pl))
                TrySetVolume(pl, vol);
            if (step >= steps)
            {
                Post(() =>
                {
                    CancelFade(cueId);
                    if (stopAfter) Stop(cueId);
                });
            }
        }, null, intervalMs, intervalMs);

        _fadeTimers[cueId] = timer;
        return true;
    }

    private void CancelFade(int cueId)
    {
        if (_fadeTimers.Remove(cueId, out var timer))
            timer.Dispose();
    }

    /// <summary>Pausa todos los vídeos en reproducción.</summary>
    public void PauseAll()
    {
        foreach (var player in _players.Values)
            try { player.SetProperty("pause", true); } catch (Exception) { }
        foreach (var hap in _hapPlayers.Values)
            hap.SetPaused(true);
        foreach (var pr in _proresPlayers.Values)
            pr.SetPaused(true);
    }

    /// <summary>Reanuda todos los vídeos pausados.</summary>
    public void ResumeAll()
    {
        foreach (var (cueId, player) in _players)
            if (GetState(cueId) == MediaCueState.Playing)
                try { player.SetProperty("pause", false); } catch (Exception) { }
        foreach (var (cueId, hap) in _hapPlayers)
            if (GetState(cueId) == MediaCueState.Playing)
                hap.SetPaused(false);
        foreach (var (cueId, pr) in _proresPlayers)
            if (GetState(cueId) == MediaCueState.Playing)
                pr.SetPaused(false);
    }

    /// <summary>Pausa SOLO este vídeo (congela reloj y frame).</summary>
    public void PauseCue(int cueId)
    {
        if (_players.TryGetValue(cueId, out var player))
            try { player.SetProperty("pause", true); } catch (Exception) { }
        if (_hapPlayers.TryGetValue(cueId, out var hap))
            hap.SetPaused(true);
        if (_proresPlayers.TryGetValue(cueId, out var pr))
            pr.SetPaused(true);
    }

    public void ResumeCue(int cueId)
    {
        if (_players.TryGetValue(cueId, out var player))
            try { player.SetProperty("pause", false); } catch (Exception) { }
        if (_hapPlayers.TryGetValue(cueId, out var hap))
            hap.SetPaused(false);
        if (_proresPlayers.TryGetValue(cueId, out var pr))
            pr.SetPaused(false);
    }

    /// <summary>Aplica la geometría a un vídeo EN REPRODUCCIÓN (en vivo).
    /// El modo FULL conserva la escala base del cue (no la pisa a 0).
    /// <paramref name="scalePercent"/> la ACTUALIZA (fader «Escala (%)» del
    /// inspector): se guarda en los parámetros del cue para que la escala
    /// nueva sobreviva a un reinicio del player o a la promoción del standby.
    /// null = dejarla como está.</summary>
    public void ApplyGeometry(int cueId, VideoGeometry geo, double? scalePercent = null)
    {
        if (scalePercent is double s && _playParams.GetValueOrDefault(cueId) is { } prev)
            _playParams[cueId] = prev with { Scale = s };
        var p = _playParams.GetValueOrDefault(cueId);
        if (_players.TryGetValue(cueId, out var player))
            ApplyGeo(player, geo, BaseZoom(p));
        if (_hapPlayers.TryGetValue(cueId, out var hap))
            hap.ApplyGeometry(geo, p?.Scale ?? scalePercent ?? 100.0);
        if (_proresPlayers.TryGetValue(cueId, out var pr))
            pr.ApplyGeometry(geo, p?.Scale ?? scalePercent ?? 100.0);
    }

    /// <summary>video-zoom (log2) equivalente al parámetro Scale del cue.</summary>
    private static double BaseZoom(VideoPlayParams? p) =>
        p is null || Math.Abs(p.Scale - 100.0) < 0.01
            ? 0.0
            : Math.Log2(Math.Max(1.0, p.Scale) / 100.0);

    /// <summary>Termina todos los players y fades (al salir de la aplicación).
    /// Es el ÚNICO sitio donde los pipelines del pool se desmontan de verdad
    /// (el proceso muere justo después: la fuga del teardown ya no importa).</summary>
    public void Cleanup()
    {
        DiscardStandby();
        // Congelados primero: sueltan sus sinks mientras los pipelines viven
        ReleaseFrozenSurfaces();
        foreach (int cueId in _handoverTimers.Keys.ToList())
            if (_handoverTimers.Remove(cueId, out var h))
                h.Dispose();
        _handoverDeadlines.Clear();
        foreach (int cueId in _syncPlls.Keys.ToList())
            StopSyncPll(cueId);
        foreach (int cueId in _fadeTimers.Keys.ToList())
            CancelFade(cueId);
        // LOS TRES TIPOS de player, no solo los de mpv: los motores propios
        // tienen HILOS VIVOS (decode y presentación) que están usando el device
        // D3D de su slot. Destruir el pipeline con esos hilos dentro es un
        // acceso a recursos liberados — con ProRes, además, dentro de código
        // NATIVO. Antes solo se barría _players y los motores propios se
        // quedaban corriendo hasta que el proceso los arrastraba.
        foreach (int cueId in _players.Keys.Concat(_hapPlayers.Keys)
                     .Concat(_proresPlayers.Keys).ToList())
        {
            TerminatePlayer(cueId);
        }
        DiscardGpuPools();
    }

    /// <summary>Desmonta los TRES pools del compositor: pipeline liberado y
    /// sink desenganchado. Lo usan Cleanup() (salida de la aplicación) y
    /// DiscardCompositorPipelines() (petición EXPLÍCITA del anfitrión).
    ///
    /// NO lo llama SetGpuLayerSurfaceProvider, y no debe llamarlo: ver el
    /// porqué en DiscardCompositorPipelines — hacerlo pararía cues al aire en
    /// WinCue, que está en producción y no cambia de comportamiento.
    ///
    /// LLAMAR CON LOS PLAYERS YA PARADOS: los motores propios tienen hilos
    /// vivos usando el device D3D de su slot.</summary>
    private void DiscardGpuPools()
    {
        foreach (var slot in _gpuPool.ToList())
        {
            DetachSinks(slot.Pipeline, slot.Sinks);
            slot.Pipeline.Dispose();
        }
        _gpuPool.Clear();
        foreach (var slot in _hapPool.ToList())
        {
            slot.Pipeline.FrameRendered -= slot.Sink.InvalidateFrame;
            slot.Sink.ClearSurface();
            slot.Pipeline.Dispose();
        }
        _hapPool.Clear();
        foreach (var slot in _proresPool.ToList())
        {
            slot.Pipeline.FrameRendered -= slot.Sink.InvalidateFrame;
            slot.Sink.ClearSurface();
            // La sesión (ring de texturas de plano) muere ANTES que su device
            slot.Session?.Dispose();
            slot.Pipeline.Dispose();
        }
        _proresPool.Clear();
    }

    /// <summary>El copiador interop→presentación que le toca a un sink: CON
    /// fence SOLO si va a abrir la superficie en OTRO device.
    ///
    /// Es la misma decisión en los TRES pools (mpv, HAP, ProRes) y vive aquí
    /// porque es una REGLA, no un detalle de cada uno:
    ///
    ///   · Secuence (<c>NeedsCrossDeviceFence</c> = true) abre la superficie
    ///     compartida D3D9Ex desde D3D11, y ahí no hay sincronización implícita
    ///     entre colas: sin la barrera se lee negro de forma no determinista
    ///     (demostrado en la Tarea 4, quitando el fence la prueba por píxel
    ///     vuelve a rojo).
    ///   · WinCue NO lo implementa —el miembro es una implementación por
    ///     defecto del interfaz, false— porque hospeda esa misma superficie en
    ///     SU device D3D9Ex. Se queda con <c>CopyPresent</c> a secas y no paga
    ///     un bloqueo de GPU por fotograma y por capa. Es un producto que se
    ///     está vendiendo: meterle ese bloqueo sería una regresión de
    ///     rendimiento invisible en el código y visible en sala, y por eso hay
    ///     una prueba (SIN GPU) que afirma justo esto.</summary>
    internal static Action FrameCopierFor(IVideoLayerSurface sink,
        Action sinFence, Action conFence)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return sink.NeedsCrossDeviceFence ? conFence : sinFence;
    }

    /// <summary>Desengancha los sinks de un pipeline mpv: PRIMERO la
    /// suscripción a FrameRendered y después ClearSurface.
    ///
    /// El orden importa y la suscripción también: un pipeline vivo con
    /// `FrameRendered += sink.InvalidateFrame` MANTIENE VIVO al sink (el
    /// evento guarda la referencia al objetivo del delegado). Sin este `-=`,
    /// cada superficie descartada seguía colgando del pipeline — la misma
    /// forma de fuga que la auditoría 05, y aquí las fugas ya salieron
    /// caras una vez.</summary>
    private static void DetachSinks(MpvGpuRenderer pipeline,
        IReadOnlyList<IVideoLayerSurface> sinks)
    {
        foreach (var s in sinks)
            DetachSink(pipeline, s);
    }

    /// <summary>UN sink: el `-=` y después ClearSurface (ver DetachSinks para
    /// el porqué del orden). Extraído para poder desmontar sink a sink en los
    /// caminos de error, donde uno que lance no puede llevarse por delante a
    /// los demás.</summary>
    private static void DetachSink(MpvGpuRenderer pipeline, IVideoLayerSurface sink)
    {
        pipeline.FrameRendered -= sink.InvalidateFrame;
        sink.ClearSurface();
    }

    /// <summary>Un paso de desmontaje dentro de un `catch` que va a RELANZAR,
    /// blindado para que su propio fallo no se escape.
    ///
    /// Sin esto, cualquier excepción del desmontaje haría dos daños a la vez:
    ///   · SUSTITUIRÍA a la excepción original — y la original es justo la que
    ///     hace que el llamante marque _gpuFailed, avise y saque el cue por la
    ///     ruta clásica; el llamante vería en su lugar un fallo de limpieza que
    ///     no sabe interpretar;
    ///   · ABORTARÍA los pasos siguientes, entre ellos el Dispose del pipeline,
    ///     que para entonces ya no está en el pool y del que nadie más guarda
    ///     referencia: fuga total del device D3D9Ex, el FBO, las superficies
    ///     compartidas y el interop GL — el MISMO modo de fallo que estos catch
    ///     vinieron a eliminar.
    ///
    /// Y no es teórico: ClearSurface, en Secuence, toma el lock del grafo del
    /// compositor y libera recursos D3D11. Queda traza (WINCUE_MPV_LOG) para
    /// que un desmontaje fallido no sea invisible.</summary>
    private static void TeardownStep(string what, Action step)
    {
        try
        {
            step();
        }
        catch (Exception exc)
        {
            Trace($"desmontaje tras fallo: {what} lanzó " +
                $"({exc.GetType().Name}: {exc.Message})");
        }
    }

    /// <summary>Desmonta un slot mpv en un camino de ERROR y GARANTIZA que el
    /// pipeline queda liberado, pase lo que pase con los sinks. Sink a sink a
    /// propósito: si el primero lanza, los demás tienen que desengancharse
    /// IGUAL — mientras conserven la suscripción a FrameRendered, el pipeline
    /// los mantiene vivos a todos.</summary>
    private static void TearDownFailedGpuSlot(MpvGpuRenderer pipeline,
        IReadOnlyList<IVideoLayerSurface> sinks)
    {
        foreach (var s in sinks)
            TeardownStep("desenganchar sink mpv", () => DetachSink(pipeline, s));
        TeardownStep("liberar pipeline mpv", pipeline.Dispose);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup();
    }

    // ------------------------------------------------------------------
    // Verificación asíncrona de archivo (equivale a _FileVerifierWorker)
    // ------------------------------------------------------------------

    private void LaunchVerifier(int cueId, string filePath)
    {
        Task.Run(() =>
        {
            try
            {
                if (!File.Exists(filePath))
                    throw new FileNotFoundException($"Archivo no encontrado: {filePath}");

                string ext = Path.GetExtension(filePath);
                if (!SupportedFormats.Contains(ext))
                    throw new NotSupportedException(
                        $"Formato '{ext}' no soportado. " +
                        $"Soportados: {string.Join(", ", ListSupportedFormats())}");

                // Archivo en uso por otro proceso (caso 4 del spec Python)
                try
                {
                    using var fh = File.OpenRead(filePath);
                    var probe = new byte[16];
                    _ = fh.Read(probe, 0, probe.Length);
                }
                catch (UnauthorizedAccessException)
                {
                    throw new IOException($"Archivo en uso por otro proceso: {filePath}");
                }

                Post(() => OnFileVerified(cueId, filePath));
            }
            catch (Exception exc)
            {
                Post(() => OnFileFailed(cueId, exc.Message));
            }
        });
    }

    private void OnFileVerified(int cueId, string filePath)
    {
        _files[cueId] = filePath;
        SetState(cueId, MediaCueState.Loaded);

        bool willPlay = _playWhenReady.Remove(cueId, out bool w) && w;
        if (willPlay)
            StartPlayback(cueId);
    }

    private void OnFileFailed(int cueId, string message)
    {
        _playWhenReady.Remove(cueId);
        EmitError(cueId, $"Error al verificar vídeo: {message}");
    }

    // ------------------------------------------------------------------
    // Reproducción — creación de MPV e inicio (main thread)
    // ------------------------------------------------------------------

    private void StartPlayback(int cueId)
    {
        if (!_files.TryGetValue(cueId, out string? filePath))
        {
            EmitError(cueId, "Archivo no está en caché. Llama a Load() primero.");
            return;
        }

        // El cue vuelve a salir: si tenía un fotograma congelado de un cruce
        // anterior, ya no vale de nada (y su slot debe volver al pool)
        ReleaseFrozenSurfaces(cueId);

        var p = _playParams.GetValueOrDefault(cueId, new VideoPlayParams());
        int layer = Math.Clamp(p.Layer, 0, 7);
        bool gpu = _gpuSurfaces is not null && !_gpuFailed && _forcedVo is null
            && !p.ForceClassicPath;
        Trace($"StartPlayback cue={cueId} gpu={gpu} standby={_standby?.CueId.ToString() ?? "none"}");
        // MODO CANVAS: solo con compositor GPU (varias vistas de un render).
        // Sin compositor, el cue cae al modo clásico sobre su salida Screen.
        bool canvas = gpu && p.CanvasOutputs is { Count: > 0 };
        // En canvas el cue ocupa su pista en TODAS las salidas → la clave de
        // exclusividad usa la salida virtual -1 (global).
        int screen = canvas ? -1 : Math.Clamp(p.Screen, 0, 3);

        // ── GO INSTANTÁNEO: promover el standby preparado (player en pausa
        // sobre su primer frame) — solo hay que quitar la pausa ─────────
        if (gpu && _standby is { } sb && sb.CueId == cueId && sb.Path == filePath
            && ParamsEquivalent(sb.Params, p))
        {
            _standby = null;
            TerminateSlotOccupants(cueId, screen, layer,
                gpu && p.HandoverSeconds > 0.0, p.HandoverSeconds);
            // (Sin TerminatePlayer defensivo aquí: con standby activo no puede
            // existir instancia previa del cue — PrepareStandby lo impide — y
            // soltaría el slot del pool que el propio standby tiene ocupado.)

            _players[cueId] = sb.Player;
            _playerSlots[cueId] = (screen, layer);
            _volumes[cueId] = p.Volume;
            TryApplyEq(sb.Player, _eqs.GetValueOrDefault(cueId));
            long tGo = System.Diagnostics.Stopwatch.GetTimestamp();
            try { sb.Player.SetProperty("pause", false); }
            catch (Exception exc)
            {
                TerminatePlayer(cueId);
                EmitError(cueId, $"GO instantáneo falló al despausar: {exc.Message}");
                return;
            }
            SetState(cueId, MediaCueState.Playing);
            if (p.ExternalAudio)
                StartSyncPll(cueId, sb.Player, p);
            Post(() =>
            {
                LatencyReport?.Invoke(cueId,
                    System.Diagnostics.Stopwatch.GetElapsedTime(tGo).TotalMilliseconds);
                FrameReady?.Invoke(cueId);
            });
            return;
        }
        // Standby de este cue que NO se puede promover (parámetros/modo
        // cambiaron): descartarlo y seguir por el camino normal
        if (_standby?.CueId == cueId)
            DiscardStandby(cueId);

        // ── MOTOR HAP PROPIO (paso 3): .mov HAP con compositor activo ────
        if (p.UseHapEngine)
        {
            if (gpu && !canvas && StartHapPlayback(cueId, filePath, p, screen, layer))
                return;
            // Fallback de emergencia: mpv clásico MUDO — el audio del cue
            // ya suena por el AudioEngine (espejo del controlador); con
            // audio aquí sonaría doble. ExternalAudio=true: sin pista de
            // audio (aid=no) y con el PLL híbrido disciplinando el vídeo al
            // reloj del espejo — mejor sincronía que el reloj libre de antes.
            _playParams[cueId] = p with
            {
                UseHapEngine = false,
                ForceClassicPath = true,
                Volume = 0.0,
                CanvasOutputs = null,
                HandoverSeconds = 0.0,
                ExternalAudio = true,
            };
            StartPlayback(cueId);
            return;
        }

        // ── MOTOR ProRes PROPIO (paso 2): .mov ProRes con compositor ─────
        if (p.UseProResEngine)
        {
            if (gpu && !canvas && StartProResPlayback(cueId, filePath, p, screen, layer))
                return;
            // Fallback de emergencia, idéntico al de HAP: mpv clásico MUDO. El
            // audio del cue ya suena por el AudioEngine (espejo del
            // controlador), así que con audio aquí sonaría DOBLE.
            // ExternalAudio=true: sin pista de audio (aid=no) y con el PLL
            // híbrido disciplinando el vídeo al reloj del espejo.
            // OJO: ForceClassicPath NO se activa (a diferencia de HAP) — mpv SÍ
            // renderiza ProRes por el compositor, así que el cue conserva los
            // fundidos de opacidad aunque el motor propio no haya podido.
            _playParams[cueId] = p with
            {
                UseProResEngine = false,
                Volume = 0.0,
                ExternalAudio = true,
            };
            StartPlayback(cueId);
            return;
        }

        // ── Validación del HWND (modo clásico; el compositor no usa wid) ─
        nint hwnd = 0;
        if (_forcedVo is null && !gpu)
        {
            hwnd = _getLayerHandle?.Invoke(screen, layer) ?? _getWindowHandle();
            if (hwnd == 0)
            {
                EmitError(cueId,
                    "Handle de la superficie de vídeo no disponible. " +
                    "Asegúrate de que la ventana de salida es visible.");
                return;
            }
        }

        TerminateSlotOccupants(cueId, screen, layer,
            gpu && p.HandoverSeconds > 0.0, p.HandoverSeconds);
        TerminatePlayer(cueId);   // player anterior de este cue, si existía

        // ── Crear instancia MPV con cadena de fallback de vo ────────────
        bool isImage = ImageFormats.Contains(Path.GetExtension(filePath));

        MpvPlayer? player = null;
        string usedVo = "gpu";
        string[] voCandidates = gpu ? new[] { "libmpv" }
            : _forcedVo is not null ? new[] { _forcedVo } : VoFallbacks;
        foreach (string vo in voCandidates)
        {
            var options = new Dictionary<string, string>
            {
                ["vo"] = vo,
                ["hwdec"] = "auto",
                ["keep-open"] = "yes",
                ["hr-seek"] = "yes",
                ["keepaspect"] = "yes",
                ["osc"] = "no",
                ["input-default-bindings"] = "no",
                ["input-vo-keyboard"] = "no",
            };
            if (hwnd != 0) options["wid"] = hwnd.ToString();
            if (_nullAudio) options["ao"] = "null";
            // RUTA HÍBRIDA: mpv nace SIN pista de audio (el espejo del
            // AudioEngine es quien suena) — fijado ANTES del loadfile
            if (p.ExternalAudio) options["aid"] = "no";
            // Diagnóstico: WINCUE_MPV_LOG=<dir> vuelca el log verboso de mpv por
            // cue — imprescindible para cazar crashes nativos del render.
            if (Environment.GetEnvironmentVariable("WINCUE_MPV_LOG") is { Length: > 0 } logDir)
            {
                options["log-file"] = System.IO.Path.Combine(logDir, $"mpv-cue{cueId}.log");
                options["msg-level"] = "all=v";
            }
            // Opciones que deben estar ANTES de loadfile
            if (p.Loop) options["loop-file"] = "inf";
            else if (p.PlayCount > 1) options["loop-file"] = (p.PlayCount - 1).ToString();
            if (p.TrimStart > 0.0) options["start"] = p.TrimStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
            if (p.TrimEnd > 0.0) options["end"] = p.TrimEnd.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
            if (isImage) options["image-display-duration"] = "inf";

            try
            {
                // CA2000: la propiedad del player se transfiere a _players (o
                // se libera en cada ruta de error de más abajo) — falso positivo.
#pragma warning disable CA2000
                player = new MpvPlayer(options);
#pragma warning restore CA2000
                usedVo = vo;
                break;
            }
            catch (Exception)
            {
                player = null;
            }
        }

        if (player is null)
        {
            EmitError(cueId, "No se pudo inicializar ningún video output (gpu/gpu-next/null).");
            return;
        }

        if (usedVo != "gpu" && _forcedVo is null && !gpu)
            ErrorOccurred?.Invoke(cueId, $"[AVISO] vo=gpu no disponible. Usando vo={usedVo}.");

        // ── Compositor GPU: slot persistente del pool + attach de sesión ─
        if (gpu)
        {
            GpuSlot? slot = null;
            try
            {
                var geo = p.Geometry ?? new VideoGeometry();
                // En modo canvas el FBO de mpv ES el canvas y cada salida
                // recibe su recorte; en clásico, una única vista completa.
                var views = canvas ? p.CanvasOutputs : null;
                slot = AcquireGpuSlot(cueId, screen, layer,
                    (int)geo.SurfW, (int)geo.SurfH, views);
                slot.Pipeline.AttachPlayer(player);
                // Confirmación visible del modo (barra de estado): sin esto es
                // imposible saber desde fuera si el compositor está activo
                ErrorOccurred?.Invoke(cueId, canvas
                    ? $"[AVISO] Compositor GPU activo — canvas {slot.Pipeline.Width}×" +
                      $"{slot.Pipeline.Height}, {slot.Sinks.Count} salida(s), pista {layer + 1}."
                    : $"[AVISO] Compositor GPU activo — salida {screen + 1}, pista {layer + 1}.");
            }
            catch (Exception exc)
            {
                // Driver sin interop GL↔D3D (o similar): caer al modo clásico
                // de forma permanente en esta sesión y reintentar este cue.
                if (slot is not null)
                    ReleaseGpuSlot(cueId);
                player.Dispose();
                _gpuFailed = true;
                ErrorOccurred?.Invoke(cueId,
                    $"[AVISO] Compositor GPU no disponible ({exc.Message}). " +
                    "Usando modo clásico por HWND.");
                StartPlayback(cueId);
                return;
            }
        }

        // ── Eventos mpv (llegan en el hilo de eventos → remitir) ────────
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        bool latencyDone = false;

        player.PlaybackRestart += () =>
        {
            if (latencyDone) return;
            latencyDone = true;
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            Post(() =>
            {
                LatencyReport?.Invoke(cueId, ms);
                FrameReady?.Invoke(cueId);
            });
        };

        var thisPlayer = player;
        player.EndFile += () => Post(() =>
        {
            // Fin natural de archivo (no parada manual: esa retira el player
            // antes). OJO: verificar que el evento viene del player ACTUAL del
            // cue — el EndFile PÓSTUMO de una instancia anterior (p. ej. un
            // standby descartado, que emite EndFile al disponerse) llegaba
            // encolado y TERMINABA al player nuevo en pleno arranque ("No
            // render context set" en el compositor).
            if (GetState(cueId) == MediaCueState.Playing &&
                _players.TryGetValue(cueId, out var cur) &&
                ReferenceEquals(cur, thisPlayer))
            {
                SetState(cueId, MediaCueState.Stopped);   // publicar ANTES del Dispose
                TerminatePlayer(cueId);
            }
        });

        // ── Volumen, velocidad, escala y geometría ──────────────────────
        _volumes[cueId] = p.Volume;
        try
        {
            player.SetProperty("volume", Math.Clamp(p.Volume * 100.0, 0.0, 100.0) * _master);
            if (Math.Abs(p.Rate - 1.0) > 0.001)
                player.SetProperty("speed", p.Rate);
            player.SetProperty("audio-pitch-correction", p.PreservePitch);
        }
        catch (Exception) { /* propiedades no críticas */ }
        TryApplyEq(player, _eqs.GetValueOrDefault(cueId));

        // Escala + geometría en una sola pasada (la escala del cue es el zoom
        // base del modo FULL; en Python la geometría FULL pisaba la escala a 0)
        ApplyGeo(player, p.Geometry ?? new VideoGeometry(), BaseZoom(p));

        // ── Iniciar reproducción — T0 justo antes de play() ─────────────
        _players[cueId] = player;
        _playerSlots[cueId] = (screen, layer);
        t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            player.Play(filePath);
        }
        catch (Exception exc)
        {
            _players.Remove(cueId);
            player.Dispose();
            EmitError(cueId, $"mpv no pudo cargar el archivo: {exc.Message}");
            return;
        }

        SetState(cueId, MediaCueState.Playing);
        if (p.ExternalAudio)
            StartSyncPll(cueId, player, p);
    }

    /// <summary>Fija en mpv la parte factible de Display &amp; Geometry
    /// (misma matemática que la versión Python). baseZoom = escala del cue
    /// en log2, usada como zoom del modo FULL.</summary>
    private static void ApplyGeo(MpvPlayer player, VideoGeometry geo, double baseZoom)
    {
        try
        {
            if (geo.Mode.Equals("CUSTOM", StringComparison.OrdinalIgnoreCase))
            {
                double halfW = Math.Max(1.0, geo.SurfW / 2.0);
                double halfH = Math.Max(1.0, geo.SurfH / 2.0);
                player.SetProperty("keepaspect", "yes");
                player.SetProperty("video-zoom", Math.Log2(Math.Max(0.01, geo.ScaleX)));
                player.SetProperty("video-pan-x", geo.TranslateX / halfW);
                player.SetProperty("video-pan-y", -geo.TranslateY / halfH);
                long rot = ((long)(Math.Round(geo.RotateZ / 90.0) * 90) % 360 + 360) % 360;
                player.SetProperty("video-rotate", rot);
            }
            else   // FULL surface
            {
                player.SetProperty("keepaspect", geo.PreserveAspect ? "yes" : "no");
                player.SetProperty("video-zoom", baseZoom);
                player.SetProperty("video-pan-x", 0.0);
                player.SetProperty("video-pan-y", 0.0);
                player.SetProperty("video-rotate", 0L);
            }
        }
        catch (Exception) { /* geometría es best-effort */ }
    }

    /// <summary>Salida única POR (SALIDA, PISTA): termina los players de la
    /// misma pista de la misma salida; pistas o salidas distintas conviven.
    /// El huérfano pasa a STOPPED. EXCEPCIÓN — crossfade en la misma pista
    /// (compositor): con handover el saliente sigue vivo durante el fundido
    /// (su propio fin lo retira; timer de seguridad por si no).</summary>
    private void TerminateSlotOccupants(int cueId, int screen, int layer,
        bool handover, double handoverSeconds)
    {
        // _playerSlots cubre AMBOS tipos de player (mpv y HAP): un cue HAP
        // sustituye al mpv de su pista y viceversa
        foreach (int otherId in _playerSlots.Keys.ToList())
        {
            if (otherId == cueId) continue;
            if (_playerSlots.GetValueOrDefault(otherId, (0, 2)) != (screen, layer)) continue;
            if (handover)
            {
                ScheduleHandoverKill(otherId, handoverSeconds + 0.25);
                continue;
            }
            CancelFade(otherId);
            SetState(otherId, MediaCueState.Stopped);
            TerminatePlayer(otherId);
        }
        // Sin cruce, quien toma la pista la toma ENTERA: los fotogramas
        // congelados de cruces anteriores en esa (salida, pista) sobran.
        if (!handover)
            ReleaseFrozenOnSlot(screen, layer);
    }

    /// <summary>Suelta los congelados de una (salida, pista) concreta.</summary>
    private void ReleaseFrozenOnSlot(int screen, int layer)
    {
        var ids = _gpuPool
            .Where(s => s.FrozenCueId is not null && s.Screen == screen && s.Layer == layer)
            .Select(s => s.FrozenCueId!.Value)
            .Concat(_hapPool
                .Where(s => s.FrozenCueId is not null && s.Screen == screen && s.Layer == layer)
                .Select(s => s.FrozenCueId!.Value))
            .Concat(_proresPool
                .Where(s => s.FrozenCueId is not null && s.Screen == screen && s.Layer == layer)
                .Select(s => s.FrozenCueId!.Value))
            .ToList();
        foreach (int id in ids)
            ReleaseFrozenSurfaces(id);
    }

    /// <summary>Plazo del CONGELADO si el cue muere con un cruce vivo encima
    /// (fin de archivo, fin de su duración declarada o Stop del propio cue).
    /// null = no hay cruce que servir → liberación normal.</summary>
    private long? FreezeDeadlineFor(int cueId)
    {
        if (!_handoverDeadlines.TryGetValue(cueId, out long deadline))
            return null;
        if (Environment.TickCount64 >= deadline)
            return null;   // el cruce ya terminó: no hay nada que sostener
        Trace($"freeze: cue={cueId} muere DENTRO del cruce → congelar último fotograma");
        return deadline;
    }

    // ------------------------------------------------------------------
    // GO instantáneo: precarga en PAUSA del cue en standby (compositor)
    // ------------------------------------------------------------------

    /// <summary>Prepara el cue en standby con el player YA creado, cargado y
    /// pausado en su primer frame (solo compositor GPU; sin él cae a la
    /// verificación clásica). El Play posterior con parámetros equivalentes
    /// solo quita la pausa. Idempotente si ya está preparado igual.</summary>
    public void PrepareStandby(int cueId, string filePath, VideoPlayParams? p = null)
    {
        if (!MpvAvailable || string.IsNullOrEmpty(filePath))
            return;
        var np = NormalizeParams(p ?? new VideoPlayParams());
        // MOTOR HAP: sin precarga en pausa (documentado) — el GO paga solo el
        // demux del moov + el primer frame (~ms: en HAP todo es intra). La
        // verificación clásica deja el archivo cacheado para el Play.
        if (np.UseHapEngine || np.UseProResEngine)
        {
            // Motor ProRes: mismo criterio. El GO paga el demux del moov y el
            // primer frame; ProRes es todo-intra, así que no hay GOP que
            // rebobinar y el arranque es de milisegundos.
            Load(cueId, filePath);
            return;
        }
        bool gpu = _gpuSurfaces is not null && !_gpuFailed && _forcedVo is null
            && !np.ForceClassicPath;
        if (!gpu || _players.ContainsKey(cueId))
        {
            Load(cueId, filePath);   // clásico o ya sonando: solo verificación
            return;
        }
        if (_standby is { } sb && sb.CueId == cueId && sb.Path == filePath
            && ParamsEquivalent(sb.Params, np))
            return;

        DiscardStandby();
        try
        {
            _standby = BuildStandby(cueId, filePath, np);
            _files[cueId] = filePath;   // caché: un GO no-promovible no re-verifica
            if (GetState(cueId) is MediaCueState.Idle or MediaCueState.Stopped)
                SetState(cueId, MediaCueState.Loaded);
        }
        catch (Exception exc)
        {
            ErrorOccurred?.Invoke(cueId,
                $"[AVISO] Precarga en pausa falló ({exc.Message}); el GO usará el camino normal.");
            Load(cueId, filePath);
        }
    }

    private StandbyPrep BuildStandby(int cueId, string filePath, VideoPlayParams p)
    {
        int layer = Math.Clamp(p.Layer, 0, 7);
        bool canvas = p.CanvasOutputs is { Count: > 0 };
        bool isImage = ImageFormats.Contains(Path.GetExtension(filePath));

        var options = new Dictionary<string, string>
        {
            ["vo"] = "libmpv",
            ["hwdec"] = "auto",
            ["keep-open"] = "yes",
            ["hr-seek"] = "yes",
            ["keepaspect"] = "yes",
            ["osc"] = "no",
            ["input-default-bindings"] = "no",
            ["input-vo-keyboard"] = "no",
            ["pause"] = "yes",   // ← cargado y congelado en su primer frame
        };
        if (_nullAudio) options["ao"] = "null";
        if (p.ExternalAudio) options["aid"] = "no";   // híbrido: mpv sin audio
        if (p.Loop) options["loop-file"] = "inf";
        else if (p.PlayCount > 1) options["loop-file"] = (p.PlayCount - 1).ToString();
        if (p.TrimStart > 0.0) options["start"] = p.TrimStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        if (p.TrimEnd > 0.0) options["end"] = p.TrimEnd.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        if (isImage) options["image-display-duration"] = "inf";

        // CA2000: la propiedad pasa al StandbyPrep devuelto; el catch libera
        // slot y player en toda ruta de error — el analizador no lo rastrea.
#pragma warning disable CA2000
        var player = new MpvPlayer(options);
#pragma warning restore CA2000
        GpuSlot? slot = null;
        try
        {
            var geo = p.Geometry ?? new VideoGeometry();
            int screen = canvas ? -1 : Math.Clamp(p.Screen, 0, 3);
            slot = AcquireGpuSlot(cueId, screen, layer,
                (int)geo.SurfW, (int)geo.SurfH, canvas ? p.CanvasOutputs : null);
            slot.Pipeline.AttachPlayer(player);

            var thisPlayer = player;
            player.EndFile += () => Post(() =>
            {
                // Solo si ESTE player (promovido) sigue siendo el del cue — su
                // EndFile póstumo tras un descarte no debe tocar al sucesor
                if (GetState(cueId) == MediaCueState.Playing &&
                    _players.TryGetValue(cueId, out var cur) &&
                    ReferenceEquals(cur, thisPlayer))
                {
                    SetState(cueId, MediaCueState.Stopped);
                    TerminatePlayer(cueId);
                }
            });

            try
            {
                player.SetProperty("volume",
                    Math.Clamp(p.Volume * 100.0, 0.0, 100.0) * _master);
                if (Math.Abs(p.Rate - 1.0) > 0.001)
                    player.SetProperty("speed", p.Rate);
                player.SetProperty("audio-pitch-correction", p.PreservePitch);
            }
            catch (Exception) { /* no críticas */ }
            ApplyGeo(player, p.Geometry ?? new VideoGeometry(), BaseZoom(p));

            player.Play(filePath);   // carga PAUSADO (pause=yes va en opciones)
            return new StandbyPrep(cueId, filePath, p, player);
        }
        catch
        {
            // El slot vuelve al pool (el pipeline sobrevive); solo muere el core
            if (slot is not null)
                ReleaseGpuSlot(cueId);
            player.Dispose();
            throw;
        }
    }

    /// <summary>Descarta la precarga en pausa (cueId concreto o -1 = la que
    /// haya). Regla mpv: el render context muere ANTES que el core — Detach
    /// lo garantiza; el pipeline del pool sobrevive para el siguiente cue.</summary>
    public void DiscardStandby(int cueId = -1)
    {
        if (_standby is not { } sb || (cueId >= 0 && sb.CueId != cueId))
            return;
        Trace($"DiscardStandby cue={sb.CueId}");
        _standby = null;
        ReleaseGpuSlot(sb.CueId);
        sb.Player.Dispose();
    }

    /// <summary>Red de seguridad del crossfade: termina al saliente cuando el
    /// fundido acaba, si nadie lo ha parado antes (p.ej. un vídeo en loop).</summary>
    private void ScheduleHandoverKill(int cueId, double seconds)
    {
        if (_handoverTimers.Remove(cueId, out var prev))
            prev.Dispose();
        _handoverDeadlines[cueId] =
            Environment.TickCount64 + (long)(Math.Max(0.05, seconds) * 1000);
        var timer = new System.Threading.Timer(_ => Post(() =>
        {
            if (_handoverTimers.Remove(cueId, out var t))
                t.Dispose();
            _handoverDeadlines.Remove(cueId);
            // El cruce acabó: si el saliente sigue vivo se retira, y si murió
            // dentro del fundido se suelta su fotograma congelado.
            ReleaseFrozenSurfaces(cueId);
            // OJO: cubre AMBOS motores — el saliente de un crossfade puede
            // ser un player HAP (antes solo se miraba _players y el HAP
            // quedaba vivo debajo del entrante para siempre)
            if ((_players.ContainsKey(cueId) || _hapPlayers.ContainsKey(cueId))
                && GetState(cueId) == MediaCueState.Playing)
            {
                CancelFade(cueId);
                SetState(cueId, MediaCueState.Stopped);
                TerminatePlayer(cueId);
            }
        }), null, (int)(Math.Max(0.05, seconds) * 1000), System.Threading.Timeout.Infinite);
        _handoverTimers[cueId] = timer;
    }

    // ------------------------------------------------------------------
    // PLL de la RUTA HÍBRIDA (ExternalAudio): vídeo mpv mudo disciplinado
    // al reloj del audio espejo. Semántica de decisión en MpvSyncPll.
    // ------------------------------------------------------------------

    /// <summary>RED DE SEGURIDAD: el espejo de audio del cue falló en
    /// caliente → recuperar el audio POR MPV en el player vivo (aid=auto) y
    /// apagar el PLL (sin reloj externo ya no hay nada que disciplinar).
    /// El volumen lo reajusta el controlador después (el fallback HAP corre
    /// con volume 0).</summary>
    public void RestoreInternalAudio(int cueId)
    {
        StopSyncPll(cueId);
        if (!_players.TryGetValue(cueId, out var player))
            return;
        try
        {
            player.SetProperty("speed", 1.0);
            player.SetProperty("aid", "auto");
        }
        catch (Exception) { /* player muriendo: el Stop llegará enseguida */ }
        Trace($"pll: RestoreInternalAudio cue={cueId} (aid=auto)");
    }

    private void StartSyncPll(int cueId, MpvPlayer player, VideoPlayParams p)
    {
        StopSyncPll(cueId);
        if (_hapClock is null)
            return;   // sin reloj inyectado (tests/arranque raro): free-run
        var runner = new PllRunner();
        runner.Timer = new System.Threading.Timer(
            _ => SyncPllTick(cueId, player, runner, p), null, PllTickMs, PllTickMs);
        _syncPlls[cueId] = runner;
        Trace($"pll: start cue={cueId} (tick {PllTickMs} ms)");
    }

    private void StopSyncPll(int cueId)
    {
        if (_syncPlls.Remove(cueId, out var runner))
        {
            runner.Dispose();
            Trace($"pll: stop cue={cueId}");
        }
    }

    /// <summary>Tick del PLL (hilo del timer). mpv es thread-safe para
    /// propiedades (mismo patrón que los fades); el player puede estar
    /// muriendo en paralelo → todo defensivo.</summary>
    private void SyncPllTick(int cueId, MpvPlayer player, PllRunner runner,
        VideoPlayParams p)
    {
        try
        {
            if (player.ObservedPaused)
                return;   // cue pausado: ambos relojes congelados — tick inerte

            double? video = player.ObservedTimePosSmooth(runner.Speed) is double t
                ? Math.Max(0.0, t - p.TrimStart)
                : null;
            double? audio = _hapClock?.Invoke(cueId);

            // Duración efectiva (con trims) para la guarda de loop; se cachea
            // al primer read válido (no consultar el core en cada tick)
            if (runner.CachedDuration is null
                && player.GetPropertyDouble("duration") is double raw && raw > 0.0)
            {
                double end = p.TrimEnd > 0.0 ? Math.Min(p.TrimEnd, raw) : raw;
                runner.CachedDuration = Math.Max(0.0, end - p.TrimStart);
            }

            var d = runner.Pll.Tick(video, audio, runner.CachedDuration ?? 0.0, p.Loop);
            if (d.HardSeekTo is double target)
            {
                Trace($"pll: cue={cueId} HARD-SEEK → {target:F3}s " +
                    $"(video={video:F3} audio={audio:F3})");
                player.SetProperty("time-pos", target + p.TrimStart);
            }
            if (d.WriteSpeed)
            {
                runner.Speed = d.Speed;
                player.SetProperty("speed", d.Speed);
                Trace($"pll: cue={cueId} speed={d.Speed:F3} " +
                    $"drift={(video is double vv && audio is double aa ? vv - aa : 0.0):F3}s");
            }
        }
        catch (ObjectDisposedException) { /* player terminado bajo el timer */ }
        catch (Exception) { /* propiedad no crítica / carrera de cierre */ }
    }

    private void TerminatePlayer(int cueId)
    {
        StopSyncPll(cueId);
        _playerSlots.Remove(cueId);
        if (_handoverTimers.Remove(cueId, out var handover))
            handover.Dispose();
        // CONGELADO: ¿muere con un cruce vivo encima? La decisión se toma AQUÍ
        // y no en los manejadores de fin, porque el saliente puede morir por
        // tres caminos distintos (fin de archivo, fin de la duración declarada
        // del cue vía Stop del controlador, o Stop directo) y en los tres hay
        // un crossfade que sigue necesitando su imagen.
        long? freezeUntil = FreezeDeadlineFor(cueId);
        _handoverDeadlines.Remove(cueId);
        // Regla de mpv: el render context se libera ANTES que el core
        // (ReleaseGpuSlot hace el Detach; el pipeline del pool sobrevive)
        ReleaseGpuSlot(cueId, freezeUntil);
        if (_players.Remove(cueId, out var player))
            player.Dispose();
        // Motor HAP: parar hilos y liberar la sesión ANTES de soltar el slot
        // (el pipeline del pool sobrevive, igual que el de mpv)
        if (_hapPlayers.Remove(cueId, out var hap))
        {
            Trace($"hap: terminate cue={cueId}");
            hap.Dispose();
            ReleaseHapSlot(cueId, freezeUntil);
        }
        // Motor ProRes: igual — el player para sus hilos y suelta los frames
        // nativos del ring (63 MB cada uno en 4K 4444) antes de tocar el slot
        if (_proresPlayers.Remove(cueId, out var pr))
        {
            Trace($"prores: terminate cue={cueId}");
            pr.Dispose();
            ReleaseProResSlot(cueId, freezeUntil);
        }
    }

    /// <summary>Programa el DESCONGELADO al vencer el plazo del cruce. Timer
    /// propio: el del handover lo desmonta TerminatePlayer al morir el
    /// saliente, así que no sirve para sostener la liberación diferida.</summary>
    private void ScheduleThaw(int cueId, long deadlineMs)
    {
        if (_thawTimers.Remove(cueId, out var prev))
            prev.Dispose();
        int ms = (int)Math.Clamp(deadlineMs - Environment.TickCount64, 50, int.MaxValue);
        _thawTimers[cueId] = new System.Threading.Timer(
            _ => Post(() => ReleaseFrozenSurfaces(cueId)), null,
            ms, System.Threading.Timeout.Infinite);
    }

    private void DisposeThawTimer(int cueId)
    {
        if (_thawTimers.Remove(cueId, out var timer))
            timer.Dispose();
    }

    /// <summary>TOPE DE SEGURIDAD por tiempo: ningún congelado sobrevive a su
    /// plazo. Se barre en cada petición de slot, así que la propia demanda de
    /// superficies reclama las que se hubieran quedado atrás.</summary>
    private void SweepExpiredFrozen() =>
        _freeze.ReleaseExpired(Environment.TickCount64);

    // ------------------------------------------------------------------
    // Pool de pipelines GPU persistentes (auditoría 05)
    // ------------------------------------------------------------------

    private static string GpuSpec(int w, int h, IReadOnlyList<CanvasView>? views) =>
        views is null
            ? $"{w}x{h}"
            : $"{w}x{h}|" + string.Join(";",
                views.Select(v => $"{v.Screen},{v.X},{v.Y},{v.W},{v.H}"));

    /// <summary>Devuelve un slot LIBRE del pool para (salida, pista, spec) —
    /// o crea su pipeline si no lo hay (primera vez, o crossfade que necesita
    /// un segundo en la misma pista). Marca el slot ocupado y asigna los
    /// sinks al cue (opacidad/z en la ventana).</summary>
    private GpuSlot AcquireGpuSlot(int cueId, int screen, int layer,
        int surfW, int surfH, IReadOnlyList<CanvasView>? views)
    {
        SweepExpiredFrozen();
        string spec = GpuSpec(surfW, surfH, views);
        // Un slot CONGELADO no está libre: repartirlo pisaría el fotograma
        // que el crossfade todavía está mezclando.
        var slot = _gpuPool.FirstOrDefault(s =>
            s.BusyCueId is null && s.FrozenCueId is null
            && s.Screen == screen && s.Layer == layer
            && s.Spec == spec);
        if (slot is null)
        {
            var pipeline = new MpvGpuRenderer(surfW, surfH, views);
            var sinks = new List<IVideoLayerSurface>();
            try
            {
                for (int i = 0; i < pipeline.Views.Count; i++)
                {
                    var v = pipeline.Views[i];
                    int sinkScreen = views is not null ? v.Screen : Math.Max(0, screen);
                    var sink = _gpuSurfaces!(sinkScreen, layer, _gpuSlotSeq++);
                    sink.SetSurface(pipeline.ViewSurface(i), v.W, v.H);
                    sink.SetSharedHandle(pipeline.ViewSharedHandle(i));
                    int viewIndex = i;
                    sink.SetFrameCopier(FrameCopierFor(sink,
                        sinFence: () => pipeline.CopyPresent(viewIndex),
                        conFence: () => pipeline.CopyPresentFenced(viewIndex)));
                    pipeline.FrameRendered += sink.InvalidateFrame;
                    sinks.Add(sink);
                }
            }
            catch (Exception)
            {
                // El proveedor de superficies puede LANZAR (Secuence lo hace
                // cuando el puente D3D9Ex→D3D11 no se puede montar: es la
                // única forma de que este método falle y el llamante caiga a
                // la ruta clásica). Si se escapa sin pasar por aquí, este
                // pipeline —device D3D9Ex, FBO, superficies compartidas,
                // interop GL— no lo libera NADIE: la fuga de la auditoría 05
                // por el borde de error. En la ruta mpv se pagaría una vez
                // por sesión (_gpuFailed engancha), pero en HAP y ProRes no
                // hay latch y sería una fuga POR CUE.
                //
                // El desmontaje va BLINDADO (ver TeardownStep): si él mismo
                // lanzara, sustituiría a la excepción original y dejaría el
                // pipeline sin liberar, que es exactamente lo que este catch
                // viene a evitar.
                TearDownFailedGpuSlot(pipeline, sinks);
                throw;
            }
            slot = new GpuSlot
            {
                Pipeline = pipeline, Sinks = sinks,
                Screen = screen, Layer = layer, Spec = spec,
            };
            _gpuPool.Add(slot);
        }
        // AssignCue TAMBIEN puede lanzar (el anfitrion se encuentra la capa
        // muerta, o su compositor perdido). Si se escapa de aqui, el `slot`
        // local del LLAMANTE sigue en null —la asignacion nunca llego a
        // ejecutarse— y su guarda `if (slot is not null) ReleaseGpuSlot(...)`
        // no corre: el slot se queda en el pool con BusyCueId puesto para
        // siempre, no vuelve a casar con la busqueda de slots libres, y su
        // pipeline no se libera hasta Cleanup(). En mpv lo tapa _gpuFailed,
        // pero en HAP y ProRes no hay latch y seria una fuga POR CUE — el
        // mismo modo de fallo que este try venia a eliminar, una linea mas
        // abajo. Por eso el slot se desmonta ENTERO y se relanza: el llamante
        // cae a la ruta clasica y el pool queda limpio.
        try
        {
            slot.BusyCueId = cueId;
            foreach (var s in slot.Sinks)
                s.AssignCue(cueId);
        }
        catch (Exception)
        {
            // Primero lo que NO puede fallar (el pool queda limpio pase lo que
            // pase) y solo después el desmontaje, blindado: ver TeardownStep.
            slot.BusyCueId = null;
            _gpuPool.Remove(slot);
            TearDownFailedGpuSlot(slot.Pipeline, slot.Sinks);
            throw;
        }
        return slot;
    }

    /// <summary>Suelta el slot del cue: Detach del render context (regla mpv:
    /// antes que el core) y sinks ocultos. El pipeline queda LIBRE en el pool
    /// — el interop jamás se desmonta por cue (esa era LA fuga de VRAM).
    /// Si el Detach falla (hilo dañado), el pipeline se descarta entero.
    /// Con <paramref name="freezeUntilMs"/> el Detach ocurre igual (el core
    /// muere ahora) pero los sinks NO se ocultan: la superficie se queda con
    /// el último fotograma hasta ese plazo — CONGELADO del saliente.</summary>
    private void ReleaseGpuSlot(int cueId, long? freezeUntilMs = null)
    {
        var slot = _gpuPool.FirstOrDefault(s => s.BusyCueId == cueId);
        if (slot is null)
            return;
        Trace($"ReleaseGpuSlot cue={cueId} (detach{(freezeUntilMs is null ? "" : ", congelado")})");
        bool ok = slot.Pipeline.DetachPlayer();
        slot.BusyCueId = null;
        if (ok && freezeUntilMs is long deadline)
        {
            slot.FrozenCueId = cueId;
            _freeze.Freeze(cueId, deadline, () =>
            {
                DisposeThawTimer(cueId);
                if (slot.FrozenCueId != cueId)
                    return;
                slot.FrozenCueId = null;
                foreach (var s in slot.Sinks)
                    s.ReleaseCue(cueId);
            });
            ScheduleThaw(cueId, deadline);
            return;
        }
        foreach (var s in slot.Sinks)
            s.ReleaseCue(cueId);
        if (!ok)
        {
            _gpuPool.Remove(slot);
            // Desenganchar ANTES de liberar: mientras el pipeline conserve la
            // suscripción a FrameRendered, mantiene vivos a sus sinks.
            DetachSinks(slot.Pipeline, slot.Sinks);
            slot.Pipeline.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // MOTOR HAP PROPIO (paso 3): arranque, pool y ciclo de vida
    // ------------------------------------------------------------------

    /// <summary>Arranca un cue por el motor HAP propio. Devuelve false si el
    /// demux o el pipeline GPU fallan (el llamante cae a mpv clásico muteado).
    /// El VÍDEO va por aquí; su AUDIO lo arranca el controlador en el
    /// AudioEngine (id espejo) y llega a este player como reloj inyectado.</summary>
    private bool StartHapPlayback(int cueId, string filePath, VideoPlayParams p,
        int screen, int layer)
    {
        MovInfo info;
        HapTextureFormat format;
        try
        {
            info = MovDemuxer.Open(filePath);
            format = info.VideoFourcc switch
            {
                "Hap1" => HapTextureFormat.RgbDxt1,
                "Hap5" => HapTextureFormat.RgbaDxt5,
                "HapY" => HapTextureFormat.YCoCgDxt5,
                _ => throw new InvalidDataException(
                    $"fourcc HAP no soportado: {info.VideoFourcc}"),
            };
            if (info.FrameCount == 0 || info.Fps <= 1.0)
                throw new InvalidDataException("pista de vídeo vacía o sin fps");
        }
        catch (Exception exc)
        {
            Trace($"hap: demux falló cue={cueId}: {exc.Message}");
            ErrorOccurred?.Invoke(cueId,
                $"[AVISO] Motor HAP: demux falló ({exc.Message}). Fallback a mpv.");
            return false;
        }

        TerminateSlotOccupants(cueId, screen, layer,
            p.HandoverSeconds > 0.0, p.HandoverSeconds);
        TerminatePlayer(cueId);

        HapSlot? slot = null;
        HapCuePlayer? player = null;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var geo = p.Geometry ?? new VideoGeometry();
            slot = AcquireHapSlot(cueId, screen, layer, (int)geo.SurfW, (int)geo.SurfH);
            // CA2000: la propiedad del player se transfiere a _hapPlayers; el
            // catch lo libera en toda ruta de error.
#pragma warning disable CA2000
            player = new HapCuePlayer(filePath, info, format, slot.Pipeline, p,
                () => _hapClock?.Invoke(cueId), Trace);
#pragma warning restore CA2000
            var thisPlayer = player;
            player.FirstFrame += () => Post(() =>
            {
                LatencyReport?.Invoke(cueId,
                    System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                FrameReady?.Invoke(cueId);
            });
            player.Ended += () => Post(() =>
            {
                // Fin natural. Igual que el EndFile de mpv: solo si ESTE
                // player sigue siendo el del cue (un Ended póstumo de una
                // instancia sustituida no debe tocar al sucesor). La
                // IDENTIDAD manda: aunque un estado intermedio no sea
                // Playing, un player terminado no debe quedarse en
                // _hapPlayers (contaminaría GetElapsed/IsPlaying del cue).
                if (_hapPlayers.TryGetValue(cueId, out var cur) &&
                    ReferenceEquals(cur, thisPlayer))
                {
                    if (GetState(cueId) == MediaCueState.Playing)
                        SetState(cueId, MediaCueState.Stopped);
                    TerminatePlayer(cueId);
                }
            });

            _hapPlayers[cueId] = player;
            _playerSlots[cueId] = (screen, layer);
            _volumes[cueId] = p.Volume;
            player.Start();
        }
        catch (Exception exc)
        {
            _hapPlayers.Remove(cueId);
            _playerSlots.Remove(cueId);
            player?.Dispose();
            if (slot is not null)
                ReleaseHapSlot(cueId);
            Trace($"hap: arranque falló cue={cueId}: {exc.Message}");
            ErrorOccurred?.Invoke(cueId,
                $"[AVISO] Motor HAP no disponible ({exc.Message}). Fallback a mpv.");
            return false;
        }

        Trace($"hap: play cue={cueId} {info.Width}x{info.Height} " +
            $"{info.VideoFourcc} fps={info.Fps:F2} frames={info.FrameCount}");
        ErrorOccurred?.Invoke(cueId,
            $"[AVISO] Motor HAP propio — salida {screen + 1}, pista {layer + 1}, " +
            "reloj: audio del cue.");
        SetState(cueId, MediaCueState.Playing);
        return true;
    }

    /// <summary>Slot LIBRE del pool HAP para (salida, pista, spec) — o crea su
    /// pipeline (primera vez, o crossfade que necesita un segundo en la misma
    /// pista). El sink del compositor se cablea UNA vez, igual que en el pool
    /// mpv: SetSurface + SetFrameCopier + FrameRendered→InvalidateFrame.</summary>
    private HapSlot AcquireHapSlot(int cueId, int screen, int layer, int surfW, int surfH)
    {
        SweepExpiredFrozen();
        string spec = $"{Math.Max(16, surfW)}x{Math.Max(16, surfH)}";
        var slot = _hapPool.FirstOrDefault(s =>
            s.BusyCueId is null && s.FrozenCueId is null
            && s.Screen == screen && s.Layer == layer
            && s.Spec == spec);
        if (slot is null)
        {
            // CA2000: el pipeline pasa al pool (vive toda la sesión).
#pragma warning disable CA2000
            var pipeline = new HapGpuPipeline(surfW, surfH, SharePresentSurfaces);
#pragma warning restore CA2000
            IVideoLayerSurface sink;
            try
            {
                sink = _gpuSurfaces!(Math.Max(0, screen), layer, _gpuSlotSeq++);
                sink.SetSurface(pipeline.PresentSurfacePointer, pipeline.Width, pipeline.Height);
                sink.SetSharedHandle(pipeline.PresentSharedHandle);
                sink.SetFrameCopier(FrameCopierFor(sink,
                    sinFence: pipeline.CopyPresent,
                    conFence: pipeline.CopyPresentFenced));
                pipeline.FrameRendered += sink.InvalidateFrame;
            }
            catch (Exception)
            {
                // Sin esto, un proveedor que lance (Secuence, cuando el
                // puente no se puede montar) fugaría el pipeline ENTERO —
                // y aquí, a diferencia de la ruta mpv, no hay _gpuFailed que
                // enganche: sería una fuga POR CUE. Ver AcquireGpuSlot.
                // Blindado para que un Dispose que lance no sustituya a la
                // excepción original (ver TeardownStep).
                TeardownStep("liberar pipeline HAP", pipeline.Dispose);
                throw;
            }
            slot = new HapSlot
            {
                Pipeline = pipeline, Sink = sink,
                Screen = screen, Layer = layer, Spec = spec,
            };
            _hapPool.Add(slot);
        }
        // Mismo desmontaje que en AcquireGpuSlot si AssignCue lanza — y aqui
        // importa mas: sin _gpuFailed que enganche, un slot zombi con su
        // pipeline (device D3D9Ex, hilo de decodificacion) seria una fuga por
        // cada cue.
        try
        {
            slot.BusyCueId = cueId;
            slot.Sink.AssignCue(cueId);
        }
        catch (Exception)
        {
            // Primero lo que NO puede fallar, y el desmontaje blindado
            // después: si ClearSurface lanzara (en Secuence toma el lock del
            // grafo y libera D3D11), el pipeline se quedaría sin liberar y sin
            // nadie que lo referencie. Ver TeardownStep.
            slot.BusyCueId = null;
            _hapPool.Remove(slot);
            var hapPipe = slot.Pipeline;
            var hapSink = slot.Sink;
            TeardownStep("desenganchar sink HAP", () =>
            {
                hapPipe.FrameRendered -= hapSink.InvalidateFrame;
                hapSink.ClearSurface();
            });
            TeardownStep("liberar pipeline HAP", hapPipe.Dispose);
            throw;
        }
        return slot;
    }

    /// <summary>Suelta el slot HAP del cue: sink oculto y pipeline LIBRE en el
    /// pool (jamás se desmonta por cue — misma regla que ReleaseGpuSlot).
    /// Llamar con el player YA parado (su sesión de texturas liberada).
    /// Con <paramref name="freezeUntilMs"/>, el sink NO se oculta: la
    /// superficie de presentación conserva el último fotograma dibujado
    /// (el ring de texturas del cue sí muere) — CONGELADO del saliente.</summary>
    private void ReleaseHapSlot(int cueId, long? freezeUntilMs = null)
    {
        var slot = _hapPool.FirstOrDefault(s => s.BusyCueId == cueId);
        if (slot is null)
            return;
        slot.BusyCueId = null;
        if (freezeUntilMs is long deadline)
        {
            Trace($"hap: congelado cue={cueId} (último fotograma en pantalla)");
            slot.FrozenCueId = cueId;
            _freeze.Freeze(cueId, deadline, () =>
            {
                DisposeThawTimer(cueId);
                if (slot.FrozenCueId != cueId)
                    return;
                slot.FrozenCueId = null;
                slot.Sink.ReleaseCue(cueId);
            });
            ScheduleThaw(cueId, deadline);
            return;
        }
        Trace($"hap: release slot cue={cueId}");
        slot.Sink.ReleaseCue(cueId);
    }

    // ------------------------------------------------------------------
    // MOTOR ProRes PROPIO (paso 2): arranque, pool y ciclo de vida
    // ------------------------------------------------------------------

    /// <summary>Cues que ahora mismo reproduce el motor ProRes. Es el reparto
    /// del presupuesto de hilos: el laboratorio midió que lo correcto es
    /// ≈ lógicos/capas, no ProcessorCount por capa (§ 4 del INFORME).</summary>
    private int ActiveProResLayers => _proresPlayers.Count + 1;

    /// <summary>Arranca un cue por el motor ProRes propio. Devuelve false si el
    /// demux, las DLL de FFmpeg o el pipeline GPU fallan (el llamante cae a mpv
    /// clásico MUTEADO). El VÍDEO va por aquí; su AUDIO lo arranca el
    /// controlador en el AudioEngine (id espejo) y llega como reloj inyectado.</summary>
    private bool StartProResPlayback(int cueId, string filePath, VideoPlayParams p,
        int screen, int layer)
    {
        MovInfo info;
        try
        {
            if (!FFmpegLoader.IsAvailable)
            {
                throw new DllNotFoundException(
                    "no están las DLL LGPL de FFmpeg junto al ejecutable");
            }

            info = MovDemuxer.Open(filePath);
            if (!ProResFormat.IsProRes(info.VideoFourcc))
                throw new InvalidDataException($"fourcc no es ProRes: {info.VideoFourcc}");
            if (info.FrameCount == 0 || info.Fps <= 1.0)
                throw new InvalidDataException("pista de vídeo vacía o sin fps");
        }
        catch (Exception exc)
        {
            Trace($"prores: demux/carga falló cue={cueId}: {exc.Message}");
            ErrorOccurred?.Invoke(cueId,
                $"[AVISO] Motor ProRes: {exc.Message}. Fallback a mpv.");
            return false;
        }

        // El LAYOUT real (formato de píxel) lo decide el decodificador, no el
        // fourcc: hay que abrirlo una vez para saber la spec de las texturas.
        // Es también el ensayo general del decode nativo — si va a reventar,
        // que reviente AQUÍ, donde todavía se puede caer a mpv.
        PixelLayout layout;
        try
        {
            layout = ProbeLayout(filePath, info);
        }
        catch (Exception exc)
        {
            Trace($"prores: sondeo del layout falló cue={cueId}: {exc.Message}");
            ErrorOccurred?.Invoke(cueId,
                $"[AVISO] Motor ProRes: no se pudo decodificar ({exc.Message}). Fallback a mpv.");
            return false;
        }

        TerminateSlotOccupants(cueId, screen, layer,
            p.HandoverSeconds > 0.0, p.HandoverSeconds);
        TerminatePlayer(cueId);

        ProResSlot? slot = null;
        ProResCuePlayer? player = null;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int threads = ThreadBudget.For(ActiveProResLayers);
        try
        {
            var geo = p.Geometry ?? new VideoGeometry();
            slot = AcquireProResSlot(cueId, screen, layer, (int)geo.SurfW, (int)geo.SurfH,
                info.Width, info.Height, layout);
            // CA2000: la propiedad del player se transfiere a _proresPlayers; el
            // catch lo libera en toda ruta de error.
#pragma warning disable CA2000
            player = new ProResCuePlayer(filePath, info, slot.Pipeline, slot.Session!, p,
                threads, () => _hapClock?.Invoke(cueId), Trace);
#pragma warning restore CA2000
            var thisPlayer = player;
            player.FirstFrame += () => Post(() =>
            {
                LatencyReport?.Invoke(cueId,
                    System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                FrameReady?.Invoke(cueId);
            });
            player.Ended += () => Post(() =>
            {
                // Fin natural. Misma regla de IDENTIDAD que en HAP: un Ended
                // póstumo de una instancia sustituida no debe tocar al sucesor.
                if (_proresPlayers.TryGetValue(cueId, out var cur) &&
                    ReferenceEquals(cur, thisPlayer))
                {
                    if (GetState(cueId) == MediaCueState.Playing)
                        SetState(cueId, MediaCueState.Stopped);
                    TerminatePlayer(cueId);
                }
            });

            _proresPlayers[cueId] = player;
            _playerSlots[cueId] = (screen, layer);
            _volumes[cueId] = p.Volume;
            player.Start();
        }
        catch (Exception exc)
        {
            _proresPlayers.Remove(cueId);
            _playerSlots.Remove(cueId);
            player?.Dispose();
            if (slot is not null)
                ReleaseProResSlot(cueId);
            Trace($"prores: arranque falló cue={cueId}: {exc.Message}");
            ErrorOccurred?.Invoke(cueId,
                $"[AVISO] Motor ProRes no disponible ({exc.Message}). Fallback a mpv.");
            return false;
        }

        Trace($"prores: play cue={cueId} {info.Width}x{info.Height} " +
            $"{info.VideoFourcc} {layout.Name} fps={info.Fps:F2} " +
            $"frames={info.FrameCount} hilos={threads}");
        ErrorOccurred?.Invoke(cueId,
            $"[AVISO] Motor ProRes propio — salida {screen + 1}, pista {layer + 1}, " +
            $"{layout.Name}, {threads} hilos, reloj: audio del cue.");
        SetState(cueId, MediaCueState.Playing);
        return true;
    }

    /// <summary>Decodifica el primer frame para saber el FORMATO DE PÍXEL real
    /// (el fourcc solo dice el perfil). Se descarta enseguida: solo interesa el
    /// layout, que es lo que dimensiona las texturas del ring.</summary>
    private static PixelLayout ProbeLayout(string filePath, MovInfo info)
    {
        using var decoder = new ProResDecoder(info.Width, info.Height, ThreadBudget.Min,
            ThreadMode.Slice, info.VideoFourcc);
        using var buffer = new PaddedBuffer(Math.Max(1, info.MaxSampleSize));
        using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        SampleEntry first = info.Samples[0];
        _ = buffer.ReadSample(fs, first);
        decoder.SendPacket(buffer.AsPacket(first.Size));
        if (!decoder.TryReceiveFrame())
            throw new InvalidDataException("el decodificador no entregó el primer frame");
        PixelLayout layout = decoder.Layout;
        decoder.ReleaseFrame();
        return layout;
    }

    /// <summary>Slot LIBRE del pool ProRes para (salida, pista, spec de
    /// SUPERFICIE) — o crea su pipeline. El sink del compositor se cablea UNA
    /// vez, igual que en los otros dos pools.
    /// Además garantiza que el slot tiene una SESIÓN con la spec del VÍDEO
    /// (tamaño + formato de píxel): si la que tenía no vale, se reconstruye.</summary>
    private ProResSlot AcquireProResSlot(int cueId, int screen, int layer,
        int surfW, int surfH, int videoW, int videoH, PixelLayout layout)
    {
        SweepExpiredFrozen();
        string spec = $"{Math.Max(16, surfW)}x{Math.Max(16, surfH)}";
        var slot = _proresPool.FirstOrDefault(s =>
            s.BusyCueId is null && s.FrozenCueId is null
            && s.Screen == screen && s.Layer == layer
            && s.Spec == spec);
        if (slot is null)
        {
            // CA2000: el pipeline pasa al pool (vive toda la sesión).
#pragma warning disable CA2000
            var pipeline = new ProResGpuPipeline(surfW, surfH, SharePresentSurfaces);
#pragma warning restore CA2000
            IVideoLayerSurface sink;
            try
            {
                sink = _gpuSurfaces!(Math.Max(0, screen), layer, _gpuSlotSeq++);
                sink.SetSurface(pipeline.PresentSurfacePointer, pipeline.Width, pipeline.Height);
                sink.SetSharedHandle(pipeline.PresentSharedHandle);
                sink.SetFrameCopier(FrameCopierFor(sink,
                    sinFence: pipeline.CopyPresent,
                    conFence: pipeline.CopyPresentFenced));
                pipeline.FrameRendered += sink.InvalidateFrame;
            }
            catch (Exception)
            {
                // Mismo motivo que en HAP: sin latch, un proveedor que lance
                // fugaría un pipeline ProRes por cada cue. Blindado para que un
                // Dispose que lance no sustituya a la excepción original.
                TeardownStep("liberar pipeline ProRes", pipeline.Dispose);
                throw;
            }
            slot = new ProResSlot
            {
                Pipeline = pipeline, Sink = sink,
                Screen = screen, Layer = layer, Spec = spec,
            };
            _proresPool.Add(slot);
        }

        // La SESIÓN se reutiliza si el vídeo entrante tiene la misma spec
        // (tamaño Y formato de píxel). En un show real eso es lo habitual —
        // los clips de una lista suelen compartir formato — y ahorra crear y
        // destruir hasta 190 MB de texturas en cada GO.
        string sessionSpec = ProResVideoSession.SpecOf(videoW, videoH, layout);
        if (slot.Session is null || slot.SessionSpec != sessionSpec)
        {
            slot.Session?.Dispose();
            slot.Session = slot.Pipeline.CreateSession(videoW, videoH, layout);
            slot.SessionSpec = sessionSpec;
            Trace($"prores: sesión nueva {sessionSpec} en slot {spec} " +
                $"(salida {screen}, pista {layer})");
        }

        // Mismo desmontaje que en AcquireGpuSlot/AcquireHapSlot si AssignCue
        // lanza. La SESION muere antes que su device, como en todas partes.
        try
        {
            slot.BusyCueId = cueId;
            slot.Sink.AssignCue(cueId);
        }
        catch (Exception)
        {
            // Primero lo que NO puede fallar, y el desmontaje blindado
            // después (ver TeardownStep): la sesión y el pipeline tienen que
            // liberarse aunque ClearSurface lance, porque el slot ya no está
            // en el pool y nadie más los referencia.
            slot.BusyCueId = null;
            _proresPool.Remove(slot);
            var prPipe = slot.Pipeline;
            var prSink = slot.Sink;
            var prSession = slot.Session;
            TeardownStep("desenganchar sink ProRes", () =>
            {
                prPipe.FrameRendered -= prSink.InvalidateFrame;
                prSink.ClearSurface();
            });
            // La SESION muere antes que su device, como en todas partes.
            TeardownStep("liberar sesión ProRes", () => prSession?.Dispose());
            TeardownStep("liberar pipeline ProRes", prPipe.Dispose);
            throw;
        }
        return slot;
    }

    /// <summary>Suelta el slot ProRes del cue: sink oculto y pipeline LIBRE en
    /// el pool (jamás se desmonta por cue). La SESIÓN se conserva para el
    /// siguiente cue de la misma spec. Con <paramref name="freezeUntilMs"/> el
    /// sink NO se oculta: la superficie de presentación conserva el último
    /// fotograma dibujado — CONGELADO del saliente del crossfade.</summary>
    private void ReleaseProResSlot(int cueId, long? freezeUntilMs = null)
    {
        var slot = _proresPool.FirstOrDefault(s => s.BusyCueId == cueId);
        if (slot is null)
            return;
        slot.BusyCueId = null;
        if (freezeUntilMs is long deadline)
        {
            Trace($"prores: congelado cue={cueId} (último fotograma en pantalla)");
            slot.FrozenCueId = cueId;
            _freeze.Freeze(cueId, deadline, () =>
            {
                DisposeThawTimer(cueId);
                if (slot.FrozenCueId != cueId)
                    return;
                slot.FrozenCueId = null;
                slot.Sink.ReleaseCue(cueId);
            });
            ScheduleThaw(cueId, deadline);
            return;
        }
        Trace($"prores: release slot cue={cueId}");
        slot.Sink.ReleaseCue(cueId);
    }

    // ------------------------------------------------------------------
    // Helpers de estado / dispatch
    // ------------------------------------------------------------------

    private void Post(Action action)
    {
        if (_dispatch is not null) _dispatch(action);
        else action();
    }

    private void SetState(int cueId, MediaCueState state)
    {
        _states[cueId] = state;
        StateChanged?.Invoke(cueId, state);
    }

    private void EmitError(int cueId, string message)
    {
        _states[cueId] = MediaCueState.Error;
        StateChanged?.Invoke(cueId, MediaCueState.Error);
        ErrorOccurred?.Invoke(cueId, message);
    }
}
