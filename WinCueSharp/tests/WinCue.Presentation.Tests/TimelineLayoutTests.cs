// =============================================================================
// TimelineLayoutTests.cs — red del cálculo del timeline (hitos-0-1, timeline).
// =============================================================================
// Fijan los valores que TimelineCanvas.cs (WinCue.App) tiene HOY: mapeo
// tiempo↔píxel, marcas de la regla según la escala, colocación de bloques
// (secuencia, grupos, capas), posición del playhead y detección de
// solapamiento (avisos de fundido). Si alguien cambia un número aquí, el
// timeline se ve distinto en pantalla en los DOS productos — que es
// exactamente lo que este archivo existe para impedir por accidente.
//
// Los cues de prueba se arman con el helper C(...) de más abajo: valores
// mínimos explícitos, todo lo demás a los defaults reales de Cue.cs.
// =============================================================================

using WinCue.Controller;
using WinCue.Core;
using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class TimelineLayoutTests
{
    private static Cue C(
        int id, CueType type = CueType.Audio, int? parentId = null,
        double preWait = 0, double postWait = 0, double duration = 0,
        ContinueMode continueMode = ContinueMode.None, bool armed = true,
        int timelineLayer = -1, string color = "", int? targetId = null,
        GroupMode groupMode = GroupMode.Enter, double trimStart = 0, double trimEnd = 0,
        double volume = 1.0, bool loop = false, bool fadeStopOnDone = true,
        string name = "") => new()
    {
        Id = id,
        Type = type,
        Name = name,
        ParentId = parentId,
        PreWait = preWait,
        PostWait = postWait,
        Duration = duration,
        ContinueMode = continueMode,
        Armed = armed,
        TimelineLayer = timelineLayer,
        Color = color,
        TargetId = targetId,
        GroupMode = groupMode,
        TrimStart = trimStart,
        TrimEnd = trimEnd,
        Volume = volume,
        Loop = loop,
        FadeStopOnDone = fadeStopOnDone,
    };

    private static CueModel Model(params Cue[] cues)
    {
        var m = new CueModel();
        foreach (var c in cues)
            m.AddCue(c);
        return m;
    }

    // ═════════════════════════ Mapeo tiempo ↔ píxel ═════════════════════════

    [Fact]
    public void SecToXEsLinealConElZoomYElScroll()
    {
        Assert.Equal(200.0, TimelineLayout.SecToX(10, pxPerSec: 20, scrollX: 0));
        Assert.Equal(180.0, TimelineLayout.SecToX(10, pxPerSec: 20, scrollX: 20));
    }

    [Fact]
    public void XToSecEsLaInversaDeSecToX()
    {
        double x = TimelineLayout.SecToX(37.5, pxPerSec: 13, scrollX: 42);
        Assert.Equal(37.5, TimelineLayout.XToSec(x, pxPerSec: 13, scrollX: 42), precision: 9);
    }

    // ═════════════════════════ Pistas ═════════════════════════

    [Fact]
    public void LayerHeightRepartaElAltoDisponibleMenosLaRegla()
    {
        // (200 - 20) / 3 = 60
        Assert.Equal(60.0, TimelineLayout.LayerHeight(200, visibleLayers: 3));
    }

    [Fact]
    public void LayerHeightTieneUnSueloDeVeinticuatroPixeles()
    {
        Assert.Equal(24.0, TimelineLayout.LayerHeight(actualHeight: 10, visibleLayers: 8));
    }

    [Fact]
    public void VisibleLayersEsLaMasAltaUsadaMasDosConSueloYTecho()
    {
        var blocks = new List<TimelineBlock>
        {
            new() { Cue = C(1), Layer = 1 },
        };
        Assert.Equal(3, TimelineLayout.VisibleLayers(blocks)); // suelo: MinLayers
    }

    [Fact]
    public void VisibleLayersNoPasaDelTechoDeOchoPistas()
    {
        var blocks = new List<TimelineBlock> { new() { Cue = C(1), Layer = 7 } };
        Assert.Equal(8, TimelineLayout.VisibleLayers(blocks)); // 7+2=9, techo 8
    }

    // ═════════════════════════ Regla de tiempo ═════════════════════════

    [Theory]
    [InlineData(640, 0.1)]   // 0.1 s * 640 px/s = 64
    [InlineData(320, 0.2)]
    [InlineData(128, 0.5)]
    [InlineData(64, 1)]
    [InlineData(10, 10)]     // 1 s * 10 px/s = 10 < 64; 10 s * 10 px/s = 100 >= 64
    [InlineData(1, 120)]
    [InlineData(0.02, 3600)] // zoom mínimo real del timeline
    public void RulerStepEligeElPrimerPasoQueLlegaASesentaYCuatroPixeles(double pxPerSec, double esperado)
    {
        Assert.Equal(esperado, TimelineLayout.RulerStep(pxPerSec));
    }

    [Fact]
    public void RulerStepSeQuedaEnElUltimoPasoSiNadaLlegaASesentaYCuatro()
    {
        Assert.Equal(14400, TimelineLayout.RulerStep(0.001));
    }

    [Fact]
    public void RulerTicksEmpiezaEnCeroYRespetaElPaso()
    {
        var ticks = TimelineLayout.RulerTicks(pxPerSec: 20, scrollX: 0, width: 100).ToList();
        // step = 5 (5*20=100 >= 64); SecToX(5)=100 no es < 100: solo cabe t=0
        Assert.Equal(new[] { 0.0 }, ticks);
    }

    [Fact]
    public void RulerTicksCabenVariasMarcasEnUnLienzoMasAncho()
    {
        var ticks = TimelineLayout.RulerTicks(pxPerSec: 20, scrollX: 0, width: 220).ToList();
        Assert.Equal(new[] { 0.0, 5.0, 10.0 }, ticks);
    }

    [Fact]
    public void RulerTicksArrancaAntesDeCeroSiElScrollLoPide()
    {
        // pxPerSec=20 → step=5; scrollX=60 → XToSec(0)=3 → firstTick=floor(3/5)*5=0
        // con scrollX=110 → XToSec(0)=5.5 → firstTick=5
        var ticks = TimelineLayout.RulerTicks(pxPerSec: 20, scrollX: 110, width: 100).ToList();
        Assert.Equal(5.0, ticks[0]);
    }

    [Theory]
    [InlineData(0.4, "0.4s")]
    [InlineData(59.9, "59.9s")]
    [InlineData(60, "1:00")]   // "00.#" sin parte fraccionaria no imprime el punto
    [InlineData(90.5, "1:30.5")]
    [InlineData(3599, "59:59")]
    public void FormatTimePorDebajoDeLaHora(double s, string esperado)
    {
        // FormatTime interpola sin fijar cultura (igual que el FmtTime
        // original): el separador decimal depende de CurrentCulture. Se fija
        // Invariant aquí para que el test sea determinista sin cambiar el
        // comportamiento real (que SIGUE dependiendo del hilo en producción,
        // tal cual hacía TimelineCanvas.FmtTime).
        var prev = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(esperado, TimelineLayout.FormatTime(s));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = prev;
        }
    }

    [Fact]
    public void FormatTimeAPartirDeLaHoraUsaHMmSs()
    {
        // 1h 2m 3s = 3723 s
        Assert.Equal("1:02:03", TimelineLayout.FormatTime(3723));
    }

    // ═════════════════════════ Proyección: secuencia simple ═════════════════

    [Fact]
    public void UnCueSueltoEmpiezaEnCeroMasSuPreWait()
    {
        var model = Model(C(1, preWait: 2, duration: 5));
        var proj = TimelineLayout.Project(model);
        var b = Assert.Single(proj.Blocks);
        Assert.Equal(2.0, b.Start, precision: 9);
        Assert.Equal(5.0, b.Dur, precision: 9);
        Assert.Equal(2.0, proj.StartById[1], precision: 9);
    }

    [Fact]
    public void GoManualProyectaElSiguienteAlFinalYMarcaUnGoMark()
    {
        var model = Model(
            C(1, duration: 4, continueMode: ContinueMode.None),
            C(2, duration: 3));
        var proj = TimelineLayout.Project(model);
        Assert.Equal(0.0, proj.Blocks[0].Start, precision: 9);
        Assert.Equal(4.0, proj.Blocks[1].Start, precision: 9); // dispara al fin del anterior
        Assert.Equal(new[] { 4.0 }, proj.GoMarks);
    }

    [Fact]
    public void AutoContinueDisparaTrasElPostWaitDesdeElInicio()
    {
        var model = Model(
            C(1, duration: 4, postWait: 1, continueMode: ContinueMode.AutoContinue),
            C(2, duration: 2));
        var proj = TimelineLayout.Project(model);
        // trigger = actionStart(0) + postWait(1) = 1
        Assert.Equal(1.0, proj.Blocks[1].Start, precision: 9);
        Assert.Empty(proj.GoMarks);
    }

    [Fact]
    public void AutoFollowDisparaCuandoElAnteriorTermina()
    {
        var model = Model(
            C(1, duration: 4, continueMode: ContinueMode.AutoFollow),
            C(2, duration: 2));
        var proj = TimelineLayout.Project(model);
        Assert.Equal(4.0, proj.Blocks[1].Start, precision: 9);
        Assert.Empty(proj.GoMarks);
    }

    [Fact]
    public void UnCueDesarmadoNoAvanzaElDisparoDelSiguiente()
    {
        var model = Model(
            C(1, duration: 4, armed: false, continueMode: ContinueMode.AutoFollow),
            C(2, duration: 2));
        var proj = TimelineLayout.Project(model);
        // cue.Armed ? end : start → un desarmado "termina" donde empezó
        Assert.Equal(0.0, proj.Blocks[1].Start, precision: 9);
        Assert.True(proj.Blocks[0].Dimmed);
    }

    // ═════════════════════════ Proyección: grupos ═════════════════════════

    [Fact]
    public void GrupoTimelineDisparaTodosLosHijosALaVez()
    {
        var group = C(10, type: CueType.Group, groupMode: GroupMode.Timeline, preWait: 1);
        var model = Model(group,
            C(11, parentId: 10, duration: 3, preWait: 0),
            C(12, parentId: 10, duration: 5, preWait: 0.5));
        var proj = TimelineLayout.Project(model);
        var b11 = proj.Blocks.Single(b => b.Cue.Id == 11);
        var b12 = proj.Blocks.Single(b => b.Cue.Id == 12);
        Assert.Equal(1.0, b11.Start, precision: 9);   // start del grupo + su pre_wait (0)
        Assert.Equal(1.5, b12.Start, precision: 9);   // + su pre_wait (0.5)
        var span = Assert.Single(proj.Spans);
        Assert.Equal(1.0, span.Start, precision: 9);
        Assert.Equal(6.5, span.End, precision: 9);    // max(1+3, 1.5+5) = 6.5
    }

    [Fact]
    public void GrupoEnterEncadenaSusHijosComoUnaSecuencia()
    {
        var group = C(10, type: CueType.Group, groupMode: GroupMode.Enter);
        var model = Model(group,
            C(11, parentId: 10, duration: 2, continueMode: ContinueMode.AutoFollow),
            C(12, parentId: 10, duration: 3));
        var proj = TimelineLayout.Project(model);
        var b11 = proj.Blocks.Single(b => b.Cue.Id == 11);
        var b12 = proj.Blocks.Single(b => b.Cue.Id == 12);
        Assert.Equal(0.0, b11.Start, precision: 9);
        Assert.Equal(2.0, b12.Start, precision: 9); // AutoFollow: al terminar el anterior
    }

    [Fact]
    public void GrupoGoNextSoloDisparaElPrimeroYApagaLosDemas()
    {
        var group = C(10, type: CueType.Group, groupMode: GroupMode.GoNext);
        var model = Model(group,
            C(11, parentId: 10, duration: 2),
            C(12, parentId: 10, duration: 3));
        var proj = TimelineLayout.Project(model);
        var b12 = proj.Blocks.Single(b => b.Cue.Id == 12);
        Assert.True(b12.Dimmed); // rama que no se disparará (i > 0 en GoNext)
    }

    // ═════════════════════════ Capas: manual y auto-apilado ═════════════════

    [Fact]
    public void DosCuesQueSeSolapanSeApilanEnPistasDistintas()
    {
        // Grupo TIMELINE: los dos hijos arrancan a la vez (solape real) —
        // el auto-apilado greedy de AssignLayers debe separarlos de pista.
        var group = C(20, type: CueType.Group, groupMode: GroupMode.Timeline);
        var model = Model(group,
            C(21, parentId: 20, duration: 10),
            C(22, parentId: 20, duration: 5));
        var proj = TimelineLayout.Project(model);
        var b21 = proj.Blocks.Single(b => b.Cue.Id == 21);
        var b22 = proj.Blocks.Single(b => b.Cue.Id == 22);
        Assert.NotEqual(b21.Layer, b22.Layer);
    }

    [Fact]
    public void LaCapaFijadaAManoSeRespetaSiempre()
    {
        var group = C(20, type: CueType.Group, groupMode: GroupMode.Timeline);
        var model = Model(group,
            C(21, parentId: 20, duration: 10, timelineLayer: 5),
            C(22, parentId: 20, duration: 5));
        var proj = TimelineLayout.Project(model);
        var b21 = proj.Blocks.Single(b => b.Cue.Id == 21);
        Assert.Equal(5, b21.Layer);
        Assert.True(b21.ManualLayer);
    }

    [Fact]
    public void DosCuesQueNoSeSolapanComparteLaMismaCapaAuto()
    {
        var model = Model(
            C(1, duration: 4),
            C(2, duration: 3)); // GO manual: empieza cuando el 1 termina
        var proj = TimelineLayout.Project(model);
        Assert.Equal(proj.Blocks[0].Layer, proj.Blocks[1].Layer);
    }

    // ═════════════════════════ Cues de control: START / STOP / FADE ═════════

    [Fact]
    public void UnStartAñadeUnBloqueFantasmaEnSuInstante()
    {
        var target = C(1, duration: 10, armed: true);
        var start = C(2, type: CueType.Start, targetId: 1, preWait: 3);
        var model = Model(target, start);
        var proj = TimelineLayout.Project(model);
        var ghost = proj.Blocks.Single(b => b.Ghost);
        Assert.Equal(1, ghost.Cue.Id);
        // El target (cue 1) ocupa [0,10); el START es el SIGUIENTE cue de la
        // secuencia raíz (GO manual): dispara al fin del target (10) más su
        // propio pre_wait (3) = 13; el fantasma nace ahí, con el pre_wait del
        // TARGET sumado (0 en este caso).
        Assert.Equal(13.0, ghost.Start, precision: 9);
    }

    [Fact]
    public void UnStopTruncaElBloqueDeSuObjetivo()
    {
        // Un grupo TIMELINE dispara al target y al STOP a la vez (cada uno a
        // su pre_wait): así el STOP cae DENTRO del bloque que corta.
        var group = C(10, type: CueType.Group, groupMode: GroupMode.Timeline);
        var model = Model(group,
            C(1, parentId: 10, duration: 10),
            C(2, parentId: 10, type: CueType.Stop, targetId: 1, preWait: 4));
        var proj = TimelineLayout.Project(model);
        var block = proj.Blocks.Single(b => !b.Ghost && b.Cue.Id == 1);
        Assert.Equal(4.0, block.CutAt);
    }

    [Fact]
    public void UnFadeConParadaAlTerminarTruncaElBloque()
    {
        var group = C(10, type: CueType.Group, groupMode: GroupMode.Timeline);
        var model = Model(group,
            C(1, parentId: 10, duration: 10),
            C(2, parentId: 10, type: CueType.Fade, targetId: 1, preWait: 2, duration: 3,
                fadeStopOnDone: true));
        var proj = TimelineLayout.Project(model);
        var block = proj.Blocks.Single(b => !b.Ghost && b.Cue.Id == 1);
        Assert.Equal(2.0, block.RampStart, precision: 9);
        Assert.Equal(3.0, block.RampLen, precision: 9);
        Assert.Equal(5.0, block.CutAt); // preWait(2) + duration(3)
    }

    [Fact]
    public void UnFadeAVolumenCeroSinPararDejaElRestoEnSilencio()
    {
        var group = C(10, type: CueType.Group, groupMode: GroupMode.Timeline);
        var model = Model(group,
            C(1, parentId: 10, duration: 10),
            C(2, parentId: 10, type: CueType.Fade, targetId: 1, preWait: 2, duration: 3,
                fadeStopOnDone: false, volume: 0.0));
        var proj = TimelineLayout.Project(model);
        var block = proj.Blocks.Single(b => !b.Ghost && b.Cue.Id == 1);
        Assert.True(block.RampToSilence);
        Assert.Null(block.CutAt);
    }

    // ═════════════════════════ Solapamiento / avisos de fundido ═════════════

    [Fact]
    public void UnCrossfadeQueNoCabeEnElSalienteQuedaMarcado()
    {
        // Dos vídeos en la MISMA capa (fijada a mano), dentro de un grupo
        // TIMELINE para que se solapen de verdad: el segundo entra en t=2,
        // cuando al primero (duración 5) le quedan 3 s; su fade_in de 4 s no
        // cabe en ese hueco -> CrossfadeTooLong.
        var group = C(10, type: CueType.Group, groupMode: GroupMode.Timeline);
        var model = Model(group,
            new Cue { Id = 1, Type = CueType.Video, ParentId = 10, Duration = 5, TimelineLayer = 0 },
            new Cue { Id = 2, Type = CueType.Video, ParentId = 10, Duration = 5, TimelineLayer = 0, PreWait = 2, FadeIn = 4 });
        var proj = TimelineLayout.Project(model);
        var b2 = proj.Blocks.Single(b => b.Cue.Id == 2);
        Assert.NotNull(b2.Headroom);
        Assert.Equal(3.0, b2.Headroom!.Value, precision: 9);
        Assert.True(b2.Issues.HasFlag(FadeIssues.CrossfadeTooLong));
    }

    [Fact]
    public void SinIncoherenciasNoHayAviso()
    {
        var model = Model(C(1, duration: 5));
        var proj = TimelineLayout.Project(model);
        Assert.Equal(FadeIssues.None, proj.Blocks[0].Issues);
    }

    // ═════════════════════════ TotalSeconds ═════════════════════════

    [Fact]
    public void TotalSecondsEsElFinDelBloqueMasTardio()
    {
        var model = Model(C(1, duration: 4), C(2, duration: 6));
        var proj = TimelineLayout.Project(model);
        Assert.Equal(10.0, TimelineLayout.TotalSeconds(proj.Blocks), precision: 9);
    }

    [Fact]
    public void UnCueInstantaneoCuentaConLaHuellaMinima()
    {
        var model = Model(C(1, type: CueType.Memo, duration: 0));
        var proj = TimelineLayout.Project(model);
        Assert.Equal(TimelineLayout.InstantFootprint, TimelineLayout.TotalSeconds(proj.Blocks), precision: 9);
    }

    // ═════════════════════════ Geometría de bloque ═════════════════════════

    [Fact]
    public void BlockGeometryUsaLaAnchuraMinimaDeCuatroPixeles()
    {
        var b = new TimelineBlock { Cue = C(1), Start = 0, Dur = 0.01, Layer = 0 };
        var g = TimelineLayout.BlockGeometry(b, pxPerSec: 20, scrollX: 0, layerHeight: 60);
        Assert.Equal(4.0, g.Rect.Width);
    }

    [Fact]
    public void BlockGeometryUnBloqueInstantaneoMideDoceInstantaneo()
    {
        var b = new TimelineBlock { Cue = C(1), Start = 2, Dur = 0, Layer = 1 };
        var g = TimelineLayout.BlockGeometry(b, pxPerSec: 20, scrollX: 0, layerHeight: 60);
        Assert.True(g.Instant);
        Assert.Equal(12.0, g.Rect.Width);
        // y = RulerH + layer*layerHeight + 4 = 20 + 60 + 4 = 84
        Assert.Equal(84.0, g.Rect.Y, precision: 9);
        Assert.Equal(52.0, g.Rect.Height, precision: 9); // layerHeight - 8
    }

    [Fact]
    public void BlockGeometryRespetaElCorteDeUnStopEnElAnchoDibujado()
    {
        var b = new TimelineBlock { Cue = C(1), Start = 0, Dur = 10, Layer = 0, CutAt = 4 };
        var g = TimelineLayout.BlockGeometry(b, pxPerSec: 10, scrollX: 0, layerHeight: 60);
        Assert.Equal(4.0, g.DrawnDuration, precision: 9);
        Assert.Equal(40.0, g.Rect.Width, precision: 9); // 4 s * 10 px/s
    }

    [Fact]
    public void SpanBoxRectAbrazaDesdeMinLayerHastaMaxLayer()
    {
        var s = new TimelineSpan { Group = C(10, type: CueType.Group), Start = 0, End = 5, MinLayer = 1, MaxLayer = 2 };
        var r = TimelineLayout.SpanBoxRect(s, pxPerSec: 10, scrollX: 0, layerHeight: 60);
        // y = RulerH + 1*60 + 1 = 81; bh = (2-1+1)*60 - 2 = 118
        Assert.Equal(81.0, r.Y, precision: 9);
        Assert.Equal(118.0, r.Height, precision: 9);
    }

    // ═════════════════════════ Etiquetas ═════════════════════════

    [Fact]
    public void BlockLabelTextUneNumeroYNombre()
    {
        var cue = C(1, name: "Ambiente");
        cue.Number = "1";
        var b = new TimelineBlock { Cue = cue };
        Assert.Equal("1 · Ambiente", TimelineLayout.BlockLabelText(b));
    }

    [Fact]
    public void BlockLabelTextMarcaLosFantasmasConElIconoDeSalto()
    {
        var cue = C(1, name: "X");
        cue.Number = "1";
        var b = new TimelineBlock { Cue = cue, Ghost = true };
        Assert.StartsWith(Icons.Jump, TimelineLayout.BlockLabelText(b));
    }

    [Fact]
    public void BlockLabelTextMarcaLaPistaFijadaAMano()
    {
        var cue = C(1, name: "X", timelineLayer: 2);
        cue.Number = "1";
        var b = new TimelineBlock { Cue = cue, ManualLayer = true };
        Assert.StartsWith(Icons.Pin, TimelineLayout.BlockLabelText(b));
    }

    // ═════════════════════════ Colores (Palette, sin drift) ═════════════════

    [Fact]
    public void ForCueTypeSaleDePaletteParaCadaTipo()
    {
        Assert.Equal(Palette.TypeAudio, TimelinePalette.ForCueType(CueType.Audio));
        Assert.Equal(Palette.TypeVideo, TimelinePalette.ForCueType(CueType.Video));
        Assert.Equal(Palette.TypeWait, TimelinePalette.ForCueType(CueType.Wait));
        Assert.Equal(Palette.Warn, TimelinePalette.ForCueType(CueType.Fade));
        Assert.Equal(Palette.Go, TimelinePalette.ForCueType(CueType.Start));
        Assert.Equal(Palette.Pgm, TimelinePalette.ForCueType(CueType.Stop));
        Assert.Equal(Palette.TypeScript, TimelinePalette.ForCueType(CueType.Script));
        Assert.Equal(Palette.TypeMemo, TimelinePalette.ForCueType(CueType.Memo));
        Assert.Equal(Palette.Standby, TimelinePalette.ForCueType(CueType.Group));
    }

    [Fact]
    public void ResolveCueColorPrefiereElColorManualDelCue()
    {
        var cue = C(1, color: "red");
        Assert.Equal(Palette.Pgm, TimelinePalette.ResolveCueColor(cue)); // #c0392b == Palette.Pgm
    }

    [Fact]
    public void ResolveCueColorCaeAlTipoSinColorManual()
    {
        var cue = C(1, type: CueType.Video, color: "");
        Assert.Equal(Palette.TypeVideo, TimelinePalette.ResolveCueColor(cue));
    }

    [Fact]
    public void DarkenReduceCadaCanalPorElFactor()
    {
        uint darkened = TimelinePalette.Darken(0xffffff, 0.5);
        Assert.Equal(0x7f7f7fu, darkened);
    }

    // ═════════════════════════ Playhead ═════════════════════════

    [Fact]
    public void CursorSecondsEsNullSinCuesActivos()
    {
        var proj = TimelineLayout.Project(Model(C(1, duration: 5)));
        var playhead = new TimelinePlayhead();
        Assert.Null(playhead.CursorSeconds(proj.Blocks, clockNow: 0));
    }

    [Fact]
    public void CursorSecondsUsaElElapsedDelCueActivo()
    {
        var model = Model(C(1, duration: 5));
        var proj = TimelineLayout.Project(model);
        var playhead = new TimelinePlayhead();
        playhead.SetActive(new[]
        {
            new ActiveCueInfo(1, "1", "X", CueType.Audio, "", Elapsed: 2.0, Remaining: 3.0, Fraction: 0.4),
        }, now: 100.0);
        // Sin avance detectado en el primer tick (no había Elapsed previo por
        // encima): DrawnElapsed persigue el objetivo desde su propio valor.
        double? cursor = playhead.CursorSeconds(proj.Blocks, clockNow: 100.0);
        Assert.NotNull(cursor);
        Assert.True(cursor >= 0 && cursor <= 5);
    }

    [Fact]
    public void CursorSecondsSeLimitaAlFinalDelBloque()
    {
        var model = Model(C(1, duration: 5));
        var proj = TimelineLayout.Project(model);
        var playhead = new TimelinePlayhead();
        playhead.SetActive(new[]
        {
            new ActiveCueInfo(1, "1", "X", CueType.Audio, "", Elapsed: 5.0, Remaining: 0.0, Fraction: 1.0),
        }, now: 100.0);
        playhead.SetActive(new[]
        {
            new ActiveCueInfo(1, "1", "X", CueType.Audio, "", Elapsed: 5.0, Remaining: 0.0, Fraction: 1.0),
        }, now: 100.0);
        double? cursor = playhead.CursorSeconds(proj.Blocks, clockNow: 100.0);
        Assert.NotNull(cursor);
        Assert.True(cursor <= 5.0 + 1e-6);
    }

    [Fact]
    public void UnCueEnPreWaitNoCuentaParaElCursor()
    {
        var model = Model(C(1, preWait: 2, duration: 5));
        var proj = TimelineLayout.Project(model);
        var playhead = new TimelinePlayhead();
        playhead.SetActive(new[]
        {
            new ActiveCueInfo(1, "1", "X", CueType.Audio, "", Elapsed: 0.0, Remaining: null, Fraction: null,
                PreWaitRemaining: 1.0),
        }, now: 0.0);
        Assert.Null(playhead.CursorSeconds(proj.Blocks, clockNow: 0.0));
    }

    // ═════════════════════════ Override de arrastre (WinCue en vivo) ═════════

    [Fact]
    public void ConOverrideDeArrastreMoverCambiaElPreWaitEfectivo()
    {
        var model = Model(C(1, preWait: 0, duration: 5));
        var drag = new TimelineDragOverride
        {
            CueId = 1, IsGroup = false, Mode = TimelineDragMode.Move,
            PreWait = 3.0, Layer = 0, TrimStart = 0, TrimEnd = 0, Duration = 5,
        };
        var proj = TimelineLayout.Project(model, drag);
        Assert.Equal(3.0, proj.Blocks[0].Start, precision: 9);
    }

    [Fact]
    public void ConOverrideDeArrastreResizeRCambiaLaDuracionDeUnWait()
    {
        var model = Model(C(1, type: CueType.Wait, duration: 5));
        var drag = new TimelineDragOverride
        {
            CueId = 1, IsGroup = false, Mode = TimelineDragMode.ResizeR,
            PreWait = 0, Layer = 0, TrimStart = 0, TrimEnd = 0, Duration = 8.0,
        };
        var proj = TimelineLayout.Project(model, drag);
        Assert.Equal(8.0, proj.Blocks[0].Dur, precision: 9);
    }

    [Fact]
    public void ConOverrideDeArrastreResizeLCambiaElTrimStartDeUnMedia()
    {
        var model = Model(C(1, type: CueType.Audio, duration: 10, trimStart: 0));
        var drag = new TimelineDragOverride
        {
            CueId = 1, IsGroup = false, Mode = TimelineDragMode.ResizeL,
            PreWait = 2.0, Layer = 0, TrimStart = 2.0, TrimEnd = 0, Duration = 10,
        };
        var proj = TimelineLayout.Project(model, drag);
        // EffectiveDuration = end(cue.Duration=10, TrimEnd override=0->cue.Duration) - ts(2) = 8
        Assert.Equal(8.0, proj.Blocks[0].Dur, precision: 9);
        Assert.Equal(2.0, proj.Blocks[0].Start, precision: 9); // pre_wait override
    }

    [Fact]
    public void ElOverrideSoloAfectaAlCueArrastradoNoALosDemas()
    {
        var model = Model(C(1, preWait: 0, duration: 4), C(2, preWait: 0, duration: 3));
        var drag = new TimelineDragOverride
        {
            CueId = 1, IsGroup = false, Mode = TimelineDragMode.Move,
            PreWait = 10.0, Layer = 0, TrimStart = 0, TrimEnd = 0, Duration = 4,
        };
        var proj = TimelineLayout.Project(model, drag);
        Assert.Equal(10.0, proj.Blocks[0].Start, precision: 9);
        // El segundo sigue disparándose por GO manual al fin del primero (ya movido)
        Assert.Equal(14.0, proj.Blocks[1].Start, precision: 9);
    }
}
