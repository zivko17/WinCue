// =============================================================================
// TimelineLayout.cs — el CÁLCULO del timeline del show, sin framework.
// =============================================================================
// Primera pasada del traslado a Win2D (ver el encargo, hitos-0-1): la
// proyección de la lista de cues en tiempo (mapeo tiempo↔píxel, marcas de la
// regla según la escala, rectángulos de los bloques, asignación a pistas,
// posición del playhead, detección de solapamiento vía FadeSanity). La
// SEGUNDA pasada trasladó también la interacción: TimelineViewport
// (zoom/scroll), TimelineGesture (hit-test y arrastre) y TimelineEditing
// (qué escribe el soltar); en TimelinePanel quedan solo WPF puro (ratón,
// pintado, cursor de edición, menú contextual).
//
// Los valores son LOS DE HOY: RulerH, MaxLayers, InstantFootprint, la escalera
// de RulerStep, el orden greedy de AssignLayers… proceden literalmente de
// TimelineCanvas (WinCue.App, ~2000 líneas). Un cambio aquí se nota en
// pantalla en los DOS productos y es un fallo, no un ajuste.
//
// POR QUÉ ESTE ARCHIVO SÍ REFERENCIA WinCue.Core Y WinCue.Controller
// —————————————————————————————————————————————————————————————————
// La regla de WinCue.Presentation.csproj es "nada de framework de INTERFAZ"
// (WPF, WinUI): Cue, CueModel, GroupMode, ActiveCueInfo o IShowClock no lo
// son — son el motor, ya compartido tal cual por WinCue.App y Secuence.App.
// Sacar la proyección del timeline sin poder recorrer un CueModel real habría
// obligado a duplicar aquí media clase Cue en forma de parámetros sueltos, que
// es la clase de "no-copiar-código" que este encargo existe para evitar.
//
// TimelineDragOverride — por qué el cálculo también sabe de arrastre
// —————————————————————————————————————————————————————————————————
// El timeline de WinCue SIGUE arrastrándose exactamente igual que hoy (esta
// pasada no le quita nada). Si el cálculo compartido solo supiera proyectar
// el modelo "quieto", WinCue necesitaría un segundo cálculo en paralelo para
// la vista previa en vivo del arrastre — y ESE sería el side channel por el
// que los dos productos podrían volver a divergir. TimelineDragOverride deja
// que la MISMA función Project() sirva a los dos casos: null (Secuence, y
// WinCue fuera de un arrastre) o con el override puesto (WinCue, mientras
// dura el gesto). Es la traducción directa de los DisplayPreWait/
// DisplayLayerRaw/DisplayTrimStart/DisplayTrimEnd/DisplayDuration que ya
// existían dentro de TimelineCanvas.
// =============================================================================

using WinCue.Controller;
using WinCue.Core;

namespace WinCue.Presentation;

/// <summary>Rectángulo neutral (sin framework) para la geometría del timeline.</summary>
public readonly record struct TimelineRect(double X, double Y, double Width, double Height);

/// <summary>Modo de arrastre en curso (mover, o redimensionar por un borde).
/// Mismos tres valores que el DragMode privado de TimelineCanvas.</summary>
public enum TimelineDragMode { Move, ResizeL, ResizeR }

/// <summary>Vista previa de un arrastre en curso, tal como la mantiene
/// TimelineCanvas mientras el operador mueve el ratón (ver OnMouseMove en
/// WinCue.App/TimelinePanel.cs). null en Project() = proyección "quieta":
/// el caso de Secuence y el de WinCue fuera de un arrastre.</summary>
public sealed class TimelineDragOverride
{
    public required int CueId;
    public required bool IsGroup;
    public required TimelineDragMode Mode;
    public required double PreWait;
    public required int Layer;
    public required double TrimStart;
    public required double TrimEnd;
    public required double Duration;
}

/// <summary>Un cue proyectado en el timeline: tiempo, duración, pista y los
/// efectos que los cues de CONTROL (START/STOP/FADE) dejan sobre él. Mismos
/// campos que el Block privado de TimelineCanvas — ver ApplyControlCueEffects
/// para el porqué de CutAt/RampStart/RampLen/RampToSilence.</summary>
public sealed class TimelineBlock
{
    public required Cue Cue;
    public double Start;
    public double Dur;            // 0 = instantáneo
    public int Layer;             // asignada (manual o auto)
    public bool Dimmed;           // desarmado / rama que no se disparará
    public bool ManualLayer;      // timeline_layer >= 0 (colocado a mano)

