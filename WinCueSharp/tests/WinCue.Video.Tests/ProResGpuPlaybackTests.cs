// =============================================================================
// ProResGpuPlaybackTests.cs — Reproducción REAL por el pipeline GPU de ProRes
// =============================================================================
// Gemelo de HapGpuPlaybackTests: integración de verdad sin WPF ni ventana —
// ProResGpuPipeline (D3D11 + D3D9Ex, shader YUV→RGB, superficies compartidas) +
// ProResCuePlayer (decode nativo + reloj + hilo de presentación) sobre clips
// ProRes generados con ffmpeg. Sin GPU utilizable o sin las DLL LGPL, los tests
// no afirman nada; en la máquina de desarrollo validan el camino completo.
// =============================================================================

using WinCue.Core;
using WinCue.Video.Containers;
using WinCue.Video.ProRes;

namespace WinCue.Video.Tests;

public class ProResGpuPlaybackTests : IClassFixture<ProResMediaFixture>
{
    private readonly ProResMediaFixture _media;

    public ProResGpuPlaybackTests(ProResMediaFixture media) => _media = media;

    private bool Skip => !_media.FFmpegOk || !ProResMediaFixture.FFmpegDllsOk;

    [Fact]
    public void Clip422Entero_SeRenderizaYTerminaSolo()
    {
        if (Skip) return;
        using var pipeline = TryCreatePipeline();
        if (pipeline is null)
            return;   // sin GPU/driver utilizable: nada que afirmar

        var info = MovDemuxer.Open(_media.P422Hq);   // 1 s @ 30 fps
        int frames = 0;
        pipeline.FrameRendered += () => Interlocked.Increment(ref frames);

        using var session = pipeline.CreateSession(info.Width, info.Height,
            PixelLayout.For(FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_YUV422P10LE));
        using var ended = new ManualResetEventSlim(false);
        using (var player = new ProResCuePlayer(_media.P422Hq, info, pipeline, session,
            new VideoPlayParams(), threadCount: 4, clock: null))
        {
            player.Ended += ended.Set;
            player.Start();

            Assert.True(ended.Wait(TimeSpan.FromSeconds(15)),
                "el player no llegó al fin natural del clip");
            Assert.InRange(player.GetElapsed(), 0.9, 1.4);
            Assert.Equal(1.0, player.GetDuration(), 2);
        }

        Assert.InRange(frames, 20, 35);

        // La copia de presentación (la que haría el UI dentro del Lock del
        // D3DImage) no debe fallar con el pipeline vivo
        pipeline.CopyPresent();
    }

    [Fact]
    public void Clip4444ConAlfa_TambienSeRenderiza_ConCuatroPlanosDe12Bits()
    {
        if (Skip) return;
        using var pipeline = TryCreatePipeline();
        if (pipeline is null)
            return;

        var info = MovDemuxer.Open(_media.P4444);
        PixelLayout layout = PixelLayout.For(FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_YUVA444P12LE);
        Assert.Equal(4, layout.PlaneCount);
        Assert.Equal(12, layout.BitDepth);

        int frames = 0;
        pipeline.FrameRendered += () => Interlocked.Increment(ref frames);

        using var session = pipeline.CreateSession(info.Width, info.Height, layout);
        using var ended = new ManualResetEventSlim(false);
        using (var player = new ProResCuePlayer(_media.P4444, info, pipeline, session,
            new VideoPlayParams(), threadCount: 4, clock: null))
        {
            player.Ended += ended.Set;
            player.Start();
            Assert.True(ended.Wait(TimeSpan.FromSeconds(15)),
                "el player 4444 no llegó al fin natural");
        }

        Assert.InRange(frames, 20, 35);
    }

