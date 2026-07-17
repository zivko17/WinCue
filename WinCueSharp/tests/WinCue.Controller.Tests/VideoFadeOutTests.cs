// =============================================================================
// VideoFadeOutTests.cs — El fade_out del vídeo vive DENTRO del bloque
// =============================================================================
// Antes el fundido arrancaba AL FINAL del bloque y se extendía más allá
// (el vídeo ya no tenía contenido → "cosas raras"). Ahora empieza en
// duration - fade_out, como lo dibuja el timeline y como hace el audio.
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class VideoFadeOutTests : ControllerTestBase
{
    [Fact]
    public void FadeOut_EmpiezaEnLosUltimosSegundosDelBloque()
    {
        var v = AddCue(CueType.Video, "clip", duration: 10.0,
            setup: c => c.FadeOut = 3.0);

        Controller.OnGoPressed();
        Assert.Empty(Video.Fades);

        Clock.Advance(6_999);
        Assert.Empty(Video.Fades);           // aún no toca

        Clock.Advance(1);                    // t = 7 s = duration - fade_out
        var fade = Assert.Single(Video.Fades);
        Assert.Equal(v.Id, fade.CueId);
        Assert.Equal(0.0, fade.Target);
        Assert.Equal(3.0, fade.Duration, 2);
        Assert.False(fade.StopAfter);        // el timer de fin hace la parada

        Clock.Advance(3_000);                // t = 10 s: fin del bloque
        Assert.False(Video.IsPlaying(v.Id));
        Assert.Single(Video.Fades);          // sin SEGUNDO fade al parar
    }

    [Fact]
    public void FadeOut_ConRate_CorreEnTiempoDePared()
    {
        // 10 s de contenido a rate 2× = 5 s de pared; fade_out 2 s de
        // contenido = 1 s de pared, empezando a los 4 s de pared.
        AddCue(CueType.Video, "rapido", duration: 10.0,
            setup: c => { c.FadeOut = 2.0; c.Rate = 2.0; });

        Controller.OnGoPressed();
        Clock.Advance(3_999);
        Assert.Empty(Video.Fades);
        Clock.Advance(1);
        var fade = Assert.Single(Video.Fades);
        Assert.Equal(1.0, fade.Duration, 2);
    }

    [Fact]
    public void FadeOut_DesdeElCursor_RespetaElOffset()
    {
        // Bloque de 100 s con fade_out 10: arrancando en el s 95 (dentro de
        // la zona de fundido) el fade empieza YA con lo que queda (5 s).
        var v = AddCue(CueType.Video, "casi", duration: 100.0,
            setup: c => c.FadeOut = 10.0);

        Controller.PlayFromTimeline(new[]
        { new MainController.TimelinePlayItem(v.Id, 95.0, 0.0) });

        Clock.Advance(1);
        var fade = Assert.Single(Video.Fades);
        Assert.Equal(5.0, fade.Duration, 2);
    }

    [Fact]
    public void Audio_MantieneSuColaDeFade()
    {
        var a = AddCue(CueType.Audio, "tema", duration: 10.0,
            setup: c => c.FadeOut = 2.0);

        Controller.OnGoPressed();
        Clock.Advance(10_000);
        var stop = Assert.Single(Audio.Stops);
        Assert.Equal(a.Id, stop.CueId);
        Assert.Equal(2.0, stop.Fade);        // el motor la aplica sample-accurate
    }
}
