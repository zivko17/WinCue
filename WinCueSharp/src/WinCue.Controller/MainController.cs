// =============================================================================
// MainController.cs — Controlador de reproducción estilo QLab (port de
// cue_controller.py)
// =============================================================================
//
// PARADIGMA
//   · PLAYHEAD (standby): índice del cue que GO disparará a continuación.
//   · CUES CONCURRENTES: GO dispara el cue del playhead y AVANZA el playhead,
//     SIN cortar los cues que ya están sonando.
//   · TEMPORIZACIÓN por cue: pre_wait → acción → (post_wait). El encadenado:
//       NONE          — nada automático (GO manual)
//       AUTO_CONTINUE — el siguiente se dispara tras pre_wait+post_wait
//       AUTO_FOLLOW   — el siguiente se dispara cuando ESTE termina su acción
//   · TRANSPORT: GO, Stop All, Panic (fade-out global), Pause/Resume.
//
// HILOS
//   Diseñado para un solo hilo (el de UI). Los motores y el scheduler remiten
//   sus callbacks por su dispatcher; el controlador nunca bloquea.
//
// RELOJ
//   Todo el tiempo de SHOW —plazos, elapsed, remaining, cuentas atrás— sale de
//   un único IShowClock, el del scheduler (ver ShowClock.cs). Ya no hay
//   Stopwatch suelto aquí. Los plazos de SECUENCIA (pre_wait, post_wait,
//   cadenas, WAIT/FADE) son ABSOLUTOS: se anclan al instante IDEAL, no al
//   disparo real, para que un timer que llega tarde no herede su retraso al
//   siguiente. Los plazos de MEDIA (fin de un audio o un vídeo) siguen
//   anclados al arranque REAL de la reproducción y corregidos contra la
//   posición del motor: el reloj del show no manda sobre el cristal de la
//   tarjeta de sonido.
// =============================================================================

using System.Diagnostics;   // Process, para los cues de SCRIPT (ya no para medir)
using WinCue.Core;

namespace WinCue.Controller;

public class MainController
{
    /// <summary>Duración por defecto del fade global de Panic (segundos).</summary>
    public const double PanicFade = 1.5;

    /// <summary>Cuánto retraso se está dispuesto a RECUPERAR comprimiendo las
    /// esperas de la cadena. Los plazos absolutos hacen que un disparo tardío
    /// no arrastre a los siguientes: si un eslabón llega 40 ms tarde, el
    /// siguiente sigue saliendo a su hora. Pero un parón GRANDE (la máquina se
    /// va varios segundos) no se recupera: intentarlo vaciaría media lista de
    /// golpe, que en directo es peor que ir tarde. Pasado este umbral la
    /// cadena se REANCLA al presente y sigue desde ahí, en sincronía con la
    /// realidad aunque haya perdido el retraso acumulado.</summary>
    public const double MaxRecuperacionSegundos = 2.0;

    /// <summary>Se emite cuando cambia el playhead.</summary>
    public event Action<int>? ActiveIndexChanged;

    /// <summary>Registro de eventos del show (GO/STOP/PANIC/carga…) para el log.</summary>
    public event Action<string>? ShowLog;

    // ------------------------------------------------------------------
    // Info de un cue en ejecución
    // ------------------------------------------------------------------

    private sealed class RunningCue
    {
        public required int CueId;
        public required int Index;
        public required CueType Kind;
        public required string Name;
        public required string Number;
        public required string Color;

        // ── Tiempos (todos en segundos del IShowClock del scheduler) ──────
        /// <summary>Instante REAL en que arrancó lo que se está midiendo
        /// (disparo, y luego la acción). En tiempo de SHOW (Now): se congela
        /// solo con la pausa global, así el elapsed no salta al reanudar.</summary>
        public double TStart;
        /// <summary>Instante IDEAL del disparo (NowRaw). Ancla de los plazos
        /// de secuencia de este cue: no hereda el retraso del timer que lo
        /// trajo, así una cadena larga no acumula deriva.</summary>
        public double Anchor;
        /// <summary>Instante IDEAL del arranque de la acción (= Anchor +
        /// pre_wait). Ancla del fin de los cues que NO son media.</summary>
        public double ActionAnchor;
        /// <summary>Fin IDEAL cuando lo marca el RELOJ y no un motor (WAIT,
        /// FADE, cues instantáneos). null en media: su fin lo dicta el motor,
        /// y encadenar desde el ideal cortaría lo que aún está sonando.</summary>
        public double? SequenceEnd;

        public double Duration;              // 0 = indeterminado (fin natural)
        public readonly List<IDisposable> Timers = new();
        public bool ManualStop;
        public bool AdvanceChain;
        public bool SelfPaused;              // pausa individual (no pause-all)
        public bool ActionStarted;           // false = aún en pre_wait
        public double Offset;                // arranque con seek (play-desde-cursor)
        public bool SuppressChain;           // la proyección ya trae la cadena resuelta
        public double? PreWaitOverride;      // espera programada del timeline (≠ pre_wait)
        public bool SuppressVisualFadeOut;   // crossfade misma pista: el entrante
                                             // lo cubre fundiendo — no oscurecer
    }

    /// <summary>Elemento de "reproducir desde el cursor del timeline":
    /// Offset &gt; 0 = el bloque cruza el cursor (arrancar YA con seek);
    /// Delay &gt; 0 = el bloque empieza más tarde (programar su acción).</summary>
    public sealed record TimelinePlayItem(int CueId, double Offset, double Delay);

    // ------------------------------------------------------------------
    // Estado
    // ------------------------------------------------------------------

    private readonly CueModel _model;
    private readonly IShowView _view;
    private readonly IAudioEngine _audio;
    private readonly IVideoEngine _video;
    private readonly IScheduler _scheduler;
    private readonly SettingsManager _settings;
    private readonly Random _random;
    private readonly ShowFileService _files;
    private readonly CueListEditor _listEditor;
    private readonly RemoteRouter _remote;
    private readonly UndoService _undo;
    private readonly CanvasLayout _canvas;

    private int _playhead;
    private bool _paused;
    private bool _lastHadRunning;

    private readonly Dictionary<int, RunningCue> _running = new();
    private readonly Dictionary<int, HashSet<int>> _rrState = new();   // RANDOM round-robin
    private readonly List<IDisposable> _chainTimers = new();

    // Anti doble-GO: instante del último GO aceptado, en tiempo de PARED
    // (NowRaw): es un gesto de mano, no debe congelarse con la pausa.
    // -infinito = todavía no ha habido ninguno.
    private double _lastGo = double.NegativeInfinity;

    // PUNTO DEL SHOW: el cue que salió al aire con el último GO, y dónde quedó
    // el playhead justo después. GoCue dispara y LUEGO avanza, así que mientras
    // suena un cue el playhead ya está en el siguiente: para volver al cue que
    // sonaba (STOP ALL, PANIC, FADE ALL) hay que recordar cuál era.
    // Se guarda el ID, no el índice: insertar o borrar cues renumera.
    private int _showPointCueId = -1;
    private int _playheadAfterGo = -1;

    public int Playhead => _playhead;
    public bool IsPaused => _paused;
    public IReadOnlyCollection<int> RunningCueIds => _running.Keys;

    /// <summary>EL reloj del show (ver ShowClock.cs). Lo aporta el scheduler
    /// para que la secuencia y la medición no puedan discrepar. La vista lee
    /// de aquí el reloj grande y el cursor del timeline, y así lo que se ve en
    /// pantalla y lo que hace el secuenciador son el mismo tiempo.</summary>
    public IShowClock Clock => _scheduler.Clock;

    // ------------------------------------------------------------------
    // Construcción
    // ------------------------------------------------------------------

    public MainController(
        CueModel model,
        IShowView view,
        IAudioEngine audio,
        IVideoEngine video,
        IScheduler scheduler,
        SettingsManager settings,
        Random? random = null,
        IHapConversionRunner? hapRunner = null)
    {
        _model = model;
        _view = view;
        _audio = audio;
        _video = video;
        _scheduler = scheduler;
        _settings = settings;
        _random = random ?? new Random();

        _audio.LatencyReport += OnLatencyReport;
        _audio.ErrorOccurred += OnAudioError;
        _audio.StateChanged += OnAudioStateChanged;
        _audio.LevelReport += (l, r) => _view.UpdateVuMeter(l, r);
        // Los ids espejo del motor HAP se REMAPEAN al cue real: el strip del
        // Mixer y el inspector muestran el VU del cue de vídeo, no el interno
        _audio.CueLevelReport += (cueId, l, r) => _view.UpdateCueVuMeter(
            cueId <= VideoAudioBase ? VideoAudioBase - cueId : cueId, l, r);

        _video.LatencyReport += OnLatencyReport;
        _video.ErrorOccurred += OnVideoError;
        _video.StateChanged += OnVideoStateChanged;
        _video.SetWindowHandleProvider(() => _view.GetVideoOutputHandle());
        _video.SetLayerHandleProvider(
            (screen, layer) => _view.GetVideoOutputLayerHandle(screen, layer));

        // Colocación de vídeo extraída (paso 5): canvas, recortes por salida,
        // geometría y factoría de VideoPlayParams. Solo lee settings/vista.
        _canvas = new CanvasLayout(settings, view);

        // Undo/redo extraído (paso 4): el controlador envuelve Push() en
        // PushUndo() para mantener el flag HasUnsavedChanges en su sitio.
        _undo = new UndoService(
            model, view,
            getPlayhead: () => _playhead,
            setPlayhead: SetPlayhead);

        // Persistencia extraída (paso 1 de la auditoría 02): el servicio recibe
        // como callbacks lo poco que necesita del núcleo — el controlador sigue
        // siendo el único dueño del estado de reproducción.
        _files = new ShowFileService(
            model, view,
            log: Log,
            pushUndo: PushUndo,
            beforeLoad: () =>
            {
                StopEverything(fade: 0.0);
                _video.DiscardStandby();   // los ids del show anterior ya no valen
            },
            setPlayhead: SetPlayhead,
            // El buffer del cue Y el de su espejo de vídeo (HAP/híbrido):
            // ambos pueden estar precargados con el archivo antiguo
            invalidateAudioCache: id =>
            {
                _audio.InvalidateCache(id);
                _audio.InvalidateCache(VideoAudioId(id));
            });

        // Edición de lista extraída (paso 2): manipulación pura del modelo.
        // Los dos únicos contactos con el estado de reproducción entran como
        // callbacks: parar un cue sonando antes de borrarlo y olvidar el
        // round-robin de un grupo deshecho.
        _listEditor = new CueListEditor(
            model, view,
            pushUndo: PushUndo,
            setPlayhead: SetPlayhead,
            stopIfRunning: cueId =>
            {
                if (_running.ContainsKey(cueId))
                    EndCue(cueId, markStopped: false);
            },
            forgetGroupState: groupId => _rrState.Remove(groupId));

        // Ruteo remoto extraído (paso 3): OSC/MIDI/MTC/wall-clock parsean y
        // despachan hacia estas acciones — nunca mutan _running directamente.
        _remote = new RemoteRouter(
            model, settings, view,
            log: Log,
            go: OnGoPressed,
            stopAll: OnStopPressed,
            panic: OnPanic,
            fadeAll: OnFadeAll,
            pauseToggle: OnPauseToggle,
            triggerCueId: TriggerCueId,
            triggerAt: idx => Trigger(idx, advanceChain: false),
            stopCue: cueId => EndCue(cueId, markStopped: true),
            isRunning: _running.ContainsKey);

        // Conversión de vídeos a HAP desde la app (alimenta el motor propio):
        // cola secuencial, sin bloquear la UI, con progreso por FlashStatus.
        _hapConvert = new HapConvertService(
            hapRunner ?? new FfmpegHapConversionRunner(), scheduler,
            onProgress: OnHapConvertProgress,
            onCompleted: OnHapConvertCompleted);

        SetPlayhead(0);
    }

    // ==================================================================
    // TRANSPORT — API pública conectada a la vista
    // ==================================================================

    /// <summary>GO: dispara el cue del playhead y avanza, sin cortar los activos.
    /// Protegido contra el doble-tap accidental (go_min_interval_ms).</summary>
    public void OnGoPressed()
    {
        int minMs = _settings.Get("go_min_interval_ms", 200);
        double now = Clock.NowRaw;
        if (minMs > 0 && (now - _lastGo) * 1000.0 < minMs)
            return;   // doble-tap: ignorar
        _lastGo = now;

        var cue = _model.GetCue(_playhead);
        if (cue is null)
            return;
        Log($"GO → {cue.DisplayNumber} · {cue.Name}");
        GoCue(_playhead);
    }

    /// <summary>Stop All: detiene todos los cues y devuelve el playhead al cue
    /// que estaba al aire, para que el GO siguiente reanude ahí mismo.</summary>
    public void OnStopPressed()
    {
        Log("STOP ALL");
        StopEverything(fade: 0.0, rewindToShowPoint: true);
        _view.FlashStatus(Loc.T("■ STOP ALL — todos los cues detenidos", "■ STOP ALL — all cues stopped"));
    }

    // Igual que el anti doble-GO: gesto de mano, en tiempo de PARED (NowRaw).
    private double _lastPanic = double.NegativeInfinity;

