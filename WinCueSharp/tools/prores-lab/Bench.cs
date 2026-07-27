using System.Diagnostics;

namespace ProResLab;

/// <summary>Resultado de una corrida de benchmark de decodificación.</summary>
/// <param name="Label">Etiqueta de la fila (clip + modo).</param>
/// <param name="Frames">Frames decodificados y entregados.</param>
/// <param name="Seconds">Segundos de pared de la medición.</param>
/// <param name="P50">Mediana del coste por iteración (ms).</param>
/// <param name="P95">Percentil 95 (ms).</param>
/// <param name="Max">Peor iteración (ms).</param>
/// <param name="CompressedBytes">Bytes comprimidos consumidos.</param>
/// <param name="DecodedBytes">Bytes de planos YUV producidos.</param>
/// <param name="PeakWorkingSetMb">Pico de working set del proceso (MB).</param>
public readonly record struct BenchResult(
    string Label,
    int Frames,
    double Seconds,
    double P50,
    double P95,
    double Max,
    long CompressedBytes,
    long DecodedBytes,
    double PeakWorkingSetMb)
{
    /// <summary>Frames por segundo sostenidos.</summary>
    public double Fps => Seconds > 0 ? Frames / Seconds : 0;

    /// <summary>MB/s de bitstream consumido (lo que tiene que dar el disco).</summary>
    public double CompressedMbPerSecond => Seconds > 0 ? CompressedBytes / Seconds / (1024.0 * 1024.0) : 0;

    /// <summary>MB/s de planos YUV producidos (lo que hay que subir a la GPU).</summary>
    public double DecodedMbPerSecond => Seconds > 0 ? DecodedBytes / Seconds / (1024.0 * 1024.0) : 0;
}

/// <summary>Frames comprimidos precargados en RAM nativa, listos para el decodificador.</summary>
public sealed class PreloadedClip : IDisposable
{
    private readonly NativeFrameStore _store;

    private PreloadedClip(MovInfo info, NativeFrameStore store, long totalBytes)
    {
        Info = info;
        _store = store;
        TotalBytes = totalBytes;
    }

    /// <summary>Metadatos del contenedor.</summary>
    public MovInfo Info { get; }

    /// <summary>Frames precargados.</summary>
    public int Count => _store.Count;

    /// <summary>Bytes comprimidos totales.</summary>
    public long TotalBytes { get; }

    /// <summary>Vista del frame <paramref name="index"/>.</summary>
    public PacketRef Packet(int index) => _store[index];

    /// <summary>
    /// Lee el clip entero a RAM. El benchmark mide DECODE, no disco: la E/S
    /// se saca de la ecuación a propósito y se reporta aparte en MB/s.
    /// </summary>
    public static PreloadedClip Load(string path, int maxFrames = int.MaxValue)
    {
        MovInfo info = MovDemuxer.Open(path);
        int n = Math.Min(info.FrameCount, maxFrames);
        long total = 0;
        for (int i = 0; i < n; i++)
        {
            total += info.Samples[i].Size;
        }

        NativeFrameStore store = new(total, n);
        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1 << 20, FileOptions.SequentialScan);
            for (int i = 0; i < n; i++)
            {
                store.Append(fs, info.Samples[i]);
            }
        }
        catch
        {
            store.Dispose();
            throw;
        }

        return new PreloadedClip(info, store, total);
    }

    public void Dispose() => _store.Dispose();
}

/// <summary>Bancos de prueba de decodificación.</summary>
public static class Bench
{
    /// <summary>
    /// Decodifica el clip entero <paramref name="repeat"/> veces con UN
    /// decodificador y mide el coste por iteración.
    /// </summary>
    /// <remarks>
    /// Con <see cref="ThreadMode.Slice"/> el tiempo por iteración ES la latencia
    /// del frame (el decodificador no devuelve hasta terminarlo). Con
    /// <see cref="ThreadMode.Frame"/> hay retardo de tubería: la iteración mide
    /// un PASO de la tubería, no el frame; ahí lo válido es el fps agregado.
    /// </remarks>
    public static BenchResult Run(PreloadedClip clip, ThreadMode mode, int threads, int repeat, string label)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(repeat);

        using ProResDecoder decoder = new(clip.Info.Width, clip.Info.Height, threads, mode, clip.Info.VideoFourcc);

        // Calentamiento: JIT, tablas del decodificador, primer alloc de frames.
        Warmup(clip, decoder);

        List<double> times = new(clip.Count * repeat);
        long decodedBytes = 0;
        long compressedBytes = 0;
        int delivered = 0;

        Stopwatch total = Stopwatch.StartNew();
        Stopwatch step = new();
        for (int r = 0; r < repeat; r++)
        {
            for (int i = 0; i < clip.Count; i++)
            {
                step.Restart();
                decoder.SendPacket(clip.Packet(i));
                bool got = decoder.TryReceiveFrame();
                step.Stop();
                times.Add(step.Elapsed.TotalMilliseconds);
                compressedBytes += clip.Packet(i).Size;

                if (got)
                {
                    delivered++;
                    decodedBytes += decoder.Layout.FrameBytes(decoder.FrameWidth, decoder.FrameHeight);
                    decoder.ReleaseFrame();
                }
            }
        }

