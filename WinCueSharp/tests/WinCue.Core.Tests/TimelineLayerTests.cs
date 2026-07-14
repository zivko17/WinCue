// =============================================================================
// TimelineLayerTests.cs — Campo timeline_layer (editor de línea de tiempo)
// =============================================================================
// timeline_layer es un campo C#-only (como audio_output): Python lo ignora al
// cargar. Aquí se cubre el round-trip, el default retrocompatible y el clamp.
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
    public void FromJson_SinClave_DefaultCero()
    {
        var cues = CueModel.FromJson(
            """{"version":1,"cues":[{"id":1,"type":"AUDIO","name":"a","duration":0,"file_path":""}]}""");

        Assert.Equal(0, cues[0].TimelineLayer);
    }

    [Theory]
    [InlineData(-3, 0)]
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
