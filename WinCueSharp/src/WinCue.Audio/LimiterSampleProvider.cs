// =============================================================================
// LimiterSampleProvider.cs — Limitador brickwall del bus de salida
// =============================================================================
//
// PROPÓSITO
//   Última etapa antes del dispositivo: la SUMA de N cues simultáneos no puede
//   saturar, dispare lo que dispare el operador. Feed-forward, stereo-linked
//   (una sola ganancia para todos los canales del frame: sin bailes de imagen
//   estéreo), ataque instantáneo (brickwall de verdad, sin overshoot) y
//   release exponencial suave para que no bombee.
//
// LO QUE NO CUBRE (honesto)
//   El audio de vídeo (mpv → dispositivo directo) no pasa por este bus; ahí la
//   referencia es el VU de programa por loopback con su clip-hold.
//
// HILOS
//   Read() corre en el hilo de audio. _gainReductionDb es el único estado
//   compartido con la UI (medidor GR): volatile float, escritura única por Read.
// =============================================================================

using NAudio.Wave;

namespace WinCue.Audio;

public sealed class LimiterSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly float _threshold;     // lineal (p. ej. −1 dBFS = 0.891)
    private readonly float _releaseCoef;   // suavizado por frame hacia gain=1
    private float _envelope = 1.0f;        // ganancia actual del limitador
    private volatile float _gainReductionDb;

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Reducción de ganancia actual en dB (≥ 0) para el medidor GR.</summary>
    public float GainReductionDb => _gainReductionDb;

    public LimiterSampleProvider(ISampleProvider source,
                                 double thresholdDb = -1.0,
                                 double releaseMs = 150.0)
    {
        _source = source;
        _channels = Math.Max(1, source.WaveFormat.Channels);
        _threshold = (float)Math.Pow(10.0, thresholdDb / 20.0);
        _releaseCoef = (float)Math.Exp(
            -1.0 / (source.WaveFormat.SampleRate * (releaseMs / 1000.0)));
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        int frames = read / _channels;
        float env = _envelope;

        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * _channels;

            // Pico del frame entre todos los canales (stereo-link)
            float peak = 0f;
            for (int c = 0; c < _channels; c++)
            {
                float a = Math.Abs(buffer[idx + c]);
                if (a > peak) peak = a;
            }

            // Ganancia objetivo: la justa para que el pico quede en el techo
            float target = peak > _threshold ? _threshold / peak : 1.0f;

            if (target < env)
                env = target;   // ataque instantáneo: ningún pico escapa
            else
                env = target + (env - target) * _releaseCoef;   // release suave

            if (env < 0.9999f)
                for (int c = 0; c < _channels; c++)
                    buffer[idx + c] *= env;
        }

        _envelope = env;
        _gainReductionDb = env < 0.9999f
            ? (float)(-20.0 * Math.Log10(env))
            : 0f;
        return read;
    }
}
