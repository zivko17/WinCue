// =============================================================================
// FadeAllVisualTests.cs — FADE ALL / PANIC funden también la IMAGEN
// =============================================================================
// Reportado en directo: «el botón de fade all no funciona, se corta la imagen
// pero el audio sí que hace el fade». La rampa de opacidad SÍ se programaba en
// EndCue, pero el retroceso del playhead que hace StopEverything volvía a
// preparar el standby del cue que acababa de salir del aire y le fijaba la
// opacidad a 0 SIN fundido — matando la rampa recién empezada.
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class FadeAllVisualTests : ControllerTestBase
{
    [Fact]
    public void FadeAll_ProgramaLaRampaDeOpacidadYNadieLaPisa()
    {
        var v = AddCue(CueType.Video, "clip", duration: 100.0);
        View.VisibleOutputs.Add(0);
        Settings.Set("fade_all_seconds", 5.0);

        Controller.OnGoPressed();
        View.CompositorLog.Clear();

        Controller.OnFadeAll();

        // El vídeo funde su opacidad a 0 en los 5 s del FADE ALL...
        var ramp = Assert.Single(View.CompositorLog.Where(
            e => e.Op == "opacity" && e.CueId == v.Id && e.Fade > 0.0));
        Assert.Equal(0.0, ramp.Value);
        Assert.Equal(5.0, ramp.Fade, 2);

        // ...y NADIE toca su opacidad después: un SetVideoCueOpacity sin fundido
        // corta la animación en seco (BeginAnimation(null)) = corte de imagen.
        int rampIdx = View.CompositorLog.FindIndex(
            e => e.Op == "opacity" && e.CueId == v.Id && e.Fade > 0.0);
        Assert.DoesNotContain(View.CompositorLog.Skip(rampIdx + 1),
            e => e.Op == "opacity" && e.CueId == v.Id);
    }

    [Fact]
    public void FadeAll_NoPreparaElStandbyDelCueQueSigueEnPantalla()
    {
        var v = AddCue(CueType.Video, "clip", duration: 100.0);
        View.VisibleOutputs.Add(0);
        Settings.Set("fade_all_seconds", 5.0);

        Controller.OnGoPressed();
        Video.Prepared.Clear();

        Controller.OnFadeAll();

        // Preparar el standby de un cue cuya imagen sigue fundiéndose le roba
        // la superficie del compositor (AssignCue reinicia opacidad y z).
        Assert.DoesNotContain(Video.Prepared, p => p.CueId == v.Id);
    }

    [Fact]
    public void FadeAll_TrasElFundido_ElCueVuelveAPrepararse()
    {
        var v = AddCue(CueType.Video, "clip", duration: 100.0);
        View.VisibleOutputs.Add(0);
        Settings.Set("fade_all_seconds", 5.0);

        Controller.OnGoPressed();
        Controller.OnFadeAll();
        Video.Prepared.Clear();

        Clock.Advance(5_000);   // fin del fundido: la imagen ya no está

        // Ahora sí: el playhead está sobre ese cue y el GO siguiente debe ser
        // instantáneo → standby preparado.
        Assert.Contains(Video.Prepared, p => p.CueId == v.Id);
    }

    [Fact]
    public void Panic_ConFundido_TambienFundeLaImagen()
    {
        var v = AddCue(CueType.Video, "clip", duration: 100.0);
        View.VisibleOutputs.Add(0);
        Settings.Set("panic_fade", 1.5);

        Controller.OnGoPressed();
        View.CompositorLog.Clear();

        Controller.OnPanic();

        var ramp = Assert.Single(View.CompositorLog.Where(
            e => e.Op == "opacity" && e.CueId == v.Id && e.Fade > 0.0));
        Assert.Equal(1.5, ramp.Fade, 2);
        int rampIdx = View.CompositorLog.FindIndex(
            e => e.Op == "opacity" && e.CueId == v.Id && e.Fade > 0.0);
        Assert.DoesNotContain(View.CompositorLog.Skip(rampIdx + 1),
            e => e.Op == "opacity" && e.CueId == v.Id);
    }

    [Fact]
    public void StopAllSinFundido_SiguePreparandoElStandby()
    {
        // Sin fundido no hay imagen que proteger: el STOP ALL debe seguir
        // dejando el cue listo para el GO siguiente (GO instantáneo).
        var v = AddCue(CueType.Video, "clip", duration: 100.0);
        View.VisibleOutputs.Add(0);

        Controller.OnGoPressed();
        Video.Prepared.Clear();

        Controller.OnStopPressed();

        Assert.Contains(Video.Prepared, p => p.CueId == v.Id);
    }
}
