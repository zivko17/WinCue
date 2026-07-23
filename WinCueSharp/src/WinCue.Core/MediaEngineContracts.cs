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
    IReadOnlyList<CanvasView>? CanvasOutputs = null,
    double HandoverSeconds = 0.0,
    bool ForceClassicPath = false);
// ForceClassicPath = true: el cue NO pasa por el compositor GPU aunque esté
// activo — va por la ruta clásica (vo=gpu embebido en la ventana de salida).
// Lo usa el controlador para códecs que el interop del compositor no soporta
// (HAP revienta de forma nativa; la ruta clásica lo reproduce bien). Ese cue
// pierde los fundidos de opacidad del compositor (transiciones a corte).
// HandoverSeconds > 0 = CROSSFADE en la misma pista (solo compositor GPU):
// el vídeo que ya ocupa (Screen, Layer) NO se termina al entrar este; sigue
// vivo ese tiempo (su propio fin/fade_out lo retira) para que ambos se vean
// durante el fundido. En modo clásico se ignora (sustitución a corte).

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

    /// <summary>Reducción de ganancia actual del compresor del cue (dB ≥ 0)
    /// para el medidor GR. Por defecto: 0 (motores falsos).</summary>
    float GetGainReductionDb(int cueId) => 0f;

    /// <summary>Reducción de ganancia del LIMITADOR del bus master (dB ≥ 0)
    /// para el medidor GR del panel Audio. Por defecto: 0 (motores falsos).</summary>
    float GetMasterGainReductionDb() => 0f;

    /// <summary>RMS estéreo del dispositivo de AUDICIÓN por loopback (VU del
    /// PVW/panel Audio). Solo emite con medidor de audición activo.</summary>
    event Action<float, float>? AuditionLevelReport;
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

    /// <summary>Soltar la superficie del todo (main thread). Con el pool de
    /// pipelines (auditoría 05) solo se llama al DESTRUIR el pipeline (salida
    /// de la app / cambio de spec), no por cue.</summary>
    void ClearSurface();

    /// <summary>Un cue toma esta superficie del pool persistente: mapear el
    /// cueId (opacidad/z) y hacerla visible. Main thread. El binding D3DImage
    /// NO se recrea — la auditoría 05 demostró que el ciclo crear/destruir
    /// por cue fuga VRAM vía interop+dirty-rects.</summary>
    void AssignCue(int cueId) { }

    /// <summary>El cue soltó la superficie: desmapear y ocultar. El binding
    /// sobrevive para el siguiente cue. Main thread.</summary>
    void ReleaseCue(int cueId) { }

    /// <summary>Copiador de frame: el sink DEBE invocarlo DENTRO del Lock del
    /// D3DImage, justo antes de AddDirtyRect (contrato D3DImage: escribir al
    /// back buffer sin el Lock provoca renaming del driver = fuga de VRAM por
    /// frame — auditoría 05).</summary>
    void SetFrameCopier(Action copyFrame) { }
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

    /// <summary>GO INSTANTÁNEO: prepara el cue en STANDBY con el player ya
    /// creado y PAUSADO en su primer frame (compositor GPU); el Play posterior
    /// con los mismos parámetros solo quita la pausa (~0 ms de latencia).
    /// Sin compositor cae a la verificación clásica. Default: PreloadNext.</summary>
    void PrepareStandby(int cueId, string filePath, VideoPlayParams? p = null) =>
        PreloadNext(cueId, filePath);

    /// <summary>Descarta la precarga en pausa (cueId concreto, o -1 =
    /// cualquiera): playhead movido, cue editado o show recargado.</summary>
    void DiscardStandby(int cueId = -1) { }
    void Stop(int cueId);
    void StopAll();
    bool FadeVolume(int cueId, double target, double duration, bool stopAfter = false);
    void SetVolume(int cueId, double volume);

    /// <summary>EQ/compresor del audio del cue de vídeo (cadena af de mpv,
    /// paridad con el motor de audio). Aplica EN VIVO si el cue suena y se
    /// recuerda para players futuros. Default: no-op (motores falsos).</summary>
    void SetEq(int cueId, AudioEqParams? eq) { }
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
    /// render API a superficies D3D compartidas (opacidad real) en vez de a
    /// HWNDs. El provider recibe (salida, pista, cueId) y devuelve una
    /// superficie POR CUE: dos cues pueden convivir en la misma pista
    /// durante un crossfade. null = modo clásico. Default: no-op (fakes).</summary>
    void SetGpuLayerSurfaceProvider(Func<int, int, int, IVideoLayerSurface>? provider) { }

    void Cleanup();
}