    /// <summary>Panic: fundido de salida global (panic_fade, 1,5 s por defecto)
    /// y parada de todo. Con panic_action = "image", muestra la imagen de
    /// respaldo en las salidas visibles (se quita sola al siguiente GO de
    /// vídeo). Segunda pulsación en &lt;2 s = CORTE DURO (panic_double_hard).</summary>
    public void OnPanic()
    {
        double fade = Math.Clamp(_settings.Get("panic_fade", PanicFade), 0.0, 10.0);
        bool showImage = _settings.Get("panic_action", "black") == "image";

        double nowRaw = Clock.NowRaw;
        bool doublePress = _settings.Get("panic_double_hard", true) &&
            nowRaw - _lastPanic < 2.0;
        _lastPanic = nowRaw;

        if (doublePress)
        {
            Log("PANIC (doble — corte duro)");
            StopEverything(fade: 0.0, rewindToShowPoint: true);
            if (showImage)
                _view.ShowBackupSlate(0.15);
            _view.FlashStatus(Loc.T("⚠⚠ PANIC ×2 — CORTE DURO", "⚠⚠ PANIC ×2 — HARD CUT"));
            return;
        }

        Log("PANIC");
        StopEverything(fade: fade, rewindToShowPoint: true);
        if (showImage)
            _view.ShowBackupSlate(fade);
        _view.FlashStatus(showImage
            ? Loc.T($"⚠ PANIC — fundido de {fade:0.#}s a imagen de respaldo", $"⚠ PANIC — {fade:0.#}s fade to backup image")
            : Loc.T($"⚠ PANIC — fundido de {fade:0.#}s y parada", $"⚠ PANIC — {fade:0.#}s fade and stop"));
    }

    /// <summary>FADE ALL: fundido elegante de todo (distinto del PANIC de
    /// emergencia). El tiempo se configura en fade_all_seconds.</summary>
    public void OnFadeAll()
    {
        double t = Math.Max(0.1, _settings.Get("fade_all_seconds", 5.0));
        Log($"FADE ALL ({t:0.#}s)");
        StopEverything(fade: t, rewindToShowPoint: true);
        _view.FlashStatus(Loc.T($"◢ FADE ALL — fundido de {t:0.#}s", $"◢ FADE ALL — {t:0.#}s fade"));
    }

    /// <summary>Id reservado para la audición local (nunca colisiona con cues).</summary>
    public const int AuditionCueId = -9999;

    // ==================================================================
    // AUDIO ESPEJO de los cues de vídeo (pasos 3 y 3b): la pista de AUDIO
    // del vídeo suena por el AudioEngine (rack completo: EQ, compresor,
    // VU por cue, limitador) con un id espejo negativo. Dos rutas la usan:
    //   · MOTOR HAP PROPIO — el vídeo va por WinCue.Video.Hap y el audio
    //     espejo actúa de RELOJ MAESTRO del vídeo.
    //   · HÍBRIDO mpv — vídeo no-HAP por mpv MUDO (aid=no) disciplinado al
    //     reloj del audio espejo con un PLL suave (MpvSyncPll).
    // La App inyecta el provider de reloj en el engine para ambas.
    // ==================================================================

    /// <summary>Base de los ids espejo de audio de los cues de vídeo. Por
    /// debajo de AuditionCueId: jamás colisiona con cues ni con la audición.</summary>
    public const int VideoAudioBase = -20000;

    /// <summary>Id espejo de audio del cue de vídeo (cueId ≥ 0).</summary>
    public static int VideoAudioId(int cueId) => VideoAudioBase - cueId;

    /// <summary>Cues de vídeo con AUDIO ESPEJO activo (motor HAP propio o
    /// híbrido mpv): su audio suena por el id espejo y hay que arrastrarlo
    /// en stop/pausa/fades/volumen/EQ en vivo.</summary>
    private readonly HashSet<int> _mirroredAudio = new();

    /// <summary>true si el cue puede ir por el motor HAP propio: HAP en
    /// contenedor .mov, rate 1.0, compositor activo, modo clásico (sin
    /// canvas) y geometría FULL. WINCUE_HAP_ENGINE=0 lo desactiva en
    /// caliente (red de seguridad de directo → ruta clásica mpv de siempre).</summary>
    private bool HapEngineEligible(Cue cue) =>
        Environment.GetEnvironmentVariable("WINCUE_HAP_ENGINE") != "0"
        && string.Equals(Path.GetExtension(cue.FilePath), ".mov", StringComparison.OrdinalIgnoreCase)
        && Math.Abs(RateOf(cue) - 1.0) < 0.001
        && _settings.Get("video_compositor", false)
        && !CanvasMode
        && cue.GeoMode != GeoMode.Custom;

    /// <summary>true si el cue de vídeo NO-HAP (lo reproduce mpv) es elegible
    /// para la RUTA HÍBRIDA (paso 3b): su audio por el AudioEngine (rack
    /// nativo) con mpv MUDO (aid=no) disciplinado por el PLL. Condiciones:
    ///   · no es imagen (no hay audio que enrutar);
    ///   · rate 1.0 — el varispeed retima el audio en mpv (resample+pitch);
    ///     el espejo no lo replica → ruta clásica CON audio, como siempre;
    ///   · duración CONOCIDA (el PLL y los timers de fin la necesitan) y
    ///     dentro del techo de MirrorAudioMaxSeconds (con el STREAMING del
    ///     paso 4 no hay techo: un largometraje ya puede llevar el rack).
    /// Kill-switch de directo: WINCUE_VIDEO_AUDIO=mpv desactiva la ruta
    /// entera (audio por mpv como siempre).</summary>
    private bool HybridAudioEligible(Cue cue) =>
        Environment.GetEnvironmentVariable("WINCUE_VIDEO_AUDIO") != "mpv"
        && !MediaImporter.ImageFormats.Contains(Path.GetExtension(cue.FilePath))
        && Math.Abs(RateOf(cue) - 1.0) < 0.001
        && cue.Duration > 0.0
        && cue.Duration <= MirrorAudioMaxSeconds();

    /// <summary>Techo de duración para el AUDIO ESPEJO. Con STREAMING (paso
    /// 4) el espejo ya no decodifica el archivo entero a RAM, así que
    /// `video_audio_max_minutes` pasa a 0 = SIN LÍMITE (nuevo default) y solo
    /// actúa como OPT-OUT explícito (&gt; 0 → techo en minutos). Si el
    /// kill-switch WINCUE_MIRROR_STREAMING=0 fuerza la ruta RAM (decode
    /// entero, ~23 MB/min), vuelve el techo prudente clásico de 20 min salvo
    /// ajuste explícito del usuario.</summary>
    private double MirrorAudioMaxSeconds()
    {
        double maxMin = _settings.Get("video_audio_max_minutes", 0.0);
        if (maxMin > 0.0)
            return maxMin * 60.0;
        bool streaming =
            Environment.GetEnvironmentVariable("WINCUE_MIRROR_STREAMING") != "0";
        return streaming ? double.PositiveInfinity : 20.0 * 60.0;
    }

    /// <summary>Parámetros del AUDIO ESPEJO de un cue de vídeo: los mismos
    /// que un cue de audio (trims/loop/fades/EQ; rate SIEMPRE 1.0 — la
    /// elegibilidad lo garantiza) y Streaming=true (paso 4: ffmpeg → ring
    /// acotado; el engine cae solo a RAM si el spawn falla).</summary>
    private static AudioPlayParams MirrorPlayParams(Cue cue, double offset, double fadeIn) => new(
        Volume: cue.Volume,
        FadeIn: fadeIn,
        FadeOut: cue.FadeOut,
        Loop: cue.Loop,
        TrimStart: cue.TrimStart + offset, TrimEnd: cue.TrimEnd,
        PlayCount: cue.PlayCount, OutputDevice: cue.AudioOutput,
        Eq: EqFor(cue), Rate: 1.0,
        Streaming: true);

    // ------------------------------------------------------------------
    // Conversión a HAP desde la app (preparación de show, no de directo)
    // ------------------------------------------------------------------

    private readonly HapConvertService _hapConvert;
    private int _lastHapConvertPct = -1;   // anti-spam del FlashStatus de progreso

    /// <summary>true si el archivo del cue es un vídeo convertible a HAP:
    /// existe, no es imagen y no es ya HAP. Lo usa el menú contextual de la
    /// tabla para decidir visibilidad (el sondeo de códec va cacheado).</summary>
    public static bool IsHapConvertible(Cue cue) =>
        cue.Type == CueType.Video && cue.FilePath.Length > 0
        && File.Exists(cue.FilePath)
        && !MediaImporter.ImageFormats.Contains(Path.GetExtension(cue.FilePath))
        && !MediaImporter.IsUnsupportedVideoCodec(cue.FilePath);

    /// <summary>Encola la conversión a HAP del cue de la fila (menú contextual
    /// de la tabla). El cue sigue reproducible por su archivo original hasta
    /// que la conversión termine y se commitee la ruta nueva.</summary>
    public void ConvertCueToHap(int row)
    {
        var cue = _model.GetCue(row);
        if (cue is null || !IsHapConvertible(cue))
        {
            _view.FlashStatus(Loc.T(
                "Convertir a HAP: selecciona un cue de VÍDEO cuyo archivo no sea ya HAP",
                "Convert to HAP: select a VIDEO cue whose file is not already HAP"));
            return;
        }
        if (!_hapConvert.Enqueue(cue.Id, cue.FilePath))
        {
            _view.FlashStatus(Loc.T(
                $"«{cue.Name}» ya está en la cola de conversión a HAP",
                $"«{cue.Name}» is already in the HAP conversion queue"));
            return;
        }
        Log($"Conversión a HAP encolada: {cue.Name} ({cue.FilePath})");
        _view.FlashStatus(Loc.T(
            $"🎞 Conversión a HAP encolada: {cue.Name}",
            $"🎞 HAP conversion queued: {cue.Name}"));
    }

    /// <summary>Acción de show: encola TODOS los vídeos convertibles (no
    /// imagen, no ya-HAP). Devuelve cuántos se encolaron.</summary>
    public int ConvertAllVideosToHap()
    {
        int queued = 0;
        foreach (var cue in _model.Cues)
            if (IsHapConvertible(cue) && _hapConvert.Enqueue(cue.Id, cue.FilePath))
                queued++;
        Log($"Conversión a HAP de todo el show: {queued} vídeo(s) encolados");
        _view.FlashStatus(queued > 0
            ? Loc.T($"🎞 Convirtiendo a HAP: {queued} vídeo(s) en cola",
                    $"🎞 Converting to HAP: {queued} video(s) queued")
            : Loc.T("No hay vídeos que convertir a HAP (¿ya son HAP?)",
                    "No videos to convert to HAP (already HAP?)"));
        return queued;
    }

    /// <summary>Cancela la cola de conversiones y el ffmpeg en curso.</summary>
    public void CancelHapConversions() => _hapConvert.CancelAll();

    private void OnHapConvertProgress(HapConvertService.Job job, double pct)
    {
        int p = (int)pct;
        if (p == _lastHapConvertPct)
            return;   // -progress emite cada ~0,5 s; solo refrescar al cambiar el %
        _lastHapConvertPct = p;
        string name = _model.GetById(job.CueId)?.Name ?? Path.GetFileName(job.Source);
        _view.FlashStatus(Loc.T(
            $"🎞 Convirtiendo a HAP: {name} — {p}%",
            $"🎞 Converting to HAP: {name} — {p}%"));
    }

    private void OnHapConvertCompleted(
        HapConvertService.Job job, HapConversionResult result, bool reused)
    {
        _lastHapConvertPct = -1;
        var cue = _model.GetById(job.CueId);
        string name = cue?.Name ?? Path.GetFileName(job.Source);

        if (!result.Ok)
        {
            Log($"Conversión a HAP FALLIDA: {name} — {result.Error}" +
                (result.LogFile.Length > 0 ? $" (stderr en {result.LogFile})" : ""));
            _view.FlashStatus(Loc.T(
                $"⚠ Conversión a HAP falló: {name} — {result.Error}",
                $"⚠ HAP conversion failed: {name} — {result.Error}"));
            return;
        }
        if (cue is null)   // el cue se borró mientras convertía: el .hap.mov queda en disco
        {
            Log($"Conversión a HAP terminada pero el cue ya no existe: {job.Dest}");
            return;
        }

        // El destino es un archivo NUEVO (o reescrito): tirar su sondeo cacheado
        // para que el commit lo re-sondee de verdad (AssignToCue calienta la caché
        // y el siguiente GO enruta por el motor propio sin ffprobe en caliente).
        MediaImporter.InvalidateCodecProbe(job.Dest);

        int row = _model.IndexOfId(job.CueId);
        if (row < 0)
            return;
        // RUTA DE EDICIÓN OFICIAL (la misma que usa el inspector al cambiar de
        // archivo): undo, tipo, duración por ffprobe y refresco de fila/timeline
        // vienen solos.
        OnCueEditCommitted(row,
            new Dictionary<string, object?> { ["file_path"] = job.Dest });
        // Cachés que ese commit no toca: los buffers de audio del cue y de su
        // espejo HAP podían estar precargados con el archivo antiguo, y el
        // standby de vídeo apuntaba al original.
        _audio.InvalidateCache(cue.Id);
        _audio.InvalidateCache(VideoAudioId(cue.Id));
        _video.DiscardStandby(cue.Id);
        if (row == _playhead && !_running.ContainsKey(cue.Id) && cue.FilePath.Length > 0)
            PrepareVideoStandby(cue);

        Log(reused
            ? $"Conversión a HAP: {name} reutiliza {job.Dest} (ya existía)"
            : $"Conversión a HAP terminada: {name} → {job.Dest}");
        _view.FlashStatus(reused
            ? Loc.T($"🎞 «{name}»: ya estaba convertido — usando {Path.GetFileName(job.Dest)} (motor propio en el siguiente GO)",
                    $"🎞 «{name}»: already converted — using {Path.GetFileName(job.Dest)} (native engine on next GO)")
            : Loc.T($"🎞 «{name}» convertido a HAP — motor propio en el siguiente GO",
                    $"🎞 «{name}» converted to HAP — native engine on next GO"));
    }

