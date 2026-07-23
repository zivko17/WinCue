using System.IO;

namespace HapLab.View;

/// <summary>
/// Hilo decodificador con ring de frames DXT por delante de la presentación.
/// Lee los samples del .mov (E/S real de disco, sin precarga) y los decodifica
/// en orden con MovDemuxer/HapFrame en buffers reutilizados (cero allocaciones
/// por frame en régimen). El presentador pide el frame que toca por índice
/// (TryGet) y libera los anteriores al confirmar (Consume); el decodificador
/// se bloquea cuando lleva RingSize frames de ventaja.
/// </summary>
internal sealed class FrameSource : IDisposable
{
    private const int RingSize = 4;

    private readonly string _path;
    private readonly MovInfo _info;
    private readonly bool _parallelChunks;
    private readonly byte[][] _slots = new byte[RingSize][];
    private readonly int[] _slotFrame = new int[RingSize];
    private readonly object _gate = new();
    private readonly Thread _thread;
    private readonly double[] _decodeMs;
    private int _consumed = -1;   // último frame confirmado por el presentador
    private volatile bool _stop;

    public FrameSource(string path, MovInfo info, HapTextureFormat format, bool parallelChunks)
    {
        _path = path;
        _info = info;
        _parallelChunks = parallelChunks;
        _decodeMs = new double[info.FrameCount];
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

    /// <summary>ms de lectura+decode por frame decodificado (índice = frame).</summary>
    public IReadOnlyList<double> DecodeMs => _decodeMs;

    /// <summary>Frames decodificados hasta ahora.</summary>
    public int DecodedCount { get; private set; }

    /// <summary>Intenta obtener el DXT del frame pedido sin bloquear.</summary>
    public bool TryGet(int frameIndex, out byte[] dxt)
    {
        lock (_gate)
        {
            int slot = frameIndex % RingSize;
            if (_slotFrame[slot] == frameIndex)
            {
                dxt = _slots[slot];
                return true;
            }
        }

        dxt = [];
        return false;
    }

    /// <summary>El presentador confirmó el frame: libera ese slot y anteriores.</summary>
    public void Consume(int frameIndex)
    {
        lock (_gate)
        {
            if (frameIndex > _consumed)
            {
                _consumed = frameIndex;
                Monitor.PulseAll(_gate);
            }
        }
    }

    private void DecodeLoop()
    {
        try
        {
            using FileStream fs = new(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            byte[] compressed = [];
            long ticksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000;

            for (int i = 0; i < _info.FrameCount && !_stop; i++)
            {
                // Esperar hueco: el slot i%Ring está libre cuando su ocupante
                // anterior (i - RingSize) ya fue consumido
                lock (_gate)
                {
                    while (i - RingSize > _consumed && !_stop)
                    {
                        _ = Monitor.Wait(_gate, 100);
                    }
                }

                if (_stop)
                {
                    return;
                }

                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                SampleEntry sample = _info.Samples[i];
                if (compressed.Length < sample.Size)
                {
                    compressed = new byte[Math.Max(sample.Size, compressed.Length * 2)];
                }

                fs.Position = sample.Offset;
                fs.ReadExactly(compressed.AsSpan(0, sample.Size));

                int slot = i % RingSize;
                _ = HapFrame.Decode(compressed.AsMemory(0, sample.Size), _slots[slot], _parallelChunks);
                _decodeMs[i] = (double)(System.Diagnostics.Stopwatch.GetTimestamp() - t0) / ticksPerMs;

                lock (_gate)
                {
                    _slotFrame[slot] = i;
                    DecodedCount = i + 1;
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

        if (!_thread.Join(2000))
        {
            // Hilo background: si no muere a tiempo, el proceso lo arrastra al salir
        }
    }
}