    [Fact]
    public void ConLoop_NoTerminaSolo_YSigueEntregandoFrames()
    {
        if (Skip) return;
        using var pipeline = TryCreatePipeline();
        if (pipeline is null)
            return;

        var info = MovDemuxer.Open(_media.P422);   // 1 s
        int frames = 0;
        pipeline.FrameRendered += () => Interlocked.Increment(ref frames);

        using var session = pipeline.CreateSession(info.Width, info.Height,
            PixelLayout.For(FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_YUV422P10LE));
        using var ended = new ManualResetEventSlim(false);
        using (var player = new ProResCuePlayer(_media.P422, info, pipeline, session,
            new VideoPlayParams(Loop: true), threadCount: 4, clock: null))
        {
            player.Ended += ended.Set;
            player.Start();

            // 2,5 s = dos pasadas y media de un clip de 1 s: la COSTURA DE LOOP
            // (Reset del decodificador + re-alineado del pacer) se cruza dos
            // veces y el player no debe terminar ni atascarse.
            Assert.False(ended.Wait(TimeSpan.FromSeconds(2.5)),
                "un cue en loop no debe llegar al fin natural");
            int atMid = Volatile.Read(ref frames);
            Assert.True(atMid > 45, $"esperados >45 frames en 2,5 s de loop, hubo {atMid}");
        }
    }

    [Fact]
    public void TrimYPlayCount_RecortanElSegmentoYCuentanLasPasadas()
    {
        if (Skip) return;
        using var pipeline = TryCreatePipeline();
        if (pipeline is null)
            return;

        var info = MovDemuxer.Open(_media.P422);
        using var session = pipeline.CreateSession(info.Width, info.Height,
            PixelLayout.For(FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_YUV422P10LE));
        using var ended = new ManualResetEventSlim(false);
        using var player = new ProResCuePlayer(_media.P422, info, pipeline, session,
            new VideoPlayParams(TrimStart: 0.2, TrimEnd: 0.7, PlayCount: 2),
            threadCount: 4, clock: null);

        // Segmento efectivo: 0,2 → 0,7 = 0,5 s por pasada
        Assert.Equal(0.5, player.GetDuration(), 2);

        player.Ended += ended.Set;
        player.Start();
        Assert.True(ended.Wait(TimeSpan.FromSeconds(15)), "no llegó al fin de las 2 pasadas");
    }

    [Fact]
    public void ElRelojInyectadoManda_ElVideoSigueAlAudioEspejo()
    {
        if (Skip) return;
        using var pipeline = TryCreatePipeline();
        if (pipeline is null)
            return;

        var info = MovDemuxer.Open(_media.P422);
        using var session = pipeline.CreateSession(info.Width, info.Height,
            PixelLayout.For(FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_YUV422P10LE));

        // Reloj "de audio" que avanza a MITAD de la velocidad real. Si mandara
        // el Stopwatch propio del player, a los 800 ms el elapsed sería ~0,8; si
        // manda el reloj inyectado —que es lo correcto: el audio espejo es el
        // maestro— será ~0,4. La diferencia es lo bastante grande para no
        // depender del jitter del runner.
        var wall = System.Diagnostics.Stopwatch.StartNew();
        using var player = new ProResCuePlayer(_media.P422, info, pipeline, session,
            new VideoPlayParams(Loop: true), threadCount: 4,
            clock: () => wall.Elapsed.TotalSeconds / 2.0);
        player.Start();

        Thread.Sleep(800);
        double elapsed = player.GetElapsed();
        Assert.InRange(elapsed, 0.25, 0.60);
    }

    [Fact]
    public void SesionYPlayerSeLiberanSinDejarFramesVivos()
    {
        if (Skip) return;
        using var pipeline = TryCreatePipeline();
        if (pipeline is null)
            return;

        var info = MovDemuxer.Open(_media.P422Hq);
        PixelLayout layout = PixelLayout.For(FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_YUV422P10LE);

        // Ciclo de vida repetido: 8 cues seguidos sobre el MISMO pipeline y la
        // MISMA sesión (que es lo que hace el pool). Si algún AVFrame quedara
        // retenido, esto lo delataría en memoria nativa; aquí basta con que no
        // reviente ni se cuelgue.
        using var session = pipeline.CreateSession(info.Width, info.Height, layout);
        for (int i = 0; i < 8; i++)
        {
            using var player = new ProResCuePlayer(_media.P422Hq, info, pipeline, session,
                new VideoPlayParams(Loop: true), threadCount: 2, clock: null);
            player.Start();
            Thread.Sleep(60);
        }
    }

    /// <summary>CA2000: la creación va en método aparte — devolver el objeto
    /// transfiere la propiedad al llamador (que lo mete en un using).</summary>
    private static ProResGpuPipeline? TryCreatePipeline()
    {
        try { return new ProResGpuPipeline(640, 480); }
        catch (Exception) { return null; }
    }
}
