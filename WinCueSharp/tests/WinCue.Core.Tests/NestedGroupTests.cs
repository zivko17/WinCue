// =============================================================================
// NestedGroupTests.cs — Grupos SIN ANIDAR: aplanado de shows antiguos
// =============================================================================
// Un grupo dentro de otro ya no se puede crear, pero los shows guardados con
// versiones anteriores (o por la versión Python) pueden traerlos. Cargar nunca
// debe fallar: el grupo interior se degrada a NOTA y sus hijos pasan al grupo
// exterior, sin perder cues ni mover el orden.
// =============================================================================

using WinCue.Core;

namespace WinCue.Core.Tests;

public class NestedGroupTests
{
    /// <summary>Show con G1 &gt; (a, G2 &gt; (c), b) — el clásico "Grupo &gt; Grupo".</summary>
    private const string ShowAnidado = """
        {"version": 1, "cues": [
          {"id": 1, "type": "GROUP", "name": "G1", "color": "red"},
          {"id": 2, "type": "AUDIO", "name": "a", "parent_id": 1},
          {"id": 3, "type": "GROUP", "name": "G2", "color": "blue", "number": "9",
           "notes": "apuntes", "parent_id": 1},
          {"id": 4, "type": "AUDIO", "name": "c", "parent_id": 3},
          {"id": 5, "type": "MEMO", "name": "b", "parent_id": 1}
        ]}
        """;

    [Fact]
    public void Carga_ShowConGrupoAnidado_NoRevienta_YLoAplana()
    {
        var cues = CueModel.FromJson(ShowAnidado, out int aplanados);

        Assert.Equal(1, aplanados);
        Assert.Equal(5, cues.Count);                       // no se pierde ningún cue
        Assert.Equal(new[] { 1, 2, 3, 4, 5 },              // ni se mueve el orden
                     cues.Select(c => c.Id));
    }

    [Fact]
    public void GrupoInterior_SeDegradaANota_ConservandoSusDatos()
    {
        var cues = CueModel.FromJson(ShowAnidado);
        var g2 = cues.Single(c => c.Id == 3);

        Assert.Equal(CueType.Memo, g2.Type);
        Assert.Equal("G2", g2.Name);
        Assert.Equal("9", g2.Number);
        Assert.Equal("blue", g2.Color);
        Assert.Equal("apuntes", g2.Notes);
        Assert.Equal(1, g2.ParentId);   // sigue dentro del grupo exterior
    }

    [Fact]
    public void HijosDelGrupoInterior_SubenAlGrupoExterior()
    {
        var cues = CueModel.FromJson(ShowAnidado);

        Assert.Equal(1, cues.Single(c => c.Id == 4).ParentId);
        // Único GROUP que queda, y sin grupo por encima
        var grupo = Assert.Single(cues.Where(c => c.Type == CueType.Group));
        Assert.Equal(1, grupo.Id);
        Assert.Null(grupo.ParentId);
    }

    [Fact]
    public void TresNiveles_SeAplananTodos()
    {
        string json = """
            {"cues": [
              {"id": 1, "type": "GROUP", "name": "G1"},
              {"id": 2, "type": "GROUP", "name": "G2", "parent_id": 1},
              {"id": 3, "type": "GROUP", "name": "G3", "parent_id": 2},
              {"id": 4, "type": "AUDIO", "name": "hoja", "parent_id": 3}
            ]}
            """;

        var cues = CueModel.FromJson(json, out int aplanados);

        Assert.Equal(2, aplanados);
        Assert.Single(cues.Where(c => c.Type == CueType.Group));
        Assert.Equal(1, cues.Single(c => c.Id == 4).ParentId);   // la hoja cuelga de G1
    }

    [Fact]
    public void ShowPlano_NoSeToca()
    {
        string json = """
            {"cues": [
              {"id": 1, "type": "GROUP", "name": "G1"},
              {"id": 2, "type": "AUDIO", "name": "a", "parent_id": 1},
              {"id": 3, "type": "GROUP", "name": "G2"},
              {"id": 4, "type": "AUDIO", "name": "b", "parent_id": 3}
            ]}
            """;

        var cues = CueModel.FromJson(json, out int aplanados);

        Assert.Equal(0, aplanados);
        Assert.Equal(2, cues.Count(c => c.Type == CueType.Group));
    }

    [Fact]
    public void Aplanar_EsIdempotente()
    {
        var cues = CueModel.FromJson(ShowAnidado);
        Assert.Equal(0, CueModel.FlattenNestedGroups(cues));
    }

    [Fact]
    public void GuardarTrasCargar_ElShowYaSaleSinAnidar()
    {
        var model = new CueModel();
        model.ReplaceAll(CueModel.FromJson(ShowAnidado));

        var releidos = CueModel.FromJson(model.ToJson(), out int aplanados);

        Assert.Equal(0, aplanados);
        Assert.Equal(5, releidos.Count);
    }

    // ── Color del grupo (identificación por color) ─────────────────────

    [Fact]
    public void ColorDelGrupo_SePersisteComoElDeCualquierCue()
    {
        var model = new CueModel();
        model.AddCue(new Cue { Id = 1, Type = CueType.Group, Name = "G", Color = "purple" });

        string json = model.ToJson();
        Assert.Contains("\"color\": \"purple\"", json, StringComparison.Ordinal);

        var releido = CueModel.FromJson(json)[0];
        Assert.Equal("purple", releido.Color);
        Assert.Equal(CueType.Group, releido.Type);
    }

    [Fact]
    public void ColorDelGrupo_ClaveDeLaPaletaCompartidaConPython()
    {
        Assert.All(new[] { "red", "orange", "yellow", "green", "blue", "purple" },
            key => Assert.True(CueConstants.CueColors.ContainsKey(key)));
    }
}
