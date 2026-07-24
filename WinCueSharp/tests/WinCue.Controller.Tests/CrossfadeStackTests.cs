// =============================================================================
// CrossfadeStackTests.cs — El entrante de un crossfade sale AL FRENTE
// =============================================================================
// Fallo de directo cazado en julio 2026: el z de la superficie del compositor
// se fijaba cuando el cue TOMABA la superficie, y con la precarga en STANDBY
// eso ocurre mucho antes del GO. Si otro vídeo arrancaba entre medias, el
// entrante fundía su opacidad POR DEBAJO del saliente: el crossfade no se veía
// y la imagen daba un salto seco al morir el saliente.
//
// Aquí se comprueba, sin GPU, lo que sí es comprobable: que el controlador
// SUBE AL FRENTE la superficie del cue en el GO y que lo hace ANTES de tocar
// su opacidad (ni un fotograma compuesto con el orden viejo).
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class CrossfadeStackTests : ControllerTestBase
{
    [Fact]
    public void GoDeVideo_SubeSuSuperficieAlFrenteAntesDeTocarLaOpacidad()
    {
        var v = AddCue(CueType.Video, "clip", duration: 10.0);

        Controller.OnGoPressed();

        Assert.Equal(new[] { v.Id }, View.RaisedCues);
        // El primer apunte del cue en la pila es el "raise", no la opacidad
        var suyos = View.CompositorLog.Where(e => e.CueId == v.Id).ToList();
        Assert.Equal("raise", suyos[0].Op);
    }

    [Fact]
    public void CrossfadeEnLaMismaPista_ElEntranteSubeAntesDeArrancarLaRampa()
    {
        AddCue(CueType.Video, "saliente", duration: 30.0,
            setup: c => c.TimelineLayer = 2);
        var entrante = AddCue(CueType.Video, "entrante", duration: 30.0,
            setup: c => { c.TimelineLayer = 2; c.FadeIn = 2.0; });

        Controller.OnGoPressed();          // saliente
        View.CompositorLog.Clear();
        Controller.OnGoPressed();          // entrante: crossfade en la misma pista

        int raise = View.CompositorLog.FindIndex(
            e => e.Op == "raise" && e.CueId == entrante.Id);
        int rampa = View.CompositorLog.FindIndex(
            e => e.Op == "opacity" && e.CueId == entrante.Id && e.Fade > 0.0);
        Assert.True(raise >= 0, "el entrante no subió al frente");
        Assert.True(rampa >= 0, "el entrante no arrancó su rampa de opacidad");
        Assert.True(raise < rampa,
            "la rampa del entrante empezó ANTES de subirlo al frente");
        // Y la rampa dura el fade_in, desde 0
        var arranque = View.CompositorLog.First(
            e => e.Op == "opacity" && e.CueId == entrante.Id);
        Assert.Equal(0.0, arranque.Value);
        Assert.Equal(2.0, View.CompositorLog
            .First(e => e.Op == "opacity" && e.CueId == entrante.Id && e.Fade > 0.0).Fade, 2);
    }

    [Fact]
    public void CueSinFundido_TambienSubeAlFrente()
    {
        AddCue(CueType.Video, "saliente", duration: 30.0,
            setup: c => c.TimelineLayer = 2);
        var entrante = AddCue(CueType.Video, "corte", duration: 30.0,
            setup: c => c.TimelineLayer = 2);

        Controller.OnGoPressed();
        Controller.OnGoPressed();

        Assert.Contains(entrante.Id, View.RaisedCues);
    }
}
