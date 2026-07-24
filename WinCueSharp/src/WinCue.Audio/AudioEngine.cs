// =============================================================================
// AudioEngine.cs — Motor de Audio multi-stream (port de audio_engine.py)
// =============================================================================
//
// RESPONSABILIDAD
//   Gestiona la carga (pre-decode en RAM) y la reproducción SIMULTÁNEA de
//   varios cues de audio, cada uno con su volumen, fundidos y bucle. No conoce
//   nada de la UI ni del CueModel; solo emite eventos.
//
// DIFERENCIA ARQUITECTÓNICA RESPECTO A PYTHON (deliberada)
//   Python abría un OutputStream del SO por cue (mezcla en el SO). Aquí hay UN
//   dispositivo WASAPI/ASIO persistente + MixingSampleProvider (mezcla
//   in-process, sample-accurate). El dispositivo queda abierto emitiendo
//   silencio entre cues → el GO no paga el coste de abrir stream (menor
//   latencia de disparo que la versión Python).
//
// MODELO DE HILOS
//   · MAIN THREAD : toda la API pública (Load/Play/Stop/Fade/SetVolume/…).
//   · Task pool   : decodificación de archivos (AudioDecoder). Sin UI.
//   · Hilo audio  : Read() de cada CueSampleProvider. Solo RAM y aritmética.
//   Los eventos que nacen en otros hilos se remiten por el dispatcher que se
//   pasa al constructor (en WPF: Dispatcher.BeginInvoke). Si es null, se
//   invocan en el hilo que los origina.
// =============================================================================

using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WinCue.Core;

namespace WinCue.Audio;

/// <summary>Dispositivo de salida disponible.</summary>
public sealed record AudioDeviceInfo(int Index, string Id, string Name, bool IsDefault);

/// <summary>De dónde sale el VU de PROGRAMA en cada momento. El operador tiene
/// derecho a saber qué está mirando: no es lo mismo medir el dispositivo
/// entero que medir solo lo que pasa por nuestro mixer.</summary>
public enum ProgramMeterSource
{
    /// <summary>Captura loopback del dispositivo: TODO lo que suena en él
    /// (cues, audio de los vídeos, audiciones, sonidos del sistema).</summary>
    DeviceLoopback,
    /// <summary>Mezcla interna del motor: solo los cues de audio del mixer.
    /// Es la vía con ASIO (puentea el mixer de Windows) y el respaldo cuando
    /// el loopback no puede abrirse.</summary>
    InternalMix,
}

public sealed class AudioEngine : IAudioEngine, IDisposable
{
    // ------------------------------------------------------------------
    // Eventos (espejo de las señales Qt de Python)
    // ------------------------------------------------------------------

    public event Action<int, MediaCueState>? StateChanged;   // (cueId, newState)
    public event Action<int, double>? LatencyReport;          // (cueId, ms)
    public event Action<int, string>? ErrorOccurred;          // (cueId, message)
    /// <summary>RMS estéreo (L, R) para el VU de PROGRAMA. SIEMPRE hay una vía
    /// viva: con la captura loopback funcionando mide lo que sale del
    /// dispositivo (audio + vídeo + todo) y, si esa captura no existe o no
    /// está capturando (ASIO, dispositivo caído, reintento en curso), cae al
    /// RMS de la mezcla interna del mixer. ProgramMeterSource dice cuál de las
    /// dos está alimentando el VU en este momento.</summary>
    public event Action<float, float>? LevelReport;

    /// <summary>Cambió la vía que alimenta el VU de PROGRAMA (para que la UI
    /// lo escriba en el strip MASTER).</summary>
    public event Action<ProgramMeterSource>? ProgramMeterSourceChanged;

    /// <summary>Cambió si el VU del PVW lo alimenta el loopback de audición.
    /// Existe porque los medidores se abren FUERA del hilo de UI: el estado no
    /// puede consultarse justo después de SetAuditionMeterDevice.</summary>
    public event Action<bool>? AuditionMeterActiveChanged;

    /// <summary>Cambió la salida realmente abierta (p. ej. ASIO cayó a WASAPI).
    /// La salida no se abre hasta el primer Play: por eso es un evento.</summary>
    public event Action<string>? OutputDescriptionChanged;

    /// <summary>RMS estéreo (L, R) de UN cue concreto (inspector/audición).</summary>
    public event Action<int, float, float>? CueLevelReport;

    /// <summary>RMS estéreo del dispositivo de AUDICIÓN por loopback. Solo
    /// emite con el medidor de audición activo (dispositivo propio).</summary>
    public event Action<float, float>? AuditionLevelReport;

    // ------------------------------------------------------------------
    // Estado interno
    // ------------------------------------------------------------------

    private readonly Action<Action>? _dispatch;   // marshaling a main thread
    private readonly int _mixSampleRate;
    private readonly int _latencyMs;

    private string? _deviceId;                    // null = dispositivo por defecto
    private string _driver;                       // "wasapi" | "asio"
    private string? _asioDriverName;

    // MULTI-SALIDA: un bus (dispositivo + mixer persistente) por nombre de
    // dispositivo. La clave "" es la salida principal configurada en settings.
    private readonly Dictionary<string, OutputBus> _outputs = new();
    private bool _paused;

    private readonly Dictionary<int, AudioBuffer> _buffers = new();
    private readonly Dictionary<int, MediaCueState> _states = new();
    private readonly Dictionary<int, AudioPlayParams> _pendingPlay = new();
    private readonly Dictionary<int, ActiveCue> _active = new();
    private readonly HashSet<int> _loading = new();

    // RELOJ para el motor de vídeo HAP: mapa CONCURRENTE en paralelo a
    // _active (que solo toca el main thread) para poder leer el elapsed de
    // un cue desde el hilo de presentación del vídeo sin carreras.
    // CueSampleProvider.ElapsedSeconds ya es thread-safe (Volatile.Read).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, CueSampleProvider>
        _clockProviders = new();

