// =============================================================================
// MediaEngineContracts.cs — Contratos de los motores de media
// =============================================================================
// Interfaces y tipos compartidos entre el controlador y los motores de audio
// (NAudio) y vídeo (libmpv). Viven en Core para que el controlador se pueda
// testear con motores falsos sin depender de hardware ni DLLs nativas.
// =============================================================================

namespace WinCue.Core;

/// <summary>Estado interno de un motor para un cue (espejo de los strings
/// IDLE/LOADING/LOADED/PLAYING/STOPPED/ERROR de la versión Python).</summary>
public enum MediaCueState { Idle, Loading, Loaded, Playing, Stopped, Error }

/// <summary>Banda paramétrica de EQ (frecuencia central, ganancia y ancho).</summary>
public sealed record EqBand(double Freq, double GainDb, double Q);

/// <summary>Compresor por clip (post-EQ): umbral/ratio con ataque y release.</summary>
public sealed record AudioCompParams(
    bool Enabled = false,
    double ThresholdDb = -18.0,
    double Ratio = 3.0,
    double AttackMs = 10.0,
    double ReleaseMs = 120.0,
    double MakeupDb = 0.0);

/// <summary>Procesado de audio de un cue: ganancia de clip (dB, pre-EQ),
/// HPF/LPF, hasta 4 bandas paramétricas y compresor. null = sin procesado.</summary>
public sealed record AudioEqParams(
    double GainDb = 0.0,
    bool EqEnabled = false,
    bool HpfOn = false,
    double HpfFreq = 80.0,
    bool LpfOn = false,
    double LpfFreq = 12000.0,
    IReadOnlyList<EqBand>? Bands = null,
    AudioCompParams? Comp = null);

/// <summary>Parámetros de reproducción de un cue de audio.
/// OutputDevice: nombre del dispositivo de salida ("" = el principal).</summary>
public sealed record AudioPlayParams(
    double Volume = 1.0,
    double FadeIn = 0.0,
    double FadeOut = 0.0,
    bool Loop = false,
    double TrimStart = 0.0,
    double TrimEnd = 0.0,
    int PlayCount = 1,
    string OutputDevice = "",
    AudioEqParams? Eq = null,
    double Rate = 1.0);

/// <summary>Display &amp; Geometry aplicable en vivo (subset factible sin compositor).</summary>
public sealed record VideoGeometry(
    string Mode = "FULL",            // FULL | CUSTOM
    bool PreserveAspect = true,
    double TranslateX = 0.0,         // px desde el centro (CUSTOM)
    double TranslateY = 0.0,
    double ScaleX = 1.0,             // multiplicador (CUSTOM)
    double RotateZ = 0.0,            // grados; se cuantiza a múltiplos de 90
    double SurfW = 1920.0,           // dimensiones del canvas de salida
    double SurfH = 1080.0);

/// <summary>Recorte del CANVAS que muestra una salida (modo canvas, estilo
/// Watchout): la salida Screen presenta el rectángulo (X, Y, W, H) del canvas
/// en su monitor. Coordenadas en píxeles de canvas, origen arriba-izquierda.</summary>
public sealed record CanvasView(int Screen, int X, int Y, int W, int H);

/// <summary>Parámetros de reproducción de un cue de vídeo.
/// Layer: pista de prioridad de la salida (0 = arriba del todo … 7 = fondo;
/// 2 = por defecto). Vídeos en pistas distintas conviven — el de la pista
/// superior tapa al de abajo; en la misma pista se sustituyen.
/// Screen: salida de vídeo de destino (0 = principal … 3); cada salida tiene
/// sus propias 5 pistas — la exclusividad de pista es POR SALIDA.
/// CanvasOutputs: MODO CANVAS (solo compositor GPU): el vídeo se coloca en el
/// canvas global (Geometry.SurfW/SurfH = tamaño del canvas) y cada salida de
/// la lista muestra su recorte — puede cruzar varias salidas con sync
/// perfecta (un solo render). Con lista, la exclusividad de pista es GLOBAL
/// (ignora Screen). null/vacía = modo clásico por salida.</summary>
public sealed record VideoPlayParams(
    double Volume = 1.0,
    bool Loop = false,
    double Scale = 100.0,            // % (100 = ajuste a la salida)
    double TrimStart = 0.0,
    double TrimEnd = 0.0,
    double Rate = 1.0,
    int PlayCount = 1,
    bool PreservePitch = true,
    VideoGeometry? Geometry = null,
    int Layer = 2,
    int Screen = 0,
    IReadOnlyList<CanvasView>? CanvasOutputs = null);

