// =============================================================================
// MpvAfChainTests.cs — AudioEqParams → cadena af de mpv (string puro, sin mpv)
// =============================================================================

using System.Globalization;
using WinCue.Core;
using WinCue.Video;

namespace WinCue.Video.Tests;

public class MpvAfChainTests
{
    [Fact]
    public void NullOrNeutralParams_ProduceEmptyChain()
    {
        Assert.Equal("", MpvAfChain.Build(null));
        Assert.Equal("", MpvAfChain.Build(new AudioEqParams()));
        // Bandas presentes pero EQ apagada → nada
        Assert.Equal("", MpvAfChain.Build(new AudioEqParams(
            EqEnabled: false, Bands: new[] { new EqBand(1000, -6.0, 1.0) })));
    }

    [Fact]
    public void GainOnly_MapsToVolumeFilter()
    {
        Assert.Equal("lavfi=[volume=3.5dB]",
            MpvAfChain.Build(new AudioEqParams(GainDb: 3.5)));
    }

    [Fact]
    public void FullChain_KeepsEqChainSemantics_AndOrder()
    {
        var eq = new AudioEqParams(
            GainDb: -2.0,
            EqEnabled: true,
            HpfOn: true, HpfFreq: 80.0,
            LpfOn: true, LpfFreq: 12000.0,
            Bands: new[]
            {
                new EqBand(250, -4.5, 1.0),
                new EqBand(4000, 0.0, 2.0),    // ganancia 0 → se omite (como EqChain)
                new EqBand(8000, 2.0, 0.7),
            },
            Comp: new AudioCompParams(Enabled: true, ThresholdDb: -18.0,
                Ratio: 3.0, AttackMs: 10.0, ReleaseMs: 120.0, MakeupDb: 6.0));

        string af = MpvAfChain.Build(eq);
        Assert.StartsWith("lavfi=[volume=-2dB,highpass=f=80,lowpass=f=12000,", af);
        Assert.Contains("equalizer=f=250:t=q:w=1:g=-4.5", af);
        Assert.DoesNotContain("f=4000", af);
        Assert.Contains("equalizer=f=8000:t=q:w=0.7:g=2", af);
        // Umbral y makeup en LINEAL (−18 dB ≈ 0.125893; +6 dB ≈ 1.9953)
        Assert.Contains("acompressor=threshold=0.125893:ratio=3:attack=10:release=120:makeup=1.9953", af);
        Assert.EndsWith("]", af);
    }

    [Fact]
    public void HpfLpf_ApplyEvenWithEqDisabled_LikeEqChain()
    {
        string af = MpvAfChain.Build(new AudioEqParams(
            EqEnabled: false, HpfOn: true, HpfFreq: 100.0,
            Bands: new[] { new EqBand(1000, -6.0, 1.0) }));
        Assert.Equal("lavfi=[highpass=f=100]", af);
    }

    [Fact]
    public void DecimalFormat_IsInvariant_UnderSpanishCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("es-ES");
            string af = MpvAfChain.Build(new AudioEqParams(GainDb: 3.5,
                EqEnabled: true, Bands: new[] { new EqBand(1250.5, -4.25, 1.41) }));
            Assert.DoesNotContain("3,5", af);     // coma decimal = mpv no parsea
            Assert.DoesNotContain("4,25", af);
            Assert.Contains("volume=3.5dB", af);
            Assert.Contains("equalizer=f=1250.5:t=q:w=1.41:g=-4.25", af);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
