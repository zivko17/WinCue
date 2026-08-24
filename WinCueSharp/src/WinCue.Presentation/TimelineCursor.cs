using WinCue.Controller;
using WinCue.Core;

namespace WinCue.Presentation;

/// <summary>Las dos cuentas del cursor de EDICIÓN del timeline: a qué
/// fotograma mirar (scrub) y qué arranca al reproducir desde ahí.
///
/// Ninguna de las dos es de interfaz. La de abajo, además, es de las que no
/// conviene tener escrita dos veces: decide qué suena y con cuánto retraso al
/// pulsar «reproducir desde aquí», y equivocarse un signo o un límite
/// significa que en un producto arranca un cue que en el otro no.</summary>
public static class TimelineCursor
{
    /// <summary>El vídeo que hay que enseñar en el visor de edición cuando el
    /// cursor está en <paramref name="seconds"/>, y en qué punto DEL ARCHIVO
    /// (contando su trim de entrada).
    ///
    /// Manda la pista MÁS ALTA de las que cruzan ese instante: si hay dos
    /// vídeos superpuestos, se mira el de encima, que es el que se está
    /// viendo. Los fantasmas (instancias lanzadas por un START) no cuentan.
    /// </summary>
    public static (Cue? Video, double MediaPosition) ScrubTarget(
        IReadOnlyList<TimelineBlock> blocks, double seconds)
    {
        Cue? video = null;
        int bestLayer = int.MaxValue;
        double mediaPos = 0;
        foreach (var b in blocks)
        {
            if (b.Cue.Type != CueType.Video || b.Dur <= 0 || b.Ghost)
                continue;
            if (seconds < b.Start || seconds > b.Start + b.Dur || b.Layer >= bestLayer)
                continue;
            bestLayer = b.Layer;
            video = b.Cue;
            mediaPos = b.Cue.TrimStart + (seconds - b.Start);
        }
        return (video, mediaPos);
    }

    /// <summary>Qué disparar al «reproducir desde el cursor».
    ///
    /// Dos reglas que no se ven en el código si no se dicen:
    ///
    /// 1. El SEGMENTO. Solo entra lo que cae antes del próximo salto manual
    ///    (marcador GO) posterior al cursor. Reproducir desde la mitad de una
    ///    sección no puede arrastrar la sección siguiente, que precisamente
    ///    espera un GO del operador.
    /// 2. Lo que CRUZA el cursor arranca a mitad, y solo si es media: un WAIT
    ///    o un FADE empezados por la mitad no aportan nada, porque la cadena
    ///    ya viene resuelta en la proyección.
    ///
    /// Los fantasmas se excluyen porque los dispara su propio cue START:
    /// incluirlos los duplicaría.</summary>
    public static List<MainController.TimelinePlayItem> PlayItems(
        IReadOnlyList<TimelineBlock> blocks,
        IReadOnlyList<double> goMarks,
        double seconds)
    {
        const double eps = 1e-6;

        double limit = double.PositiveInfinity;
        foreach (double g in goMarks)
            if (g > seconds + eps)
                limit = Math.Min(limit, g);

        var items = new List<MainController.TimelinePlayItem>();
        foreach (var b in blocks)
        {
            if (b.Dimmed || b.Ghost || b.Cue.Type == CueType.Group)
                continue;
            double end = b.Start + Math.Max(b.Dur, 0.0);
            if (end <= seconds + eps || b.Start >= limit - eps)
                continue;   // ya pasó · pertenece al siguiente segmento manual

            if (b.Start <= seconds)
            {
                if (b.Cue.Type is CueType.Audio or CueType.Video && b.Dur > 0)
                    items.Add(new MainController.TimelinePlayItem(
                        b.Cue.Id, seconds - b.Start, 0.0));
            }
            else if (b.Cue.Type is CueType.Audio or CueType.Video
                     or CueType.Fade or CueType.Start or CueType.Stop
                     or CueType.Script)
            {
                items.Add(new MainController.TimelinePlayItem(
                    b.Cue.Id, 0.0, b.Start - seconds));
            }
        }
        return items;
    }
}