    // STREAMING (paso 4): fuentes ffmpeg pre-lanzadas por Preload (standby),
    // listas para consumir en el GO; y tokens de los spawns en vuelo para que
    // un Stop/replay descarte el arranque obsoleto (y mate su proceso).
    private sealed record ParkedStream(
        ICueAudioSource Source, string Path, double TrimStart, double TrimEnd);
    private readonly Dictionary<int, ParkedStream> _parkedStreams = new();
    private readonly List<int> _parkedOrder = new();       // poda FIFO (máx 2)
    private readonly Dictionary<int, long> _pendingStreamPlay = new();
    private readonly Dictionary<int, long> _pendingPark = new();
    private long _streamSeq;
    private readonly Func<string, AudioPlayParams, ICueAudioSource>? _streamingFactory;

    private float _master = 1.0f;
    private bool _disposed;

    // VU de programa por loopback WASAPI (null o sin capturar = mezcla interna)
    private LoopbackProgramMeter? _loopback;
    private bool _loopbackWanted;
    private ProgramMeterSource _meterSource = ProgramMeterSource.InternalMix;

    // VU de audición por loopback (solo si su dispositivo ≠ salida principal)
    private LoopbackProgramMeter? _auditionLoopback;
    private string _auditionMeterDevice = "";   // FriendlyName; "" = sin medidor

    // Épocas de los medidores: los abre el ThreadPool (nunca el hilo de UI) y
    // un cambio de driver posterior debe descartar el que venía en camino.
    private int _programMeterEpoch;
    private int _auditionMeterEpoch;

    // Descripción de la salida REALMENTE abierta (p. ej. "ASIO · ASIO4ALL v2"
    // o "WASAPI · ASIO no disponible"): lo que el operador ve en el mixer.
    private string _outputDescription = "";
    // El driver es ASIO pero no se pudo abrir y sonamos por WASAPI → SÍ hay
    // loopback posible (el VU vuelve a medir el dispositivo entero).
    private bool _asioFellBackToWasapi;

    // Vigilancia de salida MUDA: un driver puede abrirse y no pedir nunca
    // audio (típico de ASIO4ALL sin dispositivo WDM habilitado). El cue se ve
    // «Playing», no suena nada y el VU está a cero sin explicación.
    private readonly Dictionary<int, long> _playStartedTicks = new();
    private bool _outputStallReported;

    private sealed record OutputBus(IWavePlayer Player, MixingSampleProvider Mixer,
                                    LimiterSampleProvider Limiter);
    private sealed record ActiveCue(CueSampleProvider Provider, ISampleProvider MixerInput, string Bus);

    // ------------------------------------------------------------------
    // Construcción
    // ------------------------------------------------------------------

    /// <param name="dispatch">Remite un callback al main thread (en WPF:
    /// a => dispatcher.BeginInvoke(a)). null → invocar inline.</param>
    /// <param name="streamingFactory">Fábrica de fuentes de STREAMING
    /// inyectable (tests). null → StreamingAudioSource real (ffmpeg).</param>
    public AudioEngine(
        Action<Action>? dispatch = null,
        string? deviceId = null,
        string driver = "wasapi",
        int sampleRate = 48000,
        int latencyMs = 60,
        string? asioDriverName = null,
        Func<string, AudioPlayParams, ICueAudioSource>? streamingFactory = null)
    {
        _dispatch = dispatch;
        _deviceId = deviceId;
        _driver = driver;
        _mixSampleRate = sampleRate;
        _latencyMs = Math.Clamp(latencyMs, 20, 500);
        _asioDriverName = asioDriverName;
        _streamingFactory = streamingFactory;
    }

    // ------------------------------------------------------------------
    // API pública — dispositivo
    // ------------------------------------------------------------------

    /// <summary>Cambia el dispositivo de salida. Detiene lo que suene y
    /// reabre la salida en el siguiente Play.</summary>
    public void SetDevice(string? deviceId, string driver = "wasapi", string? asioDriverName = null)
    {
        StopAll();
        CloseOutput();
        _deviceId = deviceId;
        _driver = driver;
        _asioDriverName = asioDriverName;
        SetOutputDescription("");   // se conocerá al abrir la salida nueva
        RestartProgramMeter();   // el loopback debe seguir al dispositivo nuevo
        RestartAuditionMeter();  // y re-evaluar si audición == principal
    }

    /// <summary>Activa el VU de programa por loopback WASAPI: mide lo que
    /// REALMENTE sale del dispositivo (cues de audio, audio de vídeo, todo).
    /// Con ASIO activo no hay loopback (ASIO puentea el mixer de Windows) y
    /// LevelReport sigue alimentándose del mixer interno. Lo llama la App;
    /// los tests que no lo llaman conservan el comportamiento clásico.</summary>
    public void StartProgramMeter()
    {
        _loopbackWanted = true;
        RestartProgramMeter();
    }

    /// <summary>Vía que alimenta AHORA MISMO el VU de PROGRAMA.</summary>
    public ProgramMeterSource ProgramMeterSource => _meterSource;

    /// <summary>Salida realmente abierta ("" si aún no se abrió ninguna).
    /// Con ASIO dice QUÉ driver se abrió — o que se cayó a WASAPI.</summary>
    public string OutputDescription => _outputDescription;

    /// <summary>Configura el VU de audición por loopback. deviceName es el
    /// FriendlyName de settings (audition_device_name). Solo mide si ese
    /// dispositivo existe Y no es el mismo que la salida principal (en ese
    /// caso el VU de programa ya lo cubre y la vista usa el VU de stream).</summary>
    public void SetAuditionMeterDevice(string deviceName)
    {
        _auditionMeterDevice = deviceName;
        RestartAuditionMeter();
    }

    /// <summary>true si el VU del PVW se alimenta del loopback de audición
    /// (la vista debe ignorar entonces el VU de stream de la audición).</summary>
    public bool AuditionMeterActive => _auditionLoopback is not null;

