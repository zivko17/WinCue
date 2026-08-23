// =============================================================================
// TimelineGesture.cs — hit-test y gesto de arrastre del timeline, sin framework.
// =============================================================================
// Segunda pasada del traslado del timeline: la INTERACCIÓN deja de ser
// exclusiva de WinCue.App/TimelinePanel.cs. Aquí vive lo que un puntero hace
// sobre la proyección — qué bloque/recuadro hay bajo el punto, si un borde
// arma un redimensionado, y el estado de un arrastre en curso (umbral de
// 4 px, snap de 0,1 s, los topes de trim/duración) — traducido LITERALMENTE
// de HitBlock/HitSpan/HitSpanChip/EdgeModeAt/OnMouseMove de TimelinePanel.
// Un cambio aquí cambia cómo se agarra y suelta en los DOS productos.
//
// Lo ÚNICO que no puede vivir aquí es medir texto: el ancho del asa (chip) de
// un grupo depende de la etiqueta renderizada, y eso lo mide cada framework
// (FormattedText en WPF, CanvasTextLayout en Win2D). Por eso SpanChip recibe
// el ancho medido por callback y SpanChipRect hace el resto del cálculo.
//
// TimelineDragGesture es el estado del gesto que TimelinePanel llevaba en una
// docena de campos _drag*: se crea al agarrar (con los valores originales del
// cue), Update() lo mueve con los MISMOS topes de siempre, ToOverride() lo
// convierte en el TimelineDragOverride que Project() ya sabía pintar, y al
// soltar TimelineEditing.CommitDrag (ver TimelineEditing.cs) lo convierte en
// commits sobre el modelo.
// =============================================================================

using WinCue.Core;

namespace WinCue.Presentation;

/// <summary>Qué hay bajo un punto del timeline: prioridad asa de grupo →
/// bloque → cuerpo del recuadro, la misma que aplica el clic de WinCue.</summary>
public static class TimelineHitTest
{
    /// <summary>Alto del asa (chip) del recuadro de grupo, en píxeles.</summary>
    public const double ChipH = 16;

    /// <summary>Bloque bajo el punto (el pintado ENCIMA gana: se recorre la
    /// lista al revés). Los fantasmas no se agarran. Un instantáneo cuenta
    /// con su ancho mínimo dibujado (12 px).</summary>
    public static TimelineBlock? Block(IReadOnlyList<TimelineBlock> blocks,
        double x, double y, double pxPerSec, double scrollX, double layerHeight)
    {
        if (y < TimelineLayout.RulerH)
            return null;
        int layer = (int)((y - TimelineLayout.RulerH) / layerHeight);
        double sec = TimelineLayout.XToSec(x, pxPerSec, scrollX);
        for (int i = blocks.Count - 1; i >= 0; i--)
        {
            var b = blocks[i];
            if (b.Layer != layer || b.Ghost)   // los fantasmas no se editan
                continue;
            double wSec = Math.Max(b.Dur, 12 / pxPerSec);
            if (sec >= b.Start && sec <= b.Start + wSec)
                return b;
        }
        return null;
    }

    /// <summary>Recuadro de grupo bajo el punto (el más interno si hay
    /// anidados), con 3 px de margen a cada lado.</summary>
    public static TimelineSpan? Span(IReadOnlyList<TimelineSpan> spans,
        double x, double y, double pxPerSec, double scrollX, double layerHeight)
    {
        if (y < TimelineLayout.RulerH)
            return null;
        double sec = TimelineLayout.XToSec(x, pxPerSec, scrollX);
        int layer = (int)((y - TimelineLayout.RulerH) / layerHeight);
        TimelineSpan? best = null;
        foreach (var s in spans)
        {
            if (sec < s.Start - 3 / pxPerSec || sec > s.End + 3 / pxPerSec)
                continue;
            if (layer < s.MinLayer || layer > s.MaxLayer)
                continue;
            if (best is null || s.End - s.Start < best.End - best.Start)
                best = s;
        }
        return best;
    }

