// =============================================================================
// GpuSourceTests.cs — Fundamento del motor nuevo sobre GPU real (F1)
// =============================================================================
// Integración sin WPF, sin ventana y sin compositor: device único → anillo de
// fotogramas con PTS → mpv publicando dentro de texturas D3D11, verificado POR
// PÍXEL con lectura de vuelta (lo mismo que probó tools/mpv-d3d11-lab, ahora
// como regresión permanente).
//
// Si la máquina no tiene GPU utilizable, libmpv o el clip, los tests no
// afirman nada — misma convención que HapGpuPlaybackTests.
//
// OJO CON ESO: un salto silencioso se ve igual que un verde. Si hay que
// comprobar que en esta máquina se está ejercitando el camino de verdad, la
// forma rápida es convertir temporalmente las guardas en asserts y volver a
// pasar los tests. Medido así el 2026-07-26 en una RTX 5070 Ti: luma 128,60,
// pts 0,267, 2 fotogramas publicados.
// =============================================================================

using WinCue.Core;
using WinCue.Video;
using WinCue.Video.Gpu;
using WinCue.Video.Containers;
using WinCue.Video.Hap;

namespace WinCue.Video.Tests;

public class GpuSourceTests : IClassFixture<HapMediaFixture>
{
    private readonly HapMediaFixture _media;

    public GpuSourceTests(HapMediaFixture media) => _media = media;

    private static GpuDevice? TryCreateGpu()
    {
        try
        {
            return new GpuDevice();
        }
        catch (Exception)
        {
            return null;   // CI headless o driver sin D3D11
        }
    }

