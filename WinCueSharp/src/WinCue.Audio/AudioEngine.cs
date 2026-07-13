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
    /// <summary>Nivel RMS instantáneo (0.0–1.0) de un stream cualquiera; el VU
    /// aplica peak-hold, así que con varios streams muestra el más alto.</summary>
    public event Action<float>? LevelReport;

    // ------------------------------------------------------------------
    // Estado interno
    // ------------------------------------------------------------------

    private readonly Action<Action>? _dispatch;   // marshaling a main thread
    private readonly int _mixSampleRate;
    private readonly int _latencyMs;

    private string? _deviceId;                    // null = dispositivo por defecto
    private string _driver;                       // "wasapi" | "asio"
    private string? _asioDriverName;

    private IWavePlayer? _output;
    private MixingSampleProvider? _mixer;
    private bool _paused;

    private readonly Dictionary<int, AudioBuffer> _buffers = new();
    private readonly Dictionary<int, MediaCueState> _states = new();
    private readonly Dictionary<int, AudioPlayParams> _pendingPlay = new();
    private readonly Dictionary<int, ActiveCue> _active = new();
    private readonly HashSet<int> _loading = new();

    private float _master = 1.0f;
    private bool _disposed;

    private sealed record ActiveCue(CueSampleProvider Provider, ISampleProvider MixerInput);

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
        if (_paused || _output is null)
            return;
        _paused = true;
        try { _output.Pause(); } catch (Exception) { }
    }

    /// <summary>Reanuda las reproducciones pausadas desde su posición congelada.</summary>
    public void ResumeAll()
    {
        if (!_paused || _output is null)
            return;
        _paused = false;
        try { _output.Play(); } catch (Exception) { }
    }

    public bool IsPlaying(int cueId) => _active.ContainsKey(cueId);

    public bool AnyPlaying => _active.Count > 0;

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
        if (!EnsureOutput())
        {
            return;   // EmitError ya emitido
        }

        var provider = new CueSampleProvider(
            cueId, buffer,
            volume: p.Volume, fadeIn: p.FadeIn, fadeOut: p.FadeOut,
            loop: p.Loop, trimStart: p.TrimStart, trimEnd: p.TrimEnd,
            playCount: p.PlayCount);
        provider.SetMaster(_master);
        provider.LatencyMeasured += ms => Post(() => LatencyReport?.Invoke(cueId, ms));
        provider.LevelReport += rms => Post(() => LevelReport?.Invoke(rms));

        var input = AdaptToMixerFormat(provider);
        _active[cueId] = new ActiveCue(provider, input);
        _mixer!.AddMixerInput(input);
        SetState(cueId, MediaCueState.Playing);
    }

    /// <summary>Adapta canales y samplerate del cue al formato del mixer
    /// (estéreo, _mixSampleRate). El fade se calcula ANTES de resamplear,
    /// en frames del archivo → sample-accurate respecto al material.</summary>
    private ISampleProvider AdaptToMixerFormat(ISampleProvider source)
    {
        // Canales → estéreo
        if (source.WaveFormat.Channels == 1)
            source = new MonoToStereoSampleProvider(source);
        else if (source.WaveFormat.Channels > 2)
            source = new TakeFirstChannelsProvider(source, 2);

        // Samplerate → el del mixer
        if (source.WaveFormat.SampleRate != _mixSampleRate)
            source = new WdlResamplingSampleProvider(source, _mixSampleRate);

        return source;
    }

    private bool EnsureOutput()
    {
        if (_output is not null)
            return true;
        try
        {
            _mixer = new MixingSampleProvider(
                WaveFormat.CreateIeeeFloatWaveFormat(_mixSampleRate, 2))
            {
                ReadFully = true,   // silencio cuando no hay cues → stream siempre vivo
            };
            _mixer.MixerInputEnded += OnMixerInputEnded;

            _output = CreateOutput();
            _output.Init(_mixer);
            _output.Play();
            _paused = false;
            return true;
        }
        catch (Exception exc)
        {
            _mixer = null;
            _output = null;
            EmitError(-1, $"No se pudo abrir el dispositivo de audio: {exc.Message}");
            return false;
        }
    }

    private IWavePlayer CreateOutput()
    {
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

    /// <summary>Retira el cue del mixer y del registro (parada manual).</summary>
    private void StopCtx(int cueId, bool emitState)
    {
        if (!_active.Remove(cueId, out var a))
            return;
        a.Provider.RequestStop();               // por si el mixer lee antes del Remove
        _mixer?.RemoveMixerInput(a.MixerInput);
        if (emitState)
            SetState(cueId, MediaCueState.Stopped);
    }

    private void CloseOutput()
    {
        var output = _output;
        _output = null;
        _mixer = null;
        if (output is not null)
        {
            try { output.Stop(); } catch (Exception) { }
            try { output.Dispose(); } catch (Exception) { }
        }
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
