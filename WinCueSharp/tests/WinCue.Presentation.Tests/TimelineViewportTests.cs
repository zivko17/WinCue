// =============================================================================
// TimelineViewportTests.cs — zoom y scroll del timeline (hitos-0-1, timeline
// interactivo).
// =============================================================================
// Fijan los números que TimelineCanvas (WinCue.App) tiene HOY: factor 1.25
// por muesca de Ctrl+rueda, 0.6 px por delta de rueda, la holgura de +10 s al
// final del scroll, el 30 %/95 % del ancho del auto-desplazamiento… El zoom
// anclado tiene además una propiedad que NO puede romperse: el instante bajo
// el puntero se queda bajo el puntero.
// =============================================================================

using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class TimelineViewportTests
{
    [Fact]
    public void ArrancaEnElZoomDeSiempre()
    {
        var vp = new TimelineViewport();
        Assert.Equal(20, vp.PxPerSec);
        Assert.Equal(0, vp.ScrollX);
    }

    [Fact]
    public void ZoomAncladoDejaElInstanteBajoElPuntero()
    {
        var vp = new TimelineViewport();
        vp.ScrollByWheel(-1000, totalSeconds: 600, width: 800);   // scroll a media pista
        double anchorX = 350;
        double secAntes = vp.XToSec(anchorX);
        vp.ZoomAt(1.25, anchorX);
        Assert.Equal(secAntes, vp.XToSec(anchorX), 9);
        vp.ZoomAt(1 / 1.25, anchorX);
        Assert.Equal(secAntes, vp.XToSec(anchorX), 6);
    }

    [Fact]
    public void ZoomSeQuedaEntreLosTopes()
    {
        var vp = new TimelineViewport();
        for (int i = 0; i < 100; i++)
            vp.ZoomAt(2.0, 0);
        Assert.Equal(TimelineLayout.MaxPxPerSec, vp.PxPerSec);
        for (int i = 0; i < 100; i++)
            vp.ZoomAt(0.5, 0);
        Assert.Equal(TimelineLayout.MinPxPerSec, vp.PxPerSec);
    }

    [Fact]
    public void ZoomNoDejaElScrollNegativo()
    {
        var vp = new TimelineViewport();
        // Alejar con el ancla a la derecha empujaría el scroll bajo cero
        vp.ZoomAt(0.5, anchorX: 700);
        Assert.True(vp.ScrollX >= 0);
    }

    [Fact]
    public void FitMeteElShowEnteroConHolgura()
    {
        var vp = new TimelineViewport();
        vp.Fit(totalSeconds: 98, width: 1020);
        // (1020 - 20) px / (98 + 2) s = 10 px/s, y al principio
        Assert.Equal(10, vp.PxPerSec, 9);
        Assert.Equal(0, vp.ScrollX);
    }

    [Fact]
    public void FitDeUnShowVacioNoRevienta()
    {
        var vp = new TimelineViewport();
        vp.Fit(totalSeconds: 0, width: 1020);
        // Mínimo 10 s de horizonte: (1020-20)/10 = 100 px/s
        Assert.Equal(100, vp.PxPerSec, 9);
    }

    [Fact]
    public void ZoomToFitSinMedirUsaElAnchoDeReserva()
    {
        var vp = new TimelineViewport();
        vp.ZoomToFit(totalSeconds: 98, actualWidth: 0);
        // Reserva 900: (900-20)/100 = 8.8 px/s
        Assert.Equal(8.8, vp.PxPerSec, 9);
    }

    [Fact]
    public void RuedaSinCtrlDesplazaYRespetaLosTopes()
    {
        var vp = new TimelineViewport();   // 20 px/s
        // Hacia el final: delta negativo → scroll crece 0.6 px por unidad
        vp.Wheel(-120, ctrl: false, anchorX: 0, totalSeconds: 600, width: 800);
        Assert.Equal(72, vp.ScrollX, 9);
        // Hacia el principio, de sobra: se queda en 0
        vp.Wheel(99999, ctrl: false, anchorX: 0, totalSeconds: 600, width: 800);
        Assert.Equal(0, vp.ScrollX);
        // Tope al final: (600+10)*20 - 800
        vp.Wheel(-999999, ctrl: false, anchorX: 0, totalSeconds: 600, width: 800);
        Assert.Equal(610 * 20 - 800, vp.ScrollX, 9);
    }

    [Fact]
    public void RuedaConCtrlHaceZoomPorMuescas()
    {
        var vp = new TimelineViewport();
        vp.Wheel(120, ctrl: true, anchorX: 0, totalSeconds: 600, width: 800);
        Assert.Equal(25, vp.PxPerSec, 9);   // 20 × 1.25
        vp.Wheel(-120, ctrl: true, anchorX: 0, totalSeconds: 600, width: 800);
        Assert.Equal(20, vp.PxPerSec, 9);
    }

    [Fact]
    public void EnsureVisibleNoMueveLoQueYaSeVe()
    {
        var vp = new TimelineViewport();   // 20 px/s, scroll 0
        Assert.False(vp.EnsureVisible(startSec: 10, width: 800));   // x = 200
        Assert.Equal(0, vp.ScrollX);
    }

    [Fact]
    public void EnsureVisibleTraeLoQueQuedoFuera()
    {
        var vp = new TimelineViewport();   // 20 px/s
        // 100 s → x = 2000, fuera de un ancho de 800: al 30 % del borde
        Assert.True(vp.EnsureVisible(startSec: 100, width: 800));
        Assert.Equal(100 * 20 - 800 * 0.3, vp.ScrollX, 9);
        // Y ahora un instante que quedó a la IZQUIERDA del scroll
        Assert.True(vp.EnsureVisible(startSec: 0, width: 800));
        Assert.Equal(0, vp.ScrollX);
    }

    [Fact]
    public void EnsureVisibleSinAnchoNoHaceNada()
    {
        var vp = new TimelineViewport();
        Assert.False(vp.EnsureVisible(startSec: 100, width: 0));
        Assert.Equal(0, vp.ScrollX);
    }
}