    /// <summary>Reabre el medidor de AUDICIÓN. Todo el trabajo caro (enumerar
    /// dispositivos por COM y abrir la captura) va al ThreadPool: llamarlo
    /// desde el hilo de UI —que es lo que hace «guardar Ajustes»— no debe
    /// bloquearlo. El resultado se instala de vuelta por el dispatcher.</summary>
    private void RestartAuditionMeter()
    {
        int epoch = ++_auditionMeterEpoch;
        _auditionLoopback?.Dispose();     // ya no bloquea: libera en ThreadPool
        _auditionLoopback = null;
        AuditionMeterActiveChanged?.Invoke(false);
        if (_auditionMeterDevice.Length == 0 || _disposed)
            return;

        string wantedName = _auditionMeterDevice;
        string? mainDeviceId = _deviceId;
        string driver = _driver;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var meter = BuildAuditionMeter(wantedName, mainDeviceId, driver);
            if (meter is null)
                return;
            Post(() =>
            {
                if (_disposed || epoch != _auditionMeterEpoch)
                {
                    meter.Dispose();   // nos relevaron mientras se abría
                    return;
                }
                _auditionLoopback = meter;
                AuditionMeterActiveChanged?.Invoke(true);
            });
        });
    }

    /// <summary>THREADPOOL: decide si procede un medidor de audición y lo abre.
    /// null = no hay que medir (sin dispositivo, o es la misma salida principal
    /// y el VU de programa ya lo cubre).</summary>
    private LoopbackProgramMeter? BuildAuditionMeter(
        string wantedName, string? mainDeviceId, string driver)
    {
        var devices = ListDevices();
        string? id = devices.FirstOrDefault(d => d.Name == wantedName)?.Id;
        if (id is null)
            return null;   // dispositivo no presente ahora mismo → sin medidor

        // Mismo dispositivo que la salida principal → el PGM ya lo mide y el
        // PVW usa el VU de stream (con ASIO la principal no es WASAPI: no choca)
        if (!IsAsioActive(driver))
        {
            string mainId = mainDeviceId is { Length: > 0 }
                ? mainDeviceId
                : devices.FirstOrDefault(d => d.IsDefault)?.Id ?? "";
            if (id == mainId)
                return null;
        }

        var meter = new LoopbackProgramMeter(id, fallbackToDefault: false);
        meter.LevelReport += (l, r) => Post(() => AuditionLevelReport?.Invoke(l, r));
        meter.Start();
        return meter;
    }

    /// <summary>Reabre el medidor de PROGRAMA. Igual que el de audición: el
    /// hilo de UI solo suelta el anterior (liberación diferida) y encarga el
    /// nuevo. Mientras no haya captura viva el VU se alimenta de la mezcla
    /// interna, así que NO hay ningún instante sin VU.</summary>
    private void RestartProgramMeter()
    {
        int epoch = ++_programMeterEpoch;
        _loopback?.Dispose();
        _loopback = null;
        UpdateProgramMeterSource();
        if (!_loopbackWanted || _disposed)
            return;
        // ASIO real (driver asio Y drivers instalados) Y abierto de verdad:
        // CreateOutput no pasa por el mixer de Windows → el loopback no vería
        // nada, se mide por la mezcla interna. Si ASIO no pudo abrirse y
        // sonamos por WASAPI, el loopback SÍ vale y se recupera.
        if (IsAsioActive(_driver) && !_asioFellBackToWasapi)
            return;

        string? deviceId = _deviceId;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var meter = new LoopbackProgramMeter(deviceId);
            meter.LevelReport += (l, r) => Post(() => LevelReport?.Invoke(l, r));
            meter.Start();
            Post(() =>
            {
                if (_disposed || epoch != _programMeterEpoch)
                {
                    meter.Dispose();   // nos relevaron mientras se abría
                    return;
                }
                _loopback = meter;
                UpdateProgramMeterSource();
            });
        });
        // La captura tarda ~10 ms en abrir: hasta que IsCapturing sea true el
        // VU sigue saliendo del mixer. Un tick tardío de refresco lo cuadra.
        Post(UpdateProgramMeterSource);
    }

    /// <summary>true si el driver ASIO está realmente en juego (elegido Y con
    /// drivers instalados). La lista se cachea: leerla es COM/registro.</summary>
    private static bool IsAsioActive(string driver) =>
        driver.Equals("asio", StringComparison.OrdinalIgnoreCase) &&
        ListAsioDrivers().Count > 0;

    /// <summary>La verdad sobre el VU: el loopback manda SOLO si está
    /// capturando de verdad; en cualquier otro caso, mezcla interna.</summary>
    private void UpdateProgramMeterSource()
    {
        var now = _loopback is { IsCapturing: true }
            ? ProgramMeterSource.DeviceLoopback
            : ProgramMeterSource.InternalMix;
        if (now == _meterSource)
            return;
        _meterSource = now;
        ProgramMeterSourceChanged?.Invoke(now);
    }

    /// <summary>Reevalúa la vía del VU (la captura puede caerse o recuperarse
    /// sola por el reintento) y vigila que la salida consuma audio. La UI lo
    /// llama en su tick lento; es leer un par de campos.</summary>
    public void RefreshProgramMeterSource()
    {
        UpdateProgramMeterSource();
        CheckOutputAlive();
    }

    /// <summary>Salida MUDA: el dispositivo se abrió sin error pero no pide
    /// audio (caso real: ASIO4ALL abierto sin dispositivo WDM habilitado —
    /// AsioOut queda en Playing y jamás llama al callback). Sin esto el cue se
    /// ve «Playing», no suena nada y el VU está a cero SIN EXPLICACIÓN, que es
    /// justo lo que el operador reporta como «el VU no funciona». Se avisa una
    /// sola vez por salida abierta.</summary>
    private void CheckOutputAlive()
    {
        if (_outputStallReported || _paused || _active.Count == 0)
            return;
        long now = Environment.TickCount64;
        foreach (var (cueId, a) in _active)
        {
            if (a.Provider.IsPaused
                || !_playStartedTicks.TryGetValue(cueId, out long started)
                || now - started < 2_000)
                continue;
            if (a.Provider.ElapsedSeconds > 0.0)
                continue;   // el dispositivo sí está tirando de audio
            _outputStallReported = true;
            ErrorOccurred?.Invoke(cueId,
                $"La salida de audio no está consumiendo audio ({(_outputDescription.Length > 0 ? _outputDescription : _driver)}). " +
                "Revisa el panel de control del driver (con ASIO, que tenga un dispositivo habilitado) o vuelve a WASAPI.");
            return;
        }
    }

    private void SetOutputDescription(string text)
    {
        if (_outputDescription == text)
            return;
        _outputDescription = text;
        OutputDescriptionChanged?.Invoke(text);
    }

    /// <summary>Volumen master global (0..1), aplicado a todos los streams en vivo.</summary>
    public void SetMasterVolume(double volume)
    {
        _master = (float)Math.Clamp(volume, 0.0, 1.0);
        foreach (var a in _active.Values)
            a.Provider.SetMaster(_master);
    }

    /// <summary>Dispositivos de salida WASAPI disponibles en el sistema.</summary>
    public static List<AudioDeviceInfo> ListDevices()
    {
        var result = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string defaultId = "";
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = def.ID;
            }
            catch (Exception) { /* sin dispositivo por defecto */ }

            int idx = 0;
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (dev)
                    result.Add(new AudioDeviceInfo(idx++, dev.ID, dev.FriendlyName, dev.ID == defaultId));
            }
        }
        catch (Exception) { /* sin subsistema de audio */ }
        return result;
    }

    // La lista de drivers ASIO se lee por COM/registro (hasta 2 ms medidos) y
    // la consultan SetDevice y los medidores. Se cachea: instalar un driver
    // ASIO requiere instalador (y normalmente reinicio), no pasa en mitad de
    // un show. El botón «Refrescar dispositivos» de Ajustes la invalida.
    private static List<string>? _asioDriversCache;
    private static readonly object _asioCacheGate = new();

    /// <summary>Nombres de drivers ASIO instalados (vacío si no hay). Cacheado.</summary>
    public static List<string> ListAsioDrivers()
    {
        lock (_asioCacheGate)
        {
            if (_asioDriversCache is null)
            {
                try { _asioDriversCache = AsioOut.GetDriverNames().ToList(); }
                catch (Exception) { _asioDriversCache = new List<string>(); }
            }
            return new List<string>(_asioDriversCache);
        }
    }

    /// <summary>Olvida la caché de drivers ASIO (Ajustes → Refrescar).</summary>
    public static void RefreshAsioDrivers()
    {
        lock (_asioCacheGate) { _asioDriversCache = null; }
    }

    // ------------------------------------------------------------------
    // API pública — carga
    // ------------------------------------------------------------------

    /// <summary>Inicia la carga asíncrona del archivo en RAM (estado LOADING).</summary>
    public void Load(int cueId, string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            EmitError(cueId, "Ruta de archivo vacía; asigna un archivo al cue.");
            return;
        }
        if (_buffers.ContainsKey(cueId))
        {
            SetState(cueId, MediaCueState.Loaded);
            return;
        }
        if (_loading.Contains(cueId))
            return;
        SetState(cueId, MediaCueState.Loading);
        LaunchLoader(cueId, filePath);
    }

    /// <summary>Pre-carga un cue sin reproducir (latencia cero en el GO).
    /// Ruta RAM: decode a buffer. Con p.Streaming (audio espejo, paso 4):
    /// pre-spawn del ffmpeg con el ring llenándose, APARCADO sin consumir —
    /// el Play con los mismos trims lo toma tal cual (GO instantáneo).</summary>
    public void Preload(int cueId, string filePath, AudioPlayParams? p = null)
    {
        if (p is not null && UseStreaming(p))
        {
            PreloadStreaming(cueId, filePath, p);
            return;
        }
        Load(cueId, filePath);
    }

    /// <summary>Descarta el buffer en RAM de un cue (su archivo cambió) y, si
    /// tenía una fuente de streaming aparcada, la mata (cero zombis).</summary>
    public void InvalidateCache(int cueId)
    {
        _buffers.Remove(cueId);
        _states.Remove(cueId);
        _pendingStreamPlay.Remove(cueId);
        _pendingPark.Remove(cueId);
        if (_parkedStreams.Remove(cueId, out var parked))
        {
            _parkedOrder.Remove(cueId);
            parked.Source.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // API pública — reproducción
    // ------------------------------------------------------------------

    /// <summary>Reproduce el cue. Si ya está sonando, lo reinicia desde el
    /// principio. Si el buffer está en RAM arranca de inmediato; si no, carga
    /// primero y arranca al terminar (parámetros en _pendingPlay).</summary>
    public void Play(int cueId, string filePath, AudioPlayParams? p = null)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            EmitError(cueId, "No hay archivo asignado a este cue de audio.");
            return;
        }

        p ??= new AudioPlayParams();
        var clamped = p with
        {
            Volume = Math.Clamp(p.Volume, 0.0, 1.0),
            FadeIn = Math.Max(0.0, p.FadeIn),
            FadeOut = Math.Max(0.0, p.FadeOut),
            TrimStart = Math.Max(0.0, p.TrimStart),
            TrimEnd = Math.Max(0.0, p.TrimEnd),
            PlayCount = Math.Max(1, p.PlayCount),
        };

        // Reinicio si ya estaba sonando
        if (_active.ContainsKey(cueId))
            StopCtx(cueId, emitState: false);

        // STREAMING (paso 4, audio espejo): ffmpeg → ring acotado. Si el
        // spawn falla, StartStreamingPlayback cae SOLO a la ruta RAM clásica.
        if (UseStreaming(clamped))
        {
            StartStreamingPlayback(cueId, filePath, clamped);
            return;
        }

        PlayClassic(cueId, filePath, clamped);
    }

    /// <summary>Ruta RAM clásica: buffer pre-decodificado (cargando primero
    /// si hace falta). También es el FALLBACK del streaming.</summary>
    private void PlayClassic(int cueId, string filePath, AudioPlayParams p)
    {
        if (_buffers.ContainsKey(cueId))
        {
            StartPlayback(cueId, p);
        }
        else
        {
            _pendingPlay[cueId] = p;
            if (!_loading.Contains(cueId))
            {
                SetState(cueId, MediaCueState.Loading);
                LaunchLoader(cueId, filePath);
            }
        }
    }

    /// <summary>Detiene un cue. fade &gt; 0 → fundido de salida antes de parar.</summary>
    public void Stop(int cueId, double fade = 0.0)
    {
        _pendingStreamPlay.Remove(cueId);   // descarta un spawn en vuelo
        if (!_active.TryGetValue(cueId, out var a))
            return;
        if (fade > 0.0)
            a.Provider.BeginFade(0.0, fade, stopAfter: true);
        else
            StopCtx(cueId, emitState: true);
    }

    /// <summary>Detiene TODOS los cues de audio activos (opcionalmente con fade).</summary>
    public void StopAll(double fade = 0.0)
    {
        foreach (int cueId in _active.Keys.ToList())
            Stop(cueId, fade);
    }

    /// <summary>Funde el volumen de un cue activo hacia target en duration s.
    /// Devuelve true si el cue estaba activo y el fade se inició.</summary>
    public bool Fade(int cueId, double targetVolume, double duration, bool stopAfter = false)
    {
        if (!_active.TryGetValue(cueId, out var a))
            return false;
        a.Provider.BeginFade(
            Math.Clamp(targetVolume, 0.0, 1.0),
            Math.Max(0.0, duration),
            stopAfter);
        return true;
    }

    /// <summary>Fija instantáneamente el volumen de un cue activo.</summary>
    public void SetVolume(int cueId, double volume)
    {
        if (_active.TryGetValue(cueId, out var a))
            a.Provider.SetVolume(volume);
    }

    /// <summary>Pausa todas las reproducciones activas (congela posición).</summary>
    public void PauseAll()
    {
        if (_paused || _outputs.Count == 0)
            return;
        _paused = true;
        foreach (var bus in _outputs.Values)
            try { bus.Player.Pause(); } catch (Exception) { }
    }

    /// <summary>Reanuda las reproducciones pausadas desde su posición congelada.</summary>
    public void ResumeAll()
    {
        if (!_paused || _outputs.Count == 0)
            return;
        _paused = false;
        foreach (var bus in _outputs.Values)
            try { bus.Player.Play(); } catch (Exception) { }
    }

    /// <summary>Pausa SOLO este cue (silencio, posición congelada).</summary>
    public void PauseCue(int cueId)
    {
        if (_active.TryGetValue(cueId, out var a))
            a.Provider.SetPaused(true);
    }

    public void ResumeCue(int cueId)
    {
        if (_active.TryGetValue(cueId, out var a))
            a.Provider.SetPaused(false);
    }

    public bool IsPlaying(int cueId) => _active.ContainsKey(cueId);

    public bool AnyPlaying => _active.Count > 0;

    /// <summary>Reconfigura la EQ/ganancia de un cue sonando (en vivo).</summary>
    public void SetEq(int cueId, AudioEqParams eq)
    {
        if (_active.TryGetValue(cueId, out var a))
            a.Provider.SetEq(eq);
    }

    /// <summary>Espectro post-EQ del cue para el RTA (null si no suena).</summary>
    public float[]? GetSpectrum(int cueId) =>
        _active.TryGetValue(cueId, out var a) ? a.Provider.GetSpectrum() : null;

    /// <summary>Reducción de ganancia del compresor (dB ≥ 0; 0 si no suena).</summary>
    public float GetGainReductionDb(int cueId) =>
        _active.TryGetValue(cueId, out var a) ? a.Provider.GetGainReductionDb() : 0f;

    /// <summary>Reducción de ganancia del LIMITADOR del bus principal (dB ≥ 0)
    /// para el medidor GR del strip master. 0 si la salida aún no se abrió.</summary>
    public float GetMasterGainReductionDb() =>
        _outputs.TryGetValue("", out var bus) ? bus.Limiter.GainReductionDb : 0f;

    /// <summary>Duración del audio en s (de la sesión activa —con trim— o del buffer).</summary>
    public double GetDuration(int cueId)
    {
        if (_active.TryGetValue(cueId, out var a))
            return a.Provider.DurationSeconds;
        return _buffers.TryGetValue(cueId, out var buf) ? buf.DurationSeconds : 0.0;
    }

    /// <summary>Segundos reproducidos del cue activo (posición de la pasada actual).</summary>
    public double GetElapsed(int cueId) =>
        _active.TryGetValue(cueId, out var a) ? a.Provider.ElapsedSeconds : 0.0;

    /// <summary>Elapsed del cue LEÍBLE DESDE CUALQUIER HILO (reloj maestro del
    /// motor de vídeo HAP), o null si el cue no suena. _active es del main
    /// thread; este mapa concurrente existe justo para esta consulta.</summary>
    public double? GetElapsedThreadSafe(int cueId) =>
        _clockProviders.TryGetValue(cueId, out var p) ? p.ElapsedSeconds : null;

    public MediaCueState GetState(int cueId) =>
        _states.TryGetValue(cueId, out var s) ? s : MediaCueState.Idle;

    /// <summary>Cierra todos los streams (al salir de la aplicación).</summary>
    public void Cleanup()
    {
        foreach (int cueId in _active.Keys.ToList())
            StopCtx(cueId, emitState: false);
        // Streaming: descartar spawns en vuelo y matar los pre-spawns aparcados
        _pendingStreamPlay.Clear();
        _pendingPark.Clear();
        foreach (var parked in _parkedStreams.Values)
            parked.Source.Dispose();
        _parkedStreams.Clear();
        _parkedOrder.Clear();
        CloseOutput();
        // Épocas al día: un medidor que se estuviera abriendo en el ThreadPool
        // se descarta al aterrizar (y se libera) en vez de quedarse vivo.
        _programMeterEpoch++;
        _auditionMeterEpoch++;
        _loopbackWanted = false;
        _loopback?.Dispose();
        _loopback = null;
        _auditionMeterDevice = "";
        _auditionLoopback?.Dispose();
        _auditionLoopback = null;
        UpdateProgramMeterSource();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup();
    }

    // ------------------------------------------------------------------
    // Carga asíncrona (equivalente a _LoaderWorker + QThread)
    // ------------------------------------------------------------------

    private void LaunchLoader(int cueId, string filePath)
    {
        _loading.Add(cueId);
        Task.Run(() =>
        {
            try
            {
                var buffer = AudioDecoder.Decode(filePath);
                Post(() => OnLoaded(cueId, buffer));
            }
            catch (Exception exc)
            {
                Post(() => OnLoadFailed(cueId, exc.Message));
            }
        });
    }

    private void OnLoaded(int cueId, AudioBuffer buffer)
    {
        _buffers[cueId] = buffer;
        _loading.Remove(cueId);
        SetState(cueId, MediaCueState.Loaded);
        if (_pendingPlay.Remove(cueId, out var p))
            StartPlayback(cueId, p);
    }

    private void OnLoadFailed(int cueId, string message)
    {
        _loading.Remove(cueId);
        _pendingPlay.Remove(cueId);
        EmitError(cueId, $"Error al cargar audio: {message}");
    }

    // ------------------------------------------------------------------
    // Reproducción — mixer compartido con un CueSampleProvider por cue
    // ------------------------------------------------------------------

    private void StartPlayback(int cueId, AudioPlayParams p)
    {
        if (!_buffers.TryGetValue(cueId, out var buffer))
        {
            EmitError(cueId, "Buffer en RAM no disponible para reproducción.");
            return;
        }
        StartPlaybackWithSource(cueId,
            new RamAudioSource(buffer, p.TrimStart, p.TrimEnd), p);
    }

    /// <summary>Arranca la reproducción sobre una fuente ya construida (RAM o
    /// streaming): provider + registro + mixer. Común a ambas rutas.</summary>
    private void StartPlaybackWithSource(int cueId, ICueAudioSource source, AudioPlayParams p)
    {
        string bus = p.OutputDevice ?? "";
        var mixer = EnsureOutput(cueId, bus);
        if (mixer is null)
        {
            source.Dispose();   // EmitError ya emitido; sin ffmpeg huérfano
            return;
        }

        // CA2000: la propiedad del provider pasa a _active (y se libera en
        // StopCtx/OnMixerInputEnded); el analizador no reconoce el patrón.
#pragma warning disable CA2000
        var provider = new CueSampleProvider(
            cueId, source,
            volume: p.Volume, fadeIn: p.FadeIn, fadeOut: p.FadeOut,
            loop: p.Loop, playCount: p.PlayCount, eq: p.Eq);
#pragma warning restore CA2000
        provider.SetMaster(_master);
        provider.LatencyMeasured += ms => Post(() => LatencyReport?.Invoke(cueId, ms));
        provider.LevelReport += (l, r) => Post(() =>
        {
            // El VU de PROGRAMA lo alimenta el dispositivo SOLO si su captura
            // loopback está viva de verdad (IsCapturing, no «existe el objeto»):
            // con ASIO, con el loopback caído o mientras reintenta, la mezcla
            // interna toma el relevo — el VU nunca se queda a cero por diseño.
            // Los VU por-cue siguen siendo siempre del stream.
            if (_loopback is not { IsCapturing: true })
                LevelReport?.Invoke(l, r);
            CueLevelReport?.Invoke(cueId, l, r);
        });

        // El ffmpeg del streaming murió MAL en caliente → cortar el espejo y
        // subir el error (el controlador recupera el audio por mpv). Cortarlo
        // evita el solape: el ring aún tendría ~3 s decodificados por delante.
        if (source is StreamingAudioSource streaming)
            streaming.FatalError += msg => Post(() => OnStreamFatal(cueId, provider, msg));

        var input = AdaptToMixerFormat(provider, p.Rate);
        _active[cueId] = new ActiveCue(provider, input, bus);
        _clockProviders[cueId] = provider;
        _playStartedTicks[cueId] = Environment.TickCount64;
        mixer.AddMixerInput(input);
        SetState(cueId, MediaCueState.Playing);
    }

    // ------------------------------------------------------------------
    // STREAMING (paso 4) — audio espejo por ffmpeg subproceso + ring
    // ------------------------------------------------------------------

    /// <summary>true si estos parámetros van por STREAMING: lo pide el
    /// controlador (p.Streaming, espejos de vídeo), el kill-switch de directo
    /// WINCUE_MIRROR_STREAMING=0 no lo veta, y hay ffmpeg (o factory de tests).</summary>
    private bool UseStreaming(AudioPlayParams p) =>
        p.Streaming
        && Environment.GetEnvironmentVariable("WINCUE_MIRROR_STREAMING") != "0"
        && (_streamingFactory is not null || AudioDecoder.IsFFmpegAvailable);

    // CA2000: la propiedad de la fuente pasa al llamador (provider activo o
    // aparcamiento del preload); ambos la liberan — patrón no reconocido.
