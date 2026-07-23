// =============================================================================
// HapGpuPlaybackTests.cs — Reproducción REAL por el pipeline GPU del motor HAP
// =============================================================================
// Integración de verdad sin WPF ni ventana: HapGpuPipeline (D3D11 + D3D9Ex,
// shaders, superficies compartidas) + HapCuePlayer (decode + reloj + hilo de
// presentación) sobre un clip HAP generado con ffmpeg. Si la máquina no tiene
// GPU utilizable (CI headless), la creación del pipeline lanza y el test no
// afirma nada — en la máquina de desarrollo valida el camino completo.
// =============================================================================

using WinCue.Core;
using WinCue.Video.Hap;

namespace WinCue.Video.Tests;

public class HapGpuPlaybackTests : IClassFixture<HapMediaFixture>
{
    private readonly HapMediaFixture _media;

    public HapGpuPlaybackTests(HapMediaFixture media) => _media = media;

    [Fact]
    public void ClipEntero_SeRenderizaYTerminaSolo()
    {
        if (!_media.FFmpegOk) return;
        using var pipeline = TryCreatePipeline();
        if (pipeline is null)
            return;   // sin GPU/driver utilizable: nada que afirmar
        var info = MovDemuxer.Open(_media.Hap1);   // 1 s @ 30 fps
        int frames = 0;
        pipeline.FrameRendered += () => Interlocked.Increment(ref frames);

        using var ended = new ManualResetEventSlim(false);
        using (var player = new HapCuePlayer(_media.Hap1, info,
            HapTextureFormat.RgbDxt1, pipeline, new VideoPlayParams(),
            clock: null))
        {
            player.Ended += ended.Set;
            player.Start();

            // El clip dura 1 s: margen holgado para el arranque + presentación
            Assert.True(ended.Wait(TimeSpan.FromSeconds(10)),
                "el player no llegó al fin natural del clip");
            Assert.InRange(player.GetElapsed(), 0.9, 1.4);
            Assert.Equal(1.0, player.GetDuration(), 2);
        }

        // A 30 fps en 1 s deben haberse presentado ~30 frames (sin reloj de
        // audio: Stopwatch propio; toleramos arranques lentos del runner)
        Assert.InRange(frames, 20, 35);

        // La copia de presentación (la que haría el UI dentro del Lock del
        // D3DImage) no debe fallar con el pipeline vivo
        pipeline.CopyPresent();
    }

    /// <summary>CA2000: la creación va en método aparte — devolver el objeto
    /// transfiere la propiedad al llamador (que lo mete en un using).</summary>
    private static HapGpuPipeline? TryCreatePipeline()
    {
        try { return new HapGpuPipeline(640, 480); }
        catch (Exception) { return null; }
    }
}
