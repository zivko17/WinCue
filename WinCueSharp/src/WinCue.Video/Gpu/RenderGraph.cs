// =============================================================================
// RenderGraph.cs — El grafo de composición (capa L3)
// =============================================================================
// LA decisión de fondo del motor nuevo: no una lista de capas, un grafo. Con
// lista plana, cada cosa es un caso especial dentro del bucle; con grafo son
// el MISMO mecanismo:
//
//   transición  → un nodo `mix`
//   PiP         → un nodo `transform`
//   modo canvas → un `transform` de recorte por salida
//   slate/veil  → un nodo `solid` mezclado como cualquier otro
//
// FORMA DE EVALUACIÓN: los nodos PINTAN EN EL DESTINO; no devuelven texturas.
// Eso evita tener que mantener vivas texturas intermedias entre nodos (y las
// asignaciones de llevar la cuenta), y hace que el préstamo del anillo de una
// fuente dure exactamente lo que su dibujo. Cuando lleguen los nodos `effect`,
// que sí necesitan aislar su subárbol, ahí es donde entra RtPool.
//
// CERO ASIGNACIONES por fotograma en todo este archivo: es el hilo de render.
// =============================================================================

using Vortice.Direct3D11;

namespace WinCue.Video.Gpu;

/// <summary>Dónde y cómo se pinta una capa.</summary>
public readonly record struct LayerTarget(
    ID3D11RenderTargetView Rtv,
    DestRect Dest,
    UvRect Uv,
    float Opacity)
{
    public LayerTarget WithOpacity(float o) => this with { Opacity = o };
}

/// <summary>Estado del fotograma en curso, común a todo el grafo.</summary>
public readonly record struct GraphFrame(double MediaTime, CompositorFrameStats Stats)
{
    internal LayerRenderer Renderer { get; init; }
    internal RtPool Pool { get; init; }

    /// <summary>Este pase solo MIRA: no consume banderas de "fotograma nuevo"
    /// ni dispara copias en las fuentes. Lo pone el pase de ESPEJO del
    /// compositor, que puede recorrer nodos de OTRA salida — ver
    /// IVideoSource.TryAcquireReadOnly para el porqué entero. Por defecto
    /// false: el pase principal SÍ es el dueño y sí hace ese trabajo.</summary>
    public bool SoloLectura { get; init; }
}

/// <summary>Contadores de UN fotograma del compositor. Se reinician por
/// fotograma y se agregan en CompositorStats.</summary>
public sealed class CompositorFrameStats
{
    public int LayersDrawn { get; internal set; }

    /// <summary>Capas que no pudieron pintarse porque su fuente no tenía
    /// fotograma para ese instante. Esto sí se ve en pantalla.</summary>
    public int Starved { get; internal set; }

    internal void Reset()
    {
        LayersDrawn = 0;
        Starved = 0;
    }
}

public abstract class GraphNode
{
    /// <summary>Pinta este nodo (y su subárbol) en el destino dado.</summary>
    public abstract void Render(in GraphFrame frame, in LayerTarget target);
}

// ---------------------------------------------------------------------------
// source — una fuente de la capa L1
// ---------------------------------------------------------------------------

/// <summary>Pinta el fotograma que la fuente tenga para el instante del frame.
/// El préstamo del anillo dura exactamente lo que el dibujo.</summary>
public sealed class SourceNode : GraphNode
{
    private readonly IVideoSource _source;

    /// <summary>Desfase entre el reloj del compositor y el tiempo del medio de
    /// esta fuente (p. ej. el trim de entrada del cue).</summary>
    public double TimeOffset { get; set; }

    public SourceNode(IVideoSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    public IVideoSource Source => _source;

    public override void Render(in GraphFrame frame, in LayerTarget target)
    {
        // En un pase de SOLO LECTURA (el espejo) se mira lo último que haya
        // sin tocar nada de la fuente: ni banderas ni copias. Ver
        // IVideoSource.TryAcquireReadOnly.
        double t = frame.MediaTime + TimeOffset;
        bool hay = frame.SoloLectura
            ? _source.TryAcquireReadOnly(t, out FrameLease lease)
            : _source.TryAcquire(t, out lease);
        if (!hay)
        {
            frame.Stats.Starved++;
            return;
        }
        using (lease)
        {
            frame.Renderer.Draw(target.Rtv, lease.View, target.Dest, target.Uv, target.Opacity);
            frame.Stats.LayersDrawn++;
        }
    }
}

// ---------------------------------------------------------------------------
// transform — geometría y recorte
// ---------------------------------------------------------------------------

/// <summary>Cambia el rectángulo de destino y/o el recorte del origen de su
/// subárbol. Es la pieza del PiP, del letterbox y del modo canvas.</summary>
public sealed class TransformNode : GraphNode
{
    public TransformNode(GraphNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        Child = child;
    }