#pragma warning disable CA2000
    private ICueAudioSource CreateStreamingSource(string filePath, AudioPlayParams p) =>
        _streamingFactory is not null
            ? _streamingFactory(filePath, p)
            : new StreamingAudioSource(filePath, _mixSampleRate, p.TrimStart, p.TrimEnd);
#pragma warning restore CA2000

    /// <summary>true para los tests: hay una fuente de streaming aparcada.</summary>
    public bool HasParkedStream(int cueId) => _parkedStreams.ContainsKey(cueId);

    /// <summary>Pre-spawn del standby: construye la fuente en segundo plano y
    /// la aparca llena, sin consumir. Idempotente si ya hay una compatible.</summary>
    private void PreloadStreaming(int cueId, string filePath, AudioPlayParams p)
    {
        if (_parkedStreams.TryGetValue(cueId, out var parked))
        {
            if (parked.Path == filePath
                && parked.TrimStart == p.TrimStart && parked.TrimEnd == p.TrimEnd)
                return;   // ya hay un pre-spawn compatible aparcado
            _parkedStreams.Remove(cueId);
            _parkedOrder.Remove(cueId);
            parked.Source.Dispose();
        }

        long token = ++_streamSeq;
        _pendingPark[cueId] = token;
        Task.Run(() =>
        {
            ICueAudioSource src;
            try
            {
                src = CreateStreamingSource(filePath, p);
            }
            catch (Exception)
            {
                return;   // el GO caerá al spawn en vivo (y de ahí a RAM)
            }
            Post(() =>
            {
                // Solo aparca si nadie lo relevó mientras se construía
                if (_disposed || !_pendingPark.TryGetValue(cueId, out long t) || t != token
                    || _parkedStreams.ContainsKey(cueId))
                {
                    src.Dispose();
                    return;
                }
                _pendingPark.Remove(cueId);
                _parkedStreams[cueId] = new ParkedStream(src, filePath, p.TrimStart, p.TrimEnd);
                _parkedOrder.Add(cueId);
                // Poda FIFO: el standby real es 1; dejar margen de 2 evita
                // matar el bueno en un cambio rápido de playhead
                while (_parkedOrder.Count > 2)
                {
                    int victim = _parkedOrder[0];
                    _parkedOrder.RemoveAt(0);
                    if (_parkedStreams.Remove(victim, out var old))
                        old.Source.Dispose();
                }
            });
        });
    }

    /// <summary>Toma (y desaparca) la fuente pre-lanzada si es compatible con
    /// los parámetros del Play; una incompatible se mata y se devuelve null.</summary>
    private ICueAudioSource? TakeParkedStream(int cueId, string filePath, AudioPlayParams p)
    {
        if (!_parkedStreams.Remove(cueId, out var parked))
            return null;
        _parkedOrder.Remove(cueId);
        if (parked.Path == filePath
            && parked.TrimStart == p.TrimStart && parked.TrimEnd == p.TrimEnd)
            return parked.Source;
        parked.Source.Dispose();
        return null;
    }

    /// <summary>Play por streaming: consume el pre-spawn del standby (GO
    /// instantáneo) o lanza ffmpeg en segundo plano. FALLBACK EN CASCADA: si
    /// el spawn falla → ruta RAM clásica; si esa también falla, su propio
    /// EmitError dispara la red de seguridad del controlador.</summary>
    private void StartStreamingPlayback(int cueId, string filePath, AudioPlayParams p)
    {
        _pendingPark.Remove(cueId);   // un pre-spawn en vuelo ya no se aparca
        if (TakeParkedStream(cueId, filePath, p) is { } ready)
        {
            StartPlaybackWithSource(cueId, ready, p);
            return;
        }

        SetState(cueId, MediaCueState.Loading);
        long token = ++_streamSeq;
        _pendingStreamPlay[cueId] = token;
        Task.Run(() =>
        {
            try
            {
                var src = CreateStreamingSource(filePath, p);
                Post(() =>
                {
                    if (_disposed || !_pendingStreamPlay.TryGetValue(cueId, out long t) || t != token)
                    {
                        src.Dispose();   // un Stop/replay nos relevó: sin zombis
                        return;
                    }
                    _pendingStreamPlay.Remove(cueId);
                    StartPlaybackWithSource(cueId, src, p);
                });
            }
            catch (Exception exc)
            {
                Post(() =>
                {
                    if (_disposed || !_pendingStreamPlay.TryGetValue(cueId, out long t) || t != token)
                        return;
                    _pendingStreamPlay.Remove(cueId);
                    // Spawn fallido → reintento por la ruta RAM de siempre
                    _ = exc;
                    PlayClassic(cueId, filePath, p);
                });
            }
        });
    }

    /// <summary>El ffmpeg de un cue en streaming murió mal en caliente:
    /// retirar el espejo (sin estado STOPPED: lo que sigue es un ERROR) y
    /// subirlo — el controlador restaura el audio por mpv.</summary>
    private void OnStreamFatal(int cueId, CueSampleProvider provider, string message)
    {
        // Solo si ESTE provider sigue siendo el activo (no un replay posterior)
        if (!_active.TryGetValue(cueId, out var a) || !ReferenceEquals(a.Provider, provider))
            return;
        StopCtx(cueId, emitState: false);
        EmitError(cueId, $"Streaming de audio caído: {message}");
    }

    /// <summary>Adapta canales y samplerate del cue al formato del mixer
    /// (estéreo, _mixSampleRate). El fade se calcula ANTES de resamplear,
    /// en frames del archivo → sample-accurate respecto al material.
    /// rate ≠ 1 → VARISPEED (estilo cinta: la velocidad cambia el tono): se
    /// resamplea a mixRate/rate y se declara mixRate, con lo que el mixer
    /// consume el material rate× más rápido/lento.</summary>
    private ISampleProvider AdaptToMixerFormat(ISampleProvider source, double rate = 1.0)
    {
        // Canales → estéreo
        if (source.WaveFormat.Channels == 1)
            source = new MonoToStereoSampleProvider(source);
        else if (source.WaveFormat.Channels > 2)
            source = new TakeFirstChannelsProvider(source, 2);

        rate = Math.Clamp(rate, 0.25, 4.0);
        int targetRate = Math.Abs(rate - 1.0) < 0.001
            ? _mixSampleRate
            : Math.Max(4000, (int)(_mixSampleRate / rate));

        if (source.WaveFormat.SampleRate != targetRate)
            source = new WdlResamplingSampleProvider(source, targetRate);

        if (targetRate != _mixSampleRate)
            source = new ClaimedFormatProvider(source,
                WaveFormat.CreateIeeeFloatWaveFormat(_mixSampleRate, 2));

        return source;
    }

    /// <summary>Pasa el audio tal cual pero DECLARA otro formato (truco del
    /// varispeed: el mixer consume samples "lentos" a su propia velocidad).</summary>
    private sealed class ClaimedFormatProvider : ISampleProvider
    {
        private readonly ISampleProvider _inner;
        public WaveFormat WaveFormat { get; }

        public ClaimedFormatProvider(ISampleProvider inner, WaveFormat claimed)
        {
            _inner = inner;
            WaveFormat = claimed;
        }

        public int Read(float[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);
    }

    /// <summary>Devuelve (creando si hace falta) el mixer del bus dado.
    /// bus "" = salida principal; otro valor = nombre de dispositivo.</summary>
    private MixingSampleProvider? EnsureOutput(int cueId, string bus)
    {
        if (_outputs.TryGetValue(bus, out var existing))
            return existing.Mixer;
        IWavePlayer? player = null;
        bool asioFallbackBefore = _asioFellBackToWasapi;
        try
        {
            var mixer = new MixingSampleProvider(
                WaveFormat.CreateIeeeFloatWaveFormat(_mixSampleRate, 2))
            {
                ReadFully = true,   // silencio cuando no hay cues → stream siempre vivo
            };
            mixer.MixerInputEnded += OnMixerInputEnded;

            // Limitador brickwall al final del bus: la suma de N cues no
            // puede saturar el dispositivo (techo −1 dBFS, release 150 ms)
            var limiter = new LimiterSampleProvider(mixer);

            // CA2000: la propiedad pasa a _outputs; el catch libera en toda
            // ruta de error — el analizador no reconoce el patrón.
#pragma warning disable CA2000
            player = CreateOutput(bus);
#pragma warning restore CA2000
            player.Init(limiter);
            player.Play();
            _outputs[bus] = new OutputBus(player, mixer, limiter);
            // El driver ASIO no pudo abrirse y sonamos por WASAPI: entonces SÍ
            // hay loopback y el VU vuelve a medir el dispositivo entero.
            if (bus.Length == 0 && _asioFellBackToWasapi && !asioFallbackBefore)
                RestartProgramMeter();
            return mixer;
        }
        catch (Exception exc)
        {
            // Si Init/Play fallan tras crear la salida, liberar el dispositivo
            // (antes se filtraba el handle WASAPI/ASIO en cada reintento)
            try { player?.Dispose(); } catch (Exception) { }
            EmitError(cueId, $"No se pudo abrir el dispositivo de audio" +
                (bus.Length > 0 ? $" '{bus}'" : "") + $": {exc.Message}");
            return null;
        }
    }

    private IWavePlayer CreateOutput(string bus)
    {
        // Bus con nombre: buscar dispositivo WASAPI o driver ASIO por nombre
        if (bus.Length > 0)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    if (dev.FriendlyName == bus)
                        return new WasapiOut(dev, AudioClientShareMode.Shared,
                            useEventSync: true, _latencyMs);
                    dev.Dispose();
                }
            }
            catch (Exception) { }
            if (ListAsioDrivers().Contains(bus))
                return new AsioOut(bus);
            // Dispositivo desaparecido → caer a la salida por defecto
            return new WasapiOut(AudioClientShareMode.Shared, _latencyMs);
        }

        // Bus principal: configuración de settings (driver + device)
        if (_driver.Equals("asio", StringComparison.OrdinalIgnoreCase))
        {
            // CA2000: la propiedad pasa al llamador (EnsureOutput la guarda en
            // _outputs o la libera en su catch) — patrón no reconocido.
#pragma warning disable CA2000
            var asio = OpenAsio();
#pragma warning restore CA2000
            if (asio is not null)
                return asio;
            // Sin ASIO utilizable → WASAPI. En directo es preferible sonar por
            // el dispositivo del sistema (con VU) a quedarse mudo; el mixer
            // avisa de la caída con OutputDescription.
            _asioFellBackToWasapi = true;
            SetOutputDescription(ListAsioDrivers().Count == 0
                ? "WASAPI · sin drivers ASIO instalados"
                : "WASAPI · ningún driver ASIO pudo abrirse");
        }
        else
        {
            SetOutputDescription("");
        }

        // El MMDevice pasa a ser propiedad del WasapiOut (lo libera él); si no
        // llegamos a construirlo hay que soltarlo o se filtra un handle COM.
        MMDevice? device = null;
        if (_deviceId is { Length: > 0 })
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                device = enumerator.GetDevice(_deviceId);
            }
            catch (Exception) { device = null; }   // id obsoleto → por defecto
        }

        try
        {
            return device is not null
                ? new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, _latencyMs)
                : new WasapiOut(AudioClientShareMode.Shared, _latencyMs);
        }
        catch (Exception)
        {
            device?.Dispose();
            throw;
        }
    }

    /// <summary>Abre la salida ASIO. El driver CONFIGURADO manda; si no hay
    /// ninguno configurado se prueban los instalados en orden hasta que uno
    /// abra — antes se cogía names[0] a ciegas y bastaba con tener instalado
    /// un driver de un cacharro desconectado (típico: una interfaz USB) para
    /// que ASIO fallara SIEMPRE: sin salida, sin sonido y sin VU.
    /// null = ningún ASIO utilizable.</summary>
    private AsioOut? OpenAsio()
    {
        var names = ListAsioDrivers();
        if (names.Count == 0)
            return null;

        var order = new List<string>();
        if (_asioDriverName is { Length: > 0 } wanted && names.Contains(wanted))
            order.Add(wanted);   // el elegido por el operador: solo ese
        else
            order.AddRange(names);

        foreach (string name in order)
        {
            try
            {
                var outp = new AsioOut(name);
                SetOutputDescription($"ASIO · {name}");
                return outp;
            }
            catch (Exception)
            {
                // Cacharro desconectado / driver ocupado → probar el siguiente
            }
        }
        return null;
    }

    /// <summary>El mixer retiró un input porque su Read devolvió 0 (fin natural
    /// o request_stop). Llega en el hilo de audio → remitir al main thread.</summary>
    private void OnMixerInputEnded(object? sender, SampleProviderEventArgs e)
    {
        Post(() =>
        {
            foreach (var (cueId, a) in _active)
            {
                if (ReferenceEquals(a.MixerInput, e.SampleProvider))
                {
                    _active.Remove(cueId);
                    _playStartedTicks.Remove(cueId);
                    _clockProviders.TryRemove(cueId, out _);
                    a.Provider.Dispose();   // streaming: mata el ffmpeg
                    SetState(cueId, MediaCueState.Stopped);
                    break;
                }
            }
        });
    }

    /// <summary>Retira el cue del mixer de su bus y del registro (parada manual).</summary>
    private void StopCtx(int cueId, bool emitState)
    {
        _clockProviders.TryRemove(cueId, out _);
        _playStartedTicks.Remove(cueId);
        if (!_active.Remove(cueId, out var a))
            return;
        a.Provider.RequestStop();               // por si el mixer lee antes del Remove
        if (_outputs.TryGetValue(a.Bus, out var bus))
            bus.Mixer.RemoveMixerInput(a.MixerInput);
        a.Provider.Dispose();                   // streaming: mata el ffmpeg
        if (emitState)
            SetState(cueId, MediaCueState.Stopped);
    }

    private void CloseOutput()
    {
        foreach (var bus in _outputs.Values)
        {
            try { bus.Player.Stop(); } catch (Exception) { }
            try { bus.Player.Dispose(); } catch (Exception) { }
        }
        _outputs.Clear();
        _playStartedTicks.Clear();
        _paused = false;
        _asioFellBackToWasapi = false;
        _outputStallReported = false;
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

    /// <summary>Pasa los N primeros canales de una fuente multicanal (>2ch).</summary>
    private sealed class TakeFirstChannelsProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _sourceChannels;
        private float[] _work = Array.Empty<float>();

        public WaveFormat WaveFormat { get; }

        public TakeFirstChannelsProvider(ISampleProvider source, int channels)
        {
            _source = source;
            _sourceChannels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, channels);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int outChannels = WaveFormat.Channels;
            int frames = count / outChannels;
            int needed = frames * _sourceChannels;
            if (_work.Length < needed)
                _work = new float[needed];

            int readSamples = _source.Read(_work, 0, needed);
            int readFrames = readSamples / _sourceChannels;
            for (int f = 0; f < readFrames; f++)
                for (int c = 0; c < outChannels; c++)
                    buffer[offset + f * outChannels + c] = _work[f * _sourceChannels + c];
            return readFrames * outChannels;
        }
    }
}