    /// <summary>Cue que suena por la audición local (para EQ/RTA en vivo).</summary>
    private int _auditionSourceCueId = -1;

    /// <summary>Id del cue que está SONANDO POR LOS CASCOS ahora mismo, o −1.
    /// Lo usan los botones CUE de la mesa y del inspector para encenderse.
    ///
    /// Cuenta las DOS rutas: el pinchazo en vivo (PFL, el botón CUE) y la
    /// audición clásica que relanza el archivo (menú de la lista).</summary>
    public int AuditionSourceCueId
    {
        get
        {
            int pfl = _audio.PflCueId;
            if (pfl != -1)
                return pfl <= VideoAudioBase ? VideoAudioBase - pfl : pfl;
            return _audio.IsPlaying(AuditionCueId) ? _auditionSourceCueId : -1;
        }
    }

    /// <summary>Qué fuente está sonando por el bus CUE ahora mismo. Las dos van
    /// al mismo oído y se relevan, así que saber CUÁL es lo que evita el
    /// «¿por qué suena esto?» — y es información, no un modo que recordar.</summary>
    public CueBusSource CueBusNow =>
        _audio.PflCueId != -1 ? CueBusSource.Pfl
        : _audio.IsPlaying(AuditionCueId) ? CueBusSource.Preview
        : CueBusSource.None;

    /// <summary>Botón CUE: pincha la señal VIVA de ese cue en los cascos. Es un
    /// toggle. Devuelve false si no se pudo — el cue no suena, o su audio no
    /// pasa por el motor (vídeo cuyo sonido saca mpv) y no hay nada que
    /// derivar. En ese caso no se hace nada: fingir una preescucha con una
    /// reproducción nueva desde el principio es justo lo que NO es un CUE.</summary>
    public bool PflToggle(int cueId)
    {
        // El audio de un cue de VÍDEO va por su id ESPEJO: es ahí donde hay
        // muestras que pinchar, no en el id del cue.
        int audioId = _running.ContainsKey(cueId) && _mirroredAudio.Contains(cueId)
            ? VideoAudioId(cueId)
            : cueId;

        // El toggle mira EL PINCHAZO, no «este cue está en mis cascos por la
        // vía que sea». Mirando lo segundo, pulsar CUE sobre el cue que estabas
        // PREVISUALIZANDO se entendía como «ya lo tienes, apágalo»: soltaba un
        // pinchazo que no existía y dejaba el preview sonando.
        if (_audio.PflCueId == audioId)
        {
            _audio.ClearPfl();
            _view.FlashStatus(Loc.T("🎧 CUE fuera", "🎧 CUE off"));
            return true;
        }
        string device = _settings.Get("audition_device_name", "");
        if (device.Length == 0)
        {
            // Sin cascos, la preescucha no tiene dónde ir. Se dice DÓNDE se
            // arregla: un «no se puede» sin salida obliga a buscarla a ciegas.
            _view.FlashStatus(Loc.T(
                "CUE: falta el dispositivo de audición (Ajustes → Audio)",
                "CUE: no audition device set (Settings → Audio)"));
            return false;
        }
        bool ok = _audio.SetPfl(audioId, device);
        if (ok)
        {
            // Y el pinchazo releva al preview, por lo mismo que el preview
            // releva al pinchazo: una fuente cada vez en el oído.
            if (_audio.IsPlaying(AuditionCueId))
            {
                _audio.Stop(AuditionCueId);
                _auditionSourceCueId = -1;
            }
            _view.FlashStatus(Loc.T("🎧 CUE — escuchando la señal en vivo",
                                    "🎧 CUE — listening to the live signal"));
            return true;
        }
        // Dos motivos posibles, y conviene distinguirlos: o el cue no está
        // sonando por el rack, o el dispositivo de cascos ya no está.
        _view.FlashStatus(_running.ContainsKey(cueId)
            ? Loc.T($"CUE: el dispositivo de audición «{device}» no está disponible",
                    $"CUE: audition device “{device}” is not available")
            : Loc.T("CUE: ese cue no está sonando (no hay señal que pinchar)",
                    "CUE: that cue is not playing (no live signal to tap)"));
        return false;
    }

    /// <summary>Audición local: reproduce el cue de la fila por el dispositivo
    /// de audición (cascos) SIN pasar por el flujo del show. Toggle.</summary>
    public void AuditionToggle(int row)
    {
        if (_audio.IsPlaying(AuditionCueId))
        {
            _audio.Stop(AuditionCueId);
            _auditionSourceCueId = -1;
            _view.FlashStatus(Loc.T("🎧 Preview detenido", "🎧 Preview stopped"));
            return;
        }
        var cue = _model.GetCue(row);
        if (cue is null || cue.Type != CueType.Audio || cue.FilePath.Length == 0)
        {
            _view.FlashStatus(Loc.T("Preview: selecciona un cue de AUDIO con archivo", "Preview: select an AUDIO cue with a file"));
            return;
        }
        // EL PREVIEW RELEVA AL PINCHAZO. Las dos fuentes van al mismo oído: con
        // las dos sonando a la vez no se distingue cuál es cuál, y el preview es
        // lo que acabas de pedir expresamente. No vuelve solo al terminar —
        // volver a pulsar CUE es un gesto, dar por hecho un estado no lo es.
        _audio.ClearPfl();
        _auditionSourceCueId = cue.Id;
        _audio.Play(AuditionCueId, cue.FilePath, new AudioPlayParams(
            Volume: cue.Volume * AuditionTrim, TrimStart: cue.TrimStart, TrimEnd: cue.TrimEnd,
            OutputDevice: _settings.Get("audition_device_name", ""),
            Eq: EqFor(cue),
            // FUERA DEL MASTER: la preescucha es del técnico, no de la sala.
            // Sin esto, bajar el master bajaba también los cascos.
            BypassMaster: true));
        _view.FlashStatus(Loc.T($"🎧 Preview: {cue.Name}", $"🎧 Preview: {cue.Name}"));
    }

    /// <summary>Trim del fader de audición (0..1) del panel Audio: se aplica
    /// SOBRE el volumen del cue auditado (efectivo = cue × trim).</summary>
    private double AuditionTrim =>
        Math.Clamp(_settings.Get("audition_volume", 100) / 100.0, 0.0, AudioLevels.MaxGain);

    /// <summary>Fader de audición del panel Audio (0-100). Se aplica EN VIVO
    /// si hay una audición sonando; persist=true además guarda en settings
    /// (el panel lo pide con debounce al final del arrastre).</summary>
    public void OnAuditionVolume(int value, bool persist = true)
    {
        value = Math.Clamp(value, 0, (int)Math.Round(AudioLevels.MaxGain * 100.0));
        if (persist)
            _settings.Set("audition_volume", value);
        // El fader CUE es el master del BUS: manda sobre las dos fuentes, el
        // pinchazo en vivo (PFL) y el preview. Antes solo tocaba el preview, y
        // el mismo fader hacía una cosa u otra según qué estuvieras oyendo.
        _audio.SetPflBusGain(value / 100.0);
        if (!_audio.IsPlaying(AuditionCueId))
            return;
        int row = _model.IndexOfId(_auditionSourceCueId);
        double cueVol = _model.GetCue(row)?.Volume ?? 1.0;
        _audio.SetVolume(AuditionCueId, cueVol * value / 100.0);
    }

    /// <summary>Cadena de EQ del cue para el motor (ganancia + filtros).</summary>
    private static AudioEqParams EqFor(Cue c) => new(
        GainDb: c.GainDb,
        EqEnabled: c.EqEnabled,
        HpfOn: c.HpfOn, HpfFreq: c.HpfFreq,
        LpfOn: c.LpfOn, LpfFreq: c.LpfFreq,
        Bands: new[]
        {
            new EqBand(c.Eq1Freq, c.Eq1Gain, c.Eq1Q),
            new EqBand(c.Eq2Freq, c.Eq2Gain, c.Eq2Q),
            new EqBand(c.Eq3Freq, c.Eq3Gain, c.Eq3Q),
            new EqBand(c.Eq4Freq, c.Eq4Gain, c.Eq4Q),
        },
        Comp: new AudioCompParams(
            c.CompEnabled, c.CompThreshold, c.CompRatio,
            c.CompAttack, c.CompRelease, c.CompMakeup));

    /// <summary>Espectro para el RTA del inspector: del cue sonando, o de su
    /// audición local si es lo que está sonando.</summary>
    public float[]? GetAudioSpectrum(int cueId)
    {
        var s = _audio.GetSpectrum(cueId);
        if (s is null && cueId == _auditionSourceCueId)
            s = _audio.GetSpectrum(AuditionCueId);
        if (s is null && _mirroredAudio.Contains(cueId))
            s = _audio.GetSpectrum(VideoAudioId(cueId));   // audio espejo HAP
        return s;
    }

    /// <summary>Reducción de ganancia del compresor (dB ≥ 0) para el medidor
    /// GR del inspector: del cue sonando, o de su audición local.</summary>
    public float GetAudioGainReduction(int cueId)
    {
        float gr = _audio.GetGainReductionDb(cueId);
        if (gr <= 0f && cueId == _auditionSourceCueId)
            gr = _audio.GetGainReductionDb(AuditionCueId);
        if (gr <= 0f && _mirroredAudio.Contains(cueId))
            gr = _audio.GetGainReductionDb(VideoAudioId(cueId));   // espejo HAP
        return gr;
    }

    /// <summary>Reducción de ganancia del limitador del bus master (dB ≥ 0)
    /// para el medidor GR del panel Audio.</summary>
    public float GetMasterGainReduction() => _audio.GetMasterGainReductionDb();

    /// <summary>true si el audio de este cue pasa (o pasará) por el RACK
    /// NATIVO y por tanto se puede MEDIR: VU por cue, RTA del analizador y
    /// medidor GR del compresor.
    ///   · AUDIO — siempre (es el rack de siempre).
    ///   · VÍDEO — solo si su audio va por el ESPEJO (motor HAP propio o ruta
    ///     híbrida). Si lo saca mpv (varispeed, kill-switch, imagen, sin
    ///     compositor…) la EQ/compresor SÍ se aplican —cadena af de mpv— pero
    ///     no hay nada que medir: el inspector debe decirlo en vez de enseñar
    ///     un analizador muerto.
    ///   · El resto de tipos (MEMO/WAIT/FADE…) no tienen audio.</summary>
    public bool CueHasAudioAnalysis(int cueId)
    {
        if (_mirroredAudio.Contains(cueId))
            return true;                       // espejo sonando ahora mismo
        if (_model.GetById(cueId) is not Cue cue)
            return false;
        if (cue.Type == CueType.Audio)
            return true;
        if (cue.Type != CueType.Video || cue.FilePath.Length == 0)
            return false;
        if (_running.ContainsKey(cueId))
            return false;                      // ya suena y NO está espejado → mpv
        return WillMirrorAudio(cue);
    }

    /// <summary>Decisión de enrutado del audio de un cue de vídeo ANTES del GO
    /// — la MISMA que aplica Trigger: HAP → motor propio si es elegible;
    /// no-HAP → ruta híbrida si es elegible.</summary>
    private bool WillMirrorAudio(Cue cue) =>
        MediaImporter.IsUnsupportedVideoCodec(cue.FilePath)
            ? HapEngineEligible(cue)
            : HybridAudioEligible(cue);

    /// <summary>Detiene un cue por id (botón ■ del panel de activos).</summary>
    public void StopCueById(int cueId)
    {
        if (_running.ContainsKey(cueId))
        {
            Log($"STOP → cue id {cueId}");
            EndCue(cueId, markStopped: true);
        }
    }

    /// <summary>Pausa/reanuda SOLO el cue de la fila dada (media en ejecución).
    /// Los timers de duración se congelan y reprograman al reanudar.</summary>
    public void OnPauseCueToggle(int row)
    {
        var cue = _model.GetCue(row);
        if (cue is null || !cue.IsMedia() ||
            !_running.TryGetValue(cue.Id, out var run))
            return;

        if (!run.SelfPaused)
        {
            run.SelfPaused = true;
            if (cue.Type == CueType.Audio) _audio.PauseCue(cue.Id);
            else _video.PauseCue(cue.Id);
            if (_mirroredAudio.Contains(cue.Id))
                _audio.PauseCue(VideoAudioId(cue.Id));   // el audio espejo también
            // Congelar el timer de duración (si lo hay)
            foreach (var t in run.Timers) t.Dispose();
            run.Timers.Clear();
            _view.FlashStatus(Loc.T($"⏸ {cue.DisplayNumber} en pausa", $"⏸ {cue.DisplayNumber} paused"));
        }
        else
        {
            run.SelfPaused = false;
            double elapsed;
            if (cue.Type == CueType.Audio)
            {
                _audio.ResumeCue(cue.Id);
                elapsed = _audio.GetElapsed(cue.Id);
            }
            else
            {
                if (_mirroredAudio.Contains(cue.Id))
                    _audio.ResumeCue(VideoAudioId(cue.Id));   // el reloj vuelve
                _video.ResumeCue(cue.Id);
                elapsed = _video.GetElapsed(cue.Id);
            }
            // Reprogramar el resto de la duración fijada
            if (run.Duration > 0.0 && !cue.Loop)
            {
                double remaining = Math.Max(0.0, run.Duration - elapsed);
                Schedule(run, (int)(remaining * 1000), () => DurationElapsed(cue.Id));
            }
            _view.FlashStatus(Loc.T($"▶ {cue.DisplayNumber} reanudado", $"▶ {cue.DisplayNumber} resumed"));
        }
    }