        // Drenar la tubería del modo FRAME para no perder los últimos frames
        decoder.Flush();
        while (decoder.TryReceiveFrame())
        {
            delivered++;
            decodedBytes += decoder.Layout.FrameBytes(decoder.FrameWidth, decoder.FrameHeight);
            decoder.ReleaseFrame();
        }

        total.Stop();

        (double p50, double p95, double max) = Percentiles(times);
        return new BenchResult(
            label, delivered, total.Elapsed.TotalSeconds, p50, p95, max,
            compressedBytes, decodedBytes, PeakWorkingSetMb());
    }

    /// <summary>
    /// Simula <paramref name="streams"/> vídeos SIMULTÁNEOS: un decodificador
    /// independiente por hilo, cada uno con <paramref name="threadsPerStream"/>
    /// hilos. Es la pregunta del directo: ¿caben 2-3 a la vez?
    /// </summary>
    public static BenchResult RunConcurrent(
        PreloadedClip clip, int streams, ThreadMode mode, int threadsPerStream, int repeat, string label)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(streams);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(repeat);

        long decodedBytes = 0;
        long compressedBytes = 0;
        int delivered = 0;
        List<double>[] perStream = new List<double>[streams];

        // Calentamiento por stream ANTES de arrancar el cronómetro común
        ProResDecoder[] decoders = new ProResDecoder[streams];
        try
        {
            for (int s = 0; s < streams; s++)
            {
                decoders[s] = new ProResDecoder(clip.Info.Width, clip.Info.Height, threadsPerStream, mode, clip.Info.VideoFourcc);
                Warmup(clip, decoders[s]);
                perStream[s] = new List<double>(clip.Count * repeat);
            }

            Stopwatch total = Stopwatch.StartNew();
            Parallel.For(0, streams, s =>
            {
                ProResDecoder decoder = decoders[s];
                Stopwatch step = new();
                long localDecoded = 0;
                long localCompressed = 0;
                int localDelivered = 0;

                for (int r = 0; r < repeat; r++)
                {
                    for (int i = 0; i < clip.Count; i++)
                    {
                        step.Restart();
                        decoder.SendPacket(clip.Packet(i));
                        bool got = decoder.TryReceiveFrame();
                        step.Stop();
                        perStream[s].Add(step.Elapsed.TotalMilliseconds);
                        localCompressed += clip.Packet(i).Size;
                        if (got)
                        {
                            localDelivered++;
                            localDecoded += decoder.Layout.FrameBytes(decoder.FrameWidth, decoder.FrameHeight);
                            decoder.ReleaseFrame();
                        }
                    }
                }

                decoder.Flush();
                while (decoder.TryReceiveFrame())
                {
                    localDelivered++;
                    localDecoded += decoder.Layout.FrameBytes(decoder.FrameWidth, decoder.FrameHeight);
                    decoder.ReleaseFrame();
                }

                Interlocked.Add(ref decodedBytes, localDecoded);
                Interlocked.Add(ref compressedBytes, localCompressed);
                Interlocked.Add(ref delivered, localDelivered);
            });
            total.Stop();

            List<double> all = [];
            foreach (List<double> l in perStream)
            {
                all.AddRange(l);
            }

            (double p50, double p95, double max) = Percentiles(all);
            return new BenchResult(
                label, delivered, total.Elapsed.TotalSeconds, p50, p95, max,
                compressedBytes, decodedBytes, PeakWorkingSetMb());
        }
        finally
        {
            foreach (ProResDecoder? d in decoders)
            {
                d?.Dispose();
            }
        }
    }

    private static void Warmup(PreloadedClip clip, ProResDecoder decoder)
    {
        int warm = Math.Min(clip.Count, 12);
        for (int i = 0; i < warm; i++)
        {
            decoder.SendPacket(clip.Packet(i));
            if (decoder.TryReceiveFrame())
            {
                decoder.ReleaseFrame();
            }
        }

        decoder.Flush();
        while (decoder.TryReceiveFrame())
        {
            decoder.ReleaseFrame();
        }

        // Sin este Reset el contexto se queda en modo drenaje y el primer
        // paquete de la medición devolvería AVERROR_EOF.
        decoder.Reset();
    }

    /// <summary>p50/p95/max de una serie (no muta la original).</summary>
    public static (double P50, double P95, double Max) Percentiles(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return (0, 0, 0);
        }

        double[] sorted = [.. values];
        Array.Sort(sorted);
        return (Pick(sorted, 0.50), Pick(sorted, 0.95), sorted[^1]);
    }

    private static double Pick(double[] sorted, double p)
    {
        double rank = p * (sorted.Length - 1);
        int lo = (int)rank;
        int hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + ((sorted[hi] - sorted[lo]) * (rank - lo));
    }

    private static double PeakWorkingSetMb()
    {
        using Process p = Process.GetCurrentProcess();
        p.Refresh();
        return p.PeakWorkingSet64 / (1024.0 * 1024.0);
    }
}
