using WinCue.Controller;
using Xunit;

namespace WinCue.Controller.Tests;

public class LoudnessGainTests
{
    [Fact]
    public void UnClipMasFlojoQueElObjetivo_SUBE()
    {
        // −23 LUFS es material bastante más flojo que el objetivo: +7 dB.
        Assert.Equal(7.0, MediaImporter.GainForTarget(-23.0), 3);
    }

    [Fact]
    public void UnClipMasFuerteQueElObjetivo_BAJA()
    {
        Assert.Equal(-4.0, MediaImporter.GainForTarget(-12.0), 3);
    }

    [Fact]
    public void UnClipYaEnElObjetivo_NoSeToca()
    {
        Assert.Equal(0.0, MediaImporter.GainForTarget(MediaImporter.TargetLufs), 3);
    }

    [Fact]
    public void SeRedondeaAUnDecimal()
    {
        // El campo de ganancia del inspector no muestra más.
        Assert.Equal(2.3, MediaImporter.GainForTarget(-18.34), 3);
        Assert.Equal(-1.4, MediaImporter.GainForTarget(-14.62), 3);
    }

    [Theory]
    // Un clip grabado a nivel ínfimo pediría +40 dB: se queda en el tope.
    [InlineData(-60.0, 12.0)]
    // Y uno saturadísimo pediría −30: se queda en el suelo.
    [InlineData(14.0, -24.0)]
    public void SeAcotaAlRangoDelCampoDeGanancia(double lufs, double esperado)
    {
        // [−24, +12] es EXACTAMENTE el rango del campo del inspector:
        // proponer algo fuera seria escribirle al cue un valor que el propio
        // inspector rechazaria al reeditarlo.
        Assert.Equal(esperado, MediaImporter.GainForTarget(lufs), 3);
    }

    [Fact]
    public void ElObjetivoEsMenos16()
    {
        Assert.Equal(-16.0, MediaImporter.TargetLufs, 3);
    }
}
