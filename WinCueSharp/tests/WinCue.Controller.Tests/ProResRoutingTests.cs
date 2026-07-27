// =============================================================================
// ProResRoutingTests.cs — Enrutado del MOTOR ProRes PROPIO (paso 2 de ProRes)
// =============================================================================
// Un cue ProRes en .mov con rate 1.0 y compositor activo va por el motor
// propio: el vídeo con UseProResEngine=true y su AUDIO por el AudioEngine con
// id espejo negativo (VideoAudioId), que además hace de reloj maestro.
//
// Todo lo demás vuelve a la ruta de siempre, y son MUCHAS ramas porque este
// motor tiene DOS apagados (el interruptor de Ajustes que pidió Andrés y el
// kill-switch de entorno) además de las condiciones de elegibilidad. Con el
// motor apagado un ProRes con duración conocida sigue yendo por la RUTA
// HÍBRIDA (mpv mudo + audio por el rack), que es lo que hace hoy: apagar el
// motor propio no debe degradar el audio.
//
// La caché del sondeo de códec se SIEMBRA (SeedProResCache): determinista, sin
// ffprobe ni archivo real. Todos los tests en UNA clase porque el del
// kill-switch toca una variable de entorno de proceso y no puede correr en
// paralelo con los demás.
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class ProResRoutingTests : ControllerTestBase
{
    /// <summary>Cue de vídeo ProRes .mov (caché sembrada) con compositor activo.</summary>
    private Cue AddProResCue(string name = "prores", double duration = 10.0,
        Action<Cue>? setup = null)
    {
        Settings.Set("video_compositor", true);
        string path = Path.Combine(TempDir, $"{name}.mov");
        MediaImporter.SeedProResCache(path, isProRes: true);
        MediaImporter.SeedUnsupportedCodecCache(path, unsupported: false);   // no es HAP
        return AddCue(CueType.Video, name, duration, c =>
        {
            c.FilePath = path;
            setup?.Invoke(c);
        });
    }

    [Fact]
    public void ProResMov_ConCompositor_EnrutaMotorPropio_YArrancaAudioEspejo()
    {
        var cue = AddProResCue("clip", setup: c =>
        {
            c.Volume = 0.8;
            c.TrimStart = 1.5;
            c.Loop = true;
        });
        Controller.OnGoPressed();

        var vp = Assert.Single(Video.Plays);
        Assert.True(vp.P.UseProResEngine);
        Assert.False(vp.P.UseHapEngine);
        // A diferencia de HAP, ProRes NO fuerza la ruta clásica: mpv sabe
        // renderizarlo, así que el fallback conserva el compositor.
        Assert.False(vp.P.ForceClassicPath);

        int mirrorId = MainController.VideoAudioId(cue.Id);
        Assert.True(mirrorId < MainController.AuditionCueId);   // jamás colisiona
        var ap = Assert.Single(Audio.Plays, p => p.CueId == mirrorId);
        Assert.Equal(cue.FilePath, ap.Path);
        Assert.Equal(0.8, ap.P.Volume);
        Assert.Equal(1.5, ap.P.TrimStart);
        Assert.True(ap.P.Loop);
        Assert.Equal(1.0, ap.P.Rate);        // la elegibilidad garantiza rate 1
        Assert.NotNull(ap.P.Eq);             // el rack entero desde el arranque
    }

    [Fact]
    public void InterruptorDeAjustesApagado_VuelveAMpv()
    {
        // EL INTERRUPTOR QUE PIDIÓ ANDRÉS: «por si el PC en el que corra da
        // problemas». Apagado, ProRes va por mpv exactamente como hasta hoy.
        var cue = AddProResCue("sin-motor");
        Settings.Set("video_prores_engine", false);
        Controller.OnGoPressed();

        var vp = Assert.Single(Video.Plays);
        Assert.False(vp.P.UseProResEngine);
        // …pero NO se pierde el audio por el rack: el híbrido sigue en pie
        Assert.True(vp.P.ExternalAudio);
        Assert.Contains(Audio.Plays, p => p.CueId == MainController.VideoAudioId(cue.Id));
    }

    [Fact]
    public void InterruptorDeAjustes_PorDefectoEncendido()
    {
        // El default de fábrica es true: quien no toque nada tiene el motor.
        Assert.True(Settings.Get("video_prores_engine", false));
    }

    [Fact]
    public void KillSwitch_VariableDeEntorno_VuelveAMpv()
    {
        var cue = AddProResCue("kill");
        Environment.SetEnvironmentVariable("WINCUE_PRORES_ENGINE", "0");
        try
        {
            Controller.OnGoPressed();
        }
        finally
        {
            Environment.SetEnvironmentVariable("WINCUE_PRORES_ENGINE", null);
        }

        Assert.False(Video.Plays[0].P.UseProResEngine);
        Assert.True(Video.Plays[0].P.ExternalAudio);
        _ = cue;
    }

    [Fact]
    public void SinCompositor_NoHayMotorPropio()
    {
        var cue = AddProResCue("clasico");
        Settings.Set("video_compositor", false);
        Controller.OnGoPressed();

        Assert.False(Video.Plays[0].P.UseProResEngine);
        _ = cue;
    }

    [Fact]
    public void RateDistintoDe1_NoHayMotorPropio_NiAudioEspejo()
    {
        // El varispeed retima el audio en mpv (resample + pitch) y el espejo no
        // lo replica: ruta clásica CON audio, como siempre.
        var cue = AddProResCue("rapido", setup: c => c.Rate = 1.5);
        Controller.OnGoPressed();

        Assert.False(Video.Plays[0].P.UseProResEngine);
        Assert.False(Video.Plays[0].P.ExternalAudio);
        Assert.DoesNotContain(Audio.Plays, p => p.CueId == MainController.VideoAudioId(cue.Id));
    }

    [Fact]
    public void GeometriaCustom_NoHayMotorPropio()
    {
        // El motor propio solo sabe letterbox + escala sobre la superficie
        // FULL; pan/zoom/rotación arbitrarios siguen siendo de mpv.
        var cue = AddProResCue("custom", setup: c => c.GeoMode = GeoMode.Custom);
        Controller.OnGoPressed();

        Assert.False(Video.Plays[0].P.UseProResEngine);
        _ = cue;
    }

    [Fact]
    public void ModoCanvas_NoHayMotorPropio()
    {
        var cue = AddProResCue("canvas");
        Settings.Set("video_canvas_mode", true);
        Controller.OnGoPressed();

        Assert.False(Video.Plays[0].P.UseProResEngine);
        _ = cue;
    }

    [Fact]
    public void UnMp4ConProRes_NoEntra_ElDemuxerEsDeQuickTime()
    {
        // El demuxer propio es de contenedor QuickTime: fuera del .mov no hay
        // motor propio aunque el códec sea ProRes.
        Settings.Set("video_compositor", true);
        string path = Path.Combine(TempDir, "raro.mp4");
        MediaImporter.SeedProResCache(path, isProRes: true);
        var cue = AddCue(CueType.Video, "mp4", duration: 10.0, c => c.FilePath = path);
        Controller.OnGoPressed();

        Assert.False(Video.Plays[0].P.UseProResEngine);
        _ = cue;
    }

    [Fact]
    public void UnVideoQueNoEsProRes_NoEntra()
    {
        Settings.Set("video_compositor", true);
        string path = Path.Combine(TempDir, "h264.mov");
        MediaImporter.SeedProResCache(path, isProRes: false);
        MediaImporter.SeedUnsupportedCodecCache(path, unsupported: false);
        var cue = AddCue(CueType.Video, "h264", duration: 10.0, c => c.FilePath = path);
        Controller.OnGoPressed();

        Assert.False(Video.Plays[0].P.UseProResEngine);
        _ = cue;
    }

    [Fact]
    public void UnHap_SigueYendoPorElMotorHap_NoPorElDeProRes()
    {
        // Las dos elegibilidades no pueden pisarse: HAP se decide antes.
        Settings.Set("video_compositor", true);
        string path = Path.Combine(TempDir, "hap.mov");
        MediaImporter.SeedUnsupportedCodecCache(path, unsupported: true);
        MediaImporter.SeedProResCache(path, isProRes: false);
        var cue = AddCue(CueType.Video, "hap", duration: 10.0, c => c.FilePath = path);
        Controller.OnGoPressed();

        Assert.True(Video.Plays[0].P.UseHapEngine);
        Assert.False(Video.Plays[0].P.UseProResEngine);
        _ = cue;
    }

    // ------------------------------------------------------------------
    // Ciclo de vida con el audio espejo (mismo contrato que HAP)
    // ------------------------------------------------------------------

    [Fact]
    public void Stop_ParaVideoYAudioEspejo()
    {
        var cue = AddProResCue("parada");
        Controller.OnGoPressed();
        Controller.StopCueById(cue.Id);

        Assert.Contains(cue.Id, Video.Stops);
        Assert.Contains(Audio.Stops, s => s.CueId == MainController.VideoAudioId(cue.Id));
    }

    [Fact]
    public void FinNaturalDelVideo_ParaElAudioEspejo()
    {
        var cue = AddProResCue("fin");
        Controller.OnGoPressed();
        Video.EndNaturally(cue.Id);

        Assert.Contains(Audio.Stops, s => s.CueId == MainController.VideoAudioId(cue.Id));
        Assert.Empty(Controller.RunningCueIds);
    }

    [Fact]
    public void PausaPorCue_PausaYReanudaAmbos()
    {
        var cue = AddProResCue("pausa");
        Controller.OnGoPressed();
        int mirrorId = MainController.VideoAudioId(cue.Id);

        Controller.OnPauseCueToggle(0);
        Assert.Contains(cue.Id, Video.PausedCues);
        Assert.Contains(mirrorId, Audio.PausedCues);

        Controller.OnPauseCueToggle(0);
        Assert.DoesNotContain(cue.Id, Video.PausedCues);
        Assert.DoesNotContain(mirrorId, Audio.PausedCues);
    }

    [Fact]
    public void VolumenYEqEnVivo_VanAlAudioEspejo()
    {
        var cue = AddProResCue("vivo");
        Controller.OnGoPressed();
        int mirrorId = MainController.VideoAudioId(cue.Id);

        Controller.OnCueEditCommitted(0, new Dictionary<string, object?> { ["volume"] = 0.4 });
        Assert.Equal(0.4, Audio.Volumes[mirrorId]);

        Controller.OnCueEditCommitted(0, new Dictionary<string, object?> { ["gain_db"] = 3.0 });
        Assert.Contains(Audio.Eqs, e => e.CueId == mirrorId && e.Eq.GainDb == 3.0);
    }

    [Fact]
    public void CueLevelReport_DelIdEspejo_SeRemapeaAlCueReal()
    {
        var cue = AddProResCue("vu");
        Controller.OnGoPressed();

        Audio.RaiseCueLevel(MainController.VideoAudioId(cue.Id), 0.5f, 0.25f);
        Assert.True(View.CueVuLevels.TryGetValue(cue.Id, out float v));
        Assert.Equal(0.5f, v);
    }

    [Fact]
    public void ErrorDelAudioEspejo_NoMarcaElCueEnError()
    {
        var cue = AddProResCue("mudo");
        Controller.OnGoPressed();

        // .mov sin pista de audio: el Play del espejo emite error → tragado
        Audio.RaiseError(MainController.VideoAudioId(cue.Id), "sin pista de audio");
        Assert.NotEqual(CueState.Error, Model.Cues[0].State);
        Assert.Contains(cue.Id, Controller.RunningCueIds);   // el vídeo sigue
    }

    [Fact]
    public void Panic_FundeYParaAmbos()
    {
        var cue = AddProResCue("panico");
        Controller.OnGoPressed();
        Controller.OnPanic();

        Assert.Contains(Video.Fades, f => f.CueId == cue.Id && f.StopAfter);
        Assert.Contains(Audio.Stops, s =>
            s.CueId == MainController.VideoAudioId(cue.Id) && s.Fade > 0.0);
    }

    [Fact]
    public void Standby_PrecargaElAudioEspejo_YNoPreparaStandbyGpu()
    {
        // El standby GPU dejaría preparado un player de MPV que NO es el que va
        // a reproducir, y encima le robaría la superficie del pool al motor
        // propio. Se precalienta el audio espejo, que es el reloj maestro.
        var cue = AddProResCue("standby");
        Controller.OnSetPlayhead(0);

        Assert.Contains(cue.Id, Video.Preloaded);
        Assert.Contains(MainController.VideoAudioId(cue.Id), Audio.Preloads);
        Assert.Empty(Video.Prepared);
    }

    [Fact]
    public void FadeCue_SobreCueProRes_FundeElAudioEspejo()
    {
        var cue = AddProResCue("objetivo");
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
            f.CueId == MainController.VideoAudioId(cue.Id) && f.Target == 0.0);
        _ = fade;
    }
}
