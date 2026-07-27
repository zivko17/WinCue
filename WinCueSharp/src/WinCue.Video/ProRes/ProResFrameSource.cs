// =============================================================================
// ProResFrameSource.cs — Hilo decodificador con ring de frames ProRes
// =============================================================================
// Gemelo de HapFrameSource, misma aritmética de índices LÓGICOS: el índice L
// corresponde al frame físico startFrame + (L % segmentFrames), así el mismo
// bucle sirve para trim, loop infinito y playCount > 1 (ProRes es todo-intra,
// igual que HAP: no hay seek real, solo aritmética de índices).
//
// TRES DIFERENCIAS QUE NO SE PUEDEN SALTAR:
//
//  1. Los frames NO se copian: cada slot posee su AVFrame y el decodificador le
//     traspasa la referencia (ProResFrameSlot). Copiarlos costaría un memcpy de
//     63 MB por fotograma en 4K 4444.
//
//  2. RING DE 3, no de 4. Con HAP un slot es un array DXT (8 MB en 4K); aquí es
//     un frame entero de FFmpeg (31,6 MB en 4K 4:2:2, 63,3 MB en 4444). Cuatro
//     slots de 4K 4444 serían 253 MB por cue; tres son 190. El colchón de tres
//     ya cubre el jitter medido (p95 ≈ 1,1 × p50 con slice threading).
//
//  3. RESET DEL DECODIFICADOR en cada salto no secuencial (costura de loop y
//     SkipTo). Es la trampa documentada del paso 1: el contexto arrastra estado
//     y tras un drenaje queda en EOF para siempre. avcodec_flush_buffers es
//     barato y quita toda duda.
//
// Buffers reutilizados: un único PaddedBuffer nativo dimensionado al sample más
// grande del fichero → cero allocaciones por frame en régimen.
// =============================================================================

using WinCue.Video.Containers;

namespace WinCue.Video.ProRes;

internal sealed class ProResFrameSource : IDisposable
{
    internal const int RingSize = 3;

    private readonly string _path;
    private readonly MovInfo _info;
    private readonly int _startFrame;
    private readonly int _segmentFrames;
    private readonly long _totalLogical;       // -1 = infinito (loop)
    private readonly int _threadCount;
    private readonly ProResFrameSlot[] _slots = new ProResFrameSlot[RingSize];
    private readonly long[] _slotFrame = new long[RingSize];
    private readonly object _gate = new();
    private readonly Thread _thread;
    private long _consumed = -1;               // último lógico confirmado
    private volatile bool _stop;

    /// <param name="startFrame">Primer frame físico del segmento (trim de entrada).</param>
    /// <param name="segmentFrames">Frames del segmento recortado (una pasada).</param>
    /// <param name="totalLogical">Frames lógicos totales, o -1 para loop infinito.</param>
    /// <param name="threadCount">Hilos del decodificador (ver ThreadBudget).</param>
    public ProResFrameSource(string path, MovInfo info, int startFrame, int segmentFrames,
        long totalLogical, int threadCount)
    {
        _path = path;
        _info = info;
        _startFrame = startFrame;
        _segmentFrames = Math.Max(1, segmentFrames);
        _totalLogical = totalLogical;
        _threadCount = Math.Clamp(threadCount, ThreadBudget.Min, ThreadBudget.Max);

        for (int i = 0; i < RingSize; i++)
        {
            _slots[i] = new ProResFrameSlot();
            _slotFrame[i] = -1;
        }

        _thread = new Thread(DecodeLoop) { IsBackground = true, Name = "prores-decode" };
        _thread.Start();
    }

    /// <summary>Excepción del hilo decodificador, si murió.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Frames lógicos decodificados hasta ahora.</summary>
    public long DecodedCount { get; private set; }

