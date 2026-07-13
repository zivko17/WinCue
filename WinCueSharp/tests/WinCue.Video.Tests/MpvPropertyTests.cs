// =============================================================================
// MpvPropertyTests.cs — Volumen, master, rate, trim y geometría (readback mpv)
// =============================================================================

using WinCue.Video;

namespace WinCue.Video.Tests;

public class MpvPropertyTests : IClassFixture<MediaFixture>
{
    private readonly MediaFixture _media;

    public MpvPropertyTests(MediaFixture media) => _media = media;

    private bool Ready => VideoEngine.MpvAvailable && _media.FFmpegOk;

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

    /// <summary>Engine headless + acceso al player interno vía reflexión
    /// (solo para leer propiedades mpv reales en los tests).</summary>
    private static (VideoEngine engine, Func<int, MpvPlayer?> playerOf) MakeEngine()
    {
        var engine = new VideoEngine(() => 0, forcedVo: "null", nullAudio: true);
        var field = typeof(VideoEngine).GetField("_players",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        MpvPlayer? PlayerOf(int cueId)
        {
            var dict = (Dictionary<int, MpvPlayer>)field.GetValue(engine)!;
            return dict.GetValueOrDefault(cueId);
        }
        return (engine, PlayerOf);
    }

    [Fact]
    public void Volume_And_Master_MapTo_MpvVolumeProperty()
    {
        if (!Ready) return;
        var (engine, playerOf) = MakeEngine();
        using var _ = engine;

        engine.Play(1, _media.Mp4OneSecond, new VideoPlayParams(Volume: 0.8, Loop: true));
        Assert.True(WaitFor(() => engine.IsPlaying(1), 10_000));

        var player = playerOf(1)!;
        Assert.Equal(80.0, player.GetPropertyDouble("volume")!.Value, 1);

        engine.SetVolume(1, 0.5);
        Assert.Equal(50.0, player.GetPropertyDouble("volume")!.Value, 1);

        engine.SetMasterVolume(0.5);   // master multiplica: 0.5 * 0.5 → 25
        Assert.Equal(25.0, player.GetPropertyDouble("volume")!.Value, 1);
        engine.Stop(1);
    }

    [Fact]
    public void FadeVolume_ReachesTarget()
    {
        if (!Ready) return;
        var (engine, playerOf) = MakeEngine();
        using var _ = engine;

        engine.Play(1, _media.Mp4OneSecond, new VideoPlayParams(Volume: 1.0, Loop: true));
        Assert.True(WaitFor(() => engine.IsPlaying(1), 10_000));

        Assert.True(engine.FadeVolume(1, 0.2, 0.3));
        var player = playerOf(1)!;
        Assert.True(WaitFor(
            () => Math.Abs((player.GetPropertyDouble("volume") ?? -1) - 20.0) < 2.0, 3_000),
            $"el fade no llegó al objetivo (volume={player.GetPropertyDouble("volume")})");
        engine.Stop(1);
    }

    [Fact]
    public void FadeVolume_WithStopAfter_StopsCue()
    {
        if (!Ready) return;
        var (engine, _) = MakeEngine();
        using var __ = engine;

        engine.Play(1, _media.Mp4OneSecond, new VideoPlayParams(Loop: true));
        Assert.True(WaitFor(() => engine.IsPlaying(1), 10_000));

        Assert.True(engine.FadeVolume(1, 0.0, 0.2, stopAfter: true));
        Assert.True(WaitFor(() => !engine.IsPlaying(1), 3_000),
            "el cue no se detuvo tras el fade");
        Assert.Equal(VideoCueState.Stopped, engine.GetState(1));
    }

    [Fact]
    public void Rate_SetsSpeedProperty()
    {
        if (!Ready) return;
        var (engine, playerOf) = MakeEngine();
        using var _ = engine;

        engine.Play(1, _media.Mp4OneSecond, new VideoPlayParams(Rate: 2.0, Loop: true));
        Assert.True(WaitFor(() => engine.IsPlaying(1), 10_000));

        Assert.Equal(2.0, playerOf(1)!.GetPropertyDouble("speed")!.Value, 2);
        engine.Stop(1);
    }

    [Fact]
    public void Scale_MapsToLog2VideoZoom()
    {
        if (!Ready) return;
        var (engine, playerOf) = MakeEngine();
        using var _ = engine;

        // 200 % → video-zoom = log2(2) = 1
        engine.Play(1, _media.Mp4OneSecond, new VideoPlayParams(Scale: 200.0, Loop: true));
        Assert.True(WaitFor(() => engine.IsPlaying(1), 10_000));

        Assert.Equal(1.0, playerOf(1)!.GetPropertyDouble("video-zoom")!.Value, 3);
        engine.Stop(1);
    }

    [Fact]
    public void Geometry_Custom_SetsZoomPanRotate()
    {
        if (!Ready) return;
        var (engine, playerOf) = MakeEngine();
        using var _ = engine;

        var geo = new VideoGeometry(
            Mode: "CUSTOM", ScaleX: 2.0,
            TranslateX: 480.0, TranslateY: -270.0,
            RotateZ: 93.0,                    // se cuantiza a 90
            SurfW: 1920.0, SurfH: 1080.0);
        engine.Play(1, _media.Mp4OneSecond, new VideoPlayParams(Loop: true, Geometry: geo));
        Assert.True(WaitFor(() => engine.IsPlaying(1), 10_000));

        var player = playerOf(1)!;
        Assert.Equal(1.0, player.GetPropertyDouble("video-zoom")!.Value, 3);          // log2(2)
        Assert.Equal(0.5, player.GetPropertyDouble("video-pan-x")!.Value, 3);         // 480/960
        Assert.Equal(0.5, player.GetPropertyDouble("video-pan-y")!.Value, 3);         // -(-270)/540
        Assert.Equal(90, player.GetPropertyLong("video-rotate")!.Value);

        // Cambio de geometría EN VIVO → FULL restaura
        engine.ApplyGeometry(1, new VideoGeometry(Mode: "FULL", PreserveAspect: false));
        Assert.Equal(0.0, player.GetPropertyDouble("video-zoom")!.Value, 3);
        Assert.Equal(0, player.GetPropertyLong("video-rotate")!.Value);
        Assert.Equal("no", player.GetPropertyString("keepaspect"));
        engine.Stop(1);
    }

    [Fact]
    public void Trim_SetsStartEndOptions()
    {
        if (!Ready) return;
        var (engine, playerOf) = MakeEngine();
        using var _ = engine;

        engine.Play(1, _media.Mp4OneSecond,
            new VideoPlayParams(TrimStart: 0.3, TrimEnd: 0.8, Loop: true));
        Assert.True(WaitFor(() => engine.IsPlaying(1), 10_000));

        var player = playerOf(1)!;
        // mpv arranca en el punto de trim: time-pos ≥ 0.3 desde el primer frame
        Assert.True(WaitFor(() => (player.GetPropertyDouble("time-pos") ?? 0) >= 0.29, 5_000),
            $"time-pos no respeta trim_start (fue {player.GetPropertyDouble("time-pos")})");
        engine.Stop(1);
    }

    [Fact]
    public void PlayCount_MapsToLoopFile()
    {
        if (!Ready) return;
        var (engine, playerOf) = MakeEngine();
        using var _ = engine;

        engine.Play(1, _media.Mp4Half, new VideoPlayParams(PlayCount: 3));
        Assert.True(WaitFor(() => engine.IsPlaying(1), 10_000));

        // play_count=3 → loop-file=2 (repeticiones adicionales)
        Assert.Equal("2", playerOf(1)!.GetPropertyString("loop-file"));
        engine.Stop(1);
    }
}
