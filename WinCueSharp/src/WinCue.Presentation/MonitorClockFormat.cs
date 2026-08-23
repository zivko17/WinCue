// =============================================================================
// MonitorClockFormat.cs — reloj "posición / duración" de los previos PGM/PVW
// =============================================================================
// Extraído de MonitorTransportBar.Mmss (src/WinCue.App/MonitorTransportBar.cs),
// verbatim: es el CÁLCULO de la tira de transporte del monitor — no toca WPF ni
// WinUI 3 — así que sube aquí para que Secuence lo comparta sin copiarlo, misma
// regla que CueTimeField/TimelineLayout.
//
// OJO — esto NO es CueTimeField.Format. Ese formatea un campo EDITABLE del
// inspector (sin minuto por debajo de 60s, para poder re-escribirlo tal cual);
// este es el reloj DE SOLO LECTURA del transporte del monitor, siempre
// "m:ss.d" desde 0:00.0. Interpola SIN fijar cultura a propósito (igual que
// TimelineLayout.FormatTime): el separador decimal depende de CurrentCulture,
// tal cual hacía Mmss en WPF — extraerlo no puede cambiar ese comportamiento
// (WinCue no puede cambiar de aspecto).
// =============================================================================

namespace WinCue.Presentation;

/// <summary>Reloj de solo lectura de un monitor PGM/PVW: "posición / duración",
/// cada mitad en "m:ss.d".</summary>
public static class MonitorClockFormat
{
    /// <summary>Segundos → "m:ss.d". NaN o negativo se trata como 0.</summary>
    public static string Mmss(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0)
            seconds = 0;
        int m = (int)(seconds / 60);
        return $"{m}:{seconds - m * 60:00.0}";
    }

    /// <summary>"posición / duración", el texto exacto de la tira de
    /// transporte del monitor.</summary>
    public static string Format(double position, double duration) =>
        $"{Mmss(position)} / {Mmss(duration)}";
}
