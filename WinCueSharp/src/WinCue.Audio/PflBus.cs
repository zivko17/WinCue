// =============================================================================
// PflBus.cs — El bus de PREESCUCHA (CUE / PFL): pinchar lo que YA está sonando
// =============================================================================
//
// QUÉ NO ES: no es volver a reproducir el archivo por los cascos. Eso es un
// preview, empieza por el principio y no te dice nada de lo que está pasando en
// la sala AHORA. Un CUE de mesa pincha la señal viva del canal y te la manda al
// oído — mismo instante, mismo procesado, misma pasada del bucle.
//
// CÓMO. El cue ya se lee para su salida; se le pone una DERIVACIÓN (PflTap) que,
// mientras está armada, copia esas mismas muestras a un anillo. En el
// dispositivo de cascos hay un lector del anillo. Nadie reproduce dos veces.
//
// LOS DOS RELOJES. La salida de sala y la de cascos son dos dispositivos con
// relojes independientes: uno escribe y otro lee a ritmos que NO son idénticos,
// y a la larga uno le gana al otro. Es inevitable y se asume:
//   · Si el anillo se llena, se tira lo MÁS VIEJO. En un monitor lo que importa
//     es oír el ahora; acumular retraso sería peor que un chasquido.
//   · Si se vacía, se emite SILENCIO. Nunca se repite audio para rellenar: un
//     eco corto se confunde con un fallo del archivo.
// Medio segundo de anillo da margen de sobra para el desfase entre buffers sin
// que la preescucha se oiga con retardo perceptible.
// =============================================================================

using NAudio.Wave;

namespace WinCue.Audio;

/// <summary>Anillo entre la salida que suena y el dispositivo de cascos.</summary>
public sealed class PflBus
{
    private readonly object _lock = new();
    private readonly float[] _ring;
    private int _write, _read, _count;

    public WaveFormat WaveFormat { get; }

    /// <param name="seconds">Profundidad del anillo. Es el colchón contra el
    /// desfase de los dos relojes, no una latencia deseada.</param>
    public PflBus(int sampleRate, int channels, double seconds = 0.5)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        _ring = new float[Math.Max(1024, (int)(sampleRate * channels * seconds))];
    }

    /// <summary>Hay algún cue pinchado ahora mismo.</summary>
    public volatile bool Active;

    /// <summary>Ganancia del bus, la del fader CUE. El fader del bus manda
    /// sobre TODO lo que entra en él: sin esto controlaba el preview (que va
    /// como un stream normal, con su volumen) pero no el pinchazo, y hacía una
    /// cosa u otra según qué estuvieras oyendo.</summary>
    public volatile float Gain = 1.0f;

    /// <summary>Vacía el anillo (al cambiar de cue pinchado o al soltar el CUE):
    /// sin esto se oiría la cola del anterior al pinchar el siguiente.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _write = _read = _count = 0;
        }
    }

    /// <summary>Copia muestras desde el hilo de audio de la salida que suena.</summary>
    public void Write(float[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
            {
                _ring[_write] = buffer[offset + i];
                _write = (_write + 1) % _ring.Length;
                if (_count < _ring.Length)
                {
                    _count++;
                }
                else
                {
                    // Anillo lleno: el lector va por detrás. Se tira lo más
                    // viejo — en un monitor manda el ahora.
                    _read = (_read + 1) % _ring.Length;
                }
            }
        }
    }

    /// <summary>Lector para el mixer del dispositivo de cascos.</summary>
    public ISampleProvider Reader => new RingReader(this);

    private int ReadInto(float[] buffer, int offset, int count)
    {
        float g = Gain;
        lock (_lock)
        {
            int n = Math.Min(count, _count);
            for (int i = 0; i < n; i++)
            {
                buffer[offset + i] = _ring[_read] * g;
                _read = (_read + 1) % _ring.Length;
            }
            _count -= n;
            // El resto, SILENCIO: el lector siempre entrega el bloque completo
            // (el mixer del bus vive con ReadFully) y rellenar repitiendo audio
            // sonaría a avería del archivo.
            for (int i = n; i < count; i++)
                buffer[offset + i] = 0f;
            return count;
        }
    }

    private sealed class RingReader(PflBus bus) : ISampleProvider
    {
        public WaveFormat WaveFormat => bus.WaveFormat;

        public int Read(float[] buffer, int offset, int count) =>
            bus.ReadInto(buffer, offset, count);
    }
}

/// <summary>Derivación sobre la entrada de un cue en su mixer: deja pasar el
/// audio intacto y, si está ARMADA, copia una réplica al bus de preescucha.
///
/// Va DESPUÉS de la adaptación al formato del mixer a propósito: lo que se oye
/// por los cascos tiene que ser exactamente lo que va a la sala, con su
/// resampleo y su velocidad, no una versión parecida.</summary>
public sealed class PflTap(ISampleProvider inner, PflBus bus) : ISampleProvider
{
    /// <summary>Este cue es el pinchado.</summary>
    public volatile bool Armed;

    public WaveFormat WaveFormat => inner.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        if (Armed && read > 0)
            bus.Write(buffer, offset, read);
        return read;
    }
}
