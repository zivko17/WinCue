using System.Globalization;
using FFmpeg.AutoGen;

namespace ProResLab;

/// <summary>Subcomandos del ejecutable del laboratorio.</summary>
public static class Commands
{
    /// <summary>
    /// <c>--licencia</c>: ¿es LGPL de verdad la build de FFmpeg que cargamos?
    /// Devuelve 0 solo si el veredicto es LGPL (para usarlo en CI/scripts).
    /// </summary>
    public static int Licencia()
    {
        string? dir = FFmpegLoader.Locate();
        if (dir is null)
        {
            Console.WriteLine("ERROR: no se encontró la carpeta de DLL de FFmpeg.");
            Console.WriteLine("Ejecuta:  powershell -File obtener-ffmpeg-lgpl.ps1");
            Console.WriteLine($"O fija {FFmpegLoader.DirEnvVar} a una carpeta con avcodec-*.dll.");
            return 2;
        }

        FFmpegLoader.Load();

        Console.WriteLine($"RootPath fijado = {FFmpegLoader.RootPath}");
        Console.WriteLine("Módulos nativos REALMENTE cargados en el proceso:");
        foreach (string m in FFmpegLoader.LoadedModules())
        {
            Console.WriteLine($"  {m}");
        }

        Console.WriteLine();
        Console.WriteLine($"avutil    {Ver(ffmpeg.avutil_version())}");
        Console.WriteLine($"avcodec   {Ver(ffmpeg.avcodec_version())}");
        Console.WriteLine($"avformat  {Ver(ffmpeg.avformat_version())}");
        Console.WriteLine();

        LicenseCheck.Result r = LicenseCheck.Inspect();
        Console.WriteLine($"configuración = {r.Configuration}");
        Console.WriteLine();
        foreach (KeyValuePair<string, string> kv in r.Licenses)
        {
            Console.WriteLine($"  {kv.Key,-9} license = {kv.Value}");
        }

        Console.WriteLine();
        Console.WriteLine($"VEREDICTO: {r.Verdict} — {r.Reason}");

        Console.WriteLine();
        bool prores = HasDecoder("prores");
        Console.WriteLine($"decoder 'prores'      = {(prores ? "presente" : "AUSENTE")}");
        Console.WriteLine($"decoder 'prores_raw'  = {(HasDecoder("prores_raw") ? "presente" : "ausente (no hace falta)")}");

        if (r.Verdict != LicenseVerdict.Lgpl)
        {
            return 1;
        }

        return prores ? 0 : 3;
    }

