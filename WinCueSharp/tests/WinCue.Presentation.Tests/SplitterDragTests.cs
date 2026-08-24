using WinCue.Presentation;
using Xunit;

namespace WinCue.Presentation.Tests;

public class SplitterDragTests
{
    // Los números son los de WinCue (MainWindow.xaml): el panel de pestañas
    // arranca en 330 con MinHeight 140, la lista de cues tiene MinHeight 120,
    // y el separador mide 5.
    const double Min = 140, Max = 900, NeighbourMin = 120, Splitter = 5;

    static double Resize(double current, double delta, double available) =>
        SplitterDrag.Resize(current, delta, Min, Max, available, NeighbourMin, Splitter);

    [Fact]
    public void ArrastrarHaciaSuLado_CreceLoQueCorreElPuntero()
    {
        Assert.Equal(400, Resize(330, 70, 800));
    }

    [Fact]
    public void ArrastrarAlReves_Encoge()
    {
        Assert.Equal(280, Resize(330, -50, 800));
    }

    [Fact]
    public void NoBajaDeSuPropioMinimo()
    {
        Assert.Equal(Min, Resize(330, -1000, 800));
    }

    [Fact]
    public void NoPasaDeSuPropioMaximo()
    {
        // Hueco de sobra: el que para es el máximo, no el vecino.
        Assert.Equal(Max, Resize(330, 5000, 4000));
    }

    [Fact]
    public void DejaSiempreElMinimoDelVecino()
    {
        // 800 de hueco − 120 del vecino − 5 del separador = 675.
        Assert.Equal(675, Resize(330, 5000, 800));
    }

    [Fact]
    public void ConLaVentanaMuyBaja_GanaElMinimoPropio()
    {
        // 200 de hueco no dan para los dos mínimos (140 + 120 + 5 = 265).
        // El panel se queda en el suyo en vez de devolver algo ilegible.
        Assert.Equal(Min, Resize(330, 0, 200));
        Assert.Equal(Min, Resize(330, 500, 200));
    }

    [Fact]
    public void SinMaximo_ElVecinoSigueMandando()
    {
        double v = SplitterDrag.Resize(455, 5000, 200, double.PositiveInfinity,
                                       1920, 400, 5);
        Assert.Equal(1515, v);   // 1920 − 400 − 5
    }

    [Fact]
    public void QuedarseQuieto_NoCambiaNada()
    {
        Assert.Equal(330, Resize(330, 0, 800));
    }
}
