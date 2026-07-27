// =============================================================================
// OutputCompositor.cs — El compositor que hoy no existe (capas L2 + L3 + L4)
// =============================================================================
// Un hilo, un reloj, un grafo, una render target. Cada fotograma:
//
//   1. esperar al tick de la salida (vsync del swapchain o reloj de software)
//   2. preguntar la hora al reloj maestro
//   3. evaluar el grafo EN ESE INSTANTE — cada fuente entrega el fotograma que
//      le corresponde a T (FrameCadence), no "el último que haya llegado"
//   4. presentar
//
// Eso es lo que hoy hace el DWM sin que nadie se lo pida y sin que nadie pueda
// decidir cuándo: de ahí que no haya transiciones propias, ni sincronía entre
// capas, ni una textura final que capturar.
//
// MUTACIONES: el grafo se toca desde fuera (el motor de cues) con Mutate(),
// que serializa contra el hilo de render. Un GO acaba siendo eso — cambiar el
// grafo — y por eso el motor nuevo puede ser DETERMINISTA de una forma que un
// mezclador de directo no necesita ser.
//
// CERO ASIGNACIONES por fotograma en el bucle.
// =============================================================================

using System.Diagnostics;

namespace WinCue.Video.Gpu;

/// <summary>Salud acumulada del compositor. Sin esto no hay diagnóstico
/// posible en la máquina de un cliente.</summary>
public sealed class CompositorStats
{
    public long FramesPresented { get; internal set; }

    /// <summary>Fotogramas en los que alguna capa se quedó sin imagen para el
    /// instante pedido. Esto se ve.</summary>
    public long FramesWithStarvation { get; internal set; }

    public int LastLayersDrawn { get; internal set; }

    /// <summary>Milisegundos del último fotograma (evaluación + presentación).</summary>
    public double LastFrameMs { get; internal set; }

    /// <summary>Peor fotograma visto. Es el número que importa en directo: la
    /// media siempre sale bien.</summary>
    public double WorstFrameMs { get; internal set; }

    /// <summary>Profundidad máxima de RTs intermedias que ha pedido el grafo.</summary>
    public int RtHighWaterMark { get; internal set; }
}

public sealed class OutputCompositor : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly IOutputSink _sink;
    private readonly LayerRenderer _renderer;
    private readonly RtPool _pool;
    private readonly Thread _thread;
    private readonly object _graphLock = new();
    private readonly CompositorFrameStats _frameStats = new();
    private readonly Stopwatch _frameWatch = new();
    private readonly Func<double> _clock;

    private GraphNode? _root;
    private volatile bool _shutdown;

    public CompositorStats Stats { get; } = new();

    public int Width => _sink.Width;
    public int Height => _sink.Height;

    /// <summary>Color del fondo del lienzo (lo que se ve donde no hay capa).</summary>
    public (float R, float G, float B) Background { get; set; } = (0f, 0f, 0f);

    /// <param name="clock">Reloj maestro: devuelve el instante del show en
    /// segundos. En producción lo pone el motor de cues (y en última instancia
    /// el audio, que es el maestro). Por defecto, un cronómetro propio.</param>
    public OutputCompositor(GpuDevice gpu, IOutputSink sink, Func<double>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(sink);
        _gpu = gpu;
        _sink = sink;
        _renderer = new LayerRenderer(gpu);
        _pool = new RtPool(gpu, sink.Width, sink.Height);

        var wall = Stopwatch.StartNew();
        _clock = clock ?? (() => wall.Elapsed.TotalSeconds);

        _thread = new Thread(RenderLoop)
        {
            Name = "wincue-compositor",
            IsBackground = true,
            // Por encima de lo normal: perder el turno de CPU en el hilo que
            // presenta se ve como un tirón. No es tiempo real (eso sí puede
            // colgar la máquina), solo prioridad alta.
            Priority = ThreadPriority.AboveNormal,
        };
    }

    public void Start() => _thread.Start();

    /// <summary>Cambia el grafo de forma segura frente al hilo de render.
    /// Un GO del motor de cues es exactamente esto.</summary>
    public void Mutate(Action<OutputCompositor> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        lock (_graphLock)
            mutation(this);
    }

    /// <summary>Raíz del grafo. Tocarla fuera de Mutate() es una carrera.</summary>
    public GraphNode? Root
    {
        get => _root;
        set => _root = value;
    }

    /// <summary>Dibuja UN fotograma en el instante dado, en el hilo llamante.
    /// Es lo que usan las pruebas: mismo camino que el bucle, sin hilo ni
    /// reloj de por medio, para poder afirmar sobre los píxeles.</summary>
    public void RenderOnce(double mediaTime)
    {
        lock (_graphLock)
            DrawFrame(mediaTime);
    }

    private void RenderLoop()
    {
        while (!_shutdown)
        {
            if (!_sink.WaitForNextFrame())
                break;
            if (_shutdown)
                break;

            double t = _clock();
            lock (_graphLock)
            {
                _frameWatch.Restart();
                DrawFrame(t);
                _sink.Present();
                _frameWatch.Stop();

                double ms = _frameWatch.Elapsed.TotalMilliseconds;
                Stats.LastFrameMs = ms;
                if (ms > Stats.WorstFrameMs)
                    Stats.WorstFrameMs = ms;
                Stats.FramesPresented++;
            }
        }
    }

    /// <summary>Llamar SIEMPRE con _graphLock tomado.</summary>
    private void DrawFrame(double mediaTime)
    {
        _frameStats.Reset();

        _renderer.Clear(_sink.Rtv, Background.R, Background.G, Background.B);

        GraphNode? root = _root;
        if (root is not null)
        {
            var frame = new GraphFrame(mediaTime, _frameStats)
            {
                Renderer = _renderer,
                Pool = _pool,
            };
            var target = new LayerTarget(_sink.Rtv,
                new DestRect(0, 0, _sink.Width, _sink.Height), UvRect.Full, 1f);
            root.Render(frame, target);
        }

        Stats.LastLayersDrawn = _frameStats.LayersDrawn;
        if (_frameStats.Starved > 0)
            Stats.FramesWithStarvation++;
        Stats.RtHighWaterMark = _pool.HighWaterMark;
    }

    public void Dispose()
    {
        if (_shutdown)
            return;
        _shutdown = true;
        if (Thread.CurrentThread != _thread && _thread.IsAlive)
            _ = _thread.Join(2_000);
        _pool.Dispose();
        _renderer.Dispose();
    }
}
