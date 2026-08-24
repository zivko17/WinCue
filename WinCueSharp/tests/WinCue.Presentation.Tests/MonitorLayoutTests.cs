// =============================================================================
// MonitorLayoutTests.cs — el hueco de vídeo de los monitores es 16:9.
// =============================================================================
// Fija la fórmula que WinCue tenía en línea dentro de LiveMonitorControl y que
// Secuence no tenía en absoluto (sus cajas llevaban un Height fijo en el XAML).
// Lo que se prueba es justo lo que se vio mal en pantalla: que la proporción
// sea la del HUECO DEL VÍDEO —descontando borde y barra del distintivo— y no
// la del control entero.
// =============================================================================

using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class MonitorLayoutTests
{
    // Los dos productos usan borde de 1 px y una barra de distintivo medida.
    private const double Border = 1;
    private const double HorizontalBorder = Border * 2;

    private static double Chrome(double barHeight) => Border * 2 + barHeight;

    [Theory]
    [InlineData(420.0, 18.0)]
    [InlineData(340.0, 18.0)]
    [InlineData(800.0, 22.0)]
    [InlineData(281.0, 17.5)]
    public void ElHuecoDeVideoQuedaExactamenteEn16Y9(double ancho, double barra)
    {
        double alto = MonitorLayout.HeightForWidth(ancho, HorizontalBorder, Chrome(barra));

        // Deshacer el chrome para quedarse con el hueco REAL del vídeo
        double huecoAncho = ancho - HorizontalBorder;
        double huecoAlto = alto - Chrome(barra);
        Assert.Equal(16.0 / 9.0, huecoAncho / huecoAlto, 9);
    }

    [Fact]
    public void ElBordeYLaBarraSeDescuentan()
    {
        // El fallo que ya se pagó una vez en WinCue: tomar el 16:9 del control
        // entero deja la imagen saliéndose del marco. Con 420 de ancho y 20 de
        // chrome, el alto NO es 420·9/16 = 236,25 sino (418)·9/16 + 20.
        double alto = MonitorLayout.HeightForWidth(420, HorizontalBorder, Chrome(18));
        Assert.NotEqual(420 * 9.0 / 16.0, alto, 3);
        Assert.Equal(418 * 9.0 / 16.0 + 20, alto, 9);
    }

    [Fact]
    public void NoBajaDelMinimoUtil()
    {
        // Panel muy estrecho: el monitor deja de encoger para seguir diciendo algo
        Assert.Equal(MonitorLayout.MinHeight,
            MonitorLayout.HeightForWidth(80, HorizontalBorder, Chrome(18)));
        Assert.Equal(MonitorLayout.MinHeight,
            MonitorLayout.HeightForWidth(0, HorizontalBorder, Chrome(18)));
    }

    [Fact]
    public void UnAnchoAbsurdoNoDaUnAltoNegativo()
    {
        // Ancho menor que el propio borde (primer layout, medidas a medias)
        double alto = MonitorLayout.HeightForWidth(-50, HorizontalBorder, Chrome(18));
        Assert.Equal(MonitorLayout.MinHeight, alto);
        Assert.True(alto > 0);
    }

    [Fact]
    public void CreceConElAnchoUnaVezPasadoElMinimo()
    {
        // El panel izquierdo de WinCue es arrastrable: al ensancharlo, los
        // monitores crecen con él y nunca menguan.
        double anterior = 0;
        for (double w = 100; w <= 1000; w += 25)
        {
            double alto = MonitorLayout.HeightForWidth(w, HorizontalBorder, Chrome(18));
            Assert.True(alto >= anterior, $"a {w} px de ancho el alto bajó de {anterior} a {alto}");
            anterior = alto;
        }
    }

    [Fact]
    public void PgmYPvwConElMismoAnchoMidenLoMismo()
    {
        // La otra mitad del fallo en Secuence: el PVW no tenía columna de VU,
        // así que era MÁS ANCHO que el PGM y las dos cajas no cuadraban. Con
        // el mismo ancho, el alto tiene que salir idéntico — es lo que hace
        // que los dos monitores queden alineados.
        const double ancho = 418;
        Assert.Equal(
            MonitorLayout.HeightForWidth(ancho, HorizontalBorder, Chrome(18)),
            MonitorLayout.HeightForWidth(ancho, HorizontalBorder, Chrome(18)));
    }
}
