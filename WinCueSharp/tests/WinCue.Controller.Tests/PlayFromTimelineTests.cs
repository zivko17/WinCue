// =============================================================================
// PlayFromTimelineTests.cs — Reproducir desde el cursor del timeline
// =============================================================================
// El timeline entrega los bloques del segmento: los que cruzan el cursor
// arrancan YA con seek (offset) y los posteriores se programan a su hora.
// Los encadenados propios de cada cue se suprimen (la proyección ya los trae).
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class PlayFromTimelineTests : ControllerTestBase
{
    [Fact]
    public void Spanning_ArrancaConSeekYSinPreWait()
    {
        // Vídeo de 3:30 con el cursor a los 100 s de su bloque
        var v = AddCue(CueType.Video, "deftones", duration: 210.0,
            setup: c => c.TrimStart = 5.0);

        Controller.PlayFromTimeline(new[]
        { new MainController.TimelinePlayItem(v.Id, 100.0, 0.0) });

        var play = Assert.Single(Video.Plays);
        // Seek = trim_start del cue + offset del cursor
        Assert.Equal(105.0, play.P.TrimStart);
        Assert.True(Video.IsPlaying(v.Id));
    }

    [Fact]
    public void Posterior_SeProgramaASuHoraNoAlPreWaitDelCue()
    {
        // El cue tiene un pre_wait ENORME (3:24) pero el timeline ya lo ha
        // resuelto: debe sonar a los 8 s del cursor, no esperar 204 s.
        var v = AddCue(CueType.Video, "later", duration: 155.0,
            setup: c => c.PreWait = 204.0);

        Controller.PlayFromTimeline(new[]
        { new MainController.TimelinePlayItem(v.Id, 0.0, 8.0) });

        Assert.Empty(Video.Plays);          // aún no
        Clock.Advance(7_999);
        Assert.Empty(Video.Plays);
        Clock.Advance(1);
        var play = Assert.Single(Video.Plays);
        Assert.Equal(0.0, play.P.TrimStart);   // desde su principio real
    }

    [Fact]
    public void Spanning_ElTimerDeFinCuentaSoloLoQueQueda()
    {
        var a = AddCue(CueType.Audio, "tema", duration: 100.0);

        Controller.PlayFromTimeline(new[]
        { new MainController.TimelinePlayItem(a.Id, 60.0, 0.0) });

        Assert.True(Audio.IsPlaying(a.Id));
        Clock.Advance(39_000);
        Assert.True(Audio.IsPlaying(a.Id));    // quedan ~1 s
        Clock.Advance(2_000);
        Assert.False(Audio.IsPlaying(a.Id));   // fin a los ~40 s, no a los 100
    }

    [Fact]
    public void CadenasSuprimidas_NoDuplicanDisparos()
    {
        // a (AUTO_FOLLOW) → b: el timeline ya programó b aparte, así que el
        // fin de a NO debe volver a dispararlo.
        var a = AddCue(CueType.Audio, "a", duration: 10.0,
            setup: c => c.ContinueMode = ContinueMode.AutoFollow);
        var b = AddCue(CueType.Audio, "b", duration: 10.0);

        Controller.PlayFromTimeline(new[]
        {
            new MainController.TimelinePlayItem(a.Id, 5.0, 0.0),
            new MainController.TimelinePlayItem(b.Id, 0.0, 5.0),
        });

        Clock.Advance(20_000);
        // b sonó UNA vez (el disparo programado), no dos
        Assert.Single(Audio.Plays, p => p.CueId == b.Id);
    }

    [Fact]
    public void CorteLimpio_ParaLoQueSonabaAntes()
    {
        var old = AddCue(CueType.Audio, "viejo", duration: 100.0);
        Controller.OnGoPressed();
        Assert.True(Audio.IsPlaying(old.Id));

        var v = AddCue(CueType.Video, "nuevo", duration: 60.0);
        Controller.PlayFromTimeline(new[]
        { new MainController.TimelinePlayItem(v.Id, 10.0, 0.0) });

        Assert.False(Audio.IsPlaying(old.Id));
        Assert.True(Video.IsPlaying(v.Id));
    }
}