    public GraphNode Child { get; }

    /// <summary>Destino en píxeles del lienzo. null = heredar el del padre.</summary>
    public DestRect? Dest { get; set; }

    /// <summary>Recorte del origen. null = heredar.</summary>
    public UvRect? Uv { get; set; }

    public override void Render(in GraphFrame frame, in LayerTarget target)
    {
        var t = new LayerTarget(target.Rtv, Dest ?? target.Dest, Uv ?? target.Uv, target.Opacity);
        Child.Render(frame, t);
    }
}

// ---------------------------------------------------------------------------
// mix — la transición
// ---------------------------------------------------------------------------

/// <summary>Mezcla dos subárboles. <see cref="Factor"/> 0 = solo A,
/// 1 = solo B. Un fundido cruzado es este nodo con el factor animado por el
/// reloj del compositor — no por una animación de WPF.</summary>
public sealed class MixNode : GraphNode
{
    public MixNode(GraphNode a, GraphNode b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        A = a;
        B = b;
    }

    public GraphNode A { get; }
    public GraphNode B { get; }

    private float _factor;

    /// <summary>Factor fijo. Se ignora mientras haya <see cref="Fade"/>.</summary>
    public float Factor
    {
        get => _factor;
        set => _factor = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>Fundido agendado en el RELOJ DEL COMPOSITOR. Mientras esté
    /// puesto, el factor sale de evaluarlo en el instante del fotograma — el
    /// mismo con el que las fuentes eligen su imagen. Ese es el cambio de
    /// fondo respecto a animar Opacity en WPF: todo el fotograma corresponde a
    /// un único instante.</summary>
    public Ramp? Fade { get; set; }

    /// <summary>Factor efectivo en un instante (lo que se verá).</summary>
    public float FactorAt(double t) => Fade?.At(t) ?? _factor;

    public override void Render(in GraphFrame frame, in LayerTarget target)
    {
        float f = FactorAt(frame.MediaTime);

        // A al fondo con la opacidad heredada; B encima con el factor. El
        // SALIENTE sigue pintado mientras dura el cruce aunque su fuente haya
        // muerto: es lo que en la ruta vieja hacía falta congelar a mano
        // (VisualFreezeRegistry), y aquí es el comportamiento natural.
        A.Render(frame, target);
        if (f > 0f)
            B.Render(frame, target.WithOpacity(target.Opacity * f));
    }
}

// ---------------------------------------------------------------------------
// opacity — la opacidad propia de UNA capa
// ---------------------------------------------------------------------------

/// <summary>Modula la opacidad de su subárbol, con fundido opcional.
///
/// POR QUÉ HACE FALTA HABIENDO <see cref="MixNode"/>: el mix modula la
/// opacidad ENTRE DOS RAMAS — es la transición, y necesita un saliente y un
/// entrante. Una pista SUELTA con opacidad propia (el campo «opacidad» de un
/// cue de vídeo, y el cue de control FADE que lo mueve) no tiene segunda rama
/// contra la que mezclarse: mezclarla contra un negro inventado pintaría un
/// velo que taparía a las pistas de DEBAJO en vez de dejarlas ver. La opacidad
/// viaja en <see cref="LayerTarget"/>, y este es el único nodo del grafo que la
/// cambia para una rama sola.
///
/// El fundido es un <see cref="Ramp"/> evaluado en el RELOJ DEL COMPOSITOR,
/// con el mismo instante con el que las fuentes eligen su fotograma: todo el
/// fotograma corresponde a un único momento. Ese es el cambio de fondo
/// respecto a animar Opacity en WPF.
///
/// HILOS: <see cref="Opacity"/> y <see cref="Fade"/> los escribe el hilo del
/// motor de cues DENTRO de OutputCompositor.Mutate() y los lee el hilo de
/// render dentro de ese mismo lock. Fuera de Mutate es una carrera.</summary>
public sealed class OpacityNode : GraphNode
{
    public OpacityNode(GraphNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        Child = child;
    }

