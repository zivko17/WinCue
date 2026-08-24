using System.Globalization;
using WinCue.Audio;
using Xunit;

namespace WinCue.Audio.Tests;

public class MediaProbeTests
{
    [Theory]
    [InlineData("10.000000", 10.0)]
    [InlineData("8.5", 8.5)]
    [InlineData("0.033367", 0.033367)]
    [InlineData("3600", 3600.0)]
    public void LeeLaSalidaDeFfprobe(string salida, double esperado)
    {
        Assert.Equal(esperado, MediaProbe.ParseDuration(salida), 6);
    }

    [Fact]
    public void SeComeLosEspaciosYElSaltoDeLinea()
    {
        // ffprobe termina con salto de línea; ReadToEnd lo trae.
        Assert.Equal(10.0, MediaProbe.ParseDuration("10.000000\n"));
        Assert.Equal(10.0, MediaProbe.ParseDuration("  10.0  "));
    }

    [Fact]
    public void ElPuntoDECIMALManda_AunqueLaCulturaUseComa()
    {
        // Este es el motivo de que la función exista aparte y esté probada:
        // en un Windows en español, leer "10.5" con la cultura del sistema
        // daría 105. La salida de ffprobe SIEMPRE trae punto.
        var previa = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("es-ES");
        try
        {
            Assert.Equal(10.5, MediaProbe.ParseDuration("10.5"));
        }
        finally { CultureInfo.CurrentCulture = previa; }
    }

    [Theory]
    [InlineData("N/A")]
    [InlineData("")]
    [InlineData("no es un numero")]
    public void LoQueNoEsUnNumero_LANZA(string salida)
    {
        // Que lance es el contrato: quien llama lo traduce a "no disponible"
        // en la barra en vez de dibujar una duración inventada.
        Assert.ThrowsAny<System.Exception>(() => MediaProbe.ParseDuration(salida));
    }
}
