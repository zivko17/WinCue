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

public sealed class AudioEngine : IAudioEngine, IDisposable
{
    // ------------------------------------------------------------------
    // Eventos (espejo de las señales Qt de Python)
    // ------------------------------------------------------------------

    public event Action<int, MediaCueState>? StateChanged;   // (cueId, newState)
    public event Action<int, double>? LatencyReport;          // (cueId, ms)
    public event Action<int, string>? ErrorOccurred;          // (cueId, message)
    /// <summary>RMS estéreo (L, R) para el VU de PROGRAMA. Con el medidor
    /// loopback activo (StartProgramMeter, driver no-ASIO) mide lo que sale
    /// del dispositivo (audio + vídeo + todo); si no, cae al RMS de los
    /// streams del mixer interno (el VU aplica peak-hold y muestra el más
    /// alto).</summary>
    public event Action<float, float>? LevelReport;

    /// <summary>RMS estéreo (L, R) de UN cue concreto (inspector/audición).</summary>
    public event Action<int, float, float>? CueLevelReport;

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

    private float _master = 1.0f;
    private bool _disposed;

    // VU de programa por loopback WASAPI (null = fallback al mixer interno)
    private LoopbackProgramMeter? _loopback;
    private bool _loopbackWanted;

    private sealed record OutputBus(IWavePlayer Player, MixingSampleProvider Mixer);
    private sealed record ActiveCue(CueSampleProvider Provider, ISampleProvider MixerInput, string Bus);

    // ------------------------------------------------------------------
    // Construcción
    // ------------------------------------------------------------------

    /// <param name="dispatch">Remite un callback al main thread (en WPF:
    /// a => dispatcher.BeginInvoke(a)). null → invocar inline.</param>
    public AudioEngine(
        Action<Action>? dispatch = null,
        string? deviceId = null,
        string driver = "wasapi",
        int sampleRate = 48000,
        int latencyMs = 60,
        string? asioDriverName = null)
    {
        _dispatch = dispatch;
        _deviceId = deviceId;
        _driver = driver;
        _mixSampleRate = sampleRate;
        _latencyMs = Math.Clamp(latencyMs, 20, 500);
        _asioDriverName = asioDriverName;
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
        RestartProgramMeter();   // el loopback debe seguir al dispositivo nuevo
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

    private void RestartProgramMeter()
    {
        _loopback?.Dispose();
        _loopback = null;
        if (!_loopbackWanted)
            return;
        // ASIO real (driver asio Y drivers instalados): CreateOutput no pasará
        // por el mixer de Windows → el loopback no vería nada. Fallback a mixer.
        if (_driver.Equals("asio", StringComparison.OrdinalIgnoreCase) &&
            ListAsioDrivers().Count > 0)
            return;
        var meter = new LoopbackProgramMeter(_deviceId);
        meter.LevelReport += (l, r) => Post(() => LevelReport?.Invoke(l, r));
        meter.Start();
        _loopback = meter;
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

    /// <summary>Nombres de drivers ASIO instalados (vacío si no hay).</summary>
    public static List<string> ListAsioDrivers()
    {
        try { return AsioOut.GetDriverNames().ToList(); }
        catch (Exception) { return new List<string>(); }
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

    /// <summary>Pre-carga un cue en RAM sin reproducir (latencia cero en el GO).</summary>
    public void Preload(int cueId, string filePath) => Load(cueId, filePath);

    /// <summary>Descarta el buffer en RAM de un cue (su archivo cambió).</summary>
    public void InvalidateCache(int cueId)
    {
        _buffers.Remove(cueId);
        _states.Remove(cueId);
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

        if (_buffers.ContainsKey(cueId))
        {
            StartPlayback(cueId, clamped);
        }
        else
        {
            _pendingPlay[cueId] = clamped;
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

    public MediaCueState GetState(int cueId) =>
        _states.TryGetValue(cueId, out var s) ? s : MediaCueState.Idle;

    /// <summary>Cierra todos los streams (al salir de la aplicación).</summary>
    public void Cleanup()
    {
        foreach (int cueId in _active.Keys.ToList())
            StopCtx(cueId, emitState: false);
        CloseOutput();
        _loopbackWanted = false;
        _loopback?.Dispose();
        _loopback = null;
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

        string bus = p.OutputDevice ?? "";
        var mixer = EnsureOutput(cueId, bus);
        if (mixer is null)
            return;   // EmitError ya emitido

        var provider = new CueSampleProvider(
            cueId, buffer,
            volume: p.Volume, fadeIn: p.FadeIn, fadeOut: p.FadeOut,
            loop: p.Loop, trimStart: p.TrimStart, trimEnd: p.TrimEnd,
            playCount: p.PlayCount, eq: p.Eq);
        provider.SetMaster(_master);
        provider.LatencyMeasured += ms => Post(() => LatencyReport?.Invoke(cueId, ms));
        provider.LevelReport += (l, r) => Post(() =>
        {
            // Con loopback activo el VU de programa lo alimenta el dispositivo
            // (evita duplicar reports); los VU por-cue siguen siendo del stream
            if (_loopback is null)
                LevelReport?.Invoke(l, r);
            CueLevelReport?.Invoke(cueId, l, r);
        });

        var input = AdaptToMixerFormat(provider, p.Rate);
        _active[cueId] = new ActiveCue(provider, input, bus);
        mixer.AddMixerInput(input);
        SetState(cueId, MediaCueState.Playing);
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
        try
        {
            var mixer = new MixingSampleProvider(
                WaveFormat.CreateIeeeFloatWaveFormat(_mixSampleRate, 2))
            {
                ReadFully = true,   // silencio cuando no hay cues → stream siempre vivo
            };
            mixer.MixerInputEnded += OnMixerInputEnded;

            // CA2000: la propiedad pasa a _outputs; el catch libera en toda
            // ruta de error — el analizador no reconoce el patrón.
#pragma warning disable CA2000
            player = CreateOutput(bus);
#pragma warning restore CA2000
            player.Init(mixer);
            player.Play();
            _outputs[bus] = new OutputBus(player, mixer);
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
            var names = ListAsioDrivers();
            if (names.Count > 0)
            {
                string name = _asioDriverName is { Length: > 0 } wanted && names.Contains(wanted)
                    ? wanted : names[0];
                return new AsioOut(name);
            }
            // Sin drivers ASIO instalados → caer a WASAPI
        }

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

        return device is not null
            ? new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, _latencyMs)
            : new WasapiOut(AudioClientShareMode.Shared, _latencyMs);
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
                    SetState(cueId, MediaCueState.Stopped);
                    break;
                }
            }
        });
    }

    /// <summary>Retira el cue del mixer de su bus y del registro (parada manual).</summary>
    private void StopCtx(int cueId, bool emitState)
    {
        if (!_active.Remove(cueId, out var a))
            return;
        a.Provider.RequestStop();               // por si el mixer lee antes del Remove
        if (_outputs.TryGetValue(a.Bus, out var bus))
            bus.Mixer.RemoveMixerInput(a.MixerInput);
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
        _paused = false;
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
