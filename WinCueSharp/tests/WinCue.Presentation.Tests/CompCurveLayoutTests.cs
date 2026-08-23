// =============================================================================
// CompCurveLayoutTests.cs — red del cálculo de la gráfica del compresor
// (traslado a Win2D).
// =============================================================================

using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class CompCurveLayoutTests
{
    // ═════════════════════════ Ejes dB↔píxel ═════════════════════════════════

    [Fact]
    public void XFromDb_ExtremosCaenEnLosBordes()
    {
        double pw = 200;
        Assert.Equal(0.0, CompCurveLayout.XFromDb(-60.0, pw), precision: 6);
        Assert.Equal(200.0, CompCurveLayout.XFromDb(0.0, pw), precision: 6);
    }

    [Theory]
    [InlineData(-60.0)]
    [InlineData(-30.0)]
    [InlineData(-6.0)]
    [InlineData(0.0)]
    public void DbFromX_EsInversaExactaDeXFromDb(double db)
    {
        double pw = 240;
        double x = CompCurveLayout.XFromDb(db, pw);
        Assert.Equal(db, CompCurveLayout.DbFromX(x, pw), precision: 6);
    }

    [Fact]
    public void YFromDb_CeroEnElOrigenYMinAlFondo()
    {
        double h = 150;
        Assert.Equal(0.0, CompCurveLayout.YFromDb(0, h), precision: 6);
        Assert.Equal(h, CompCurveLayout.YFromDb(CompCurveLayout.DbMin, h), precision: 6);
    }

    [Fact]
    public void PlotWidth_RestaElMedidorGrYSuSeparacion()
    {
        double pw = CompCurveLayout.PlotWidth(220);
        Assert.Equal(220 - CompCurveLayout.MeterW - 4, pw, precision: 6);
    }

    [Fact]
    public void PlotWidth_NuncaBajaDeUno()
    {
        Assert.Equal(1.0, CompCurveLayout.PlotWidth(5));
    }

    // ═════════════════════════ Transferencia (umbral/ratio/makeup) ══════════

    [Fact]
    public void OutDb_PorDebajoDelUmbralEsUnoAUno()
    {
        Assert.Equal(-30.0, CompCurveLayout.OutDb(-30.0, thresholdDb: -20.0, ratio: 4.0, makeupDb: 0.0), 6);
    }

    [Fact]
    public void OutDb_EnElUmbralEsElPropioUmbral()
    {
        Assert.Equal(-20.0, CompCurveLayout.OutDb(-20.0, thresholdDb: -20.0, ratio: 4.0, makeupDb: 0.0), 6);
    }

    [Fact]
    public void OutDb_PorEncimaDivideElExcesoPorElRatio()
    {
        // Entrada 0 dB, umbral -20 dB, ratio 4:1 → exceso 20 dB / 4 = 5 dB de
        // salida por encima del umbral → -20 + 5 = -15 dB.
        Assert.Equal(-15.0, CompCurveLayout.OutDb(0.0, thresholdDb: -20.0, ratio: 4.0, makeupDb: 0.0), 6);
    }

    [Fact]
    public void OutDb_MakeupSeSumaSiempre()
    {
        Assert.Equal(-24.0, CompCurveLayout.OutDb(-30.0, thresholdDb: -20.0, ratio: 4.0, makeupDb: 6.0), 6);
        Assert.Equal(-9.0, CompCurveLayout.OutDb(0.0, thresholdDb: -20.0, ratio: 4.0, makeupDb: 6.0), 6);
    }

    [Fact]
    public void OutDb_RatioMenorQueUnoNoAmplificaElExceso()
    {
        // Math.Max(1.0, ratio): un ratio < 1 (dato corrupto o límite mal
        // puesto) no puede EXPANDIR el exceso — se trata como 1:1, así que el
        // exceso (10 dB) pasa entero: -20 + 10 = -10, nunca más que eso.
        Assert.Equal(-10.0, CompCurveLayout.OutDb(-10.0, thresholdDb: -20.0, ratio: 0.5, makeupDb: 0.0), 6);
    }

    [Fact]
    public void TransferCurve_CubreTodoElAnchoConPasoDeDosPixeles()
    {
        var pts = CompCurveLayout.TransferCurve(-20.0, 4.0, 0.0, plotWidth: 200, height: 150);

        Assert.Equal(0, pts[0].X, precision: 6);
        for (int i = 1; i < pts.Count - 1; i++)
            Assert.Equal(2.0, pts[i].X - pts[i - 1].X, precision: 6);
    }

    [Fact]
    public void TransferCurve_BypassEsLaDiagonal()
    {
        // Umbral a 0 dB: nada llega a comprimirse dentro del rango de
        // entrada (-60..0) → la curva es 1:1 (diagonal), Y creciente con X.
        var pts = CompCurveLayout.TransferCurve(0.0, 4.0, 0.0, plotWidth: 200, height: 150);

        Assert.True(pts[0].Y > pts[^1].Y);   // -60 dB (abajo) → 0 dB (arriba)
    }

    [Fact]
    public void ThresholdPoint_XCoincideConXFromDbDelUmbral()
    {
        double pw = 200, h = 150;
        var p = CompCurveLayout.ThresholdPoint(-20.0, 4.0, 0.0, pw, h);

        Assert.Equal(CompCurveLayout.XFromDb(-20.0, pw), p.X, precision: 6);
    }

    // ═════════════════════════ Interacción ═══════════════════════════════════

    [Fact]
    public void ThresholdFromDragX_AcotaAlRangoDelCompresor()
    {
        double pw = 200;
        Assert.Equal(-60.0, CompCurveLayout.ThresholdFromDragX(-500, pw));
        Assert.Equal(0.0, CompCurveLayout.ThresholdFromDragX(9999, pw));
    }

    [Fact]
    public void RatioFromWheel_SubeYBajaEnFactorConstanteYAcota()
    {
        Assert.Equal(1.15, CompCurveLayout.RatioFromWheel(1.0, wheelUp: true), 2);
        Assert.Equal(5.0 / 1.15, CompCurveLayout.RatioFromWheel(5.0, wheelUp: false), 2);
        Assert.Equal(20.0, CompCurveLayout.RatioFromWheel(20.0, wheelUp: true));
        // Ya en el suelo (1:1): bajar más no lo cruza, se queda en el tope.
        Assert.Equal(1.0, CompCurveLayout.RatioFromWheel(1.0, wheelUp: false));
    }

    [Fact]
    public void GrFraction_AcotaEntreCeroYUno()
    {
        Assert.Equal(0.0, CompCurveLayout.GrFraction(-5.0));
        Assert.Equal(0.5, CompCurveLayout.GrFraction(12.0), 6);   // mitad de 24 dB
        Assert.Equal(1.0, CompCurveLayout.GrFraction(100.0));
    }
}