    /// <summary>Volumen master global en GANANCIA POR CIENTO (100 = 0 dB, hasta
    /// +10 dB). El tope estaba en la unidad y recortaba en silencio todo lo que
    /// el fader dejaba subir por encima de 0 dB.</summary>
    public void OnMasterVolume(int value)
    {
        double m = Math.Clamp(value / 100.0, 0.0, AudioLevels.MaxGain);
        _audio.SetMasterVolume(m);
        _video.SetMasterVolume(m);
    }

    /// <summary>Pausa/reanuda todos los motores de reproducción.</summary>
    public void OnPauseToggle()
    {
        if (_running.Count == 0)
            return;
        if (_paused)
        {
            _audio.ResumeAll();
            _video.ResumeAll();
            // El countdown no salta porque el TIEMPO DE SHOW se reanuda donde
            // se quedó: antes había que corregir la marca de cada cue hacia
            // adelante (TStartTicks += dt) y el que hubiera arrancado DURANTE
            // la pausa se iba al futuro (elapsed negativo). Ahora es el reloj
            // el que se congela, y todos los elapsed con él.
            Clock.Resume();
            _paused = false;
        }
        else
        {
            _audio.PauseAll();
            _video.PauseAll();
            Clock.Freeze();
            _paused = true;
        }
        _view.UpdateTransport(_paused);
    }

    /// <summary>Detiene el cue de la fila dada, si está en ejecución.</summary>
    public void OnStopSelected(int row)
    {
        var cue = _model.GetCue(row);
        if (cue is not null && _running.ContainsKey(cue.Id))
            EndCue(cue.Id, markStopped: true);
    }

    public void OnPrevPressed()
    {
        if (_model.Count > 0)
            SetPlayhead(Math.Max(0, _playhead - 1));
    }

    public void OnNextPressed()
    {
        if (_model.Count > 0)
            SetPlayhead(Math.Min(_model.Count - 1, _playhead + 1));
    }

    /// <summary>Salta el playhead a un cue por posición (1-based). No dispara.</summary>
    public void OnJumpToCue(int cueNumber)
    {
        int index = cueNumber - 1;
        if (index >= 0 && index < _model.Count)
            SetPlayhead(index);
    }

    /// <summary>Fija el playhead a la fila dada (p.ej. clic del usuario).</summary>
    public void OnSetPlayhead(int row)
    {
        if (row >= 0 && row < _model.Count)
            SetPlayhead(row);
    }

    // ==================================================================
    // CONTROL REMOTO — implementación en RemoteRouter (OSC/MIDI/MTC/reloj)
    // ==================================================================

    /// <summary>Rutea un mensaje OSC entrante a la acción correspondiente.</summary>
    public void OnOscMessage(string address, IReadOnlyList<object?> args) =>
        _remote.OnOscMessage(address, args);

    /// <summary>Mensaje MIDI entrante: notas globales de GO/STOP y triggers por cue.</summary>
    public void OnMidiMessage(MidiTriggerType type, int channel, int data1, int data2) =>
        _remote.OnMidiMessage(type, channel, data1, data2);

    /// <summary>Posición de timecode entrante (segundos desde 00:00:00:00).</summary>
    public void OnTimecode(double seconds) => _remote.OnTimecode(seconds);

    /// <summary>"HH:MM:SS:FF" (frames a fps dado), "HH:MM:SS.mmm" o "MM:SS".</summary>
    public static double? ParseTimecode(string text, double fps = 30.0) =>
        RemoteRouter.ParseTimecode(text, fps);

    // ==================================================================
    // NÚCLEO DE DISPARO
    // ==================================================================

    /// <param name="anchor">Instante IDEAL de este disparo (NowRaw). null = un
    /// GO de mano, que ocurre cuando ocurre. Con valor viene de una CADENA: es
    /// el plazo teórico del eslabón, no el momento en que el timer despertó —
    /// así el retraso de un eslabón no se hereda al siguiente.</param>
    private void GoCue(int idx, double? anchor = null)
    {
        var cue = _model.GetCue(idx);
        if (cue is null)
            return;

        if (cue.Type == CueType.Group)
        {
            GoGroup(idx, cue, anchor);
            return;
        }

        // DISPARAR ANTES DE AVANZAR: mover el playhead prepara el standby del
        // SIGUIENTE cue, y eso DESCARTA el del cue que estamos disparando —
        // el GO instantáneo no se promovía jamás y el entrante llegaba al
        // cruce con la superficie aún sin pintar (un fotograma de negro puro
        // medido en los cortes secos). Con el orden correcto el cue sale al
        // aire con su primer fotograma ya puesto, y el playhead avanza
        // después, a tiempo de precargar el siguiente.
        Trigger(idx, advanceChain: true, anchor);
        SetPlayhead(idx + 1);
        MarkShowPoint(cue);
    }

    /// <summary>Anota qué cue acaba de salir al aire por GO (y dónde quedó el
    /// playhead tras avanzar). Es el punto al que vuelve un STOP ALL.</summary>
    private void MarkShowPoint(Cue cue)
    {
        _showPointCueId = cue.Id;
        _playheadAfterGo = _playhead;
    }

    private void GoGroup(int idx, Cue cue, double? anchor = null)
    {
        int extent = _model.GroupExtent(idx);

        if (cue.GroupMode == GroupMode.Enter)
        {
            // Grupo transparente: dispara el primer hijo como un GO normal
            var childIndices = _model.DirectChildIndices(cue.Id);
            if (childIndices.Count > 0)
                GoCue(childIndices[0], anchor);
            else
                SetPlayhead(extent);
            return;
        }

        // TIMELINE / GO_NEXT / RANDOM: el playhead salta tras el grupo
        SetPlayhead(extent);
        Trigger(idx, advanceChain: true, anchor);
        MarkShowPoint(cue);
    }

    /// <param name="anchor">Instante IDEAL en que el grupo abrió: los hijos
    /// cuelgan de él, no del momento real en que despertó el timer del grupo.</param>
    private void FireGroupChildren(int idx, double? anchor = null)
    {
        var group = _model.GetCue(idx);
        if (group is null)
            return;
        var childIndices = _model.DirectChildIndices(group.Id);

        switch (group.GroupMode)
        {
            case GroupMode.Timeline:
                foreach (int ci in childIndices)
                    Trigger(ci, advanceChain: false, anchor);
                break;
            case GroupMode.GoNext:
                if (childIndices.Count > 0)
                    Trigger(childIndices[0], advanceChain: false, anchor);
                break;
            case GroupMode.Random:
                FireRandomChild(group, childIndices, anchor);
                break;
            // ENTER no llega aquí (grupo transparente en GoGroup)
        }
    }

    private void FireRandomChild(Cue group, List<int> childIndices, double? anchor = null)
    {
        var fired = _rrState.TryGetValue(group.Id, out var f)
            ? f : _rrState[group.Id] = new HashSet<int>();

        List<int> Eligible(bool requireUnfired)
        {
            var result = new List<int>();
            foreach (int ci in childIndices)
            {
                var c = _model.GetCue(ci);
                if (c is null || !c.Armed || _running.ContainsKey(c.Id))
                    continue;
                if (requireUnfired && fired.Contains(c.Id))
                    continue;
                result.Add(ci);
            }
            return result;
        }

        var candidates = Eligible(requireUnfired: true);
        if (candidates.Count == 0)
        {
            fired.Clear();
            candidates = Eligible(requireUnfired: false);
        }
        if (candidates.Count == 0)
            return;
        int chosen = candidates[_random.Next(candidates.Count)];
        var cue = _model.GetCue(chosen);
        if (cue is not null)
            fired.Add(cue.Id);
        Trigger(chosen, advanceChain: false, anchor);
    }

    /// <summary>Dispara el cue en idx: pre_wait, acción y encadenado.
    /// advanceChain=false → sub-secuencia (START/grupo) que no mueve el playhead.</summary>
    /// <param name="anchor">Instante IDEAL del disparo (NowRaw); null = ahora
    /// (GO de mano). De él cuelgan pre_wait y la cadena AUTO_CONTINUE.</param>
    private void Trigger(int idx, bool advanceChain, double? anchor = null)
    {
        var cue = _model.GetCue(idx);
        if (cue is null)
            return;

        // ── Second trigger: el cue ya está sonando ──────────────────────
        if (_running.ContainsKey(cue.Id))
        {
            switch (cue.Retrigger)
            {
                case RetriggerMode.None:
                    return;                                  // ignorar 2º disparo
                case RetriggerMode.Stop:
                case RetriggerMode.HardStop:
                    EndCue(cue.Id, markStopped: true);
                    return;
                case RetriggerMode.Panic:
                    EndCue(cue.Id, markStopped: true, fade: PanicFade);
                    return;
                    // HARD_RESTART: continúa abajo (para + reinicia)
            }
        }

        // Plazo IDEAL de este disparo. Si viene de una cadena, es el instante
        // teórico del eslabón (aunque el timer haya despertado tarde); si es
        // un GO de mano, es ahora mismo. Un ideal DEMASIADO viejo (parón
        // gordo de la máquina) se descarta: ver MaxRecuperacionSegundos.
        double ahora = Clock.NowRaw;
        double t0 = anchor is double a && ahora - a <= MaxRecuperacionSegundos
            ? a : ahora;

        var run = new RunningCue
        {
            CueId = cue.Id,
            Index = idx,
            Kind = cue.Type,
            Name = cue.Name,
            Number = cue.DisplayNumber,
            Color = cue.Color,
            AdvanceChain = advanceChain,
            Anchor = t0,
            ActionAnchor = t0 + cue.PreWait,
            // El elapsed que se VE mide tiempo real: si el disparo llegó
            // tarde, la cuenta lo refleja en vez de fingir el ideal.
            TStart = Clock.Now,
        };
        // Retrigger HARD_RESTART: cancelar la instancia previa del mismo cue
        if (_running.ContainsKey(cue.Id))
            EndCue(cue.Id, markStopped: false);
        _running[cue.Id] = run;

        // AUTO_CONTINUE: timer de cadena INDEPENDIENTE del ciclo de vida del cue
        // (debe disparar aunque la acción termine antes del post_wait).
        // Plazo ABSOLUTO: el eslabón siguiente cuelga del ideal de este, así
        // cien cues encadenados suman sus tiempos nominales y no los retrasos.
        if (cue.ContinueMode == ContinueMode.AutoContinue)
        {
            double due = t0 + cue.PreWait + cue.PostWait;
            ScheduleChainAt(due, () => ContinueNext(idx, advanceChain, due));
        }

        // Acción: inmediata o tras pre_wait (también con plazo absoluto)
        if (cue.PreWait > 0.0)
            ScheduleAt(run, run.ActionAnchor, () => StartAction(idx));
        else
            StartAction(idx);
    }

