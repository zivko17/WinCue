// =============================================================================
// TimelineGestureTests.cs — hit-test y gesto de arrastre del timeline
// (hitos-0-1, timeline interactivo).
// =============================================================================
// Fijan la interacción que TimelinePanel (WinCue.App) tiene HOY: qué bloque o
// recuadro se agarra bajo un punto (el pintado encima gana, los fantasmas no
// se editan), cuándo un borde arma un redimensionado (±5 px, nunca en bloques
// de <18 px), el umbral de 4 px que separa un clic de un arrastre, el snap de
// 0,1 s y los topes de trim/duración. Si un número cambia aquí, agarrar y
// soltar se siente distinto en los DOS productos.
//
// Los cues se arman con el helper C(...) — mismo idioma que
// TimelineLayoutTests; los bloques, a mano, porque el hit-test opera sobre la
// proyección ya calculada, no sobre el modelo.
// =============================================================================

using WinCue.Core;
using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class TimelineGestureTests
{
    private static Cue C(int id, CueType type = CueType.Audio,
        double preWait = 0, double duration = 0,
        double trimStart = 0, double trimEnd = 0, int timelineLayer = -1) => new()
    {
        Id = id,
        Type = type,
        PreWait = preWait,
        Duration = duration,
        TrimStart = trimStart,
        TrimEnd = trimEnd,
        TimelineLayer = timelineLayer,
    };

    private static TimelineBlock B(Cue cue, double start, double dur,
        int layer = 0, bool ghost = false) => new()
    {
        Cue = cue,
        Start = start,
        Dur = dur,
        Layer = layer,
        Ghost = ghost,
    };

    // Geometría de referencia de estos tests: 10 px/s, sin scroll, pistas de
    // 40 px. La pista L empieza en y = RulerH + L·40.
    private const double Px = 10, Lh = 40;
    private static double LayerY(int layer) => TimelineLayout.RulerH + layer * Lh + Lh / 2;

    // ═════════════════════════════ Hit-test ═════════════════════════════

    [Fact]
    public void EnLaReglaNoSeAgarraNada()
    {
        var blocks = new[] { B(C(1), 0, 10) };
        Assert.Null(TimelineHitTest.Block(blocks, 50, TimelineLayout.RulerH - 1, Px, 0, Lh));
    }

    [Fact]
    public void ElBloqueSeEncuentraPorPistaYTiempo()
    {
        var b0 = B(C(1), 0, 10, layer: 0);
        var b1 = B(C(2), 0, 10, layer: 1);
        var blocks = new[] { b0, b1 };
        Assert.Same(b1, TimelineHitTest.Block(blocks, 50, LayerY(1), Px, 0, Lh));
        Assert.Same(b0, TimelineHitTest.Block(blocks, 50, LayerY(0), Px, 0, Lh));
        Assert.Null(TimelineHitTest.Block(blocks, 150, LayerY(0), Px, 0, Lh));   // 15 s: pasado el final
    }

    [Fact]
    public void ElPintadoEncimaGanaYLosFantasmasNo()
    {
        var abajo = B(C(1), 0, 10);
        var encima = B(C(2), 5, 10);          // se pinta después: gana en el solape
        var fantasma = B(C(3), 5, 10, ghost: true);
        var blocks = new[] { abajo, encima, fantasma };
        Assert.Same(encima, TimelineHitTest.Block(blocks, 70, LayerY(0), Px, 0, Lh));
    }

    [Fact]
    public void UnInstantaneoSeAgarraPorSuAnchoMinimo()
    {
        // Dur 0 → se dibuja con 12 px: a 10 px/s son 1,2 s de huella clicable
        var b = B(C(1, CueType.Memo), 5, 0);
        var blocks = new[] { b };
        Assert.Same(b, TimelineHitTest.Block(blocks, 61, LayerY(0), Px, 0, Lh));   // 6,1 s
        Assert.Null(TimelineHitTest.Block(blocks, 63, LayerY(0), Px, 0, Lh));      // 6,3 s
    }

    [Fact]
    public void DelRecuadroGanaElMasInterno()
    {
        var externo = new TimelineSpan { Group = C(1, CueType.Group), Start = 0, End = 20, MinLayer = 0, MaxLayer = 1 };
        var interno = new TimelineSpan { Group = C(2, CueType.Group), Start = 5, End = 10, MinLayer = 0, MaxLayer = 0 };
        var spans = new[] { externo, interno };
        Assert.Same(interno, TimelineHitTest.Span(spans, 70, LayerY(0), Px, 0, Lh));
        Assert.Same(externo, TimelineHitTest.Span(spans, 150, LayerY(0), Px, 0, Lh));
        Assert.Same(externo, TimelineHitTest.Span(spans, 70, LayerY(1), Px, 0, Lh));   // fuera de las pistas del interno
    }

    [Fact]
    public void ElAsaDelGrupoMideSegunSuEtiqueta()
    {
        var span = new TimelineSpan { Group = C(1, CueType.Group), Start = 0, End = 30, MinLayer = 0, MaxLayer = 1 };
        var chip = TimelineHitTest.SpanChipRect(span, Px, 0, Lh, labelWidth: 50);
        var box = TimelineLayout.SpanBoxRect(span, Px, 0, Lh);
        Assert.Equal(box.X, chip.X);
        Assert.Equal(box.Y, chip.Y);
        Assert.Equal(62, chip.Width);              // etiqueta + 12, cabe en el marco
        Assert.Equal(TimelineHitTest.ChipH, chip.Height);

        // Una etiqueta más ancha que el marco se recorta a él
        var estrecho = new TimelineSpan { Group = C(2, CueType.Group), Start = 0, End = 5, MinLayer = 0, MaxLayer = 0 };
        var boxE = TimelineLayout.SpanBoxRect(estrecho, Px, 0, Lh);
        var chipE = TimelineHitTest.SpanChipRect(estrecho, Px, 0, Lh, labelWidth: 500);
        Assert.Equal(Math.Max(30, boxE.Width), chipE.Width);
    }

    [Fact]
    public void ElAsaSeAgarraYGanaLaDelGrupoMasInterno()
    {
        var externo = new TimelineSpan { Group = C(1, CueType.Group), Start = 0, End = 30, MinLayer = 0, MaxLayer = 1 };
        var interno = new TimelineSpan { Group = C(2, CueType.Group), Start = 0, End = 10, MinLayer = 0, MaxLayer = 0 };
        var spans = new[] { externo, interno };
        // Los dos chips nacen en la misma esquina: gana el interno
        double y = TimelineLayout.RulerH + 5;
        Assert.Same(interno, TimelineHitTest.SpanChip(spans, 5, y, Px, 0, Lh, _ => 50));
        Assert.Null(TimelineHitTest.SpanChip(spans, 5, y + TimelineHitTest.ChipH + 20, Px, 0, Lh, _ => 50));
    }

    [Fact]
    public void LosBordesArmanElRedimensionado()
    {
        var media = B(C(1, CueType.Audio, duration: 10), 5, 10);   // [50, 150] px
        Assert.Equal(TimelineDragMode.ResizeL, TimelineHitTest.EdgeMode(media, 52, Px, 0));
        Assert.Equal(TimelineDragMode.ResizeR, TimelineHitTest.EdgeMode(media, 148, Px, 0));
        Assert.Equal(TimelineDragMode.Move, TimelineHitTest.EdgeMode(media, 100, Px, 0));

        // WAIT/FADE: solo el borde derecho (duración); el izquierdo mueve
        var wait = B(C(2, CueType.Wait, duration: 10), 5, 10);
        Assert.Equal(TimelineDragMode.Move, TimelineHitTest.EdgeMode(wait, 52, Px, 0));
        Assert.Equal(TimelineDragMode.ResizeR, TimelineHitTest.EdgeMode(wait, 148, Px, 0));
    }

    [Fact]
    public void UnBloqueEstrechoSoloSeMueve()
    {
        // 1,5 s a 10 px/s = 15 px < 18: los bordes no se distinguen del cuerpo
        var b = B(C(1, CueType.Audio, duration: 1.5), 5, 1.5);
        Assert.Equal(TimelineDragMode.Move, TimelineHitTest.EdgeMode(b, 51, Px, 0));
        Assert.Equal(TimelineDragMode.Move, TimelineHitTest.EdgeMode(b, 64, Px, 0));
        Assert.Equal(TimelineDragMode.Move, TimelineHitTest.EdgeMode(null, 0, Px, 0));
    }

    // ═════════════════════════ Gesto de arrastre ═════════════════════════

    private static TimelineDragGesture Grab(Cue cue, TimelineBlock? hit,
        TimelineDragMode mode = TimelineDragMode.Move, bool isGroup = false,
        double startX = 100, double startY = 40) =>
        new(cue, isGroup, mode, hit, startX, startY, new Dictionary<int, double>());

    [Fact]
    public void BajoElUmbralEsUnClicNoUnArrastre()
    {
        var cue = C(1, preWait: 2);
        var g = Grab(cue, B(cue, 2, 10));
        g.Update(103, 42, Px, Lh);   // 3 px: nada
        Assert.False(g.Moved);
        Assert.Equal(2, g.PreWait);
    }

    [Fact]
    public void MoverEnHorizontalDesplazaElPreWaitConSnap()
    {
        var cue = C(1, preWait: 2);
        var g = Grab(cue, B(cue, 2, 10));
        g.Update(127, 40, Px, Lh);   // +27 px = +2,7 s exactos con el snap de 0,1
        Assert.True(g.Moved);
        Assert.Equal(2 + 2.7, g.PreWait, 9);
        // La vista previa admite negativo (solapar con el anterior)
        g.Update(100 - 45, 40, Px, Lh);
        Assert.Equal(2 - 4.5, g.PreWait, 9);
    }

    [Fact]
    public void LaPistaSoloCambiaConGestoVertical()
    {
        var cue = C(1);
        var g = Grab(cue, B(cue, 0, 10, layer: 1));
        Assert.Equal(1, g.Layer);   // arranca en la fila donde SE VE

        g.Update(130, 45, Px, Lh);  // horizontal: menos de media pista vertical
        Assert.False(g.LayerTouched);
        Assert.Equal(1, g.Layer);

        g.Update(130, TimelineLayout.RulerH + 3 * Lh + 10, Px, Lh);   // a la pista 3
        Assert.True(g.LayerTouched);
        Assert.Equal(3, g.Layer);
    }

    [Fact]
    public void UnGrupoSoloSeMueveEnElTiempo()
    {
        var grupo = C(1, CueType.Group);
        var g = Grab(grupo, null, isGroup: true);
        g.Update(130, 300, Px, Lh);
        Assert.False(g.LayerTouched);
        g.Update(130, 40, Px, Lh);
        Assert.Equal(3.0, g.PreWait - 0, 9);   // +30 px = +3 s sobre preWait 0
    }

    [Fact]
    public void ElBordeIzquierdoDeslizaLaEntradaConSusTopes()
    {
        // trim_start 1, pre_wait 2, huella efectiva 9 s
        var cue = C(1, CueType.Audio, preWait: 2, duration: 10, trimStart: 1);
        var g = Grab(cue, B(cue, 2, 9), TimelineDragMode.ResizeL);

        g.Update(120, 40, Px, Lh);   // +2 s: entra más tarde
        Assert.Equal(4, g.PreWait, 9);
        Assert.Equal(3, g.TrimStart, 9);

        // Hacia la izquierda no puede pasar del trim 0 NI del pre_wait 0
        g.Update(100 - 500, 40, Px, Lh);
        Assert.Equal(1, g.PreWait, 9);      // 2 + (-1): manda -trimStart
        Assert.Equal(0, g.TrimStart, 9);

        // Y hacia la derecha deja al menos 0,1 s de bloque
        g.Update(100 + 500, 40, Px, Lh);
        Assert.Equal(1 + (9 - 0.1), g.TrimStart, 9);
    }

    [Fact]
    public void ElBordeDerechoDeUnMediaRecortaConSusTopes()
    {
        var cue = C(1, CueType.Audio, duration: 10, trimEnd: 8);
        var g = Grab(cue, B(cue, 0, 8), TimelineDragMode.ResizeR);

        g.Update(110, 40, Px, Lh);   // +1 s
        Assert.Equal(9, g.TrimEnd, 9);

        g.Update(100 + 500, 40, Px, Lh);   // no puede pasar de la duración real
        Assert.Equal(10, g.TrimEnd, 9);

        g.Update(100 - 500, 40, Px, Lh);   // ni dejar menos de 0,1 s de bloque
        Assert.Equal(0.1, g.TrimEnd, 9);
    }

    [Fact]
    public void ElBordeDerechoDeUnWaitCambiaSuDuracion()
    {
        var cue = C(1, CueType.Wait, duration: 5);
        var g = Grab(cue, B(cue, 0, 5), TimelineDragMode.ResizeR);
        g.Update(130, 40, Px, Lh);
        Assert.Equal(8, g.Duration, 9);
        g.Update(100 - 500, 40, Px, Lh);   // nunca por debajo de 0,1 s
        Assert.Equal(0.1, g.Duration, 9);
    }

    [Fact]
    public void ElOverrideLlevaLaVistaPreviaExacta()
    {
        var cue = C(7, preWait: 1);
        var g = Grab(cue, B(cue, 1, 10, layer: 2));
        g.Update(150, 40, Px, Lh);
        var o = g.ToOverride();
        Assert.Equal(7, o.CueId);
        Assert.False(o.IsGroup);
        Assert.Equal(TimelineDragMode.Move, o.Mode);
        Assert.Equal(g.PreWait, o.PreWait);
        Assert.Equal(2, o.Layer);
    }

    [Fact]
    public void LasAnclasSonUnaFotoDelInicioDelGesto()
    {
        var startById = new Dictionary<int, double> { [1] = 0, [2] = 5 };
        var cue = C(1);
        var g = new TimelineDragGesture(cue, false, TimelineDragMode.Move,
            B(cue, 0, 5), 100, 40, startById);
        startById[2] = 99;   // el modelo sigue moviéndose; el ancla no
        Assert.Equal(5, g.Anchors[2]);
    }
}
