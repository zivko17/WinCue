// =============================================================================
// ProResFallbackTests.cs — El fallback a mpv del motor ProRes, con mpv REAL
// =============================================================================
// La regla de oro del enrutado: si el motor propio no puede, el cue SIGUE
// VIÉNDOSE. Nunca negro. Y como su audio ya lo está reproduciendo el
// AudioEngine (espejo), mpv tiene que entrar MUDO y sin pista de audio: nunca
// audio doble.
//
// Se ejercita con el engine headless (vo=null, sin proveedor de superficies del
// compositor), que es exactamente la condición «no hay compositor GPU» → el
// motor propio no arranca y la ruta de emergencia debe recoger el cue.
// =============================================================================

using WinCue.Core;

namespace WinCue.Video.Tests;

public class ProResFallbackTests : IClassFixture<ProResMediaFixture>
{
    private readonly ProResMediaFixture _media;

    public ProResFallbackTests(ProResMediaFixture media) => _media = media;

    private bool Ready => VideoEngine.MpvAvailable && _media.FFmpegOk;

    private static VideoEngine MakeEngine() =>
        new(getWindowHandle: () => 0, forcedVo: "null", nullAudio: true);

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(15);
        }

        return condition();
    }

    [Fact]
    public void SinCompositor_ElCueProResSigueViendose_PorMpv()
    {
        if (!Ready) return;
        using var engine = MakeEngine();
        var errores = new List<string>();
        engine.ErrorOccurred += (_, m) => errores.Add(m);

        engine.Play(1, _media.P422Hq, new VideoPlayParams(UseProResEngine: true, Volume: 0.9));

        Assert.True(WaitFor(() => engine.GetState(1) == MediaCueState.Playing, 15_000),
            $"el cue no llegó a PLAYING tras el fallback (estado: {engine.GetState(1)})");
        Assert.True(engine.IsPlaying(1));
        // No entró por el motor propio: no hay compositor GPU en este engine
        Assert.Equal(0, engine.ProResEnginePlayerCount);
        // …y no se marcó ERROR por el camino
        Assert.DoesNotContain(errores, m => !m.StartsWith("[AVISO]", StringComparison.Ordinal));
    }

    [Fact]
    public void ElCueProResQueCaeAMpv_AvanzaSuReloj()
    {
        if (!Ready) return;
        using var engine = MakeEngine();
        engine.Play(2, _media.P422, new VideoPlayParams(UseProResEngine: true));

        Assert.True(WaitFor(() => engine.GetState(2) == MediaCueState.Playing, 15_000));
        Assert.True(WaitFor(() => engine.GetElapsed(2) > 0.05, 10_000),
            "el reloj del cue no avanzó tras el fallback a mpv");
    }

    [Fact]
    public void UnFicheroQueNoEsProRes_MarcadoComoProRes_TambienCaeAMpv()
    {
        // Enrutado equivocado (caché de códec envenenada, archivo sustituido en
        // caliente): el motor propio rechaza el fourcc y el cue debe seguir
        // viéndose igual. Es la red que impide que un error de sondeo deje una
        // salida en negro.
        if (!Ready) return;
        using var engine = MakeEngine();
        engine.Play(3, _media.ConAudio, new VideoPlayParams(UseProResEngine: true));

        Assert.True(WaitFor(() => engine.GetState(3) == MediaCueState.Playing, 15_000));
        Assert.Equal(0, engine.ProResEnginePlayerCount);
    }

    [Fact]
    public void StopDeUnCueQueCayoAMpv_LoRetiraLimpiamente()
    {
        if (!Ready) return;
        using var engine = MakeEngine();
        engine.Play(4, _media.P422, new VideoPlayParams(UseProResEngine: true, Loop: true));
        Assert.True(WaitFor(() => engine.GetState(4) == MediaCueState.Playing, 15_000));

        engine.Stop(4);
        Assert.Equal(MediaCueState.Stopped, engine.GetState(4));
        Assert.False(engine.IsPlaying(4));
        Assert.Equal(0, engine.FrozenSurfaceCount);
    }
}