    public bool Ghost;            // instancia lanzada por un START (no editable)
    public double? CutAt;         // un STOP (o FADE y parar) lo corta aquí (abs)
    public double RampStart = -1; // rampa de un FADE sobre este bloque (abs)
    public double RampLen;
    public bool RampToSilence;    // FADE a volumen 0 sin parar: resto en silencio

    // AVISO SUAVE de fundido incoherente (el "indicador de solapamiento" del
    // encargo: sale del propio cálculo, vía OutgoingRemaining + FadeSanity).
    public FadeIssues Issues;
    public double? Headroom;
}

/// <summary>Recuadro de grupo: abraza las pistas de sus bloques descendientes.</summary>
public sealed class TimelineSpan
{
    public required Cue Group;
    public double Start;
    public double End;
    public int MinLayer;
    public int MaxLayer;
}

/// <summary>Resultado completo de una proyección: bloques, recuadros de
/// grupo, marcadores GO y el inicio absoluto de cada cue por id.</summary>
public sealed class TimelineProjection
{
    public List<TimelineBlock> Blocks { get; } = new();
    public List<TimelineSpan> Spans { get; } = new();
    public List<double> GoMarks { get; } = new();
    public Dictionary<int, double> StartById { get; } = new();
}

/// <summary>Geometría en píxeles de un bloque ya proyectado. DrawnDuration es
/// la duración RECORTADA por un CutAt (STOP/FADE con parada) cuando aplica —
/// no b.Dur a secas: el ancho dibujado del bloque tiene que respetar el corte,
/// exactamente como hace hoy TimelineCanvas.DrawBlock.</summary>
public readonly record struct TimelineBlockGeometry(TimelineRect Rect, double DrawnDuration, bool Instant);

/// <summary>Geometría y cálculo puro del timeline. Ni un tipo de framework de
/// interfaz — cada producto dibuja estos TimelineRect con lo que sepa
/// (DrawingContext.DrawRoundedRectangle, CanvasDrawingSession.FillRoundedRectangle…).</summary>
public static class TimelineLayout
{
    /// <summary>Alto de la regla de tiempo, en píxeles.</summary>
    public const double RulerH = 20;

    /// <summary>Techo de pistas — a juego con las 8 superficies de la ventana
    /// de salida.</summary>
    public const int MaxLayers = 8;

    /// <summary>Pistas mínimas visibles (UI ligera; crecen solas al usarlas).</summary>
    public const int MinLayers = 3;

    /// <summary>Huella temporal mínima de un cue instantáneo, en segundos —
    /// para que ocupe algo en el cálculo de solapamiento/pistas y de duración
    /// total, aunque no tenga Dur propia.</summary>
    public const double InstantFootprint = 0.3;

    /// <summary>Zoom mínimo, en píxeles por segundo (entran ~20 horas de show).</summary>
    public const double MinPxPerSec = 0.02;

    /// <summary>Zoom máximo, en píxeles por segundo.</summary>
    public const double MaxPxPerSec = 400;

    // ------------------------------------------------------------------
    // Mapeo tiempo ↔ píxel
    // ------------------------------------------------------------------

    public static double SecToX(double sec, double pxPerSec, double scrollX) => sec * pxPerSec - scrollX;

    public static double XToSec(double x, double pxPerSec, double scrollX) => (x + scrollX) / pxPerSec;

    // ------------------------------------------------------------------
    // Pistas
    // ------------------------------------------------------------------

    /// <summary>Alto de cada pista: reparte el alto disponible del lienzo
    /// (estilo Watchout) entre las pistas visibles.</summary>
    public static double LayerHeight(double actualHeight, int visibleLayers) =>
        Math.Max(24, (Math.Max(actualHeight, 90) - RulerH) / Math.Max(1, visibleLayers));

    /// <summary>Pistas visibles: la más alta usada + 2 de margen, entre
    /// MinLayers y MaxLayers.</summary>
    public static int VisibleLayers(IReadOnlyList<TimelineBlock> blocks)
    {
        int maxUsed = 0;
        foreach (var b in blocks)
            maxUsed = Math.Max(maxUsed, b.Layer);
        return Math.Clamp(maxUsed + 2, MinLayers, MaxLayers);
    }

