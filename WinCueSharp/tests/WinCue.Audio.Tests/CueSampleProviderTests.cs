// =============================================================================
// CueSampleProviderTests.cs — Matemática del hilo RT verificada muestra a muestra
// =============================================================================
// SR = 1000 Hz en casi todos los tests para que frames == milisegundos.
// =============================================================================

using WinCue.Audio;

namespace WinCue.Audio.Tests;

public class CueSampleProviderTests
{
    private const int SR = 1000;

    /// <summary>Buffer mono con rampa 0,1,2,…,n-1 (valores = índice de frame).</summary>
    private static AudioBuffer RampBuffer(int frames)
    {
        var s = new float[frames];
        for (int i = 0; i < frames; i++) s[i] = i;
        return new AudioBuffer { Samples = s, SampleRate = SR, Channels = 1 };
    }

    /// <summary>Buffer mono constante a 1.0.</summary>
    private static AudioBuffer OnesBuffer(int frames)
    {
        var s = new float[frames];
        Array.Fill(s, 1.0f);
        return new AudioBuffer { Samples = s, SampleRate = SR, Channels = 1 };
    }

    /// <summary>Lee TODO el provider en bloques de blockFrames y lo concatena.</summary>
    private static List<float> ReadAll(CueSampleProvider p, int blockFrames = 64, int maxFrames = 1_000_000)
    {
        var result = new List<float>();
        int channels = p.WaveFormat.Channels;
        var block = new float[blockFrames * channels];
        while (result.Count < maxFrames * channels)
        {
            int read = p.Read(block, 0, block.Length);
            if (read == 0) break;
            for (int i = 0; i < read; i++) result.Add(block[i]);
        }
        return result;
    }

    // ------------------------------------------------------------------
    // Reproducción básica
    // ------------------------------------------------------------------

    [Fact]
    public void PlainPlayback_OutputsSourceVerbatim_ThenEnds()
    {
        using var p = new CueSampleProvider(1, RampBuffer(500));
        var output = ReadAll(p);
        Assert.Equal(500, output.Count);
        for (int i = 0; i < 500; i++)
            Assert.Equal(i, output[i]);
        // Tras el fin: Read devuelve 0 y queda completado
        Assert.True(p.IsCompleted);
    }

    [Fact]
    public void Volume_ScalesAllSamples()
    {
        using var p = new CueSampleProvider(1, OnesBuffer(100), volume: 0.5);
        var output = ReadAll(p);
        Assert.All(output, v => Assert.Equal(0.5f, v, precision: 5));
    }

    [Fact]
    public void Master_MultipliesOnTopOfVolume()
    {
        using var p = new CueSampleProvider(1, OnesBuffer(100), volume: 0.5);
        p.SetMaster(0.5f);
        var output = ReadAll(p);
        Assert.All(output, v => Assert.Equal(0.25f, v, precision: 5));
    }

    // ------------------------------------------------------------------
    // Fades
    // ------------------------------------------------------------------

    [Fact]
    public void FadeIn_RampsFromZeroToVolume()
    {
        // 1000 frames, fade-in de 0.5 s = 500 frames
        using var p = new CueSampleProvider(1, OnesBuffer(1000), volume: 1.0, fadeIn: 0.5);
        var output = ReadAll(p);
        Assert.Equal(1000, output.Count);

        Assert.True(output[0] < 0.01f, $"primer sample debe ser ~0, fue {output[0]}");
        Assert.Equal(0.5f, output[249], 1);            // mitad del fade ≈ 0.5
        Assert.Equal(1.0f, output[499], 2);            // fin del fade = volumen
        Assert.All(output.Skip(500), v => Assert.Equal(1.0f, v, precision: 4));
        // Monótono creciente durante el fade
        for (int i = 1; i < 500; i++)
            Assert.True(output[i] >= output[i - 1]);
    }

    [Fact]
    public void AutoFadeOut_RampsToZeroAtEnd()
    {
        // 1000 frames, fade-out de 0.2 s = últimos 200 frames
        using var p = new CueSampleProvider(1, OnesBuffer(1000), fadeOut: 0.2);
        var output = ReadAll(p);
        Assert.Equal(1000, output.Count);

        Assert.All(output.Take(800), v => Assert.Equal(1.0f, v, precision: 4));
        Assert.True(output[999] < 0.01f, $"último sample debe ser ~0, fue {output[999]}");
        // Monótono decreciente durante el fade
        for (int i = 801; i < 1000; i++)
            Assert.True(output[i] <= output[i - 1]);
    }

