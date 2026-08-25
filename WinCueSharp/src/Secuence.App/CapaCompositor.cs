// =============================================================================
// CapaCompositor.cs — Una capa del grafo, vista como IVideoLayerSurface
// =============================================================================
// Lo que VideoEngine cree que le entrega la interfaz: una superficie donde
// pintar una pista. En WPF eso es un D3DImage (CompositorLayerControl); aqui
// no hay D3DImage, asi que es un NODO del grafo de su salida.
//
// HILOS — leelo antes de tocar nada:
//   · SetSurface / SetSharedHandle / SetFrameCopier / AssignCue / ReleaseCue /
//     ClearSurface: hilo principal.
//   · InvalidateFrame: HILO DE RENDER DE MPV. Solo levanta la bandera de la
//     fuente. Nada mas. Nada bloqueante.
//   · El copiador NO se llama aqui: lo llama el compositor al muestrear, en su
//     hilo. Un solo hilo copia y muestrea — eso es lo que garantizaba el Lock
//     del D3DImage en WPF.
//
// FENCE ENTRE DEVICES: esta clase declara NeedsCrossDeviceFence = true, y por
// eso VideoEngine le entrega el copiador FENCEADO (CopyPresentFenced) en vez
// del de siempre. Una superficie compartida D3D9Ex abierta en D3D11 no trae
// sincronizacion implicita entre devices: sin el fence, D3D11 lee negro de
// forma no determinista (demostrado en la Tarea 4 — quitando el fence, la
// prueba por pixel vuelve a rojo). WinCue, que hospeda la MISMA superficie en
// su propio device D3D9Ex, devuelve false y sigue sin fence, que es lo suyo.
//
// LO QUE ESE FENCE CUESTA, ANOTADO A PROPOSITO: el copiador lo invoca el
// compositor desde TryAcquire, o sea DENTRO de OutputCompositor._graphLock, y
// CopyPresentFenced BLOQUEA hasta que la GPU termina lo encolado en el device
// D3D9Ex. Es decir: el hilo de render se para ahi, con el lock del grafo
// tomado, una vez por capa y por fotograma NUEVO (no por fotograma compuesto:
// SharedSurfaceSource solo llama al copiador cuando mpv ha entregado algo).
// Medido y aceptado — sin ese bloqueo se lee negro no determinista, que es
// peor que esperar. Queda escrito porque si algun dia el peor fotograma del
// compositor se dispara, este es el primer sitio donde mirar.
// =============================================================================

using WinCue.Core;
using WinCue.Video.Gpu;

namespace Secuence.App;

internal sealed class CapaCompositor : IVideoLayerSurface, IDisposable
{
    private readonly SalidaGpu _salida;

    // volatile por el mismo motivo que SharedSurfaceSource._disposed: lo LEE
    // el HILO DE RENDER DE MPV en InvalidateFrame y lo ESCRIBE el principal en
    // LiberarFuente. Sin volatile, un lector en otro nucleo podria no ver
    // nunca la escritura y llamar a MarkFrameReady sobre una fuente liberada.
    private volatile SharedSurfaceSource? _fuente;
    private OpacityNode? _nodo;
    private Action? _copiador;
    private int _ancho = 1, _alto = 1;
    private bool _enElGrafo;

    public CapaCompositor(SalidaGpu salida, int layer, int secuencia)
    {
        _salida = salida ?? throw new ArgumentNullException(nameof(salida));
        Layer = Math.Clamp(layer, 0, 7);
        Secuencia = secuencia;
    }

    public int Layer { get; }

    /// <summary>Numero de slot del pool de VideoEngine que pidio esta capa.
    /// Solo sirve para poder identificarla en los avisos a disco: dos capas
    /// de la misma pista (un cruce) son indistinguibles sin el.</summary>
    public int Secuencia { get; }

    public int? CueId { get; private set; }
    public SharedSurfaceSource? Fuente => _fuente;
    public GraphNode? Nodo => _nodo;

    // ── IVideoLayerSurface ────────────────────────────────────────────

    /// <summary>Esta superficie abre el handle compartido en OTRO device
    /// (D3D11): necesita el copiador fenceado. Ver la cabecera.</summary>
    public bool NeedsCrossDeviceFence => true;

    /// <summary>El puntero D3D9 NO se usa: lo que sirve es el handle. Aqui
    /// solo se apunta el tamano.</summary>
    public void SetSurface(nint d3d9Surface, int width, int height)
    {
        _ancho = Math.Max(1, width);
        _alto = Math.Max(1, height);
    }

