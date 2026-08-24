using WinCue.Presentation;
using Xunit;

namespace WinCue.Presentation.Tests;

public class StatusLogTests
{
    static readonly DateTime Momento = new(2026, 8, 24, 21, 5, 3);

    [Fact]
    public void SellaConLaHora()
    {
        var log = new StatusLog();
        log.Add("Show cargado", Momento);
        Assert.Equal("[21:05:03] Show cargado", log.Entries[0]);
    }

    [Fact]
    public void AlPasarseDelTope_TiraElMasAntiguo()
    {
        var log = new StatusLog();
        for (int i = 0; i < StatusLog.MaxEntries + 10; i++)
            log.Add($"mensaje {i}", Momento);

        Assert.Equal(StatusLog.MaxEntries, log.Entries.Count);
        // Los diez primeros se fueron; el más antiguo que queda es el 10.
        Assert.Contains("mensaje 10", log.Entries[0]);
        Assert.Contains($"mensaje {StatusLog.MaxEntries + 9}", log.Entries[^1]);
    }

    [Fact]
    public void SeEnsenaElMasRECIENTEPrimero()
    {
        var log = new StatusLog();
        log.Add("primero", Momento);
        log.Add("segundo", Momento);
        var lineas = log.Render("(vacío)").Split(Environment.NewLine);
        Assert.Contains("segundo", lineas[0]);
        Assert.Contains("primero", lineas[1]);
    }

    [Fact]
    public void SinMensajes_SaleElTextoDeVacio()
    {
        Assert.Equal("(sin mensajes todavía)",
            new StatusLog().Render("(sin mensajes todavía)"));
    }

    [Theory]
    [InlineData("[AVISO] el fundido no cabe")]
    [InlineData("Error al abrir el archivo")]
    [InlineData("ERROR EN MAYUSCULAS")]          // no distingue mayúsculas
    [InlineData("La conversión falló")]
    [InlineData("La conversión FALLÓ")]
    public void LoQueEnciendeElBoton(string mensaje)
    {
        Assert.True(StatusLog.IsWarning(mensaje));
    }

    [Fact]
    public void ElGlifoDeAvisoTambienCuenta()
    {
        // Hay mensajes que no traen la palabra, solo el glifo.
        Assert.True(StatusLog.IsWarning($"{Icons.Warning} algo pasa"));
    }

    [Theory]
    [InlineData("Show cargado: show-prueba.json")]
    [InlineData("Cue añadido")]
    [InlineData("")]
    public void LaRutinaNoEnciendeNada(string mensaje)
    {
        Assert.False(StatusLog.IsWarning(mensaje));
    }
}
