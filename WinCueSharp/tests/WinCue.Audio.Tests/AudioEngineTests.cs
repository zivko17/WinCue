// =============================================================================
// AudioEngineTests.cs — Integración del engine (usa el dispositivo real WASAPI)
// =============================================================================
// Estos tests reproducen audio de verdad (silencioso o casi) en el dispositivo
// por defecto. Si la máquina no tiene salida de audio se saltan sin fallar.
// =============================================================================

using NAudio.Wave;
using WinCue.Core;
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
        var states = new List<MediaCueState>();
        var stateLock = new object();
        double latency = -1;
        engine.StateChanged += (id, s) => { lock (stateLock) states.Add(s); };
        engine.LatencyReport += (id, ms) => latency = ms;

        string path = MakeQuietWav(0.3);
        engine.Play(1, path);

        Assert.True(WaitFor(() => engine.GetState(1) == MediaCueState.Playing, 5_000),
            $"no llegó a PLAYING (estado: {engine.GetState(1)})");
        Assert.True(engine.IsPlaying(1));
        Assert.True(engine.AnyPlaying);
        Assert.Equal(0.3, engine.GetDuration(1), 2);

        Assert.True(WaitFor(() => engine.GetState(1) == MediaCueState.Stopped, 5_000),
            $"no llegó a STOPPED (estado: {engine.GetState(1)})");
        Assert.False(engine.IsPlaying(1));
        Assert.True(latency >= 0.0, "no se reportó latencia");

        lock (stateLock)
        {
            Assert.Contains(MediaCueState.Loading, states);
            Assert.Contains(MediaCueState.Playing, states);
            Assert.Contains(MediaCueState.Stopped, states);
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
        Assert.Equal(MediaCueState.Stopped, engine.GetState(1));
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
    public void LevelReport_EmitsStereoLevels_WhileAudioPlays()
    {
        if (!HasAudioDevice) return;

        using var engine = new AudioEngine();
        var levels = new List<(float L, float R)>();
        var cueLevels = new List<(int Id, float L, float R)>();
        engine.LevelReport += (l, r) => { lock (levels) levels.Add((l, r)); };
        engine.CueLevelReport += (id, l, r) => { lock (cueLevels) cueLevels.Add((id, l, r)); };

        engine.Play(7, MakeQuietWav(1.0));
        Assert.True(WaitFor(() => engine.IsPlaying(7), 5_000));
        Assert.True(WaitFor(() => { lock (levels) return levels.Count >= 3; }, 5_000),
            "el motor no emitió niveles durante la reproducción");

        lock (levels)
            Assert.Contains(levels, v => v.L > 0.001f);   // señal real, no silencio
        lock (cueLevels)
        {
            Assert.NotEmpty(cueLevels);
            Assert.All(cueLevels, v => Assert.Equal(7, v.Id));
        }
        engine.StopAll();
    }

    [Fact]
    public void ProgramMeter_EmitsDeviceLevels_WhileEnginePlays()
    {
        if (!HasAudioDevice) return;

        using var engine = new AudioEngine();
        engine.StartProgramMeter();
        int reports = 0;
        engine.LevelReport += (l, r) => Interlocked.Increment(ref reports);

        engine.Play(1, MakeQuietWav(2.0));
        Assert.True(WaitFor(() => engine.IsPlaying(1), 5_000));
        // Con loopback activo los niveles vienen del DISPOSITIVO: basta con que
        // el flujo esté vivo (la amplitud depende del volumen del sistema)
        Assert.True(WaitFor(() => Volatile.Read(ref reports) >= 3, 5_000),
            "el VU de programa no emitió niveles con audio sonando");
        engine.StopAll();
    }

    // ── FALLO A: el VU de PROGRAMA tiene que funcionar con CUALQUIER driver ──

    [Fact]
    public void ProgramMeter_WithAsioDriver_FallsBackToInternalMix()
    {
        if (!HasAudioDevice) return;
        // Con ASIO no hay loopback (ASIO puentea el mixer de Windows). Si no
        // hay drivers ASIO instalados el motor cae a WASAPI y sí lo hay: en
        // ese caso este test comprueba la otra mitad (loopback) y ya está
        // cubierto por ProgramMeter_EmitsDeviceLevels_WhileEnginePlays.
        using var engine = new AudioEngine(driver: "asio");
        engine.StartProgramMeter();
        if (AudioEngine.ListAsioDrivers().Count == 0)
            return;   // sin ASIO instalado no hay nada que comprobar aquí

        Assert.Equal(ProgramMeterSource.InternalMix, engine.ProgramMeterSource);
    }

    [Fact]
    public void ProgramMeter_WithoutLoopback_LevelReportComesFromMixer()
    {
        if (!HasAudioDevice) return;
        // Sin StartProgramMeter no hay medidor loopback → la ÚNICA vía posible
        // del VU de programa es la mezcla interna. Es la vía que salva el VU
        // con ASIO y con el loopback caído: tiene que emitir de verdad.
        using var engine = new AudioEngine();
        Assert.Equal(ProgramMeterSource.InternalMix, engine.ProgramMeterSource);

        int reports = 0;
        engine.LevelReport += (l, r) => Interlocked.Increment(ref reports);
        engine.Play(3, MakeQuietWav(2.0));
        Assert.True(WaitFor(() => engine.IsPlaying(3), 5_000));
        Assert.True(WaitFor(() => Volatile.Read(ref reports) >= 3, 5_000),
            "el VU de programa no emitió por la vía del mixer interno");
        engine.StopAll();
    }

    [Fact]
    public void ProgramMeterSource_IsLoopback_OnlyWhileCaptureIsAlive()
    {
        if (!HasAudioDevice) return;
        using var engine = new AudioEngine();
        engine.StartProgramMeter();
        // El medidor se abre FUERA del hilo llamante: se espera al cambio
        bool becameLoopback = WaitFor(
            () => engine.ProgramMeterSource == ProgramMeterSource.DeviceLoopback, 5_000);
        if (!becameLoopback)
            return;   // máquina sin loopback utilizable: nada que comprobar

        engine.Cleanup();
        // Cerrado el medidor, el VU vuelve a la mezcla interna en el acto
        Assert.Equal(ProgramMeterSource.InternalMix, engine.ProgramMeterSource);
    }

    // ── FALLO B: cambiar el driver en caliente no puede bloquear la UI ──

    [Fact]
    public void SetDevice_RepeatedHotSwitches_DoNotAccumulateOrThrow()
    {
        if (!HasAudioDevice) return;
        using var engine = new AudioEngine();
        engine.StartProgramMeter();
        engine.Play(1, MakeQuietWav(3.0));
        Assert.True(WaitFor(() => engine.IsPlaying(1), 5_000));

        // 12 cambios de driver seguidos: ni excepciones ni estado inconsistente
        for (int i = 0; i < 12; i++)
            engine.SetDevice(null, i % 2 == 0 ? "asio" : "wasapi");

        engine.SetDevice(null, "wasapi");
        int reports = 0;
        engine.LevelReport += (l, r) => Interlocked.Increment(ref reports);
        engine.Play(1, MakeQuietWav(3.0));
        Assert.True(WaitFor(() => engine.IsPlaying(1), 5_000));
        Assert.True(WaitFor(() => Volatile.Read(ref reports) >= 3, 6_000),
            "el VU de programa dejó de emitir tras varios cambios de driver");
        engine.StopAll();
    }

    [Fact]
    public void SetDevice_DoesNotBlockCallerThread()
    {
        if (!HasAudioDevice) return;
        using var engine = new AudioEngine();
        engine.StartProgramMeter();
        WaitFor(() => engine.ProgramMeterSource == ProgramMeterSource.DeviceLoopback, 5_000);

        // El cierre de la captura (Join del hilo de captura) y la apertura de
        // la nueva se hacen fuera del hilo llamante: en la app ese hilo es el
        // de UI y bloquearlo son los tirones de la barra y los relojes.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 5; i++)
            engine.SetDevice(null, "wasapi");
        sw.Stop();
        Assert.True(sw.Elapsed.TotalMilliseconds < 250,
            $"SetDevice bloqueó el hilo llamante {sw.Elapsed.TotalMilliseconds:0} ms en 5 cambios");
    }

    [Fact]
    public void PlayMissingFile_EmitsError()
    {
        using var engine = new AudioEngine();
        string? error = null;
        engine.ErrorOccurred += (id, msg) => error = msg;

        engine.Play(1, Path.Combine(_dir, "no_existe.wav"));
        Assert.True(WaitFor(() => error is not null, 5_000), "no se emitió error");
        Assert.Equal(MediaCueState.Error, engine.GetState(1));
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
