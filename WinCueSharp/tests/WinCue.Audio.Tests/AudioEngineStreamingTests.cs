// =============================================================================
// AudioEngineStreamingTests.cs — Enrutado de STREAMING del engine (paso 4)
// =============================================================================
// Con la FÁBRICA de fuentes inyectable (sin depender de ffmpeg): el Play con
// Streaming=true usa la fábrica, el Preload aparca un pre-spawn que el GO
// consume, el spawn fallido cae a la ruta RAM clásica, el Stop descarta el
// spawn en vuelo (y lo libera), InvalidateCache mata el aparcado y el
// kill-switch WINCUE_MIRROR_STREAMING=0 fuerza la ruta RAM.
//
// UNA clase = ejecución en serie (el test del kill-switch toca variables de
// entorno del proceso). Los tests que reproducen de verdad se saltan sin
// dispositivo de audio, como los de AudioEngineTests.
// =============================================================================

using NAudio.Wave;
using WinCue.Core;
using WinCue.Audio;

namespace WinCue.Audio.Tests;

public class AudioEngineStreamingTests : IDisposable
{
    private readonly string _dir;

    public AudioEngineStreamingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wincue_engstream_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static bool HasAudioDevice => AudioEngine.ListDevices().Count > 0;

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

    /// <summary>Fuente "streaming" falsa sobre RAM, con flag de liberación
    /// (para verificar que el engine mata las fuentes en cada ruta).</summary>
    private sealed class FakeStreamSource : ICueAudioSource
    {
        private readonly RamAudioSource _inner;
        public volatile bool Disposed;

        public FakeStreamSource(double seconds)
        {
            int frames = (int)(44100 * seconds);
            var samples = new float[frames];
            for (int i = 0; i < frames; i++)
                samples[i] = 0.01f * MathF.Sin(2 * MathF.PI * 440 * i / 44100f);
            _inner = new RamAudioSource(
                new AudioBuffer { Samples = samples, SampleRate = 44100, Channels = 1 });
        }

        public int SampleRate => _inner.SampleRate;
        public int Channels => _inner.Channels;
        public long TotalFrames => _inner.TotalFrames;
        public bool PassEnded => _inner.PassEnded;
        public int ReadFrames(float[] dest, int offset, int frames) =>
            _inner.ReadFrames(dest, offset, frames);
        public void PrepareNextPass() => _inner.PrepareNextPass();
        public bool BeginNextPass() => _inner.BeginNextPass();
        public void Dispose() { Disposed = true; _inner.Dispose(); }
    }

    private static AudioPlayParams Streaming(double trimStart = 0.0, double trimEnd = 0.0) =>
        new(Streaming: true, TrimStart: trimStart, TrimEnd: trimEnd);

    // ------------------------------------------------------------------

    [Fact]
    public void PlayStreaming_UsaLaFabrica_SuenaYLiberaAlTerminar()
    {
        if (!HasAudioDevice) return;

        var created = new List<FakeStreamSource>();
        using var engine = new AudioEngine(streamingFactory: (path, p) =>
        {
            var s = new FakeStreamSource(0.3);
            created.Add(s);
            return s;
        });

        engine.Play(1, MakeQuietWav(0.3), Streaming());
        Assert.True(WaitFor(() => engine.GetState(1) == MediaCueState.Playing, 5_000),
            $"no llegó a PLAYING (estado: {engine.GetState(1)})");
        Assert.Single(created);

        Assert.True(WaitFor(() => engine.GetState(1) == MediaCueState.Stopped, 5_000),
            "no llegó a STOPPED");
        // El fin natural libera la fuente (streaming real: mata el ffmpeg)
        Assert.True(WaitFor(() => created[0].Disposed, 2_000),
            "la fuente no se liberó al terminar");
    }

    [Fact]
    public void SpawnFallido_CaeALaRutaRamClasica()
    {
        if (!HasAudioDevice) return;

        int attempts = 0;
        using var engine = new AudioEngine(streamingFactory: (path, p) =>
        {
            attempts++;
            throw new StreamingSpawnException("sin ffmpeg (simulado)");
        });

        engine.Play(1, MakeQuietWav(0.5), Streaming());
        // FALLBACK EN CASCADA: el decode RAM clásico arranca y suena
        Assert.True(WaitFor(() => engine.GetState(1) == MediaCueState.Playing, 10_000),
            $"el fallback RAM no sonó (estado: {engine.GetState(1)})");
        Assert.Equal(1, attempts);
        Assert.True(engine.GetElapsed(1) >= 0.0);
        engine.StopAll();
    }

