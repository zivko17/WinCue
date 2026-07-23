// =============================================================================
// IShowView.cs — Contrato del controlador hacia la vista
// =============================================================================
// En Python el controlador llamaba métodos concretos de MainWindow. Aquí se
// formaliza como interfaz: la ventana WPF la implementará en la fase 5 y los
// tests usan una vista falsa que registra las llamadas.
// Los diálogos de archivo NO están aquí: la vista los muestra y llama a
// SaveShow(path)/LoadShow(path) del controlador.
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller;

/// <summary>Fila del panel de cues activos (countdown, barra de progreso).
/// PreWaitRemaining != null → el cue aún está en su espera previa (pre_wait):
/// la vista lo muestra en ámbar para que el operador sepa que "viene".
/// MirrorAudio = true → cue de VÍDEO cuyo audio suena por el AudioEngine
/// (espejo HAP o híbrido): el Mixer le pone VU real de stream.</summary>
public sealed record ActiveCueInfo(
    int Id,
    string Number,
    string Name,
    CueType Type,
    string Color,
    double Elapsed,
    double? Remaining,
    double? Fraction,
    double? PreWaitRemaining = null,
    bool MirrorAudio = false);

public interface IShowView
{
    // ── Feedback de estado ─────────────────────────────────────────────
    void FlashStatus(string message);
    void ShowError(string title, string message);

    // ── Tabla / playhead ───────────────────────────────────────────────
    void SetPlayhead(int index);
    void RefreshRow(int index);
    void ReloadTable();
    void AppendCueRow(Cue cue);
    void ApplyGroupVisibility();
    void RebuildHotkeys();

    // ── Transport / paneles en vivo ────────────────────────────────────
    void UpdateTransport(bool paused);
    void UpdateCountdown(double? secondsRemaining);
    void UpdateActiveCues(IReadOnlyList<ActiveCueInfo> rows);

    /// <summary>Imagen de respaldo del PANIC: mostrarla en las salidas visibles
    /// (fundido de entrada de fadeSeconds) y quitarla. Default no-op (fakes y
    /// vistas sin salidas de vídeo).</summary>
    void ShowBackupSlate(double fadeSeconds) { }
    void HideBackupSlate() { }
    /// <summary>RMS estéreo del programa (VU junto al monitor PGM).</summary>
    void UpdateVuMeter(float left, float right);

    /// <summary>RMS estéreo de un cue concreto (inspector; audición → VU del PVW).</summary>
    void UpdateCueVuMeter(int cueId, float left, float right);

    // ── Salida de vídeo ────────────────────────────────────────────────
    /// <summary>HWND del destino de vídeo actual (ventana de salida o testigo).
    /// 0 si aún no existe.</summary>
    nint GetVideoOutputHandle();

    /// <summary>HWND de la superficie de la PISTA dada (0..7) dentro de la
    /// SALIDA dada (0..3). Por defecto cae a la superficie única (fakes).</summary>
    nint GetVideoOutputLayerHandle(int screen, int layer) => GetVideoOutputHandle();

    /// <summary>Pistas con contenido activo EN UNA SALIDA: su ventana oculta
    /// las superficies vacías para que no tapen a las de abajo.</summary>
    void SetActiveVideoLayers(int screen, IReadOnlyCollection<int> activeLayers) { }

    /// <summary>Opacidad (0..1) del vídeo de UN CUE (compositor GPU: cada cue
    /// tiene su superficie, en todas las salidas donde se vea). fadeSeconds
    /// &gt; 0 → transición animada (fundidos/crossfades, también entre dos
    /// cues de la misma pista). Default: no-op (fakes / modo clásico).</summary>
    void SetVideoCueOpacity(int cueId, double opacity, double fadeSeconds = 0.0) { }

    /// <summary>true si la ventana de esa salida ya está visible. La precarga
    /// en pausa del standby solo se hace entonces (no debe ABRIR ventanas al
    /// mover el playhead en modo EDIT). Default: false (fakes).</summary>
    bool IsVideoOutputVisible(int screen) => false;

    /// <summary>Dimensiones del canvas de salida (para la geometría CUSTOM).</summary>
    (double Width, double Height) SurfaceDimensions();

    /// <summary>Dimensiones del canvas de UNA salida concreta. Por defecto
    /// cae a las de la salida principal (vistas falsas).</summary>
    (double Width, double Height) SurfaceDimensions(int screen) => SurfaceDimensions();

    /// <summary>Testigo: nombre del vídeo en pantalla, o null = NEGRO.</summary>
    void SetOutputContent(string? videoName);

    /// <summary>Desglose multilínea del contenido por salida ("S1: vídeo X ·
    /// pista 3"). La vista lo muestra como tooltip del tally. Default: no-op.</summary>
    void SetOutputDetails(string details) { }
}
