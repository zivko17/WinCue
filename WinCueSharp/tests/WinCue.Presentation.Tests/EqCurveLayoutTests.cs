// =============================================================================
// EqCurveLayoutTests.cs — red del cálculo del editor de EQ (traslado a Win2D).
// =============================================================================
// LA TRAMPA DE ESTE CONTROL (ver el encargo): el eje de frecuencias es
// LOGARÍTMICO. Si el mapeo se extrae mal, la curva se ve parecida pero está
// mintiendo. Por eso el primer bloque fija los extremos del rango, las tres
// décadas (100/1k/10k, que tienen que caer a la MISMA distancia entre sí) y
// un valor intermedio — antes de tocar nada más del control.
// =============================================================================

using WinCue.Core;
using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class EqCurveLayoutTests
{
    // ═════════════════════════ Eje de frecuencias (LOGARÍTMICO) ═════════════

    [Fact]
    public void XFromFreq_ExtremosCaenEnLosBordesDelLienzo()
    {
        Assert.Equal(0.0, EqCurveLayout.XFromFreq(20.0, 300), precision: 6);
        Assert.Equal(300.0, EqCurveLayout.XFromFreq(20000.0, 300), precision: 6);
    }

    [Fact]
    public void XFromFreq_LasTresDecadasCaenALaMismaDistanciaEntreSi()
    {
        // 20 Hz .. 20 kHz son EXACTAMENTE 3 décadas (×1000 = 10³). 100 Hz,
        // 1 kHz y 10 kHz tienen que quedar equiespaciadas: si el mapeo no
        // fuera logarítmico, o lo fuera con la base o el rango equivocados,
        // este reparto no saldría igual.
        double width = 300;
        double x100 = EqCurveLayout.XFromFreq(100, width);
        double x1k = EqCurveLayout.XFromFreq(1000, width);
        double x10k = EqCurveLayout.XFromFreq(10000, width);

        double gap1 = x1k - x100;
        double gap2 = x10k - x1k;
        Assert.Equal(gap1, gap2, precision: 6);
        Assert.Equal(100.0, gap1, precision: 3);   // 300 px / 3 décadas
    }

    [Fact]
    public void XFromFreq_PuntoIntermedioConocido()
    {
        // 200 Hz = 20 Hz × 10¹ → exactamente 1/3 del lienzo.
        Assert.Equal(100.0, EqCurveLayout.XFromFreq(200.0, 300), precision: 6);
        // 2000 Hz = 20 Hz × 10² → exactamente 2/3.
        Assert.Equal(200.0, EqCurveLayout.XFromFreq(2000.0, 300), precision: 6);
    }

    [Fact]
    public void XFromFreq_AcotaFueraDeRango()
    {
        Assert.Equal(0.0, EqCurveLayout.XFromFreq(1.0, 300), precision: 6);
        Assert.Equal(300.0, EqCurveLayout.XFromFreq(50000.0, 300), precision: 6);
    }

    [Theory]
    [InlineData(20.0)]
    [InlineData(100.0)]
    [InlineData(1000.0)]
    [InlineData(10000.0)]
    [InlineData(20000.0)]
    [InlineData(437.5)]
    public void FreqFromX_EsInversaExactaDeXFromFreq(double freq)
    {
        double width = 340;
        double x = EqCurveLayout.XFromFreq(freq, width);
        double back = EqCurveLayout.FreqFromX(x, width);

        Assert.Equal(freq, back, precision: 3);
    }

    [Fact]
    public void FreqFromX_ExtremosDelLienzo()
    {
        Assert.Equal(20.0, EqCurveLayout.FreqFromX(0, 300), precision: 3);
        Assert.Equal(20000.0, EqCurveLayout.FreqFromX(300, 300), precision: 1);
    }

    // ═════════════════════════ Eje de dB (lineal, invertido) ════════════════

    [Fact]
    public void YFromDb_CeroCaeEnElCentro()
    {
        Assert.Equal(100.0, EqCurveLayout.YFromDb(0, 200), precision: 6);
    }

    [Fact]
    public void YFromDb_PositivoSubeYNegativoBaja()
    {
        double height = 200;
        double yPos = EqCurveLayout.YFromDb(18.0, height);
        double yNeg = EqCurveLayout.YFromDb(-18.0, height);

        Assert.True(yPos < 100);    // sube en pantalla = Y menor
        Assert.True(yNeg > 100);
        // Simétrico respecto al centro (mismo margen de 6 px a cada lado).
        Assert.Equal(100.0 - yPos, yNeg - 100.0, precision: 6);
    }

    [Theory]
    [InlineData(-18.0)]
    [InlineData(-6.0)]
    [InlineData(0.0)]
    [InlineData(6.0)]
    [InlineData(18.0)]
    public void DbFromY_EsInversaExactaDeYFromDb(double db)
    {
        double height = 220;
        double y = EqCurveLayout.YFromDb(db, height);
        double back = EqCurveLayout.DbFromY(y, height);

        Assert.Equal(db, back, precision: 6);
    }

    // ═════════════════════════ Curva de respuesta (muestreo) ════════════════

    [Fact]
    public void ResponseCurve_CubreTodoElAnchoConElPasoDeclarado()
    {
        var eq = new WinCue.Core.AudioEqParams();
        var pts = EqCurveLayout.ResponseCurve(eq, 300, 200);

        Assert.Equal(0, pts[0].X, precision: 6);
        Assert.True(pts[^1].X <= 300);
        // Paso de 3 px entre puntos consecutivos (salvo el último, recortado).
        for (int i = 1; i < pts.Count - 1; i++)
            Assert.Equal(EqCurveLayout.CurveStepPx, pts[i].X - pts[i - 1].X, precision: 6);
    }

    [Fact]
    public void ResponseCurve_PlanaSinNadaActivo()
    {
        var eq = new WinCue.Core.AudioEqParams();
        var pts = EqCurveLayout.ResponseCurve(eq, 300, 200);

        // Sin filtros ni bandas, 0 dB en toda la curva → Y = YFromDb(0, 200).
        double expectedY = EqCurveLayout.YFromDb(0, 200);
        foreach (var p in pts)
            Assert.Equal(expectedY, p.Y, precision: 3);
    }

    // ═════════════════════════ RTA: balística ════════════════════════════════

    [Fact]
    public void RtaStep_PrimerEspectroClonaEnLasDosTrazas()
    {
        float[] spectrum = { -20f, -10f, -30f };

        var (smooth, peak) = EqCurveLayout.RtaStep(null, null, spectrum);

        Assert.Equal(spectrum, smooth);
        Assert.Equal(spectrum, peak);
    }

    [Fact]
    public void RtaStep_CambioDeLongitudReinicia()
    {
        float[] prev = { -20f, -10f };
        float[] spectrum = { -20f, -10f, -30f };

        var (smooth, peak) = EqCurveLayout.RtaStep(prev, prev, spectrum);

        Assert.Equal(spectrum, smooth);
        Assert.Equal(spectrum, peak);
    }

    [Fact]
    public void RtaStep_AtaqueEsInstantaneo()
    {
        float[] prev = { -40f };
        float[] spectrum = { -10f };   // sube: la traza debe seguirlo YA

        var (smooth, _) = EqCurveLayout.RtaStep(prev, prev, spectrum);

        Assert.Equal(-10f, smooth[0]);
    }

    [Fact]
    public void RtaStep_ReleaseNoBajaMasDeLoPermitidoPorFotograma()
    {
        float[] prev = { 0f };
        float[] spectrum = { -90f };   // caída brusca

        var (smooth, _) = EqCurveLayout.RtaStep(prev, prev, spectrum);

        Assert.Equal(0f - EqCurveLayout.RtaReleasePerFrameDb, smooth[0], 3);
    }

    [Fact]
    public void RtaStep_PicoSigueAlValorMasAltoYCaeDespacio()
    {
        float[] prevSmooth = { -40f };
        float[] prevPeak = { -5f };    // pico alto sostenido
        float[] spectrum = { -40f };   // la señal actual ya bajó

        var (_, peak) = EqCurveLayout.RtaStep(prevSmooth, prevPeak, spectrum);

        // El pico no puede subir de golpe a -40 dB: solo cae 0.65 dB/fotograma.
        Assert.Equal(-5f - EqCurveLayout.RtaPeakDecayPerFrameDb, peak[0], 3);
    }

    // ═════════════════════════ Manejadores: hit-test y arrastre ═════════════

    [Fact]
    public void HitHandle_AciertaSobreUnaBanda()
    {
        double width = 300, height = 200;
        var bands = new (double Freq, double Gain)[] { (1000, 6.0), (200, -3.0) };
        var p = EqCurveLayout.BandPoint(1000, 6.0, width, height);

        int hit = EqCurveLayout.HitHandle(p, bands, hpfFreq: 80, lpfFreq: 12000, width, height);

        Assert.Equal(0, hit);
    }

    [Fact]
    public void HitHandle_AciertaSobreHpfYLpf()
    {
        double width = 300, height = 200;
        var bands = Array.Empty<(double, double)>();

        var hpfPoint = EqCurveLayout.FilterPoint(80, width, height);
        var lpfPoint = EqCurveLayout.FilterPoint(12000, width, height);

        Assert.Equal(EqHandle.Hpf, EqCurveLayout.HitHandle(hpfPoint, bands, 80, 12000, width, height));
        Assert.Equal(EqHandle.Lpf, EqCurveLayout.HitHandle(lpfPoint, bands, 80, 12000, width, height));
    }

    [Fact]
    public void HitHandle_FallaLejosDeCualquierManejador()
    {
        double width = 300, height = 200;
        var bands = new (double Freq, double Gain)[] { (1000, 0.0) };

        int hit = EqCurveLayout.HitHandle(new GeoPoint(5, 5), bands, 80, 12000, width, height);

        Assert.Equal(EqHandle.None, hit);
    }

    [Fact]
    public void ClampHpfFreq_AcotaAlRangoDeLaCajaNumerica()
    {
        Assert.Equal(20.0, EqCurveLayout.ClampHpfFreq(5.0));
        Assert.Equal(2000.0, EqCurveLayout.ClampHpfFreq(9000.0));
        Assert.Equal(150.0, EqCurveLayout.ClampHpfFreq(150.0));
    }

    [Fact]
    public void ClampLpfFreq_AcotaAlRangoDeLaCajaNumerica()
    {
        Assert.Equal(200.0, EqCurveLayout.ClampLpfFreq(5.0));
        Assert.Equal(20000.0, EqCurveLayout.ClampLpfFreq(50000.0));
    }

    [Fact]
    public void QFromWheel_SubeYBajaEnFactorConstanteYAcota()
    {
        double up = EqCurveLayout.QFromWheel(1.0, wheelUp: true);
        double down = EqCurveLayout.QFromWheel(1.0, wheelUp: false);

        Assert.Equal(1.12, up, 2);
        Assert.Equal(1.0 / 1.12, down, 2);
        Assert.Equal(12.0, EqCurveLayout.QFromWheel(12.0, wheelUp: true));
        Assert.Equal(0.1, EqCurveLayout.QFromWheel(0.1, wheelUp: false));
    }

    [Fact]
    public void GainFromDragY_AcotaAlRangoDeGananciaYRedondea()
    {
        double height = 200;
        Assert.Equal(18.0, EqCurveLayout.GainFromDragY(-1000, height));
        Assert.Equal(-18.0, EqCurveLayout.GainFromDragY(1000, height));
        Assert.Equal(0.0, EqCurveLayout.GainFromDragY(EqCurveLayout.YFromDb(0, height), height));
    }

    // ═════════════════════════ EqParamsFromCue ═══════════════════════════════

    [Fact]
    public void EqParamsFromCue_IgnoraLaGananciaDeClip()
    {
        // La curva de este lienzo es solo de FILTROS: la ganancia tiene su
        // propio fader al lado y no debe colarse en ResponseDb.
        var cue = new Cue { GainDb = 12.0, EqEnabled = true };

        var eq = EqCurveLayout.EqParamsFromCue(cue);

        Assert.Equal(0.0, eq.GainDb);
    }

    [Fact]
    public void EqParamsFromCue_CopiaLasCuatroBandasEnOrden()
    {
        var cue = new Cue
        {
            Eq1Freq = 100, Eq1Gain = 1, Eq1Q = 0.5,
            Eq2Freq = 200, Eq2Gain = 2, Eq2Q = 1.0,
            Eq3Freq = 300, Eq3Gain = 3, Eq3Q = 1.5,
            Eq4Freq = 400, Eq4Gain = 4, Eq4Q = 2.0,
        };

        var eq = EqCurveLayout.EqParamsFromCue(cue);

        Assert.Equal(4, eq.Bands!.Count);
        Assert.Equal((100.0, 1.0, 0.5), (eq.Bands[0].Freq, eq.Bands[0].GainDb, eq.Bands[0].Q));
        Assert.Equal((400.0, 4.0, 2.0), (eq.Bands[3].Freq, eq.Bands[3].GainDb, eq.Bands[3].Q));
    }

    // ═════════════════════════ RTA: puntos de área y picos ═══════════════════

    [Fact]
    public void RtaAreaPoints_EmpiezaYTerminaEnLaBaseDelLienzo()
    {
        float[] smooth = { -20f, -10f, -30f };
        var pts = EqCurveLayout.RtaAreaPoints(smooth, gainDb: 0, width: 100, height: 200);

        Assert.Equal(201.0, pts[0].Y, precision: 6);     // height + 1
        Assert.Equal(201.0, pts[^1].Y, precision: 6);
        Assert.Equal(0.0, pts[0].X, precision: 6);
        Assert.Equal(100.0, pts[^1].X, precision: 6);
    }

    [Fact]
    public void RtaAreaPoints_VacioConMenosDeDosBandas()
    {
        Assert.Empty(EqCurveLayout.RtaAreaPoints(new float[] { -20f }, 0, 100, 200));
        Assert.Empty(EqCurveLayout.RtaAreaPoints(Array.Empty<float>(), 0, 100, 200));
    }

    [Fact]
    public void RtaPeakSegments_OmiteLosQueCaenPegadosAlSuelo()
    {
        // Un pico en el suelo (RtaFloorDb) cae en y = height, por encima del
        // recorte (height - 1) → se omite. Uno alto sí aparece.
        float[] peak = { (float)EqCurveLayout.RtaFloorDb, 0f };

        var segs = EqCurveLayout.RtaPeakSegments(peak, gainDb: 0, width: 100, height: 200);

        Assert.Single(segs);
    }
}
