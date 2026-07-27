// =============================================================================
// FrameCadenceTests.cs — La regla de sincronía del motor nuevo (capa L2)
// =============================================================================
// Estas pruebas son el criterio de salida de F1 en lo que a tiempo se refiere,
// y no necesitan GPU: la regla es pura. Si esto está en verde, la sincronía
// entre fuentes de distinto framerate y la salida es correcta POR
// CONSTRUCCIÓN, no por ajuste fino.
//
// La regla: el último fotograma cuyo PTS no supera el instante pedido.
// De ella salen solas las cadencias del sector — 2:2 para 30p en 60 Hz y el
// pulldown 3:2 para 24p en 60 Hz.
// =============================================================================

using WinCue.Video.Gpu;

namespace WinCue.Video.Tests;

public class FrameCadenceTests
{
    /// <summary>Longitudes de racha: cuántos instantes seguidos de salida
    /// muestran el MISMO fotograma de origen.</summary>
    private static List<int> RunLengths(double sourceFps, double outputHz, int outputTicks)
    {
        // Ventana amplia de fotogramas de origen: aquí se prueba la REGLA, no
        // el anillo (eso se prueba con FrameRing sobre GPU).
        int frames = (int)Math.Ceiling(outputTicks / outputHz * sourceFps) + 4;
        double[] pts = new double[frames];
        for (int n = 0; n < frames; n++)
            pts[n] = n / sourceFps;

        var runs = new List<int>();
        int previous = -1, run = 0;
        for (int k = 0; k < outputTicks; k++)
        {
            int pick = FrameCadence.SelectLatestNotAfter(pts, k / outputHz);
            Assert.True(pick >= 0, $"tick {k}: no se eligió fotograma");
            if (pick == previous)
            {
                run++;
            }
            else
            {
                if (previous >= 0)
                    runs.Add(run);
                // Un fotograma nunca se salta: la selección avanza de uno en uno
                if (previous >= 0)
                    Assert.Equal(previous + 1, pick);
                previous = pick;
                run = 1;
            }
        }
        return runs;
    }

    [Fact]
    public void TreintaPEnSesentaHz_EsDosADos()
    {
        List<int> runs = RunLengths(sourceFps: 30, outputHz: 60, outputTicks: 60);
        Assert.NotEmpty(runs);
        Assert.All(runs, r => Assert.Equal(2, r));
    }

    [Fact]
    public void SesentaPEnSesentaHz_EsUnoAUno()
    {
        List<int> runs = RunLengths(sourceFps: 60, outputHz: 60, outputTicks: 60);
        Assert.NotEmpty(runs);
        Assert.All(runs, r => Assert.Equal(1, r));
    }

    [Fact]
    public void VeinticuatroPEnSesentaHz_EsElPulldownTresADos()
    {
        List<int> runs = RunLengths(sourceFps: 24, outputHz: 60, outputTicks: 120);
        Assert.NotEmpty(runs);

        // Alterna 3 y 2, empezando por 3 (media 2,5 = 60/24)
        for (int i = 0; i < runs.Count; i++)
            Assert.Equal(i % 2 == 0 ? 3 : 2, runs[i]);
        Assert.Equal(2.5, runs.Average(), precision: 1);
    }

    [Fact]
    public void AntesDelPrimerFotograma_NoHayNadaQueEnsenar()
    {
        double[] pts = { 1.0, 1.5, 2.0 };
        Assert.Equal(-1, FrameCadence.SelectLatestNotAfter(pts, 0.5));
    }

    [Fact]
    public void SlotEnEscritura_SeSalta()
    {
        // NaN = el productor está escribiendo ese slot: es invisible
        double[] pts = { 1.0, double.NaN, 2.0 };
        Assert.Equal(2, FrameCadence.SelectLatestNotAfter(pts, 5.0));

        double[] soloEnEscritura = { double.NaN, double.NaN };
        Assert.Equal(-1, FrameCadence.SelectLatestNotAfter(soloEnEscritura, 5.0));
    }

    [Fact]
    public void AnilloDesordenado_EligeElUltimoValido()
    {
        // Un anillo circular NO está ordenado: el slot 0 puede ser el más nuevo
        double[] pts = { 3.0, 1.0, 2.0 };
        Assert.Equal(0, FrameCadence.SelectLatestNotAfter(pts, 3.5));
        Assert.Equal(2, FrameCadence.SelectLatestNotAfter(pts, 2.5));
        Assert.Equal(1, FrameCadence.SelectLatestNotAfter(pts, 1.5));
    }

    [Fact]
    public void InstanteExactoDeUnFotograma_LoEnsenaYa()
    {
        // El límite es "no posterior", no "anterior": en su hora exacta, entra
        double[] pts = { 0.0, 0.5, 1.0 };
        Assert.Equal(1, FrameCadence.SelectLatestNotAfter(pts, 0.5));
    }
}
