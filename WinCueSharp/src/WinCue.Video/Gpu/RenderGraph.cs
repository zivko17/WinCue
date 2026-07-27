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
        if (!_source.TryAcquire(frame.MediaTime + TimeOffset, out FrameLease lease))
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
