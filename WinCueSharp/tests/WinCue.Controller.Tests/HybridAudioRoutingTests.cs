// =============================================================================
// HybridAudioRoutingTests.cs — Enrutado de la RUTA HÍBRIDA de audio (paso 3b)
// =============================================================================
// Un cue de vídeo NO-HAP (lo reproduce mpv) con rate 1.0 y duración conocida
// ≤ video_audio_max_minutes saca su AUDIO por el AudioEngine con el id espejo
// (VideoAudioId) y mpv corre MUDO (ExternalAudio=true → aid=no + PLL). Todo el
// enrutado en vivo (volumen/EQ/fades/stop/pausa/VU) es EL MISMO set que el
// motor HAP. Si el espejo falla en caliente, el controlador recupera el audio
// por mpv (RestoreInternalAudio) — nunca un cue sin audio que antes lo tenía.
//
// Todos los tests viven en UNA clase: xUnit los ejecuta en serie, y el test
// del kill-switch (variable de entorno de proceso) no puede correr en
// paralelo con los demás.
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class HybridAudioRoutingTests : ControllerTestBase
{
    /// <summary>Cue de vídeo H.264 (códec soportado por mpv: la caché de
    /// sondeo NO se siembra como unsupported) con duración conocida.</summary>
    private Cue AddH264Cue(string name = "clip", double duration = 10.0,
        Action<Cue>? setup = null) =>
        AddCue(CueType.Video, name, duration, c =>
        {
            c.FilePath = Path.Combine(TempDir, $"{name}.mp4");
            setup?.Invoke(c);
        });

    [Fact]
    public void VideoNoHap_Elegible_ArrancaEspejoYMpvMudo()
    {
        var cue = AddH264Cue("h264", 10.0, c =>
        {
            c.Volume = 0.7;
            c.TrimStart = 1.0;
            c.TrimEnd = 8.0;
            c.Loop = true;
        });
        Controller.OnGoPressed();

        var vp = Assert.Single(Video.Plays);
        Assert.True(vp.P.ExternalAudio);          // mpv MUDO (aid=no)
        Assert.False(vp.P.UseHapEngine);
        Assert.False(vp.P.ForceClassicPath);

        int mirrorId = MainController.VideoAudioId(cue.Id);
        Assert.True(mirrorId < MainController.AuditionCueId);
        var ap = Assert.Single(Audio.Plays, p => p.CueId == mirrorId);
        Assert.Equal(cue.FilePath, ap.Path);
        Assert.Equal(0.7, ap.P.Volume);
        Assert.Equal(1.0, ap.P.TrimStart);
        Assert.Equal(8.0, ap.P.TrimEnd);
        Assert.True(ap.P.Loop);
        Assert.Equal(1.0, ap.P.Rate);             // la elegibilidad lo garantiza
        Assert.NotNull(ap.P.Eq);                  // el rack entero desde el GO
    }

    [Fact]
    public void RateDistintoDe1_RutaClasicaConAudioMpv()
    {
        var cue = AddH264Cue("rapido", 10.0, c => c.Rate = 1.5);
        Controller.OnGoPressed();

        Assert.False(Video.Plays[0].P.ExternalAudio);
        Assert.DoesNotContain(Audio.Plays,
            p => p.CueId == MainController.VideoAudioId(cue.Id));
    }

    [Fact]
    public void DuracionDesconocida_RutaClasicaConAudioMpv()
    {
        var cue = AddH264Cue("sindur", 0.0);
        Controller.OnGoPressed();

        Assert.False(Video.Plays[0].P.ExternalAudio);
        Assert.DoesNotContain(Audio.Plays,
            p => p.CueId == MainController.VideoAudioId(cue.Id));
    }

    [Fact]
    public void DuracionMayorQueElUmbral_RutaClasicaConAudioMpv()
    {
        // Umbral default 20 min: el espejo decodifica a RAM — un archivo
        // largo iría a GB. 21 min → mpv clásico con su audio de siempre.
        var cue = AddH264Cue("largo", 21.0 * 60.0);
        Controller.OnGoPressed();

        Assert.False(Video.Plays[0].P.ExternalAudio);
        Assert.DoesNotContain(Audio.Plays,
            p => p.CueId == MainController.VideoAudioId(cue.Id));
    }

    [Fact]
    public void UmbralConfigurable_SubirloHabilitaElCueLargo()
    {
        Settings.Set("video_audio_max_minutes", 30.0);
        var cue = AddH264Cue("largo30", 21.0 * 60.0);
        Controller.OnGoPressed();

        Assert.True(Video.Plays[0].P.ExternalAudio);
        Assert.Contains(Audio.Plays,
            p => p.CueId == MainController.VideoAudioId(cue.Id));
    }

    [Fact]
    public void Imagen_NoArrancaEspejo()
    {
        var cue = AddCue(CueType.Video, "foto", duration: 10.0,
            c => c.FilePath = Path.Combine(TempDir, "foto.png"));
        Controller.OnGoPressed();

        Assert.False(Video.Plays[0].P.ExternalAudio);
        Assert.DoesNotContain(Audio.Plays,
            p => p.CueId == MainController.VideoAudioId(cue.Id));
    }

    [Fact]
    public void KillSwitch_VariableDeEntorno_DesactivaElHibrido()
    {
        var cue = AddH264Cue("seguro");
        Environment.SetEnvironmentVariable("WINCUE_VIDEO_AUDIO", "mpv");
        try
        {
            Controller.OnGoPressed();
        }
        finally
        {
            Environment.SetEnvironmentVariable("WINCUE_VIDEO_AUDIO", null);
        }

        Assert.False(Video.Plays[0].P.ExternalAudio);
        Assert.DoesNotContain(Audio.Plays,
            p => p.CueId == MainController.VideoAudioId(cue.Id));
    }

    [Fact]
    public void Stop_ParaVideoYEspejo()
    {
        var cue = AddH264Cue("parada");
        Controller.OnGoPressed();
        Controller.StopCueById(cue.Id);

        Assert.Contains(cue.Id, Video.Stops);
        Assert.Contains(Audio.Stops, s => s.CueId == MainController.VideoAudioId(cue.Id));
    }

    [Fact]
    public void Panic_FundeYParaAmbos()
    {
        var cue = AddH264Cue("panico");
        Controller.OnGoPressed();
        Controller.OnPanic();

        Assert.Contains(Video.Fades, f => f.CueId == cue.Id && f.StopAfter);
        Assert.Contains(Audio.Stops, s =>
            s.CueId == MainController.VideoAudioId(cue.Id) && s.Fade > 0.0);
    }

    [Fact]
    public void PausaPorCue_PausaYReanudaAmbos()
    {
        var cue = AddH264Cue("pausa");
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
    public void VolumenYEqEnVivo_VanAlEspejo()
    {
        var cue = AddH264Cue("vivo");
        Controller.OnGoPressed();
        int mirrorId = MainController.VideoAudioId(cue.Id);

        Controller.OnCueEditCommitted(0, new Dictionary<string, object?>
        { ["volume"] = 0.3 });
        Assert.Equal(0.3, Audio.Volumes[mirrorId]);

        Controller.OnCueEditCommitted(0, new Dictionary<string, object?>
        { ["gain_db"] = 6.0 });
        Assert.Contains(Audio.Eqs, e => e.CueId == mirrorId && e.Eq.GainDb == 6.0);
    }

    [Fact]
    public void CueLevelReport_DelEspejo_SeRemapeaAlCueReal()
    {
        var cue = AddH264Cue("vu");
        Controller.OnGoPressed();

        Audio.RaiseCueLevel(MainController.VideoAudioId(cue.Id), 0.6f, 0.3f);
        Assert.True(View.CueVuLevels.TryGetValue(cue.Id, out float v));
        Assert.Equal(0.6f, v);
    }

    [Fact]
    public void SnapshotActiveCues_MarcaMirrorAudio_SoloEnCuesConEspejo()
    {
        // Pistas distintas: la exclusividad por pista no debe cortar al 1º
        var hibrido = AddH264Cue("conespejo", 10.0, c => c.TimelineLayer = 1);
        var clasico = AddCue(CueType.Video, "sinespejo", duration: 0.0, c =>
        {
            c.FilePath = Path.Combine(TempDir, "sinespejo.mp4");
            c.TimelineLayer = 2;
        });
        Controller.OnGoPressed();
        Controller.OnGoPressed();

        var rows = Controller.SnapshotActiveCues(out _);
        Assert.True(Assert.Single(rows, r => r.Id == hibrido.Id).MirrorAudio);
        Assert.False(Assert.Single(rows, r => r.Id == clasico.Id).MirrorAudio);
    }

    [Fact]
    public void ErrorDelEspejo_RecuperaElAudioPorMpv_YSacaElCueDelSet()
    {
        var cue = AddH264Cue("fallback", 10.0, c => c.Volume = 0.8);
        Controller.OnGoPressed();

        // El decoder del espejo no traga el audio del archivo (raro: ambos
        // son ffmpeg — pero posible). El cue NO se marca en error, el vídeo
        // sigue, y su audio se RECUPERA por mpv (aid=auto + volumen real).
        Audio.RaiseError(MainController.VideoAudioId(cue.Id), "códec no soportado");

        Assert.NotEqual(CueState.Error, Model.Cues[0].State);
        Assert.Contains(cue.Id, Controller.RunningCueIds);
        Assert.Contains(cue.Id, Video.RestoredInternalAudio);
        Assert.Equal(0.8, Video.Volumes[cue.Id]);

        // Fuera del set: los cambios en vivo ya no van al espejo muerto…
        Controller.OnCueEditCommitted(0, new Dictionary<string, object?>
        { ["volume"] = 0.5 });
        Assert.False(Audio.Volumes.ContainsKey(MainController.VideoAudioId(cue.Id)));
        // …y el Mixer deja de anunciar VU de espejo (recreará el strip)
        var rows = Controller.SnapshotActiveCues(out _);
        Assert.False(Assert.Single(rows, r => r.Id == cue.Id).MirrorAudio);
    }

    [Fact]
    public void Standby_DeCueElegible_PreparaConExternalAudio_YPrecalientaElEspejo()
    {
        var cue = AddH264Cue("standby");
        View.VisibleOutputs.Add(0);   // salida abierta → standby GPU en pausa
        Controller.OnSetPlayhead(0);

        var prep = Assert.Single(Video.Prepared, p => p.CueId == cue.Id);
        Assert.True(prep.P.ExternalAudio);   // debe COINCIDIR con el Play
        Assert.Contains(MainController.VideoAudioId(cue.Id), Audio.Preloads);
    }

    [Fact]
    public void Standby_DeCueNoElegible_SinEspejoNiExternalAudio()
    {
        var cue = AddH264Cue("standby-rate", 10.0, c => c.Rate = 2.0);
        View.VisibleOutputs.Add(0);
        Controller.OnSetPlayhead(0);

        var prep = Assert.Single(Video.Prepared, p => p.CueId == cue.Id);
        Assert.False(prep.P.ExternalAudio);
        Assert.DoesNotContain(MainController.VideoAudioId(cue.Id), Audio.Preloads);
    }

    [Fact]
    public void FinNaturalDelVideo_ParaElEspejo()
    {
        var cue = AddH264Cue("fin");
        Controller.OnGoPressed();
        Video.EndNaturally(cue.Id);

        Assert.Contains(Audio.Stops, s => s.CueId == MainController.VideoAudioId(cue.Id));
        Assert.Empty(Controller.RunningCueIds);
    }

    [Fact]
    public void FadeCue_SobreCueHibrido_FundeElEspejo()
    {
        var cue = AddH264Cue("objetivo");
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

    [Fact]
    public void CambioDeArchivo_InvalidaTambienLaCacheDelEspejo()
    {
        var cue = AddH264Cue("recambio");
        Controller.OnCueEditCommitted(0, new Dictionary<string, object?>
        {
            ["file_path"] = Path.Combine(TempDir, "otro.mp4"),
            ["type"] = "VIDEO",
        });

        Assert.Contains(cue.Id, Audio.Invalidated);
        Assert.Contains(MainController.VideoAudioId(cue.Id), Audio.Invalidated);
    }
}
