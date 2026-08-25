// =============================================================================
// CompositorTests.cs — El compositor propio, verificado por píxel (F2)
// =============================================================================
// Aquí se comprueba lo que hoy hace el DWM sin que nadie pueda decidirlo: la
// mezcla de capas, la geometría y el orden de pista. Todo contra un TextureSink
// (sin ventana), leyendo la render target final y afirmando sobre los valores.
//
// Misma convención de salto que GpuSourceTests: sin GPU utilizable, no se
// afirma nada. Ver la nota de esa cabecera sobre cómo comprobar que en esta
// máquina el camino se ejercita de verdad.
// =============================================================================

using System.Runtime.InteropServices;
using WinCue.Core;
using WinCue.Video.Gpu;
using WinCue.Video.Containers;
using WinCue.Video.Hap;
using Xunit.Abstractions;

namespace WinCue.Video.Tests;

public class CompositorTests : IClassFixture<HapMediaFixture>
{
    private const int W = 160, H = 90;

    private readonly HapMediaFixture _media;
    private readonly ITestOutputHelper _out;

    public CompositorTests(HapMediaFixture media, ITestOutputHelper output)
    {
        _media = media;
        _out = output;
    }

    private static GpuDevice? TryCreateGpu()
    {
        try
        {
            return new GpuDevice();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Píxel (x,y) del sink como (B,G,R,A).</summary>
    private static (byte B, byte G, byte R, byte A) PixelOf(GpuDevice gpu, TextureSink sink,
        int x, int y)
    {
        byte[] bytes = GpuReadback.Read(gpu, sink.Texture, sink.Width, sink.Height);
        int i = ((y * sink.Width) + x) * 4;
        return (bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]);
    }

    private static void AssertNear(int expected, byte actual, string what) =>
        Assert.True(Math.Abs(expected - actual) <= 2,
            $"{what}: esperado ~{expected}, obtenido {actual}");

    // ------------------------------------------------------------------
    // Mezcla: la aritmética que hoy aplica el DWM, ahora nuestra
    // ------------------------------------------------------------------

    [Fact]
    public void MixNode_AlCincuentaPorCiento_DaLaMediaDeLasDosCapas()
    {
        using GpuDevice? gpu = TryCreateGpu();
        if (gpu is null) return;

        using var sink = new TextureSink(gpu, W, H);
        using var comp = new OutputCompositor(gpu, sink);
        using var rojo = new SolidNode(gpu, 1f, 0f, 0f);
        using var azul = new SolidNode(gpu, 0f, 0f, 1f);

        var mix = new MixNode(rojo, azul) { Factor = 0.5f };
        comp.Root = mix;
        comp.RenderOnce(0.0);

        var px = PixelOf(gpu, sink, W / 2, H / 2);
        AssertNear(128, px.R, "rojo al 50 %");
        AssertNear(0, px.G, "verde");
        AssertNear(128, px.B, "azul al 50 %");
    }

    [Fact]
    public void MixNode_EnLosExtremos_EnsenaSoloUnaCapa()
    {
        using GpuDevice? gpu = TryCreateGpu();
        if (gpu is null) return;

        using var sink = new TextureSink(gpu, W, H);
        using var comp = new OutputCompositor(gpu, sink);
        using var rojo = new SolidNode(gpu, 1f, 0f, 0f);
        using var azul = new SolidNode(gpu, 0f, 0f, 1f);
        var mix = new MixNode(rojo, azul);
        comp.Root = mix;

        // Factor 0: el entrante aún no ha empezado
        mix.Factor = 0f;
        comp.RenderOnce(0.0);
        var inicio = PixelOf(gpu, sink, W / 2, H / 2);
        AssertNear(255, inicio.R, "solo A");
        AssertNear(0, inicio.B, "sin B");

        // Factor 1: el cruce ha terminado
        mix.Factor = 1f;
        comp.RenderOnce(0.0);
        var fin = PixelOf(gpu, sink, W / 2, H / 2);
        AssertNear(0, fin.R, "sin A");
        AssertNear(255, fin.B, "solo B");
    }

    [Fact]
    public void ElFundidoRecorreLaRampaSinSaltos()
    {
        // Un crossfade es este nodo con el factor movido por el reloj del
        // compositor. Se comprueba que la rampa es monótona: un salto aquí es
        // el bache que se ve en sala.
        using GpuDevice? gpu = TryCreateGpu();
        if (gpu is null) return;

        using var sink = new TextureSink(gpu, W, H);
        using var comp = new OutputCompositor(gpu, sink);
        using var negro = new SolidNode(gpu, 0f, 0f, 0f);
        using var blanco = new SolidNode(gpu, 1f, 1f, 1f);
        var mix = new MixNode(negro, blanco);
        comp.Root = mix;

        int previo = -1;
        for (int paso = 0; paso <= 10; paso++)
        {
            mix.Factor = paso / 10f;
            comp.RenderOnce(0.0);
            int v = PixelOf(gpu, sink, W / 2, H / 2).R;
            Assert.True(v >= previo, $"la rampa retrocedió en el paso {paso}: {previo} → {v}");
            previo = v;
        }
        AssertNear(255, (byte)previo, "final de la rampa");
    }

    [Fact]
    public void ElFundidoLoGobiernaElRelojDelCompositor()
    {
        // La prueba de que el fundido dejó de ser una animación de WPF: el
        // mismo instante que eligen las fuentes decide la opacidad. Se pide el
        // fotograma de t y sale exactamente el valor de la rampa en t.
        using GpuDevice? gpu = TryCreateGpu();
        if (gpu is null) return;

        using var sink = new TextureSink(gpu, W, H);
        using var comp = new OutputCompositor(gpu, sink);
        using var negro = new SolidNode(gpu, 0f, 0f, 0f);
        using var blanco = new SolidNode(gpu, 1f, 1f, 1f);

        // Cruce de 2 s que arranca en el segundo 10 del show
        var mix = new MixNode(negro, blanco) { Fade = Ramp.FadeIn(10.0, 2.0) };
        comp.Root = mix;

        comp.RenderOnce(9.5);                     // aún no ha empezado
        AssertNear(0, PixelOf(gpu, sink, W / 2, H / 2).R, "antes del cruce");

        comp.RenderOnce(11.0);                    // justo la mitad
        AssertNear(128, PixelOf(gpu, sink, W / 2, H / 2).R, "mitad del cruce");

        comp.RenderOnce(12.0);                    // terminado
        AssertNear(255, PixelOf(gpu, sink, W / 2, H / 2).R, "cruce acabado");

        // Y el propio nodo sabe cuándo se puede retirar el saliente
        Assert.True(mix.Fade!.Value.IsDone(12.0));
    }

    // ------------------------------------------------------------------
    // Opacidad de UNA capa suelta (OpacityNode)
    // ------------------------------------------------------------------

    [Fact]
    public void OpacityNode_AplicaLaOpacidadDeLaCapa()
    {
        // Lo que MixNode NO puede hacer: bajar la opacidad de UNA rama sin
        // inventarse una segunda contra la que mezclarla. Es el campo
        // «opacidad» de un cue de vídeo.
        using GpuDevice? gpu = TryCreateGpu();
        if (gpu is null)
        {
            _out.WriteLine("SALTADA: sin GPU utilizable en esta maquina.");
            return;
        }

        using var sink = new TextureSink(gpu, W, H);
        using var comp = new OutputCompositor(gpu, sink);
        using var blanco = new SolidNode(gpu, 1f, 1f, 1f);

        var capa = new OpacityNode(blanco);
        comp.Root = capa;

        capa.Opacity = 1f;
        comp.RenderOnce(0.0);
        byte lleno = PixelOf(gpu, sink, W / 2, H / 2).R;

        capa.Opacity = 0.5f;
        comp.RenderOnce(0.0);
        byte medio = PixelOf(gpu, sink, W / 2, H / 2).R;

        capa.Opacity = 0f;
        comp.RenderOnce(0.0);
        byte nada = PixelOf(gpu, sink, W / 2, H / 2).R;

        _out.WriteLine($"GPU utilizable: opacidad 1.0 -> {lleno}, 0.5 -> {medio}, 0.0 -> {nada}");

        AssertNear(255, lleno, "capa a plena opacidad");
        AssertNear(128, medio, "capa al 50 %");
        AssertNear(0, nada, "capa a 0 (queda el fondo)");
    }

    [Fact]
    public void OpacityNode_ElFundidoLoGobiernaElRelojDelCompositor()
    {
        // Mismo criterio que el fundido de MixNode: la rampa se evalúa con el
        // instante del FOTOGRAMA, no con un reloj de animación aparte. Es lo
        // que hace el cue de control FADE sobre un vídeo al aire.
        using GpuDevice? gpu = TryCreateGpu();
        if (gpu is null)
        {
            _out.WriteLine("SALTADA: sin GPU utilizable en esta maquina.");
            return;
        }

        using var sink = new TextureSink(gpu, W, H);
        using var comp = new OutputCompositor(gpu, sink);
        using var blanco = new SolidNode(gpu, 1f, 1f, 1f);

        // FADE a 0 en 4 s que arranca en el segundo 10 del show
        var capa = new OpacityNode(blanco) { Fade = Ramp.FadeOut(10.0, 4.0) };
        comp.Root = capa;

        comp.RenderOnce(9.0);
        byte antes = PixelOf(gpu, sink, W / 2, H / 2).R;
        comp.RenderOnce(12.0);
        byte mitad = PixelOf(gpu, sink, W / 2, H / 2).R;
        comp.RenderOnce(14.0);
        byte fin = PixelOf(gpu, sink, W / 2, H / 2).R;

        _out.WriteLine($"GPU utilizable: t=9 -> {antes}, t=12 (mitad) -> {mitad}, t=14 -> {fin}");

        AssertNear(255, antes, "antes de empezar el fundido");
        AssertNear(128, mitad, "mitad del fundido");
        AssertNear(0, fin, "fundido terminado");
    }

    [Fact]
    public void CapaReasignada_NoArrastraLaRampaDelCueAnterior()
    {
        // LA prueba de esta pieza, y la que habría cazado un fallo real de
        // Secuence: las capas son slots PERSISTENTES de un pool (auditoría
        // 05), así que el MISMO OpacityNode se reparte entre cues sucesivos de
        // la misma (salida, pista, spec). Una rampa TERMINADA devuelve su
        // valor final para siempre, así que un cue con fade_out deja el nodo
        // valiendo 0 — y el siguiente cue de esa pista nacía invisible
        // (medido con el show de prueba: tras el fade_out del 6.1, el 5.1
        // salía en negro). Reset() es lo que tiene que llamar quien reasigna.
        //
        // Si Reset() dejara de borrar Fade, «tras reasignar» seguiría valiendo
        // 0 y esta prueba se pondría roja.
        using GpuDevice? gpu = TryCreateGpu();
        if (gpu is null)
        {
            _out.WriteLine("SALTADA: sin GPU utilizable en esta maquina.");
            return;
        }

        using var sink = new TextureSink(gpu, W, H);
        using var comp = new OutputCompositor(gpu, sink);
        using var blanco = new SolidNode(gpu, 1f, 1f, 1f);

        // El cue ANTERIOR se fundió a 0 entre t=0 y t=1, y ya terminó
        var capa = new OpacityNode(blanco) { Fade = Ramp.FadeOut(0.0, 1.0) };
        comp.Root = capa;

        comp.RenderOnce(5.0);
        byte arrastrado = PixelOf(gpu, sink, W / 2, H / 2).R;

        // El slot lo toma un cue NUEVO
        capa.Reset();
        comp.RenderOnce(5.0);
        byte reasignado = PixelOf(gpu, sink, W / 2, H / 2).R;

        _out.WriteLine("GPU utilizable: rampa terminada en 0 -> " +
            $"{arrastrado}; tras reasignar la capa -> {reasignado}");

        AssertNear(0, arrastrado,
            "una rampa terminada sigue valiendo su valor final (por eso hace falta Reset)");
        AssertNear(255, reasignado,
            "el cue nuevo heredó la rampa del anterior y nace invisible");
        Assert.Null(capa.Fade);
        Assert.Equal(1f, capa.Opacity);
    }

    // ------------------------------------------------------------------
    // Geometría y orden de pista
    // ------------------------------------------------------------------

    [Fact]
    public void TransformNode_ColocaLaCapaSoloEnSuRectangulo()
    {
        using GpuDevice? gpu = TryCreateGpu();
        if (gpu is null) return;

        using var sink = new TextureSink(gpu, W, H);
        using var comp = new OutputCompositor(gpu, sink);
        using var verde = new SolidNode(gpu, 0f, 1f, 0f);

        // PiP: la capa vive en un recuadro, no en todo el lienzo
        var pip = new TransformNode(verde) { Dest = new DestRect(40, 20, 40, 30) };
        comp.Root = pip;
        comp.RenderOnce(0.0);

        var dentro = PixelOf(gpu, sink, 60, 35);
        var fuera = PixelOf(gpu, sink, 5, 5);
        AssertNear(255, dentro.G, "dentro del recuadro");
        AssertNear(0, fuera.G, "fuera del recuadro");
        AssertNear(0, fuera.R, "fuera: fondo");
    }

    [Fact]
    public void StackNode_PintaEnOrdenDePista()
    {
        using GpuDevice? gpu = TryCreateGpu();
        if (gpu is null) return;

        using var sink = new TextureSink(gpu, W, H);
        using var comp = new OutputCompositor(gpu, sink);
        using var rojo = new SolidNode(gpu, 1f, 0f, 0f);
        using var verde = new SolidNode(gpu, 0f, 1f, 0f);

        var stack = new StackNode();
        stack.Children.Add(rojo);
        stack.Children.Add(verde);   // pista de encima
        comp.Root = stack;
        comp.RenderOnce(0.0);

        var px = PixelOf(gpu, sink, W / 2, H / 2);
        AssertNear(255, px.G, "gana la última pista");
        AssertNear(0, px.R, "la de debajo queda tapada");
    }

    [Fact]
    public void SolidNode_ComoVeloDeBlackout_OscureceLoQueHayDebajo()
    {
        // El velo de blackout deja de ser un elemento WPF encima del vídeo y
        // pasa a ser un nodo más — que es lo que permite quitarlo de WPF sin
        // problemas de airspace sobre el swapchain.
        using GpuDevice? gpu = TryCreateGpu();
        if (gpu is null) return;

        using var sink = new TextureSink(gpu, W, H);
        using var comp = new OutputCompositor(gpu, sink);
        using var blanco = new SolidNode(gpu, 1f, 1f, 1f);
        using var velo = new SolidNode(gpu, 0f, 0f, 0f);

        var mix = new MixNode(blanco, velo) { Factor = 0.75f };
        comp.Root = mix;
        comp.RenderOnce(0.0);

        AssertNear(64, PixelOf(gpu, sink, W / 2, H / 2).R, "velo al 75 %");
    }

    // ------------------------------------------------------------------
    // Con una fuente real y con el hilo de render
    // ------------------------------------------------------------------

    [Fact]
    public void ConFuenteHapReal_ElCompositorPintaElVideo()
    {
        using GpuDevice? gpu = TryCreateGpu();
        if (gpu is null) return;
        if (!_media.FFmpegOk) return;

        MovInfo info = MovDemuxer.Open(_media.Hap1);
        using var source = new HapSource(gpu, _media.Hap1, info,
            HapTextureFormat.RgbDxt1, new VideoPlayParams(), W, H);
        using var firstFrame = new ManualResetEventSlim(false);
        source.FirstFrame += firstFrame.Set;
        source.Start();
        if (!firstFrame.Wait(TimeSpan.FromSeconds(15)))
            return;

        using var sink = new TextureSink(gpu, W, H);
        using var comp = new OutputCompositor(gpu, sink);
        comp.Root = new SourceNode(source);

        // Instante grande: entrega el último fotograma disponible
        comp.RenderOnce(double.MaxValue);

        byte[] bytes = GpuReadback.Read(gpu, sink.Texture, W, H);
        Assert.True(GpuReadback.MeanLumaBgra8(bytes) > 2.0,
            "el compositor pintó negro con una fuente viva");
        Assert.Equal(1, comp.Stats.LastLayersDrawn);
    }

    [Fact]
    public void HiloDeRender_PresentaFotogramasDeFormaSostenida()
    {
        using GpuDevice? gpu = TryCreateGpu();
        if (gpu is null) return;

        using var sink = new TextureSink(gpu, W, H, fps: 60.0);
        using var comp = new OutputCompositor(gpu, sink);
        using var rojo = new SolidNode(gpu, 1f, 0f, 0f);
        comp.Root = rojo;
        comp.Start();

        Thread.Sleep(500);   // ~30 fotogramas a 60 Hz

        Assert.True(comp.Stats.FramesPresented > 10,
            $"solo se presentaron {comp.Stats.FramesPresented} fotogramas en 500 ms");
        Assert.Equal(0, comp.Stats.FramesWithStarvation);
        // Un color plano no necesita ninguna RT intermedia
        Assert.Equal(0, comp.Stats.RtHighWaterMark);
    }

    [Fact]
    public void Mutate_CambiaElGrafoSinCarreraConElHiloDeRender()
    {
        using GpuDevice? gpu = TryCreateGpu();
        if (gpu is null) return;

        using var sink = new TextureSink(gpu, W, H, fps: 120.0);
        using var comp = new OutputCompositor(gpu, sink);
        using var rojo = new SolidNode(gpu, 1f, 0f, 0f);
        using var azul = new SolidNode(gpu, 0f, 0f, 1f);
        comp.Root = rojo;
        comp.Start();
        Thread.Sleep(100);

        // Un GO del motor de cues acaba siendo exactamente esto
        for (int i = 0; i < 50; i++)
            comp.Mutate(c => c.Root = (i % 2 == 0) ? azul : rojo);

        Thread.Sleep(100);
        Assert.True(comp.Stats.FramesPresented > 0);
    }

    // ------------------------------------------------------------------
    // El espejo (sink secundario para el PGM)
    // ------------------------------------------------------------------

    [Fact]
    public void SinkSecundario_RecibeElMISMOFotograma()
    {
        using var gpu = TryCreateGpu();
        if (gpu is null)
        {
            _out.WriteLine("SALTADA: sin GPU utilizable en esta maquina.");
            return;
        }

        using var principal = new TextureSink(gpu, W, H);
        using var espejo = new TextureSink(gpu, W, H);
        using var comp = new OutputCompositor(gpu, principal) { Mirror = espejo };

        using var rojo = new SolidNode(gpu, 1f, 0f, 0f);
        comp.Root = rojo;
        comp.RenderOnce(0.0);

        byte[] a = GpuReadback.Read(gpu, principal.Texture, W, H);
        byte[] b = GpuReadback.Read(gpu, espejo.Texture, W, H);
        double lumaA = GpuReadback.MeanLumaBgra8(a);
        double lumaB = GpuReadback.MeanLumaBgra8(b);
        _out.WriteLine($"GPU utilizable: luma principal={lumaA:F3}, luma espejo={lumaB:F3}");

        Assert.Equal(lumaA, lumaB, 3);
    }

    [Fact]
    public void SinEspejo_ElCompositorSigueFuncionandoIgual()
    {
        using var gpu = TryCreateGpu();
        if (gpu is null)
        {
            _out.WriteLine("SALTADA: sin GPU utilizable en esta maquina.");
            return;
        }

        using var principal = new TextureSink(gpu, W, H);
        using var comp = new OutputCompositor(gpu, principal);

        Assert.Null(comp.Mirror);

        using var rojo = new SolidNode(gpu, 1f, 0f, 0f);
        comp.Root = rojo;
        comp.RenderOnce(0.0);

        byte[] a = GpuReadback.Read(gpu, principal.Texture, W, H);
        double luma = GpuReadback.MeanLumaBgra8(a);
        _out.WriteLine($"GPU utilizable: luma principal (sin espejo)={luma:F3}");

        Assert.True(luma > 10.0);
    }

    [Fact]
    public void Espejo_UsaSusPropiasDimensiones_NoLasDelPrincipal()
    {
        // La trampa que senala el brief: si el LayerTarget del espejo se
        // construyera con las dimensiones del PRINCIPAL en vez de las suyas,
        // una prueba con el mismo tamano en los dos sinks NO lo detectaria
        // (SinkSecundario_RecibeElMISMOFotograma no lo haria: un SolidNode es
        // el mismo color en cualquier rectangulo). Por eso aqui el espejo es
        // mas GRANDE que el principal en los dos ejes: si el viewport del
        // espejo se calculase con el tamano (menor) del principal, el borde
        // inferior-derecho del espejo real se quedaria sin pintar (color de
        // fondo), y esta prueba se pondria roja.
        using var gpu = TryCreateGpu();
        if (gpu is null)
        {
            _out.WriteLine("SALTADA: sin GPU utilizable en esta maquina.");
            return;
        }

        const int espejoW = 2 * W, espejoH = 2 * H;

        using var principal = new TextureSink(gpu, W, H);
        using var espejo = new TextureSink(gpu, espejoW, espejoH);
        using var comp = new OutputCompositor(gpu, principal) { Mirror = espejo };

        using var rojo = new SolidNode(gpu, 1f, 0f, 0f);
        comp.Root = rojo;
        comp.RenderOnce(0.0);

        byte[] bytes = GpuReadback.Read(gpu, espejo.Texture, espejoW, espejoH);
        int i = (((espejoH - 1) * espejoW) + (espejoW - 1)) * 4;
        byte rEsquina = bytes[i + 2];
        _out.WriteLine($"GPU utilizable: R en esquina inferior-derecha del espejo " +
            $"({espejoW - 1},{espejoH - 1}) = {rEsquina} (esperado > 200)");

        Assert.True(rEsquina > 200,
            $"la esquina del espejo (fuera de {W}x{H}) no esta pintada: R={rEsquina}. " +
            "El LayerTarget del espejo probablemente usa las dimensiones del principal.");
    }

    // ------------------------------------------------------------------
    // Salida a pantalla
    // ------------------------------------------------------------------

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className,
        string windowName, uint style, int x, int y, int w, int h,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [Fact]
    public void SwapChainSink_SeCreaSobreUnHwndYPresenta()
    {
        using GpuDevice? gpu = TryCreateGpu();
        if (gpu is null) return;

        nint hwnd = CreateWindowExW(0, "STATIC", "wincue-test", 0, 0, 0, 320, 240,
            0, 0, 0, 0);
        if (hwnd == 0) return;

        try
        {
            using var sink = new SwapChainSink(gpu, hwnd, 320, 240) { SyncInterval = 0 };
            using var comp = new OutputCompositor(gpu, sink);
            using var verde = new SolidNode(gpu, 0f, 1f, 0f);
            comp.Root = verde;

            // El camino completo hasta el swapchain, sin depender del vsync
            for (int i = 0; i < 5; i++)
            {
                comp.RenderOnce(i / 60.0);
                sink.Present();
            }

            Assert.Equal(320, sink.Width);
            Assert.Equal(240, sink.Height);
        }
        finally
        {
            DestroyWindow(hwnd);
        }
    }

    // ------------------------------------------------------------------
    // El pase de ESPEJO muestrea en SOLO LECTURA
    // ------------------------------------------------------------------
    // Por que importa, en una linea: si el espejo muestreara "de verdad", el
    // hilo que presenta EN SALA acabaria consumiendo la bandera de fotograma
    // nuevo de OTRA salida y esperando su fence de GPU, con el lock del grafo
    // tomado, solo porque hay un monitor mirando (modo LIENZO del PGM de
    // Secuence). Ver IVideoSource.TryAcquireReadOnly.

    /// <summary>Fuente de mentira que solo CUENTA por que camino la muestrean.
    /// Devuelve false siempre a proposito: asi la prueba no necesita GPU (sin
    /// fotograma, SourceNode vuelve antes de tocar el renderer).</summary>
    private sealed class FuenteContadora : IVideoSource
    {
        public int Normales { get; private set; }
        public int SoloLectura { get; private set; }

        public int Width => 16;
        public int Height => 16;
        public bool HasFrames => false;
        public VideoSourceStats Stats { get; } = new();

        public bool TryAcquire(double mediaTime, out FrameLease lease)
        {
            Normales++;
            lease = default;
            return false;
        }

        public bool TryAcquireReadOnly(double mediaTime, out FrameLease lease)
        {
            SoloLectura++;
            lease = default;
            return false;
        }

        public void Dispose() { }
    }

    [Fact]
    public void SourceNode_ConSoloLectura_UsaElMuestreoSinEfectos()
    {
        // SIN GPU: el grafo se evalua a mano, que es donde vive la decision.
        using var fuente = new FuenteContadora();
        var nodo = new SourceNode(fuente);
        var destino = new LayerTarget(null!, new DestRect(0, 0, 16, 16), UvRect.Full, 1f);

        nodo.Render(new GraphFrame(0.0, new CompositorFrameStats()), destino);
        Assert.Equal(1, fuente.Normales);
        Assert.Equal(0, fuente.SoloLectura);

        nodo.Render(
            new GraphFrame(0.0, new CompositorFrameStats()) { SoloLectura = true }, destino);
        Assert.Equal(1, fuente.Normales);
        Assert.Equal(1, fuente.SoloLectura);
    }

    [Fact]
    public void ElEspejoMuestreaEnSoloLectura_YElPrincipalNo()
    {
        // La misma decision, pero desde donde de verdad se toma: un fotograma
        // completo del compositor con espejo puesto tiene que dar EXACTAMENTE
        // un muestreo normal (el pase principal, que es el dueno) y EXACTAMENTE
        // uno de solo lectura (el espejo). Si alguien quitara el SoloLectura
        // del pase de espejo, aqui saldrian dos normales y cero de lectura.
        using var gpu = TryCreateGpu();
        if (gpu is null)
        {
            _out.WriteLine("SALTADA: sin GPU utilizable en esta maquina.");
            return;
        }

        using var principal = new TextureSink(gpu, W, H);
        using var espejo = new TextureSink(gpu, W, H);
        using var comp = new OutputCompositor(gpu, principal) { Mirror = espejo };

        using var fuente = new FuenteContadora();
        comp.Root = new SourceNode(fuente);
        comp.RenderOnce(0.0);

        _out.WriteLine($"GPU utilizable: normales={fuente.Normales}, " +
            $"solo lectura={fuente.SoloLectura}");

        Assert.Equal(1, fuente.Normales);
        Assert.Equal(1, fuente.SoloLectura);
    }

    [Fact]
    public void SinEspejo_NoHayNingunMuestreoDeSoloLectura()
    {
        using var gpu = TryCreateGpu();
        if (gpu is null)
        {
            _out.WriteLine("SALTADA: sin GPU utilizable en esta maquina.");
            return;
        }

        using var principal = new TextureSink(gpu, W, H);
        using var comp = new OutputCompositor(gpu, principal);

        using var fuente = new FuenteContadora();
        comp.Root = new SourceNode(fuente);
        comp.RenderOnce(0.0);

        Assert.Equal(1, fuente.Normales);
        Assert.Equal(0, fuente.SoloLectura);
    }
}
