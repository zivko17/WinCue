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

/// <summary>Fila del panel de cues activos (countdown, barra de progreso).</summary>
public sealed record ActiveCueInfo(
    int Id,
    string Number,
    string Name,
    CueType Type,
    string Color,
    double Elapsed,
    double? Remaining,
    double? Fraction);

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
    void UpdateVuMeter(float rms);

    // ── Salida de vídeo ────────────────────────────────────────────────
    /// <summary>HWND del destino de vídeo actual (ventana de salida o testigo).
    /// 0 si aún no existe.</summary>
    nint GetVideoOutputHandle();

    /// <summary>Dimensiones del canvas de salida (para la geometría CUSTOM).</summary>
    (double Width, double Height) SurfaceDimensions();

    /// <summary>Testigo: nombre del vídeo en pantalla, o null = NEGRO.</summary>
    void SetOutputContent(string? videoName);
}
