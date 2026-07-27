// =============================================================================
// GroupNestingTests.cs — Los grupos NO se anidan (crear, insertar, mover, pegar)
// =============================================================================
// Regla del proyecto: un GROUP nunca puede quedar dentro de otro GROUP. Aquí se
// cierra por las cuatro puertas que podían crear uno: agrupar la selección,
// insertar un grupo, arrastrar un grupo y pegar un bloque con grupo.
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class GroupNestingTests : ControllerTestBase
{
    /// <summary>Modelo de partida: G(hijo1, hijo2) y un cue suelto detrás.</summary>
    private (Cue G, Cue H1, Cue H2, Cue Fuera) UnGrupo()
    {
        var g = AddCue(CueType.Group, "G");
        var h1 = AddCue(CueType.Audio, "h1", setup: c => c.ParentId = g.Id);
        var h2 = AddCue(CueType.Audio, "h2", setup: c => c.ParentId = g.Id);
        var fuera = AddCue(CueType.Memo, "fuera");
        return (g, h1, h2, fuera);
    }

    // ── Agrupar selección ──────────────────────────────────────────────

    [Fact]
    public void Agrupar_CuesQueYaEstanEnUnGrupo_NoCreaNada()
    {
        var (g, _, _, _) = UnGrupo();

        Controller.OnGroupSelected(new[] { 1, 2 });   // los dos hijos

        Assert.Equal(4, Model.Count);
        Assert.Single(Model.Cues.Where(c => c.Type == CueType.Group));
        Assert.Equal(g.Id, Model.Cues[1].ParentId);   // los hijos siguen donde estaban
        Assert.Contains(View.Statuses, s => s.Contains("anida", StringComparison.OrdinalIgnoreCase)
                                         || s.Contains("nested", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Agrupar_SeleccionQueContieneUnGrupo_NoCreaNada()
    {
        UnGrupo();

        Controller.OnGroupSelected(new[] { 0, 3 });   // el grupo + el cue suelto

        Assert.Equal(4, Model.Count);
        Assert.Single(Model.Cues.Where(c => c.Type == CueType.Group));
    }

    [Fact]
    public void Agrupar_CuesSueltos_SigueFuncionando()
    {
        AddCue(CueType.Audio, "a");
        AddCue(CueType.Audio, "b");

        Controller.OnGroupSelected(new[] { 0, 1 });

        Assert.Equal(3, Model.Count);
        Assert.Equal(CueType.Group, Model.Cues[0].Type);
        Assert.Null(Model.Cues[0].ParentId);
    }

    // ── Insertar un grupo ──────────────────────────────────────────────

    [Fact]
    public void InsertarGrupo_DentroDeOtro_AterrizaDetrasDelBloque()
    {
        var (g, _, _, _) = UnGrupo();

        Controller.OnInsertCue(CueType.Group, row: 1);   // sobre el primer hijo

        Assert.Equal(5, Model.Count);
        var nuevo = Model.Cues[3];                       // tras G, h1 y h2
        Assert.Equal(CueType.Group, nuevo.Type);
        Assert.Null(nuevo.ParentId);
        Assert.Equal(g.Id, Model.Cues[2].ParentId);      // el grupo original, entero
    }

    [Fact]
    public void InsertarCueNormal_DentroDeUnGrupo_SigueHeredandoElGrupo()
    {
        var (g, _, _, _) = UnGrupo();

        Controller.OnInsertCue(CueType.Audio, row: 1);

        Assert.Equal(g.Id, Model.Cues[2].ParentId);
    }

    // ── Arrastrar un grupo ─────────────────────────────────────────────

    [Fact]
    public void MoverGrupo_SobreLaFilaDeOtroGrupo_SeQuedaDetrasDeEl()
    {
        UnGrupo();
        var g2 = AddCue(CueType.Group, "G2");            // fila 4
        AddCue(CueType.Audio, "x", setup: c => c.ParentId = g2.Id);

        Controller.OnMoveCue(fromRow: 4, toRow: 0);      // G2 sobre la fila de G

        Assert.Null(g2.ParentId);
        Assert.Equal(2, Model.Cues.Count(c => c.Type == CueType.Group));
        // G2 aterriza tras el bloque completo de G (G, h1, h2)
        Assert.Equal(g2.Id, Model.Cues[3].Id);
        Assert.Equal(g2.Id, Model.Cues[4].ParentId);     // su hijo lo acompaña
    }

    [Fact]
    public void MoverGrupo_EntreLosHijosDeOtro_SeQuedaDetrasDelBloque()
    {
        UnGrupo();
        var g2 = AddCue(CueType.Group, "G2");            // fila 4

        Controller.OnMoveCue(fromRow: 4, toRow: 2);      // en medio de los hijos de G

        Assert.Null(g2.ParentId);
        Assert.Equal(g2.Id, Model.Cues[3].Id);           // detrás de G, h1 y h2
    }

    [Fact]
    public void MoverCueNormal_SobreUnGrupo_SigueEntrandoDentro()
    {
        var (g, _, _, fuera) = UnGrupo();

        Controller.OnMoveCue(fromRow: 3, toRow: 0);      // el suelto sobre la fila de G

        Assert.Equal(g.Id, fuera.ParentId);
    }

    // ── Pegar / duplicar ───────────────────────────────────────────────

    [Fact]
    public void PegarUnGrupo_DentroDeOtro_AterrizaEnLaRaizDetrasDelBloque()
    {
        var (g, _, _, _) = UnGrupo();
        var g2 = AddCue(CueType.Group, "G2");            // fila 4
        AddCue(CueType.Audio, "x", setup: c => c.ParentId = g2.Id);

        Controller.CopyCues(new[] { 4 });                // G2 con su hijo
        Controller.PasteCues(row: 1);                    // pegar sobre un hijo de G

        Assert.Equal(8, Model.Count);
        var pegado = Model.Cues[3];                      // tras G, h1, h2
        Assert.Equal(CueType.Group, pegado.Type);
        Assert.Null(pegado.ParentId);
        Assert.Equal(pegado.Id, Model.Cues[4].ParentId);
        // Ningún grupo quedó dentro de otro
        Assert.All(Model.Cues.Where(c => c.Type == CueType.Group),
            c => Assert.False(Model.IsInsideGroup(c)));
        // Y el grupo original sigue con sus dos hijos
        Assert.Equal(2, Model.Cues.Count(c => c.ParentId == g.Id));
    }

    [Fact]
    public void DuplicarUnGrupo_NoLoMeteDentroDeSiMismo()
    {
        UnGrupo();

        Controller.DuplicateCues(new[] { 0, 1, 2 });     // el grupo entero

        Assert.Equal(7, Model.Count);
        Assert.All(Model.Cues.Where(c => c.Type == CueType.Group),
            c => Assert.False(Model.IsInsideGroup(c)));
    }

    [Fact]
    public void PegarCuesNormales_DentroDeUnGrupo_SigueEntrando()
    {
        var (g, _, _, _) = UnGrupo();

        Controller.CopyCues(new[] { 3 });                // el cue suelto
        Controller.PasteCues(row: 1);

        Assert.Equal(g.Id, Model.Cues[2].ParentId);
    }

    // ── Carga de un show antiguo con grupos anidados ───────────────────

    [Fact]
    public void CargarShowConGruposAnidados_NoFalla_YAvisaEnLaBarraDeEstado()
    {
        string path = Path.Combine(TempDir, "antiguo.json");
        File.WriteAllText(path, """
            {"version": 1, "cues": [
              {"id": 1, "type": "GROUP", "name": "G1"},
              {"id": 2, "type": "GROUP", "name": "G2", "parent_id": 1},
              {"id": 3, "type": "MEMO", "name": "hoja", "parent_id": 2}
            ]}
            """);

        Controller.LoadShow(path);

        Assert.Empty(View.Errors);
        Assert.Equal(3, Model.Count);
        Assert.Single(Model.Cues.Where(c => c.Type == CueType.Group));
        Assert.All(Model.Cues.Where(c => c.Type == CueType.Group),
            c => Assert.False(Model.IsInsideGroup(c)));
        Assert.Contains(View.Statuses, s => s.Contains("anidado", StringComparison.OrdinalIgnoreCase)
                                         || s.Contains("nested", StringComparison.OrdinalIgnoreCase));
    }
}
