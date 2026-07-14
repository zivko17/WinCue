// =============================================================================
// EqChainTests.cs — Cadena de EQ por clip (biquads RBJ + ganancia)
// =============================================================================

using WinCue.Audio;
using WinCue.Core;

namespace WinCue.Audio.Tests;

public class EqChainTests
{
    private static float[] Sine(double freq, int fs, int n)
    {
        var buf = new float[n];
        for (int i = 0; i < n; i++)
            buf[i] = (float)Math.Sin(2.0 * Math.PI * freq * i / fs);
        return buf;
    }

    private static double Rms(float[] buf, int from)
    {
        double sum = 0;
        for (int i = from; i < buf.Length; i++)
            sum += (double)buf[i] * buf[i];
        return Math.Sqrt(sum / (buf.Length - from));
    }

    [Fact]
    public void Bypass_NoModificaLaSenal()
    {
        var chain = new EqChain(48000, 1);
        chain.Configure(null);
        var buf = Sine(1000, 48000, 4800);
        var original = (float[])buf.Clone();

        chain.Process(buf, 0, buf.Length);

        Assert.Equal(original, buf);
    }

    [Fact]
    public void Gain6Db_DuplicaLaAmplitud()
    {
        var chain = new EqChain(48000, 1);
        chain.Configure(new AudioEqParams(GainDb: 6.0206));
        var buf = Sine(1000, 48000, 4800);
        double before = Rms(buf, 0);

        chain.Process(buf, 0, buf.Length);

        Assert.Equal(before * 2.0, Rms(buf, 0), 2);
    }

    [Fact]
    public void Hpf_EliminaLaContinua()
    {
        var chain = new EqChain(48000, 1);
        chain.Configure(new AudioEqParams(HpfOn: true, HpfFreq: 100));
        var buf = new float[48000];
        Array.Fill(buf, 1.0f);   // DC puro

        chain.Process(buf, 0, buf.Length);

        // Tras el transitorio, la DC debe estar prácticamente eliminada
        Assert.True(Math.Abs(buf[^1]) < 0.01,
            $"DC residual: {buf[^1]}");
    }

    [Fact]
    public void Lpf_AtenuaLosAgudos()
    {
        var chain = new EqChain(48000, 1);
        chain.Configure(new AudioEqParams(LpfOn: true, LpfFreq: 500));
        var buf = Sine(8000, 48000, 48000);

        chain.Process(buf, 0, buf.Length);

        // 8 kHz con LPF a 500 Hz (12 dB/oct): ~-48 dB → RMS ínfimo
        Assert.True(Rms(buf, 4800) < 0.05, $"RMS: {Rms(buf, 4800)}");
    }

    [Fact]
    public void Peaking_GananciaCero_EsTransparente()
    {
        var chain = new EqChain(48000, 1);
        chain.Configure(new AudioEqParams(EqEnabled: true, Bands: new[]
        {
            new EqBand(1000, 0.0, 1.0),
        }));
        var buf = Sine(1000, 48000, 4800);
        var original = (float[])buf.Clone();

        chain.Process(buf, 0, buf.Length);

        Assert.Equal(original, buf);   // gain 0 → la banda ni se instancia
    }

    [Fact]
    public void Peaking_Sube6DbEnSuFrecuencia()
    {
        var chain = new EqChain(48000, 1);
        chain.Configure(new AudioEqParams(EqEnabled: true, Bands: new[]
        {
            new EqBand(1000, 6.0, 1.0),
        }));
        var buf = Sine(1000, 48000, 48000);

        chain.Process(buf, 0, buf.Length);

        // +6 dB ≈ ×2 en amplitud (medir pasado el transitorio)
        double rms = Rms(buf, 4800);
        Assert.InRange(rms, 0.70710678 * 1.9, 0.70710678 * 2.1);
    }

    [Fact]
    public void ResponseDb_CoincideConLaBanda()
    {
        var eq = new AudioEqParams(EqEnabled: true, Bands: new[]
        {
            new EqBand(1000, 6.0, 1.0),
        });

        Assert.Equal(6.0, EqChain.ResponseDb(eq, 1000), 1);
        Assert.Equal(0.0, EqChain.ResponseDb(eq, 20), 1);      // lejos de la banda
        Assert.Equal(0.0, EqChain.ResponseDb(eq, 20000), 0);
    }

    [Fact]
    public void Compresor_ReduceLoQueSuperaElUmbral()
    {
        var chain = new EqChain(48000, 1);
        chain.Configure(new AudioEqParams(Comp: new AudioCompParams(
            Enabled: true, ThresholdDb: -20.0, Ratio: 4.0,
            AttackMs: 1.0, ReleaseMs: 50.0)));
        var buf = Sine(1000, 48000, 48000);   // 0 dBFS pico: 20 dB sobre umbral

        chain.Process(buf, 0, buf.Length);

        // 20 dB de exceso a ratio 4:1 → ~15 dB de reducción
        double rms = Rms(buf, 9600);
        Assert.True(rms < 0.71 * 0.35, $"RMS: {rms} (sin reducción sería 0.707)");
    }

    [Fact]
    public void Compresor_NoTocaLoQueQuedaBajoElUmbral()
    {
        var chain = new EqChain(48000, 1);
        chain.Configure(new AudioEqParams(Comp: new AudioCompParams(
            Enabled: true, ThresholdDb: -6.0, Ratio: 8.0)));
        var buf = Sine(1000, 48000, 24000);
        for (int i = 0; i < buf.Length; i++)
            buf[i] *= 0.1f;   // −20 dBFS, muy por debajo del umbral
        double before = Rms(buf, 4800);

        chain.Process(buf, 0, buf.Length);

        Assert.Equal(before, Rms(buf, 4800), 3);
    }

    [Fact]
    public void Estereo_ProcesaLosDosCanalesIgual()
    {
        var chain = new EqChain(48000, 2);
        chain.Configure(new AudioEqParams(GainDb: -6.0206));
        var buf = new float[9600];
        for (int i = 0; i < buf.Length; i += 2)
        {
            buf[i] = 0.8f;      // L constante
            buf[i + 1] = 0.4f;  // R constante
        }

        chain.Process(buf, 0, buf.Length);

        Assert.Equal(0.4f, buf[100], 2);
        Assert.Equal(0.2f, buf[101], 2);
    }
}
