// =============================================================================
// CueSampleProvider.cs — Fuente de audio de UN cue (matemática del hilo RT)
// =============================================================================
// Port del callback de sounddevice + _PlaybackCtx de audio_engine.py.
//
// REGLAS DEL HILO RT (Read se llama desde el hilo de audio de NAudio):
//   · Sin I/O de disco, sin allocations en el hot path (los arrays de trabajo
//     se preasignan), solo lectura del buffer en RAM y aritmética por bloque.
//   · Las mutaciones externas (BeginFade/SetVolume/RequestStop) escriben campos
//     escalares desde el main thread; el flag _fadeActive se publica SIEMPRE en
//     último lugar con Volatile.Write para que el hilo RT nunca vea una
//     envolvente a medio configurar (equivale a la garantía del GIL en Python).
//
// MEJORAS DELIBERADAS SOBRE LA VERSIÓN PYTHON (documentadas):
//   · Loop sample-accurate: el wrap rellena el mismo bloque sin hueco de
//     silencio (Python zero-padeaba el resto del bloque al hacer wrap).
//   · Fade-out automático solo se arma en la ÚLTIMA pasada de play_count
//     (en Python se armaba en la primera y dejaba mudas las siguientes).
//
// PASO 4 (streaming): los samples ya no salen de un AudioBuffer directo sino
// de una ICueAudioSource (RAM o streaming ffmpeg). La matemática RT (fades,
// loop, EQ, VU, elapsed) es LA MISMA para ambas; la diferencia nueva es el
// UNDERRUN del streaming (ring vacío con proceso vivo): silencio SIN avanzar
// la posición — el reloj del vídeo se congela y se recupera al volver el dato.
// =============================================================================

using NAudio.Wave;

namespace WinCue.Audio;

public sealed class CueSampleProvider : ISampleProvider, IDisposable
{
    private readonly ICueAudioSource _source;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _playCount;    // nº total de reproducciones (1 = una vez)
    private readonly bool _loop;
    private readonly int _fadeOutFrames;
    private readonly long _tStartTicks;

    private int _positionFrames;        // posición dentro de la pasada actual
    private int _passes;                // repeticiones ya completadas
    private bool _firstReadDone;
    private int _levelCounter;
    private bool _fadeOutArmed;
    private bool _completed;
    private int _underruns;             // bloques con silencio por ring vacío

    // ── Envolvente de ganancia (escrita por main thread, leída por RT) ──
    private double _volume;             // ganancia objetivo en régimen permanente
    private double _gain;               // ganancia actual aplicada
    private double _fadeFrom;
    private double _fadeTo;
    private int _fadeTotal = 1;         // frames totales del fade
    private int _fadePos;               // frames de fade ya aplicados
    private bool _stopAfterFade;
    private volatile bool _fadeActive;  // publicar SIEMPRE en último lugar
    private volatile bool _requestStop; // terminar tras el bloque en curso
    private volatile bool _paused;      // emitir silencio sin avanzar posición

    /// <summary>Multiplicador master global (lo fija el AudioEngine).</summary>
    private volatile float _master = 1.0f;

    // ── EQ paramétrica por clip + analizador (RTA) ─────────────────────
    private readonly EqChain _eq;
    private const int FftSize = 2048;
    private const int SpectrumBands = 60;
    private float[]? _fftMono;                    // acumulador mono (lazy)
    private int _fftFill;
    private NAudio.Dsp.Complex[]? _fftWork;
    private float[]? _hann;
    private readonly float[] _spectrumDb = new float[SpectrumBands];
    private readonly object _specLock = new();
    private volatile bool _spectrumReady;
    private volatile bool _spectrumWanted;        // solo si la UI lo pide

    // ── Callbacks hacia el engine (se invocan EN EL HILO RT) ───────────
    public event Action<double>? LatencyMeasured;   // ms desde Play al primer Read
    /// <summary>RMS estéreo (L, R) 0.0–1.0, throttled ~20 Hz. Mono → L=R.</summary>
    public event Action<float, float>? LevelReport;

    public WaveFormat WaveFormat { get; }

    public int CueId { get; }

    /// <summary>Constructor clásico sobre el buffer entero en RAM (cues de
    /// AUDIO): el trim se resuelve dentro de la RamAudioSource.</summary>
    public CueSampleProvider(
        int cueId,
        AudioBuffer buffer,
        double volume = 1.0,
        double fadeIn = 0.0,
        double fadeOut = 0.0,
        bool loop = false,
        double trimStart = 0.0,
        double trimEnd = 0.0,
        int playCount = 1,
        WinCue.Core.AudioEqParams? eq = null)
        : this(cueId, new RamAudioSource(buffer, trimStart, trimEnd),
               volume, fadeIn, fadeOut, loop, playCount, eq)
    {
    }

