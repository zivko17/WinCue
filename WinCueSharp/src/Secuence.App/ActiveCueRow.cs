// =============================================================================
// ActiveCueRow.cs — Fila del panel de cues activos (WinCue.Controller.ActiveCueInfo → UI).
// =============================================================================
// UpdateActiveCues(IReadOnlyList<ActiveCueInfo>) llega cada Tick (100 ms) con
// una lista NUEVA e inmutable: a diferencia de CueRow (que vive una fila fija
// y solo cambia su playhead), aquí no hace falta INotifyPropertyChanged — el
// panel entero se reconstruye cada vez (ver MainWindow.xaml.cs.UpdateActiveCues).
// Suficiente para esta tarea ("una barra de progreso por cue basta"); si el
// parpadeo molestara en la verificación de pantalla, la mejora natural es
// reconciliar en vez de recrear, igual que hace ActiveRowVM en WPF.
// =============================================================================

using Microsoft.UI.Xaml.Media;
using WinCue.Controller;

namespace Secuence.App;

public sealed class ActiveCueRow
{
    public ActiveCueRow(ActiveCueInfo info)
    {
        Number = info.Number;
        Name = info.Name;
        ColorBrush = Theme.BrushForLabelName(info.Color) is var b && b != Theme.TransparentBrush
            ? b
            : Theme.StandbyBrush;

        // En pre_wait (PreWaitRemaining != null): ámbar, cuenta atrás de la
        // espera previa. Sonando: progreso de la acción si se conoce
        // (Fraction), si no —duración indeterminada— la barra se queda a 0
        // pero el texto de tiempo transcurrido igual informa.
        bool enPreWait = info.PreWaitRemaining is not null;
        ProgressBrush = enPreWait ? Theme.StandbyBrush : Theme.GoBrush;
        ProgressPercent = enPreWait
            ? 0.0
            : Math.Clamp((info.Fraction ?? 0.0) * 100.0, 0.0, 100.0);

        RemainingText = enPreWait
            ? $"espera {FormatSeconds(info.PreWaitRemaining!.Value)}"
            : info.Remaining is double rem
                ? FormatSeconds(rem)
                : FormatSeconds(info.Elapsed);
    }

    public string Number { get; }
    public string Name { get; }
    public SolidColorBrush ColorBrush { get; }
    public double ProgressPercent { get; }
    public SolidColorBrush ProgressBrush { get; }
    public string RemainingText { get; }

    private static string FormatSeconds(double seconds)
    {
        seconds = Math.Max(0.0, seconds);
        int total = (int)Math.Round(seconds);
        return $"{total / 60}:{total % 60:00}";
    }
}
