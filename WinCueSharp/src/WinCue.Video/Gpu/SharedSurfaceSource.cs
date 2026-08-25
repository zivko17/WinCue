// =============================================================================
// SharedSurfaceSource.cs — El puente: superficie compartida D3D9Ex -> D3D11
// =============================================================================
// Entra por donde YA sale la imagen. MpvGpuRenderer crea una superficie
// compartida D3D9Ex por recorte y copia en ella el fotograma completo
// (StretchRect desde el FBO privado); esta clase abre ESA superficie como
// textura D3D11 con OpenSharedResource y la publica al grafo. Ni mpv ni el
// interop GL se enteran.
//
// POR QUE NO HAY ANILLO NI PTS: el recurso es UNO, ajeno, y lo escribe el
// productor de siempre. Esta fuente entrega "el ultimo fotograma copiado", que
// es exactamente lo que hoy hace el DWM con el D3DImage. El PTS de verdad
// llega con MpvSource (la ruta nativa), no con el puente.
//
// HILOS — el punto que puede romperse:
//   · MarkFrameReady() lo llama el hilo de render de mpv. SOLO levanta una
//     bandera. Nada lo bloquea (regla 4 del plan del compositor).
//   · TryAcquire() lo llama el hilo del compositor, y es el UNICO que invoca
//     FrameCopier — el StretchRect interop->presentacion. Un solo hilo copia y
//     muestrea: eso es lo que garantizaba el Lock del D3DImage en WPF.
//
// LO QUE ESTA CLASE *NO* HACE — Y ES A PROPOSITO: abrir una superficie D3D9Ex
// compartida con OpenSharedResource NO trae sincronizacion implicita entre
// devices. IDirect3DDevice9Ex y ID3D11Device son colas de comandos DISTINTAS
// sobre la MISMA GPU: que StretchRect/ColorFill devuelvan S_OK en el lado
// D3D9 solo dice que el comando se ENCOLO, no que la GPU ya lo ejecuto. Medido
// en el desarrollo de este puente (RTX 5070 Ti): leer por D3D11 justo despues
// de escribir por D3D9 falla de forma NO determinista incluso con un
// Thread.Sleep(100) de por medio — asi que no es "cuestion de darle tiempo",
// es la ausencia de una barrera real entre las dos colas.
//
// Por eso el fence NO vive aqui. SharedSurfaceSource es un CONSUMIDOR: solo
// tiene el handle compartido, nunca el device D3D9 que escribio en el, asi
// que no podria montar la barrera aunque quisiera. Y MarkFrameReady() —el
// unico sitio del lado del productor que toca esta clase— tiene prohibido
// bloquear (ver arriba). La responsabilidad de hacer visible la escritura es
// de QUIEN ESCRIBE: debe llamar a D3D9Minimal.FlushAndWait(device) sobre su
// propio device D3D9 ANTES de avisar (MarkFrameReady o equivalente), nunca
// despues. Sin ese flush del productor, "el ultimo fotograma copiado" puede
// no ser el fotograma que TryAcquire() acaba de anunciar como listo.
// =============================================================================

using Vortice.Direct3D11;

namespace WinCue.Video.Gpu;

public sealed class SharedSurfaceSource : IVideoSource
{
    private readonly ID3D11Texture2D _texture;
    private readonly ID3D11ShaderResourceView _srv;
    private volatile bool _hasFrame;

    // 0/1 en vez de bool: MarkFrameReady() lo ESCRIBE (hilo de render de
    // mpv) y TryAcquire() lo LEE-Y-LIMPIA de forma atomica (Interlocked, ver
    // abajo) para no perder un aviso concurrente ni copiar dos veces el
    // mismo fotograma. Distingue "hay fotograma NUEVO" (dispara FrameCopier)
    // de "hay algo que enseñar" (_hasFrame, una vez visto el primero, nunca
    // baja): antes ambas preguntas eran la misma bandera, y FrameCopier se
    // invocaba en CADA fotograma compuesto y por capa aunque mpv no hubiera
    // producido nada nuevo.
    private int _frameIsNew;

    // volatile por la misma razon que TextureSink._disposed (OutputSinks.cs):
    // se lee en TryAcquire (hilo del compositor) y se escribe en Dispose
    // (puede ser otro hilo). Sin volatile, un lector en otro nucleo podria no
    // ver NUNCA la escritura y usar un SRV/textura ya liberados.
    private volatile bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public bool HasFrames => _hasFrame;
    public VideoSourceStats Stats { get; } = new();

    /// <summary>Copia interop -> presentacion. La invoca el CONSUMIDOR (el
    /// hilo del compositor), nunca el productor. Ver la cabecera.</summary>
    public Action? FrameCopier { get; set; }

    public SharedSurfaceSource(GpuDevice gpu, nint sharedHandle, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        if (sharedHandle == 0)
            throw new ArgumentException("Handle compartido no valido.", nameof(sharedHandle));

        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        // El handle es el de una superficie D3D9Ex creada como compartida:
        // D3DFMT_A8R8G8B8 se ve desde D3D11 como B8G8R8A8_UNORM.
        _texture = gpu.Device.OpenSharedResource<ID3D11Texture2D>(sharedHandle);
        try
        {
            _srv = gpu.Device.CreateShaderResourceView(_texture);
        }
        catch
        {
            _texture.Dispose();
            throw;
        }
    }

    /// <summary>Hay un fotograma nuevo esperando en la superficie del interop.
    /// HILO DE RENDER DE MPV: solo levanta banderas (una asignacion y un
    /// Interlocked.Exchange sin bloqueo) — nunca invoca FrameCopier ni espera
    /// nada.</summary>
    public void MarkFrameReady()
    {
        _hasFrame = true;
        Interlocked.Exchange(ref _frameIsNew, 1);
    }

    public bool TryAcquire(double mediaTime, out FrameLease lease)
    {
        if (_disposed || !_hasFrame)
        {
            Stats.Starvations++;
            lease = default;
            return false;
        }

        // Dos preguntas DISTINTAS, a proposito:
        //   ¿hay fotograma NUEVO? -> Interlocked.Exchange consume la bandera
        //   de un tiron (evita perder un MarkFrameReady concurrente y evita
        //   copiar el mismo fotograma dos veces). Solo entonces se invoca
        //   FrameCopier — el StretchRect real, que no es gratis.
        //   ¿hay algo que ENSEÑAR? -> _hasFrame, ya comprobado arriba: en
        //   cuanto llego el primer fotograma, siempre hay algo que devolver.
        //   Si NO se consumiera asi, un video a 30p sobre una salida a 60 Hz
        //   perderia la mitad de los fotogramas (TryAcquire devolveria false
        //   en los intermedios) en vez de repetir el ultimo, que es lo que
        //   hace cualquier otra fuente del motor.
        bool isNew = Interlocked.Exchange(ref _frameIsNew, 0) != 0;
        if (isNew)
        {
            // La copia va AQUI, en el hilo que muestrea, justo antes de
            // dibujar — nunca en MarkFrameReady (ver cabecera del fichero).
            FrameCopier?.Invoke();
            Stats.Published++;
        }
        else
        {
            Stats.Repeats++;
        }

        lease = new FrameLease(_srv, _texture, mediaTime);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _srv.Dispose();
        _texture.Dispose();
    }
}
