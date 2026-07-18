// =============================================================================
// EngineSignalTests.cs — Señales del motor de VÍDEO hacia el controlador
// =============================================================================
// Huecos de la ruta crítica detectados en la auditoría 03: el fin NATURAL de
// un vídeo (OnVideoStateChanged) y los errores de vídeo (OnVideoError) nunca
// se ejercitaban — y por ahí pasa el encadenado auto-follow tras cada vídeo
// que termina solo, además de la recuperación de errores en directo.
// También: OnStopSelected (parar la fila seleccionada) y Shutdown.
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class EngineSignalTests : ControllerTestBase
{
    // ------------------------------------------------------------------
    // Fin natural del vídeo (OnVideoStateChanged)
    // ------------------------------------------------------------------

    [Fact]
    public void VideoTerminaSolo_RetiraElCueYMarcaStopped()
    {
        var v = AddCue(CueType.Video, "clip");   // sin duration: fin natural

        Controller.OnGoPressed();
        Assert.Contains(v.Id, Controller.RunningCueIds);

        Video.EndNaturally(v.Id);

        Assert.Empty(Controller.RunningCueIds);
        Assert.Equal(CueState.Stopped, v.State);
    }

    [Fact]
    public void VideoTerminaSolo_EncadenaAutoFollow()
    {
        // El caso de directo por excelencia: vídeo con auto-follow → al
        // acabarse solo, dispara el siguiente cue de la lista.
        AddCue(CueType.Video, "vt", setup: c => c.ContinueMode = ContinueMode.AutoFollow);
        var siguiente = AddCue(CueType.Audio, "cola");

        Controller.OnGoPressed();
        Assert.Empty(Audio.Plays);

        Video.EndNaturally(Model.Cues[0].Id);

        var play = Assert.Single(Audio.Plays);
        Assert.Equal(siguiente.Id, play.CueId);
    }

    [Fact]
    public void VideoTerminaSolo_ActualizaElTally()
    {
        var v = AddCue(CueType.Video, "unico");
        Controller.OnGoPressed();
        Assert.Equal("unico", View.LastOutputContent);

        Video.EndNaturally(v.Id);

        Assert.Null(View.LastOutputContent);   // la salida queda en negro
    }

    [Fact]
    public void VideoTerminaSolo_TrasParadaManual_NoEncadenaDosVeces()
    {
        // DurationElapsed marca ManualStop y para el cue; si el motor emite
        // después su Stopped (carrera real), NO debe encadenar otra vez.
        AddCue(CueType.Video, "corto", duration: 2.0,
            setup: c => c.ContinueMode = ContinueMode.AutoFollow);
        AddCue(CueType.Audio, "cola");

        Controller.OnGoPressed();
        Clock.Advance(2_000);                    // fin por duración → encadena
        Assert.Single(Audio.Plays);

        Video.EndNaturally(Model.Cues[0].Id);    // eco tardío del motor

        Assert.Single(Audio.Plays);              // sin doble disparo
    }

    // ------------------------------------------------------------------
    // Errores de vídeo (OnVideoError)
    // ------------------------------------------------------------------

    [Fact]
    public void ErrorDeVideo_MarcaErrorRetiraYAvisa()
    {
        var v = AddCue(CueType.Video, "roto");
        Controller.OnGoPressed();

        Video.RaiseError(v.Id, "no se pudo decodificar");

        Assert.Equal(CueState.Error, v.State);
        Assert.Empty(Controller.RunningCueIds);
        Assert.Contains(View.Statuses, s => s.Contains("Vídeo: no se pudo decodificar"));
    }

    [Fact]
    public void AvisoDeVideo_SoloInforma_NoTocaElCue()
    {
        // Los "[AVISO]" (p.ej. fallback del compositor) van a la barra de
        // estado pero NO son errores: el cue sigue sonando tal cual.
        var v = AddCue(CueType.Video, "clip");
        Controller.OnGoPressed();

        Video.RaiseError(v.Id, "[AVISO] Compositor GPU activo — salida 1.");

        Assert.Equal(CueState.Playing, v.State);
        Assert.Contains(v.Id, Controller.RunningCueIds);
        Assert.Contains(View.Statuses, s => s.Contains("[AVISO] Compositor GPU activo"));
    }

    // ------------------------------------------------------------------
    // Parar la fila seleccionada / cierre limpio
    // ------------------------------------------------------------------

    [Fact]
    public void OnStopSelected_ParaSoloElCueDeEsaFila()
    {
        var a = AddCue(CueType.Audio, "a", duration: 60.0);
        var b = AddCue(CueType.Audio, "b", duration: 60.0);
        Controller.OnGoPressed();
        Controller.OnGoPressed();
        Assert.Equal(2, Controller.RunningCueIds.Count);

        Controller.OnStopSelected(0);

        Assert.DoesNotContain(a.Id, Controller.RunningCueIds);
        Assert.Contains(b.Id, Controller.RunningCueIds);
        Assert.Equal(CueState.Stopped, a.State);
    }

    [Fact]
    public void OnStopSelected_FilaSinCueSonando_NoHaceNada()
    {
        AddCue(CueType.Audio, "quieto", duration: 60.0);

        Controller.OnStopSelected(0);

        Assert.Empty(Audio.Stops);
    }

    [Fact]
    public void Shutdown_ParaTodoYLiberaLosMotores()
    {
        AddCue(CueType.Audio, "a", duration: 60.0);
        AddCue(CueType.Video, "v", duration: 60.0,
            setup: c => c.ContinueMode = ContinueMode.AutoContinue);
        Controller.OnGoPressed();
        Controller.OnGoPressed();
        Assert.Equal(2, Controller.RunningCueIds.Count);

        Controller.Shutdown();

        Assert.Empty(Controller.RunningCueIds);
        Assert.Empty(Audio.Playing);
        Assert.Empty(Video.Playing);
        Assert.Equal(1, Audio.CleanupCalls);
        Assert.Equal(1, Video.CleanupCalls);
        // Las continuaciones pendientes (AUTO_CONTINUE) quedan canceladas
        Assert.Equal(0, Clock.PendingCount);
    }
}
