// =============================================================================
// FramePacer.cs — Selector de frames por reloj (puro, testeado en headless;
// promocionado de tools/hap-lab, paso 3 del motor de vídeo propio)
// =============================================================================

namespace WinCue.Video.Hap;

/// <summary>Resultado de una consulta al selector de frames.</summary>
/// <param name="FrameIndex">Frame que toca presentar (clampeado al último del clip).</param>
/// <param name="IsNew">true si el frame es distinto del último confirmado (hay que subir/renderizar).</param>
/// <param name="Dropped">Frames que se saltarían respecto al último confirmado (0 en avance normal).</param>
/// <param name="Finished">true cuando el reloj ya rebasó el final del clip.</param>
public readonly record struct PacerStep(int FrameIndex, bool IsNew, int Dropped, bool Finished);

/// <summary>
/// Selector de frames por reloj para la presentación: dado el tiempo
/// transcurrido decide qué frame toca (frame = floor(t·fps)), cuántos se
/// saltan (drops) y cuándo se acabó el clip. Clase pura SIN dependencias de
/// WPF/D3D para poder testearla en headless; el hilo de presentación real la
/// consulta desde CompositionTarget.Rendering con un Stopwatch.
/// La consulta (<see cref="Peek"/>) no muta estado: el llamador confirma con
/// <see cref="Commit"/> SOLO si de verdad presentó el frame (si el decoder
/// aún no lo tenía, el intento no cuenta y se reintenta al tick siguiente).
/// </summary>
public sealed class FramePacer
{
    private readonly double _fps;
    private readonly int _frameCount;
    private int _lastPresented = -1;

    /// <param name="fps">Frames por segundo del clip (&gt; 0).</param>
    /// <param name="frameCount">Número total de frames del clip (&gt; 0).</param>
    public FramePacer(double fps, int frameCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);
        _fps = fps;
        _frameCount = frameCount;
    }

    /// <summary>Frames presentados (confirmados con <see cref="Commit"/>).</summary>
    public int PresentedCount { get; private set; }

    /// <summary>Total de frames saltados acumulado (solo commits).</summary>
    public int TotalDropped { get; private set; }

    /// <summary>
    /// Qué frame toca en el instante <paramref name="elapsedSeconds"/>. Puro:
    /// no muta estado. Monótono respecto al último commit: si el reloj
    /// retrocede o no avanzó lo bastante, repite el último frame con
    /// IsNew = false y 0 drops.
    /// </summary>
    public PacerStep Peek(double elapsedSeconds)
    {
        if (elapsedSeconds < 0)
        {
            elapsedSeconds = 0;
        }

        // floor(t·fps): el frame n es vigente durante [n/fps, (n+1)/fps)
        int target = (int)(elapsedSeconds * _fps);
        bool finished = target >= _frameCount;
        if (target > _frameCount - 1)
        {
            target = _frameCount - 1;
        }

        if (target <= _lastPresented)
        {
            return new PacerStep(_lastPresented, IsNew: false, Dropped: 0, finished);
        }

        return new PacerStep(target, IsNew: true, target - _lastPresented - 1, finished);
    }

    /// <summary>Confirma que el frame de <paramref name="step"/> se presentó.</summary>
    public void Commit(PacerStep step)
    {
        if (!step.IsNew || step.FrameIndex <= _lastPresented)
        {
            return;
        }

        _lastPresented = step.FrameIndex;
        PresentedCount++;
        TotalDropped += step.Dropped;
    }
}