    // ------------------------------------------------------------------
    // Regla de tiempo
    // ------------------------------------------------------------------

    /// <summary>Separación de las marcas de la regla. Escalera hasta las 4
    /// horas: el zoom puede alejarse mucho (MinPxPerSec = 0.02), y con un
    /// tope más corto la regla se convierte en un borrón de números encimados.</summary>
    public static double RulerStep(double pxPerSec)
    {
        foreach (double s in RulerSteps)
            if (s * pxPerSec >= 64)
                return s;
        return RulerSteps[^1];
    }

    private static readonly double[] RulerSteps =
    {
        0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400,
    };

    /// <summary>Instantes (en segundos) donde cae una marca de regla, visibles
    /// en el ancho dado. El mismo bucle que OnRender usaba en línea.</summary>
    public static IEnumerable<double> RulerTicks(double pxPerSec, double scrollX, double width)
    {
        double step = RulerStep(pxPerSec);
        double firstTick = Math.Floor(XToSec(0, pxPerSec, scrollX) / step) * step;
        for (double t = Math.Max(0, firstTick); SecToX(t, pxPerSec, scrollX) < width; t += step)
            yield return t;
    }

    /// <summary>Formato de una marca de tiempo: "0.#s" por debajo del minuto,
    /// "m:ss.#" por debajo de la hora, "h:mm:ss" a partir de la hora (72:30.0
    /// minutos no se lee de un vistazo, y con el zoom alejado esas cifras son
    /// las habituales).</summary>
    public static string FormatTime(double s)
    {
        if (s >= 3600)
        {
            int h = (int)(s / 3600);
            int mm = (int)((s - h * 3600) / 60);
            return $"{h}:{mm:00}:{s - h * 3600 - mm * 60:00}";
        }
        int m = (int)(s / 60);
        double rest = s - m * 60;
        return s >= 60 ? $"{m}:{rest:00.#}" : $"{rest:0.#}s";
    }

    // ------------------------------------------------------------------
    // Proyección: lista de cues → bloques con tiempo y capa
    // ------------------------------------------------------------------

    /// <summary>Proyecta el show completo: recorre la jerarquía de cues,
    /// aplica los efectos de los cues de CONTROL, asigna pistas y marca los
    /// avisos de fundido. drag = null → proyección "quieta" (Secuence, y
    /// WinCue fuera de un arrastre); con drag puesto, replica exactamente la
    /// vista previa en vivo que WinCue pinta mientras dura el gesto.</summary>
    public static TimelineProjection Project(CueModel model, TimelineDragOverride? drag = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var proj = new TimelineProjection();
        var roots = model.Cues.Where(c => c.ParentId is null).ToList();
        LayoutSequence(model, roots, 0.0, dimmed: false, drag, proj);
        ApplyControlCueEffects(model, drag, proj);
        AssignLayers(proj.Blocks);

        // Los recuadros de grupo abrazan las pistas de sus bloques
        foreach (var span in proj.Spans)
        {
            int min = MaxLayers - 1, max = 0;
            foreach (var b in proj.Blocks)
                if (!b.Ghost && model.IsDescendant(b.Cue, span.Group.Id))
                {
                    min = Math.Min(min, b.Layer);
                    max = Math.Max(max, b.Layer);
                }
            span.MinLayer = Math.Min(min, max);
            span.MaxLayer = Math.Max(min, max);
        }

        MarkFadeIssues(proj);
        return proj;
    }

    /// <summary>Segundos totales del show proyectado (huella de cada bloque,
    /// con el mínimo de InstantFootprint para los instantáneos).</summary>
    public static double TotalSeconds(IReadOnlyList<TimelineBlock> blocks)
    {
        double max = 0;
        foreach (var b in blocks)
            max = Math.Max(max, b.Start + Math.Max(b.Dur, InstantFootprint));
        return max;
    }

    private static double DisplayPreWait(Cue c, TimelineDragOverride? drag) =>
        drag is not null && c.Id == drag.CueId ? drag.PreWait : c.PreWait;

