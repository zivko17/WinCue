// =============================================================================
// HapSource.cs — El motor HAP propio como fuente del compositor (capa L1)
// =============================================================================
// Empareja a MpvSource: misma interfaz, mismo anillo, mismo device. A partir
// de aquí el compositor no distingue una fuente de otra — que es justo lo que
// hoy es imposible, con HapGpuPipeline y MpvGpuRenderer en devices distintos.
//
// NO reimplementa la reproducción: la pone HapCuePlayer entero (decodificación
// propia, reloj, pacing, loop, trims, fin natural), que es código con meses de
// casos reales encima. Lo único nuevo es a dónde dibuja.
// =============================================================================

using WinCue.Core;
using WinCue.Video.Containers;
using WinCue.Video.Hap;

namespace WinCue.Video.Gpu;

public sealed class HapSource : IVideoSource
{
    private readonly HapRingTarget _target;
    private readonly HapCuePlayer _player;
    private double _lastAcquiredPts = double.NaN;

    public int Width => _target.Width;
    public int Height => _target.Height;
    public bool HasFrames => _target.Ring.Published > 0;

    private readonly VideoSourceStats _stats = new();

    /// <summary>Los contadores del anillo se leen AL CONSULTAR, no al entregar
    /// un fotograma: si no, quedan a cero mientras la fuente ya está
    /// produciendo y el diagnóstico miente.</summary>
    public VideoSourceStats Stats
    {
        get
        {
            _stats.Published = _target.Ring.Published;
            _stats.WriteStalls = _target.Ring.WriteStalls;
            return _stats;
        }
    }

    /// <summary>Primer fotograma dibujado (equivalente del FrameReady clásico).</summary>
    public event Action? FirstFrame
    {
        add => _player.FirstFrame += value;
        remove => _player.FirstFrame -= value;
    }

    /// <summary>Fin de la reproducción (natural, por trims o por pases).</summary>
    public event Action? Ended
    {
        add => _player.Ended += value;
        remove => _player.Ended -= value;
    }

    /// <param name="clock">Reloj externo (elapsed del audio del cue) o null.
    /// Igual que en la ruta clásica: el audio manda y el vídeo se esclaviza.</param>
    public HapSource(GpuDevice gpu, string path, MovInfo info, HapTextureFormat format,
        VideoPlayParams playParams, int surfaceWidth, int surfaceHeight,
        Func<double?>? clock = null, Action<string>? trace = null)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(info);

        _target = new HapRingTarget(gpu, surfaceWidth, surfaceHeight);
        try
        {
            _player = new HapCuePlayer(path, info, format, _target, playParams, clock, trace);
        }
        catch
        {
            _target.Dispose();
            throw;
        }
    }

    public void Start() => _player.Start();

    public bool TryAcquire(double mediaTime, out FrameLease lease)
    {
        if (!_target.Ring.TryAcquire(mediaTime, out lease))
        {
            _stats.Starvations++;
            return false;
        }
        if (lease.Pts == _lastAcquiredPts)
            _stats.Repeats++;
        _lastAcquiredPts = lease.Pts;
        return true;
    }

    public void Dispose()
    {
        // Orden: primero el player (deja de dibujar), luego el destino.
        _player.Dispose();
        _target.Dispose();
    }
}