/// <summary>Motor de audio multi-stream (implementado por WinCue.Audio).</summary>
public interface IAudioEngine
{
    event Action<int, MediaCueState>? StateChanged;
    event Action<int, double>? LatencyReport;
    event Action<int, string>? ErrorOccurred;
    /// <summary>RMS estéreo (L, R) de cualquier stream (VU de programa).</summary>
    event Action<float, float>? LevelReport;
    /// <summary>RMS estéreo (L, R) de UN cue concreto (VU del inspector/audición).</summary>
    event Action<int, float, float>? CueLevelReport;

    void Play(int cueId, string filePath, AudioPlayParams? p = null);
    void Preload(int cueId, string filePath);
    void Stop(int cueId, double fade = 0.0);
    void StopAll(double fade = 0.0);
    bool Fade(int cueId, double targetVolume, double duration, bool stopAfter = false);
    void SetVolume(int cueId, double volume);
    void SetMasterVolume(double volume);
    void PauseAll();
    void ResumeAll();
    /// <summary>Pausa SOLO este cue (congela su posición; emite silencio).</summary>
    void PauseCue(int cueId);
    void ResumeCue(int cueId);
    bool IsPlaying(int cueId);
    double GetDuration(int cueId);
    double GetElapsed(int cueId);
    /// <summary>Descarta el buffer en RAM de un cue (su archivo cambió).</summary>
    void InvalidateCache(int cueId);
    void Cleanup();

    /// <summary>Aplica EQ/ganancia EN VIVO a un cue sonando (edición desde el
    /// inspector). Implementación por defecto: no-op (motores falsos).</summary>
    void SetEq(int cueId, AudioEqParams eq) { }

    /// <summary>Espectro del cue sonando para el RTA: bandas log 20 Hz–20 kHz
    /// en dBFS, o null si no suena. Por defecto: null (motores falsos).</summary>
    float[]? GetSpectrum(int cueId) => null;
}

/// <summary>Superficie de una pista del COMPOSITOR GPU: recibe la render
/// target D3D9 compartida donde mpv dibuja, y los avisos de frame nuevo.
/// La implementa la ventana de salida (D3DImage + opacidad por capa).</summary>
public interface IVideoLayerSurface
{
    /// <summary>Fija la superficie D3D9 (puntero COM) y su tamaño. Se llama
    /// en el main thread al arrancar el vídeo de la pista.</summary>
    void SetSurface(nint d3d9Surface, int width, int height);

    /// <summary>Hay un frame nuevo en la superficie (¡hilo de render!).</summary>
    void InvalidateFrame();

    /// <summary>La pista quedó vacía: soltar la superficie (main thread).</summary>
    void ClearSurface();
}

/// <summary>Motor de vídeo (implementado por WinCue.Video sobre libmpv).</summary>
public interface IVideoEngine
{
    event Action<int, MediaCueState>? StateChanged;
    event Action<int, double>? LatencyReport;
    event Action<int, string>? ErrorOccurred;
    event Action<int>? FrameReady;

    void Play(int cueId, string filePath, VideoPlayParams? p = null);
    void PreloadNext(int cueId, string filePath);
    void Stop(int cueId);
    void StopAll();
    bool FadeVolume(int cueId, double target, double duration, bool stopAfter = false);
    void SetVolume(int cueId, double volume);
    void SetMasterVolume(double volume);
    void PauseAll();
    void ResumeAll();
    /// <summary>Pausa SOLO este cue (congela su reloj).</summary>
    void PauseCue(int cueId);
    void ResumeCue(int cueId);
    bool IsPlaying(int cueId);
    double GetDuration(int cueId);
    double GetElapsed(int cueId);
    void ApplyGeometry(int cueId, VideoGeometry geo);
    void SetWindowHandleProvider(Func<nint> getWindowHandle);

    /// <summary>Proveedor de superficie POR (SALIDA, PISTA). Si se fija, cada
    /// vídeo renderiza en la superficie de su (Screen, Layer).
    /// Implementación por defecto: no-op (motores falsos / compatibilidad).</summary>
    void SetLayerHandleProvider(Func<int, int, nint>? provider) { }

    /// <summary>MODO COMPOSITOR GPU: si se fija, los vídeos renderizan vía
    /// render API a superficies D3D compartidas (opacidad real entre capas)
    /// en vez de a HWNDs, por (salida, pista). null = modo clásico.
    /// Default: no-op (fakes).</summary>
    void SetGpuLayerSurfaceProvider(Func<int, int, IVideoLayerSurface>? provider) { }

    void Cleanup();
}