    /// <summary>Arranca la acción del cue. offset &gt; 0 = "reproducir desde
    /// el cursor": el media hace seek a trim_start+offset y el timer de fin
    /// cuenta solo lo que queda; los tiempos mostrados suman el offset.</summary>
    private void StartAction(int idx, double offset = 0.0)
    {
        var cue = _model.GetCue(idx);
        if (cue is null)
            return;
        if (!_running.TryGetValue(cue.Id, out var run))
            return;
        if (offset > 0.0)
            offset = Math.Min(offset, Math.Max(0.0, ActionDuration(cue) - 0.05));
        run.Offset = offset;
        run.ActionStarted = true;
        // La MEDICIÓN arranca AHORA (tiempo real, no el ideal): si el pre_wait
        // despertó tarde, elapsed/remaining lo enseñan.
        run.TStart = Clock.Now;

        if (!cue.Armed)
        {
            // Cue desarmado: sin acción, pero respeta el encadenado. Termina
            // en su instante IDEAL: una fila desarmada en medio de una cadena
            // no debe meter el retraso de su timer en el resto del show.
            run.SequenceEnd = run.ActionAnchor;
            OnActionEnd(cue.Id);
            return;
        }

        // "Fade & stop others over time" (Triggers)
        ApplyFadeOthers(cue);

        // Exclusividad de GO (Settings)
        if (cue.IsMedia())
            ApplyGoExclusivity(cue);

        switch (cue.Type)
        {
            case CueType.Audio:
                ModelState(idx, CueState.Playing);
                _audio.Play(cue.Id, cue.FilePath, new AudioPlayParams(
                    Volume: cue.Volume,
                    FadeIn: Math.Max(0.0, cue.FadeIn - offset),
                    FadeOut: cue.FadeOut,
                    Loop: cue.Loop,
                    TrimStart: cue.TrimStart + offset, TrimEnd: cue.TrimEnd,
                    PlayCount: cue.PlayCount, OutputDevice: cue.AudioOutput,
                    Eq: EqFor(cue), Rate: cue.Rate));
                run.Duration = ActionDuration(cue);
                // FRONTERA CON EL RELOJ DE MEDIA: el fin de un audio se cuenta
                // desde su arranque REAL, no desde el ideal de la secuencia.
                // Anclarlo al ideal recortaría el final de lo que está
                // sonando cuando el arranque llegó tarde — el cristal de la
                // tarjeta manda en cuánto dura lo que suena.
                if (run.Duration > 0.0 && !cue.Loop)
                    Schedule(run,
                        (int)(Math.Max(0.0, run.Duration - offset) / RateOf(cue) * 1000),
                        () => DurationElapsed(cue.Id));
                PreloadUpcoming();
                break;

            case CueType.Video:
                ModelState(idx, CueState.Playing);
                // La imagen de respaldo del PANIC se retira al volver el vídeo
                _view.HideBackupSlate();
                // Fade de entrada pendiente (con offset, lo que quede): además
                // de la opacidad, es la señal de CROSSFADE en la misma pista
                // (el motor mantiene vivo al saliente durante el fundido)
                double targetOpacity = cue.Opacity / 100.0;
                double fadeLeft = Math.Max(0.0, cue.FadeIn - offset);
                var vparams = _canvas.ParamsFor(cue, offset);
                // Códec que el compositor no renderiza con mpv (HAP):
                //  · Si es elegible (.mov, rate 1, compositor, FULL) → MOTOR
                //    HAP PROPIO: vídeo por WinCue.Video.Hap hacia la MISMA
                //    superficie del compositor (recupera los fundidos de
                //    opacidad) y su AUDIO por el AudioEngine con id espejo
                //    (rack completo) actuando de reloj maestro del vídeo.
                //  · Si no → ruta CLÁSICA de siempre (embebido en la ventana
                //    de salida, sin fundidos). Sondeo cacheado al cargar.
                bool mirrorAudio = false;
                if (MediaImporter.IsUnsupportedVideoCodec(cue.FilePath))
                {
                    if (HapEngineEligible(cue))
                    {
                        mirrorAudio = true;
                        vparams = vparams with { UseHapEngine = true, CanvasOutputs = null };
                        _view.FlashStatus(Loc.T(
                            $"«{cue.Name}»: HAP → motor propio (audio por el rack).",
                            $"«{cue.Name}»: HAP → native engine (audio through the rack)."));
                    }
                    else
                    {
                        vparams = vparams with
                        {
                            ForceClassicPath = true,
                            HandoverSeconds = 0.0,
                            CanvasOutputs = null,
                        };
                        _view.FlashStatus(Loc.T(
                            $"«{cue.Name}»: HAP → ruta clásica (sin fundidos de opacidad).",
                            $"«{cue.Name}»: HAP → classic path (no opacity fades)."));
                    }
                }
                else if (HybridAudioEligible(cue))
                {
                    // RUTA HÍBRIDA (paso 3b): mpv reproduce el vídeo MUDO
                    // (aid=no) y el audio suena por el espejo del AudioEngine
                    // (rack completo); el PLL del engine disciplina el vídeo
                    // al reloj del audio. Si el espejo falla en caliente,
                    // OnAudioError recupera el audio por mpv (aid=auto).
                    mirrorAudio = true;
                    vparams = vparams with { ExternalAudio = true };
                }
                _video.SetEq(cue.Id, EqFor(cue));   // antes del Play: el player
                                                    // nace con la af aplicada
                _video.Play(cue.Id, cue.FilePath, vparams);
                if (mirrorAudio)
                {
                    // AUDIO espejo del cue de vídeo (motor HAP o híbrido):
                    // mismos parámetros que un cue de audio (trims/loop/
                    // fades/EQ; rate fijo a 1.0 — la elegibilidad lo
                    // garantiza). Si el archivo no tiene pista de audio, el
                    // Play emite error: OnAudioError lo registra, saca el
                    // cue del set y recupera el audio por mpv si procede.
                    _mirroredAudio.Add(cue.Id);
                    _audio.Play(VideoAudioId(cue.Id), cue.FilePath,
                        MirrorPlayParams(cue, offset, fadeLeft));
                }
                RefreshVideoTally();
                // AL AIRE = AL FRENTE de su pista. El z de la superficie se
                // fija cuando el cue la TOMA, y con la precarga en STANDBY eso
                // puede ocurrir mucho antes del GO: el entrante de un
                // crossfade se quedaba DEBAJO del saliente (fundido invisible
                // y corte seco al morir el saliente). Va ANTES de la opacidad
                // para que ni un fotograma se componga en el orden viejo.
                _view.RaiseVideoCueToFront(cue.Id);
                if (fadeLeft > 0.0)
                {
                    // CROSSFADE en la misma pista: el saliente queda DEBAJO
                    // del entrante — que NO funda su opacidad (el cruce se
                    // hundiría a negro); su volumen sí funde y su fin lo retira.
                    foreach (var (ocid, orun) in _running)
                    {
                        if (ocid == cue.Id || orun.Kind != CueType.Video)
                            continue;
                        if (_model.GetById(ocid) is not Cue oc
                            || VideoLayerOf(oc) != VideoLayerOf(cue))
                            continue;
                        if (!CanvasMode && ScreenOf(oc) != ScreenOf(cue))
                            continue;
                        orun.SuppressVisualFadeOut = true;
                        _view.SetVideoCueOpacity(ocid, oc.Opacity / 100.0);
                    }
                    SetCueLayerOpacity(cue, 0.0);
                    SetCueLayerOpacity(cue, targetOpacity, fadeLeft);
                }
                else
                {
                    SetCueLayerOpacity(cue, targetOpacity);
                }
                run.Duration = ActionDuration(cue);
                if (run.Duration > 0.0 && !cue.Loop)
                {
                    Schedule(run,
                        (int)(Math.Max(0.0, run.Duration - offset) / RateOf(cue) * 1000),
                        () => VideoEndCheck(cue.Id, 0));
                    // El fade_out del VÍDEO vive DENTRO de sus últimos
                    // fade_out segundos (como lo dibuja el timeline y como
                    // hace el audio), no a partir del final del bloque.
                    double fo = Math.Clamp(cue.FadeOut, 0.0, run.Duration);
                    if (fo > 0.01)
                    {
                        double startIn = Math.Max(0.0, run.Duration - offset - fo);
                        Schedule(run, (int)(startIn / RateOf(cue) * 1000),
                            () => BeginVideoFadeOut(cue.Id, fo, 0));
                    }
                }
                break;

            // WAIT y FADE son SECUENCIA pura: su fin lo marca el reloj, no un
            // motor. Plazo ABSOLUTO desde el ideal de arranque — así una
            // cadena de esperas no acumula el retraso de cada timer (que es
            // justo donde más se nota en un PC cargado). El mínimo de 50 ms se
            // conserva: un WAIT de 0 no debe reentrar en el mismo tic.
            case CueType.Wait:
                ModelState(idx, CueState.Playing);
                run.Duration = Math.Max(0.0, cue.Duration);
                run.SequenceEnd = run.ActionAnchor
                    + Math.Max(0.05, Math.Max(0.0, cue.Duration - offset));
                ScheduleAt(run, run.SequenceEnd.Value, () => OnActionEnd(cue.Id));
                break;

            case CueType.Fade:
                ModelState(idx, CueState.Playing);
                run.Duration = Math.Max(0.0, cue.Duration);
                ExecFade(cue);
                run.SequenceEnd = run.ActionAnchor + Math.Max(0.05, cue.Duration);
                ScheduleAt(run, run.SequenceEnd.Value, () => OnActionEnd(cue.Id));
                break;

            // Cues INSTANTÁNEOS: terminan en su propio instante ideal, que es
            // el que hereda el siguiente eslabón de la cadena.
            case CueType.Start:
                run.SequenceEnd = run.ActionAnchor;
                ExecStart(cue);
                OnActionEnd(cue.Id);
                break;

            case CueType.Stop:
                run.SequenceEnd = run.ActionAnchor;
                ExecStop(cue);
                OnActionEnd(cue.Id);
                break;

            case CueType.Script:
                run.SequenceEnd = run.ActionAnchor;
                ExecScript(cue);
                OnActionEnd(cue.Id);
                break;

            case CueType.Group:
                run.SequenceEnd = run.ActionAnchor;
                FireGroupChildren(idx, run.ActionAnchor);
                OnActionEnd(cue.Id);
                break;

            default:   // MEMO u otros: sin acción
                run.SequenceEnd = run.ActionAnchor;
                OnActionEnd(cue.Id);
                break;
        }
    }

    /// <summary>Velocidad de reproducción efectiva (vive en CanvasLayout).</summary>
    private static double RateOf(Cue c) => CanvasLayout.RateOf(c);

    /// <summary>Duración efectiva de la ACCIÓN: en media, con el TRIM aplicado
    /// (el countdown y el timer de fin deben medir lo que va a sonar de
    /// verdad, no la longitud del archivo).</summary>
    private static double ActionDuration(Cue c)
    {
        if (!c.IsMedia())
            return Math.Max(0.0, c.Duration);
        double end = c.TrimEnd > 0
            ? (c.Duration > 0 ? Math.Min(c.TrimEnd, c.Duration) : c.TrimEnd)
            : c.Duration;
        return Math.Max(0.0, end - c.TrimStart);
    }

    /// <summary>Pista de prioridad visual del cue (vive en CanvasLayout).</summary>
    private static int VideoLayerOf(Cue c) => CanvasLayout.VideoLayerOf(c);

    /// <summary>Salida de vídeo del cue (vive en CanvasLayout).</summary>
    private static int ScreenOf(Cue c) => CanvasLayout.ScreenOf(c);

    // ------------------------------------------------------------------
    // MODO CANVAS — implementación en CanvasLayout
    // ------------------------------------------------------------------

    /// <summary>true si el show opera en modo canvas (Settings → Vídeo).</summary>
    private bool CanvasMode => _canvas.CanvasMode;

    /// <summary>Parsea "x,y,w,h" y lo recorta al canvas; inválido → completo.</summary>
    public static CanvasView ParseRegion(string text, int screen, int cw, int ch) =>
        CanvasLayout.ParseRegion(text, screen, cw, ch);

    /// <summary>Opacidad del vídeo del cue (compositor GPU): cada cue tiene su
    /// superficie propia en todas las salidas donde se ve — la vista la
    /// localiza por id, valga clásico multi-salida o modo canvas.</summary>
    private void SetCueLayerOpacity(Cue cue, double opacity, double fadeSeconds = 0.0) =>
        _view.SetVideoCueOpacity(cue.Id, opacity, fadeSeconds);

    /// <summary>Al disparar un media, corta otros cues según el modo de GO.
    /// El vídeo es exclusivo POR PISTA: misma pista → sustituye; pistas
    /// distintas conviven (la superior tapa a la inferior).</summary>
    private void ApplyGoExclusivity(Cue cue)
    {
        string mode = _settings.Get("go_mode", "concurrent");
        bool stopAll = mode == "sequential" || (mode == "per_cue" && cue.StopOthers);
        foreach (int otherId in _running.Keys.ToList())
        {
            if (otherId == cue.Id || !_running.TryGetValue(otherId, out var other))
                continue;
            // En modo canvas la pista es GLOBAL (el vídeo vive en todas las
            // salidas); en clásico, exclusiva por (salida, pista).
            // CROSSFADE: si el entrante trae fade_in, el saliente de su misma
            // pista NO se corta aquí — se funde y lo retira su propio fin
            // (el motor mantiene ambos vivos durante el fundido).
            bool sameVideoLayer = cue.Type == CueType.Video
                && other.Kind == CueType.Video
                && cue.FadeIn <= 0.0
                && _model.GetById(otherId) is Cue oc
                && VideoLayerOf(oc) == VideoLayerOf(cue)
                && (CanvasMode || ScreenOf(oc) == ScreenOf(cue));
            if (stopAll || sameVideoLayer)
                EndCue(otherId, markStopped: true, fade: 0.0);
        }
    }

    /// <summary>Funde y detiene otros cues activos según el ámbito (Triggers).</summary>
    private void ApplyFadeOthers(Cue cue)
    {
        if (!cue.FadeOthers)
            return;
        double t = Math.Max(0.0, cue.FadeOthersTime);
        foreach (int otherId in _running.Keys.ToList())
        {
            if (otherId == cue.Id)
                continue;
            var other = _model.GetById(otherId);
            if (other is null)
                continue;
            bool inScope = cue.FadeOthersScope is FadeScope.All or FadeScope.List
                || (cue.FadeOthersScope == FadeScope.Peers && other.ParentId == cue.ParentId);
            if (inScope)
                EndCue(otherId, markStopped: true, fade: t);
        }
    }

    /// <summary>Reproduce el show DESDE un punto del timeline (cursor de
    /// edición): los bloques que cruzan ese instante arrancan YA con seek a
    /// su offset, y los posteriores del mismo segmento (hasta el próximo GO
    /// manual) se programan a su hora relativa. La proyección del timeline
    /// ya trae las cadenas resueltas → los encadenados de cada cue se
    /// suprimen para no duplicar disparos.</summary>
    public void PlayFromTimeline(IReadOnlyList<TimelinePlayItem> items)
    {
        StopEverything(fade: 0.0);
        int fired = 0;
        // Origen COMÚN del segmento: las esperas de todos los bloques cuelgan
        // del mismo instante, no cada una del momento en que le tocó el bucle.
        double t0 = Clock.NowRaw;
        foreach (var item in items)
        {
            int idx = _model.IndexOfId(item.CueId);
            var cue = _model.GetCue(idx);
            if (cue is null || !cue.Armed || cue.Type == CueType.Group)
                continue;
            var run = new RunningCue
            {
                CueId = cue.Id,
                Index = idx,
                Kind = cue.Type,
                Name = cue.Name,
                Number = cue.DisplayNumber,
                Color = cue.Color,
                AdvanceChain = false,
                SuppressChain = true,
                PreWaitOverride = item.Delay > 0.001 ? item.Delay : null,
                Anchor = t0,
                ActionAnchor = t0 + Math.Max(0.0, item.Delay),
                TStart = Clock.Now,
            };
            // Reemplaza cualquier instancia previa (StopEverything ya limpió)
            _running[cue.Id] = run;
            if (item.Delay > 0.001)
            {
                int i = idx;
                ScheduleAt(run, run.ActionAnchor, () => StartAction(i));
            }
            else
            {
                StartAction(idx, Math.Max(0.0, item.Offset));
            }
            fired++;
        }
        Log($"Timeline: reproducir desde el cursor — {fired} cue(s) del segmento");
        _view.FlashStatus(fired > 0
            ? $"▶ Reproduciendo desde el cursor ({fired} cue(s))"
            : "Nada que reproducir en ese punto del timeline.");
    }

