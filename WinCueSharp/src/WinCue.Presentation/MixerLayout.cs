// =============================================================================
// MixerLayout.cs — el CÁLCULO de la mesa de mezclas, sin framework.
// =============================================================================
// Traslado a Win2D de src/WinCue.App/AudioMixerPanel.cs y FaderScaleControl.cs
// (ver el encargo, hitos-0-1): de todo lo que pintan esos dos archivos, esto
// es lo que NO es DrawingContext/CanvasDrawingSession —
//
//   · FaderScaleLayout — la ley del fader en PÍXELES: dónde cae cada marca
//     de la escala en un fader vertical de un alto dado. Las marcas EN dB
//     (qué valores rotula, y a qué posición 0..1 del recorrido corresponden)
//     ya viven en WinCue.Core.AudioLevels (ScaleTicks, DbToPosition) —
//     compartidas desde antes de este traslado y con sus propias pruebas
//     (ver tests/WinCue.Core.Tests/CueJsonTests.cs, FaderLaw_*). Lo que
//     faltaba por sacar de FaderScaleControl.OnRender era el ÚLTIMO paso:
//     posición 0..1 → coordenada Y, contando el medio pomo del slider que
//     sobra arriba y abajo del recorrido útil.
//   · MixerLayout.PlanCueStrips — el REPARTO de strips: qué cues sonando o
//     no muestra la mesa, con qué datos y en qué orden. Viene literal de
//     AudioMixerPanel.SyncActive ("deseados"), que hasta esta tarea vivía
//     enredado con StackPanel/Border y no se podía probar sin levantar WPF.
//   · MixerLayout.ShouldCollapse — CUÁNDO colapsa un strip: la decisión
//     manual del operador manda; sin ella, colapsado si el cue no suena.
//
// El resto de AudioMixerPanel/FaderScaleControl (Slider, TextBox, Border,
// Grid, botones MUTE/CUE, colores) es PINTADO — vive en cada interfaz.
// =============================================================================

using WinCue.Controller;
using WinCue.Core;

namespace WinCue.Presentation;

/// <summary>Geometría en píxeles de la escala en dB de un fader vertical.</summary>
public static class FaderScaleLayout
{
    /// <summary>Alto del pomo del fader vertical. Mismo valor en el Slider de
    /// WPF (App.xaml, FaderThumb) y en el de WinUI 3: si un producto pintara
    /// su escala contando un pomo de otro alto, la marca «0» del uno no
    /// caería en el mismo píxel que la del otro — y son el mismo mueble.</summary>
    public const double ThumbHeight = 13.0;

    /// <summary>Posición del recorrido (0..1, de <see cref="AudioLevels"/>) →
    /// coordenada Y en píxeles, para un fader vertical de <paramref
    /// name="height"/> de alto. El pomo se CENTRA en su valor, así que arriba
    /// y abajo sobra medio pomo que hay que descontar del recorrido o la
    /// escala queda corrida respecto al fader. Null si el control es
    /// demasiado bajo para tener recorrido.</summary>
    public static double? PositionToY(double position, double height)
    {
        double travel = height - ThumbHeight;
        if (travel <= 0)
            return null;
        double top = ThumbHeight / 2.0;
        return top + (1.0 - position) * travel;
    }

    /// <summary>Coordenada Y de la marca de un valor en dB, con la MISMA ley
    /// que mueve el fader (<see cref="AudioLevels.DbToPosition"/>) — así el
    /// rótulo «0» cae exactamente donde el fader da 0 dB. Null en las mismas
    /// condiciones que <see cref="PositionToY"/>.</summary>
    public static double? TickY(double db, double height) =>
        PositionToY(AudioLevels.DbToPosition(db), height);
}

/// <summary>Un strip de cue que la mesa DEBE mostrar: los datos que necesita
/// para pintarse, sin nada de Border/StackPanel/CanvasControl.</summary>
public readonly record struct MixerStripSpec(
    int Id, string Number, string Name, CueType Type, string Color,
    bool MirrorAudio, bool IsSounding);

/// <summary>Reparto de la mesa: qué strips de cue muestra y cuándo pliega
/// uno. MASTER y AUDICIÓN no entran aquí — son fijos, sin reparto que
/// calcular (ver AudioMixerPanel.Build en cada interfaz).</summary>
public static class MixerLayout
{
    /// <summary>Strips de cue que la mesa debe mostrar, EN ORDEN.
    ///
    /// Con <paramref name="allCues"/>: la mesa enseña TODOS los cues que
    /// PUEDEN sonar (audio y vídeo con archivo), no solo los que suenan
    /// ahora mismo — así se ve la mesa entera y se puede dejar preparado un
    /// silencio antes de disparar. El orden es el de <paramref
    /// name="allCues"/> (la lista del show), no el del disparo: una mesa
    /// cuyos canales cambian de sitio no se puede tocar de memoria.
    ///
    /// Sin <paramref name="allCues"/> (null): solo los que están sonando
    /// AHORA, en el orden que traiga <paramref name="activeRows"/> — es el
    /// caso de una mesa que todavía no conoce el show completo.</summary>
    public static IReadOnlyList<MixerStripSpec> PlanCueStrips(
        IReadOnlyList<ActiveCueInfo> activeRows, IReadOnlyList<Cue>? allCues)
    {
        var sonando = new Dictionary<int, ActiveCueInfo>();
        foreach (var r in activeRows)
            if (r.Type is CueType.Audio or CueType.Video)
                sonando[r.Id] = r;

        var result = new List<MixerStripSpec>();
        if (allCues is not null)
        {
            foreach (var c in allCues)
            {
                if (c.Type is not (CueType.Audio or CueType.Video) || c.FilePath.Length == 0)
                    continue;
                result.Add(sonando.TryGetValue(c.Id, out var viva)
                    ? new MixerStripSpec(viva.Id, viva.Number, viva.Name, viva.Type, viva.Color,
                                         viva.MirrorAudio, IsSounding: true)
                    : new MixerStripSpec(c.Id, c.DisplayNumber, c.Name, c.Type, c.Color,
                                         MirrorAudio: false, IsSounding: false));
            }
        }
        else
        {
            foreach (var r in sonando.Values)
                result.Add(new MixerStripSpec(r.Id, r.Number, r.Name, r.Type, r.Color,
                                              r.MirrorAudio, IsSounding: true));
        }
        return result;
    }

    /// <summary>¿Este strip debe estar plegado? La decisión EXPLÍCITA del
    /// operador (<paramref name="manualOverride"/>) manda siempre — sin
    /// ella, plegado si el cue no está sonando: un cue parado no tiene nivel
    /// ni VU que enseñar, así que plegado no se pierde información.</summary>
    public static bool ShouldCollapse(bool isSounding, bool? manualOverride) =>
        manualOverride ?? !isSounding;
}