    /// <summary>Caja del asa (chip) del recuadro: nace en la esquina del
    /// marco, tan ancha como su etiqueta más el margen, sin desbordar el
    /// marco (mínimo 30 px). labelWidth = ancho del texto MEDIDO por el
    /// framework de quien pinta.</summary>
    public static TimelineRect SpanChipRect(TimelineSpan span,
        double pxPerSec, double scrollX, double layerHeight, double labelWidth)
    {
        var box = TimelineLayout.SpanBoxRect(span, pxPerSec, scrollX, layerHeight);
        double cw = Math.Min(labelWidth + 12, Math.Max(30, box.Width));
        return new TimelineRect(box.X, box.Y, cw, ChipH);
    }

    /// <summary>Asa de grupo bajo el punto (la del grupo más interno).</summary>
    public static TimelineSpan? SpanChip(IReadOnlyList<TimelineSpan> spans,
        double x, double y, double pxPerSec, double scrollX, double layerHeight,
        Func<TimelineSpan, double> labelWidth)
    {
        TimelineSpan? best = null;
        foreach (var s in spans)
        {
            var chip = SpanChipRect(s, pxPerSec, scrollX, layerHeight, labelWidth(s));
            if (x < chip.X || x > chip.X + chip.Width
                || y < chip.Y || y > chip.Y + chip.Height)
                continue;
            if (best is null || s.End - s.Start < best.End - best.Start)
                best = s;
        }
        return best;
    }

    /// <summary>Modo de arrastre según el punto: bordes (±5 px) = trim/duración.
    /// Izquierdo solo para media (desliza el punto de entrada); derecho para
    /// media (trim de salida) y WAIT/FADE (duración). Un bloque estrecho
    /// (&lt;18 px) solo se mueve — sus bordes no se distinguirían del cuerpo.</summary>
    public static TimelineDragMode EdgeMode(TimelineBlock? b,
        double x, double pxPerSec, double scrollX)
    {
        if (b is null || b.Dur <= 0)
            return TimelineDragMode.Move;
        double bx = TimelineLayout.SecToX(b.Start, pxPerSec, scrollX);
        double bw = Math.Max(4, b.Dur * pxPerSec);
        if (bw < 18)
            return TimelineDragMode.Move;
        bool media = b.Cue.IsMedia();
        bool durable = b.Cue.Type is CueType.Wait or CueType.Fade;
        if (media && Math.Abs(x - bx) <= 5)
            return TimelineDragMode.ResizeL;
        if ((media || durable) && Math.Abs(x - (bx + bw)) <= 5)
            return TimelineDragMode.ResizeR;
        return TimelineDragMode.Move;
    }
}

/// <summary>Un arrastre en curso sobre el timeline (mover en tiempo/pista, o
/// redimensionar por un borde). Se crea al agarrar, Update() lo sigue con los
/// topes de siempre, ToOverride() alimenta la vista previa de Project() y
/// TimelineEditing.CommitDrag lo convierte en commits al soltar.</summary>
public sealed class TimelineDragGesture
{
    /// <summary>Rejilla del arrastre horizontal, en segundos.</summary>
    public const double SnapSeconds = 0.1;

    public Cue Cue { get; }
    public bool IsGroup { get; }
    public TimelineDragMode Mode { get; }

    /// <summary>true en cuanto el puntero superó el umbral de 4 px: hasta
    /// entonces es un clic, no un arrastre, y no hay nada que previsualizar.</summary>
    public bool Moved { get; private set; }

    /// <summary>true si hubo gesto VERTICAL (media pista o más): solo
    /// entonces el soltar puede fijar la pista a mano.</summary>
    public bool LayerTouched { get; private set; }

    public double PreWait { get; private set; }
    public int Layer { get; private set; }
    public double TrimStart { get; private set; }
    public double TrimEnd { get; private set; }
    public double Duration { get; private set; }

    /// <summary>pre_wait del cue ANTES del gesto — la referencia contra la que
    /// se mide el desplazamiento al soltar.</summary>
    public double OrigPreWait { get; }

    /// <summary>Inicio proyectado de CADA cue antes del gesto: al soltar, los
    /// no arrastrados se recolocan para quedarse donde estaban (ver
    /// TimelineEditing.AnchorOthers).</summary>
    public IReadOnlyDictionary<int, double> Anchors { get; }