    /// <summary><c>&lt;fichero&gt;</c>: demux + primer frame, sin benchmark.</summary>
    public static int Info(string path)
    {
        MovInfo info = MovDemuxer.Open(path);
        Console.WriteLine($"fichero    : {Path.GetFileName(path)}");
        Console.WriteLine($"fourcc     : {info.VideoFourcc}  (perfil {info.Profile})");
        Console.WriteLine($"dimensiones: {info.Width}x{info.Height}   depth={info.Depth}");
        Console.WriteLine($"fps        : {info.Fps.ToString("0.###", CultureInfo.InvariantCulture)}");
        Console.WriteLine($"duración   : {info.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s "
                          + $"({info.FrameCount} frames)");
        Console.WriteLine($"colr       : primaries={info.Colr.Primaries} transfer={info.Colr.Transfer} "
                          + $"matrix={info.Colr.Matrix} full={info.Colr.FullRange}");
        Console.WriteLine($"audio      : {(info.Audio is null ? "(ninguno)"
            : $"{info.Audio.Fourcc} {info.Audio.Channels}ch {info.Audio.SampleSizeBits}bit "
              + $"{info.Audio.SampleRate.ToString("0", CultureInfo.InvariantCulture)} Hz")}");

        long total = 0;
        foreach (SampleEntry s in info.Samples)
        {
            total += s.Size;
        }

        double mbits = info.DurationSeconds > 0 ? total * 8 / info.DurationSeconds / 1e6 : 0;
        Console.WriteLine($"bitstream  : {(total / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture)} MB, "
                          + $"{mbits.ToString("0", CultureInfo.InvariantCulture)} Mbit/s, "
                          + $"sample máx {info.MaxSampleSize} B");

        if (info.Profile == ProResProfile.None)
        {
            Console.WriteLine();
            Console.WriteLine("AVISO: el fourcc no es de ProRes; el laboratorio no lo decodificará.");
            return 1;
        }

        // Primer frame: lo que de verdad dice el bitstream
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using PaddedBuffer buffer = new(info.MaxSampleSize);
        using ProResDecoder decoder = new(info.Width, info.Height, 1, ThreadMode.SingleThread, info.VideoFourcc);
        int size = buffer.ReadSample(fs, info.Samples[0]);
        decoder.SendPacket(buffer.AsPacket(size));
        if (!decoder.TryReceiveFrame())
        {
            Console.WriteLine("El decodificador no entregó el primer frame.");
            return 1;
        }

        PixelLayout layout = decoder.Layout;
        Console.WriteLine();
        Console.WriteLine($"decode     : {decoder.FrameWidth}x{decoder.FrameHeight} {layout.Name} "
                          + $"({layout.BitDepth} bits, {layout.PlaneCount} planos, alfa={layout.HasAlpha})");
        Console.WriteLine($"color      : {decoder.ColorSpace} / {decoder.ColorRange} " + $"(perfil FFmpeg {decoder.DecoderProfile}, {decoder.BitsPerRawSample} bits por muestra)");
        for (int p = 0; p < layout.PlaneCount; p++)
        {
            Console.WriteLine($"  plano {p}  : {layout.PlaneWidth(p, decoder.FrameWidth)}x"
                              + $"{layout.PlaneHeight(p, decoder.FrameHeight)} muestras, stride {decoder.PlaneStride(p)} B");
        }

        Console.WriteLine($"frame YUV  : {(layout.FrameBytes(decoder.FrameWidth, decoder.FrameHeight) / (1024.0 * 1024.0))
            .ToString("0.00", CultureInfo.InvariantCulture)} MB por frame (lo que sube a la GPU)");
        decoder.ReleaseFrame();
        return 0;
    }

    /// <summary><c>--bench</c> de un clip con los modos de hilos pedidos.</summary>
    public static int BenchOne(string path, int threads, int repeat, int maxFrames)
    {
        using PreloadedClip clip = PreloadedClip.Load(path, maxFrames);
        string name = Path.GetFileNameWithoutExtension(path);
        Console.WriteLine($"{name}: {clip.Info.Width}x{clip.Info.Height} {clip.Info.VideoFourcc} "
                          + $"{clip.Count} frames, {(clip.TotalBytes / (1024.0 * 1024.0))
                              .ToString("0.0", CultureInfo.InvariantCulture)} MB en RAM");
        Console.WriteLine();
        PrintHeader();

        PrintRow(Bench.Run(clip, ThreadMode.SingleThread, 1, repeat, "1 hilo"));
        PrintRow(Bench.Run(clip, ThreadMode.Slice, threads, repeat, $"slice x{threads}"));
        PrintRow(Bench.Run(clip, ThreadMode.Frame, threads, repeat, $"frame x{threads}"));
        PrintRow(Bench.Run(clip, ThreadMode.SliceAndFrame, threads, repeat, $"slice+frame x{threads}"));
        return 0;
    }

