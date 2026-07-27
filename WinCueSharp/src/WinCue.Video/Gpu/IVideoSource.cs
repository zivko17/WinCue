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

    VideoSourceStats Stats { get; }
}
