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

    // Compositor GPU: renderer + superficie de la vista, por cue
    private Func<int, int, int, IVideoLayerSurface>? _gpuSurfaces;
    private bool _gpuFailed;   // el driver no pudo → no reintentar cada GO

    /// <summary>GO INSTANTÁNEO: player del cue en STANDBY ya creado y PAUSADO
    /// en su primer frame; el Play con parámetros equivalentes solo despausa.</summary>
    private sealed record StandbyPrep(int CueId, string Path, VideoPlayParams Params,
        MpvPlayer Player, MpvGpuRenderer Renderer, List<IVideoLayerSurface> Sinks);
    private StandbyPrep? _standby;
    // Red de seguridad del crossfade en la misma pista: si el saliente no
    // tiene fin propio (loop), se termina al acabar el fundido
    private readonly Dictionary<int, System.Threading.Timer> _handoverTimers = new();
    private readonly Dictionary<int, (MpvGpuRenderer Renderer, List<IVideoLayerSurface> Sinks)>
        _renderers = new();
    private readonly Dictionary<int, bool> _playWhenReady = new();
    private readonly Dictionary<int, VideoPlayParams> _playParams = new();
    private readonly Dictionary<int, double> _volumes = new();
    private readonly Dictionary<int, System.Threading.Timer> _fadeTimers = new();

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

    /// <summary>Activa el COMPOSITOR GPU (render API → superficies D3D con
    /// opacidad real; una superficie POR CUE, entregada por (salida, pista,
    /// cueId)). null = volver al modo clásico HWND.</summary>
    public void SetGpuLayerSurfaceProvider(Func<int, int, int, IVideoLayerSurface>? provider)
    {
        _gpuSurfaces = provider;
        _gpuFailed = false;
    }

    public static bool MpvAvailable => MpvPlayer.IsAvailable;

    public static List<string> ListSupportedFormats() =>
        SupportedFormats.OrderBy(x => x, StringComparer.Ordinal).ToList();

    // ------------------------------------------------------------------
    // API pública — carga / reproducción (main thread)
    // ------------------------------------------------------------------

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
        foreach (int cueId in _players.Keys.ToList())
            if (GetState(cueId) == MediaCueState.Playing)
                Stop(cueId);
    }

    public bool IsPlaying(int cueId) =>
        GetState(cueId) == MediaCueState.Playing && _players.ContainsKey(cueId);

    public bool AnyPlaying => _states.Values.Any(s => s == MediaCueState.Playing);

    /// <summary>Duración EFECTIVA en s (con el trim aplicado; 0 si mpv aún no
    /// la conoce). Coherente con GetElapsed: ambos son relativos al recorte.</summary>
    public double GetDuration(int cueId)
    {
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
        if (!_players.TryGetValue(cueId, out var pl))
            return 0.0;
        double t = pl.GetPropertyDouble("time-pos") ?? 0.0;
        double trimStart = _playParams.GetValueOrDefault(cueId)?.TrimStart ?? 0.0;
        return Math.Max(0.0, t - trimStart);
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

    private void TrySetVolume(MpvPlayer player, double v)
    {
        try { player.SetProperty("volume", Math.Clamp(v * 100.0, 0.0, 100.0) * _master); }
        catch (Exception) { }
    }

    /// <summary>Funde el volumen hacia target (0..1) en duration s.
    /// Devuelve true si el cue estaba en reproducción.</summary>
    public bool FadeVolume(int cueId, double target, double duration, bool stopAfter = false)
    {
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
    }

    /// <summary>Reanuda todos los vídeos pausados.</summary>
    public void ResumeAll()
    {
        foreach (var (cueId, player) in _players)
            if (GetState(cueId) == MediaCueState.Playing)
                try { player.SetProperty("pause", false); } catch (Exception) { }
    }

    /// <summary>Pausa SOLO este vídeo (congela reloj y frame).</summary>
    public void PauseCue(int cueId)
    {
        if (_players.TryGetValue(cueId, out var player))
            try { player.SetProperty("pause", true); } catch (Exception) { }
    }

    public void ResumeCue(int cueId)
    {
        if (_players.TryGetValue(cueId, out var player))
            try { player.SetProperty("pause", false); } catch (Exception) { }
    }

    /// <summary>Aplica la geometría a un vídeo EN REPRODUCCIÓN (en vivo).
    /// El modo FULL conserva la escala base del cue (no la pisa a 0).</summary>
    public void ApplyGeometry(int cueId, VideoGeometry geo)
    {
        if (_players.TryGetValue(cueId, out var player))
            ApplyGeo(player, geo, BaseZoom(_playParams.GetValueOrDefault(cueId)));
    }

    /// <summary>video-zoom (log2) equivalente al parámetro Scale del cue.</summary>
    private static double BaseZoom(VideoPlayParams? p) =>
        p is null || Math.Abs(p.Scale - 100.0) < 0.01
            ? 0.0
            : Math.Log2(Math.Max(1.0, p.Scale) / 100.0);

    /// <summary>Termina todos los players y fades (al salir de la aplicación).</summary>
    public void Cleanup()
    {
        DiscardStandby();
        foreach (int cueId in _fadeTimers.Keys.ToList())
            CancelFade(cueId);
        foreach (int cueId in _players.Keys.ToList())
            TerminatePlayer(cueId);
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

        var p = _playParams.GetValueOrDefault(cueId, new VideoPlayParams());
        int layer = Math.Clamp(p.Layer, 0, 7);
        bool gpu = _gpuSurfaces is not null && !_gpuFailed && _forcedVo is null;
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
            TerminatePlayer(cueId);   // instancia previa del mismo cue (retrigger)

            _players[cueId] = sb.Player;
            _playerSlots[cueId] = (screen, layer);
            _renderers[cueId] = (sb.Renderer, sb.Sinks);
            _volumes[cueId] = p.Volume;
            long tGo = System.Diagnostics.Stopwatch.GetTimestamp();
            try { sb.Player.SetProperty("pause", false); }
            catch (Exception exc)
            {
                TerminatePlayer(cueId);
                EmitError(cueId, $"GO instantáneo falló al despausar: {exc.Message}");
                return;
            }
            SetState(cueId, MediaCueState.Playing);
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

        // ── Compositor GPU: render context → superficies D3D de la pista ─
        if (gpu)
        {
            MpvGpuRenderer? renderer = null;
            try
            {
                var geo = p.Geometry ?? new VideoGeometry();
                // En modo canvas el FBO de mpv ES el canvas y cada salida
                // recibe su recorte; en clásico, una única vista completa.
                var views = canvas ? p.CanvasOutputs : null;
                renderer = new MpvGpuRenderer(player,
                    (int)geo.SurfW, (int)geo.SurfH, views);
                var sinks = new List<IVideoLayerSurface>();
                for (int i = 0; i < renderer.Views.Count; i++)
                {
                    var v = renderer.Views[i];
                    int sinkScreen = canvas ? v.Screen : Math.Clamp(p.Screen, 0, 3);
                    var sink = _gpuSurfaces!(sinkScreen, layer, cueId);
                    sink.SetSurface(renderer.ViewSurface(i), v.W, v.H);
                    renderer.FrameRendered += sink.InvalidateFrame;
                    sinks.Add(sink);
                }
                _renderers[cueId] = (renderer, sinks);
                // Confirmación visible del modo (barra de estado): sin esto es
                // imposible saber desde fuera si el compositor está activo
                ErrorOccurred?.Invoke(cueId, canvas
                    ? $"[AVISO] Compositor GPU activo — canvas {renderer.Width}×" +
                      $"{renderer.Height}, {sinks.Count} salida(s), pista {layer + 1}."
                    : $"[AVISO] Compositor GPU activo — salida {screen + 1}, pista {layer + 1}.");
            }
            catch (Exception exc)
            {
                // Driver sin interop GL↔D3D (o similar): caer al modo clásico
                // de forma permanente en esta sesión y reintentar este cue.
                // Regla mpv: el render context muere ANTES que el core → si el
                // renderer llegó a crearse (p.ej. falló el cableado de sinks),
                // liberarlo primero; sin esto quedaba un hilo de render vivo
                // apuntando a un player destruido.
                renderer?.Dispose();
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

        player.EndFile += () => Post(() =>
        {
            // Fin natural de archivo (no parada manual: esa retira el player antes)
            if (GetState(cueId) == MediaCueState.Playing && _players.ContainsKey(cueId))
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
        foreach (int otherId in _players.Keys.ToList())
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
        bool gpu = _gpuSurfaces is not null && !_gpuFailed && _forcedVo is null;
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
        if (p.Loop) options["loop-file"] = "inf";
        else if (p.PlayCount > 1) options["loop-file"] = (p.PlayCount - 1).ToString();
        if (p.TrimStart > 0.0) options["start"] = p.TrimStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        if (p.TrimEnd > 0.0) options["end"] = p.TrimEnd.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        if (isImage) options["image-display-duration"] = "inf";

        var player = new MpvPlayer(options);
        MpvGpuRenderer? renderer = null;
        var sinks = new List<IVideoLayerSurface>();
        try
        {
            var geo = p.Geometry ?? new VideoGeometry();
            // CA2000: la propiedad pasa al StandbyPrep devuelto; el catch de
            // abajo libera renderer y player en toda ruta de error.
#pragma warning disable CA2000
            renderer = new MpvGpuRenderer(player, (int)geo.SurfW, (int)geo.SurfH,
                canvas ? p.CanvasOutputs : null);
#pragma warning restore CA2000
            for (int i = 0; i < renderer.Views.Count; i++)
            {
                var v = renderer.Views[i];
                int sinkScreen = canvas ? v.Screen : Math.Clamp(p.Screen, 0, 3);
                var sink = _gpuSurfaces!(sinkScreen, layer, cueId);
                sink.SetSurface(renderer.ViewSurface(i), v.W, v.H);
                renderer.FrameRendered += sink.InvalidateFrame;
                sinks.Add(sink);
            }

            player.EndFile += () => Post(() =>
            {
                if (GetState(cueId) == MediaCueState.Playing && _players.ContainsKey(cueId))
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
            return new StandbyPrep(cueId, filePath, p, player, renderer, sinks);
        }
        catch
        {
            renderer?.Dispose();
            foreach (var s in sinks)
                s.ClearSurface();
            player.Dispose();
            throw;
        }
    }

    /// <summary>Descarta la precarga en pausa (cueId concreto o -1 = la que
    /// haya). Regla mpv: el render context muere ANTES que el core.</summary>
    public void DiscardStandby(int cueId = -1)
    {
        if (_standby is not { } sb || (cueId >= 0 && sb.CueId != cueId))
            return;
        _standby = null;
        sb.Renderer.Dispose();
        foreach (var sink in sb.Sinks)
            sink.ClearSurface();
        sb.Player.Dispose();
    }

    /// <summary>Red de seguridad del crossfade: termina al saliente cuando el
    /// fundido acaba, si nadie lo ha parado antes (p.ej. un vídeo en loop).</summary>
    private void ScheduleHandoverKill(int cueId, double seconds)
    {
        if (_handoverTimers.Remove(cueId, out var prev))
            prev.Dispose();
        var timer = new System.Threading.Timer(_ => Post(() =>
        {
            if (_handoverTimers.Remove(cueId, out var t))
                t.Dispose();
            if (_players.ContainsKey(cueId) && GetState(cueId) == MediaCueState.Playing)
            {
                CancelFade(cueId);
                SetState(cueId, MediaCueState.Stopped);
                TerminatePlayer(cueId);
            }
        }), null, (int)(Math.Max(0.05, seconds) * 1000), System.Threading.Timeout.Infinite);
        _handoverTimers[cueId] = timer;
    }

    private void TerminatePlayer(int cueId)
    {
        _playerSlots.Remove(cueId);
        if (_handoverTimers.Remove(cueId, out var handover))
            handover.Dispose();
        // Regla de mpv: el render context se libera ANTES que el core
        if (_renderers.Remove(cueId, out var r))
        {
            r.Renderer.Dispose();
            foreach (var sink in r.Sinks)
                sink.ClearSurface();
        }
        if (_players.Remove(cueId, out var player))
            player.Dispose();
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
