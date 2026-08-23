// =============================================================================
// MixerLayoutTests.cs — red del cálculo de la mesa de mezclas (hitos-0-1, mixer).
// =============================================================================
// Fijan lo que AudioMixerPanel.cs (WinCue.App) hace HOY: dónde cae cada marca
// de la escala en píxeles, qué strips de cue muestra la mesa y en qué orden
// (PlanCueStrips, ex "deseados" de SyncActive), y cuándo pliega uno
// (ShouldCollapse). Un cambio aquí se nota en pantalla en los DOS productos.
// =============================================================================

using WinCue.Controller;
using WinCue.Core;
using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class FaderScaleLayoutTests
{
    // Alto real de los faders del mixer/inspector/EQ (UiMetrics.FaderHeight).
    private const double H = 170.0;

    [Fact]
    public void LaMarcaDeLaUnidadCaeDondeElFaderDaCeroDb()
    {
        // 0 dB cae a 3/4 del recorrido (AudioLevels.DbToPosition(0) == 0.75).
        double travel = H - FaderScaleLayout.ThumbHeight;
        double esperado = FaderScaleLayout.ThumbHeight / 2.0 + (1.0 - 0.75) * travel;
        Assert.Equal(esperado, FaderScaleLayout.TickY(0.0, H)!.Value, 6);
    }

    [Fact]
    public void LasMarcasDeArribaAbajoVanDeMenorAMayorY()
    {
        // +10 arriba del todo, −60 abajo del todo: en píxeles Y CRECE hacia
        // abajo, así que el orden de ScaleTicks (de +10 a −60) tiene que dar
        // una Y creciente.
        double prev = double.NegativeInfinity;
        foreach (double db in AudioLevels.ScaleTicks)
        {
            double y = FaderScaleLayout.TickY(db, H)!.Value;
            Assert.True(y > prev, $"la marca de {db} dB no cae por debajo de la anterior");
            prev = y;
        }
    }

    [Fact]
    public void LosExtremosCaenEnLosBordesDelRecorridoUtil()
    {
        // +10 dB es el techo de la ley (posición 1.0): cae justo bajo el
        // medio pomo de arriba. −60 dB (posición del suelo rotulado) cae
        // bastante más abajo, pero no en el borde físico (el suelo de la ley
        // no es 0 dB, es 0 posición == silencio, fuera de ScaleTicks).
        double travel = H - FaderScaleLayout.ThumbHeight;
        double top = FaderScaleLayout.ThumbHeight / 2.0;
        Assert.Equal(top, FaderScaleLayout.TickY(10.0, H)!.Value, 6);
        Assert.True(FaderScaleLayout.TickY(-60.0, H)!.Value < top + travel);
    }

    [Fact]
    public void SinRecorridoUtilNoHayMarca()
    {
        // Un control más bajo que el propio pomo no tiene recorrido que
        // repartir: ni una marca válida, en vez de una división por casi cero.
        Assert.Null(FaderScaleLayout.TickY(0.0, FaderScaleLayout.ThumbHeight));
        Assert.Null(FaderScaleLayout.TickY(0.0, 5.0));
    }
}

public class MixerLayoutTests
{
    private static Cue C(int id, CueType type, string filePath = "a.wav",
                         string number = "", string name = "", string color = "") => new()
    {
        Id = id, Type = type, FilePath = filePath,
        Number = number.Length > 0 ? number : id.ToString(),
        Name = name.Length > 0 ? name : $"Cue {id}",
        Color = color,
    };

    private static ActiveCueInfo Active(int id, CueType type, string number, string name,
                                        bool mirror = false) =>
        new(id, number, name, type, "", 0.0, null, null, MirrorAudio: mirror);

    [Fact]
    public void SinAllCues_SoloEnseñaLosQueSuenanAhora()
    {
        var rows = new[]
        {
            Active(1, CueType.Audio, "1", "Uno"),
            Active(2, CueType.Video, "2", "Dos", mirror: true),
            Active(3, CueType.Wait, "3", "Espera"),   // ni audio ni vídeo: fuera
        };
        var plan = MixerLayout.PlanCueStrips(rows, allCues: null);

        Assert.Equal(2, plan.Count);
        Assert.All(plan, s => Assert.True(s.IsSounding));
        Assert.Contains(plan, s => s.Id == 1 && s.Type == CueType.Audio && !s.MirrorAudio);
        Assert.Contains(plan, s => s.Id == 2 && s.Type == CueType.Video && s.MirrorAudio);
    }

    [Fact]
    public void ConAllCues_EnseñaTodosLosDeAudioYVideoConArchivo_EnElOrdenDelShow()
    {
        var allCues = new[]
        {
            C(5, CueType.Audio, name: "Cinco"),
            C(1, CueType.Wait),                       // ni audio ni vídeo: fuera
            C(2, CueType.Video, filePath: ""),        // sin archivo: fuera
            C(3, CueType.Audio, name: "Tres"),
        };
        var plan = MixerLayout.PlanCueStrips(Array.Empty<ActiveCueInfo>(), allCues);

        Assert.Equal(new[] { 5, 3 }, plan.Select(s => s.Id).ToArray());
        Assert.All(plan, s => Assert.False(s.IsSounding));
    }

    [Fact]
    public void ConAllCues_UnCueSonandoUsaLosDatosVivosDeActiveCueInfo()
    {
        // El que SUENA se describe con la fila viva (ActiveCueInfo), no con
        // el Cue del modelo: es de ahí de donde sale MirrorAudio, que no
        // existe en el Cue guardado.
        var allCues = new[] { C(7, CueType.Video, name: "Siete") };
        var rows = new[] { Active(7, CueType.Video, "7", "Siete", mirror: true) };

        var plan = MixerLayout.PlanCueStrips(rows, allCues);

        var spec = Assert.Single(plan);
        Assert.True(spec.IsSounding);
        Assert.True(spec.MirrorAudio);
    }

    [Theory]
    [InlineData(true, null, false)]     // suena, sin decisión manual → abierto
    [InlineData(false, null, true)]     // parado, sin decisión manual → plegado
    [InlineData(true, true, true)]      // suena, pero el operador quiere plegarlo
    [InlineData(false, false, false)]   // parado, pero el operador quiere desplegarlo
    public void ShouldCollapse_LaDecisionManualMandaSobreElAutomatismo(
        bool isSounding, bool? manual, bool esperado) =>
        Assert.Equal(esperado, MixerLayout.ShouldCollapse(isSounding, manual));
}
