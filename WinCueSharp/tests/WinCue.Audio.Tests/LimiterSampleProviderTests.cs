// =============================================================================
// LimiterSampleProviderTests.cs — Limitador brickwall del bus de salida
// =============================================================================
// Sin hardware: el limitador es aritmética pura sobre un ISampleProvider.
// =============================================================================

using NAudio.Wave;
using WinCue.Audio;

namespace WinCue.Audio.Tests;

public class LimiterSampleProviderTests
{
    /// <summary>Fuente estéreo de amplitud constante (peor caso: DC a tope).</summary>
    private sealed class ConstantProvider : ISampleProvider
    {
        public float Amplitude;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public ConstantProvider(float amplitude) => Amplitude = amplitude;

        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
                buffer[offset + i] = Amplitude;
            return count;
        }
    }

    private const float CeilingLinear = 0.8913f;   // −1 dBFS

    [Fact]
    public void HotSignal_NeverExceedsCeiling()
    {
        var limiter = new LimiterSampleProvider(new ConstantProvider(1.5f));
        var buf = new float[9600];   // 100 ms estéreo
        limiter.Read(buf, 0, buf.Length);

        // Ataque instantáneo: NINGUNA muestra escapa, ni la primera
        Assert.All(buf, s => Assert.True(Math.Abs(s) <= CeilingLinear + 0.0005f,
            $"muestra {s} por encima del techo"));
        Assert.True(limiter.GainReductionDb > 3.0f,
            $"GR esperado ~4.5 dB, fue {limiter.GainReductionDb}");
    }

    [Fact]
    public void QuietSignal_PassesUntouched()
    {
        var limiter = new LimiterSampleProvider(new ConstantProvider(0.1f));
        var buf = new float[4800];
        limiter.Read(buf, 0, buf.Length);

        Assert.All(buf, s => Assert.Equal(0.1f, s, 4));
        Assert.Equal(0f, limiter.GainReductionDb);
    }

    [Fact]
    public void Release_RecoversAfterThePeakPasses()
    {
        var source = new ConstantProvider(1.5f);
        var limiter = new LimiterSampleProvider(source);
        var buf = new float[9600];
        limiter.Read(buf, 0, buf.Length);
        Assert.True(limiter.GainReductionDb > 3.0f);

        // El pico pasó → con release de 150 ms, en 1 s el GR debe volver a ~0
        source.Amplitude = 0.1f;
        for (int i = 0; i < 10; i++)
            limiter.Read(buf, 0, buf.Length);
        Assert.True(limiter.GainReductionDb < 0.1f,
            $"el limitador no soltó tras el pico (GR {limiter.GainReductionDb})");
    }

    [Fact]
    public void StereoLink_AppliesSameGainToBothChannels()
    {
        // L caliente, R templado: el limitador stereo-linked debe atenuar
        // los DOS por igual (si no, la imagen estéreo baila)
        var source = new AsymmetricProvider();
        var limiter = new LimiterSampleProvider(source);
        var buf = new float[9600];
        limiter.Read(buf, 0, buf.Length);

        // En régimen (última parte del bloque): ratio L/R intacto = 3.0
        float l = Math.Abs(buf[^2]), r = Math.Abs(buf[^1]);
        Assert.Equal(3.0f, l / r, 2);
        Assert.True(l <= CeilingLinear + 0.0005f);
    }

    private sealed class AsymmetricProvider : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            for (int f = 0; f < count / 2; f++)
            {
                buffer[offset + f * 2] = 1.5f;       // L
                buffer[offset + f * 2 + 1] = 0.5f;   // R
            }
            return count;
        }
    }
}
