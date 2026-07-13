// =============================================================================
// VideoEngineTests.cs — Engine de vídeo con libmpv REAL en modo headless
// =============================================================================
// vo=null + ao=null: mpv decodifica y avanza el reloj de reproducción sin
// ventana, GPU ni dispositivo de audio. Se testea la máquina de estados, las
// propiedades (volumen/rate/geometría) y el ciclo de vida de los players.
//
// Los archivos de prueba se generan una vez por clase con FFmpeg (fixture).
// Si libmpv-2.dll o FFmpeg no están disponibles, los tests se saltan.
// =============================================================================

using System.Diagnostics;
using WinCue.Core;
using WinCue.Video;

namespace WinCue.Video.Tests;

public sealed class MediaFixture : IDisposable
{
    public string Dir { get; }
    public string Mp4OneSecond { get; }      // 1 s vídeo+audio
    public string Mp4Half { get; }           // 0.5 s vídeo
    public string Png { get; }
    public bool FFmpegOk { get; }

    public MediaFixture()
    {
        Dir = Path.Combine(Path.GetTempPath(), "wincue_video_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Mp4OneSecond = Path.Combine(Dir, "one.mp4");
        Mp4Half = Path.Combine(Dir, "half.mp4");
        Png = Path.Combine(Dir, "img.png");

        FFmpegOk =
            Run("-f", "lavfi", "-i", "testsrc=duration=1:size=320x240:rate=30",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
                "-shortest", "-pix_fmt", "yuv420p", Mp4OneSecond)
            && Run("-f", "lavfi", "-i", "testsrc=duration=0.5:size=160x120:rate=30",
                "-pix_fmt", "yuv420p", Mp4Half)
            && Run("-f", "lavfi", "-i", "color=c=red:size=64x64", "-frames:v", "1", Png);
    }

    private static bool Run(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-y");
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(30_000);
            return p.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

public class VideoEngineTests : IClassFixture<MediaFixture>
{
    private readonly MediaFixture _media;

    public VideoEngineTests(MediaFixture media) => _media = media;

    private bool Ready => VideoEngine.MpvAvailable && _media.FFmpegOk;

    /// <summary>Engine headless: vo=null, ao=null, sin HWND.</summary>
    private static VideoEngine MakeEngine() =>
        new(getWindowHandle: () => 0, forcedVo: "null", nullAudio: true);

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(15);
        }
        return condition();
    }

    // ------------------------------------------------------------------
    // Disponibilidad
    // ------------------------------------------------------------------

    [Fact]
    public void Mpv_IsAvailable_OnThisMachine()
    {
        // Aviso temprano si libmpv-2.dll dejó de encontrarse
        Assert.True(VideoEngine.MpvAvailable,
            "libmpv-2.dll no se pudo cargar (¿se movió la carpeta WinCue?)");
    }

    [Fact]
    public void SupportedFormats_IncludeVideoAndImages()
    {
        var formats = VideoEngine.ListSupportedFormats();
        Assert.Contains(".mp4", formats);
        Assert.Contains(".mov", formats);
        Assert.Contains(".png", formats);
        Assert.Contains(".webm", formats);
    }

    // ------------------------------------------------------------------
    // Máquina de estados
    // ------------------------------------------------------------------

    [Fact]
    public void PlayVideo_TransitionsToPlaying_AndStopsAtEnd()
    {
        if (!Ready) return;
        using var engine = MakeEngine();
        var states = new List<MediaCueState>();
        var stateLock = new object();
        double latency = -1;
        bool frameReady = false;
        engine.StateChanged += (id, s) => { lock (stateLock) states.Add(s); };
        engine.LatencyReport += (id, ms) => latency = ms;
        engine.FrameReady += id => frameReady = true;

        engine.Play(1, _media.Mp4OneSecond);

        Assert.True(WaitFor(() => engine.GetState(1) == MediaCueState.Playing, 10_000),
            $"no llegó a PLAYING (estado: {engine.GetState(1)})");
        Assert.True(engine.IsPlaying(1));
        Assert.True(engine.AnyPlaying);

        // mpv conoce la duración y el reloj avanza
        Assert.True(WaitFor(() => engine.GetDuration(1) > 0.9, 5_000),
            $"duración no reportada (fue {engine.GetDuration(1)})");
        Assert.True(WaitFor(() => engine.GetElapsed(1) > 0.1, 5_000),
            "time-pos no avanza");

        // Fin natural → STOPPED
        Assert.True(WaitFor(() => engine.GetState(1) == MediaCueState.Stopped, 15_000),
            $"no llegó a STOPPED (estado: {engine.GetState(1)})");
        Assert.False(engine.IsPlaying(1));
        Assert.True(latency >= 0.0, "no se reportó latencia");
        Assert.True(frameReady, "no se emitió FrameReady");
        lock (stateLock)
        {
            Assert.Contains(MediaCueState.Loading, states);
            Assert.Contains(MediaCueState.Loaded, states);
            Assert.Contains(MediaCueState.Playing, states);
        }
    }

    [Fact]
    public void Stop_TerminatesPlayerImmediately()
    {
        if (!Ready) return;
        using var engine = MakeEngine();
        engine.Play(1, _media.Mp4OneSecond, new VideoPlayParams(Loop: true));
        Assert.True(WaitFor(() => engine.IsPlaying(1), 10_000));

        engine.Stop(1);
        Assert.Equal(MediaCueState.Stopped, engine.GetState(1));
        Assert.False(engine.IsPlaying(1));
        Assert.Equal(0.0, engine.GetDuration(1));   // player terminado
    }

    [Fact]
    public void Image_StaysPlaying_UntilStopped()
    {
        if (!Ready) return;
        using var engine = MakeEngine();
        engine.Play(1, _media.Png);
        Assert.True(WaitFor(() => engine.IsPlaying(1), 10_000),
            $"la imagen no llegó a PLAYING (estado: {engine.GetState(1)})");

        // image-display-duration=inf → sigue en PLAYING pasado un tiempo
        Thread.Sleep(600);
        Assert.True(engine.IsPlaying(1), "la imagen no se mantuvo en pantalla");
        engine.Stop(1);
        Assert.Equal(MediaCueState.Stopped, engine.GetState(1));
    }

    [Fact]
    public void Loop_KeepsPlayingPastFileEnd()
    {
        if (!Ready) return;
        using var engine = MakeEngine();
        engine.Play(1, _media.Mp4Half, new VideoPlayParams(Loop: true));
        Assert.True(WaitFor(() => engine.IsPlaying(1), 10_000));

        // El archivo dura 0.5 s; con loop sigue reproduciendo a los 1.2 s
        Thread.Sleep(1_200);
        Assert.True(engine.IsPlaying(1), "el loop no mantuvo la reproducción");
        engine.Stop(1);
    }

    [Fact]
    public void SingleOutput_SecondVideoTerminatesFirst()
    {
        if (!Ready) return;
        using var engine = MakeEngine();
        engine.Play(1, _media.Mp4OneSecond, new VideoPlayParams(Loop: true));
        Assert.True(WaitFor(() => engine.IsPlaying(1), 10_000));

        engine.Play(2, _media.Mp4Half, new VideoPlayParams(Loop: true));
        Assert.True(WaitFor(() => engine.IsPlaying(2), 10_000));

        // Salida única: el player del cue 1 fue TERMINADO al arrancar el 2
        Assert.False(engine.IsPlaying(1), "el primer vídeo no fue terminado");
        engine.StopAll();
        Assert.False(engine.AnyPlaying);
    }

    [Fact]
    public void PauseResume_FreezesClock()
    {
        if (!Ready) return;
        using var engine = MakeEngine();
        engine.Play(1, _media.Mp4OneSecond, new VideoPlayParams(Loop: true));
        Assert.True(WaitFor(() => engine.GetElapsed(1) > 0.1, 10_000));

        engine.PauseAll();
        Thread.Sleep(100);   // dejar drenar el pipeline
        double frozen = engine.GetElapsed(1);
        Thread.Sleep(300);
        Assert.Equal(frozen, engine.GetElapsed(1), 1);

        engine.ResumeAll();
        Assert.True(WaitFor(() => engine.GetElapsed(1) > frozen + 0.05, 5_000),
            "el reloj no avanzó tras reanudar");
        engine.Stop(1);
    }

    // ------------------------------------------------------------------
    // Errores
    // ------------------------------------------------------------------

    [Fact]
    public void MissingFile_EmitsError()
    {
        using var engine = MakeEngine();
        string? error = null;
        engine.ErrorOccurred += (id, msg) => error = msg;
        engine.Play(1, Path.Combine(_media.Dir, "no_existe.mp4"));
        Assert.True(WaitFor(() => error is not null, 5_000));
        Assert.Equal(MediaCueState.Error, engine.GetState(1));
    }

    [Fact]
    public void UnsupportedFormat_EmitsError()
    {
        using var engine = MakeEngine();
        string bad = Path.Combine(_media.Dir, "raro.xyz");
        File.WriteAllText(bad, "x");
        string? error = null;
        engine.ErrorOccurred += (id, msg) => error = msg;
        engine.Play(1, bad);
        Assert.True(WaitFor(() => error is not null, 5_000));
        Assert.Contains("no soportado", error);
    }

    [Fact]
    public void EmptyPath_EmitsErrorImmediately()
    {
        using var engine = MakeEngine();
        string? error = null;
        engine.ErrorOccurred += (id, msg) => error = msg;
        engine.Play(1, "");
        Assert.NotNull(error);
    }
}
