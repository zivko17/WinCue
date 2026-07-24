// =============================================================================
// FadeSanity.cs — AVISO SUAVE de fundido incoherente (nunca un bloqueo)
// =============================================================================
// NO hay ningún tope duro de duración: un fundido de 30 s o de 10 min es
// legítimo (cierre de función, instalaciones). El footgun real no son los
// "más de N segundos" absolutos, sino un fundido MÁS LARGO QUE EL TIEMPO SOBRE
// EL QUE PUEDE ACTUAR:
//
//   · fade_out > duración efectiva del cue  → el fundido no cabe: la rampa se
//     recorta contra el final del bloque (el controlador la limita con Clamp).
//   · fade_in (= crossfade) > lo que le queda al SALIENTE de su misma pista
//     cuando entra → el saliente muere a mitad del cruce. Con el congelado del
//     último fotograma la mezcla ya no cae a negro, pero la segunda mitad del
//     cruce se hace contra una IMAGEN FIJA: hay que decirlo.
//
// Este módulo es PURO (sin UI): la decisión se testea sola y la pintan el
// inspector (campos de "Tiempo y bucles") y el timeline.
// =============================================================================

namespace WinCue.Core;

/// <summary>Incoherencias detectadas en los fundidos de un cue (combinables).</summary>
[Flags]
public enum FadeIssues
{
    None = 0,
    /// <summary>fade_out más largo que la duración efectiva del cue.</summary>
    FadeOutTooLong = 1,
    /// <summary>fade_in más largo que la duración efectiva del cue.</summary>
    FadeInTooLong = 2,
    /// <summary>El crossfade dura más de lo que le queda al vídeo saliente.</summary>
    CrossfadeTooLong = 4,
}

public static class FadeSanity
{
    /// <summary>Margen para no avisar por redondeos de la barra de trim.</summary>
    public const double Tolerance = 0.01;

    /// <summary>Duración EFECTIVA de la acción (con el trim aplicado), la misma
    /// que usa el controlador para programar el fin y recortar el fade_out.
    /// 0 = desconocida (media sin sondear) o cue sin duración.</summary>
    public static double EffectiveDuration(Cue cue)
    {
        ArgumentNullException.ThrowIfNull(cue);
        if (!cue.IsMedia())
            return Math.Max(0.0, cue.Duration);
        double end = cue.TrimEnd > 0
            ? (cue.Duration > 0 ? Math.Min(cue.TrimEnd, cue.Duration) : cue.TrimEnd)
            : cue.Duration;
        return Math.Max(0.0, end - cue.TrimStart);
    }

    /// <summary>Revisa los fundidos del cue. <paramref name="outgoingRemaining"/>
    /// = segundos que le quedan al vídeo saliente de su misma pista cuando este
    /// entra (null = no hay saliente, o es un loop sin fin → sin aviso).</summary>
    public static FadeIssues Check(Cue cue, double? outgoingRemaining = null)
    {
        ArgumentNullException.ThrowIfNull(cue);
        if (!cue.IsMedia())
            return FadeIssues.None;

        var issues = FadeIssues.None;
        double dur = EffectiveDuration(cue);
        // Duración desconocida (0) o loop infinito: el fundido siempre cabe
        bool finite = dur > 0.0 && !cue.Loop;
        if (finite && cue.FadeOut > dur + Tolerance)
            issues |= FadeIssues.FadeOutTooLong;
        if (finite && cue.FadeIn > dur + Tolerance)
            issues |= FadeIssues.FadeInTooLong;

        // El crossfade solo existe en VÍDEO (el compositor cruza opacidades)
        if (cue.Type == CueType.Video && cue.FadeIn > 0.0
            && outgoingRemaining is double remaining
            && cue.FadeIn > remaining + Tolerance)
            issues |= FadeIssues.CrossfadeTooLong;

        return issues;
    }

    /// <summary>Aviso corto para la etiqueta (una línea), o null si no hay
    /// nada que decir. Localizado ES/EN.</summary>
    public static string? ShortText(FadeIssues issues)
    {
        if (issues == FadeIssues.None)
            return null;
        if (issues.HasFlag(FadeIssues.CrossfadeTooLong))
            return Loc.T("⚠ El fundido dura más que el vídeo saliente",
                         "⚠ The fade outlasts the outgoing video");
        return Loc.T("⚠ El fundido no cabe en el cue",
                     "⚠ The fade does not fit in the cue");
    }

    /// <summary>Explicación de QUÉ VA A PASAR (tooltip). Se enumeran todos los
    /// avisos activos; null si no hay ninguno.</summary>
    public static string? Explain(FadeIssues issues, double effectiveDuration,
        double? outgoingRemaining)
    {
        if (issues == FadeIssues.None)
            return null;
        var lines = new List<string>();
        if (issues.HasFlag(FadeIssues.FadeOutTooLong))
            lines.Add(Loc.T(
                $"El fundido de SALIDA es más largo que el cue ({Fmt(effectiveDuration)}): " +
                "se recortará al final del bloque y arrancará nada más empezar.",
                $"The FADE OUT is longer than the cue ({Fmt(effectiveDuration)}): " +
                "it will be clipped to the end of the block and start right away."));
        if (issues.HasFlag(FadeIssues.FadeInTooLong))
            lines.Add(Loc.T(
                $"El fundido de ENTRADA es más largo que el cue ({Fmt(effectiveDuration)}): " +
                "el cue terminará antes de llegar a su nivel final.",
                $"The FADE IN is longer than the cue ({Fmt(effectiveDuration)}): " +
                "the cue will end before reaching its final level."));
        if (issues.HasFlag(FadeIssues.CrossfadeTooLong))
            lines.Add(Loc.T(
                "El cruce dura más de lo que le queda al vídeo saliente" +
                (outgoingRemaining is double r ? $" ({Fmt(r)})" : "") +
                ": se completará con su ÚLTIMO FOTOGRAMA CONGELADO. " +
                "No es un fallo — la imagen no se va a negro — pero la segunda " +
                "mitad del cruce se hace contra una imagen fija.",
                "The crossfade outlasts the outgoing video" +
                (outgoingRemaining is double r2 ? $" ({Fmt(r2)})" : "") +
                ": it will finish against its LAST FRAME, held. " +
                "Not a failure — the picture never drops to black — but the " +
                "second half of the crossfade runs against a still image."));
        return string.Join("\n\n", lines);
    }

    private static string Fmt(double seconds) =>
        seconds >= 60.0
            ? $"{(int)(seconds / 60)}:{seconds % 60:00.0}"
            : seconds.ToString("0.##",
                System.Globalization.CultureInfo.InvariantCulture) + " s";
}