    public GraphNode Child { get; }

    private float _opacity = 1f;

    /// <summary>Opacidad fija. Se ignora mientras haya <see cref="Fade"/>.</summary>
    public float Opacity
    {
        get => _opacity;
        set => _opacity = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>Fundido agendado en el reloj del compositor. Una vez terminado
    /// se queda puesto a propósito: Ramp.At devuelve To para siempre, así que
    /// nadie tiene que volver a pasar a recogerlo.</summary>
    public Ramp? Fade { get; set; }

    /// <summary>Opacidad efectiva en un instante (lo que se verá).</summary>
    public float OpacityAt(double t) => Fade?.At(t) ?? _opacity;

    /// <summary>Devuelve la capa a su estado de recién nacida: plena opacidad
    /// y SIN rampa.
    ///
    /// No es cosmético. Las capas del motor son slots PERSISTENTES de un pool
    /// (auditoría 05): el mismo nodo se reparte entre cues sucesivos de la
    /// misma (salida, pista, spec). Y como una rampa terminada devuelve su
    /// valor final PARA SIEMPRE, un cue con fade_out deja tras de sí una rampa
    /// que vale 0 — con lo que el SIGUIENTE cue de esa pista nacería
    /// invisible. Medido en Secuence con el show de prueba antes de que este
    /// método existiera: tras el fade_out del 6.1, el 5.1 salía en negro.
    /// Quien REASIGNE la capa a un cue nuevo tiene que llamar a esto, dentro
    /// de Mutate.</summary>
    public void Reset()
    {
        _opacity = 1f;
        Fade = null;
    }

    public override void Render(in GraphFrame frame, in LayerTarget target)
    {
        float o = OpacityAt(frame.MediaTime);
        if (o <= 0f)
            return;   // invisible: ni se muestrea la fuente
        Child.Render(frame, target.WithOpacity(target.Opacity * o));
    }
}

// ---------------------------------------------------------------------------
// solid — color plano (slate, velo de blackout, fondo)
// ---------------------------------------------------------------------------

/// <summary>Color plano. Lo que hoy son elementos WPF con Opacity encima del
/// vídeo, que sobre un swapchain darían problemas de airspace.</summary>
public sealed class SolidNode : GraphNode, IDisposable
{
    private readonly ID3D11Texture2D _texture;
    private readonly ID3D11ShaderResourceView _srv;
    private bool _disposed;

    public SolidNode(GpuDevice gpu, float r, float g, float b)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        _texture = gpu.CreateWorkTexture(1, 1);
        try
        {
            using ID3D11RenderTargetView rtv = gpu.Device.CreateRenderTargetView(_texture, null);
            gpu.Context.ClearRenderTargetView(rtv,
                new Vortice.Mathematics.Color4(r, g, b, 1f));
            _srv = gpu.Device.CreateShaderResourceView(_texture, null);
        }
        catch
        {
            _texture.Dispose();
            throw;
        }
    }

    public override void Render(in GraphFrame frame, in LayerTarget target)
    {
        if (_disposed)
            return;
        frame.Renderer.Draw(target.Rtv, _srv, target.Dest, UvRect.Full, target.Opacity);
        frame.Stats.LayersDrawn++;
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

// ---------------------------------------------------------------------------
// stack — N capas en orden de pista
// ---------------------------------------------------------------------------

/// <summary>Pinta sus hijos en orden. El orden de la lista ES el orden de las
/// pistas del show.</summary>
public sealed class StackNode : GraphNode
{
    private readonly List<GraphNode> _children = new();

    /// <summary>Los hijos. Mutarla desde fuera del hilo de render es cosa del
    /// llamante: el compositor sincroniza con su propio lock al aplicar
    /// mutaciones (ver OutputCompositor.Mutate).</summary>
    public IList<GraphNode> Children => _children;

    public override void Render(in GraphFrame frame, in LayerTarget target)
    {
        for (int i = 0; i < _children.Count; i++)
            _children[i].Render(frame, target);
    }
}
