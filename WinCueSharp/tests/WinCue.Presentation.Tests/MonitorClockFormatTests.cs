using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class MonitorClockFormatTests
{
    [Theory]
    [InlineData(0.0, "0:00.0")]
    [InlineData(5.4, "0:05.4")]
    [InlineData(59.9, "0:59.9")]
    [InlineData(60.0, "1:00.0")]
    [InlineData(65.4, "1:05.4")]
    [InlineData(3599.9, "59:59.9")]
    [InlineData(double.NaN, "0:00.0")]
    [InlineData(-1.0, "0:00.0")]   // negativo (posición antes de cargar) → 0
    public void MmssFormateaMinutoSegundoDecimo(double seconds, string esperado)
    {
        // Mmss interpola sin fijar cultura (igual que hacía en
        // MonitorTransportBar antes de subir aquí): el separador decimal
        // depende de CurrentCulture. Se fija Invariant para que el test sea
        // determinista sin cambiar el comportamiento real (que SIGUE
        // dependiendo del hilo en producción — WinCue no puede cambiar de
        // aspecto por haberse extraído).
        var prev = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(esperado, MonitorClockFormat.Mmss(seconds));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = prev;
        }
    }

    [Fact]
    public void FormatUneLasDosMitadesConBarra()
    {
        var prev = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal("0:12.3 / 1:05.0", MonitorClockFormat.Format(12.3, 65.0));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = prev;
        }
    }
}
