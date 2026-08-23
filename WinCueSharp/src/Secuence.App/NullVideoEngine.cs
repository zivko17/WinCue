// =============================================================================
// NullVideoEngine.cs — motor de vídeo NULO, provisional.
// =============================================================================
// MainController exige un IVideoEngine no nulo en su constructor (ver
// MainController.cs) aunque esta tarea NO toca vídeo — eso es el paso
// siguiente del roadmap de Secuence. En vez de referenciar WinCue.Video (que
// arrastra libmpv, una DLL nativa que esta tarea no necesita y que el CI
// tampoco versiona), este objeto nulo cubre el contrato con honestidad: cada
// cue de VIDEO falla de forma limpia — un ErrorOccurred que MainController
// convierte en OnVideoError → FlashStatus — y la app sigue viva.
//
// Ningún miembro de aquí hace nada de verdad. Cuando llegue el motor real de
// Secuence, este archivo se borra entero y WinCue.Audio deja de ser la única
// referencia de motor en el .csproj.
// =============================================================================

using WinCue.Core;

namespace Secuence.App;

/// <summary>IVideoEngine que no reproduce nada: honesto (avisa por evento de
/// error de que no puede) en vez de fingir. Ver cabecera del archivo.</summary>
// CS0067: StateChanged, LatencyReport y FrameReady los exige la interfaz pero
// este motor nunca cambia de estado, mide latencia ni pinta un frame — no hay
// nada que emitir. Play() SÍ dispara ErrorOccurred, que es lo único que
// MainController necesita oír de un motor que no puede reproducir.
#pragma warning disable CS0067
public sealed class NullVideoEngine : IVideoEngine
{
    public event Action<int, MediaCueState>? StateChanged;
    public event Action<int, double>? LatencyReport;
    public event Action<int, string>? ErrorOccurred;
    public event Action<int>? FrameReady;

    public void Play(int cueId, string filePath, VideoPlayParams? p = null) =>
        ErrorOccurred?.Invoke(cueId,
            "El vídeo aún no está implementado en Secuence (motor nulo, hito siguiente).");

    public void PreloadNext(int cueId, string filePath) { /* sin motor: nada que precargar */ }
    public void Stop(int cueId) { /* nunca llegó a sonar */ }
    public void StopAll() { /* nunca llegó a sonar */ }
    public bool FadeVolume(int cueId, double target, double duration, bool stopAfter = false) => false;
    public void SetVolume(int cueId, double volume) { }
    public void SetMasterVolume(double volume) { }
    public void PauseAll() { }
    public void ResumeAll() { }
    public void PauseCue(int cueId) { }
    public void ResumeCue(int cueId) { }
    public bool IsPlaying(int cueId) => false;
    public double GetDuration(int cueId) => 0.0;
    public double GetElapsed(int cueId) => 0.0;
    public void ApplyGeometry(int cueId, VideoGeometry geo, double? scalePercent = null) { }
    public void SetWindowHandleProvider(Func<nint> getWindowHandle) { /* no hay ventana que pedir */ }
    public void Cleanup() { }
}
#pragma warning restore CS0067
