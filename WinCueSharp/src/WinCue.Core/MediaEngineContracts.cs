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

/// <summary>Procesado de audio de un cue: ganancia de clip (dB, pre-EQ),
/// HPF/LPF y hasta 4 bandas paramétricas. null = sin procesado.</summary>
public sealed record AudioEqParams(
    double GainDb = 0.0,
    bool EqEnabled = false,
    bool HpfOn = false,
    double HpfFreq = 80.0,
    bool LpfOn = false,
    double LpfFreq = 12000.0,
    IReadOnlyList<EqBand>? Bands = null);

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
    AudioEqParams? Eq = null);

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

/// <summary>Parámetros de reproducción de un cue de vídeo.</summary>
public sealed record VideoPlayParams(
    double Volume = 1.0,
    bool Loop = false,
    double Scale = 100.0,            // % (100 = ajuste a la salida)
    double TrimStart = 0.0,
    double TrimEnd = 0.0,
    double Rate = 1.0,
    int PlayCount = 1,
    bool PreservePitch = true,
    VideoGeometry? Geometry = null);

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
    void Cleanup();
}
