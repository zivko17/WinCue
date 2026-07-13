// =============================================================================
// AudioEngineTests.cs — Integración del engine (usa el dispositivo real WASAPI)
// =============================================================================
// Estos tests reproducen audio de verdad (silencioso o casi) en el dispositivo
// por defecto. Si la máquina no tiene salida de audio se saltan sin fallar.
// =============================================================================

using NAudio.Wave;
using WinCue.Audio;

namespace WinCue.Audio.Tests;

public class AudioEngineTests : IDisposable
{
    private readonly string _dir;

    public AudioEngineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wincue_engine_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static bool HasAudioDevice => AudioEngine.ListDevices().Count > 0;

    /// <summary>WAV corto casi inaudible (amplitud 0.01) para tests reales.</summary>
    private string MakeQuietWav(double seconds)
    {
        string path = Path.Combine(_dir, $"quiet_{seconds}.wav");
        var format = new WaveFormat(44100, 16, 1);
        using var writer = new WaveFileWriter(path, format);
        int frames = (int)(44100 * seconds);
        for (int i = 0; i < frames; i++)
            writer.WriteSamples(new[] { 0.01f * MathF.Sin(2 * MathF.PI * 440 * i / 44100f) }, 0, 1);
        return path;
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    [Fact]
    public void ListDevices_DoesNotThrow()
    {
        var devices = AudioEngine.ListDevices();
        Assert.NotNull(devices);
    }

    [Fact]
    public void PlayShortFile_TransitionsThroughStates_AndReportsLatency()
    {
        if (!HasAudioDevice) return;

        using var engine = new AudioEngine();
        var states = new List<EngineCueState>();
        var stateLock = new object();
        double latency = -1;
        engine.StateChanged += (id, s) => { lock (stateLock) states.Add(s); };
        engine.LatencyReport += (id, ms) => latency = ms;

        string path = MakeQuietWav(0.3);
        engine.Play(1, path);

        Assert.True(WaitFor(() => engine.GetState(1) == EngineCueState.Playing, 5_000),
            $"no llegó a PLAYING (estado: {engine.GetState(1)})");
        Assert.True(engine.IsPlaying(1));
        Assert.True(engine.AnyPlaying);
        Assert.Equal(0.3, engine.GetDuration(1), 2);

        Assert.True(WaitFor(() => engine.GetState(1) == EngineCueState.Stopped, 5_000),
            $"no llegó a STOPPED (estado: {engine.GetState(1)})");
        Assert.False(engine.IsPlaying(1));
        Assert.True(latency >= 0.0, "no se reportó latencia");

        lock (stateLock)
        {
            Assert.Contains(EngineCueState.Loading, states);
            Assert.Contains(EngineCueState.Playing, states);
            Assert.Contains(EngineCueState.Stopped, states);
        }
    }

    [Fact]
    public void TwoCues_PlayConcurrently()
    {
        if (!HasAudioDevice) return;

        using var engine = new AudioEngine();
        string a = MakeQuietWav(1.0);
        string b = MakeQuietWav(1.5);

        engine.Play(1, a);
        engine.Play(2, b);

        Assert.True(WaitFor(() => engine.IsPlaying(1) && engine.IsPlaying(2), 5_000),
            "los dos cues no llegaron a sonar a la vez");
        // GO no corta: ambos siguen activos
        Assert.True(engine.AnyPlaying);
        engine.StopAll();
        Assert.True(WaitFor(() => !engine.AnyPlaying, 2_000));
    }

    [Fact]
    public void Stop_RemovesActiveCue()
    {
        if (!HasAudioDevice) return;

        using var engine = new AudioEngine();
        engine.Play(1, MakeQuietWav(5.0));
        Assert.True(WaitFor(() => engine.IsPlaying(1), 5_000));

        engine.Stop(1);
        Assert.False(engine.IsPlaying(1));
        Assert.Equal(EngineCueState.Stopped, engine.GetState(1));
    }

    [Fact]
    public void FadeStop_EndsWithinExpectedTime()
    {
        if (!HasAudioDevice) return;

        using var engine = new AudioEngine();
        engine.Play(1, MakeQuietWav(10.0));
        Assert.True(WaitFor(() => engine.IsPlaying(1), 5_000));

        engine.Stop(1, fade: 0.2);
        Assert.True(engine.IsPlaying(1), "el fade no debe parar de inmediato");
        Assert.True(WaitFor(() => !engine.IsPlaying(1), 3_000),
            "el cue no terminó tras el fade de salida");
    }

    [Fact]
    public void PauseResume_FreezesPosition()
    {
        if (!HasAudioDevice) return;

        using var engine = new AudioEngine();
        engine.Play(1, MakeQuietWav(10.0));
        Assert.True(WaitFor(() => engine.IsPlaying(1), 5_000));
        Thread.Sleep(150);

        engine.PauseAll();
        double frozen = engine.GetElapsed(1);
        Thread.Sleep(200);
        Assert.Equal(frozen, engine.GetElapsed(1), 2);   // no avanza en pausa

        engine.ResumeAll();
        Assert.True(WaitFor(() => engine.GetElapsed(1) > frozen + 0.05, 2_000),
            "la posición no avanzó tras reanudar");
        engine.StopAll();
    }

    [Fact]
    public void PlayMissingFile_EmitsError()
    {
        using var engine = new AudioEngine();
        string? error = null;
        engine.ErrorOccurred += (id, msg) => error = msg;

        engine.Play(1, Path.Combine(_dir, "no_existe.wav"));
        Assert.True(WaitFor(() => error is not null, 5_000), "no se emitió error");
        Assert.Equal(EngineCueState.Error, engine.GetState(1));
    }

    [Fact]
    public void EmptyPath_EmitsErrorImmediately()
    {
        using var engine = new AudioEngine();
        string? error = null;
        engine.ErrorOccurred += (id, msg) => error = msg;
        engine.Play(1, "");
        Assert.NotNull(error);
    }
}
