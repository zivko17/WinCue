// =============================================================================
// TimelineLayerTests.cs — Campo timeline_layer (línea de tiempo)
// =============================================================================
// timeline_layer es un campo C#-only (como audio_output): Python lo ignora al
// cargar. -1 = capa automática (el timeline apila solo); 0–4 = fijada a mano.
// =============================================================================

using WinCue.Core;

namespace WinCue.Core.Tests;

public class TimelineLayerTests
{
    [Fact]
    public void RoundTrip_PreservaTimelineLayer()
    {
        var model = new CueModel();
        model.AddCue(new Cue { Id = 1, Type = CueType.Audio, TimelineLayer = 3 });

        var restored = CueModel.FromJson(model.ToJson());

        Assert.Equal(3, restored[0].TimelineLayer);
    }

    [Fact]
    public void FromJson_SinClave_DefaultAutomatica()
    {
        var cues = CueModel.FromJson(
            """{"version":1,"cues":[{"id":1,"type":"AUDIO","name":"a","duration":0,"file_path":""}]}""");

        Assert.Equal(-1, cues[0].TimelineLayer);
    }

    [Theory]
    [InlineData(-5, -1)]
    [InlineData(-1, -1)]
    [InlineData(0, 0)]
    [InlineData(4, 4)]
    [InlineData(99, 4)]
    public void Normalize_ClampaCapaAlRango(int valor, int esperado)
    {
        var cue = new Cue { Id = 1, TimelineLayer = valor };
        cue.Normalize();
        Assert.Equal(esperado, cue.TimelineLayer);
    }
}

public class EqFieldsTests
{
    [Fact]
    public void RoundTrip_PreservaEqYGanancia()
    {
        var model = new CueModel();
        model.AddCue(new Cue
        {
            Id = 1, Type = CueType.Audio,
            GainDb = -6.5, EqEnabled = true,
            HpfOn = true, HpfFreq = 120, LpfOn = true, LpfFreq = 9000,
            Eq2Freq = 640, Eq2Gain = 4.5, Eq2Q = 2.2,
        });

        var c = CueModel.FromJson(model.ToJson())[0];

        Assert.Equal(-6.5, c.GainDb);
        Assert.True(c.EqEnabled);
        Assert.True(c.HpfOn);
        Assert.Equal(120, c.HpfFreq);
        Assert.Equal(9000, c.LpfFreq);
        Assert.Equal(640, c.Eq2Freq);
        Assert.Equal(4.5, c.Eq2Gain);
        Assert.Equal(2.2, c.Eq2Q);
    }

    [Fact]
    public void FromJson_SinClaves_DefaultsNeutros()
    {
        var c = CueModel.FromJson(
            """{"version":1,"cues":[{"id":1,"type":"AUDIO","name":"a","duration":0,"file_path":""}]}""")[0];

        Assert.Equal(0.0, c.GainDb);
        Assert.False(c.EqEnabled);
        Assert.Equal(80.0, c.HpfFreq);
        Assert.Equal(1.0, c.Eq1Q);
    }

    [Fact]
    public void Normalize_ClampaGananciaYFrecuencias()
    {
        var c = new Cue
        {
            Id = 1, GainDb = 99, Eq1Freq = 5, Eq1Gain = -50, Eq1Q = 0.001,
            HpfFreq = 90000,
        };
        c.Normalize();

        Assert.Equal(12.0, c.GainDb);
        Assert.Equal(20.0, c.Eq1Freq);
        Assert.Equal(-18.0, c.Eq1Gain);
        Assert.Equal(0.1, c.Eq1Q);
        Assert.Equal(2000.0, c.HpfFreq);
    }
}
