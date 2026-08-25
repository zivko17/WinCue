// =============================================================================
// IVideoSource.cs — Contrato único de las fuentes (capa L1)
// =============================================================================
// Una fuente publica fotogramas CON PTS en un anillo. No sabe nada del grafo
// ni de la salida: no dibuja, no presenta y NUNCA bloquea al compositor.
//
// Lo que hoy son dos mundos incompatibles (HapGpuPipeline con su propio device
// y MpvGpuRenderer con D3D9Ex) pasa a ser esto, sobre un GpuDevice único.
// =============================================================================

namespace WinCue.Video.Gpu;

/// <summary>Contadores de salud de una fuente. El compositor los agrega; sin
/// esto no hay diagnóstico posible en la máquina de un cliente.</summary>
public sealed class VideoSourceStats
{
    /// <summary>Fotogramas publicados por el productor.</summary>
    public long Published { get; internal set; }

    /// <summary>Veces que el compositor pidió un instante y se le entregó el
    /// MISMO fotograma que la vez anterior. Es lo normal cuando el contenido
    /// va a menos fps que la salida (30p en 60 Hz repite cada uno: 2:2); solo
    /// es síntoma si crece sin que la cadencia lo explique.</summary>
    public long Repeats { get; internal set; }

    /// <summary>Veces que el compositor pidió un instante y NO había fotograma
    /// (la fuente va tarde). Esto sí es un fallo visible.</summary>
    public long Starvations { get; internal set; }

    /// <summary>Veces que el productor no encontró slot libre en el anillo.</summary>
    public long WriteStalls { get; internal set; }
}

public interface IVideoSource : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>true cuando la fuente ya ha publicado al menos un fotograma.</summary>
    bool HasFrames { get; }

    /// <summary>Fotograma correspondiente al instante <paramref name="mediaTime"/>
    /// (segundos del medio, no del reloj de pared). false si la fuente aún no
    /// ha llegado a ese instante. El préstamo se libera con Dispose.</summary>
    bool TryAcquire(double mediaTime, out FrameLease lease);

    /// <summary>Igual que <see cref="TryAcquire"/> pero SIN EFECTOS: quien
    /// llama solo quiere MIRAR lo último que haya, y no debe consumir banderas
    /// de "fotograma nuevo", ni disparar copias, ni mover contadores.
    ///
    /// POR QUÉ EXISTE: el pase de ESPEJO del compositor (el monitor PGM) puede
    /// muestrear nodos que pertenecen a OTRA salida — el modo LIENZO recompone
    /// el lienzo entero a partir de los grafos de todas—. Con el muestreo
    /// normal, el hilo que va A LA SALA de una salida acabaría invocando el
    /// copiador de OTRA (y, con el puente D3D9Ex, bloqueándose en un fence de
    /// un device ajeno con el lock del grafo tomado) solo porque hay un
    /// monitor mirando. La regla es que el monitor NO le cuesta nada a la
    /// sala: el espejo mira lo último que el dueño copió y, como mucho, va un
    /// fotograma por detrás — invisible en un monitor.
    ///
    /// EL COMPORTAMIENTO POR DEFECTO ES DELEGAR en <see cref="TryAcquire"/>, y
    /// eso NO ES CUMPLIR EL CONTRATO: es lo bastante bueno para las fuentes que
    /// hay hoy y nada más. Que quede claro, porque este es un contrato
    /// compartido y la trampa es fácil de pisar:
    ///
    ///   · <c>MpvSource</c> y <c>HapSource</c> escriben en su <c>TryAcquire</c>
    ///     — mueven <see cref="VideoSourceStats"/> y apuntan el PTS del último
    ///     préstamo—, así que heredando este cuerpo SÍ dejan rastro cuando solo
    ///     se les está mirando. Hoy es inocuo porque el espejo del PGM solo
    ///     recorre nodos sobre <c>SharedSurfaceSource</c>, que sí lo redefine;
    ///     deja de serlo en cuanto la ruta nativa (<c>MpvSource</c>) entre en
    ///     el grafo, que es el siguiente paso del diseño.
    ///   · Lo que el defecto evita es OBLIGAR a escribir el método a una fuente
    ///     nueva antes de que el fallo importe. Quien tenga efectos que le
    ///     pertenezcan al dueño de la salida —consumir una bandera de fotograma
    ///     nuevo, disparar una copia, mover contadores— TIENE que redefinirlo.
    ///     </summary>
    bool TryAcquireReadOnly(double mediaTime, out FrameLease lease) =>
        TryAcquire(mediaTime, out lease);

    VideoSourceStats Stats { get; }
}
