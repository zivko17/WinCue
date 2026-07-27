using System.Diagnostics;
using System.Globalization;
using System.Text;
using FFmpeg.AutoGen;
using ProResLab;
using ProResLab.Gpu;

try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // Consola redirigida: no es motivo para abortar.
}

if (args.Length == 0)
{
    Console.WriteLine("""
        prores-lab-gpu — camino GPU de 10/12 bits del laboratorio de ProRes

          prores-lab-gpu <fichero.mov> --psnr [--frame N] [--croma point|lineal]
              Decodifica el frame N, lo sube como planos R16_UNORM, lo convierte
              a RGB en shader y compara el resultado contra (a) el decode+conversión
              de ffmpeg CLI del MISMO frame y (b) la referencia de CPU del lab.

          prores-lab-gpu <fichero.mov> --bench [--frames N] [--croma point|lineal]
              Decode + subida + shader sostenidos: fps y coste por fase.
        """);
    return 0;
}

string path = args[0];
if (!File.Exists(path))
{
    Console.Error.WriteLine($"No existe el fichero: {path}");
    return 2;
}

ChromaSampling chroma = Arg("--croma", "point") == "lineal" ? ChromaSampling.Linear : ChromaSampling.Point;

if (Has("--psnr"))
{
    return Psnr(path, ArgInt("--frame", 10), chroma);
}

return BenchGpu(path, ArgInt("--frames", 0), chroma);

// ---------------------------------------------------------------------------

bool Has(string name) => Array.IndexOf(args, name) >= 0;

