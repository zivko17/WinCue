// =============================================================================
// BevelVeilTests.cs — red del cálculo del relieve de los controles
// (WinCue.Presentation.BevelVeil / BevelPalette).
// =============================================================================
// Fija exactamente donde caen las paradas HOY, incluido el caso invertido,
// para que un cambio futuro en el algoritmo sea una decisión consciente y no
// una deriva silenciosa entre WinCue y Secuence.

using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class BevelVeilTests
{
    // ═════════════════════════ BevelVeil.Stops — no invertido ═══════════════

    [Fact]
    public void Stops_SinInvertir_CuatroParadasEnLasPosicionesFijas()
    {
        var stops = BevelVeil.Stops(0x2A, 0x30);

        Assert.Equal(4, stops.Count);
        Assert.Equal(0.0, stops[0].Offset, precision: 6);
        Assert.Equal(0.45, stops[1].Offset, precision: 6);
        Assert.Equal(0.55, stops[2].Offset, precision: 6);
        Assert.Equal(1.0, stops[3].Offset, precision: 6);
    }

    [Fact]
    public void Stops_SinInvertir_ArribaEsBlancoConAlfaLight()
    {
        var stops = BevelVeil.Stops(light: 0x2A, dark: 0x30);

        var top = stops[0];
        Assert.Equal(0x2A, top.A);
        Assert.Equal(255, top.R);
        Assert.Equal(255, top.G);
        Assert.Equal(255, top.B);
    }

    [Fact]
    public void Stops_SinInvertir_AbajoEsNegroConAlfaDark()
    {
        var stops = BevelVeil.Stops(light: 0x2A, dark: 0x30);

        var bottom = stops[3];
        Assert.Equal(0x30, bottom.A);
        Assert.Equal(0, bottom.R);
        Assert.Equal(0, bottom.G);
        Assert.Equal(0, bottom.B);
    }

    [Fact]
    public void Stops_SinInvertir_LasDosParadasCentralesSonAlfaCero()
    {
        var stops = BevelVeil.Stops(0x2A, 0x30);

        Assert.Equal(0, stops[1].A);
        Assert.Equal(0, stops[2].A);
        // La parada 0.45 sigue siendo blanca (mismo canal que arriba) y la
        // 0.55 sigue siendo negra (mismo canal que abajo): el corte es solo
        // de opacidad, no de color — así no hay gris muerto en el centro.
        Assert.Equal(255, stops[1].R);
        Assert.Equal(0, stops[2].R);
    }

    // ═════════════════════════ BevelVeil.Stops — invertido ═══════════════════

    [Fact]
    public void Stops_Invertido_LaSombraPasaArriba()
    {
        // Misma llamada que Theme.BevelFacePressed = Veil(0x30, 0x2A, inverted: true).
        var stops = BevelVeil.Stops(light: 0x30, dark: 0x2A, inverted: true);

        var top = stops[0];
        var bottom = stops[3];

        // Arriba: negro con la opacidad de "dark" (el segundo argumento, 0x2A).
        Assert.Equal(0x2A, top.A);
        Assert.Equal(0, top.R);
        Assert.Equal(0, top.G);
        Assert.Equal(0, top.B);

        // Abajo: blanco con la opacidad de "light" (el primer argumento, 0x30).
        Assert.Equal(0x30, bottom.A);
        Assert.Equal(255, bottom.R);
        Assert.Equal(255, bottom.G);
        Assert.Equal(255, bottom.B);
    }

    [Fact]
    public void Stops_InvertidoDeBevelFace_ConservaLasOpacidadesYCambiaElColor()
    {
        // BevelFace = Veil(0x2A, 0x30); BevelFacePressed = Veil(0x30, 0x2A, inverted: true).
        // Los argumentos vienen intercambiados A PROPÓSITO en la llamada
        // invertida, así que las opacidades de arriba/abajo salen IGUALES en
        // los dos juegos (0x2A arriba, 0x30 abajo) — lo único que cambia es el
        // color de cada extremo (blanco arriba pasa a negro arriba): eso es lo
        // que hace que la tecla se lea hundida en vez de solo "más oscura".
        var face = BevelVeil.Stops(0x2A, 0x30);
        var pressed = BevelVeil.Stops(0x30, 0x2A, inverted: true);

        Assert.Equal(face[0].A, pressed[0].A);
        Assert.Equal(face[3].A, pressed[3].A);

        Assert.Equal(255, face[0].R);   // cara en reposo: blanco arriba
        Assert.Equal(0, pressed[0].R);  // cara pulsada: negro arriba (sombra)
        Assert.Equal(0, face[3].R);     // cara en reposo: negro abajo
        Assert.Equal(255, pressed[3].R); // cara pulsada: blanco abajo
    }

    // ═════════════════════════ BevelPalette — los juegos de HOY ══════════════

    [Fact]
    public void BevelPalette_Face_UsaLosMismosValoresQueThemeBevelFace()
    {
        var expected = BevelVeil.Stops(0x2A, 0x30);
        Assert.Equal(expected, BevelPalette.Face);
    }

    [Fact]
    public void BevelPalette_FacePressed_UsaLosMismosValoresQueThemeBevelFacePressed()
    {
        var expected = BevelVeil.Stops(0x30, 0x2A, inverted: true);
        Assert.Equal(expected, BevelPalette.FacePressed);
    }

    [Fact]
    public void BevelPalette_Edge_UsaLosMismosValoresQueThemeBevelEdge()
    {
        var expected = BevelVeil.Stops(0x40, 0x58);
        Assert.Equal(expected, BevelPalette.Edge);
    }

    [Fact]
    public void BevelPalette_HoverAlpha_EsElOchoPorCientoAproximado()
    {
        // Theme.BevelHover = Solid(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)).
        Assert.Equal(0x14, BevelPalette.HoverAlpha);
    }
}