    /// <summary>Constructor sobre una fuente arbitraria (RAM o streaming);
    /// la fuente YA lleva el trim aplicado (solo expone la región).</summary>
    public CueSampleProvider(
        int cueId,
        ICueAudioSource source,
        double volume = 1.0,
        double fadeIn = 0.0,
        double fadeOut = 0.0,
        bool loop = false,
        int playCount = 1,
        WinCue.Core.AudioEqParams? eq = null)
    {
        CueId = cueId;
        _source = source;
        _sampleRate = source.SampleRate;
        _channels = source.Channels;
        _eq = new EqChain(_sampleRate, _channels);
        _eq.Configure(eq);
        _loop = loop;
        _playCount = Math.Max(1, playCount);
        _tStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, _channels);

        _volume = Math.Clamp(volume, 0.0, 1.0);
        double fadeOutSec = Math.Max(0.0, fadeOut);
        _fadeOutFrames = fadeOutSec > 0.0 ? (int)(fadeOutSec * _sampleRate) : 0;

        // Fade-in automático: de 0 al volumen objetivo
        double fadeInSec = Math.Max(0.0, fadeIn);
        if (fadeInSec > 0.0)
        {
            _gain = 0.0;
            _fadeFrom = 0.0;
            _fadeTo = _volume;
            _fadeTotal = Math.Max(1, (int)(fadeInSec * _sampleRate));
            _fadePos = 0;
            _stopAfterFade = false;
            _fadeActive = true;
        }
        else
        {
            _gain = _volume;
        }
    }

    // ------------------------------------------------------------------
    // Propiedades de consulta (main thread)
    // ------------------------------------------------------------------

    /// <summary>Frames totales de la región reproducible (con trim aplicado).
    /// Streaming: estimación por ffprobe; 0 = desconocido.</summary>
    public int TotalFrames => (int)Math.Clamp(_source.TotalFrames, 0L, int.MaxValue);

    /// <summary>Duración de la región reproducible en segundos (0 = desconocida).</summary>
    public double DurationSeconds => (double)TotalFrames / _sampleRate;

    /// <summary>Segundos reproducidos de la pasada actual.</summary>
    public double ElapsedSeconds => (double)Volatile.Read(ref _positionFrames) / _sampleRate;

    public bool IsCompleted => _completed;

    /// <summary>Bloques que salieron con silencio por UNDERRUN del streaming
    /// (ring vacío con el subproceso vivo). Con RAM siempre 0. Diagnóstico.</summary>
    public int UnderrunCount => Volatile.Read(ref _underruns);

    // ------------------------------------------------------------------
    // Mutación externa (main thread) — mismo contrato que en Python
    // ------------------------------------------------------------------

    /// <summary>Funde hacia target en duration s. stopAfter → terminar al llegar.</summary>
    public void BeginFade(double target, double duration, bool stopAfter)
    {
        target = Math.Clamp(target, 0.0, 1.0);
        if (duration <= 0.0)
        {
            // Cambio instantáneo
            _volume = target;
            _gain = target;
            _fadeActive = false;
            if (stopAfter)
                _requestStop = true;
            return;
        }
        _fadeFrom = _gain;
        _fadeTo = target;
        _fadeTotal = Math.Max(1, (int)(duration * _sampleRate));
        _fadePos = 0;
        _stopAfterFade = stopAfter;
        if (!stopAfter)
            _volume = target;
        _fadeActive = true;   // publicar el flag SIEMPRE en último lugar
    }

    /// <summary>Fija instantáneamente el volumen (si no hay fade en curso).</summary>
    public void SetVolume(double volume)
    {
        double v = Math.Clamp(volume, 0.0, 1.0);
        _volume = v;
        if (!_fadeActive)
            _gain = v;
    }

    /// <summary>El hilo RT terminará tras el bloque en curso.</summary>
    public void RequestStop() => _requestStop = true;

    /// <summary>Pausa individual: emite silencio sin avanzar la posición
    /// (devolver 0 haría que el mixer retirase el input).</summary>
    public void SetPaused(bool paused) => _paused = paused;

    public bool IsPaused => _paused;

    /// <summary>Volumen master global (multiplicador aplicado en el hilo RT).</summary>
    public void SetMaster(float master) => _master = Math.Clamp(master, 0.0f, 1.0f);

    /// <summary>Reconfigura la EQ del clip en vivo (main thread).</summary>
    public void SetEq(WinCue.Core.AudioEqParams? eq) => _eq.Configure(eq);

    /// <summary>Reducción de ganancia actual del compresor (dB ≥ 0) para el
    /// medidor GR del inspector.</summary>
    public float GetGainReductionDb() => _eq.CurrentGainReductionDb;

    /// <summary>Espectro post-EQ para el RTA: bandas log 20 Hz–20 kHz en dBFS
    /// (−90..0), o null si aún no hay datos. La primera llamada activa el
    /// análisis (los cues sin RTA visible no pagan la FFT).</summary>
    public float[]? GetSpectrum()
    {
        _spectrumWanted = true;
        if (!_spectrumReady)
            return null;
        var copy = new float[SpectrumBands];
        lock (_specLock)
            Array.Copy(_spectrumDb, copy, SpectrumBands);
        return copy;
    }

    // ------------------------------------------------------------------
    // Hilo RT — equivalente al callback de sounddevice
    // ------------------------------------------------------------------

    public int Read(float[] buffer, int offset, int count)
    {
        // ── Latencia (una sola vez, primer bloque) ─────────────────────
        if (!_firstReadDone)
        {
            _firstReadDone = true;
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(_tStartTicks).TotalMilliseconds;
            LatencyMeasured?.Invoke(ms);
        }

        if (_completed)
            return 0;

        if (_paused)
        {
            Array.Clear(buffer, offset, count);   // silencio, posición congelada
            return count;
        }

        int channels = _channels;
        int framesRequested = count / channels;
        int framesWritten = 0;
        long totalFrames = _source.TotalFrames;   // streaming: estimado; <0 = n/d

        while (framesWritten < framesRequested)
        {
            if (_requestStop)
                break;

            // ── Fin real de la pasada (lo dicta la FUENTE, no la estimación) ─
            if (_source.PassEnded)
            {
                if (!Wrap()) break;
                continue;
            }

            long remaining = totalFrames >= 0
                ? Math.Max(0, totalFrames - _positionFrames)
                : long.MaxValue;

            // ── Precalentar la SIGUIENTE pasada (streaming: pre-spawn del
            //    subproceso con el ring llenándose ANTES de agotar la actual;
            //    RAM: no-op). 4 s de antelación > ring de 3 s. ─────────────
            if ((_loop || _passes < _playCount - 1)
                && remaining <= 4L * _sampleRate)
            {
                _source.PrepareNextPass();
            }

            // ── Fade-out automático al final (solo en la ÚLTIMA pasada) ─
            if (_fadeOutFrames > 0 && !_fadeOutArmed && !_loop && !_fadeActive
                && _passes >= _playCount - 1
                && remaining <= _fadeOutFrames)
            {
                _fadeOutArmed = true;
                _fadeFrom = _gain;
                _fadeTo = 0.0;
                _fadeTotal = Math.Max(1, (int)Math.Min(remaining, int.MaxValue));
                _fadePos = 0;
                _stopAfterFade = false;   // el fin natural de la fuente para solo
                _fadeActive = true;
            }

            int chunk = framesRequested - framesWritten;
            int dstIndex = offset + framesWritten * channels;
            int got = _source.ReadFrames(buffer, dstIndex, chunk);

            if (got == 0)
            {
                if (_source.PassEnded)
                {
                    if (!Wrap()) break;
                    continue;
                }
                // ── UNDERRUN (streaming): silencio el resto del bloque SIN
                //    avanzar la posición — el reloj se congela y el audio
                //    retoma donde iba cuando el ring vuelva a tener datos. ──
                _underruns++;
                Array.Clear(buffer, dstIndex, chunk * channels);
                framesWritten = framesRequested;
                break;
            }

            // ── Aplicar la envolvente de ganancia IN-PLACE sobre lo leído ──
            float master = _master;
            if (_fadeActive)
            {
                double total = _fadeTotal;
                double from = _fadeFrom;
                double delta = _fadeTo - _fadeFrom;
                int p0 = _fadePos;
                for (int f = 0; f < got; f++)
                {
                    double t = Math.Min((p0 + f + 1) / total, 1.0);
                    float g = (float)(from + delta * t) * master;
                    int di = dstIndex + f * channels;
                    for (int c = 0; c < channels; c++)
                        buffer[di + c] *= g;
                }
                _fadePos += got;
                if (_fadePos >= _fadeTotal)
                {
                    _gain = _fadeTo;
                    _fadeActive = false;
                    if (_stopAfterFade)
                        _requestStop = true;
                }
            }
            else
            {
                float g = (float)_gain * master;
                if (g != 1.0f)
                {
                    int samples = got * channels;
                    for (int i = 0; i < samples; i++)
                        buffer[dstIndex + i] *= g;
                }
            }

            _positionFrames += got;
            framesWritten += got;

            if (_source.PassEnded && !_requestStop)
            {
                if (!Wrap()) break;
            }
        }

        int samplesWritten = framesWritten * channels;

        // ── EQ paramétrica por clip (in-place, post-envolvente) ──────────
        if (samplesWritten > 0)
        {
            _eq.Process(buffer, offset, samplesWritten);
            if (_spectrumWanted)
                FeedSpectrum(buffer, offset, samplesWritten, channels);
        }

        // ── RMS estéreo para VU (throttle: 1 de cada 4 lecturas ≈ 20 Hz) ─
        if (samplesWritten > 0)
        {
            _levelCounter++;
            if (_levelCounter >= 4)
            {
                _levelCounter = 0;
                int ch = channels;
                int framesW = samplesWritten / ch;
                double sumL = 0.0, sumR = 0.0;
                for (int f = 0; f < framesW; f++)
                {
                    float l = buffer[offset + f * ch];
                    float r = ch > 1 ? buffer[offset + f * ch + 1] : l;
                    sumL += (double)l * l;
                    sumR += (double)r * r;
                }
                LevelReport?.Invoke(
                    (float)Math.Sqrt(sumL / framesW),
                    (float)Math.Sqrt(sumR / framesW));
            }
        }

        if (samplesWritten == 0)
            _completed = true;   // el mixer nos retira y el engine emite STOPPED

        return samplesWritten;
    }

    /// <summary>Acumula audio mono post-EQ y calcula la FFT del RTA cuando el
    /// bloque de 2048 muestras se llena (~23 Hz a 48 kHz). Hilo RT: los
    /// buffers se asignan UNA vez (lazy, la primera vez que la UI pide RTA).</summary>
    private void FeedSpectrum(float[] buffer, int offset, int samples, int channels)
    {
        if (_fftMono is null)
        {
            _fftMono = new float[FftSize];
            _fftWork = new NAudio.Dsp.Complex[FftSize];
            _hann = new float[FftSize];
            for (int i = 0; i < FftSize; i++)
                _hann[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftSize - 1))));
        }

        for (int i = 0; i + channels <= samples; i += channels)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
                sum += buffer[offset + i + c];
            _fftMono[_fftFill++] = sum / channels;
            if (_fftFill < FftSize)
                continue;
            _fftFill = 0;

            var work = _fftWork!;
            var hann = _hann!;
            for (int k = 0; k < FftSize; k++)
            {
                work[k].X = _fftMono[k] * hann[k];
                work[k].Y = 0;
            }
            NAudio.Dsp.FastFourierTransform.FFT(true, 11, work);   // 2^11 = 2048

            // Bandas log 20 Hz → 20 kHz (3 décadas) en dBFS
            int fs = _sampleRate;
            lock (_specLock)
            {
                for (int b = 0; b < SpectrumBands; b++)
                {
                    double f0 = 20.0 * Math.Pow(10.0, 3.0 * b / SpectrumBands);
                    double f1 = 20.0 * Math.Pow(10.0, 3.0 * (b + 1) / SpectrumBands);
                    int k0 = Math.Clamp((int)(f0 * FftSize / fs), 1, FftSize / 2 - 1);
                    int k1 = Math.Clamp((int)(f1 * FftSize / fs), k0 + 1, FftSize / 2);
                    double peak = 0;
                    for (int k = k0; k < k1; k++)
                    {
                        double m = work[k].X * (double)work[k].X
                                 + work[k].Y * (double)work[k].Y;
                        if (m > peak) peak = m;
                    }
                    // 2/N por la FFT + 2 por la ventana Hann ≈ amplitud real
                    double amp = 4.0 * Math.Sqrt(peak) / FftSize;
                    _spectrumDb[b] = (float)Math.Clamp(
                        20.0 * Math.Log10(Math.Max(1e-9, amp)), -90.0, 0.0);
                }
            }
            _spectrumReady = true;
        }
    }

    /// <summary>¿Repetir desde el principio? (loop infinito o play_count).
    /// La fuente conmuta de pasada (streaming: al subproceso pre-lanzado);
    /// si no puede (fallo fatal del spawn), el cue termina limpio.</summary>
    private bool Wrap()
    {
        if (_requestStop)
            return false;
        if (_loop)
        {
            if (!_source.BeginNextPass())
                return false;
            _positionFrames = 0;
            return true;
        }
        if (_passes < _playCount - 1)
        {
            if (!_source.BeginNextPass())
                return false;
            _passes++;
            _positionFrames = 0;
            return true;
        }
        return false;
    }

    /// <summary>Libera la fuente (streaming: mata el subproceso ffmpeg y sus
    /// pre-spawns). El engine lo llama al retirar el cue del mixer; RAM: no-op.</summary>
    public void Dispose() => _source.Dispose();
}