    /// <summary>Dispara un cue por id sin mover el playhead (hotkey/START externo).</summary>
    public void TriggerCueId(int cueId)
    {
        int idx = _model.IndexOfId(cueId);
        if (idx >= 0)
            Trigger(idx, advanceChain: false);
    }

    /// <param name="anchor">Instante IDEAL en que tocaba encadenar. Se pasa al
    /// eslabón siguiente para que la cadena mida siempre contra el reloj y no
    /// contra el disparo (posiblemente tardío) del anterior.</param>
    private void ContinueNext(int idx, bool advanceChain, double? anchor = null)
    {
        int nxt = idx + 1;
        if (nxt >= _model.Count)
            return;
        if (advanceChain)
            GoCue(nxt, anchor);
        else
            Trigger(nxt, advanceChain: false, anchor);
    }

    /// <summary>La acción del cue terminó. Encadena AUTO_FOLLOW y retira el cue.</summary>
    private void OnActionEnd(int cueId, double fade = 0.0)
    {
        if (!_running.TryGetValue(cueId, out var run))
            return;
        int idx = _model.IndexOfId(cueId);
        var cue = _model.GetCue(idx);

        // SuppressChain: al reproducir desde el cursor, la proyección ya trae
        // todos los bloques del segmento programados — encadenar duplicaría.
        if (cue is not null && cue.ContinueMode == ContinueMode.AutoFollow
            && !run.SuppressChain)
            // AUTO_FOLLOW hereda el fin IDEAL cuando lo marcó el reloj (WAIT,
            // FADE, cue instantáneo). En media el fin lo marca el MOTOR: ahí
            // el ancla es el ahora real, porque el ideal no sabe cuánto tardó
            // de verdad en sonar el archivo.
            ContinueNext(idx, run.AdvanceChain, run.SequenceEnd ?? Clock.NowRaw);

        EndCue(cueId, markStopped: true, fade: fade);
    }

    /// <summary>Arranca el fundido de SALIDA de un vídeo (volumen + opacidad)
    /// dentro de sus últimos fade_out segundos de CONTENIDO. El reloj de
    /// pared y la posición real del vídeo derivan (latencia de arranque de
    /// mpv ~100-300 ms): con fades cortos ese desfase se come el fundido,
    /// así que el timer se CORRIGE contra la posición real y se reprograma
    /// si llegó pronto. No detiene el cue: el fin llega con el fundido hecho.</summary>
    private void BeginVideoFadeOut(int cueId, double contentFadeSeconds, int attempt)
    {
        if (!_running.TryGetValue(cueId, out var run) || run.ManualStop)
            return;
        if (!_video.IsPlaying(cueId) || _model.GetById(cueId) is not Cue cue)
            return;

        // Base sin telemetría del motor: el fade no puede durar más de lo
        // que queda de bloque desde el punto de arranque (offset)
        double wall = Math.Min(contentFadeSeconds,
            Math.Max(0.05, run.Duration - run.Offset)) / RateOf(cue);
        double eng = _video.GetElapsed(cueId);
        if (eng > 0.0)
        {
            double pos = eng + run.Offset;                       // contenido real
            double target = run.Duration - contentFadeSeconds;   // inicio del fade
            if (pos + 0.03 < target && attempt < 60)
            {
                // Llegamos pronto (deriva): reprogramar lo que falta de verdad
                Schedule(run, Math.Max(16, (int)((target - pos) / RateOf(cue) * 1000)),
                    () => BeginVideoFadeOut(cueId, contentFadeSeconds, attempt + 1));
                return;
            }
            wall = Math.Max(0.05, (run.Duration - pos) / RateOf(cue));
        }

        _video.FadeVolume(cueId, 0.0, wall, stopAfter: false);
        // Crossfade en la MISMA pista: el entrante (encima) ya funde de 0→1
        // sobre este — oscurecer también al saliente hundiría el cruce a
        // negro. Se mantiene opaco y lo retira su fin (el volumen SÍ funde).
        if (!run.SuppressVisualFadeOut)
            SetCueLayerOpacity(cue, 0.0, wall);
    }

    /// <summary>Fin de vídeo corregido por deriva: si la posición real aún no
    /// llegó al final (arrancó con latencia), se reprograma lo que falta —
    /// clave para que los fades CORTOS no se corten a medias.</summary>
    private void VideoEndCheck(int cueId, int attempt)
    {
        if (_running.TryGetValue(cueId, out var run) && !run.ManualStop
            && _video.IsPlaying(cueId))
        {
            double eng = _video.GetElapsed(cueId);
            if (eng > 0.0 && attempt < 60
                && _model.GetById(cueId) is Cue cue)
            {
                double remaining = run.Duration - (eng + run.Offset);
                if (remaining > 0.08)
                {
                    Schedule(run, Math.Max(16, (int)(remaining / RateOf(cue) * 1000)),
                        () => VideoEndCheck(cueId, attempt + 1));
                    return;
                }
            }
        }
        DurationElapsed(cueId);
    }

    /// <summary>La duración fijada expiró antes del fin natural: parar. El
    /// AUDIO funde su cola (el motor la aplica sample-accurate); el VÍDEO ya
    /// llegó fundido (BeginVideoFadeOut) → corte directo sin doble fade.</summary>
    private void DurationElapsed(int cueId)
    {
        var cue = _model.GetById(cueId);
        if (cue is null)
            return;
        if (_running.TryGetValue(cueId, out var run))
            run.ManualStop = true;   // evita doble action-end desde el engine
        double fade = cue.Type == CueType.Audio ? cue.FadeOut : 0.0;
        OnActionEnd(cueId, fade: fade);
    }

    // ==================================================================
    // CUES QUE APUNTAN A OTRO CUE
    // ==================================================================

    private void ExecFade(Cue cue)
    {
        var target = cue.TargetId is int tid ? _model.GetById(tid) : null;
        if (target is null)
        {
            _view.FlashStatus(Loc.T("FADE sin objetivo válido — ignorado", "FADE without a valid target — ignored"));
            return;
        }
        bool stopAfter = cue.FadeStopOnDone && cue.Volume <= 0.0001;
        if (_audio.IsPlaying(target.Id))
        {
            _audio.Fade(target.Id, cue.Volume, cue.Duration, stopAfter);
        }
        else if (_video.IsPlaying(target.Id))
        {
            _video.FadeVolume(target.Id, cue.Volume, cue.Duration, stopAfter);
            // Cue HAP-routed: el volumen real vive en el audio espejo
            if (_mirroredAudio.Contains(target.Id))
                _audio.Fade(VideoAudioId(target.Id), cue.Volume, cue.Duration, stopAfter);
            // La rampa que dibuja el timeline es también VISUAL: la opacidad
            // acompaña al volumen (proporcional a la opacidad base del cue)
            SetCueLayerOpacity(target,
                Math.Clamp(cue.Volume, 0.0, 1.0) * target.Opacity / 100.0,
                Math.Max(0.0, cue.Duration));
        }
        else
        {
            _view.FlashStatus(Loc.T($"FADE → cue {target.DisplayNumber} no está sonando", $"FADE → cue {target.DisplayNumber} is not playing"));
        }
    }

    private void ExecStart(Cue cue)
    {
        if (cue.TargetId is not int tid)
        {
            _view.FlashStatus(Loc.T("START sin objetivo — ignorado", "START without a target — ignored"));
            return;
        }
        int tgtIdx = _model.IndexOfId(tid);
        if (tgtIdx < 0)
        {
            _view.FlashStatus(Loc.T("START: objetivo inexistente — ignorado", "START: target does not exist — ignored"));
            return;
        }
        Trigger(tgtIdx, advanceChain: false);
    }

    private void ExecStop(Cue cue)
    {
        if (cue.TargetId is not int tid)
        {
            StopEverything(fade: cue.FadeOut);
            return;
        }
        var tgt = _model.GetById(tid);
        if (tgt is not null && _running.ContainsKey(tgt.Id))
            EndCue(tgt.Id, markStopped: true, fade: cue.FadeOut);
    }

