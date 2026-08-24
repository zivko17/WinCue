// =============================================================================
// CueChoicesTests.cs — las opciones del inspector cubren TODO el modelo
// (hitos-0-1, inspector segunda pasada).
// =============================================================================
// Estas pruebas existen por un fallo concreto que el compilador no puede ver:
// añadir un valor a RetriggerMode (o a FadeScope, o a MidiTriggerType…) y
// olvidarse de ofrecerlo en el inspector. Antes, con la lista escrita en línea
// en cada producto, el olvido era además DOBLE y silencioso. Aquí se falla en
// rojo: por cada enum se comprueba que cada valor aparece exactamente una vez
// y que ninguna opción apunta a un valor que ya no existe.
//
// También se fija que el VALOR de cada opción es enum.ToString() (PascalCase),
// no la forma del JSON (UPPER_SNAKE): es lo que el inspector compara contra el
// cue cargado, y confundirlos dejaría el desplegable sin selección visible.
// =============================================================================

using WinCue.Core;
using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class CueChoicesTests
{
    /// <summary>Cada valor del enum tiene UNA opción, y cada opción apunta a
    /// un valor real del enum (parseable, sin distinguir mayúsculas ya que la
    /// forma esperada es exactamente enum.ToString()).</summary>
    private static void AssertCoversEnum<TEnum>(IReadOnlyList<CueChoice> choices)
        where TEnum : struct, Enum
    {
        var expected = Enum.GetNames<TEnum>().OrderBy(n => n, StringComparer.Ordinal).ToList();
        var actual = choices.Select(c => c.Value).OrderBy(v => v, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, actual);

        foreach (var c in choices)
        {
            // Exactamente enum.ToString(): "AutoContinue", nunca "AUTO_CONTINUE"
            Assert.True(Enum.TryParse<TEnum>(c.Value, ignoreCase: false, out _),
                $"«{c.Value}» no es un valor de {typeof(TEnum).Name} en su forma ToString()");
            Assert.False(string.IsNullOrWhiteSpace(c.Label),
                $"la opción «{c.Value}» no tiene etiqueta");
        }
    }

    [Fact]
    public void ContinueModesCubreElEnum() =>
        AssertCoversEnum<ContinueMode>(CueChoices.ContinueModes);

    [Fact]
    public void GroupModesCubreElEnum() =>
        AssertCoversEnum<GroupMode>(CueChoices.GroupModes);

    [Fact]
    public void RetriggerModesCubreElEnum() =>
        AssertCoversEnum<RetriggerMode>(CueChoices.RetriggerModes);

    [Fact]
    public void FadeScopesCubreElEnum() =>
        AssertCoversEnum<FadeScope>(CueChoices.FadeScopes);

    [Fact]
    public void MidiTypesCubreElEnum() =>
        AssertCoversEnum<MidiTriggerType>(CueChoices.MidiTypes);

    [Fact]
    public void ElOrdenDeLosDesplegablesEsElDeHoy()
    {
        // El orden lo ve el operador: no es alfabético ni el del enum, es el
        // que WinCue tiene en pantalla (el más usado primero).
        Assert.Equal(new[] { "None", "AutoContinue", "AutoFollow" },
            CueChoices.ContinueModes.Select(c => c.Value));
        Assert.Equal(new[] { "Enter", "Timeline", "GoNext", "Random" },
            CueChoices.GroupModes.Select(c => c.Value));
        Assert.Equal(new[] { "None", "Panic", "Stop", "HardStop", "HardRestart" },
            CueChoices.RetriggerModes.Select(c => c.Value));
        Assert.Equal(new[] { "Peers", "List", "All" },
            CueChoices.FadeScopes.Select(c => c.Value));
    }

    [Fact]
    public void ElDefaultDeCadaCampoEstaOfrecido()
    {
        // Un cue recién creado tiene que poder mostrarse: si su valor por
        // defecto no estuviera en la lista, el desplegable saldría en blanco.
        var cue = new Cue();
        Assert.Contains(cue.ContinueMode.ToString(), CueChoices.ContinueModes.Select(c => c.Value));
        Assert.Contains(cue.GroupMode.ToString(), CueChoices.GroupModes.Select(c => c.Value));
        Assert.Contains(cue.Retrigger.ToString(), CueChoices.RetriggerModes.Select(c => c.Value));
        Assert.Contains(cue.FadeOthersScope.ToString(), CueChoices.FadeScopes.Select(c => c.Value));
        Assert.Contains(cue.MidiType.ToString(), CueChoices.MidiTypes.Select(c => c.Value));
        Assert.Contains(cue.Color, CueChoices.Colors.Select(c => c.Key));
    }

    // ═════════════════════════ Muestras de color ═════════════════════════

    [Fact]
    public void LosColoresSonLosDelModeloYEnSuOrden()
    {
        Assert.Equal(CueConstants.CueColors.Keys, CueChoices.Colors.Select(c => c.Key));
        // El primero es el hueco de «sin color»: el operador tiene que poder
        // QUITAR un color, no solo cambiarlo
        Assert.Equal("", CueChoices.Colors[0].Key);
        Assert.False(CueChoices.Colors[0].HasColor);
        Assert.Equal(0u, CueChoices.Colors[0].Rgb);
    }

    [Fact]
    public void CadaColorTraeSuRgbResueltoYSuEtiqueta()
    {
        foreach (var c in CueChoices.Colors.Where(c => c.Key.Length > 0))
        {
            Assert.True(c.HasColor);
            Assert.Equal(CueConstants.CueColors[c.Key],
                $"#{c.Rgb:x6}");           // el mismo hex del modelo, sin perder un dígito
            Assert.False(string.IsNullOrWhiteSpace(c.Label));
            Assert.NotEqual(CueChoices.ColorLabel(""), c.Label);   // no es «sin color»
        }
    }

    [Fact]
    public void UnColorDesconocidoCaeEnSinColor() =>
        Assert.Equal(CueChoices.ColorLabel(""), CueChoices.ColorLabel("magenta"));

    [Fact]
    public void LasEtiquetasSiguenAlIdiomaActivo()
    {
        // Loc.Current se fija al arrancar; las listas se construyen al pedirlas
        // (ver la cabecera de CueChoices.cs), así que el idioma nunca se queda
        // congelado por el orden de arranque.
        var original = Loc.Current;
        try
        {
            Loc.SetFromCode("en");
            Assert.Equal("Red", CueChoices.ColorLabel("red"));
            Assert.Equal("Ignore", CueChoices.RetriggerModes[0].Label);
            Loc.SetFromCode("es");
            Assert.Equal("Rojo", CueChoices.ColorLabel("red"));
            Assert.Equal("Ignorar", CueChoices.RetriggerModes[0].Label);
        }
        finally
        {
            Loc.SetFromCode(original == Loc.Language.En ? "en" : "es");
        }
    }
}
