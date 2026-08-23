// =============================================================================
// PaletteTests.cs — red del traslado de los valores de color.
// =============================================================================
// Theme.cs avisa en su cabecera: «este fichero preserva los valores EXACTOS
// que había, sin cambiar un píxel». El traslado a Palette tiene que preservar
// esa promesa, y un hex mal tecleado no lo ve nadie hasta que un cue sale del
// color equivocado en un escenario. Estas pruebas son ese ojo.
// =============================================================================

using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class PaletteTests
{
    [Fact]
    public void LosColoresDeTransporteSonLosDeSiempre()
    {
        Assert.Equal(0x2ecc71u, Palette.Go);
        Assert.Equal(0x1e7e34u, Palette.GoButton);
        Assert.Equal(0x27865au, Palette.GoDeep);
        Assert.Equal(0xc0392bu, Palette.Pgm);
        Assert.Equal(0x7e1e1eu, Palette.PanicButton);
    }

    [Fact]
    public void LosAvisosYElOroSonLosDeSiempre()
    {
        Assert.Equal(0xe8c000u, Palette.Standby);
        Assert.Equal(0xe8a200u, Palette.Warn);
        Assert.Equal(0xe87a00u, Palette.WarnDeep);
        Assert.Equal(0xc9a227u, Palette.Gold);
        Assert.Equal(0xc9a84au, Palette.GoldSoft);
        Assert.Equal(0xff2020u, Palette.Clip);
        Assert.Equal(0xff8a7au, Palette.Danger);
        Assert.Equal(0x2a6fb0u, Palette.Blue);
    }

    [Fact]
    public void LosColoresPorTipoDeCueMantienenSusAlias()
    {
        Assert.Equal(0x4a86b8u, Palette.TypeAudio);
        Assert.Equal(0x9b59b6u, Palette.TypeVideo);
        Assert.Equal(0x888888u, Palette.TypeWait);

        // Derivados, no duplicados: si alguien cambia Warn, TypeFade cambia.
        Assert.Equal(Palette.Warn, Palette.TypeFade);
        Assert.Equal(Palette.Go, Palette.TypeStart);
        Assert.Equal(Palette.Pgm, Palette.TypeStop);
    }

    [Fact]
    public void EstanLos94ColoresQueTeniaTheme()
    {
        // Cuenta exacta. Si el traslado se dejó uno por el camino, el
        // compilador no dice nada — pero WinCue pinta un gris donde no toca.
        int n = typeof(Palette)
            .GetFields(System.Reflection.BindingFlags.Public |
                       System.Reflection.BindingFlags.Static)
            .Count(f => f.IsLiteral && f.FieldType == typeof(uint));

        Assert.Equal(94, n);
    }
}