    [Fact]
    public void Preload_Aparca_YElGoConsumeElPreSpawn()
    {
        if (!HasAudioDevice) return;

        var created = new List<FakeStreamSource>();
        using var engine = new AudioEngine(streamingFactory: (path, p) =>
        {
            var s = new FakeStreamSource(0.5);
            created.Add(s);
            return s;
        });
        string wav = MakeQuietWav(0.5);

        engine.Preload(-20001, wav, Streaming(trimStart: 0.1));
        Assert.True(WaitFor(() => engine.HasParkedStream(-20001), 5_000),
            "el preload no aparcó la fuente");
        Assert.Single(created);

        // GO con los MISMOS parámetros: consume el aparcado, sin re-spawn
        engine.Play(-20001, wav, Streaming(trimStart: 0.1));
        Assert.True(WaitFor(() => engine.GetState(-20001) == MediaCueState.Playing, 5_000));
        Assert.Single(created);
        Assert.False(engine.HasParkedStream(-20001));
        engine.StopAll();
    }

    [Fact]
    public void Preload_Incompatible_SeMata_YElGoRelanza()
    {
        if (!HasAudioDevice) return;

        var created = new List<FakeStreamSource>();
        using var engine = new AudioEngine(streamingFactory: (path, p) =>
        {
            var s = new FakeStreamSource(0.5);
            created.Add(s);
            return s;
        });
        string wav = MakeQuietWav(0.5);

        engine.Preload(-20001, wav, Streaming(trimStart: 0.1));
        Assert.True(WaitFor(() => engine.HasParkedStream(-20001), 5_000));

        // Trims distintos (GO con offset): el aparcado NO vale → se libera
        // y se lanza uno nuevo con los trims correctos
        engine.Play(-20001, wav, Streaming(trimStart: 0.3));
        Assert.True(WaitFor(() => engine.GetState(-20001) == MediaCueState.Playing, 5_000));
        Assert.Equal(2, created.Count);
        Assert.True(created[0].Disposed, "el pre-spawn incompatible no se mató");
        engine.StopAll();
    }

    [Fact]
    public void InvalidateCache_MataElPreSpawnAparcado()
    {
        var created = new List<FakeStreamSource>();
        using var engine = new AudioEngine(streamingFactory: (path, p) =>
        {
            var s = new FakeStreamSource(0.5);
            created.Add(s);
            return s;
        });

        engine.Preload(-20001, MakeQuietWav(0.5), Streaming());
        Assert.True(WaitFor(() => engine.HasParkedStream(-20001), 5_000));

        engine.InvalidateCache(-20001);
        Assert.False(engine.HasParkedStream(-20001));
        Assert.True(created[0].Disposed, "InvalidateCache no mató el pre-spawn");
    }

    [Fact]
    public void Stop_DuranteElSpawn_DescartaYLibera()
    {
        var created = new List<FakeStreamSource>();
        using var gate = new ManualResetEventSlim(false);
        using var engine = new AudioEngine(streamingFactory: (path, p) =>
        {
            gate.Wait(5_000);   // spawn lento simulado
            var s = new FakeStreamSource(0.5);
            created.Add(s);
            return s;
        });

        engine.Play(1, MakeQuietWav(0.5), Streaming());
        engine.Stop(1);        // el GO se canceló antes de nacer el ffmpeg
        gate.Set();

        Assert.True(WaitFor(() => created.Count == 1 && created[0].Disposed, 5_000),
            "el spawn descartado no se liberó");
        Assert.False(engine.IsPlaying(1));
    }

    [Fact]
    public void KillSwitch_FuerzaLaRutaRam()
    {
        if (!HasAudioDevice) return;

        int factoryCalls = 0;
        using var engine = new AudioEngine(streamingFactory: (path, p) =>
        {
            factoryCalls++;
            return new FakeStreamSource(0.5);
        });

        Environment.SetEnvironmentVariable("WINCUE_MIRROR_STREAMING", "0");
        try
        {
            engine.Play(1, MakeQuietWav(0.5), Streaming());
            Assert.True(WaitFor(() => engine.GetState(1) == MediaCueState.Playing, 10_000),
                $"la ruta RAM no sonó (estado: {engine.GetState(1)})");
        }
        finally
        {
            Environment.SetEnvironmentVariable("WINCUE_MIRROR_STREAMING", null);
        }
        Assert.Equal(0, factoryCalls);   // ni un spawn: RAM de siempre
        engine.StopAll();
    }
}