    private readonly double _startX, _startY;
    private readonly double _origTrimStart, _origTrimEnd, _origDuration, _origEff;

    /// <summary>Agarra <paramref name="cue"/> en (startX, startY).
    /// hitBlock = su bloque proyectado si el agarre fue sobre un bloque
    /// (null al agarrar un grupo por el asa o el marco): de él salen la fila
    /// donde SE VE (la vista previa no salta de pista sin gesto vertical) y
    /// la duración efectiva que limita el redimensionado izquierdo.</summary>
    public TimelineDragGesture(Cue cue, bool isGroup, TimelineDragMode mode,
        TimelineBlock? hitBlock, double startX, double startY,
        IReadOnlyDictionary<int, double> startById)
    {
        Cue = cue;
        IsGroup = isGroup;
        Mode = mode;
        _startX = startX;
        _startY = startY;
        OrigPreWait = cue.PreWait;
        PreWait = OrigPreWait;
        int origLayer = Math.Clamp(cue.TimelineLayer, -1, TimelineLayout.MaxLayers - 1);
        Layer = hitBlock?.Layer ?? origLayer;
        _origTrimStart = cue.TrimStart;
        _origTrimEnd = cue.TrimEnd > 0 ? cue.TrimEnd : cue.Duration;
        _origDuration = cue.Duration;
        _origEff = hitBlock?.Dur ?? 0;
        TrimStart = _origTrimStart;
        TrimEnd = _origTrimEnd;
        Duration = _origDuration;
        Anchors = new Dictionary<int, double>(startById);
    }

    /// <summary>Sigue el puntero. Antes del umbral de 4 px no hace nada
    /// (Moved sigue en false); después, aplica el desplazamiento con el snap
    /// y los topes de siempre según el modo.</summary>
    public void Update(double x, double y, double pxPerSec, double layerHeight)
    {
        if (!Moved && Math.Abs(x - _startX) < 4 && Math.Abs(y - _startY) < 4)
            return;
        Moved = true;

        double dSec = Math.Round(
            (x - _startX) / pxPerSec / SnapSeconds) * SnapSeconds;

        switch (Mode)
        {
            case TimelineDragMode.Move:
                // Se permite "negativo" en la vista previa: arrastrar sobre el
                // cue anterior = solapar (al soltar se encadena en paralelo)
                PreWait = OrigPreWait + dSec;
                // Vertical: fijar la pista (los grupos solo se mueven en tiempo)
                if (!IsGroup && Math.Abs(y - _startY) > layerHeight * 0.5)
                {
                    Layer = Math.Clamp(
                        (int)((y - TimelineLayout.RulerH) / layerHeight),
                        0, TimelineLayout.MaxLayers - 1);
                    LayerTouched = true;   // gesto vertical del usuario
                }
                break;

            case TimelineDragMode.ResizeL:
                // Deslizar el punto de entrada: el borde derecho no se mueve
                double dl = Math.Max(dSec,
                    Math.Max(-_origTrimStart, -OrigPreWait));
                dl = Math.Min(dl, _origEff - 0.1);
                PreWait = OrigPreWait + dl;
                TrimStart = _origTrimStart + dl;
                break;

            case TimelineDragMode.ResizeR:
                if (Cue.IsMedia())
                {
                    double ne = _origTrimEnd + dSec;
                    ne = Math.Max(ne, TrimStart + 0.1);
                    if (_origDuration > 0)
                        ne = Math.Min(ne, _origDuration);
                    TrimEnd = ne;
                }
                else
                {
                    Duration = Math.Max(0.1, _origDuration + dSec);
                }
                break;
        }
    }

    /// <summary>La vista previa del gesto, en el formato que
    /// TimelineLayout.Project() ya sabe pintar.</summary>
    public TimelineDragOverride ToOverride() => new()
    {
        CueId = Cue.Id,
        IsGroup = IsGroup,
        Mode = Mode,
        PreWait = PreWait,
        Layer = Layer,
        TrimStart = TrimStart,
        TrimEnd = TrimEnd,
        Duration = Duration,
    };
}
