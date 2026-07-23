// =============================================================================
// HapCoreTests.cs — Núcleo HAP promocionado (demux + decode + frame source)
// =============================================================================
// Ejercita las piezas de WinCue.Video.Hap SIN GPU: MovDemuxer y HapFrame con
// media real generada por ffmpeg (clips de 320x240, 1 s), y HapFrameSource
// con su aritmética de índices LÓGICOS (trim + pasadas). Si ffmpeg no está
// disponible los tests no afirman nada (mismo patrón que VideoEngineTests).
// =============================================================================

using System.Diagnostics;
using WinCue.Video.Hap;

namespace WinCue.Video.Tests;

public sealed class HapMediaFixture : IDisposable
{
    public string Dir { get; }
    public string Hap1 { get; }        // 1 s Hap1 (sin audio)
    public string HapConAudio { get; } // 1 s Hap1 + seno PCM 16-bit
    public bool FFmpegOk { get; }

    public HapMediaFixture()
    {
        Dir = Path.Combine(Path.GetTempPath(), "wincue_hap_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Hap1 = Path.Combine(Dir, "hap1.mov");
        HapConAudio = Path.Combine(Dir, "hap-audio.mov");

        FFmpegOk =
            Run("-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30:duration=1",
                "-c:v", "hap", "-format", "hap", Hap1)
            && Run("-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30:duration=1",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
                "-c:v", "hap", "-format", "hap", "-c:a", "pcm_s16le", "-shortest",
                HapConAudio);
    }

    private static bool Run(params string[] args)
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
            p.WaitForExit(30_000);
            return p.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

public class HapCoreTests : IClassFixture<HapMediaFixture>
{
    private readonly HapMediaFixture _media;

    public HapCoreTests(HapMediaFixture media) => _media = media;

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }

    [Fact]
    public void Demux_LeeLaPistaDeVideo()
    {
        if (!_media.FFmpegOk) return;
        var info = MovDemuxer.Open(_media.Hap1);

        Assert.Equal("Hap1", info.VideoFourcc);
        Assert.Equal(320, info.Width);
        Assert.Equal(240, info.Height);
        Assert.Equal(30, info.FrameCount);
        Assert.Equal(30.0, info.Fps, 1);
        Assert.Null(info.Audio);
    }

    [Fact]
    public void Demux_EnumeraLaPistaDeAudio()
    {
        if (!_media.FFmpegOk) return;
        var info = MovDemuxer.Open(_media.HapConAudio);

        Assert.NotNull(info.Audio);
        Assert.Equal(16, info.Audio!.SampleSizeBits);
    }

    [Fact]
    public void Decode_PrimerFrame_AlTamanoDxtExacto()
    {
        if (!_media.FFmpegOk) return;
        var info = MovDemuxer.Open(_media.Hap1);
        byte[] frame = ReadSample(_media.Hap1, info, 0);
        byte[] dxt = new byte[HapFrame.DxtSize(info.Width, info.Height, HapTextureFormat.RgbDxt1)];

        var (format, written) = HapFrame.Decode(frame, dxt, parallel: false);
        Assert.Equal(HapTextureFormat.RgbDxt1, format);
        Assert.Equal(dxt.Length, written);          // 320*240/2 = 38 400 bytes
        Assert.Contains(dxt, b => b != 0);          // no es un frame vacío
    }

    [Fact]
    public void FrameSource_TrimYPasadas_MapeanAlFrameFisicoCorrecto()
    {
        if (!_media.FFmpegOk) return;
        var info = MovDemuxer.Open(_media.Hap1);

        // Segmento con trim (frames físicos 6..17, 12 frames) y 2 pasadas:
        // el índice LÓGICO 15 (pasada 1, frame 3) debe ser el físico 9.
        const int start = 6, seg = 12;
        using var source = new HapFrameSource(_media.Hap1, info,
            HapTextureFormat.RgbDxt1, start, seg, totalLogical: seg * 2);

        byte[]? logical3 = null, logical15 = null;
        for (long l = 0; l < seg * 2; l++)
        {
            long cur = l;
            Assert.True(WaitFor(() => source.TryGet(cur, out _), 5000),
                $"el frame lógico {cur} no llegó a decodificarse");
            _ = source.TryGet(cur, out byte[] dxt);
            if (l == 3) logical3 = (byte[])dxt.Clone();
            if (l == 15) logical15 = (byte[])dxt.Clone();
            source.Consume(cur);
        }

        Assert.NotNull(logical3);
        Assert.NotNull(logical15);
        Assert.Equal(logical3, logical15);          // mismo frame físico (9)

        // Decode independiente del físico 9 como referencia externa
        byte[] reference = new byte[HapFrame.DxtSize(info.Width, info.Height, HapTextureFormat.RgbDxt1)];
        _ = HapFrame.Decode(ReadSample(_media.Hap1, info, start + 3), reference, parallel: false);
        Assert.Equal(reference, logical15);

        // Y tras la última pasada no hay más frames
        Assert.False(source.TryGet(seg * 2, out _));
        Assert.Null(source.Error);
    }

    [Fact]
    public void FramePacer_AvanceNormalYDrops()
    {
        var pacer = new FramePacer(30.0, 30);

        var s0 = pacer.Peek(0.0);
        Assert.True(s0.IsNew);
        Assert.Equal(0, s0.FrameIndex);
        pacer.Commit(s0);

        var s1 = pacer.Peek(1.0 / 30.0 + 0.001);
        Assert.Equal(1, s1.FrameIndex);
        Assert.Equal(0, s1.Dropped);
        pacer.Commit(s1);

        var s5 = pacer.Peek(5.0 / 30.0 + 0.001);    // el reloj saltó 3 frames
        Assert.Equal(5, s5.FrameIndex);
        Assert.Equal(3, s5.Dropped);
        pacer.Commit(s5);

        Assert.True(pacer.Peek(2.0).Finished);      // más allá del clip
        Assert.Equal(3, pacer.TotalDropped);
    }

    private static byte[] ReadSample(string path, MovInfo info, int index)
    {
        var sample = info.Samples[index];
        using var fs = File.OpenRead(path);
        fs.Position = sample.Offset;
        byte[] data = new byte[sample.Size];
        fs.ReadExactly(data);
        return data;
    }
}
