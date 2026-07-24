// =============================================================================
// StreamingAudioSourceTests.cs — Fuente de audio en STREAMING (paso 4)
// =============================================================================
// Tests con ffmpeg REAL y clips generados (se saltan sin ffmpeg en PATH):
//   · paridad numérica con la ruta RAM (mismos samples, wav float conocido);
//   · trim_start honrado (-ss de ffmpeg = índices de la ruta RAM);
//   · pasadas (loop/play_count): empalme limpio y elapsed que resetea;
//   · matar el ffmpeg a media reproducción → silencio + fin limpio, sin
//     cuelgue y SIN zombi (verificación por PID);
//   · underrun (fuente lenta simulada) → el hilo RT nunca bloquea.
// =============================================================================

using System.Diagnostics;
using NAudio.Wave;
using WinCue.Audio;

namespace WinCue.Audio.Tests;

public class StreamingAudioSourceTests : IDisposable
{
    private const int SR = 48000;
    private readonly string _dir;

    public StreamingAudioSourceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wincue_stream_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static bool HasFFmpeg => AudioDecoder.IsFFmpegAvailable;

    /// <summary>WAV float32 ESTÉREO determinista: L[i] = sin 440, R[i] = i·1e−5.
    /// Float pasa por ffmpeg y por NAudio sin recuantizar → paridad exacta.</summary>
    private string MakeFloatWav(string name, double seconds)
    {
        string path = Path.Combine(_dir, name);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(SR, 2);
        using var writer = new WaveFileWriter(path, format);
        int frames = (int)(SR * seconds);
        var frame = new float[2];
        for (int i = 0; i < frames; i++)
        {
            frame[0] = 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / SR);
            frame[1] = (float)(i * 1e-5);
            writer.WriteSamples(frame, 0, 2);
        }
        return path;
    }

    /// <summary>Lee TODA la pasada actual de la fuente (esperando en los
    /// underruns, con deadline) y devuelve los samples intercalados.</summary>
    private static List<float> ReadPass(ICueAudioSource src, int timeoutMs = 20_000)
    {
        var result = new List<float>();
        var block = new float[1024 * src.Channels];
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            int got = src.ReadFrames(block, 0, 1024);
            if (got > 0)
            {
                for (int i = 0; i < got * src.Channels; i++)
                    result.Add(block[i]);
                continue;
            }
            if (src.PassEnded)
                break;
            Thread.Sleep(5);   // underrun: el ring aún se está llenando
        }
        return result;
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

    /// <summary>Todos los ffmpeg lanzados por la fuente deben estar MUERTOS.</summary>
    private static void AssertNoZombies(StreamingAudioSource src)
    {
        foreach (int pid in src.SpawnedProcessIds)
        {
            bool exited = WaitFor(() =>
            {
                try { using var p = Process.GetProcessById(pid); return p.HasExited; }
                catch (ArgumentException) { return true; }   // ya no existe
            }, 5_000);
            Assert.True(exited, $"ffmpeg PID {pid} sigue vivo (zombi)");
        }
    }

    // ------------------------------------------------------------------
    // (a) Paridad numérica con la ruta RAM
    // ------------------------------------------------------------------

    [Fact]
    public void MismosSamples_QueLaRutaRam()
    {
        if (!HasFFmpeg) return;

        string wav = MakeFloatWav("paridad.wav", 2.0);
        var ram = AudioDecoder.Decode(wav);
        Assert.Equal(2, ram.Channels);
        Assert.Equal(SR, ram.SampleRate);

        using var src = new StreamingAudioSource(wav, SR);
        var streamed = ReadPass(src);

        Assert.Equal(ram.Samples.Length, streamed.Count);
        for (int i = 0; i < streamed.Count; i++)
            Assert.Equal(ram.Samples[i], streamed[i], 4);

        src.Dispose();
        AssertNoZombies(src);
    }

    // ------------------------------------------------------------------
    // (b) trim_start / trim_end honrados (-ss / -t == índices RAM)
    // ------------------------------------------------------------------

    [Fact]
    public void TrimStartYTrimEnd_Honrados()
    {
        if (!HasFFmpeg) return;

        string wav = MakeFloatWav("trims.wav", 2.0);
        var ram = AudioDecoder.Decode(wav);
        using var ramSrc = new RamAudioSource(ram, trimStart: 0.5, trimEnd: 1.25);
        var expected = ReadPass(ramSrc);

        using var src = new StreamingAudioSource(wav, SR, trimStart: 0.5, trimEnd: 1.25);
        Assert.Equal((long)(0.75 * SR), src.TotalFrames);
        var streamed = ReadPass(src);

        Assert.Equal(expected.Count, streamed.Count);
        // El canal R codifica el ÍNDICE (i·1e−5): el primer frame debe ser el
        // frame 24000 del archivo (0.5 s) — trim aplicado de verdad
        Assert.Equal(0.5 * SR * 1e-5, streamed[1], 4);
        for (int i = 0; i < streamed.Count; i++)
            Assert.Equal(expected[i], streamed[i], 4);

        src.Dispose();
        AssertNoZombies(src);
    }

    // ------------------------------------------------------------------
    // (c) Pasadas: empalme limpio y elapsed por pasada
    // ------------------------------------------------------------------

    [Fact]
    public void Pasadas_EmpalmeLimpio_YMismoContenido()
    {
        if (!HasFFmpeg) return;

        string wav = MakeFloatWav("pasadas.wav", 0.5);
        using var src = new StreamingAudioSource(wav, SR);

        var pass1 = ReadPass(src);
        Assert.Equal((int)(0.5 * SR) * 2, pass1.Count);
        Assert.True(src.PassEnded);

        // El provider precalienta ANTES del final; aquí simulamos su patrón
        src.PrepareNextPass();
        Assert.True(src.BeginNextPass());
        var pass2 = ReadPass(src);

        // Empalme limpio = la pasada 2 es EXACTAMENTE el mismo contenido
        // (mismo comando determinista): ni un sample perdido ni distinto
        Assert.Equal(pass1.Count, pass2.Count);
        for (int i = 0; i < pass1.Count; i++)
            Assert.Equal(pass1[i], pass2[i], 4);

        src.Dispose();
        AssertNoZombies(src);
    }

    [Fact]
    public void Provider_PlayCount2_ElapsedResetaPorPasada()
    {
        if (!HasFFmpeg) return;

        string wav = MakeFloatWav("loop.wav", 0.5);
        using var src = new StreamingAudioSource(wav, SR);
        using var p = new CueSampleProvider(1, src, playCount: 2);

        var block = new float[512 * 2];
        var elapsed = new List<double>();
        long totalFrames = 0;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            int read = p.Read(block, 0, block.Length);
            if (read == 0 && p.IsCompleted)
                break;
            totalFrames += read / 2;
            elapsed.Add(p.ElapsedSeconds);
            if (read == 0)
                Thread.Sleep(5);
        }

        Assert.True(p.IsCompleted, "el provider no completó las 2 pasadas");
        // El elapsed crece dentro de la pasada y RESETEA al empezar la 2ª
        // (el reloj del vídeo depende de ese reset)
        bool reset = false;
        for (int i = 1; i < elapsed.Count; i++)
            if (elapsed[i] < elapsed[i - 1] - 0.1) { reset = true; break; }
        Assert.True(reset, "ElapsedSeconds no reseteó al cambiar de pasada");
        Assert.True(elapsed.Max() <= 0.5 + 0.05,
            $"elapsed superó la duración de la pasada: {elapsed.Max():0.###}s");
        // Los underruns emiten silencio SIN frames de contenido perdidos: el
        // total contado incluye silencio, así que solo acotamos por abajo
        Assert.True(totalFrames >= 2L * (long)(0.5 * SR),
            $"faltó contenido: {totalFrames} frames");

        src.Dispose();
        AssertNoZombies(src);
    }

    // ------------------------------------------------------------------
    // (d) Muerte del ffmpeg a media reproducción
    // ------------------------------------------------------------------

    [Fact]
    public void MatarFfmpegAMedias_FinLimpio_SinCuelgueNiZombi()
    {
        if (!HasFFmpeg) return;

        // 60 s: mucho más que el ring (~3 s) — el proceso sigue vivo al matarlo
        string wav = MakeFloatWav("largo.wav", 60.0);
        using var src = new StreamingAudioSource(wav, SR);
        string? fatal = null;
        src.FatalError += msg => fatal = msg;

        // Consumir un poco de audio real primero
        var block = new float[1024 * 2];
        Assert.True(WaitFor(() => src.ReadFrames(block, 0, 1024) > 0, 10_000),
            "el streaming no entregó audio");

        int pid = src.CurrentProcessId ?? -1;
        Assert.True(pid > 0, "sin PID del ffmpeg actual");
        using (var proc = Process.GetProcessById(pid))
            proc.Kill(entireProcessTree: true);

        // El ring se drena y la fuente COMPLETA (sin bloquear jamás)
        Assert.True(WaitFor(() =>
        {
            while (src.ReadFrames(block, 0, 1024) > 0) { /* drenar */ }
            return src.PassEnded;
        }, 10_000), "la fuente no terminó tras matar el ffmpeg");

        // El proceso murió MAL → evento FatalError (el engine lo eleva y el
        // controlador restaura el audio por mpv)
        Assert.True(WaitFor(() => fatal is not null, 5_000),
            "no se emitió FatalError tras la muerte del proceso");

        src.Dispose();
        AssertNoZombies(src);
    }

    // ------------------------------------------------------------------
    // (e) Underrun: el hilo RT jamás bloquea
    // ------------------------------------------------------------------

    /// <summary>Fuente LENTA simulada: no entrega nada hasta que se le pide.</summary>
    private sealed class StarvingSource : ICueAudioSource
    {
        private readonly float[] _data;
        private int _pos;
        public volatile bool Starve = true;

        public StarvingSource(int frames)
        {
            _data = new float[frames];
            for (int i = 0; i < frames; i++) _data[i] = i;
        }

        public int SampleRate => 1000;
        public int Channels => 1;
        public long TotalFrames => _data.Length;
        public bool PassEnded => _pos >= _data.Length;

        public int ReadFrames(float[] dest, int offset, int frames)
        {
            if (Starve || PassEnded)
                return 0;
            int chunk = Math.Min(frames, _data.Length - _pos);
            Array.Copy(_data, _pos, dest, offset, chunk);
            _pos += chunk;
            return chunk;
        }

        public void PrepareNextPass() { }
        public bool BeginNextPass() { _pos = 0; return true; }
        public void Dispose() { }
    }

    [Fact]
    public void Underrun_SilencioSinAvanzar_YRecuperaElContenido()
    {
        using var src = new StarvingSource(500);
        using var p = new CueSampleProvider(1, src);
        var block = new float[64];

        // Fuente muerta de hambre: el Read devuelve el bloque ENTERO en
        // silencio, al instante, sin avanzar el reloj
        var sw = Stopwatch.StartNew();
        int read = p.Read(block, 0, 64);
        sw.Stop();
        Assert.Equal(64, read);
        Assert.All(block, v => Assert.Equal(0f, v));
        Assert.Equal(0.0, p.ElapsedSeconds);
        Assert.Equal(1, p.UnderrunCount);
        Assert.True(sw.ElapsedMilliseconds < 100, "el Read bloqueó en el underrun");
        Assert.False(p.IsCompleted);   // el mixer NO nos retira (devolvió 64)

        // Vuelve el dato: el contenido retoma EXACTAMENTE donde iba (frame 0)
        src.Starve = false;
        read = p.Read(block, 0, 64);
        Assert.Equal(64, read);
        Assert.Equal(0f, block[0]);
        Assert.Equal(63f, block[63]);
        Assert.Equal(0.064, p.ElapsedSeconds, 6);
    }
}