    private static int DisplayLayerRaw(Cue c, TimelineDragOverride? drag) =>
        drag is not null && c.Id == drag.CueId && !drag.IsGroup
            ? drag.Layer
            : Math.Clamp(c.TimelineLayer, -1, MaxLayers - 1);

    private static bool IsDragging(Cue c, TimelineDragMode mode, TimelineDragOverride? drag) =>
        drag is not null && c.Id == drag.CueId && drag.Mode == mode;

    private static double DisplayTrimStart(Cue c, TimelineDragOverride? drag) =>
        IsDragging(c, TimelineDragMode.ResizeL, drag) ? drag!.TrimStart : c.TrimStart;

    private static double DisplayTrimEnd(Cue c, TimelineDragOverride? drag) =>
        IsDragging(c, TimelineDragMode.ResizeR, drag) && c.IsMedia() ? drag!.TrimEnd : c.TrimEnd;

    private static double DisplayDuration(Cue c, TimelineDragOverride? drag) =>
        IsDragging(c, TimelineDragMode.ResizeR, drag) && !c.IsMedia() ? drag!.Duration : c.Duration;

    /// <summary>Duración de la acción del cue (0 = instantáneo), con los
    /// overrides del redimensionado en curso.</summary>
    private static double EffectiveDuration(Cue cue, TimelineDragOverride? drag)
    {
        switch (cue.Type)
        {
            case CueType.Audio:
            case CueType.Video:
                double ts = DisplayTrimStart(cue, drag);
                double te = DisplayTrimEnd(cue, drag);
                double end = te > 0
                    ? (cue.Duration > 0 ? Math.Min(te, cue.Duration) : te)
                    : cue.Duration;
                return Math.Max(0, end - ts);
            case CueType.Wait:
            case CueType.Fade:
                return Math.Max(0, DisplayDuration(cue, drag));
            default:
                return 0;
        }
    }

    /// <summary>Coloca una secuencia (nivel raíz o hijos de un grupo ENTER)
    /// encadenando triggers según continue_mode. Devuelve el fin de la última
    /// acción de la secuencia.</summary>
    private static double LayoutSequence(CueModel model, List<Cue> cues, double t0, bool dimmed,
        TimelineDragOverride? drag, TimelineProjection proj)
    {
        double trigger = t0;
        double lastEnd = t0;
        for (int i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            double actionStart = trigger + DisplayPreWait(cue, drag);
            double end = LayoutCue(model, cue, actionStart, dimmed, drag, proj);
            lastEnd = Math.Max(lastEnd, end);
            if (i == cues.Count - 1)
                break;
            switch (cue.ContinueMode)
            {
                case ContinueMode.AutoContinue:
                    trigger = actionStart + cue.PostWait;
                    break;
                case ContinueMode.AutoFollow:
                    trigger = end;
                    break;
                default:   // GO manual: proyección "dispara cuando termina"
                    trigger = Math.Max(trigger, end);
                    if (!dimmed)
                        proj.GoMarks.Add(trigger);
                    break;
            }
        }
        return lastEnd;
    }

    /// <summary>Coloca un cue (y sus hijos si es grupo) con la acción empezando
    /// en <paramref name="start"/>. Devuelve cuándo termina su acción.</summary>
    private static double LayoutCue(CueModel model, Cue cue, double start, bool dimmed,
        TimelineDragOverride? drag, TimelineProjection proj)
    {
        proj.StartById[cue.Id] = start;

        if (cue.Type == CueType.Group)
        {
            var children = model.DirectChildren(cue.Id);
            bool dim = dimmed || !cue.Armed;
            double end = start;
            switch (cue.GroupMode)
            {
                case GroupMode.Timeline:
                    // Tiempo REAL: todos a la vez, cada uno a su offset
                    foreach (var child in children)
                        end = Math.Max(end, LayoutCue(
                            model, child, start + DisplayPreWait(child, drag), dim, drag, proj));
                    break;
                case GroupMode.Enter:
                    end = LayoutSequence(model, children, start, dim, drag, proj);
                    break;
                default:   // GO_NEXT: solo el primero · RANDOM: cualquiera
                    for (int i = 0; i < children.Count; i++)
                    {
                        bool branchDim = dim ||
                            (cue.GroupMode == GroupMode.GoNext && i > 0);
                        double e = LayoutCue(model, children[i],
                            start + DisplayPreWait(children[i], drag), branchDim, drag, proj);
                        if (cue.GroupMode == GroupMode.Random || i == 0)
                            end = Math.Max(end, e);
                    }
                    break;
            }
            if (children.Count > 0)
                proj.Spans.Add(new TimelineSpan
                {
                    Group = cue,
                    Start = start,
                    End = Math.Max(end, start + 0.2),
                });
            return cue.Armed ? end : start;
        }

        int raw = DisplayLayerRaw(cue, drag);
        proj.Blocks.Add(new TimelineBlock
        {
            Cue = cue,
            Start = start,
            Dur = EffectiveDuration(cue, drag),
            Layer = raw,
            Dimmed = dimmed || !cue.Armed,
            ManualLayer = raw >= 0,
        });
        return cue.Armed ? start + EffectiveDuration(cue, drag) : start;
    }

