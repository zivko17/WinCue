// =============================================================================
// SharedSurfaceSourceTests.cs — El puente D3D9Ex -> D3D11, por pixel
// =============================================================================
// Esta es LA prueba que decide si el camino existe: una superficie compartida
// de D3D9Ex abierta en D3D11 y dibujada por el compositor. Si esto pasa, el
// modo canvas de Secuence es fontaneria; si no, no hay diseno.
//
// Misma convencion que CompositorTests y GpuSourceTests: sin GPU utilizable no
// se afirma nada, en vez de mentir.
// =============================================================================

using WinCue.Video.Gpu;

namespace WinCue.Video.Tests;

public class SharedSurfaceSourceTests
{
    private static GpuDevice? TryCreateGpu()
    {
        try { return new GpuDevice(); }
        catch (Exception) { return null; }
    }

    [Fact]
    public void SuperficieCompartidaD3D9_SeAbreEnD3D11_YSeDibuja()
    {
        using var gpu = TryCreateGpu();
        if (gpu is null)
            return;   // sin GPU utilizable: no se afirma nada

        nint d3d9 = 0;
        try { d3d9 = D3D9Minimal.CreateDevice(0); }
        catch (Exception) { return; }   // sin D3D9Ex tampoco se afirma

        try
        {
            var (surface, shared) = D3D9Minimal.CreateSharedRenderTarget(d3d9, 64, 64);
            try
            {
                // Pintar la superficie D3D9 de un color conocido: azul puro.
                D3D9Minimal.ColorFill(d3d9, surface, 0x000000FF);

                // Aqui la prueba hace de PRODUCTOR (es quien acaba de escribir
                // en la superficie D3D9), asi que es quien tiene que fencear
                // antes de avisar — igual que le tocaria a MpvGpuRenderer en
                // produccion. Sin esto, la lectura por D3D11 de mas abajo
                // puede correr antes de que la GPU haya terminado el ColorFill
                // encolado en la cola D3D9 (colas de comandos distintas sobre
                // la misma GPU): no es una tirita para que pase la prueba, es
                // la prueba modelando el contrato real (ver la cabecera de
                // SharedSurfaceSource.cs).
                D3D9Minimal.FlushAndWait(d3d9);

                using var source = new SharedSurfaceSource(gpu, shared, 64, 64);
                source.MarkFrameReady();

                using var sink = new TextureSink(gpu, 64, 64);
                using var comp = new OutputCompositor(gpu, sink);
                comp.Root = new SourceNode(source);
                comp.RenderOnce(0.0);

                byte[] bgra = GpuReadback.Read(gpu, sink.Texture, 64, 64);

                // BGRA8: el pixel central tiene que ser azul.
                int mid = ((32 * 64) + 32) * 4;
                Assert.True(bgra[mid + 0] > 200, $"B={bgra[mid + 0]}");
                Assert.True(bgra[mid + 1] < 60, $"G={bgra[mid + 1]}");
                Assert.True(bgra[mid + 2] < 60, $"R={bgra[mid + 2]}");
            }
            finally
            {
                D3D9Minimal.Release(surface);
            }
        }
        finally
        {
            D3D9Minimal.Release(d3d9);
        }
    }

    [Fact]
    public void SinFotogramaPublicado_NoEntregaPrestamo()
    {
        using var gpu = TryCreateGpu();
        if (gpu is null)
            return;

        nint d3d9 = 0;
        try { d3d9 = D3D9Minimal.CreateDevice(0); }
        catch (Exception) { return; }

        try
        {
            var (surface, shared) = D3D9Minimal.CreateSharedRenderTarget(d3d9, 32, 32);
            try
            {
                using var source = new SharedSurfaceSource(gpu, shared, 32, 32);

                Assert.False(source.HasFrames);
                Assert.False(source.TryAcquire(0.0, out FrameLease lease));
                lease.Dispose();
            }
            finally { D3D9Minimal.Release(surface); }
        }
        finally { D3D9Minimal.Release(d3d9); }
    }

    [Fact]
    public void CapaReasignada_NoEnsenaElUltimoFotogramaDelCueAnterior()
    {
        // La superficie es AJENA y PERSISTENTE (un slot del pool que se
        // reparte entre cues sucesivos): cuando un cue nuevo la toma, todavia
        // contiene el ULTIMO FOTOGRAMA DEL ANTERIOR, y _hasFrame no baja solo.
        // Como el compositor pinta en su propio hilo —sin esperar a que el de
        // interfaz ceda, que es lo que tapaba esto en WPF—, entre tomar la
        // capa y recibir la opacidad del cue nuevo puede presentar esa imagen
        // vieja a plena opacidad: un destello del cue anterior, visible en
        // sala. ResetFrames() es lo que tiene que llamar quien reasigna.
        //
        // Si ResetFrames() dejara de bajar la bandera, la segunda afirmacion
        // (TryAcquire devuelve false) se pondria roja.
        using var gpu = TryCreateGpu();
        if (gpu is null)
            return;

        nint d3d9 = 0;
        try { d3d9 = D3D9Minimal.CreateDevice(0); }
        catch (Exception) { return; }

        try
        {
            var (surface, shared) = D3D9Minimal.CreateSharedRenderTarget(d3d9, 32, 32);
            try
            {
                using var source = new SharedSurfaceSource(gpu, shared, 32, 32);

                // El cue ANTERIOR dejo su fotograma en la superficie
                source.MarkFrameReady();
                Assert.True(source.HasFrames);
                Assert.True(source.TryAcquire(0.0, out FrameLease viejo));
                viejo.Dispose();

                // El slot lo toma un cue NUEVO: lo de ahi dentro ya no vale
                source.ResetFrames();
                Assert.False(source.HasFrames);
                Assert.False(source.TryAcquire(0.0, out FrameLease durante));
                durante.Dispose();

                // ...hasta que mpv entrega el primero del cue nuevo
                source.MarkFrameReady();
                Assert.True(source.TryAcquire(0.0, out FrameLease nuevo));
                nuevo.Dispose();
            }
            finally { D3D9Minimal.Release(surface); }
        }
        finally { D3D9Minimal.Release(d3d9); }
    }

    [Fact]
    public void ElCopiador_LoLlamaElCONSUMIDOR_NoElProductor()
    {
        using var gpu = TryCreateGpu();
        if (gpu is null)
            return;

        nint d3d9 = 0;
        try { d3d9 = D3D9Minimal.CreateDevice(0); }
        catch (Exception) { return; }

        try
        {
            var (surface, shared) = D3D9Minimal.CreateSharedRenderTarget(d3d9, 32, 32);
            try
            {
                int copias = 0;
                using var source = new SharedSurfaceSource(gpu, shared, 32, 32)
                {
                    FrameCopier = () => copias++,
                };

                source.MarkFrameReady();
                Assert.Equal(0, copias);   // publicar NO copia

                Assert.True(source.TryAcquire(0.0, out FrameLease lease));
                lease.Dispose();
                Assert.Equal(1, copias);   // muestrear SI copia
            }
            finally { D3D9Minimal.Release(surface); }
        }
        finally { D3D9Minimal.Release(d3d9); }
    }
}
