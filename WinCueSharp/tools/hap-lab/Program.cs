using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace HapLab;

/// <summary>
/// CLI del laboratorio HAP: <c>hap-lab &lt;archivo.mov&gt; [--bench]</c>.
/// Sin argumentos extra imprime la información del fichero; con <c>--bench</c>
/// decodifica todos los frames midiendo latencia por frame y throughput
/// single-thread y multi-thread.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args is null || args.Length == 0)
        {
            Console.WriteLine("Uso: hap-lab <archivo.mov> [--bench]");
            return 2;
        }

        string path = args[0];
        bool bench = args.Contains("--bench", StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"No existe el fichero: {path}");
            return 1;
        }

        try
        {
            MovInfo info = MovDemuxer.Open(path);
            PrintInfo(path, info);

            if (bench)
            {
                RunBenchmark(path, info);
            }

            return 0;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"Error de formato: {ex.Message}");
            return 1;
        }
    }

    private static void PrintInfo(string path, MovInfo info)
    {
        long fileSize = new FileInfo(path).Length;
        long payload = info.Samples.Sum(s => (long)s.Size);

        // Formato/compresor del primer frame (leyendo solo su cabecera)
        HapFrameInfo first = InspectFirstFrame(path, info);

        Console.WriteLine($"Fichero        : {Path.GetFileName(path)} ({fileSize / 1024.0 / 1024.0:F1} MB)");
        Console.WriteLine($"Códec          : {info.VideoFourcc} ({DescribeFourcc(info.VideoFourcc)})");
        Console.WriteLine($"Dimensiones    : {info.Width}x{info.Height}");
        Console.WriteLine($"FPS            : {info.Fps:F3}");
        Console.WriteLine($"Frames         : {info.FrameCount} ({info.DurationSeconds:F2} s)");
        Console.WriteLine($"Formato HAP    : {first.Format}, compresor {first.Compressor}" +
                          (first.Compressor == HapCompressor.Complex ? $" ({first.ChunkCount} chunks)" : ""));
        Console.WriteLine($"DXT por frame  : {HapFrame.DxtSize(info.Width, info.Height, first.Format) / 1024.0 / 1024.0:F2} MB");
        Console.WriteLine($"Payload vídeo  : {payload / 1024.0 / 1024.0:F1} MB ({payload * 8.0 / info.DurationSeconds / 1_000_000:F1} Mbit/s)");
        Console.WriteLine(info.Audio is null
            ? "Audio          : (sin pista de audio)"
            : $"Audio          : {info.Audio.Fourcc}, {info.Audio.Channels} canal(es), {info.Audio.SampleRate:F0} Hz, {info.Audio.SampleSizeBits} bits (ignorada)");
    }

    private static HapFrameInfo InspectFirstFrame(string path, MovInfo info)
    {
        SampleEntry s = info.Samples[0];
        byte[] frame = new byte[s.Size];
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Position = s.Offset;
        fs.ReadExactly(frame);
        return HapFrame.Inspect(frame);
    }

    private static string DescribeFourcc(string fourcc) => fourcc switch
    {
        "Hap1" => "Hap, RGB DXT1",
        "Hap5" => "Hap Alpha, RGBA DXT5",
        "HapY" => "Hap Q, YCoCg DXT5",
        _ => "desconocido",
    };

    // ------------------------------------------------------------------
    //  Benchmark
    // ------------------------------------------------------------------

    private static void RunBenchmark(string path, MovInfo info)
    {
        Console.WriteLine();
        Console.WriteLine("== Benchmark ==");

        // Se precargan todos los frames comprimidos en RAM para medir SOLO la
        // decodificación (sin E/S de disco). El pico de working set incluye
        // por tanto esta precarga; se informa por separado.
        byte[][] frames = PreloadFrames(path, info);
        long compressedBytes = frames.Sum(f => (long)f.Length);

        HapFrameInfo first = HapFrame.Inspect(frames[0]);
        int dxtSize = HapFrame.DxtSize(info.Width, info.Height, first.Format);
        long decodedBytes = (long)dxtSize * frames.Length;

        // Calentamiento (JIT + pools internos de Snappier)
        byte[] warm = new byte[dxtSize];
        for (int i = 0; i < Math.Min(8, frames.Length); i++)
        {
            HapFrame.Decode(frames[i], warm, parallel: false);
        }

        // --- Pasada 1: single-thread, latencia por frame -------------------
        double[] perFrameMs = new double[frames.Length];
        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < frames.Length; i++)
        {
            long t0 = sw.ElapsedTicks;
            HapFrame.Decode(frames[i], warm, parallel: false);
            perFrameMs[i] = (sw.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
        }

        sw.Stop();
        double stTotalMs = sw.Elapsed.TotalMilliseconds;

        Array.Sort(perFrameMs);
        double p50 = Percentile(perFrameMs, 0.50);
        double p95 = Percentile(perFrameMs, 0.95);
        double max = perFrameMs[^1];
        double stFps = frames.Length / (stTotalMs / 1000.0);
        double stMBps = decodedBytes / 1024.0 / 1024.0 / (stTotalMs / 1000.0);

        // --- Pasada 2: single-thread + chunks en paralelo (si aplica) ------
        double chunkedMtMs = 0;
        if (first.Compressor == HapCompressor.Complex && first.ChunkCount > 1)
        {
            sw.Restart();
            for (int i = 0; i < frames.Length; i++)
            {
                HapFrame.Decode(frames[i], warm, parallel: true);
            }

            sw.Stop();
            chunkedMtMs = sw.Elapsed.TotalMilliseconds;
        }

        // --- Pasada 3: multi-thread a nivel de frame -----------------------
        // Simula el caso real de reproducir N vídeos a la vez: cada hilo
        // decodifica frames completos con su propio buffer de salida.
        int dop = Environment.ProcessorCount;
        sw.Restart();
        Parallel.For(
            0,
            frames.Length,
            new ParallelOptions { MaxDegreeOfParallelism = dop },
            () => new byte[dxtSize],
            (i, _, buffer) =>
            {
                HapFrame.Decode(frames[i], buffer, parallel: false);
                return buffer;
            },
            _ => { });
        sw.Stop();
        double mtTotalMs = sw.Elapsed.TotalMilliseconds;
        double mtFps = frames.Length / (mtTotalMs / 1000.0);
        double mtMBps = decodedBytes / 1024.0 / 1024.0 / (mtTotalMs / 1000.0);

        using Process proc = Process.GetCurrentProcess();
        proc.Refresh();
        double peakWsMb = proc.PeakWorkingSet64 / 1024.0 / 1024.0;
        double preloadMb = compressedBytes / 1024.0 / 1024.0;

        Console.WriteLine($"Frames decodificados : {frames.Length} ({decodedBytes / 1024.0 / 1024.0:F0} MB de DXT)");
        Console.WriteLine($"Single-thread        : {stFps:F1} fps sostenidos, {stMBps:F0} MB/s de DXT");
        Console.WriteLine($"  ms/frame           : p50 {p50:F2} | p95 {p95:F2} | max {max:F2}");
        if (chunkedMtMs > 0)
        {
            double fps = frames.Length / (chunkedMtMs / 1000.0);
            Console.WriteLine($"Chunks en paralelo   : {fps:F1} fps sostenidos ({first.ChunkCount} chunks/frame)");
        }

        Console.WriteLine($"Multi-thread (x{dop,2})  : {mtFps:F1} fps sostenidos, {mtMBps:F0} MB/s de DXT");
        Console.WriteLine($"Pico working set     : {peakWsMb:F0} MB (incluye {preloadMb:F0} MB de frames precargados)");

        var json = new
        {
            file = Path.GetFileName(path),
            codec = info.VideoFourcc,
            width = info.Width,
            height = info.Height,
            frames = frames.Length,
            compressor = first.Compressor.ToString(),
            chunks = first.ChunkCount,
            st_fps = Math.Round(stFps, 1),
            st_mbps = Math.Round(stMBps, 0),
            ms_p50 = Math.Round(p50, 3),
            ms_p95 = Math.Round(p95, 3),
            ms_max = Math.Round(max, 3),
            chunked_mt_fps = chunkedMtMs > 0 ? Math.Round(frames.Length / (chunkedMtMs / 1000.0), 1) : 0,
            mt_fps = Math.Round(mtFps, 1),
            mt_mbps = Math.Round(mtMBps, 0),
            threads = dop,
            peak_ws_mb = Math.Round(peakWsMb, 0),
            preload_mb = Math.Round(preloadMb, 0),
        };
        Console.WriteLine();
        Console.WriteLine(JsonSerializer.Serialize(json));
    }

    private static byte[][] PreloadFrames(string path, MovInfo info)
    {
        byte[][] frames = new byte[info.FrameCount][];
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        for (int i = 0; i < info.FrameCount; i++)
        {
            SampleEntry s = info.Samples[i];
            byte[] buffer = new byte[s.Size];
            fs.Position = s.Offset;
            fs.ReadExactly(buffer);
            frames[i] = buffer;
        }

        return frames;
    }

    private static double Percentile(double[] sorted, double p)
    {
        double rank = p * (sorted.Length - 1);
        int lo = (int)rank;
        int hi = Math.Min(lo + 1, sorted.Length - 1);
        double frac = rank - lo;
        return sorted[lo] + ((sorted[hi] - sorted[lo]) * frac);
    }
}
