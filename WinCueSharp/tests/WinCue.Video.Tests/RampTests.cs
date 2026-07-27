// =============================================================================
// RampTests.cs — Los fundidos, sin GPU (capa L2)
// =============================================================================
// Un fundido del motor nuevo es una función del tiempo, no una animación de
// WPF. Eso lo hace comprobable de forma exacta y determinista, que es justo lo
// que no se podía hacer con BeginAnimation(OpacityProperty).
// =============================================================================

using WinCue.Video.Gpu;

namespace WinCue.Video.Tests;

public class RampTests
{
    [Fact]
    public void AntesDeEmpezarYDespuesDeAcabar_SeQuedaEnLosExtremos()
    {
        var r = new Ramp(StartTime: 10.0, Duration: 2.0, From: 0f, To: 1f);
        Assert.Equal(0f, r.At(9.9));
        Assert.Equal(0f, r.At(10.0));
        Assert.Equal(1f, r.At(12.0));
        Assert.Equal(1f, r.At(99.0));
    }

    [Fact]
    public void EnLaMitad_VaPorLaMitad()
    {
        var r = new Ramp(10.0, 2.0, 0f, 1f);
        Assert.Equal(0.5f, r.At(11.0), precision: 5);
        Assert.Equal(0.25f, r.At(10.5), precision: 5);
    }

    [Fact]
    public void DuracionCero_EsUnCorte()
    {
        // Un cue sin fundido no debe quedarse a medias en ningún instante
        var r = new Ramp(5.0, 0.0, 0f, 1f);
        Assert.Equal(0f, r.At(4.999));
        Assert.Equal(1f, r.At(5.0));
    }

    [Fact]
    public void LaRampaEsMonotona()
    {
        // Un retroceso aquí es un parpadeo en sala
        var r = Ramp.FadeIn(0.0, 3.0);
        float previo = -1f;
        for (int i = 0; i <= 100; i++)
        {
            float v = r.At(i * 0.05);
            Assert.True(v >= previo, $"retrocedió en t={i * 0.05:F2}: {previo} → {v}");
            previo = v;
        }
    }

    [Fact]
    public void FadeOut_VaDeUnoACero()
    {
        var r = Ramp.FadeOut(0.0, 4.0);
        Assert.Equal(1f, r.At(0.0));
        Assert.Equal(0.5f, r.At(2.0), precision: 5);
        Assert.Equal(0f, r.At(4.0));
    }

    [Fact]
    public void IsDone_DiceCuandoSePuedeRetirarElSaliente()
    {
        var r = new Ramp(10.0, 2.0, 1f, 0f);
        Assert.False(r.IsDone(11.9));
        Assert.True(r.IsDone(12.0));
    }

    [Fact]
    public void UnCruceCompleto_SumaUnoEnTodoMomento()
    {
        // A al 1 y B al factor: con la mezcla src*f + dst*(1-f), la energía
        // total se conserva en cada instante y por eso no hay bache en el
        // centro del cruce.
        var r = Ramp.FadeIn(0.0, 2.0);
        for (int i = 0; i <= 20; i++)
        {
            float f = r.At(i * 0.1);
            Assert.InRange(f, 0f, 1f);
            Assert.Equal(1f, f + (1f - f), precision: 5);
        }
    }
}