    /// <summary><c>--concurrentes</c>: N vídeos a la vez, la pregunta del directo.</summary>
    public static int BenchConcurrent(string path, int maxStreams, int threadsPerStream, int repeat, int maxFrames)
    {
        using PreloadedClip clip = PreloadedClip.Load(path, maxFrames);
        string name = Path.GetFileNameWithoutExtension(path);
        Console.WriteLine($"{name}: {clip.Info.Width}x{clip.Info.Height} {clip.Info.VideoFourcc}, "
                          + $"{clip.Info.Fps.ToString("0.#", CultureInfo.InvariantCulture)} fps nominales");
        Console.WriteLine();
        PrintHeader();

        for (int s = 1; s <= maxStreams; s++)
        {
            ThreadMode mode = threadsPerStream > 1 ? ThreadMode.Slice : ThreadMode.SingleThread;
            BenchResult r = Bench.RunConcurrent(clip, s, mode, threadsPerStream, repeat,
                $"{s} vídeo(s) x{threadsPerStream}h");
            PrintRow(r);
        }

        return 0;
    }

    /// <summary>
    /// <c>--fuzz</c>: comprueba que un fichero CORRUPTO produce errores
    /// manejables y no se lleva el proceso por delante.
    /// </summary>
    /// <remarks>
    /// Es la evidencia del riesgo de estabilidad: un decodificador nativo en
    /// proceso no se puede "capturar" si peta de verdad, así que hay que saber
    /// cómo se porta con entrada hostil.
    /// </remarks>
    public static int Fuzz(string path, int iterations, int seed)
    {
        MovInfo info = MovDemuxer.Open(path);
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using PaddedBuffer buffer = new(info.MaxSampleSize);

        Random rng = new(seed);
        int ok = 0;
        int errores = 0;
        int sinFrame = 0;

        Console.WriteLine($"fuzz sobre {Path.GetFileName(path)}: {iterations} iteraciones, semilla {seed}");

        for (int it = 0; it < iterations; it++)
        {
            // Contexto NUEVO por iteración: un decodificador que ha visto basura
            // queda en estado indefinido y no debe reutilizarse.
            using (ProResDecoder decoder = new(info.Width, info.Height, 1, ThreadMode.SingleThread, info.VideoFourcc))
            {
                SampleEntry entry = info.Samples[rng.Next(info.Samples.Count)];
                int size = buffer.ReadSample(fs, entry);

                // Corrupción: entre 1 y 64 bytes al azar, EVITANDO los 8 primeros
                // (la cabecera de frame; corromperla solo prueba el rechazo temprano).
                int flips = rng.Next(1, 65);
                for (int f = 0; f < flips; f++)
                {
                    int pos = rng.Next(8, size);
                    buffer.Span[pos] = (byte)rng.Next(256);
                }

                try
                {
                    decoder.SendPacket(buffer.AsPacket(size));
                    if (decoder.TryReceiveFrame())
                    {
                        // Salida "válida": el decodificador tapó el destrozo. Se
                        // toca de verdad para provocar cualquier acceso ilegal.
                        _ = decoder.CopyPlanes();
                        decoder.ReleaseFrame();
                        ok++;
                    }
                    else
                    {
                        sinFrame++;
                    }
                }
                catch (InvalidDataException)
                {
                    errores++;
                }
                catch (NotSupportedException)
                {
                    errores++;
                }
            }
        }

        Console.WriteLine($"  frames tolerados (imagen dañada pero válida): {ok}");
        Console.WriteLine($"  errores devueltos y capturados en C#        : {errores}");
        Console.WriteLine($"  paquetes tragados sin salida                : {sinFrame}");
        Console.WriteLine("  el proceso sigue vivo: sin caída del decodificador nativo.");
        return 0;
    }

    private static void PrintHeader()
    {
        Console.WriteLine($"{"modo",-24} {"frames",7} {"fps",9} {"p50 ms",8} {"p95 ms",8} {"max ms",8} "
                          + $"{"MB/s bits",10} {"MB/s YUV",10} {"pico MB",9}");
        Console.WriteLine(new string('-', 110));
    }

    /// <summary>Imprime una fila del benchmark.</summary>
    public static void PrintRow(BenchResult r)
    {
        Console.WriteLine(
            $"{r.Label,-24} {r.Frames,7} {F(r.Fps, 1),9} {F(r.P50, 2),8} {F(r.P95, 2),8} {F(r.Max, 2),8} "
            + $"{F(r.CompressedMbPerSecond, 1),10} {F(r.DecodedMbPerSecond, 1),10} {F(r.PeakWorkingSetMb, 0),9}");
    }

    private static string F(double v, int decimals)
        => v.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static unsafe bool HasDecoder(string name)
        => ffmpeg.avcodec_find_decoder_by_name(name) is not null;

    private static string Ver(uint packed)
        => $"{packed >> 16}.{(packed >> 8) & 0xFF}.{packed & 0xFF}";
}
