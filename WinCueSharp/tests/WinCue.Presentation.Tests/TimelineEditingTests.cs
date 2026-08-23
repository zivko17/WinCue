// =============================================================================
// TimelineEditingTests.cs — qué escribe un arrastre al soltarse (hitos-0-1,
// timeline interactivo).
// =============================================================================
// Fijan la política de TimelinePanel.OnMouseUp (WinCue.App) tal como es HOY:
// mover más allá del vecino reordena la lista; soltar montado sobre el
// anterior encadena en PARALELO (AUTO_CONTINUE + pre_wait compensado); los
// bloques no arrastrados se anclan donde estaban; y el solape en la misma
// capa se convierte en crossfade (y al separarse, se retira). Si un caso de
// estos cambia aquí, soltar un bloque hace OTRA COSA en los dos productos.
//
// Los commits se aplican con un aplicador mínimo campo→propiedad que imita lo
// que MainController.OnCueEditCommitted hace sobre el modelo (síncrono, campo
// a campo): lo que se prueba aquí es la POLÍTICA del soltar, no el
// controlador — de ese contrato ya se ocupa InspectorFieldsRoundTripTests.
// =============================================================================

using WinCue.Core;
using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class TimelineEditingTests
{
    private static Cue C(int id, CueType type = CueType.Audio, int? parentId = null,
        double preWait = 0, double postWait = 0, double duration = 0,
        ContinueMode continueMode = ContinueMode.None,
        double trimStart = 0, double trimEnd = 0, int timelineLayer = -1,
        double fadeIn = 0, double fadeOut = 0,
        GroupMode groupMode = GroupMode.Enter) => new()
    {
        Id = id,
        Type = type,
        ParentId = parentId,
        PreWait = preWait,
        PostWait = postWait,
        Duration = duration,
        ContinueMode = continueMode,
        TrimStart = trimStart,
        TrimEnd = trimEnd,
        TimelineLayer = timelineLayer,
        FadeIn = fadeIn,
        FadeOut = fadeOut,
        GroupMode = groupMode,
    };

    private static CueModel Model(params Cue[] cues)
    {
        var m = new CueModel();
        foreach (var c in cues)
            m.AddCue(c);
        return m;
    }

    /// <summary>Registra los commits/moves y APLICA los commits al modelo,
    /// como haría OnCueEditCommitted (síncrono): CommitDrag reproyecta entre
    /// pasos y cuenta con ver cada cambio ya escrito.</summary>
    private sealed class Recorder
    {
        private readonly CueModel _model;
        public readonly List<(int Id, string Field, object? Value)> Commits = new();
        public readonly List<(int From, int To)> Moves = new();

        public Recorder(CueModel model) => _model = model;

        public void Commit(int id, string field, object? value)
        {
            Commits.Add((id, field, value));
            var c = _model.GetById(id)!;
            switch (field)
            {
                case "pre_wait": c.PreWait = (double)value!; break;
                case "trim_start": c.TrimStart = (double)value!; break;
                case "trim_end": c.TrimEnd = (double)value!; break;
                case "duration": c.Duration = (double)value!; break;
                case "timeline_layer": c.TimelineLayer = (int)value!; break;
                case "fade_in": c.FadeIn = (double)value!; break;
                case "fade_out": c.FadeOut = (double)value!; break;
                // El timeline solo escribe este valor de continue_mode
                case "continue_mode": c.ContinueMode = ContinueMode.AutoContinue; break;
                default: throw new InvalidOperationException($"campo inesperado: {field}");
            }
        }

        public void Move(int from, int to) => Moves.Add((from, to));
    }

    /// <summary>Agarra el bloque proyectado del cue y lo arrastra dx/dy
    /// píxeles (10 px/s, pistas de 40 px), dejando el gesto listo para
    /// soltar. startY = centro de la pista 0.</summary>
    private static TimelineDragGesture Drag(CueModel model, int cueId,
        double dxPx, double dyPx = 0, TimelineDragMode mode = TimelineDragMode.Move,
        bool isGroup = false)
    {
        var proj = TimelineLayout.Project(model);
        var block = isGroup ? null
            : proj.Blocks.First(b => !b.Ghost && b.Cue.Id == cueId);
        double startX = 100, startY = TimelineLayout.RulerH + 20;
        var g = new TimelineDragGesture(model.GetById(cueId)!, isGroup, mode,
            block, startX, startY, proj.StartById);
        g.Update(startX + dxPx, startY + dyPx, 10, 40);
        return g;
    }

    [Fact]
    public void UnClicNoEscribeNada()
    {
        var model = Model(C(1, duration: 5));
        var rec = new Recorder(model);
        var g = Drag(model, 1, dxPx: 2);   // bajo el umbral: no es un arrastre
        TimelineEditing.CommitDrag(model, g, rec.Commit, rec.Move, autoCrossfade: true);
        Assert.Empty(rec.Commits);
        Assert.Empty(rec.Moves);
    }

    [Fact]
    public void SoltarMasAllaDelVecinoReordenaLaLista()
    {
        // A [0,5] y B [5,10]: A soltado en el segundo 12 cruza a B entero
        var model = Model(C(1, duration: 5), C(2, duration: 5));
        var rec = new Recorder(model);
        var g = Drag(model, 1, dxPx: 120);
        TimelineEditing.CommitDrag(model, g, rec.Commit, rec.Move, autoCrossfade: true);
        Assert.Equal((0, 1), Assert.Single(rec.Moves));
        Assert.DoesNotContain(rec.Commits, c => c.Field == "pre_wait");
    }

    [Fact]
    public void SoltarHaciaAtrasCruzandoAlAnteriorTambienReordena()
    {
        // A [5,10] (pre_wait 5) y B [10,15]: B soltado en el segundo 2 cae
        // ANTES del inicio de A → cambio de orden, no encadenado. (Con A en
        // el 0 no hay "antes": ahí el gesto es solapar, no reordenar.)
        var model = Model(C(1, preWait: 5, duration: 5), C(2, duration: 5));
        var rec = new Recorder(model);
        var g = Drag(model, 2, dxPx: -80);
        TimelineEditing.CommitDrag(model, g, rec.Commit, rec.Move, autoCrossfade: true);
        Assert.Equal((1, 0), Assert.Single(rec.Moves));
    }

    [Fact]
    public void MoverEscribeElPreWaitYAnclaALosDemas()
    {
        // A [0,5]; B con pre_wait 3 → [8,13]. Mover A +2 s desplazaría a B a
        // 10: el ancla le compensa el pre_wait (3 → 1) para dejarlo en 8.
        var model = Model(C(1, duration: 5), C(2, preWait: 3, duration: 5));
        var rec = new Recorder(model);
        var g = Drag(model, 1, dxPx: 20);
        TimelineEditing.CommitDrag(model, g, rec.Commit, rec.Move, autoCrossfade: true);

        Assert.Empty(rec.Moves);
        Assert.Contains((1, "pre_wait", (object?)2.0), rec.Commits);
        Assert.Contains((2, "pre_wait", (object?)1.0), rec.Commits);
        Assert.Equal(8, TimelineLayout.Project(model).StartById[2], 6);
    }

    [Fact]
    public void SoltarMontadoSobreElAnteriorEncadenaEnParaleloYCruza()
    {
        // A [0,10] y B [10,20]: B soltado en el segundo 6 monta 4 s sobre A →
        // A pasa a AUTO_CONTINUE, B recibe pre_wait 6, los dos congelan su
        // pista, y el solape de 4 s se escribe como crossfade (🔀 ON).
        var model = Model(C(1, duration: 10), C(2, duration: 10));
        var rec = new Recorder(model);
        var g = Drag(model, 2, dxPx: -40);
        TimelineEditing.CommitDrag(model, g, rec.Commit, rec.Move, autoCrossfade: true);

        Assert.Empty(rec.Moves);
        Assert.Contains((1, "continue_mode", (object?)"AUTO_CONTINUE"), rec.Commits);
        Assert.Contains((2, "pre_wait", (object?)6.0), rec.Commits);
        Assert.Contains((1, "timeline_layer", (object?)0), rec.Commits);   // el anterior se congela
        Assert.Contains((2, "timeline_layer", (object?)0), rec.Commits);   // el solapado también
        Assert.Contains((1, "fade_out", (object?)4.0), rec.Commits);
        Assert.Contains((2, "fade_in", (object?)4.0), rec.Commits);
        Assert.Equal(6, TimelineLayout.Project(model).StartById[2], 6);
    }

    [Fact]
    public void SinElInterruptorElSolapeNoEscribeFades()
    {
        var model = Model(C(1, duration: 10), C(2, duration: 10));
        var rec = new Recorder(model);
        var g = Drag(model, 2, dxPx: -40);
        TimelineEditing.CommitDrag(model, g, rec.Commit, rec.Move, autoCrossfade: false);
        Assert.DoesNotContain(rec.Commits, c => c.Field is "fade_in" or "fade_out");
        Assert.Contains((2, "pre_wait", (object?)6.0), rec.Commits);
    }

    [Fact]
    public void SepararDosBloquesCruzadosRetiraElCrossfade()
    {
        // Estado ya encadenado con 4 s de cruce; separar B más allá del final
        // de A pone ambos fades a 0.
        var model = Model(
            C(1, duration: 10, continueMode: ContinueMode.AutoContinue,
                timelineLayer: 0, fadeOut: 4),
            C(2, preWait: 6, duration: 10, timelineLayer: 0, fadeIn: 4));
        var rec = new Recorder(model);
        var g = Drag(model, 2, dxPx: 60);   // B pasa de [6,16] a [12,22]
        TimelineEditing.CommitDrag(model, g, rec.Commit, rec.Move, autoCrossfade: true);

        Assert.Contains((1, "fade_out", (object?)0.0), rec.Commits);
        Assert.Contains((2, "fade_in", (object?)0.0), rec.Commits);
    }

    [Fact]
    public void ElGestoVerticalFijaLaPista()
    {
        var model = Model(C(1, duration: 5));
        var rec = new Recorder(model);
        var g = Drag(model, 1, dxPx: 0, dyPx: 80);   // dos pistas más abajo
        TimelineEditing.CommitDrag(model, g, rec.Commit, rec.Move, autoCrossfade: true);
        Assert.Equal((1, "timeline_layer", (object?)2), Assert.Single(rec.Commits));
    }

    [Fact]
    public void ElBordeDerechoDeUnWaitEscribeSuDuracion()
    {
        var model = Model(C(1, CueType.Wait, duration: 5));
        var rec = new Recorder(model);
        var g = Drag(model, 1, dxPx: 30, mode: TimelineDragMode.ResizeR);
        TimelineEditing.CommitDrag(model, g, rec.Commit, rec.Move, autoCrossfade: true);
        Assert.Equal((1, "duration", (object?)8.0), Assert.Single(rec.Commits));
    }

    [Fact]
    public void ElBordeIzquierdoEscribePreWaitYTrimJuntos()
    {
        // Deslizar la entrada +2 s: el bloque empieza más tarde (pre_wait) y
        // el media entra más adentro (trim_start) — el borde derecho no se mueve.
        var model = Model(C(1, CueType.Audio, preWait: 2, duration: 10, trimStart: 1));
        var rec = new Recorder(model);
        var g = Drag(model, 1, dxPx: 20, mode: TimelineDragMode.ResizeL);
        TimelineEditing.CommitDrag(model, g, rec.Commit, rec.Move, autoCrossfade: true);
        Assert.Contains((1, "pre_wait", (object?)4.0), rec.Commits);
        Assert.Contains((1, "trim_start", (object?)3.0), rec.Commits);
    }

    [Fact]
    public void DentroDeUnGrupoTimelineSoloSeRecortaElOffset()
    {
        // Los hijos de un grupo TIMELINE ya son paralelos: soltar "en
        // negativo" no encadena a nadie, solo deja el pre_wait en 0.
        var model = Model(
            C(1, CueType.Group, groupMode: GroupMode.Timeline),
            C(2, parentId: 1, duration: 10),
            C(3, parentId: 1, preWait: 5, duration: 10));
        var rec = new Recorder(model);
        var g = Drag(model, 3, dxPx: -80);   // -8 s: preWait previsto -3
        TimelineEditing.CommitDrag(model, g, rec.Commit, rec.Move, autoCrossfade: true);

        Assert.DoesNotContain(rec.Commits, c => c.Field == "continue_mode");
        Assert.Contains((3, "pre_wait", (object?)0.0), rec.Commits);
    }
}