    private void ExecScript(Cue cue)
    {
        string path = cue.FilePath.Trim();
        if (path.Length == 0)
        {
            _view.FlashStatus(Loc.T("SCRIPT sin archivo — ignorado", "SCRIPT without a file — ignored"));
            return;
        }
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            ProcessStartInfo psi = ext switch
            {
                ".py" => new ProcessStartInfo("python", $"\"{path}\""),
                ".bat" or ".cmd" => new ProcessStartInfo("cmd", $"/c \"{path}\""),
                ".ps1" => new ProcessStartInfo("powershell",
                    $"-ExecutionPolicy Bypass -File \"{path}\""),
                _ => new ProcessStartInfo(path) { UseShellExecute = true },
            };
            Process.Start(psi);
            _view.FlashStatus(Loc.T($"SCRIPT lanzado: {Path.GetFileName(path)}", $"SCRIPT launched: {Path.GetFileName(path)}"));
        }
        catch (Exception exc)
        {
            _view.FlashStatus(Loc.T($"SCRIPT falló: {exc.Message}", $"SCRIPT failed: {exc.Message}"));
        }
    }

    // ==================================================================
    // FIN DE CUE / STOP GLOBAL
    // ==================================================================

    /// <summary>Retira un cue de la ejecución: cancela timers y para su motor.</summary>
    private void EndCue(int cueId, bool markStopped, double fade = 0.0)
    {
        if (!_running.Remove(cueId, out var run))
            return;
        foreach (var t in run.Timers)
            t.Dispose();
        run.Timers.Clear();

        if (run.Kind == CueType.Audio && _audio.IsPlaying(cueId))
        {
            _audio.Stop(cueId, fade);
        }
        else if (run.Kind == CueType.Video)
        {
            // Cue HAP-routed: el ciclo de vida del audio espejo SIGUE al del
            // vídeo — mismo fundido, misma parada
            bool hap = _mirroredAudio.Remove(cueId);
            // Llamar Stop aunque el vídeo haya hecho end-file: termina el player
            // y limpia a negro para que su último frame no quede pegado.
            if (fade > 0.0 && _video.IsPlaying(cueId))
            {
                _video.FadeVolume(cueId, 0.0, fade, stopAfter: true);
                if (hap)
                    _audio.Stop(VideoAudioId(cueId), fade);
                // Fundido VISUAL a la par que el de audio (compositor)
                if (_model.GetById(cueId) is Cue vc)
                    SetCueLayerOpacity(vc, 0.0, fade);
            }
            else
            {
                _video.Stop(cueId);
                if (hap)
                    _audio.Stop(VideoAudioId(cueId), 0.0);
            }
        }

        if (markStopped)
        {
            int idx = _model.IndexOfId(cueId);
            if (idx >= 0)
                ModelState(idx, CueState.Stopped);
        }
    }

    /// <summary>Para todo. <paramref name="rewindToShowPoint"/> lo convierte en
    /// una parada de DIRECTO: el playhead vuelve al cue que estaba al aire, así
    /// que el GO siguiente reanuda por donde ibas. Lo usan STOP ALL, PANIC y
    /// FADE ALL; NO las paradas internas (cargar show, reproducir desde el
    /// timeline, cue STOP sin objetivo), que colocan el playhead por su cuenta
    /// o siguen el hilo del propio show.</summary>
    private void StopEverything(double fade, bool rewindToShowPoint = false)
    {
        // El retroceso se decide ANTES (EndCue vacía _running) y se aplica al
        // final: SetPlayhead precarga el standby del cue, y hacerlo antes del
        // barrido de motores lo tiraría a la basura.
        bool rewind = rewindToShowPoint && _running.Count > 0;

        // Cancelar continuaciones pendientes (AUTO_CONTINUE)
        foreach (var t in _chainTimers)
            t.Dispose();
        _chainTimers.Clear();

        foreach (int cueId in _running.Keys.ToList())
            EndCue(cueId, markStopped: true, fade: fade);

        if (fade <= 0.0)
        {
            // Parada inmediata: barrido de seguridad de ambos motores.
            _audio.StopAll(0.0);
            _video.StopAll();
        }
        else
        {
            // Con fundido: barrer solo el audio suelto; los vídeos ya se funden
            // por cue en EndCue — NO cortarlos.
            _audio.StopAll(fade);
        }

        // CONGELADOS del crossfade: ya no hay cruce que servir. Es el camino
        // explícito de STOP ALL, PANIC, FADE ALL, recarga de show y
        // PlayFromTimeline — una liberación diferida que no llegara dejaría el
        // pool de superficies del compositor sin slots (auditoría 05).
        _video.ReleaseFrozenSurfaces();

        _paused = false;
        _view.UpdateTransport(false);
        _view.UpdateCountdown(null);
        _view.UpdateActiveCues(Array.Empty<ActiveCueInfo>());
        _view.UpdateVuMeter(0.0f, 0.0f);
        RefreshVideoTally();

        // VOLVER AL CUE QUE SONABA. Solo si nadie ha tocado el playhead desde
        // aquel GO (_playheadAfterGo): si el operador ya se ha movido a otro
        // punto de la lista, esa es SU posición y un STOP ALL no la secuestra.
        if (rewind && _playhead == _playheadAfterGo)
        {
            int idx = _model.IndexOfId(_showPointCueId);
            if (idx >= 0)
                SetPlayhead(idx);
        }
    }

    // ==================================================================
    // SEÑALES DE LOS MOTORES
    // ==================================================================

    private void OnAudioStateChanged(int cueId, MediaCueState state)
    {
        if (state == MediaCueState.Stopped &&
            _running.TryGetValue(cueId, out var run) && !run.ManualStop)
        {
            OnActionEnd(cueId);   // fin natural del audio → encadenar
        }
    }

    private void OnVideoStateChanged(int cueId, MediaCueState state)
    {
        if (state == MediaCueState.Stopped)
        {
            if (_running.TryGetValue(cueId, out var run) && !run.ManualStop)
                OnActionEnd(cueId);
            RefreshVideoTally();
        }
    }

    private void OnLatencyReport(int cueId, double ms)
    {
        int idx = _model.IndexOfId(cueId);
        if (idx >= 0)
        {
            _model.SetLatency(idx, ms);
            _view.RefreshRow(idx);
        }
    }

    private void OnAudioError(int cueId, string message)
    {
        // Audio espejo de un cue de vídeo: un archivo sin pista de audio (o
        // un fallo del decoder del espejo) NO es un error del cue — el vídeo
        // sigue. RED DE SEGURIDAD: el cue sale del set espejo y, si tiene un
        // player mpv vivo (ruta híbrida o fallback HAP→mpv), se RECUPERA su
        // audio por mpv (aid=auto) — NUNCA un cue sin audio que lo tenía.
        // Un cue HAP nativo no tiene mpv: RestoreInternalAudio es no-op y el
        // vídeo sigue con su reloj propio (Stopwatch), como en el paso 3.
        if (cueId <= VideoAudioBase)
        {
            int realId = VideoAudioBase - cueId;
            Log($"Audio espejo (cue {realId}): {message}");
            if (_mirroredAudio.Remove(realId) && _video.IsPlaying(realId))
            {
                Log($"Audio espejo caído → audio del cue {realId} recuperado por mpv");
                _video.RestoreInternalAudio(realId);
                // El fallback HAP→mpv corre con volume 0 (el espejo era quien
                // sonaba): restaurar el volumen real del cue en mpv.
                if (_model.GetById(realId) is Cue rc)
                    _video.SetVolume(realId, rc.Volume);
            }
            return;
        }
        int idx = _model.IndexOfId(cueId);
        if (idx >= 0)
        {
            _model.SetState(idx, CueState.Error);
            _view.RefreshRow(idx);
        }
        _running.Remove(cueId);
        _view.FlashStatus(Loc.T($"Audio: {message}", $"Audio: {message}"));
    }

    private void OnVideoError(int cueId, string message)
    {
        if (message.StartsWith("[AVISO]", StringComparison.Ordinal))
        {
            _view.FlashStatus(message);
            return;
        }
        int idx = _model.IndexOfId(cueId);
        if (idx >= 0)
        {
            _model.SetState(idx, CueState.Error);
            _view.RefreshRow(idx);
        }
        _running.Remove(cueId);
        // El audio espejo de un cue HAP no debe quedarse sonando huérfano
        if (_mirroredAudio.Remove(cueId))
            _audio.Stop(VideoAudioId(cueId), 0.0);
        _view.FlashStatus(Loc.T($"Vídeo: {message}", $"Video: {message}"));
    }


    // ==================================================================
    // TICK — panel de activos + countdown (la UI lo llama cada ~100 ms)
    // ==================================================================

    /// <summary>Reloj inyectable (los tests fijan la hora para el wall-clock;
    /// vive en el RemoteRouter, que es quien evalúa los disparos por hora).</summary>
    public Func<DateTime> NowProvider
    {
        get => _remote.NowProvider;
        set => _remote.NowProvider = value;
    }

    public void Tick()
    {
        _remote.CheckWallclock();

        if (_running.Count == 0)
        {
            if (_lastHadRunning)
            {
                _view.UpdateActiveCues(Array.Empty<ActiveCueInfo>());
                _view.UpdateCountdown(null);
                _lastHadRunning = false;
            }
            return;
        }
        _lastHadRunning = true;

        var rows = SnapshotActiveCues(out double? primaryRemaining);
        _view.UpdateActiveCues(rows);
        _view.UpdateCountdown(primaryRemaining);
    }

    /// <summary>Instantánea de los cues activos: EXACTAMENTE la misma información
    /// que recibe la vista en UpdateActiveCues (elapsed/remaining/fracción por
    /// cue) más el countdown primario. La consume el panel en vivo y también el
    /// watchdog del soak (diagnóstico autónomo del motor HAP). Rellena de paso
    /// la duración real de un media (0 → longitud natural del motor).</summary>
    public IReadOnlyList<ActiveCueInfo> SnapshotActiveCues(out double? primaryRemaining)
    {
        var rows = new List<ActiveCueInfo>();
        primaryRemaining = null;
        foreach (var run in _running.Values)
        {
            // Rellenar la duración real de un media (0 → longitud natural)
            if (run.Duration <= 0.0 && run.Kind is CueType.Audio or CueType.Video)
            {
                double d = run.Kind == CueType.Audio
                    ? _audio.GetDuration(run.CueId)
                    : _video.GetDuration(run.CueId);
                if (d > 0.0)
                    run.Duration = d;
            }

            // Preferir la posición real del motor (más exacta que el reloj)
            // — SOLO con la acción ya arrancada: en pre_wait el elapsed del
            // motor pertenece a otra instancia (standby/player rezagado) y
            // congelaría la cuenta visible mientras el timer real sí corre
            // TIEMPO DE SHOW: el reloj ya está congelado si hay pausa global,
            // así que no hay que descontar nada a mano. Y como TStart se pone
            // en el arranque REAL, un disparo que llegó tarde se ve tal cual
            // en la cuenta (auto-corrección visible), no como el ideal.
            double wallElapsed = Math.Max(0.0, Clock.Now - run.TStart);
            double elapsed = wallElapsed;
            if (run.ActionStarted && run.Kind == CueType.Audio)
            {
                double e = _audio.GetElapsed(run.CueId);
                if (e > 0.0) elapsed = e;
            }
            else if (run.ActionStarted && run.Kind == CueType.Video)
            {
                double e = _video.GetElapsed(run.CueId);
                if (e > 0.0) elapsed = e;
            }
            // Arranque con seek (play-desde-cursor): el motor cuenta desde su
            // punto de entrada — los tiempos lógicos suman el offset
            elapsed += run.Offset;

            double? remaining = null, fraction = null;
            if (run.Duration > 0.0)
            {
                remaining = Math.Max(0.0, run.Duration - elapsed);
                fraction = Math.Min(1.0, elapsed / run.Duration);
            }

            // Fase de pre_wait: aún no hay acción; mostrar cuánto falta
            // (PreWaitOverride = espera programada por el timeline)
            double? preWaitRemaining = null;
            if (!run.ActionStarted && _model.GetById(run.CueId) is Cue rc)
            {
                double pw = run.PreWaitOverride ?? rc.PreWait;
                if (pw > 0.0)
                    preWaitRemaining = Math.Max(0.0, pw - elapsed);
            }

            rows.Add(new ActiveCueInfo(
                run.CueId, run.Number, run.Name, run.Kind, run.Color,
                elapsed, remaining, fraction, preWaitRemaining,
                MirrorAudio: _mirroredAudio.Contains(run.CueId)));
            if (remaining is double r && (primaryRemaining is null || r < primaryRemaining))
                primaryRemaining = r;
        }

        return rows;
    }

    // ==================================================================
    // PLAYHEAD / MODELO / VISTA
    // ==================================================================

    private void Log(string message) => ShowLog?.Invoke(message);

    private void SetPlayhead(int index)
    {
        int count = _model.Count;
        _playhead = count == 0 ? 0 : Math.Clamp(index, 0, count);  // count = "más allá del final"
        ActiveIndexChanged?.Invoke(_playhead);
        _view.SetPlayhead(_playhead);

        // Precargar el media del standby: el GO no paga la verificación (y
        // con compositor + salida visible, el vídeo queda EN PAUSA listo)
        var next = _model.GetCue(_playhead);
        if (next is { Type: CueType.Video, FilePath.Length: > 0 })
        {
            PrepareVideoStandby(next);
        }
        else
        {
            _video.DiscardStandby();   // el standby ya no es un vídeo
            if (next is { Type: CueType.Audio, FilePath.Length: > 0 })
                _audio.Preload(next.Id, next.FilePath);
        }
    }

    private void ModelState(int index, CueState state)
    {
        _model.SetState(index, state);
        _view.RefreshRow(index);
    }

    /// <summary>Vídeo actualmente en la salida (para el monitor PGM): ruta,
    /// segundos reproducidos, id del cue y SALIDA en la que se ve; null si la
    /// salida está en negro. Port de _update_program_monitor de Python, más
    /// el id y la salida: con el compositor GPU activo el monitor no
    /// re-decodifica el archivo — ESPEJA la superficie que ya va a la sala
    /// (y para encontrarla necesita saber de qué cue y de qué ventana).</summary>
    public (string FilePath, double Elapsed, int CueId, int Screen)? GetProgramVideo()
    {
        var top = TopVideo(requirePlaying: true);
        if (top is null)
            return null;
        double offset = _running.TryGetValue(top.Id, out var run) ? run.Offset : 0.0;
        return (top.FilePath, _video.GetElapsed(top.Id) + offset, top.Id, ScreenOf(top));
    }

    /// <summary>Vídeo visible en la salida: el de la pista MÁS ALTA activa,
    /// priorizando la Salida 1 (el monitor PGM refleja la salida principal).</summary>
    private Cue? TopVideo(bool requirePlaying)
    {
        Cue? top = null;
        var topSlot = (Screen: int.MaxValue, Layer: int.MaxValue);
        foreach (var (cid, r) in _running)
        {
            if (r.Kind != CueType.Video || _model.GetById(cid) is not Cue c)
                continue;
            if (requirePlaying && !_video.IsPlaying(cid))
                continue;
            var slot = (Screen: ScreenOf(c), Layer: VideoLayerOf(c));
            if (slot.Screen < topSlot.Screen
                || (slot.Screen == topSlot.Screen && slot.Layer < topSlot.Layer))
            {
                topSlot = slot;
                top = c;
            }
        }
        return top;
    }

    private void RefreshVideoTally()
    {
        // Pistas activas POR SALIDA → cada ventana oculta sus superficies
        // vacías. Se notifican TODAS las salidas (también las vacías, para
        // que limpien). (La opacidad NO se toca aquí: la fijan los fades de
        // StartAction y EndCue; pisarla cortaría los crossfades en curso.)
        var activeBy = new Dictionary<int, List<int>>();
        bool canvasMode = CanvasMode;
        var canvasScreens = canvasMode
            ? _canvas.CanvasViews().Select(v => v.Screen).ToHashSet()
            : null;
        foreach (var (cid, r) in _running)
            if (r.Kind == CueType.Video && _video.IsPlaying(cid)
                && _model.GetById(cid) is Cue c)
            {
                int layer = VideoLayerOf(c);
                // Canvas: el vídeo vive en TODAS las salidas habilitadas
                foreach (int screen in canvasMode
                    ? canvasScreens! : (IEnumerable<int>)new[] { ScreenOf(c) })
                {
                    if (!activeBy.TryGetValue(screen, out var list))
                        activeBy[screen] = list = new List<int>();
                    list.Add(layer);
                }
            }
        for (int screen = 0; screen < 4; screen++)
            _view.SetActiveVideoLayers(screen,
                activeBy.GetValueOrDefault(screen) ?? (IReadOnlyCollection<int>)Array.Empty<int>());
        // Tally: SOLO lo que está de verdad en pantalla — un cue en pre-wait
        // aún no "es" la salida (anunciarlo confunde al operador)
        _view.SetOutputContent(TopVideo(requirePlaying: true)?.Name);

        // Desglose por salida para el tooltip del tally (qué hay en cada una)
        var detail = new System.Text.StringBuilder(canvasMode ? "MODO CANVAS\n" : "");
        for (int screen = 0; screen < 4; screen++)
        {
            var names = new List<string>();
            foreach (var (cid, r) in _running)
                if (r.Kind == CueType.Video && _video.IsPlaying(cid)
                    && _model.GetById(cid) is Cue c
                    && (canvasMode ? canvasScreens!.Contains(screen)
                                   : ScreenOf(c) == screen))
                    names.Add($"{c.Name} · pista {VideoLayerOf(c) + 1}");
            if (screen == 0 || names.Count > 0)
                detail.AppendLine($"S{screen + 1}: " +
                    (names.Count > 0 ? string.Join(" | ", names) : "negro"));
        }
        _view.SetOutputDetails(detail.ToString().TrimEnd());
    }

    private void PreloadUpcoming()
    {
        var nxt = _model.GetCue(_playhead);
        if (nxt is { Type: CueType.Audio } && nxt.FilePath.Length > 0)
            _audio.Preload(nxt.Id, nxt.FilePath);
    }

    /// <summary>Precarga del vídeo en STANDBY: con compositor y la salida YA
    /// visible, deja el player creado y PAUSADO en su primer frame con su
    /// superficie a opacidad 0 (el GO solo despausa → disparo instantáneo);
    /// si la salida no está abierta (modo EDIT), verificación clásica — la
    /// precarga nunca debe ABRIR ventanas sola.</summary>
    private void PrepareVideoStandby(Cue cue)
    {
        // HAP (u otro códec sin soporte en el compositor mpv): solo
        // verificación clásica — el standby GPU con HAP era EL crash nativo
        // original. Si irá por el motor HAP propio, se PRECALIENTA además el
        // buffer del audio espejo: el reloj maestro arranca sin pagar la
        // decodificación del audio en el GO (evita el resync inicial).
        if (MediaImporter.IsUnsupportedVideoCodec(cue.FilePath))
        {
            _video.PreloadNext(cue.Id, cue.FilePath);
            if (HapEngineEligible(cue) && !_running.ContainsKey(cue.Id))
                _audio.Preload(VideoAudioId(cue.Id), cue.FilePath,
                    MirrorPlayParams(cue, 0.0, cue.FadeIn));
            return;
        }
        // RUTA HÍBRIDA: el standby se prepara con los MISMOS parámetros que
        // usará el Play (ExternalAudio=true — si difirieran, el GO instantáneo
        // no promovería), y el audio espejo se PRECALIENTA con sus parámetros
        // reales: en streaming el pre-spawn de ffmpeg deja el ring lleno sin
        // consumir (GO instantáneo); en RAM decodifica el buffer como antes.
        bool hybrid = HybridAudioEligible(cue);
        var vparams = _canvas.ParamsFor(cue, 0.0);
        if (hybrid)
        {
            vparams = vparams with { ExternalAudio = true };
            if (!_running.ContainsKey(cue.Id))
                _audio.Preload(VideoAudioId(cue.Id), cue.FilePath,
                    MirrorPlayParams(cue, 0.0, cue.FadeIn));
        }
        bool visible = CanvasMode
            ? _view.IsVideoOutputVisible(0)
            : _view.IsVideoOutputVisible(ScreenOf(cue));
        if (visible && !_running.ContainsKey(cue.Id))
        {
            _video.PrepareStandby(cue.Id, cue.FilePath, vparams);
            _view.SetVideoCueOpacity(cue.Id, 0.0);   // preparado ≠ en pantalla
        }
        else
        {
            _video.PreloadNext(cue.Id, cue.FilePath);
        }
    }

    /// <summary>Timer asociado a un cue en curso: se cancela al terminar/parar.
    /// RELATIVO: para lo que cuelga de un MOTOR (fin de un media, reintentos
    /// corregidos contra su posición real), no de la secuencia.</summary>
    private void Schedule(RunningCue run, int ms, Action fn) =>
        run.Timers.Add(_scheduler.Schedule(Math.Max(0, ms), fn));

    /// <summary>Igual, pero con plazo ABSOLUTO (segundos de Clock.NowRaw):
    /// para la SECUENCIA (pre_wait, WAIT, FADE), que no debe heredar el
    /// retraso del timer que la trajo.</summary>
    private void ScheduleAt(RunningCue run, double deadline, Action fn) =>
        run.Timers.Add(_scheduler.ScheduleAt(deadline, fn));

    /// <summary>Timer de continuación independiente del ciclo de vida de un cue.
    /// Solo lo cancela Stop All / Panic. Se auto-retira al dispararse.
    /// Plazo ABSOLUTO: es la columna vertebral de las cadenas AUTO_CONTINUE.</summary>
    private void ScheduleChainAt(double deadline, Action fn)
    {
        IDisposable? handle = null;
        handle = _scheduler.ScheduleAt(deadline, () =>
        {
            if (handle is not null)
                _chainTimers.Remove(handle);
            fn();
        });
        _chainTimers.Add(handle);
    }

    // ==================================================================
    // UNDO / REDO — implementación en UndoService
    // ==================================================================

    /// <summary>true si hubo ediciones desde el último Guardar/Cargar
    /// (la vista lo usa para confirmar el cierre).</summary>
    public bool HasUnsavedChanges { get; private set; }

    /// <summary>Apunta el estado en la pila de deshacer y marca el show como
    /// modificado. Es el callback que reciben los servicios de edición.</summary>
    private void PushUndo()
    {
        HasUnsavedChanges = true;
        _undo.Push();
    }

    public bool CanUndo => _undo.CanUndo;
    public bool CanRedo => _undo.CanRedo;

    public void Undo() => _undo.Undo();
    public void Redo() => _undo.Redo();

    // ==================================================================
    // COPIAR / PEGAR / DUPLICAR / MOVER — implementación en CueListEditor
    // ==================================================================

    /// <summary>Copia los cues de las filas dadas (con sus descendientes).</summary>
    public void CopyCues(IReadOnlyList<int> rows) => _listEditor.CopyCues(rows);

    /// <summary>Pega el portapapeles debajo de la fila dada, con ids nuevos.</summary>
    public void PasteCues(int row) => _listEditor.PasteCues(row);

    /// <summary>Duplica las filas dadas justo debajo (sin tocar el portapapeles).</summary>
    public void DuplicateCues(IReadOnlyList<int> rows) => _listEditor.DuplicateCues(rows);

    /// <summary>Mueve un cue de fila (drag &amp; drop de la tabla o del timeline).</summary>
    public void OnMoveCue(int fromRow, int toRow) => _listEditor.OnMoveCue(fromRow, toRow);

    // ==================================================================
    // GESTIÓN DE LA LISTA — implementación en CueListEditor
    // ==================================================================

    public void AddNewCue(CueType cueType = CueType.Audio) => _listEditor.AddNewCue(cueType);

    /// <summary>Inserta un cue nuevo del tipo dado justo debajo de la fila row.</summary>
    public void OnInsertCue(CueType cueType, int row) => _listEditor.OnInsertCue(cueType, row);

    /// <summary>Elimina el cue de la fila dada (deteniéndolo antes si sonaba).</summary>
    public void OnDeleteCue(int row) => _listEditor.OnDeleteCue(row);

    /// <summary>Renumera todos los cues 1..n en orden de lista (con undo).</summary>
    public void RenumberCues() => _listEditor.RenumberCues();

    /// <summary>Crea un GROUP que contiene los cues seleccionados (y descendientes).</summary>
    public void OnGroupSelected(IReadOnlyList<int> rows) => _listEditor.OnGroupSelected(rows);

    /// <summary>Deshace un grupo: promueve sus hijos directos y elimina el GROUP.</summary>
    public void OnUngroup(int row) => _listEditor.OnUngroup(row);

    /// <summary>Contrae/expande un grupo (oculta/muestra sus descendientes).</summary>
    public void OnToggleCollapse(int row) => _listEditor.OnToggleCollapse(row);

    /// <summary>Crea cues a partir de archivos (drag-drop / importación).</summary>
    public void AddCuesFromFiles(int targetRow, IReadOnlyList<string> paths) =>
        _listEditor.AddCuesFromFiles(targetRow, paths);

    // ------------------------------------------------------------------
    // Edición desde el inspector
    // ------------------------------------------------------------------

    /// <summary>Aplica los cambios del inspector a un cue. Los cambios de
    /// volumen/geometría se reflejan EN VIVO si el cue está sonando.</summary>
    public void OnCueEditCommitted(int row, IReadOnlyDictionary<string, object?> fields)
    {
        var cue = _model.GetCue(row);
        if (cue is null)
            return;

        PushUndo();
        bool fileChanged = fields.TryGetValue("file_path", out var fp)
            && !string.Equals(fp?.ToString() ?? "", cue.FilePath, StringComparison.Ordinal);

        if (fields.ContainsKey("file_path") && !fields.ContainsKey("type"))
        {
            // Drag & drop: dejar que MediaImporter detecte el tipo
            MediaImporter.AssignToCue(cue, fields["file_path"]?.ToString() ?? "");
            _model.UpdateCue(row, cue);
            _view.RefreshRow(row);
            if (row == _playhead)
                _view.SetPlayhead(_playhead);
            return;
        }

        foreach (var (key, value) in fields)
            CueFields.Apply(cue, key, value);

        if (fileChanged)
        {
            cue.LatencyMs = null;
            _audio.InvalidateCache(cue.Id);            // caché de audio del cue
            _audio.InvalidateCache(VideoAudioId(cue.Id));   // y de su espejo
        }

        if (fields.ContainsKey("hotkey"))
            _view.RebuildHotkeys();

        ApplyLiveEdits(cue, fields);

        // El vídeo en STANDBY quedó precargado con los parámetros de antes de
        // la edición: re-preparar (o degradar a verificación) con los nuevos
        if (cue.Type == CueType.Video && row == _playhead
            && !_running.ContainsKey(cue.Id))
        {
            _video.DiscardStandby(cue.Id);
            if (cue.FilePath.Length > 0)
                PrepareVideoStandby(cue);
        }

        _view.RefreshRow(row);
        if (row == _playhead)
            _view.SetPlayhead(_playhead);
    }

    private static readonly HashSet<string> GeoKeys = new()
    {
        "geo_mode", "preserve_aspect", "translate_x", "translate_y",
        "scale_x", "scale_y", "rotate_z",
    };

    private void ApplyLiveEdits(Cue cue, IReadOnlyDictionary<string, object?> fields)
    {
        // La EQ se refleja también en la AUDICIÓN (no vive en _running)
        if (fields.Keys.Any(IsEqKey))
        {
            if (_audio.IsPlaying(cue.Id))
                _audio.SetEq(cue.Id, EqFor(cue));
            if (cue.Id == _auditionSourceCueId && _audio.IsPlaying(AuditionCueId))
                _audio.SetEq(AuditionCueId, EqFor(cue));
            // Paridad vídeo: la misma EQ/comp vía la cadena af de mpv
            if (cue.Type == CueType.Video && _video.IsPlaying(cue.Id))
                _video.SetEq(cue.Id, EqFor(cue));
            // Cue HAP-routed: la EQ real vive en el audio espejo (rack nativo)
            if (_mirroredAudio.Contains(cue.Id))
                _audio.SetEq(VideoAudioId(cue.Id), EqFor(cue));
        }

        // El volumen también sigue a la AUDICIÓN (no vive en _running)
        if (fields.ContainsKey("volume") &&
            cue.Id == _auditionSourceCueId && _audio.IsPlaying(AuditionCueId))
        {
            _audio.SetVolume(AuditionCueId, cue.Volume * AuditionTrim);
        }

        if (!_running.ContainsKey(cue.Id))
            return;
        if (fields.ContainsKey("volume"))
        {
            if (_audio.IsPlaying(cue.Id))
                _audio.SetVolume(cue.Id, cue.Volume);
            if (_video.IsPlaying(cue.Id))
                _video.SetVolume(cue.Id, cue.Volume);
            // Cue HAP-routed: el volumen real vive en el audio espejo
            if (_mirroredAudio.Contains(cue.Id))
                _audio.SetVolume(VideoAudioId(cue.Id), cue.Volume);
        }
        if (cue.Type == CueType.Video && _video.IsPlaying(cue.Id)
            && fields.Keys.Any(GeoKeys.Contains))
        {
            _video.ApplyGeometry(cue.Id, _canvas.GeoFor(cue));
        }
        // Opacidad de la pista en vivo (compositor GPU)
        if (cue.Type == CueType.Video && _video.IsPlaying(cue.Id)
            && fields.ContainsKey("opacity"))
        {
            SetCueLayerOpacity(cue, cue.Opacity / 100.0);
        }
    }

    private static bool IsEqKey(string key) =>
        key is "gain_db" or "eq_enabled" or "hpf_on" or "hpf_freq"
            or "lpf_on" or "lpf_freq"
        || key.StartsWith("eq", StringComparison.Ordinal)
        || key.StartsWith("comp", StringComparison.Ordinal);

    // ------------------------------------------------------------------
    // Guardar / cargar show en JSON — implementación en ShowFileService
    // (la vista pone los diálogos; aquí solo queda la delegación y el
    // flag HasUnsavedChanges, que pertenece al ciclo de edición)
    // ------------------------------------------------------------------

    /// <summary>Guarda el show. El media que vive junto al .json (o debajo) se
    /// guarda con RUTA RELATIVA → el show es portátil entre máquinas.</summary>
    public void SaveShow(string path)
    {
        if (_files.SaveShow(path))
            HasUnsavedChanges = false;
    }

    /// <summary>Guardado silencioso para el autosave (sin diálogos ni status).</summary>
    public bool Autosave(string path) => _files.Autosave(path);

    /// <summary>Exporta un show PORTÁTIL: copia los medios a una carpeta «media»
    /// junto al .json de destino y guarda con rutas relativas. El show de
    /// trabajo no se altera. convertLossyAudioToWav → audio comprimido a WAV;
    /// convertVideoToHap → vídeos a HAP .mov. Devuelve (copiados, faltantes).</summary>
    public (int Copied, int Missing) ExportPortableShow(
        string path, bool convertLossyAudioToWav = false,
        bool convertVideoToHap = false) =>
        _files.ExportPortable(path, convertLossyAudioToWav, convertVideoToHap);

    /// <summary>Carga un show: resuelve rutas relativas contra la carpeta del
    /// .json y marca en ERROR los cues cuyo media no existe.</summary>
    public void LoadShow(string path)
    {
        if (_files.LoadShow(path))
            HasUnsavedChanges = false;
    }

    /// <summary>Cues de media/script cuyo archivo NO existe en disco.</summary>
    public int MissingMediaCount() => _files.MissingMediaCount();

    /// <summary>Relocaliza los medios faltantes buscando POR NOMBRE DE
    /// ARCHIVO (recursivo) en la carpeta dada — el caso "show movido de
    /// máquina". Devuelve cuántos se recuperaron.</summary>
    public int RelocateMedia(string folder) => _files.RelocateMedia(folder);

    // ------------------------------------------------------------------
    // Cierre limpio
    // ------------------------------------------------------------------

    /// <summary>Detiene todo y libera los motores (llamar al cerrar la app).</summary>
    public void Shutdown()
    {
        _hapConvert.CancelAll();   // no dejar un ffmpeg huérfano al cerrar
        foreach (var t in _chainTimers)
            t.Dispose();
        _chainTimers.Clear();
        foreach (int cueId in _running.Keys.ToList())
            EndCue(cueId, markStopped: false);
        _audio.Cleanup();
        _video.Cleanup();
    }
}
