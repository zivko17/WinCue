// =============================================================================
// UiMetricsTests.cs — red del traslado de UiMetrics a la capa compartida.
// =============================================================================
// No prueban lógica: no la hay. Fijan los VALORES que la interfaz de WinCue
// tiene hoy, para que el traslado no cambie ni un píxel. Si alguien afina una
// medida más adelante, tendrá que tocar aquí a propósito — que es el punto.
// =============================================================================

using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class UiMetricsTests
{
    [Fact]
    public void LosFadersMidenLoMismoQueAntesDelTraslado()
    {
        Assert.Equal(26, UiMetrics.FaderWidth);
        Assert.Equal(170, UiMetrics.FaderHeight);
        Assert.Equal(30, UiMetrics.FaderScaleWidth);
    }

    [Fact]
    public void LasMedidasFijasSiguenSiendoLasDeLaAuditoriaDeInterfaz()
    {
        Assert.Equal(72, UiMetrics.TrimBarHeight);
        Assert.Equal(18, UiMetrics.StripTitleLineHeight);
        Assert.Equal(270, UiMetrics.EqCanvasHeight);
        Assert.Equal(16, UiMetrics.StripExtrasHeight);
    }

    [Fact]
    public void LaLecturaEnDbConservaSuAnchoFijo()
    {
        // Fijos a propósito: el texto cambia de ancho al usarlo («+0,0 dB» →
        // «+12,0 dB») y sin ancho reservado se meneaba media columna.
        Assert.Equal(74, UiMetrics.DbReadoutWidth);
        Assert.Equal(124, UiMetrics.DbReadoutWideWidth);
    }

    // Red de regresión del traslado: estas siete quedaron sin fijar en la
    // tarea 5 (revisión posterior) y son las mismas medidas de los strips y
    // botones MUTE/SOLO que ya vivían en la interfaz de WPF.
    [Fact]
    public void LosStripsYSusBotonesMidenLoMismoQueAntesDelTraslado()
    {
        Assert.Equal(104, UiMetrics.WideMuteWidth);
        Assert.Equal(26, UiMetrics.WideMuteHeight);
        Assert.Equal(54, UiMetrics.StripButtonWidth);
        Assert.Equal(24, UiMetrics.StripButtonHeight);
        Assert.Equal(140, UiMetrics.StripWidth);
        Assert.Equal(34, UiMetrics.StripCollapsedWidth);
        Assert.Equal(292, UiMetrics.StripHeight);
    }
}