    /// <summary>Los cues de CONTROL dejan su huella en la proyección: un STOP
    /// corta el bloque de su objetivo; un FADE dibuja la rampa sobre él (y lo
    /// corta si "parar al terminar"); un START añade un bloque FANTASMA del
    /// objetivo en su instante (se ve lo que va a sonar, sin ser editable).</summary>
    private static void ApplyControlCueEffects(CueModel model, TimelineDragOverride? drag, TimelineProjection proj)
    {
        foreach (var ctl in model.Cues)
        {
            if (ctl.TargetId is not int targetId
                || !proj.StartById.TryGetValue(ctl.Id, out double at)
                || model.GetById(targetId) is not Cue target)
                continue;

            switch (ctl.Type)
            {
                case CueType.Start:
                {
                    // El disparo respeta el PRE-WAIT del objetivo (semántica
                    // QLab): el fantasma se dibuja donde SONARÁ de verdad
                    double dur = EffectiveDuration(target, drag);
                    int raw = Math.Clamp(target.TimelineLayer, -1, MaxLayers - 1);
                    proj.Blocks.Add(new TimelineBlock
                    {
                        Cue = target,
                        Start = at + DisplayPreWait(target, drag),
                        Dur = dur,
                        Layer = raw,
                        ManualLayer = raw >= 0,
                        Dimmed = !ctl.Armed || !target.Armed,
                        Ghost = true,
                    });
                    break;
                }

                case CueType.Stop:
                case CueType.Fade:
                {
                    var tb = proj.Blocks.FirstOrDefault(b =>
                        !b.Ghost && b.Cue.Id == targetId);
                    if (tb is null || tb.Dur <= 0
                        || at <= tb.Start + 0.05 || at >= tb.Start + tb.Dur)
                        break;   // el objetivo no está sonando en ese punto
                    if (ctl.Type == CueType.Stop)
                    {
                        tb.CutAt = Min(tb.CutAt, at);
                    }
                    else
                    {
                        tb.RampStart = at;
                        tb.RampLen = Math.Max(0.1, ctl.Duration);
                        if (ctl.FadeStopOnDone)
                            tb.CutAt = Min(tb.CutAt, at + tb.RampLen);
                        else if (ctl.Volume <= 0.01)
                            tb.RampToSilence = true;
                    }
                    break;
                }
            }
        }

        static double Min(double? a, double b) => a is double v ? Math.Min(v, b) : b;
    }

    /// <summary>Capas: la fijada a mano se respeta; el resto se apila greedy
    /// en la primera pista libre para su intervalo.</summary>
    private static void AssignLayers(List<TimelineBlock> blocks)
    {
        var ends = new double[MaxLayers];
        for (int i = 0; i < MaxLayers; i++)
            ends[i] = double.NegativeInfinity;

        foreach (var b in blocks.OrderBy(b => b.Start))
        {
            double foot = b.Start + Math.Max(b.Dur, InstantFootprint);
            if (b.ManualLayer)
            {
                b.Layer = Math.Clamp(b.Layer, 0, MaxLayers - 1);
                ends[b.Layer] = Math.Max(ends[b.Layer], foot);
                continue;
            }
            int chosen = MaxLayers - 1;
            for (int l = 0; l < MaxLayers; l++)
            {
                if (ends[l] <= b.Start + 1e-6)
                {
                    chosen = l;
                    break;
                }
            }
            b.Layer = chosen;
            ends[chosen] = Math.Max(ends[chosen], foot);
        }
    }