    public void SetSharedHandle(nint sharedHandle)
    {
        LiberarFuente();
        try
        {
            _fuente = new SharedSurfaceSource(
                GpuDevice.Shared, sharedHandle, _ancho, _alto)
            {
                FrameCopier = _copiador,
            };
            _nodo = new OpacityNode(new SourceNode(_fuente));
        }
        catch (Exception exc)
        {
            _fuente = null;
            _nodo = null;
            // Sin puente no hay capa, y hay que DECIRLO hacia arriba: el
            // unico que sabe caer a la ruta clasica es VideoEngine, y solo se
            // entera por una excepcion (AcquireGpuSlot la captura, marca
            // _gpuFailed y reintenta el cue por el camino de siempre). Si nos
            // la tragaramos aqui, mpv arrancaria con vo=libmpv pintando en una
            // capa muerta: pantalla negra en vez de video por la ruta vieja.
            _salida.Avisar(
                $"No se pudo abrir la superficie compartida de la capa " +
                $"(pista {Layer + 1}, slot {Secuencia}): {exc.Message}");
            throw new InvalidOperationException(
                $"No se pudo abrir la superficie compartida en D3D11: {exc.Message}", exc);
        }
    }

    public void SetFrameCopier(Action copyFrame)
    {
        _copiador = copyFrame;
        if (_fuente is not null)
            _fuente.FrameCopier = copyFrame;
    }

    /// <summary>HILO DE RENDER DE MPV. Solo la bandera.</summary>
    public void InvalidateFrame() => _fuente?.MarkFrameReady();

    public void AssignCue(int cueId)
    {
        CueId = cueId;

        // LANZAR, no volver en silencio. Una capa sin nodo es una capa MUERTA
        // (su puente no se monto, o el anfitrion la solto al apagar el
        // compositor), y el unico que sabe caer a la ruta clasica es
        // VideoEngine — que solo se entera por una excepcion. Volviendo sin
        // mas, mpv arrancaba con vo=libmpv pintando en la nada: negro
        // permanente, con la barra de estado diciendo «Compositor GPU activo»
        // y sin que _gpuFailed llegara a marcarse nunca.
        if (_nodo is null || _fuente is null)
            throw new InvalidOperationException(
                $"La capa del compositor (pista {Layer + 1}, slot {Secuencia}) " +
                "no tiene superficie: el puente no esta montado.");

        // La superficie es AJENA y PERSISTENTE: todavia contiene el ultimo
        // fotograma del cue ANTERIOR. Olvidarlo aqui evita el destello de esa
        // imagen vieja mientras el cue nuevo arranca — el porque entero, y por
        // que se reinicia la bandera y no la opacidad, en
        // SharedSurfaceSource.ResetFrames().
        _fuente.ResetFrames();

        // Insertar REINICIA la opacidad, y no es un detalle: las capas son
        // slots PERSISTENTES de un pool (auditoria 05), asi que el cue nuevo
        // hereda la opacidad —y la rampa— del que uso el slot antes. Un cue
        // con fade_out deja su rampa terminando en 0, y Ramp.At devuelve 0
        // para siempre: sin reiniciar, el SIGUIENTE cue de esa pista nace
        // invisible. Medido en Secuence con el show de prueba: tras el
        // fade_out del 6.1 (pista 3), el 5.1 salia en negro.
        // Tambien LANZA si la salida se quedo sin compositor.
        _salida.Insertar(this);
        _enElGrafo = true;
    }

    public void ReleaseCue(int cueId)
    {
        if (CueId != cueId)
            return;
        CueId = null;
        if (!_enElGrafo)
            return;
        _salida.Quitar(this);
        _enElGrafo = false;
    }

    public void ClearSurface()
    {
        if (_enElGrafo)
        {
            _salida.Quitar(this);
            _enElGrafo = false;
        }
        CueId = null;
        LiberarFuente();
        _salida.Olvidar(this);
    }

    // ── Opacidad (la llama SalidaGpu, YA dentro de Mutate) ────────────

    /// <summary>Opacidad efectiva ahora mismo: si hay fundido en marcha, la
    /// que se esta viendo — para que un fundido nuevo arranque DESDE ahi y no
    /// desde el valor final del anterior (un crossfade interrumpido daria un
    /// salto).</summary>
    internal float OpacidadEn(double ahora) => _nodo?.OpacityAt(ahora) ?? 1f;

    /// <summary>LLAMAR SIEMPRE DENTRO DE Compositor.Mutate: el hilo de render
    /// lee estos dos campos en cada fotograma.</summary>
    internal void FijarOpacidad(float opacidad, Ramp? fundido)
    {
        if (_nodo is null)
            return;
        _nodo.Opacity = opacidad;
        _nodo.Fade = fundido;
    }

    /// <summary>La capa la toma un cue NUEVO: plena opacidad y sin rampa. El
    /// porque —y el fallo real que lo motivo— viven en OpacityNode.Reset().
    /// LLAMAR SIEMPRE DENTRO DE Compositor.Mutate.</summary>
    internal void ReiniciarOpacidad() => _nodo?.Reset();

    /// <summary>La salida vacio su grafo por debajo (recreacion de swapchain,
    /// o cierre): esta capa ya NO esta dentro, aunque ella creyera que si. Sin
    /// esto, un Quitar posterior no encontraria nada y un AssignCue se saltaria
    /// la insercion por creerse ya colocada.</summary>
    internal void OlvidarQueEstaEnElGrafo() => _enElGrafo = false;

    private void LiberarFuente()
    {
        _nodo = null;
        _fuente?.Dispose();
        _fuente = null;
    }

    public void Dispose() => ClearSurface();
}