    private static string? FindMedia()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "hap-test",
                "demo-h264-1080p30-noaudio.mp4");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Anillo de fotogramas
    // ------------------------------------------------------------------

    [Fact]
    public void Anillo_EntregaElFotogramaDelInstantePedido()
    {
        using GpuDevice? gpu = TryCreateGpu();
        if (gpu is null) return;
        using var ring = new FrameRing(gpu, 64, 64);

        foreach (double pts in new[] { 0.0, 0.5, 1.0 })
        {
            int slot = ring.BeginWrite();
            Assert.True(slot >= 0);
            ring.Commit(slot, pts);
        }

        Assert.True(ring.TryAcquire(0.7, out FrameLease lease));
        using (lease)
            Assert.Equal(0.5, lease.Pts);

        // Antes del primero no hay nada que enseñar
        Assert.False(ring.TryAcquire(-1.0, out _));
    }

    [Fact]
    public void Anillo_NoPisaUnSlotPrestado()
    {
        using GpuDevice? gpu = TryCreateGpu();
        if (gpu is null) return;
        using var ring = new FrameRing(gpu, 64, 64, capacity: 3);

        int first = ring.BeginWrite();
        ring.Commit(first, 0.0);

        Assert.True(ring.TryAcquire(0.0, out FrameLease held));
        try
        {
            // Con el préstamo vivo, el productor no puede volver a ese slot
            var used = new HashSet<int>();
            for (int i = 0; i < 6; i++)
            {
                int slot = ring.BeginWrite();
                if (slot < 0)
                    break;
                Assert.NotEqual(first, slot);
                used.Add(slot);
                ring.Commit(slot, 1.0 + i);
            }
            Assert.NotEmpty(used);
        }
        finally
        {
            held.Dispose();
        }
    }

    [Fact]
    public void Anillo_NuncaReutilizaElUltimoPublicado()
    {
        using GpuDevice? gpu = TryCreateGpu();
        if (gpu is null) return;
        using var ring = new FrameRing(gpu, 64, 64, capacity: 2);

        int a = ring.BeginWrite();
        ring.Commit(a, 0.0);
        int b = ring.BeginWrite();

        // Con 2 slots y uno recién publicado, el productor debe ir al otro:
        // si pudiera reescribir el último, el compositor se quedaría sin nada
        // mientras dura la escritura.
        Assert.NotEqual(a, b);
    }

    // ------------------------------------------------------------------
    // mpv como fuente de textura D3D11
    // ------------------------------------------------------------------

    [Fact]
    public void MpvSource_PublicaFotogramasConImagenYTiempoCreciente()
    {
        using GpuDevice? gpu = TryCreateGpu();
        if (gpu is null) return;
        if (!MpvPlayer.IsAvailable) return;
        string? media = FindMedia();
        if (media is null) return;

        const int W = 640, H = 360;
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["terminal"] = "no",
            ["msg-level"] = "all=no",
            ["vo"] = "libmpv",      // obligatorio para el render API
            ["ao"] = "null",
            ["hwdec"] = "no",       // aísla la pregunta: interop, no decodificación
            ["keep-open"] = "yes",
        };

        using var player = new MpvPlayer(options);
        MpvSource? source = null;
        try
        {
            // CA2000: la propiedad pasa al `using (source)` de abajo; el
            // analizador no reconoce el patrón crear-en-try / usar-después
            // (necesario para poder saltar el test si el driver no soporta interop)
#pragma warning disable CA2000
            source = new MpvSource(gpu, player, W, H);
#pragma warning restore CA2000
        }
        catch (NotSupportedException)
        {
            return;   // driver sin WGL_NV_DX_interop2: nada que afirmar aquí
        }

        using (source)
        {
            player.Play(media);

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && !source.HasFrames)
                Thread.Sleep(20);
            Assert.True(source.HasFrames, "mpv no publicó ningún fotograma");

            // 1) El fotograma tiene IMAGEN de verdad dentro de la textura D3D11
            Assert.True(source.TryAcquire(double.MaxValue, out FrameLease lease));
            double firstPts;
            using (lease)
            {
                firstPts = lease.Pts;
                byte[] pixels = GpuReadback.Read(gpu, lease.Texture, W, H);
                double luma = GpuReadback.MeanLumaBgra8(pixels);
                Assert.True(luma > 2.0, $"la textura salió (casi) negra: luma {luma:F2}");
            }

            // 2) El tiempo avanza: no es el mismo fotograma congelado
            var advanceDeadline = DateTime.UtcNow.AddSeconds(10);
            double laterPts = firstPts;
            while (DateTime.UtcNow < advanceDeadline && laterPts <= firstPts)
            {
                if (source.TryAcquire(double.MaxValue, out FrameLease next))
                {
                    using (next)
                        laterPts = next.Pts;
                }
                Thread.Sleep(50);
            }
            Assert.True(laterPts > firstPts,
                $"el PTS no avanzó: {firstPts:F3} → {laterPts:F3}");

            // 3) Y no se ha quedado sin fotogramas por el camino
            Assert.Equal(0, source.Stats.WriteStalls);
        }
    }

    // ------------------------------------------------------------------
    // El motor HAP propio como fuente — la OTRA mitad de F1
    // ------------------------------------------------------------------

    [Fact]
    public void HapSource_PublicaFotogramasConImagenYTiempoCreciente()
    {
        using GpuDevice? gpu = TryCreateGpu();
        if (gpu is null) return;
        if (!_media.FFmpegOk) return;

        MovInfo info = MovDemuxer.Open(_media.Hap1);   // 1 s @ 30 fps, Hap1
        const int W = 320, H = 180;

        using var source = new HapSource(gpu, _media.Hap1, info,
            HapTextureFormat.RgbDxt1, new VideoPlayParams(), W, H);

        using var firstFrame = new ManualResetEventSlim(false);
        source.FirstFrame += firstFrame.Set;
        source.Start();

        Assert.True(firstFrame.Wait(TimeSpan.FromSeconds(15)), "no llegó el primer fotograma");

        // 1) Hay imagen de verdad dentro de la textura D3D11 del anillo
        Assert.True(source.TryAcquire(double.MaxValue, out FrameLease lease));
        double firstPts;
        using (lease)
        {
            firstPts = lease.Pts;
            byte[] pixels = GpuReadback.Read(gpu, lease.Texture, W, H);
            Assert.True(GpuReadback.MeanLumaBgra8(pixels) > 2.0, "la textura salió negra");
        }

        // 2) El PTS avanza al ritmo del medio (el clip es de 1 s a 30 fps)
        var deadline = DateTime.UtcNow.AddSeconds(10);
        double laterPts = firstPts;
        while (DateTime.UtcNow < deadline && laterPts <= firstPts)
        {
            if (source.TryAcquire(double.MaxValue, out FrameLease next))
            {
                using (next)
                    laterPts = next.Pts;
            }
            Thread.Sleep(20);
        }
        Assert.True(laterPts > firstPts, $"el PTS no avanzó: {firstPts:F3} → {laterPts:F3}");
    }

    [Fact]
    public void DosFuentesDistintas_VivenSobreElMismoDevice()
    {
        // El criterio de salida de F1: HAP y mpv, que hoy están en devices
        // incompatibles, publicando a la vez sobre UN device. Sin esto no
        // puede haber compositor.
        using GpuDevice? gpu = TryCreateGpu();
        if (gpu is null) return;
        if (!_media.FFmpegOk) return;
        if (!MpvPlayer.IsAvailable) return;
        string? mp4 = FindMedia();
        if (mp4 is null) return;

        MovInfo info = MovDemuxer.Open(_media.Hap1);
        using var hap = new HapSource(gpu, _media.Hap1, info,
            HapTextureFormat.RgbDxt1, new VideoPlayParams(), 320, 180);

        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["terminal"] = "no",
            ["msg-level"] = "all=no",
            ["vo"] = "libmpv",
            ["ao"] = "null",
            ["hwdec"] = "no",
            ["keep-open"] = "yes",
        };
        using var player = new MpvPlayer(options);
        MpvSource? mpv = null;
        try
        {
            // CA2000: igual que arriba — la propiedad pasa al `using (mpv)`
#pragma warning disable CA2000
            mpv = new MpvSource(gpu, player, 320, 180);
#pragma warning restore CA2000
        }
        catch (NotSupportedException)
        {
            return;
        }

        using (mpv)
        {
            hap.Start();
            player.Play(mp4);

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && !(hap.HasFrames && mpv.HasFrames))
                Thread.Sleep(20);

            Assert.True(hap.HasFrames, "la fuente HAP no publicó");
            Assert.True(mpv.HasFrames, "la fuente mpv no publicó");

            // Y las dos entregan fotograma muestreables a la vez
            Assert.True(hap.TryAcquire(double.MaxValue, out FrameLease a));
            using (a)
            {
                Assert.True(mpv.TryAcquire(double.MaxValue, out FrameLease b));
                using (b)
                {
                    Assert.NotNull(a.View);
                    Assert.NotNull(b.View);
                }
            }
        }
    }
}