    /// <summary>Segundos que le QUEDAN al vídeo SALIENTE de la misma pista
    /// cuando este cue entra, según la proyección. null = no hay saliente vivo
    /// (no hay cruce) o el saliente no acaba nunca (loop). Es el dato que
    /// convierte "fade_in de 8 s" en "el cruce no cabe".</summary>
    public static double? OutgoingRemaining(IReadOnlyList<TimelineBlock> blocks, int cueId)
    {
        var block = blocks.FirstOrDefault(b => !b.Ghost && b.Cue.Id == cueId);
        if (block is null || block.Cue.Type != CueType.Video)
            return null;
        TimelineBlock? prev = null;
        foreach (var b in blocks)
        {
            if (b.Ghost || b.Cue.Id == cueId || b.Cue.Type != CueType.Video)
                continue;
            if (b.Layer != block.Layer || b.Dimmed)
                continue;
            if (b.Start > block.Start + 0.001)
                continue;                                  // entra después
            if (b.Cue.Loop)
                return null;                               // no acaba nunca
            if (b.Start + b.Dur <= block.Start + 0.001)
                continue;                                  // ya había acabado
            if (prev is null || b.Start > prev.Start)
                prev = b;
        }
        return prev is null ? null : prev.Start + prev.Dur - block.Start;
    }

    /// <summary>Marca en cada bloque sus avisos de fundido incoherente (aviso
    /// SUAVE: quien pinte decide cómo mostrarlo; no impide NADA). Es el
    /// "indicador de solapamiento" del encargo: sale del propio cálculo
    /// (OutgoingRemaining) vía FadeSanity, sin estado propio de interfaz.</summary>
    private static void MarkFadeIssues(TimelineProjection proj)
    {
        foreach (var b in proj.Blocks)
        {
            b.Headroom = b.Ghost ? null : OutgoingRemaining(proj.Blocks, b.Cue.Id);
            b.Issues = b.Ghost ? FadeIssues.None : FadeSanity.Check(b.Cue, b.Headroom);
        }
    }

    // ------------------------------------------------------------------
    // Geometría de pintado
    // ------------------------------------------------------------------

    /// <summary>Rectángulo del bloque, ya en píxeles. La duración DIBUJADA
    /// respeta un CutAt (STOP/FADE con parada): el bloque se ve truncado
    /// hasta el corte, no hasta su duración completa.</summary>
    public static TimelineBlockGeometry BlockGeometry(
        TimelineBlock block, double pxPerSec, double scrollX, double layerHeight)
    {
        double dur = block.CutAt is double cutAbs
            ? Math.Max(0.05, Math.Min(block.Dur, cutAbs - block.Start))
            : block.Dur;
        double x = SecToX(block.Start, pxPerSec, scrollX);
        double y = RulerH + block.Layer * layerHeight + 4;
        double bh = layerHeight - 8;
        bool instant = block.Dur <= 0;
        double bw = instant ? 12 : Math.Max(4, dur * pxPerSec);
        return new TimelineBlockGeometry(new TimelineRect(x, y, bw, bh), dur, instant);
    }

    /// <summary>Caja del recuadro de un grupo (marco), en píxeles. El "asa"
    /// con el nombre la mide y la coloca cada interfaz (depende de cómo mida
    /// texto su framework) — ver SpanLabelText para el texto exacto.</summary>
    public static TimelineRect SpanBoxRect(TimelineSpan span, double pxPerSec, double scrollX, double layerHeight)
    {
        double x0 = SecToX(span.Start, pxPerSec, scrollX) - 4;
        double x1 = SecToX(span.End, pxPerSec, scrollX) + 4;
        double y = RulerH + span.MinLayer * layerHeight + 1;
        double bh = (span.MaxLayer - span.MinLayer + 1) * layerHeight - 2;
        return new TimelineRect(x0, y, Math.Max(12, x1 - x0), bh);
    }