    [Fact]
    public void ManualFade_ToTarget_ChangesSteadyVolume()
    {
        using var p = new CueSampleProvider(1, OnesBuffer(2000));
        // Consumir 100 frames a volumen 1
        var pre = new float[100];
        p.Read(pre, 0, 100);
        Assert.Equal(1.0f, pre[99], 4);

        // Fade a 0.2 en 0.5 s (500 frames), sin parar
        p.BeginFade(0.2, 0.5, stopAfter: false);
        var output = ReadAll(p);
        // Tras el fade, régimen permanente a 0.2
        Assert.Equal(0.2f, output[600], 3);
        Assert.Equal(0.2f, output[^1], 3);
    }

    [Fact]
    public void FadeToZero_WithStopAfter_EndsPlayback()
    {
        using var p = new CueSampleProvider(1, OnesBuffer(100_000));
        p.BeginFade(0.0, 0.1, stopAfter: true);   // 100 frames de fade
        var output = ReadAll(p);

        // Termina poco después de completar el fade (mismo bloque como máximo)
        Assert.InRange(output.Count, 100, 200);
        Assert.True(output[^1] < 0.02f);
        Assert.True(p.IsCompleted);
    }

    [Fact]
    public void InstantFade_DurationZero_AppliesImmediately()
    {
        using var p = new CueSampleProvider(1, OnesBuffer(1000));
        p.BeginFade(0.3, 0.0, stopAfter: false);
        var block = new float[10];
        p.Read(block, 0, 10);
        Assert.All(block, v => Assert.Equal(0.3f, v, precision: 5));
    }

    [Fact]
    public void SetVolume_Instant_WhenNoFadeActive()
    {
        using var p = new CueSampleProvider(1, OnesBuffer(1000));
        p.SetVolume(0.7);
        var block = new float[10];
        p.Read(block, 0, 10);
        Assert.All(block, v => Assert.Equal(0.7f, v, precision: 5));
    }

    // ------------------------------------------------------------------
    // Loop / play_count
    // ------------------------------------------------------------------

    [Fact]
    public void PlayCount_RepeatsExactlyNTimes_SampleAccurate()
    {
        using var p = new CueSampleProvider(1, RampBuffer(300), playCount: 3);
        var output = ReadAll(p);
        Assert.Equal(900, output.Count);
        // Sin huecos en el wrap: el frame 300 es de nuevo el 0 de la fuente
        Assert.Equal(299f, output[299]);
        Assert.Equal(0f, output[300]);
        Assert.Equal(0f, output[600]);
        Assert.Equal(299f, output[899]);
    }

    [Fact]
    public void InfiniteLoop_ContinuesUntilRequestStop()
    {
        using var p = new CueSampleProvider(1, RampBuffer(100), loop: true);
        var block = new float[64];
        int total = 0;
        // Leer 10 bloques: mucho más que la longitud del buffer
        for (int i = 0; i < 10; i++)
            total += p.Read(block, 0, 64);
        Assert.Equal(640, total);

        p.RequestStop();
        // El siguiente Read termina (0) — la parada actúa al inicio del bloque
        Assert.Equal(0, p.Read(block, 0, 64));
        Assert.True(p.IsCompleted);
    }

    [Fact]
    public void AutoFadeOut_WithPlayCount_OnlyArmsOnLastPass()
    {
        // Bug de la versión Python (no replicado): con play_count>1 el fade-out
        // se armaba en la primera pasada y las siguientes sonaban a ganancia 0.
        using var p = new CueSampleProvider(1, OnesBuffer(200), fadeOut: 0.1, playCount: 2);
        var output = ReadAll(p);
        Assert.Equal(400, output.Count);

        // Primera pasada COMPLETA a volumen pleno (incluido su final)
        Assert.All(output.Take(200), v => Assert.Equal(1.0f, v, precision: 4));
        // Segunda pasada: fade solo en los últimos 100 frames
        Assert.All(output.Skip(200).Take(100), v => Assert.Equal(1.0f, v, precision: 4));
        Assert.True(output[399] < 0.01f);
    }

    // ------------------------------------------------------------------
    // Trim
    // ------------------------------------------------------------------

