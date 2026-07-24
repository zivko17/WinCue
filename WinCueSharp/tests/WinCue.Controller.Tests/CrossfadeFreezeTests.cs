// =============================================================================
// CrossfadeFreezeTests.cs — el congelado del saliente se SUELTA siempre
// =============================================================================
// Cuando el vídeo saliente de un crossfade acaba DENTRO del fundido, el motor
// deja su superficie en pantalla con el último fotograma (si no, la mezcla
// cae sobre negro: luma medida 46 con las mesetas en 66/197). Ese congelado
// es una liberación DIFERIDA, y la lección cara de este proyecto (auditoría
// 05) es que una liberación que no llega deja el pool sin superficies.
//
// Aquí se comprueba, sin GPU, que el controlador pide la liberación explícita
// en TODOS sus caminos de cierre: STOP ALL, PANIC (fundido y corte duro),
// FADE ALL, STOP global, recarga de show y reproducción desde el timeline.
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class CrossfadeFreezeTests : ControllerTestBase
{
    /// <summary>Deja un crossfade en marcha: saliente + entrante con fade_in
    /// en la misma pista (lo que activa el handover en el motor).</summary>
    private void ArrancarCrossfade()
    {
        AddCue(CueType.Video, "saliente", duration: 30.0,
            setup: c => c.TimelineLayer = 2);
        AddCue(CueType.Video, "entrante", duration: 30.0,
            setup: c => { c.TimelineLayer = 2; c.FadeIn = 8.0; });
        Controller.OnGoPressed();
        Controller.OnGoPressed();
        Video.FrozenReleases.Clear();
    }

    [Fact]
    public void StopAll_SueltaTodosLosCongelados()
    {
        ArrancarCrossfade();

        Controller.OnStopPressed();

        Assert.Contains(-1, Video.FrozenReleases);   // -1 = todos
    }

    [Fact]
    public void Panic_ConFundido_SueltaTodosLosCongelados()
    {
        // El PANIC con fundido NO pasa por StopAll del motor (los vídeos se
        // funden por cue): sin la petición explícita, el congelado se
        // quedaría reservado hasta su tope de seguridad.
        ArrancarCrossfade();

        Controller.OnPanic();

        Assert.Contains(-1, Video.FrozenReleases);
    }

    [Fact]
    public void Panic_DobleCorteDuro_SueltaTodosLosCongelados()
    {
        ArrancarCrossfade();

        Controller.OnPanic();
        Video.FrozenReleases.Clear();
        Controller.OnPanic();   // segunda pulsación en <2 s = corte duro

        Assert.Contains(-1, Video.FrozenReleases);
    }

    [Fact]
    public void FadeAll_SueltaTodosLosCongelados()
    {
        ArrancarCrossfade();

        Controller.OnFadeAll();

        Assert.Contains(-1, Video.FrozenReleases);
    }

    [Fact]
    public void StopGlobal_DesdeUnCueStop_SueltaTodosLosCongelados()
    {
        AddCue(CueType.Video, "saliente", duration: 30.0,
            setup: c => c.TimelineLayer = 2);
        AddCue(CueType.Video, "entrante", duration: 30.0,
            setup: c => { c.TimelineLayer = 2; c.FadeIn = 8.0; });
        AddCue(CueType.Stop, "stop global");   // sin target = parar todo
        Controller.OnGoPressed();
        Controller.OnGoPressed();
        Video.FrozenReleases.Clear();

        Controller.OnGoPressed();

        Assert.Contains(-1, Video.FrozenReleases);
    }

    [Fact]
    public void RecargaDeShow_SueltaTodosLosCongelados()
    {
        ArrancarCrossfade();
        string path = Path.Combine(TempDir, "show.json");
        Controller.SaveShow(path);
        Video.FrozenReleases.Clear();

        Controller.LoadShow(path);

        Assert.Contains(-1, Video.FrozenReleases);
    }

    [Fact]
    public void PlayFromTimeline_SueltaTodosLosCongelados()
    {
        ArrancarCrossfade();

        Controller.PlayFromTimeline(Array.Empty<MainController.TimelinePlayItem>());

        Assert.Contains(-1, Video.FrozenReleases);
    }

    [Fact]
    public void CierreDeLaApp_LimpiaLosMotores()
    {
        // Shutdown va por Cleanup(), que suelta los congelados dentro del motor
        ArrancarCrossfade();

        Controller.Shutdown();

        Assert.Equal(1, Video.CleanupCalls);
    }
}