string Arg(string name, string fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

int ArgInt(string name, int fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length
        && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
}

// ---------------------------------------------------------------------------

static int Psnr(string path, int frameIndex, ChromaSampling chroma)
{
    MovInfo info = MovDemuxer.Open(path);
    if (frameIndex < 0 || frameIndex >= info.FrameCount)
    {
        Console.Error.WriteLine($"Frame {frameIndex} fuera de rango (0..{info.FrameCount - 1}).");
        return 2;
    }

    using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    using PaddedBuffer buffer = new(info.MaxSampleSize);
    using ProResDecoder decoder = new(info.Width, info.Height, 1, ThreadMode.SingleThread, info.VideoFourcc);

    // ProRes es TODO intra: el frame N se decodifica solo, sin arrastrar los previos.
    int size = buffer.ReadSample(fs, info.Samples[frameIndex]);
    decoder.SendPacket(buffer.AsPacket(size));
    if (!decoder.TryReceiveFrame())
    {
        Console.Error.WriteLine("El decodificador no entregó el frame.");
        return 1;
    }

    PixelLayout layout = decoder.Layout;
    bool fullRange = decoder.ColorRange == AVColorRange.AVCOL_RANGE_JPEG;
    ColorMatrix matrix = MatrixOf(decoder, info);
    ColorParams parameters = ColorParams.Create(layout.BitDepth, fullRange, matrix, layout.HasAlpha);

    Console.WriteLine($"{Path.GetFileName(path)} frame {frameIndex}: {decoder.FrameWidth}x{decoder.FrameHeight} "
                      + $"{layout.Name}, matriz {matrix}, rango {(fullRange ? "completo" : "limitado")}, "
                      + $"croma {chroma}");

    using YuvGpuPipeline pipeline = new(
        decoder.FrameWidth, decoder.FrameHeight, layout, parameters, chroma, keepAlpha: false);
    Console.WriteLine($"adaptador: {pipeline.AdapterName}");

    pipeline.Upload(0, decoder);
    pipeline.Render(0);
    pipeline.WaitForGpu();
    byte[] gpu = pipeline.ReadbackRenderTarget();

    ushort[][] planes = decoder.CopyPlanes();
    decoder.ReleaseFrame();
    byte[] cpu = CpuReference.ToBgra(planes, info.Width, info.Height, layout, fullRange, matrix);

    byte[]? reference = FfmpegReference.Decode(path, frameIndex, info.Width, info.Height);

    Console.WriteLine();
    if (reference is not null)
    {
        Console.WriteLine($"PSNR GPU vs ffmpeg CLI : {Fmt(ProResLab.Psnr.Compute(gpu, reference))} dB   "
                          + $"(máx |Δ| = {ProResLab.Psnr.MaxAbsDiff(gpu, reference)})");
        Console.WriteLine($"PSNR CPU vs ffmpeg CLI : {Fmt(ProResLab.Psnr.Compute(cpu, reference))} dB   "
                          + $"(máx |Δ| = {ProResLab.Psnr.MaxAbsDiff(cpu, reference)})");
    }
    else
    {
        Console.WriteLine("(sin ffmpeg en el PATH: no hay referencia externa)");
    }

    Console.WriteLine($"PSNR GPU vs CPU del lab: {Fmt(ProResLab.Psnr.Compute(gpu, cpu))} dB   "
                      + $"(máx |Δ| = {ProResLab.Psnr.MaxAbsDiff(gpu, cpu)})");
    return 0;
}

static int BenchGpu(string path, int maxFrames, ChromaSampling chroma)
{
    MovInfo info = MovDemuxer.Open(path);
    int n = maxFrames > 0 ? Math.Min(maxFrames, info.FrameCount) : info.FrameCount;

    using PreloadedClip clip = PreloadedClip.Load(path, n);
    using ProResDecoder decoder = new(
        info.Width, info.Height, Environment.ProcessorCount, ThreadMode.Slice, info.VideoFourcc);

    // Primer frame para conocer layout/color antes de crear el pipeline
    decoder.SendPacket(clip.Packet(0));
    if (!decoder.TryReceiveFrame())
    {
        Console.Error.WriteLine("El decodificador no entregó el primer frame.");
        return 1;
    }

    PixelLayout layout = decoder.Layout;
    bool fullRange = decoder.ColorRange == AVColorRange.AVCOL_RANGE_JPEG;
    ColorMatrix matrix = MatrixOf(decoder, info);
    ColorParams parameters = ColorParams.Create(layout.BitDepth, fullRange, matrix, layout.HasAlpha);

    using YuvGpuPipeline pipeline = new(
        info.Width, info.Height, layout, parameters, chroma, layout.HasAlpha);
    decoder.ReleaseFrame();
    decoder.Reset();

    Console.WriteLine($"{Path.GetFileName(path)}: {info.Width}x{info.Height} {layout.Name} "
                      + $"{clip.Count} frames · adaptador {pipeline.AdapterName}");
    Console.WriteLine($"subida por frame: {(pipeline.UploadBytesPerFrame / (1024.0 * 1024.0))
        .ToString("0.00", CultureInfo.InvariantCulture)} MB en {layout.PlaneCount} planos");
    Console.WriteLine();

    List<double> decodeMs = new(clip.Count);
    List<double> uploadMs = new(clip.Count);
    List<double> renderMs = new(clip.Count);

    // Calentamiento
    for (int i = 0; i < Math.Min(10, clip.Count); i++)
    {
        decoder.SendPacket(clip.Packet(i));
        if (decoder.TryReceiveFrame())
        {
            pipeline.Upload(i, decoder);
            pipeline.Render(i);
            decoder.ReleaseFrame();
        }
    }

    pipeline.WaitForGpu();
    decoder.Reset();

    Stopwatch step = new();
    Stopwatch total = Stopwatch.StartNew();
    int frames = 0;
    for (int i = 0; i < clip.Count; i++)
    {
        step.Restart();
        decoder.SendPacket(clip.Packet(i));
        bool got = decoder.TryReceiveFrame();
        step.Stop();
        decodeMs.Add(step.Elapsed.TotalMilliseconds);
        if (!got)
        {
            continue;
        }

        step.Restart();
        pipeline.Upload(i, decoder);
        step.Stop();
        uploadMs.Add(step.Elapsed.TotalMilliseconds);

        step.Restart();
        pipeline.Render(i);
        step.Stop();
        renderMs.Add(step.Elapsed.TotalMilliseconds);

        decoder.ReleaseFrame();
        frames++;
    }

    pipeline.WaitForGpu();
    total.Stop();

    Console.WriteLine($"{"fase",-12} {"p50 ms",8} {"p95 ms",8} {"max ms",8}");
    Console.WriteLine(new string('-', 40));
    Row("decode", decodeMs);
    Row("subida", uploadMs);
    Row("shader", renderMs);
    Console.WriteLine();
    Console.WriteLine($"cadena completa: {(frames / total.Elapsed.TotalSeconds).ToString("0.0", CultureInfo.InvariantCulture)} fps "
                      + $"({frames} frames en {total.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s)");
    Console.WriteLine($"ancho de banda de subida: {(pipeline.UploadBytesPerFrame * frames / total.Elapsed.TotalSeconds / (1024.0 * 1024.0))
        .ToString("0", CultureInfo.InvariantCulture)} MB/s");
    return 0;

    static void Row(string name, List<double> values)
    {
        (double p50, double p95, double max) = Bench.Percentiles(values);
        Console.WriteLine($"{name,-12} {Fmt(p50),8} {Fmt(p95),8} {Fmt(max),8}");
    }
}

static ColorMatrix MatrixOf(ProResDecoder decoder, MovInfo info)
{
    // El BITSTREAM manda: ffmpeg no escribe el átomo 'colr' al codificar ProRes
    // (comprobado), pero el decodificador sí rellena AVFrame.colorspace desde
    // la cabecera del frame. El 'colr' del contenedor queda de respaldo para
    // ficheros de otras herramientas (Apple SÍ lo escribe).
    if (decoder.ColorSpace is not (AVColorSpace.AVCOL_SPC_UNSPECIFIED or AVColorSpace.AVCOL_SPC_RGB))
    {
        return ColorConvert.MatrixFromCode((int)decoder.ColorSpace);
    }

    return info.Colr.HasMatrix ? ColorConvert.MatrixFromCode(info.Colr.Matrix) : ColorMatrix.Bt709;
}

static string Fmt(double v) => double.IsInfinity(v)
    ? "∞"
    : v.ToString("F2", CultureInfo.InvariantCulture);
