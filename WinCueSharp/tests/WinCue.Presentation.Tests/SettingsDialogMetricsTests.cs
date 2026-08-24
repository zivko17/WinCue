// =============================================================================
// SettingsDialogMetricsTests.cs — el cuadro de Ajustes no baila y cabe en los
// dos monitores.
// =============================================================================
// Dos cosas que no se pueden comprobar desde este repo en Linux (Secuence es
// WinUI 3) y que sí se pueden fijar aquí:
//   1. El alto NO depende de la pestaña — es el mismo número siempre. Ese era
//      el fallo: el diálogo cambiaba de tamaño al cambiar de pestaña.
//   2. El diálogo COMPLETO (contenido + título + botones) cabe en la ventana,
//      incluida la del monitor de 1024x600 de Andrés.
// =============================================================================

using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class SettingsDialogMetricsTests
{
    [Fact]
    public void ElAltoNoDependeDeLaPestaña()
    {
        // No hay ningún parámetro por el que colar "qué pestaña": para una
        // ventana dada, el alto es UNO. Es la prueba del fallo original.
        const double ventana = 1040;
        double primera = SettingsDialogMetrics.ContentHeight(ventana);
        for (int i = 0; i < 5; i++)
            Assert.Equal(primera, SettingsDialogMetrics.ContentHeight(ventana));
    }

    [Theory]
    [InlineData(1040)]   // 1920x1080 maximizada
    [InlineData(570)]    // 1024x600 maximizada — el monitor pequeño de Andrés
    [InlineData(700)]
    [InlineData(900)]
    public void ElDialogoCompletoCabeEnLaVentana(double ventana)
    {
        double total = SettingsDialogMetrics.ContentHeight(ventana)
                     + SettingsDialogMetrics.ChromeHeight;
        Assert.True(total <= ventana,
            $"con una ventana de {ventana} px el diálogo pide {total} px");
    }

    [Fact]
    public void EnUnaPantallaGrandeSeQuedaEnElMaximoUtil()
    {
        // Ni un píxel más que la pestaña más alta: pasado ese punto solo
        // se añadiría hueco vacío en todas las demás.
        Assert.Equal(SettingsDialogMetrics.MaxContentHeight,
            SettingsDialogMetrics.ContentHeight(4000));
    }

    [Fact]
    public void EnUnaVentanaDiminutaNoBajaDelMinimoUsable()
    {
        // Aquí el diálogo YA no cabe entero (nada puede arreglar eso) y se
        // prefiere un cuadro usable con scroll a uno de dos filas.
        Assert.Equal(SettingsDialogMetrics.MinContentHeight,
            SettingsDialogMetrics.ContentHeight(320));
    }

    [Fact]
    public void SinMedirLaVentanaSeUsaElMaximo()
    {
        // XamlRoot todavía sin medir: el valor de antes en una pantalla normal
        Assert.Equal(SettingsDialogMetrics.MaxContentHeight,
            SettingsDialogMetrics.ContentHeight(0));
        Assert.Equal(SettingsDialogMetrics.MaxContentHeight,
            SettingsDialogMetrics.ContentHeight(-1));
        Assert.Equal(SettingsDialogMetrics.MaxContentHeight,
            SettingsDialogMetrics.ContentHeight(double.NaN));
    }

    [Fact]
    public void CreceConLaVentanaHastaElTope()
    {
        // Monótono: una ventana más alta nunca da un cuadro más bajo
        double anterior = 0;
        for (double v = 400; v <= 1200; v += 50)
        {
            double h = SettingsDialogMetrics.ContentHeight(v);
            Assert.True(h >= anterior, $"a {v} px el alto bajó de {anterior} a {h}");
            anterior = h;
        }
    }
}