    /// <summary>Intenta obtener el frame lógico pedido sin bloquear.</summary>
    public bool TryGet(long logicalIndex, out ProResFrameSlot frame)
    {
        lock (_gate)
        {
            int slot = (int)(logicalIndex % RingSize);
            if (_slotFrame[slot] == logicalIndex && _slots[slot].HasFrame)
            {
                frame = _slots[slot];
                return true;
            }
        }

        frame = null!;
        return false;
    }

    /// <summary>El presentador confirmó hasta este lógico: libera slots.</summary>
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
    /// decodificador secuencial no alcanzaría sin liberar antes los frames
    /// saltados. Misma costura de loop que en HAP (ver HapFrameSource.SkipTo):
    /// sin esto, decodificador y presentador se esperan mutuamente y el guardián
    /// de fin abandona el bucle tras una sola pasada.</summary>
    public void SkipTo(long logicalIndex)
    {
        lock (_gate)
        {
            if (logicalIndex - _consumed > RingSize)
            {
                _consumed = logicalIndex - 1;
                Monitor.PulseAll(_gate);
            }
        }
    }

    private void DecodeLoop()
    {
        ProResDecoder? decoder = null;
        PaddedBuffer? buffer = null;
        try
        {
            decoder = new ProResDecoder(_info.Width, _info.Height, _threadCount,
                ThreadMode.Slice, _info.VideoFourcc);
            buffer = new PaddedBuffer(Math.Max(1, _info.MaxSampleSize));

            using FileStream fs = new(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            int previousPhysical = -1;

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

                // COSTURA DE LOOP (o salto adelante): el decodificador arrastra
                // estado entre paquetes. ProRes es intra y sobreviviría, pero el
                // contexto que ha visto un drenaje queda en EOF para siempre;
                // flush_buffers es barato y quita toda duda.
                if (previousPhysical >= 0 && physical != previousPhysical + 1)
                    decoder.Reset();
                previousPhysical = physical;

                SampleEntry sample = _info.Samples[physical];
                _ = buffer.ReadSample(fs, sample);
                decoder.SendPacket(buffer.AsPacket(sample.Size));

                if (!decoder.TryReceiveFrame())
                {
                    // Con FF_THREAD_SLICE un paquete entrega su frame de
                    // inmediato; si no lo hace, el fichero está dañado.
                    throw new InvalidDataException(
                        $"El decodificador no entregó frame para el sample {physical}.");
                }

                int slot = (int)(l % RingSize);
                // Traspaso SIN COPIA: el slot pasa a ser dueño del AVFrame
                decoder.MoveFrameTo(_slots[slot]);

                lock (_gate)
                {
                    _slotFrame[slot] = l;
                    DecodedCount = l + 1;
                }
            }
        }
        catch (Exception ex) when (
            ex is IOException or InvalidDataException or ObjectDisposedException
               or NotSupportedException or DllNotFoundException or InvalidOperationException
               or ArgumentOutOfRangeException)
        {
            Error = ex;
        }
        finally
        {
            buffer?.Dispose();
            decoder?.Dispose();
        }
    }

    public void Dispose()
    {
        _stop = true;
        lock (_gate)
        {
            Monitor.PulseAll(_gate);
        }

        // Los slots retienen frames de FFmpeg (63 MB cada uno en 4K 4444) y hay
        // que soltarlos, PERO solo cuando el hilo decodificador está muerto de
        // verdad: liberarlos con el hilo vivo sería un use-after-free dentro del
        // código nativo — justo el fallo que se lleva la aplicación entera. Aquí
        // el motor HAP podía ser laxo (sus slots son arrays gestionados); este
        // no puede. El bucle mira _stop en cada vuelta y espera en tramos de
        // 100 ms, así que 2 s es holgadísimo; si aun así no muere, se prefiere
        // dejar la memoria al proceso (el hilo es background y morirá al salir).
        if (_thread.Join(2000))
        {
            foreach (ProResFrameSlot slot in _slots)
                slot.Dispose();
        }
    }
}
