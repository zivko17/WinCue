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
// =============================================================================

using NAudio.Wave;

namespace WinCue.Audio;

public sealed class CueSampleProvider : ISampleProvider
{
    private readonly AudioBuffer _buf;
    private readonly int _startFrame;   // trim in (frames absolutos en el buffer)
    private readonly int _endFrame;     // trim out (exclusivo)
    private readonly int _playCount;    // nº total de reproducciones (1 = una vez)
    private readonly bool _loop;
    private readonly int _fadeOutFrames;
    private readonly long _tStartTicks;

    private int _positionFrames;        // posición relativa a _startFrame
    private int _passes;                // repeticiones ya completadas
    private bool _firstReadDone;
    private int _levelCounter;
    private bool _fadeOutArmed;
    private bool _completed;

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

    // ── Callbacks hacia el engine (se invocan EN EL HILO RT) ───────────
    public event Action<double>? LatencyMeasured;   // ms desde Play al primer Read
    /// <summary>RMS estéreo (L, R) 0.0–1.0, throttled ~20 Hz. Mono → L=R.</summary>
    public event Action<float, float>? LevelReport;

    public WaveFormat WaveFormat { get; }

    public int CueId { get; }

    public CueSampleProvider(
        int cueId,
        AudioBuffer buffer,
        double volume = 1.0,
        double fadeIn = 0.0,
        double fadeOut = 0.0,
        bool loop = false,
        double trimStart = 0.0,
        double trimEnd = 0.0,
        int playCount = 1)
    {
        CueId = cueId;
        _buf = buffer;
        _loop = loop;
        _playCount = Math.Max(1, playCount);
        _tStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(buffer.SampleRate, buffer.Channels);

        // Recorte (in/out) dentro del archivo, estilo QLab
        int n = buffer.FrameCount;
        int s = trimStart > 0.0 ? (int)(trimStart * buffer.SampleRate) : 0;
        int e = trimEnd > 0.0 ? (int)(trimEnd * buffer.SampleRate) : n;
        s = Math.Clamp(s, 0, n);
        e = Math.Clamp(e, s, n);
        if (e <= s) { s = 0; e = n; }   // trim inválido → archivo completo
        _startFrame = s;
        _endFrame = e;

        _volume = Math.Clamp(volume, 0.0, 1.0);
        double fadeOutSec = Math.Max(0.0, fadeOut);
        _fadeOutFrames = fadeOutSec > 0.0 ? (int)(fadeOutSec * buffer.SampleRate) : 0;

        // Fade-in automático: de 0 al volumen objetivo
        double fadeInSec = Math.Max(0.0, fadeIn);
        if (fadeInSec > 0.0)
        {
            _gain = 0.0;
            _fadeFrom = 0.0;
            _fadeTo = _volume;
            _fadeTotal = Math.Max(1, (int)(fadeInSec * buffer.SampleRate));
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

    /// <summary>Frames totales de la región reproducible (con trim aplicado).</summary>
    public int TotalFrames => _endFrame - _startFrame;

    /// <summary>Duración de la región reproducible en segundos.</summary>
    public double DurationSeconds => (double)TotalFrames / _buf.SampleRate;

    /// <summary>Segundos reproducidos de la pasada actual.</summary>
    public double ElapsedSeconds => (double)Volatile.Read(ref _positionFrames) / _buf.SampleRate;

    public bool IsCompleted => _completed;

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
        _fadeTotal = Math.Max(1, (int)(duration * _buf.SampleRate));
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

        int channels = _buf.Channels;
        int framesRequested = count / channels;
        int framesWritten = 0;
        int totalFrames = TotalFrames;
        float[] src = _buf.Samples;

        while (framesWritten < framesRequested)
        {
            if (_requestStop)
                break;

            int remaining = totalFrames - _positionFrames;
            if (remaining <= 0)
            {
                if (!Wrap()) break;
                remaining = totalFrames;
            }

            // ── Fade-out automático al final (solo en la ÚLTIMA pasada) ─
            if (_fadeOutFrames > 0 && !_fadeOutArmed && !_loop && !_fadeActive
                && _passes >= _playCount - 1
                && remaining <= _fadeOutFrames)
            {
                _fadeOutArmed = true;
                _fadeFrom = _gain;
                _fadeTo = 0.0;
                _fadeTotal = Math.Max(1, remaining);
                _fadePos = 0;
                _stopAfterFade = false;   // el fin natural del buffer para solo
                _fadeActive = true;
            }

            int chunk = Math.Min(framesRequested - framesWritten, remaining);
            int srcIndex = (_startFrame + _positionFrames) * channels;
            int dstIndex = offset + framesWritten * channels;

            // ── Copiar bloque aplicando envolvente de ganancia ──────────
            float master = _master;
            if (_fadeActive)
            {
                double total = _fadeTotal;
                double from = _fadeFrom;
                double delta = _fadeTo - _fadeFrom;
                int p0 = _fadePos;
                for (int f = 0; f < chunk; f++)
                {
                    double t = Math.Min((p0 + f + 1) / total, 1.0);
                    float g = (float)(from + delta * t) * master;
                    int si = srcIndex + f * channels;
                    int di = dstIndex + f * channels;
                    for (int c = 0; c < channels; c++)
                        buffer[di + c] = src[si + c] * g;
                }
                _fadePos += chunk;
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
                if (g == 1.0f)
                {
                    Array.Copy(src, srcIndex, buffer, dstIndex, chunk * channels);
                }
                else
                {
                    int samples = chunk * channels;
                    for (int i = 0; i < samples; i++)
                        buffer[dstIndex + i] = src[srcIndex + i] * g;
                }
            }

            _positionFrames += chunk;
            framesWritten += chunk;

            if (_positionFrames >= totalFrames && !_requestStop)
            {
                if (!Wrap()) break;
            }
        }

        int samplesWritten = framesWritten * channels;

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

    /// <summary>¿Repetir desde el principio? (loop infinito o play_count).</summary>
    private bool Wrap()
    {
        if (_requestStop)
            return false;
        if (_loop)
        {
            _positionFrames = 0;
            return true;
        }
        if (_passes < _playCount - 1)
        {
            _passes++;
            _positionFrames = 0;
            return true;
        }
        return false;
    }
}
