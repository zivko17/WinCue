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
/// la vista lo muestra en ámbar para que el operador sepa que "viene".</summary>
public sealed record ActiveCueInfo(
    int Id,
    string Number,
    string Name,
    CueType Type,
    string Color,
    double Elapsed,
    double? Remaining,
    double? Fraction,
    double? PreWaitRemaining = null);

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
    /// <summary>RMS estéreo del programa (VU junto al monitor PGM).</summary>
    void UpdateVuMeter(float left, float right);

    /// <summary>RMS estéreo de un cue concreto (inspector; audición → VU del PVW).</summary>
    void UpdateCueVuMeter(int cueId, float left, float right);

    // ── Salida de vídeo ────────────────────────────────────────────────
    /// <summary>HWND del destino de vídeo actual (ventana de salida o testigo).
    /// 0 si aún no existe.</summary>
    nint GetVideoOutputHandle();

    /// <summary>HWND de la superficie de la PISTA dada (0..4) dentro de la
    /// salida. Por defecto cae a la superficie única (vistas falsas).</summary>
    nint GetVideoOutputLayerHandle(int layer) => GetVideoOutputHandle();

    /// <summary>Pistas de vídeo con contenido activo: la ventana de salida
    /// oculta las superficies vacías para que no tapen a las de abajo.</summary>
    void SetActiveVideoLayers(IReadOnlyCollection<int> activeLayers) { }

    /// <summary>Opacidad (0..1) de una pista de vídeo (solo compositor GPU).
    /// fadeSeconds &gt; 0 → transición animada (fundidos/crossfade de vídeo).</summary>
    void SetVideoLayerOpacity(int layer, double opacity, double fadeSeconds = 0.0) { }

    /// <summary>Dimensiones del canvas de salida (para la geometría CUSTOM).</summary>
    (double Width, double Height) SurfaceDimensions();

    /// <summary>Testigo: nombre del vídeo en pantalla, o null = NEGRO.</summary>
    void SetOutputContent(string? videoName);
}
