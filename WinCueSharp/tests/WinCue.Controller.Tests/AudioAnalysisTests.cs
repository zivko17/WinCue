// =============================================================================
// AudioAnalysisTests.cs — CueHasAudioAnalysis: ¿este cue se puede MEDIR?
// =============================================================================
// El inspector enseña el rack (ganancia + EQ + compresor) tanto para AUDIO
// como para VÍDEO, pero los MEDIDORES (VU por cue, RTA, GR) solo existen si el
// audio pasa por el rack nativo:
//   · AUDIO — siempre.
//   · VÍDEO con AUDIO ESPEJO (motor HAP propio o ruta híbrida) — sí.
//   · VÍDEO cuyo audio saca mpv (rate ≠ 1, imagen, sin compositor,
//     kill-switch…) — NO: la EQ actúa por la cadena af, pero no hay analizador.
//   · Tipos sin audio (MEMO/WAIT/FADE…) — no.
// El predicado responde ANTES del GO (decisión de enrutado predicha) y también
// con el cue sonando (el set real de espejos manda).
//
// Todos los tests en UNA clase: el de kill-switch toca variables de entorno de
// proceso y no puede correr en paralelo con los demás.
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class AudioAnalysisTests : ControllerTestBase
{
    private Cue AddHapCue(string name = "hap", Action<Cue>? setup = null)
    {
        Settings.Set("video_compositor", true);
        string path = Path.Combine(TempDir, $"{name}.mov");
        MediaImporter.SeedUnsupportedCodecCache(path, unsupported: true);
        return AddCue(CueType.Video, name, duration: 10.0, c =>
        {
            c.FilePath = path;
            setup?.Invoke(c);
        });
    }

    private Cue AddH264Cue(string name = "clip", double duration = 10.0,
        Action<Cue>? setup = null) =>
        AddCue(CueType.Video, name, duration, c =>
        {
            c.FilePath = Path.Combine(TempDir, $"{name}.mp4");
            setup?.Invoke(c);
        });

    [Fact]
    public void CueDeAudio_SiempreTieneAnalizador()
    {
        var cue = AddCue(CueType.Audio, "tema");
        Assert.True(Controller.CueHasAudioAnalysis(cue.Id));
    }

    [Fact]
    public void TiposSinAudio_NoTienenAnalizador()
    {
        var memo = AddCue(CueType.Memo, "aviso");
        var wait = AddCue(CueType.Wait, "espera", duration: 2.0);
        var fade = AddCue(CueType.Fade, "fundido", duration: 2.0);
        Assert.False(Controller.CueHasAudioAnalysis(memo.Id));
        Assert.False(Controller.CueHasAudioAnalysis(wait.Id));
        Assert.False(Controller.CueHasAudioAnalysis(fade.Id));
    }

    [Fact]
    public void CueInexistente_NoTieneAnalizador() =>
        Assert.False(Controller.CueHasAudioAnalysis(4242));

    [Fact]
    public void VideoHapConCompositor_TieneAnalizador_AntesYDuranteElGo()
    {
        var cue = AddHapCue("clip");

        // ANTES del GO: se predice el enrutado (motor HAP → audio espejo)
        Assert.True(Controller.CueHasAudioAnalysis(cue.Id));

        Controller.OnGoPressed();
        Assert.True(Controller.CueHasAudioAnalysis(cue.Id));   // espejo sonando
    }

    [Fact]
    public void VideoHapSinCompositor_NoTieneAnalizador()
    {
        var cue = AddHapCue("clasico");
        Settings.Set("video_compositor", false);   // ruta clásica: audio por mpv

        Assert.False(Controller.CueHasAudioAnalysis(cue.Id));
        Controller.OnGoPressed();
        Assert.False(Controller.CueHasAudioAnalysis(cue.Id));
    }

    [Fact]
    public void VideoHibridoElegible_TieneAnalizador()
    {
        var cue = AddH264Cue("hibrido");
        Assert.True(Controller.CueHasAudioAnalysis(cue.Id));
        Controller.OnGoPressed();
        Assert.True(Controller.CueHasAudioAnalysis(cue.Id));
    }

    [Fact]
    public void VideoConVarispeed_NoTieneAnalizador()
    {
        // rate ≠ 1: el audio lo retima mpv → sin espejo, sin analizador
        var cue = AddH264Cue("rapido", 10.0, c => c.Rate = 1.5);
        Assert.False(Controller.CueHasAudioAnalysis(cue.Id));
        Controller.OnGoPressed();
        Assert.False(Controller.CueHasAudioAnalysis(cue.Id));
    }

    [Fact]
    public void ImagenComoVideo_NoTieneAnalizador()
    {
        var cue = AddCue(CueType.Video, "foto", duration: 10.0,
            c => c.FilePath = Path.Combine(TempDir, "foto.png"));
        Assert.False(Controller.CueHasAudioAnalysis(cue.Id));
    }

    [Fact]
    public void VideoSinArchivo_NoTieneAnalizador()
    {
        var cue = AddCue(CueType.Video, "vacio", duration: 10.0, c => c.FilePath = "");
        Assert.False(Controller.CueHasAudioAnalysis(cue.Id));
    }

    [Fact]
    public void EspejoQueMuereEnCaliente_PierdeElAnalizador()
    {
        // El espejo falla → el audio se recupera por mpv: ya no hay nada que
        // medir aunque el cue siga sonando
        var cue = AddH264Cue("caido");
        Controller.OnGoPressed();
        Assert.True(Controller.CueHasAudioAnalysis(cue.Id));

        Audio.RaiseError(MainController.VideoAudioId(cue.Id), "códec no soportado");
        Assert.False(Controller.CueHasAudioAnalysis(cue.Id));
    }

    [Fact]
    public void KillSwitchDeAudioPorMpv_QuitaElAnalizador()
    {
        var cue = AddH264Cue("seguro");
        Environment.SetEnvironmentVariable("WINCUE_VIDEO_AUDIO", "mpv");
        try
        {
            Assert.False(Controller.CueHasAudioAnalysis(cue.Id));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WINCUE_VIDEO_AUDIO", null);
        }
    }

    [Fact]
    public void GananciaDeUnCueDeVideo_LlegaAlMotorYAlEspejo()
    {
        // El fader GAIN del editor (gain_db) es un campo de EQ: con el vídeo
        // sonando debe llegar a la cadena af de mpv Y al audio espejo
        var cue = AddH264Cue("ganancia");
        Controller.OnGoPressed();
        Video.Eqs.Clear();
        Audio.Eqs.Clear();

        Controller.OnCueEditCommitted(0, new Dictionary<string, object?>
        { ["gain_db"] = -4.5 });

        Assert.Contains(Video.Eqs, e => e.CueId == cue.Id && e.Eq!.GainDb == -4.5);
        Assert.Contains(Audio.Eqs, e =>
            e.CueId == MainController.VideoAudioId(cue.Id) && e.Eq.GainDb == -4.5);
    }

    [Fact]
    public void CompresorDeUnCueDeVideoHap_LlegaAlEspejo()
    {
        var cue = AddHapCue("comp");
        Controller.OnGoPressed();
        Audio.Eqs.Clear();

        Controller.OnCueEditCommitted(0, new Dictionary<string, object?>
        {
            ["comp_enabled"] = true,
            ["comp_threshold"] = -18.0,
            ["comp_ratio"] = 4.0,
        });

        var eq = Assert.Single(Audio.Eqs,
            e => e.CueId == MainController.VideoAudioId(cue.Id)).Eq;
        Assert.NotNull(eq.Comp);
        Assert.True(eq.Comp!.Enabled);
        Assert.Equal(-18.0, eq.Comp.ThresholdDb);
        Assert.Equal(4.0, eq.Comp.Ratio);
    }
}
