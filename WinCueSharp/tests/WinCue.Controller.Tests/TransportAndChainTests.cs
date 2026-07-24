// =============================================================================
// TransportAndChainTests.cs — GO/STOP/PANIC/pausa y cadenas temporizadas
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class TransportAndChainTests : ControllerTestBase
{
    [Fact]
    public void Go_FiresCueAndAdvancesPlayhead()
    {
        var a = AddCue(CueType.Audio, duration: 10.0);
        AddCue(CueType.Audio);

        Controller.OnGoPressed();

        Assert.Equal(1, Controller.Playhead);
        var play = Assert.Single(Audio.Plays);
        Assert.Equal(a.Id, play.CueId);
        Assert.Equal(CueState.Playing, a.State);
    }

    [Fact]
    public void Go_DoesNotCutActiveCues()
    {
        var a = AddCue(CueType.Audio);
        var b = AddCue(CueType.Audio);

        Controller.OnGoPressed();
        Controller.OnGoPressed();

        // Modelo QLab: los dos suenan a la vez
        Assert.True(Audio.IsPlaying(a.Id));
        Assert.True(Audio.IsPlaying(b.Id));
        Assert.Empty(Audio.Stops);
    }

    [Fact]
    public void Go_PassesCueParamsToEngine()
    {
        AddCue(CueType.Audio, setup: c =>
        {
            c.Volume = 0.7; c.FadeIn = 1.5; c.FadeOut = 2.5;
            c.Loop = true; c.TrimStart = 3.0; c.TrimEnd = 8.0; c.PlayCount = 4;
        });

        Controller.OnGoPressed();

        var p = Audio.Plays[0].P;
        Assert.Equal(0.7, p.Volume);
        Assert.Equal(1.5, p.FadeIn);
        Assert.Equal(2.5, p.FadeOut);
        Assert.True(p.Loop);
        Assert.Equal(3.0, p.TrimStart);
        Assert.Equal(8.0, p.TrimEnd);
        Assert.Equal(4, p.PlayCount);
    }

    [Fact]
    public void PreWait_DelaysAction()
    {
        AddCue(CueType.Audio, setup: c => c.PreWait = 2.0);

        Controller.OnGoPressed();
        Assert.Empty(Audio.Plays);           // aún en pre-wait

        Clock.Advance(1_999);
        Assert.Empty(Audio.Plays);
        Clock.Advance(1);
        Assert.Single(Audio.Plays);          // acción disparada a los 2 s exactos
    }

    [Fact]
    public void Wait_AutoFollow_ChainsToNext()
    {
        AddCue(CueType.Wait, duration: 1.0,
            setup: c => c.ContinueMode = ContinueMode.AutoFollow);
        var b = AddCue(CueType.Audio);

        Controller.OnGoPressed();
        Assert.Empty(Audio.Plays);

        Clock.Advance(1_000);                // el WAIT termina → auto-follow
        var play = Assert.Single(Audio.Plays);
        Assert.Equal(b.Id, play.CueId);
        Assert.Equal(2, Controller.Playhead);   // la cadena avanza el playhead
    }

    [Fact]
    public void AutoContinue_FiresAfterPostWait_EvenIfActionEndedEarly()
    {
        // MEMO: su acción termina al instante, pero la cadena espera post_wait
        AddCue(CueType.Memo, setup: c =>
        {
            c.ContinueMode = ContinueMode.AutoContinue;
            c.PostWait = 1.5;
        });
        var b = AddCue(CueType.Audio);

        Controller.OnGoPressed();
        Assert.Empty(Audio.Plays);           // la cadena aún no venció

        Clock.Advance(1_500);
        Assert.Single(Audio.Plays);
        Assert.Equal(b.Id, Audio.Plays[0].CueId);
    }

    [Fact]
    public void NaturalAudioEnd_TriggersAutoFollow()
    {
        var a = AddCue(CueType.Audio,
            setup: c => c.ContinueMode = ContinueMode.AutoFollow);
        var b = AddCue(CueType.Audio);

        Controller.OnGoPressed();
        Assert.Single(Audio.Plays);

        Audio.EndNaturally(a.Id);            // fin natural desde el motor
        Assert.Equal(2, Audio.Plays.Count);
        Assert.Equal(b.Id, Audio.Plays[1].CueId);
    }

    [Fact]
    public void DurationElapsed_StopsMediaWithItsFadeOut()
    {
        var a = AddCue(CueType.Audio, duration: 2.0, setup: c => c.FadeOut = 0.5);

        Controller.OnGoPressed();
        Clock.Advance(2_000);

        var stop = Assert.Single(Audio.Stops);
        Assert.Equal(a.Id, stop.CueId);
        Assert.Equal(0.5, stop.Fade);
    }

    [Fact]
    public void StopAll_StopsCuesAndCancelsChains()
    {
        var a = AddCue(CueType.Audio, setup: c =>
        {
            c.ContinueMode = ContinueMode.AutoContinue;
            c.PostWait = 5.0;
        });
        AddCue(CueType.Audio);

        Controller.OnGoPressed();
        Controller.OnStopPressed();

        Assert.False(Audio.IsPlaying(a.Id));
        Assert.True(Audio.StopAllCalls > 0);
        Assert.Equal(CueState.Stopped, a.State);

        // La cadena AUTO_CONTINUE quedó cancelada: no dispara nada después
        Clock.Advance(10_000);
        Assert.Single(Audio.Plays);
    }

    [Fact]
    public void Panic_FadesEverythingOut()
    {
        var a = AddCue(CueType.Audio);
        Controller.OnGoPressed();

        Controller.OnPanic();

        var stop = Assert.Single(Audio.Stops);
        Assert.Equal(MainController.PanicFade, stop.Fade);
        Assert.Equal(MainController.PanicFade, Audio.LastStopAllFade);
        // Con fundido NO se barren los vídeos (se funden por cue)
        Assert.Equal(0, Video.StopAllCalls);
    }

    [Fact]
    public void PauseToggle_PausesAndResumesEngines()
    {
        AddCue(CueType.Audio, duration: 10.0);
        Controller.OnGoPressed();

        Controller.OnPauseToggle();
        Assert.True(Controller.IsPaused);
        Assert.True(Audio.PausedAll);
        Assert.True(Video.PausedAll);
        Assert.Equal(true, View.LastTransportPaused);

        Controller.OnPauseToggle();
        Assert.False(Controller.IsPaused);
        Assert.True(Audio.ResumedAll);
        Assert.Equal(false, View.LastTransportPaused);
    }

    [Fact]
    public void PauseToggle_WithNothingRunning_IsNoOp()
    {
        AddCue(CueType.Audio);
        Controller.OnPauseToggle();
        Assert.False(Controller.IsPaused);
        Assert.False(Audio.PausedAll);
    }

    // ------------------------------------------------------------------
    // Retrigger (second trigger)
    // ------------------------------------------------------------------

    [Fact]
    public void Retrigger_None_IgnoresSecondTrigger()
    {
        var a = AddCue(CueType.Audio, duration: 10.0);
        Controller.OnGoPressed();
        Controller.TriggerCueId(a.Id);
        Assert.Single(Audio.Plays);
    }

    [Fact]
    public void Retrigger_Stop_StopsTheCue()
    {
        var a = AddCue(CueType.Audio, duration: 10.0,
            setup: c => c.Retrigger = RetriggerMode.Stop);
        Controller.OnGoPressed();
        Controller.TriggerCueId(a.Id);

        Assert.Single(Audio.Plays);
        Assert.False(Audio.IsPlaying(a.Id));
        Assert.Equal(CueState.Stopped, a.State);
    }

    [Fact]
    public void Retrigger_Panic_StopsWithFade()
    {
        var a = AddCue(CueType.Audio, duration: 10.0,
            setup: c => c.Retrigger = RetriggerMode.Panic);
        Controller.OnGoPressed();
        Controller.TriggerCueId(a.Id);

        var stop = Assert.Single(Audio.Stops);
        Assert.Equal(MainController.PanicFade, stop.Fade);
    }

    [Fact]
    public void Retrigger_HardRestart_StopsAndRestarts()
    {
        var a = AddCue(CueType.Audio, duration: 10.0,
            setup: c => c.Retrigger = RetriggerMode.HardRestart);
        Controller.OnGoPressed();
        Controller.TriggerCueId(a.Id);

        Assert.Equal(2, Audio.Plays.Count);
        Assert.True(Audio.IsPlaying(a.Id));
    }

    // ------------------------------------------------------------------
    // Exclusividad de GO / fade others
    // ------------------------------------------------------------------

    [Fact]
    public void GoMode_Sequential_CutsOtherMedia()
    {
        Settings.Set("go_mode", "sequential");
        var a = AddCue(CueType.Audio);
        AddCue(CueType.Audio);

        Controller.OnGoPressed();
        Controller.OnGoPressed();

        Assert.False(Audio.IsPlaying(a.Id));   // el primero fue cortado
        Assert.Contains(Audio.Stops, s => s.CueId == a.Id);
    }

    [Fact]
    public void GoMode_PerCue_CutsOnlyWithStopOthers()
    {
        Settings.Set("go_mode", "per_cue");
        var a = AddCue(CueType.Audio);
        AddCue(CueType.Audio);                              // sin stop_others
        var c = AddCue(CueType.Audio, setup: x => x.StopOthers = true);

        Controller.OnGoPressed();   // a
        Controller.OnGoPressed();   // b: no corta
        Assert.True(Audio.IsPlaying(a.Id));

        Controller.OnGoPressed();   // c: stop_others → corta a y b
        Assert.True(Audio.IsPlaying(c.Id));
        Assert.Equal(2, Audio.Stops.Count);
    }

    [Fact]
    public void Video_IsAlwaysExclusive()
    {
        var v1 = AddCue(CueType.Video);
        var v2 = AddCue(CueType.Video);

        Controller.OnGoPressed();
        Controller.OnGoPressed();

        Assert.True(Video.IsPlaying(v2.Id));
        Assert.Contains(v1.Id, Video.Stops);
    }

    [Fact]
    public void FadeOthers_PeersScope_OnlyAffectsSameParent()
    {
        var peer = AddCue(CueType.Audio);                    // mismo padre (raíz)
        var inGroup = AddCue(CueType.Audio, setup: c => c.ParentId = 99);
        var trigger = AddCue(CueType.Audio, setup: c =>
        {
            c.FadeOthers = true;
            c.FadeOthersTime = 2.0;
            c.FadeOthersScope = FadeScope.Peers;
        });

        Controller.TriggerCueId(peer.Id);
        Controller.TriggerCueId(inGroup.Id);
        Controller.TriggerCueId(trigger.Id);

        // Solo el peer (mismo parent) se funde; el del grupo sigue
        Assert.Contains(Audio.Stops, s => s.CueId == peer.Id && s.Fade == 2.0);
        Assert.True(Audio.IsPlaying(inGroup.Id));
    }

    [Fact]
    public void ArmedFalse_SkipsActionButRespectsChain()
    {
        AddCue(CueType.Audio, setup: c =>
        {
            c.Armed = false;
            c.ContinueMode = ContinueMode.AutoFollow;
        });
        var b = AddCue(CueType.Audio);

        Controller.OnGoPressed();

        // Sin acción del desarmado, pero el siguiente sí se dispara
        var play = Assert.Single(Audio.Plays);
        Assert.Equal(b.Id, play.CueId);
    }

    [Fact]
    public void MasterVolume_ReachesBothEngines()
    {
        Controller.OnMasterVolume(50);
        Assert.Equal(0.5, Audio.Master);
        Assert.Equal(0.5, Video.Master);
    }

    [Fact]
    public void Tick_ReportsActiveRowsAndCountdown()
    {
        var a = AddCue(CueType.Audio, "Mi tema", duration: 10.0);
        Controller.OnGoPressed();
        Audio.Elapsed[a.Id] = 4.0;

        Controller.Tick();

        var row = Assert.Single(View.LastActiveCues);
        Assert.Equal("Mi tema", row.Name);
        Assert.Equal(4.0, row.Elapsed);
        Assert.Equal(6.0, row.Remaining!.Value, 3);
        Assert.Equal(0.4, row.Fraction!.Value, 3);
        Assert.Equal(6.0, View.LastCountdown!.Value, 3);
    }

    [Fact]
    public void Tick_FillsDurationFromEngineWhenUnknown()
    {
        var a = AddCue(CueType.Audio, duration: 0.0);   // duración desconocida
        Controller.OnGoPressed();
        Audio.Durations[a.Id] = 33.0;
        Audio.Elapsed[a.Id] = 3.0;

        Controller.Tick();

        var row = Assert.Single(View.LastActiveCues);
        Assert.Equal(30.0, row.Remaining!.Value, 3);
    }

    [Fact]
    public void Tick_EnPreWait_LaCuentaNoUsaElElapsedDelMotor()
    {
        // Depuración julio 2026: un elapsed > 0 del motor con el mismo cue id
        // (player rezagado de un crossfade / standby) CONGELABA la cuenta
        // visible de la espera ("espera −97,4s" clavada) aunque el timer real
        // siguiera corriendo. En pre_wait manda el reloj de pared del run.
        var a = AddCue(CueType.Video, duration: 10.0, setup: c => c.PreWait = 60.0);
        Controller.OnGoPressed();
        Video.Elapsed[a.Id] = 57.0;          // elapsed fantasma de otro player

        Controller.Tick();

        var row = Assert.Single(View.LastActiveCues);
        Assert.NotNull(row.PreWaitRemaining);
        Assert.True(row.PreWaitRemaining!.Value > 55.0,
            $"la espera visible debe seguir el reloj de pared (quedó {row.PreWaitRemaining})");
    }

    [Fact]
    public void AutoContinue_ConSolape_DisparaAunqueElAnteriorNoTermine()
    {
        // Cadena con crossfade-on-overlap: el disparo del siguiente es un
        // timer INDEPENDIENTE (pre+post wait) — debe llegar puntual aunque el
        // fin natural del vídeo anterior no llegue nunca (bug del reloj HAP).
        var a = AddCue(CueType.Video, duration: 10.0, setup: c =>
        {
            c.ContinueMode = ContinueMode.AutoContinue;
            c.PostWait = 5.0;                 // B entra 5 s antes del fin de A
        });
        var b = AddCue(CueType.Video, duration: 10.0, setup: c => c.FadeIn = 2.0);

        Controller.OnGoPressed();
        Clock.Advance(4_999);
        Assert.DoesNotContain(Video.Plays, p => p.CueId == b.Id);

        Clock.Advance(1);                     // post_wait cumplido; A sigue vivo
        Assert.Contains(Video.Plays, p => p.CueId == b.Id);
        Assert.True(Video.IsPlaying(a.Id));   // el solape existe (crossfade)
    }

    [Fact]
    public void PlayheadNavigation_PrevNextJumpClick()
    {
        AddCue(CueType.Memo); AddCue(CueType.Memo); AddCue(CueType.Memo);

        Controller.OnNextPressed();
        Assert.Equal(1, Controller.Playhead);
        Controller.OnPrevPressed();
        Assert.Equal(0, Controller.Playhead);
        Controller.OnJumpToCue(3);
        Assert.Equal(2, Controller.Playhead);
        Controller.OnSetPlayhead(1);
        Assert.Equal(1, Controller.Playhead);
        Controller.OnSetPlayhead(99);   // fuera de rango → no-op
        Assert.Equal(1, Controller.Playhead);
    }

    [Fact]
    public void AudioPreload_LoadsUpcomingCue()
    {
        AddCue(CueType.Audio);
        var next = AddCue(CueType.Audio);

        Controller.OnGoPressed();   // al sonar el 1º, se pre-carga el del playhead

        Assert.Contains(next.Id, Audio.Preloads);
    }

    [Fact]
    public void EngineError_MarksModelErrorAndFlashes()
    {
        var a = AddCue(CueType.Audio, duration: 10.0);
        Controller.OnGoPressed();

        Audio.RaiseError(a.Id, "dispositivo desconectado");

        Assert.Equal(CueState.Error, a.State);
        Assert.Contains(View.Statuses, s => s.Contains("dispositivo desconectado"));
        Assert.DoesNotContain(a.Id, Controller.RunningCueIds);
    }

    [Fact]
    public void GetProgramVideo_ReturnsPlayingVideoOrNull()
    {
        Assert.Null(Controller.GetProgramVideo());

        var v = AddCue(CueType.Video, "clip");
        Controller.OnGoPressed();
        Video.Elapsed[v.Id] = 4.2;

        var info = Controller.GetProgramVideo();
        Assert.NotNull(info);
        Assert.Equal(@"C:\fake\media.wav", info!.Value.FilePath);
        Assert.Equal(4.2, info.Value.Elapsed);
        // El monitor PGM necesita el CUE y la SALIDA para poder ESPEJAR su
        // superficie del compositor en vez de re-decodificar el archivo
        Assert.Equal(v.Id, info.Value.CueId);
        Assert.Equal(0, info.Value.Screen);

        Controller.OnStopPressed();
        Assert.Null(Controller.GetProgramVideo());
    }

    [Fact]
    public void GetProgramVideo_ReportaLaSalidaDelCue()
    {
        // Un cue en la Salida 3 (video_output = 2): el monitor debe espejar
        // ESA ventana, no la principal
        var v = AddCue(CueType.Video, "clip", setup: c => c.VideoOutput = 2);
        Controller.OnGoPressed();

        var info = Controller.GetProgramVideo();
        Assert.NotNull(info);
        Assert.Equal(v.Id, info!.Value.CueId);
        Assert.Equal(2, info.Value.Screen);
    }

    [Fact]
    public void LevelReports_ReachTheView()
    {
        // Cableado motor → controlador → vista (VU de PGM y VU por cue)
        Audio.RaiseLevel(0.8f, 0.3f);
        Assert.Equal(0.8f, View.LastVu);          // máx(L,R)

        Audio.RaiseCueLevel(42, 0.5f, 0.7f);
        Assert.Equal(0.7f, View.CueVuLevels[42]);
    }

    [Fact]
    public void LatencyReport_StoredInModel()
    {
        var a = AddCue(CueType.Audio);
        Controller.OnGoPressed();
        Audio.RaiseLatency(a.Id, 12.5);
        Assert.Equal(12.5, a.LatencyMs);
    }
}