    /// <summary>Etiqueta del recuadro de grupo: icono + número · nombre · modo.
    ///
    /// <paramref name="withIcon"/> = false la devuelve SIN el glifo. No es un
    /// capricho de estilo: quien pinta con Win2D (Secuence) no puede
    /// resolverlo —DirectWrite no ve la fuente de iconos, que se carga solo
    /// para XAML— y el glifo saldría como un cuadradito. Es la misma razón
    /// por la que allí el marcador GO va sin su ▸.</summary>
    public static string SpanLabelText(TimelineSpan span, bool withIcon = true) =>
        (withIcon ? $"{Icons.Group} " : "")
        + $"{span.Group.DisplayNumber} · {span.Group.Name} · "
        + span.Group.GroupMode.ToString().ToUpperInvariant();

    /// <summary>Etiqueta de un bloque: número · nombre, con los prefijos/sufijos
    /// de hoy (↗ fantasma, ∞/×N repeticiones, 📌 pista fijada a mano).</summary>
    public static string BlockLabelText(TimelineBlock b)
    {
        string text = $"{b.Cue.DisplayNumber} · {b.Cue.Name}";
        if (b.Ghost)
            text = $"{Icons.Jump} " + text;
        if (b.Cue.Loop || b.Cue.PlayCount > 1)
            text += b.Cue.Loop ? $" {Icons.Loop}" : $" ×{b.Cue.PlayCount}";
        if (b.ManualLayer && !b.Ghost)
            text = $"{Icons.Pin} " + text;
        return text;
    }
}

/// <summary>Colores del timeline derivados de <see cref="Palette"/>, para que
/// la tabla y el timeline (y los dos productos) no puedan volver a divergir —
/// ver la cabecera de Theme.cs (WinCue.App) para la historia del desacople
/// que esto reemplaza.</summary>
public static class TimelinePalette
{
    /// <summary>Factor de oscurecimiento del degradado de los bloques (de
    /// arriba/izquierda hacia abajo/derecha): mismo valor que el
    /// LinearGradientBrush de hoy en TimelineCanvas.DrawBlock.</summary>
    public const double BlockGradientDarken = 0.65;

    /// <summary>Color por tipo de cue — ÚNICA definición, la comparten la
    /// columna «Tipo» de la tabla y los bloques del timeline, en los DOS
    /// productos.</summary>
    public static uint ForCueType(CueType type) => type switch
    {
        CueType.Audio => Palette.TypeAudio,
        CueType.Video => Palette.TypeVideo,
        CueType.Wait => Palette.TypeWait,
        CueType.Fade => Palette.Warn,
        CueType.Start => Palette.Go,
        CueType.Stop => Palette.Pgm,
        CueType.Script => Palette.TypeScript,
        CueType.Memo => Palette.TypeMemo,
        CueType.Group => Palette.Standby,
        _ => Palette.TextPrimary,
    };

    /// <summary>Color a mostrar para un cue: su etiqueta de color manual si
    /// tiene una puesta (menú contextual "Color"), si no el de su tipo.</summary>
    public static uint ResolveCueColor(Cue cue) =>
        cue.Color.Length > 0
        && CueConstants.CueColors.TryGetValue(cue.Color, out string? hex)
        && !string.IsNullOrEmpty(hex)
            ? ParseHex(hex)
            : ForCueType(cue.Type);

    /// <summary>Oscurece un color de Palette por un factor (0..1).</summary>
    public static uint Darken(uint rgb, double factor)
    {
        byte r = (byte)Math.Clamp(((rgb >> 16) & 0xff) * factor, 0, 255);
        byte g = (byte)Math.Clamp(((rgb >> 8) & 0xff) * factor, 0, 255);
        byte b = (byte)Math.Clamp((rgb & 0xff) * factor, 0, 255);
        return ((uint)r << 16) | ((uint)g << 8) | b;
    }

    private static uint ParseHex(string hex) => Convert.ToUInt32(hex.TrimStart('#'), 16);
}

/// <summary>Playhead del timeline: cursor de reproducción real, extrapolado
/// entre ticks del controlador con el reloj del show. Mismo patrón que
/// VuMeterState — estado mutable por show (no por bloque), porque un tick de
/// activos llega muchas veces por segundo y no hay nada que ganar
/// inmutabilizándolo.</summary>
public sealed class TimelinePlayhead
{
    private readonly Dictionary<int, double> _activeElapsed = new();
    private readonly Dictionary<int, bool> _activeAdvancing = new();
    private readonly Dictionary<int, double> _smoothElapsed = new();
    private double _activeStamp;

