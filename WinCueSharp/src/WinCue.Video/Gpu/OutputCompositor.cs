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

    /// <summary>Sink SECUNDARIO opcional: se dibuja en el MISMO fotograma y en
    /// el MISMO hilo que el principal. Es lo que hace del PGM un espejo del
    /// compositor en vez de un segundo decodificador. Dos hilos dibujando los
    /// mismos nodos serian una carrera sobre la superficie de presentacion;
    /// en el mismo fotograma no hay nada que sincronizar. No es propiedad
    /// nuestra: Dispose() no lo libera.</summary>
    public IOutputSink? Mirror { get; set; }

    /// <summary>Raiz alternativa para el espejo (el PGM en modo LIENZO
    /// reconstruye el lienzo entero). Si es null, el espejo pinta la raiz
    /// normal.</summary>
    public Func<GraphNode?>? MirrorRoot { get; set; }

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

    /// <summary>El hilo de render ya NO está corriendo: nadie recorre el grafo.
    ///
    /// Existe porque Dispose() hace Join(2 s) y NO mira el resultado, así que
    /// «he llamado a Dispose» no demuestra que el hilo haya muerto — y hay un
    /// caso real en el que no muere: quedarse dentro de DrawFrame (una copia
    /// fenceada esperando a una cola de GPU que ya no avanza), justo con
    /// _graphLock tomado y recorriendo Children POR ÍNDICE. Quien vaya a tocar
    /// el grafo «después de disponer» —sin Mutate, porque ya no hay compositor
    /// con quien serializar— tiene que preguntar esto ANTES: vaciar la lista
    /// bajo los pies de StackNode.Render tira la aplicación entera.
    ///
    /// Antes de Start() también es true, y es correcto: un compositor que nunca
    /// arrancó no tiene hilo que competir.</summary>
    public bool RenderThreadStopped => !_thread.IsAlive;

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
                Mirror?.Present();
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

        // El espejo se dibuja aqui mismo, en el mismo instante y el mismo
        // hilo: es la unica forma de que el PGM sea un espejo del compositor
        // y no un segundo decodificador con su propia idea de "ahora".
        IOutputSink? mirror = Mirror;
        if (mirror is not null)
        {
            _renderer.Clear(mirror.Rtv, Background.R, Background.G, Background.B);

            GraphNode? mirrorRoot = MirrorRoot is not null ? MirrorRoot() : root;
            if (mirrorRoot is not null)
            {
                var mirrorFrame = new GraphFrame(mediaTime, _frameStats)
                {
                    Renderer = _renderer,
                    Pool = _pool,
                    // SOLO LECTURA, y es lo que hace que un monitor no le
                    // cueste nada a la sala: MirrorRoot puede traer nodos de
                    // OTRA salida (el PGM en modo LIENZO recompone el lienzo
                    // entero), y muestrearlos "de verdad" pondría a ESTE hilo
                    // —el que presenta en sala— a consumir la bandera de
                    // fotograma nuevo de la otra salida y a esperar su fence
                    // de GPU, con el lock del grafo tomado. Ver
                    // IVideoSource.TryAcquireReadOnly.
                    SoloLectura = true,
                };
                // Dimensiones del espejo, no las del principal: puede ser un
                // PGM mas pequeno que la salida de sala.
                var mirrorTarget = new LayerTarget(mirror.Rtv,
                    new DestRect(0, 0, mirror.Width, mirror.Height), UvRect.Full, 1f);
                mirrorRoot.Render(mirrorFrame, mirrorTarget);
            }
        }
    }

    /// <summary>Para el hilo de render y libera lo suyo. El Join tiene TOPE:
    /// si expira, este método vuelve igual (colgar la aplicación esperando a un
    /// hilo de GPU atascado sería peor) y el hilo sigue vivo — quien lo
    /// necesite saber tiene <see cref="RenderThreadStopped"/>.</summary>
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
