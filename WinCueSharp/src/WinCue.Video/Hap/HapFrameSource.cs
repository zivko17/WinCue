// =============================================================================
// HapFrameSource.cs — Hilo decodificador con ring de frames DXT (adaptado de
// tools/hap-lab/HapLab.View/FrameSource.cs al SEGMENTO recortado y al loop)
// =============================================================================
// Lee los samples del .mov (E/S real de disco, sin precarga) y los decodifica
// en orden LÓGICO: el índice lógico L corresponde al frame físico
// startFrame + (L % segmentFrames) — así el mismo bucle sirve para trim,
// loop infinito y playCount>1 (todo es intra en HAP: no hay seek real, solo
// aritmética de índices). Buffers reutilizados: cero allocaciones por frame
// en régimen. El presentador pide por índice lógico (TryGet) y confirma con
// Consume; el decodificador se bloquea con RingSize frames de ventaja.
// =============================================================================

using WinCue.Video.Containers;

namespace WinCue.Video.Hap;

internal sealed class HapFrameSource : IDisposable
{
    private const int RingSize = 4;

    private readonly string _path;
    private readonly MovInfo _info;
    private readonly int _startFrame;
    private readonly int _segmentFrames;
    private readonly long _totalLogical;       // -1 = infinito (loop)
    private readonly byte[][] _slots = new byte[RingSize][];
    private readonly long[] _slotFrame = new long[RingSize];
    private readonly object _gate = new();
    private readonly Thread _thread;
    private long _consumed = -1;               // último lógico confirmado
    private volatile bool _stop;

    /// <param name="startFrame">Primer frame físico del segmento (trim de entrada).</param>
    /// <param name="segmentFrames">Frames del segmento recortado (una pasada).</param>
    /// <param name="totalLogical">Frames lógicos totales a decodificar
    /// (segmento × pasadas), o -1 para loop infinito.</param>
    public HapFrameSource(string path, MovInfo info, HapTextureFormat format,
        int startFrame, int segmentFrames, long totalLogical)
    {
        _path = path;
        _info = info;
        _startFrame = startFrame;
        _segmentFrames = Math.Max(1, segmentFrames);
        _totalLogical = totalLogical;
        int dxtSize = HapFrame.DxtSize(info.Width, info.Height, format);
        for (int i = 0; i < RingSize; i++)
        {
            _slots[i] = new byte[dxtSize];
            _slotFrame[i] = -1;
        }

        _thread = new Thread(DecodeLoop) { IsBackground = true, Name = "hap-decode" };
        _thread.Start();
    }

    /// <summary>Excepción del hilo decodificador, si murió.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Frames lógicos decodificados hasta ahora.</summary>
    public long DecodedCount { get; private set; }

    /// <summary>Intenta obtener el DXT del frame lógico pedido sin bloquear.</summary>
    public bool TryGet(long logicalIndex, out byte[] dxt)
    {
        lock (_gate)
        {
            int slot = (int)(logicalIndex % RingSize);
            if (_slotFrame[slot] == logicalIndex)
            {
                dxt = _slots[slot];
                return true;
            }
        }

        dxt = Array.Empty<byte>();
        return false;
    }

    /// <summary>El presentador confirmó hasta este lógico: libera slots. Con
    /// drops (saltos de índice) libera también los intermedios no pedidos.</summary>
    public void Consume(long logicalIndex)
    {
        lock (_gate)
        {
            if (logicalIndex > _consumed)
            {
                _consumed = logicalIndex;
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>El presentador SALTÓ hacia delante a un lógico que el
    /// decodificador secuencial no alcanzaría sin liberar antes los frames que
    /// se saltó (queda FUERA del ring por delante del último consumido).
    /// Ocurre en la COSTURA DE LOOP por envolvido por tiempo (vídeo sin audio
    /// maestro: el reloj cae en local≈WrapMargin → el pacer pide el frame
    /// WrapMargin·fps, que a 30/60 fps supera el ring de 4) y en un resync del
    /// reloj hacia delante. Sin esto, decodificador y presentador se esperaban
    /// mutuamente (el decoder bloqueado esperando que se consuman los frames
    /// saltados; el presentador esperando el frame de destino) y el guardián de
    /// >1 s ABANDONABA el loop tras una sola pasada. Adelantar el consumo
    /// desbloquea al decoder para que produzca hasta el frame pedido.</summary>
    public void SkipTo(long logicalIndex)
    {
        lock (_gate)
        {
            // Solo si el destino queda fuera del ring por delante: en catch-up
            // normal (dentro del ring) el decoder llega solo y no se toca.
            if (logicalIndex - _consumed > RingSize)
            {
                _consumed = logicalIndex - 1;
                Monitor.PulseAll(_gate);
            }
        }
    }

    private void DecodeLoop()
    {
        try
        {
            using FileStream fs = new(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            byte[] compressed = Array.Empty<byte>();

            for (long l = 0; (_totalLogical < 0 || l < _totalLogical) && !_stop; l++)
            {
                // Esperar hueco: el slot l%Ring queda libre cuando su ocupante
                // anterior (l - RingSize) ya fue consumido
                lock (_gate)
                {
                    while (l - RingSize > _consumed && !_stop)
                        _ = Monitor.Wait(_gate, 100);
                }

                if (_stop)
                    return;

                int physical = _startFrame + (int)(l % _segmentFrames);
                SampleEntry sample = _info.Samples[physical];
                if (compressed.Length < sample.Size)
                    compressed = new byte[Math.Max(sample.Size, compressed.Length * 2)];

                fs.Position = sample.Offset;
                fs.ReadExactly(compressed.AsSpan(0, sample.Size));

                int slot = (int)(l % RingSize);
                _ = HapFrame.Decode(compressed.AsMemory(0, sample.Size), _slots[slot], parallel: true);

                lock (_gate)
                {
                    _slotFrame[slot] = l;
                    DecodedCount = l + 1;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
        {
            Error = ex;
        }
    }

    public void Dispose()
    {
        _stop = true;
        lock (_gate)
        {
            Monitor.PulseAll(_gate);
        }

        _ = _thread.Join(2000);
        // Hilo background: si no muere a tiempo, el proceso lo arrastra al salir
    }
}