    [Fact]
    public void Trim_PlaysOnlyTheRegion()
    {
        // Fuente 0..999; trim in=0.1s (frame 100), out=0.5s (frame 500)
        using var p = new CueSampleProvider(1, RampBuffer(1000), trimStart: 0.1, trimEnd: 0.5);
        Assert.Equal(400, p.TotalFrames);
        Assert.Equal(0.4, p.DurationSeconds, 6);

        var output = ReadAll(p);
        Assert.Equal(400, output.Count);
        Assert.Equal(100f, output[0]);
        Assert.Equal(499f, output[^1]);
    }

    [Fact]
    public void InvalidTrim_FallsBackToFullFile()
    {
        // in > out → región vacía → archivo completo (defensivo)
        using var p = new CueSampleProvider(1, RampBuffer(100), trimStart: 0.9, trimEnd: 0.1);
        Assert.Equal(100, p.TotalFrames);
    }

    [Fact]
    public void Trim_WithLoop_WrapsInsideRegion()
    {
        using var p = new CueSampleProvider(1, RampBuffer(1000), trimStart: 0.1, trimEnd: 0.2, playCount: 2);
        var output = ReadAll(p);
        Assert.Equal(200, output.Count);
        Assert.Equal(100f, output[0]);
        Assert.Equal(199f, output[99]);
        Assert.Equal(100f, output[100]);   // wrap vuelve al trim_start
    }

    // ------------------------------------------------------------------
    // Estéreo
    // ------------------------------------------------------------------

    [Fact]
    public void Stereo_AppliesGainToBothChannels()
    {
        // L = 1.0, R = 0.5 constantes, 100 frames
        var s = new float[200];
        for (int f = 0; f < 100; f++) { s[f * 2] = 1.0f; s[f * 2 + 1] = 0.5f; }
        var buf = new AudioBuffer { Samples = s, SampleRate = SR, Channels = 2 };

        using var p = new CueSampleProvider(1, buf, volume: 0.5);
        var output = ReadAll(p);
        Assert.Equal(200, output.Count);
        for (int f = 0; f < 100; f++)
        {
            Assert.Equal(0.5f, output[f * 2], 4);
            Assert.Equal(0.25f, output[f * 2 + 1], 4);
        }
    }

    // ------------------------------------------------------------------
    // Telemetría (latencia / VU)
    // ------------------------------------------------------------------

    [Fact]
    public void LatencyMeasured_FiresExactlyOnce()
    {
        using var p = new CueSampleProvider(1, OnesBuffer(1000));
        int count = 0;
        double reported = -1;
        p.LatencyMeasured += ms => { count++; reported = ms; };

        var block = new float[64];
        p.Read(block, 0, 64);
        p.Read(block, 0, 64);
        Assert.Equal(1, count);
        Assert.True(reported >= 0.0);
    }

    [Fact]
    public void LevelReport_FiresThrottled_Stereo()
    {
        using var p = new CueSampleProvider(1, OnesBuffer(10_000));
        var levels = new List<(float L, float R)>();
        p.LevelReport += (l, r) => levels.Add((l, r));

        var block = new float[64];
        for (int i = 0; i < 8; i++)
            p.Read(block, 0, 64);
        // 1 de cada 4 lecturas → 2 reports en 8 lecturas
        Assert.Equal(2, levels.Count);
        // RMS de señal constante 1.0 = 1.0; mono → L=R
        Assert.All(levels, l => Assert.Equal(1.0f, l.L, precision: 3));
        Assert.All(levels, l => Assert.Equal(1.0f, l.R, precision: 3));
    }

    [Fact]
    public void LevelReport_Stereo_SeparatesChannels()
    {
        // L = 1.0, R = 0.5 → RMS L=1.0, R=0.5
        var s = new float[2000];
        for (int f = 0; f < 1000; f++) { s[f * 2] = 1.0f; s[f * 2 + 1] = 0.5f; }
        var buf = new AudioBuffer { Samples = s, SampleRate = SR, Channels = 2 };
        using var p = new CueSampleProvider(1, buf);
        (float L, float R)? report = null;
        p.LevelReport += (l, r) => report = (l, r);

        var block = new float[128];
        for (int i = 0; i < 4; i++)
            p.Read(block, 0, 128);

        Assert.NotNull(report);
        Assert.Equal(1.0f, report!.Value.L, 3);
        Assert.Equal(0.5f, report.Value.R, 3);
    }
}
