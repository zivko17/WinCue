using WinCue.Presentation;
using Xunit;

namespace WinCue.Presentation.Tests;

public class HotkeyNameTests
{
    [Theory]
    // Los dos alias de Qt que traen los shows guardados por la versión Python.
    [InlineData("Esc", "Escape")]
    [InlineData("Return", "Enter")]
    public void AliasHeredadosDeQt_SeTraducen(string escrito, string esperado)
    {
        Assert.Equal(esperado, HotkeyName.Normalize(escrito));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("F5")]
    [InlineData("Space")]
    [InlineData("Escape")]
    [InlineData("Enter")]
    public void LoQueYaEsCanonico_NoSeToca(string nombre)
    {
        Assert.Equal(nombre, HotkeyName.Normalize(nombre));
    }

    [Fact]
    public void NoValida_LoQueNoEsUnaTeclaSaleTalCual()
    {
        // Rechazarlo es cosa del Enum.TryParse de cada producto, no de aquí:
        // esta clase solo traduce alias.
        Assert.Equal("ni idea", HotkeyName.Normalize("ni idea"));
        Assert.Equal("", HotkeyName.Normalize(""));
    }

    [Fact]
    public void LosAliasDistinguenMayusculas_ComoEnWinCue()
    {
        // El switch de WinCue compara exacto, así que "esc" NO es el alias.
        // Se deja escrito para que quede claro que es el comportamiento
        // heredado y no un descuido: quien lo cambie, que cambie los dos
        // productos a la vez.
        Assert.Equal("esc", HotkeyName.Normalize("esc"));
    }
}
