// =============================================================================
// CueSampleProviderLiveTests.cs — Controles EN VIVO del provider (auditoría 03)
// =============================================================================
// Huecos de ruta crítica a 0%: pausa por cue (SetPaused congela sin matar el
// stream), EQ en caliente (SetEq), el RTA (GetSpectrum: lazy, 60 bandas log)
// y el medidor GR del compresor (GetGainReductionDb). Todo DSP puro, sin
// hardware — mismo patrón que CueSampleProviderTests.
// =============================================================================

using WinCue.Audio;
using WinCue.Core;

namespace WinCue.Audio.Tests;

public class CueSampleProviderLiveTests
{
    private static AudioBuffer Ramp(int frames, int sr = 1000)
    {
        var s = new float[frames];
        for (int i = 0; i < frames; i++) s[i] = i;
        return new AudioBuffer { Samples = s, SampleRate = sr, Channels = 1 };
    }

    private static AudioBuffer Sine(double freq, int fs, int frames, float amp = 1.0f)
    {
        var s = new float[frames];
        for (int i = 0; i < frames; i++)
            s[i] = amp * (float)Math.Sin(2.0 * Math.PI * freq * i / fs);
        return new AudioBuffer { Samples = s, SampleRate = fs, Channels = 1 };
    }

    private static float[] Read(CueSampleProvider p, int frames)
    {
        var block = new float[frames * p.WaveFormat.Channels];
        int n = p.Read(block, 0, block.Length);
        return block[..n];
    }

    // ------------------------------------------------------------------
    // Pausa por cue
    // ------------------------------------------------------------------

    [Fact]
    public void SetPaused_SilencioConPosicionCongelada_YReanudaDondeEstaba()
    {
        using var p = new CueSampleProvider(1, Ramp(100));

        var antes = Read(p, 50);
        Assert.Equal(49f, antes[^1]);            // reproduce 0..49
        double elapsed = p.ElapsedSeconds;

        p.SetPaused(true);
        Assert.True(p.IsPaused);
        var pausa = Read(p, 30);
        Assert.Equal(30, pausa.Length);          // el stream sigue VIVO (silencio)
        Assert.All(pausa, v => Assert.Equal(0f, v));
        Assert.Equal(elapsed, p.ElapsedSeconds); // posición congelada
        Assert.False(p.IsCompleted);

        p.SetPaused(false);
        var despues = Read(p, 10);
        Assert.Equal(50f, despues[0]);           // continúa EXACTAMENTE donde estaba
    }

    // ------------------------------------------------------------------
    // EQ en caliente (SetEq mientras suena)
    // ------------------------------------------------------------------

    [Fact]
    public void SetEq_EnCaliente_AplicaLaGananciaDelClip()
    {
        var s = new float[2000];
        Array.Fill(s, 1.0f);
        using var p = new CueSampleProvider(1,
            new AudioBuffer { Samples = s, SampleRate = 1000, Channels = 1 });

        var antes = Read(p, 100);
        Assert.All(antes, v => Assert.Equal(1.0f, v, precision: 5));

        p.SetEq(new AudioEqParams(GainDb: -6.0206));   // ×0.5

        var despues = Read(p, 100);
        Assert.All(despues, v => Assert.Equal(0.5f, v, precision: 3));
    }

    // ------------------------------------------------------------------
    // RTA (GetSpectrum)
    // ------------------------------------------------------------------

    [Fact]
    public void GetSpectrum_EsLazy_YLocalizaElTono()
    {
        // 440 Hz a SR 8000: la banda log b = 20·log10(440/20) ≈ 26 debe ser
        // el pico del espectro tras llenar el bloque FFT de 2048 muestras.
        using var p = new CueSampleProvider(1, Sine(440.0, 8000, 4096, amp: 0.5f));

        Assert.Null(p.GetSpectrum());            // 1ª llamada: arma el análisis

        var block = new float[4096];
        p.Read(block, 0, block.Length);          // 4096 muestras ≥ 2 FFTs

        float[]? spec = p.GetSpectrum();
        Assert.NotNull(spec);
        Assert.Equal(60, spec!.Length);
        Assert.All(spec, v => Assert.InRange(v, -90f, 0f));

        int peakBand = Array.IndexOf(spec, spec.Max());
        Assert.InRange(peakBand, 24, 28);        // 440 Hz ± tolerancia de banda
        // NOTA: la escala ABSOLUTA del RTA es baja (~-73 dBFS para un seno de
        // 0,5 — la normalización divide dos veces por N; posible defecto
        // cosmético anotado en la auditoría 03). Lo funcional para el RTA es
        // que el tono DESTAQUE sobre el suelo de las demás bandas:
        var ordered = spec.OrderBy(v => v).ToArray();
        float floor = ordered[spec.Length / 2];  // mediana ≈ suelo
        Assert.True(spec[peakBand] > floor + 15f,
            $"el pico ({spec[peakBand]:0.0} dB) no destaca sobre el suelo ({floor:0.0} dB)");
    }

    // ------------------------------------------------------------------
    // Medidor GR del compresor
    // ------------------------------------------------------------------

    [Fact]
    public void GetGainReductionDb_ConCompresor_ReportaLaReduccion()
    {
        // Mismo escenario que el GR de EqChainTests, pero por la API del
        // provider (la que usa el controlador): 0 dBFS, umbral −20, ratio 4:1
        // → ~15 dB de reducción.
        using var p = new CueSampleProvider(1, Sine(1000.0, 48000, 9600));
        p.SetEq(new AudioEqParams(Comp: new AudioCompParams(
            Enabled: true, ThresholdDb: -20.0, Ratio: 4.0,
            AttackMs: 0.1, ReleaseMs: 500.0, MakeupDb: 0.0)));

        var block = new float[9600];
        p.Read(block, 0, block.Length);

        Assert.InRange(p.GetGainReductionDb(), 13.0f, 16.5f);
    }

    [Fact]
    public void GetGainReductionDb_SinCompresor_EsCero()
    {
        using var p = new CueSampleProvider(1, Sine(1000.0, 48000, 4800));
        var block = new float[4800];
        p.Read(block, 0, block.Length);

        Assert.Equal(0f, p.GetGainReductionDb());
    }
}
