// =============================================================================
// VideoOutputTests.cs — Multi-salida de vídeo (video_output por cue)
// =============================================================================
// Cada cue de vídeo elige su salida (0 = principal … 3). La exclusividad de
// pista es POR SALIDA: misma pista en salidas distintas convive; misma pista
// en la misma salida sustituye.
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class VideoOutputTests : ControllerTestBase
{
    [Fact]
    public void Go_PasaLaSalidaDelCueAlMotor()
    {
        AddCue(CueType.Video, setup: c => c.VideoOutput = 2);

        Controller.OnGoPressed();

        var play = Assert.Single(Video.Plays);
        Assert.Equal(2, play.P.Screen);
    }

    [Fact]
    public void Go_SinSalidaExplicita_UsaLaPrincipal()
    {
        AddCue(CueType.Video);

        Controller.OnGoPressed();

        Assert.Equal(0, Assert.Single(Video.Plays).P.Screen);
    }

    [Fact]
    public void MismaPista_SalidasDistintas_Conviven()
    {
        var a = AddCue(CueType.Video, setup: c => { c.TimelineLayer = 2; c.VideoOutput = 0; });
        var b = AddCue(CueType.Video, setup: c => { c.TimelineLayer = 2; c.VideoOutput = 1; });

        Controller.OnGoPressed();
        Controller.OnGoPressed();

        // Salidas distintas: el segundo GO no corta al primero
        Assert.True(Video.IsPlaying(a.Id));
        Assert.True(Video.IsPlaying(b.Id));
        Assert.Empty(Video.Stops);
    }

    [Fact]
    public void MismaPista_MismaSalida_Sustituye()
    {
        var a = AddCue(CueType.Video, setup: c => { c.TimelineLayer = 2; c.VideoOutput = 1; });
        var b = AddCue(CueType.Video, setup: c => { c.TimelineLayer = 2; c.VideoOutput = 1; });

        Controller.OnGoPressed();
        Controller.OnGoPressed();

        Assert.False(Video.IsPlaying(a.Id));
        Assert.True(Video.IsPlaying(b.Id));
    }

    [Fact]
    public void VideoOutput_SePersisteYRecarga()
    {
        AddCue(CueType.Video, "multi", setup: c => c.VideoOutput = 3);
        string path = Path.Combine(TempDir, "salidas.json");

        Controller.SaveShow(path);
        Assert.Contains("\"video_output\": 3", File.ReadAllText(path));

        Controller.LoadShow(path);
        Assert.Equal(3, Model.Cues[0].VideoOutput);
    }

    [Fact]
    public void Normalize_LimitaLaSalidaAlRango()
    {
        var cue = new Cue { Id = 1, Type = CueType.Video, VideoOutput = 9 };
        cue.Normalize();
        Assert.Equal(3, cue.VideoOutput);

        cue.VideoOutput = -2;
        cue.Normalize();
        Assert.Equal(0, cue.VideoOutput);
    }
}
