// =============================================================================
// CueTimeField.cs — Formato y parseo del campo de tiempo EDITABLE del inspector
// =============================================================================
// Extraído de src/WinCue.App/InspectorPanel.cs (TimeF/FormatTime/ParseTime),
// verbatim: es la única pieza de InspectorPanel que no es ni WPF ni una regla
// de negocio del motor, así que sube aquí para que Secuence la comparta sin
// copiarla — misma regla que TimelineLayout y VuMeterLayout.
//
// OJO — esto NO es TimelineLayout.FormatTime. Ese formatea ETIQUETAS de la
// regla del timeline (con sufijo "s", solo para leer) y este formatea un
// campo que hay que poder RE-ESCRIBIR: sin unidad, y Parse tiene que aceptar
// exactamente lo que Format produce (además de "90" a secas y "1:30,5" con
// coma). Round-trip, no legibilidad de regla.
// =============================================================================

using System.Globalization;

namespace WinCue.Presentation;

/// <summary>Formato y parseo del campo de tiempo editable del inspector
/// (pre-wait, duración, post-wait, fundidos, recorte). Acepta "90", "1:30" y
/// "1:30.5" (o con coma) al escribir; muestra "m:ss.d" a partir del minuto y
/// segundos con decimales por debajo.</summary>
public static class CueTimeField
{
    /// <summary>Segundos → texto editable. Por debajo del minuto, segundos con
    /// hasta 3 decimales; a partir del minuto, "m:ss.d" con el resto de ceros
    /// recortado (pero conservando al menos un decimal).</summary>
    public static string Format(double seconds) =>
        seconds >= 60.0
            ? $"{(int)(seconds / 60)}:{seconds % 60:00.0##}".TrimEnd('0').TrimEnd('.')
            : seconds.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Texto escrito a mano → segundos, o null si no se entiende.
    /// Acepta "90", "1:30", "1:30.5" y su forma con coma decimal.</summary>
    public static double? Parse(string? text)
    {
        if (text is null)
            return null;
        text = text.Trim().Replace(',', '.');
        if (text.Contains(':'))
        {
            var parts = text.Split(':');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double sec))
                return m * 60 + sec;
            return null;
        }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v : null;
    }
}
