// =============================================================================
// HapConvertIntegrationTests.cs — Conversión in-app con ffmpeg REAL
// =============================================================================
// Genera un clip diminuto h264 (2 s, 320x240, con audio), lo convierte con el
// runner real del controlador (la receta única de MediaImporter con -chunks) y
// verifica que el RESULTADO lo abre y decodifica el motor propio (MovDemuxer +
// HapFrame). Si ffmpeg no está en el PATH, el test no afirma nada (mismo
// patrón que HapCoreTests).
// =============================================================================

using System.Diagnostics;
using WinCue.Controller;
using WinCue.Video.Hap;

namespace WinCue.Video.Tests;

public sealed class HapConvertIntegrationTests : IDisposable
{
    private readonly string _dir;

    public HapConvertIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wincue_hapconv_it_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static bool RunFfmpeg(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-y");
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardError.ReadToEnd();
            p.WaitForExit(60_000);
            return p.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }

    [Fact]
    public async Task ConversionReal_H264AHap_ElMotorPropioLoAbreYDecodifica()
    {
        // Clip origen: 2 s de testsrc2 en h264 + seno (la pista de audio debe
        // CONSERVARSE en la conversión, transcodificada a PCM 16-bit)
        string src = Path.Combine(_dir, "origen.mp4");
        if (!RunFfmpeg(
                "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30:duration=2",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                "-c:v", "libx264", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-shortest", src))
            return;   // sin ffmpeg no se afirma nada

        string dest = HapConvertService.DestPathFor(src);
        var progress = new List<double>();
        var runner = new FfmpegHapConversionRunner();
        var result = await runner.ConvertAsync(src, dest, progress.Add, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(Path.Combine(_dir, "origen.hap.mov"), dest);
        Assert.Contains(progress, p => p > 0.0);              // hubo progreso real
        Assert.Equal(100.0, progress[^1], 3);

        // ffprobe (la caché del enrutado del controlador) lo ve como HAP
        Assert.True(MediaImporter.IsUnsupportedVideoCodec(dest));

        // El MovDemuxer del motor propio lo abre: vídeo Hap1, dimensiones y
        // fps del original, ~60 frames, pista de audio PCM 16-bit conservada
        var info = MovDemuxer.Open(dest);
        Assert.Equal("Hap1", info.VideoFourcc);
        Assert.Equal(320, info.Width);
        Assert.Equal(240, info.Height);
        Assert.Equal(30.0, info.Fps, 1);
        Assert.Equal(60, info.FrameCount);
        Assert.NotNull(info.Audio);
        Assert.Equal(16, info.Audio!.SampleSizeBits);

        // Y el primer frame decodifica al tamaño DXT1 exacto (la receta lleva
        // -chunks 4: frames «Complex» — la ruta paralela del decoder)
        var sample = info.Samples[0];
        byte[] frame = new byte[sample.Size];
        using (var fs = File.OpenRead(dest))
        {
            fs.Position = sample.Offset;
            fs.ReadExactly(frame);
        }
        byte[] dxt = new byte[HapFrame.DxtSize(info.Width, info.Height, HapTextureFormat.RgbDxt1)];
        var (format, written) = HapFrame.Decode(frame, dxt, parallel: true);
        Assert.Equal(HapTextureFormat.RgbDxt1, format);
        Assert.Equal(dxt.Length, written);
        Assert.Contains(dxt, b => b != 0);
    }
}
