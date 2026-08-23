// =============================================================================
// CueTimeFieldTests.cs — Format/Parse del campo de tiempo editable del
// inspector, extraído verbatim de InspectorPanel (TimeF/FormatTime/ParseTime).
// =============================================================================
// El contrato que de verdad importa: Format(Parse(texto)) tiene que devolver
// algo que Parse vuelva a leer al mismo número — es un campo que se relee a
// sí mismo constantemente (perder foco, recargar el cue). Estas pruebas fijan
// los valores exactos de HOY (los mismos que ya tenía WinCue) para que el
// traslado no cambie ni un carácter, más los casos de ida y vuelta.
// =============================================================================

using System.Globalization;
using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class CueTimeFieldTests
{
    // El tramo ≥60 s formatea con un $"...:{...}" sin CultureInfo explícita
    // —igual que el InspectorPanel.cs del que se extrajo, verbatim, sin
    // corregirlo: es una decisión de WinCue anterior a esta tarea y "no
    // cambies el comportamiento de WinCue" es la regla— así que en una
    // máquina con coma decimal (la de esta tarea, es-ES) el separador que
    // sale es una coma, no un punto. Fijar aquí la cultura invariante para
    // ESTAS aserciones concretas hace la prueba determinista sin tocar la
    // función: comprueba la forma del texto, no el separador de quien la
    // ejecuta. Parse() ya normaliza coma Y punto, así que el campo se
    // relee bien en cualquier locale — ver la duda en el informe.
    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(1.5, "1.5")]
    [InlineData(45.25, "45.25")]
    [InlineData(59.999, "59.999")]
    public void FormatPorDebajoDelMinutoEsSegundosASecas(double seconds, string expected) =>
        Assert.Equal(expected, CueTimeField.Format(seconds));

    [Theory]
    [InlineData(60.0, "1:00")]
    [InlineData(90.0, "1:30")]
    [InlineData(90.5, "1:30.5")]
    [InlineData(125.25, "2:05.25")]
    [InlineData(600.0, "10:00")]
    public void FormatAPartirDelMinutoEsMSsConDecimalesRecortados(double seconds, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, CueTimeField.Format(seconds));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("90", 90.0)]
    [InlineData("1:30", 90.0)]
    [InlineData("1:30.5", 90.5)]
    [InlineData("1:30,5", 90.5)]   // coma decimal, da igual la configuración regional
    [InlineData(" 45.25 ", 45.25)] // espacios sueltos
    [InlineData("0", 0.0)]
    public void ParseLeeSegundosYElFormatoMSs(string text, double expected) =>
        Assert.Equal(expected, CueTimeField.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1:2:3")]      // tres partes, no es un tiempo de este campo
    [InlineData("1:")]
    [InlineData(":30")]
    [InlineData(null)]
    public void ParseDevuelveNullSiNoSeEntiende(string? text) =>
        Assert.Null(CueTimeField.Parse(text));

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.5)]
    [InlineData(59.75)]
    [InlineData(90.0)]
    [InlineData(90.5)]
    [InlineData(3599.999)]
    public void FormatYParseHacenIdaYVuelta(double seconds)
    {
        string text = CueTimeField.Format(seconds);
        double? back = CueTimeField.Parse(text);
        Assert.NotNull(back);
        Assert.Equal(seconds, back!.Value, precision: 6);
    }
}
