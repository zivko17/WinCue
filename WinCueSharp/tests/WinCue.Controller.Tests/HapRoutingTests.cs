// =============================================================================
// HapRoutingTests.cs — Enrutado del MOTOR HAP PROPIO (paso 3)
// =============================================================================
// Un cue HAP en .mov con rate 1.0 y compositor activo va por el motor propio:
// el vídeo con UseHapEngine=true y su AUDIO por el AudioEngine con id espejo
// negativo (HapAudioId). Todo lo demás (sin compositor, rate≠1, canvas,
// kill-switch) sigue por la ruta clásica de siempre. La caché del sondeo de
// códec se SIEMBRA (SeedUnsupportedCodecCache): determinista, sin ffprobe.
//
// Todos los tests viven en UNA clase: xUnit los ejecuta en serie, y el test
// del kill-switch (variable de entorno de proceso) no puede correr en
// paralelo con los demás.
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class HapRoutingTests : ControllerTestBase
{
    /// <summary>Cue de vídeo HAP .mov (caché sembrada) con compositor activo.</summary>
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

    [Fact]
    public void HapMov_ConCompositor_EnrutaMotorPropio_YArrancaAudioEspejo()
    {
        var cue = AddHapCue("clip", c => { c.Volume = 0.8; c.TrimStart = 1.5; c.Loop = true; });
        Controller.OnGoPressed();

        var vp = Assert.Single(Video.Plays);
        Assert.True(vp.P.UseHapEngine);
        Assert.False(vp.P.ForceClassicPath);

        int hapId = MainController.HapAudioId(cue.Id);
        Assert.True(hapId < MainController.AuditionCueId);   // jamás colisiona
        var ap = Assert.Single(Audio.Plays, p => p.CueId == hapId);
        Assert.Equal(cue.FilePath, ap.Path);
        Assert.Equal(0.8, ap.P.Volume);
        Assert.Equal(1.5, ap.P.TrimStart);
        Assert.True(ap.P.Loop);
        Assert.Equal(1.0, ap.P.Rate);        // la elegibilidad garantiza rate 1
        Assert.NotNull(ap.P.Eq);             // el rack entero desde el arranque
    }

    [Fact]
    public void SinCompositor_RutaClasica_SinAudioEspejo()
    {
        var cue = AddHapCue("clasico");
        Settings.Set("video_compositor", false);
        Controller.OnGoPressed();

        var vp = Assert.Single(Video.Plays);
        Assert.False(vp.P.UseHapEngine);
        Assert.True(vp.P.ForceClassicPath);
        Assert.DoesNotContain(Audio.Plays, p => p.CueId == MainController.HapAudioId(cue.Id));
    }

    [Fact]
    public void RateDistintoDe1_RutaClasica()
    {
        var cue = AddHapCue("rapido", c => c.Rate = 1.5);
        Controller.OnGoPressed();

        Assert.False(Video.Plays[0].P.UseHapEngine);
        Assert.True(Video.Plays[0].P.ForceClassicPath);
        Assert.DoesNotContain(Audio.Plays, p => p.CueId == MainController.HapAudioId(cue.Id));
    }

    [Fact]
    public void KillSwitch_VariableDeEntorno_VuelveARutaClasica()
    {
        AddHapCue("seguro");
        Environment.SetEnvironmentVariable("WINCUE_HAP_ENGINE", "0");
        try
        {
            Controller.OnGoPressed();
        }
        finally
        {
            Environment.SetEnvironmentVariable("WINCUE_HAP_ENGINE", null);
        }

        Assert.False(Video.Plays[0].P.UseHapEngine);
        Assert.True(Video.Plays[0].P.ForceClassicPath);
    }

    [Fact]
    public void VideoNoHap_NoTocaElAudioEspejo()
    {
        Settings.Set("video_compositor", true);
        var cue = AddCue(CueType.Video, "normal", duration: 5.0,
            c => c.FilePath = Path.Combine(TempDir, "normal.mp4"));
        Controller.OnGoPressed();

        Assert.False(Video.Plays[0].P.UseHapEngine);
        Assert.Empty(Audio.Plays);
        Assert.DoesNotContain(Audio.Preloads, id => id <= MainController.HapAudioBase);
        _ = cue;
    }

    [Fact]
    public void Stop_ParaVideoYAudioEspejo()
    {
        var cue = AddHapCue("parada");
        Controller.OnGoPressed();
        Controller.StopCueById(cue.Id);

        Assert.Contains(cue.Id, Video.Stops);
        Assert.Contains(Audio.Stops, s => s.CueId == MainController.HapAudioId(cue.Id));
    }

    [Fact]
    public void Panic_FundeYParaAmbos()
    {
        var cue = AddHapCue("panico");
        Controller.OnGoPressed();
        Controller.OnPanic();

        // El vídeo se retira con fade (stopAfter) y el audio espejo con el
        // MISMO fundido; el barrido StopAll del motor de audio también corre
        Assert.Contains(Video.Fades, f => f.CueId == cue.Id && f.StopAfter);
        Assert.Contains(Audio.Stops, s =>
            s.CueId == MainController.HapAudioId(cue.Id) && s.Fade > 0.0);
    }

    [Fact]
    public void FinNaturalDelVideo_ParaElAudioEspejo()
    {
        var cue = AddHapCue("fin");
        Controller.OnGoPressed();
        Video.EndNaturally(cue.Id);

        Assert.Contains(Audio.Stops, s => s.CueId == MainController.HapAudioId(cue.Id));
        Assert.Empty(Controller.RunningCueIds);
    }

    [Fact]
    public void PausaPorCue_PausaYReanudaAmbos()
    {
        var cue = AddHapCue("pausa");
        Controller.OnGoPressed();
        int hapId = MainController.HapAudioId(cue.Id);

        Controller.OnPauseCueToggle(0);
        Assert.Contains(cue.Id, Video.PausedCues);
        Assert.Contains(hapId, Audio.PausedCues);

        Controller.OnPauseCueToggle(0);
        Assert.DoesNotContain(cue.Id, Video.PausedCues);
        Assert.DoesNotContain(hapId, Audio.PausedCues);
    }

    [Fact]
    public void VolumenYEqEnVivo_VanAlAudioEspejo()
    {
        var cue = AddHapCue("vivo");
        Controller.OnGoPressed();
        int hapId = MainController.HapAudioId(cue.Id);

        Controller.OnCueEditCommitted(0, new Dictionary<string, object?>
        { ["volume"] = 0.4 });
        Assert.Equal(0.4, Audio.Volumes[hapId]);

        Controller.OnCueEditCommitted(0, new Dictionary<string, object?>
        { ["gain_db"] = 3.0 });
        Assert.Contains(Audio.Eqs, e => e.CueId == hapId && e.Eq.GainDb == 3.0);
    }

    [Fact]
    public void CueLevelReport_DelIdEspejo_SeRemapeaAlCueReal()
    {
        var cue = AddHapCue("vu");
        Controller.OnGoPressed();

        Audio.RaiseCueLevel(MainController.HapAudioId(cue.Id), 0.5f, 0.25f);
        Assert.True(View.CueVuLevels.TryGetValue(cue.Id, out float v));
        Assert.Equal(0.5f, v);
    }

    [Fact]
    public void ErrorDelAudioEspejo_NoMarcaElCueEnError()
    {
        var cue = AddHapCue("mudo");
        Controller.OnGoPressed();

        // .mov sin pista de audio: el Play del espejo emite error → tragado
        Audio.RaiseError(MainController.HapAudioId(cue.Id), "sin pista de audio");
        Assert.NotEqual(CueState.Error, Model.Cues[0].State);
        Assert.Contains(cue.Id, Controller.RunningCueIds);   // el vídeo sigue
    }

    [Fact]
    public void Standby_DeCueElegible_PrecargaElAudioEspejo()
    {
        var cue = AddHapCue("standby");
        Controller.OnSetPlayhead(0);

        Assert.Contains(cue.Id, Video.Preloaded);            // verificación clásica
        Assert.Contains(MainController.HapAudioId(cue.Id), Audio.Preloads);
        Assert.Empty(Video.Prepared);                        // sin standby GPU con HAP
    }

    [Fact]
    public void FadeCue_SobreCueHap_FundeElAudioEspejo()
    {
        var cue = AddHapCue("objetivo");
        Controller.OnGoPressed();

        var fade = AddCue(CueType.Fade, "fundido", duration: 2.0, c =>
        {
            c.TargetId = cue.Id;
            c.Volume = 0.0;
            c.FilePath = "";
        });
        Controller.OnSetPlayhead(1);
        Controller.OnGoPressed();

        Assert.Contains(Audio.Fades, f =>
            f.CueId == MainController.HapAudioId(cue.Id) && f.Target == 0.0);
        _ = fade;
    }
}