    /// <summary>true mientras haya algún cue activo (para saber si hace
    /// falta seguir repintando a la frecuencia de fotograma).</summary>
    public bool HasActive => _activeElapsed.Count > 0;

    /// <summary>Tick del controlador: qué cues están sonando ahora mismo
    /// (Elapsed cuenta desde el disparo; los en pre_wait no cuentan — su
    /// acción no ha empezado). now = IShowClock.Now en el instante del tick.</summary>
    public void SetActive(IReadOnlyList<ActiveCueInfo> rows, double now)
    {
        // ¿Avanza cada cue respecto al tick anterior? (para extrapolar solo
        // los que corren; los pausados se quedan quietos)
        _activeAdvancing.Clear();
        foreach (var r in rows)
            if (r.PreWaitRemaining is null)
                _activeAdvancing[r.Id] =
                    !_activeElapsed.TryGetValue(r.Id, out double prev)
                    || r.Elapsed > prev + 0.001;
        _activeStamp = now;
        _activeElapsed.Clear();
        foreach (var r in rows)
        {
            if (r.PreWaitRemaining is not null)
                continue;
            _activeElapsed[r.Id] = r.Elapsed;
        }
        // Purga del filtro de persecución: cues que ya no suenan
        if (_smoothElapsed.Count > 0)
            foreach (int id in _smoothElapsed.Keys.Where(k => !_activeElapsed.ContainsKey(k)).ToList())
                _smoothElapsed.Remove(id);
    }

    public bool TryGetElapsed(int cueId, out double raw) => _activeElapsed.TryGetValue(cueId, out raw);

    /// <summary>true si el cue está activo (sonando, fuera de pre_wait) en el
    /// último tick recibido.</summary>
    public bool IsActive(int cueId) => _activeElapsed.ContainsKey(cueId);

    /// <summary>Ids de los cues activos en el último tick — para saber, por
    /// ejemplo, si algún descendiente de un grupo está sonando.</summary>
    public IEnumerable<int> ActiveCueIds => _activeElapsed.Keys;

    /// <summary>Objetivo: raw del tick + extrapolación con el reloj del show
    /// (cap 350 ms; solo si el cue avanzaba — uno pausado no debe deslizar
    /// y volver). clockNow = IShowClock.Now en el instante de PINTAR (no el
    /// del tick): la extrapolación crece según pasa el tiempo real entre ticks.</summary>
    public double LiveElapsed(int cueId, double raw, double clockNow)
    {
        if (!_activeAdvancing.GetValueOrDefault(cueId))
            return raw;
        double extra = clockNow - _activeStamp;
        return raw + Math.Min(Math.Max(0.0, extra), 0.35);
    }

    /// <summary>Posición DIBUJADA: persigue al objetivo (25 % del error por
    /// frame) en vez de saltar a él — las correcciones de los pushes del
    /// motor quedarían como micro-tirones. Un error grande (seek) sí salta.</summary>
    public double DrawnElapsed(int cueId, double raw, double clockNow)
    {
        double target = LiveElapsed(cueId, raw, clockNow);
        double cur = _smoothElapsed.TryGetValue(cueId, out double c) ? c : target;
        double delta = target - cur;
        cur = Math.Abs(delta) > 0.6 ? target : cur + delta * 0.25;
        _smoothElapsed[cueId] = cur;
        return cur;
    }

    /// <summary>Posición del playhead (segundos, absoluto): la más avanzada
    /// de los cues sonando, limitada al final de su bloque (con trim, el
    /// contador nunca debe salirse de él). null = nada sonando. Los
    /// FANTASMAS de un START no cuentan: comparten id con su cue real y
    /// teletransportarían el cursor al final del show.</summary>
    public double? CursorSeconds(IReadOnlyList<TimelineBlock> blocks, double clockNow)
    {
        double? cursor = null;
        foreach (var b in blocks)
            if (!b.Ghost && TryGetElapsed(b.Cue.Id, out double rawElapsed))
            {
                double elapsed = DrawnElapsed(b.Cue.Id, rawElapsed, clockNow);
                double lim = b.Dur > 0 ? b.Dur : elapsed;
                double pos = b.Start + Math.Min(Math.Max(0, elapsed), lim);
                cursor = Math.Max(cursor ?? 0, pos);
            }
        return cursor;
    }
}
