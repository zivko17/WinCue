// =============================================================================
// ShowPruebaE2ETests.cs — E2E: el show sintético REAL contra el controlador
// =============================================================================
// Carga tests\show-prueba\show-prueba.json (el mismo archivo que Andrés abre
// en la app) y reproduce sus secciones con el reloj virtual, verificando la
// secuenciación de verdad: cadenas AUTO_CONTINUE, crossfades en la misma
// pista (handover), y los cues de control FADE/STOP/START. Si alguien cambia
// el show o rompe la semántica del motor, esto lo caza sin ojos humanos.
// Los medias son dummies (los motores falsos no decodifican).
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class ShowPruebaE2ETests : ControllerTestBase
{
    public ShowPruebaE2ETests()
    {
        string src = FindShowJson();
        string dst = Path.Combine(TempDir, "show-prueba.json");
        File.Copy(src, dst);
        foreach (var cue in CueModel.FromJson(File.ReadAllText(src)))
            if (cue.FilePath.Length > 0)
            {
                string p = Path.Combine(TempDir, cue.FilePath);
                if (!File.Exists(p))
                    File.WriteAllBytes(p, new byte[] { 0 });
            }
        Controller.LoadShow(dst);
    }

    /// <summary>Sube desde bin\ hasta encontrar tests\show-prueba.</summary>
    internal static string FindShowJson()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent!)
        {
            string candidate = Path.Combine(dir.FullName, "show-prueba", "show-prueba.json");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException("tests\\show-prueba\\show-prueba.json no encontrado");
    }

    [Fact]
    public void Carga_SinMediasPerdidasNiErrores()
    {
        Assert.True(Model.Count >= 20, $"cues cargados: {Model.Count}");
        Assert.Equal(0, Controller.MissingMediaCount());
        Assert.Empty(View.Errors);
    }

    [Fact]
    public void Seccion1_CrossfadeCorto_ConvivenYSustituyen()
    {
        Controller.TriggerCueId(10);   // memo ① → AUTO_CONTINUE

        Clock.Advance(100);            // t = 0,1
        Assert.True(Video.IsPlaying(11), "1.1 debe arrancar con la sección");

        Clock.Advance(9_300);          // t = 9,4 — aún no toca el 1.2
        Assert.DoesNotContain(Video.Plays, p => p.CueId == 12);

        Clock.Advance(300);            // t = 9,7 — dentro del solape de 0,5 s
        Assert.True(Video.IsPlaying(12), "1.2 entra a los 9,5 s");
        Assert.True(Video.IsPlaying(11), "crossfade: ambos vivos en el solape");
        Assert.Equal(0.5, Video.Plays.Single(p => p.CueId == 12).P.HandoverSeconds, 2);

        Clock.Advance(500);            // t = 10,2 — fin del 1.1
        Assert.False(Video.IsPlaying(11), "1.1 termina en su fin, no antes");
        Assert.True(Video.IsPlaying(12));
    }

    [Fact]
    public void Seccion2_CrossfadeLargo_TiemposExactos()
    {
        Controller.TriggerCueId(20);

        Clock.Advance(5_900);          // t = 5,9 — el 2.2 (pre_wait 6) aún no
        Assert.DoesNotContain(Video.Plays, p => p.CueId == 22);
        Clock.Advance(200);            // t = 6,1 — solape de 4 s en marcha
        Assert.True(Video.IsPlaying(22));
        Assert.True(Video.IsPlaying(21));
        Assert.Equal(4.0, Video.Plays.Single(p => p.CueId == 22).P.HandoverSeconds, 2);
        Clock.Advance(4_100);          // t = 10,2 — fin del 2.1
        Assert.False(Video.IsPlaying(21));
        Assert.True(Video.IsPlaying(22));
    }

    [Fact]
    public void Seccion4_CrossfadeDeAudio_SinHueco()
    {
        Controller.TriggerCueId(40);

        Clock.Advance(100);
        Assert.True(Audio.IsPlaying(41));
        Clock.Advance(6_100);          // t = 6,2 — solape de 2 s
        Assert.True(Audio.IsPlaying(42));
        Assert.True(Audio.IsPlaying(41), "los dos tonos se mezclan en el solape");
        var p42 = Audio.Plays.Single(p => p.CueId == 42).P;
        Assert.Equal(2.0, p42.FadeIn, 2);
        Clock.Advance(2_100);          // t = 8,3 — fin del 4.1
        Assert.False(Audio.IsPlaying(41));
        Assert.True(Audio.IsPlaying(42));
    }

    [Fact]
    public void Seccion7_FadeStopStart_ActuanSobreSusObjetivos()
    {
        int playheadBefore = Controller.Playhead;
        Controller.TriggerCueId(70);

        Clock.Advance(100);            // t = 0,1
        Assert.True(Video.IsPlaying(71));

        Clock.Advance(4_100);          // t = 4,2 — FADE 7.2 ejecutado; audio 7.3 arranca
        var fade = Assert.Single(Video.Fades.Where(f => f.CueId == 71));
        Assert.Equal(0.0, fade.Target);
        Assert.Equal(3.0, fade.Duration, 2);
        Assert.True(fade.StopAfter, "fade a 0 con 'parar al terminar'");
        Assert.True(Audio.IsPlaying(73));

        Clock.Advance(4_900);          // t = 9,1 — STOP 7.4 (5 s tras arrancar 7.3)
        Assert.False(Audio.IsPlaying(73));
        Assert.Contains(Audio.Stops, s => s.CueId == 73);

        // START 7.5 ejecuta en t=15, pero su objetivo (42) tiene pre_wait 6:
        // el disparo lo RESPETA (semántica QLab) → suena en t=21
        Clock.Advance(6_100);          // t = 15,2 — objetivo en pre-wait
        Assert.False(Audio.IsPlaying(42), "el pre_wait del objetivo se respeta");
        Clock.Advance(6_000);          // t = 21,2 — ahora sí
        Assert.True(Audio.IsPlaying(42), "el START lanza su objetivo");
        Assert.Equal(playheadBefore, Controller.Playhead);   // sin mover playhead
    }

    [Fact]
    public void Seccion5_ImagenSobreVideo_SinFades()
    {
        Controller.TriggerCueId(50);

        Clock.Advance(3_100);          // t = 3,1 — la imagen 5.2 entra a corte
        Assert.True(Video.IsPlaying(51));
        Assert.True(Video.IsPlaying(52), "capas distintas conviven");
        var p52 = Video.Plays.Single(p => p.CueId == 52).P;
        Assert.Equal(0.0, p52.HandoverSeconds, 3);   // sin fade = sin handover
        Assert.NotEqual(p52.Layer, Video.Plays.Single(p => p.CueId == 51).P.Layer);

        Clock.Advance(4_100);          // t = 7,2 — la imagen (4 s) ya salió
        Assert.False(Video.IsPlaying(52));
        Assert.True(Video.IsPlaying(51), "el fondo sigue tras salir la imagen");
    }

    [Fact]
    public void RoundTrip_GuardarYRecargarConservaElShow()
    {
        string outPath = Path.Combine(TempDir, "roundtrip.json");
        Controller.SaveShow(outPath);
        var reloaded = CueModel.FromJson(File.ReadAllText(outPath));

        Assert.Equal(Model.Count, reloaded.Count);
        Assert.Equal(Model.Cues.Select(c => c.Id), reloaded.Select(c => c.Id));
        Assert.Equal(Model.Cues.Select(c => c.TimelineLayer),
            reloaded.Select(c => c.TimelineLayer));
        Assert.Equal(Model.Cues.Select(c => c.FadeIn), reloaded.Select(c => c.FadeIn));
        Assert.Equal(Model.Cues.Select(c => c.PreWait), reloaded.Select(c => c.PreWait));
    }
}
