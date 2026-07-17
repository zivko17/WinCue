// =============================================================================
// ShowPruebaFileTests.cs — Validación estructural del show sintético
// =============================================================================
// El archivo tests\show-prueba\show-prueba.json es la referencia de pruebas
// manuales de Andrés: aquí se garantiza que siempre es un show VÁLIDO
// (parsea, normaliza, los targets existen, los medias tienen ruta, los
// crossfades del guion son simétricos).
// =============================================================================

using WinCue.Core;

namespace WinCue.Core.Tests;

public class ShowPruebaFileTests
{
    private static string FindShowJson()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent!)
        {
            string candidate = Path.Combine(dir.FullName, "show-prueba", "show-prueba.json");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException("tests\\show-prueba\\show-prueba.json no encontrado");
    }

    private static List<Cue> Load() =>
        CueModel.FromJson(File.ReadAllText(FindShowJson()));

    [Fact]
    public void Parsea_YNormalizaSinErrores()
    {
        var cues = Load();
        Assert.True(cues.Count >= 20);
        foreach (var c in cues)
            c.Normalize();   // no debe lanzar
        // Ids únicos (los targets y el motor dependen de ello)
        Assert.Equal(cues.Count, cues.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void TodosLosTargetsExisten()
    {
        var cues = Load();
        var ids = cues.Select(c => c.Id).ToHashSet();
        foreach (var c in cues.Where(c => c.TargetId is not null))
            Assert.Contains(c.TargetId!.Value, ids);
        // Y los cues de control DEBEN tener target (si no, son no-ops)
        foreach (var c in cues.Where(c => c.Type is CueType.Fade or CueType.Start or CueType.Stop))
            Assert.NotNull(c.TargetId);
    }

    [Fact]
    public void LosMediaTienenRutaYDuracion()
    {
        foreach (var c in Load().Where(c => c.IsMedia()))
        {
            Assert.False(string.IsNullOrWhiteSpace(c.FilePath), $"cue {c.Id} sin ruta");
            Assert.True(c.Duration > 0, $"cue {c.Id} sin duración (los timers de fin dependen de ella)");
        }
    }

    [Fact]
    public void LosCrossfadesDelGuionSonSimetricos()
    {
        var cues = Load();
        Cue ById(int id) => cues.Single(c => c.Id == id);

        // ① corto: fade_out(1.1) == fade_in(1.2) == 0,5 y solape coherente
        Assert.Equal(0.5, ById(11).FadeOut, 2);
        Assert.Equal(0.5, ById(12).FadeIn, 2);
        Assert.Equal(ById(11).Duration - 0.5, ById(12).PreWait, 2);

        // ② largo: 4 s
        Assert.Equal(4.0, ById(21).FadeOut, 2);
        Assert.Equal(4.0, ById(22).FadeIn, 2);
        Assert.Equal(ById(21).Duration - 4.0, ById(22).PreWait, 2);

        // ④ audio: 2 s
        Assert.Equal(2.0, ById(41).FadeOut, 2);
        Assert.Equal(2.0, ById(42).FadeIn, 2);
    }
}
