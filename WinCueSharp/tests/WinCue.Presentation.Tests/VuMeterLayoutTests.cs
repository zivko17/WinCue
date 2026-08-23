// =============================================================================
// VuMeterLayoutTests.cs — red del cálculo del VU meter (piloto Win2D).
// =============================================================================
// Fijan los valores que VuMeterControl.cs (WinCue.App) tiene HOY: mapeo de
// RMS a posición, ataque/release, retención de pico, testigo de clip y
// geometría en píxeles. Si alguien cambia un número aquí, el VU se ve
// distinto en pantalla en los DOS productos — que es exactamente lo que este
// archivo existe para impedir por accidente.
//
// Los tiempos se simulan con "ticks" de Stopwatch.Frequency, no con Sleep: el
// código de producción usa Stopwatch.GetElapsedTime(desde, hasta), así que
// fabricar los dos extremos a mano da control exacto sobre los milisegundos
// transcurridos sin que el test dependa de la velocidad de la máquina.
// =============================================================================

using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class VuMeterLayoutTests
{
    // Milisegundos de reloj de pared -> ticks de Stopwatch, para fabricar
    // "ahora" e "instantes pasados" sin depender de tiempo real.
    private static long Ticks(double ms) => (long)(ms * System.Diagnostics.Stopwatch.Frequency / 1000.0);

    // ═════════════════════════ Normalize (RMS → posición 0..1) ═════════════

    [Fact]
    public void SilencioAbsolutoDaPosicionCero()
    {
        Assert.Equal(0.0, VuMeterState.Normalize(0f));
    }

    [Fact]
    public void PorDebajoDelSueloDeSilencioDaCero()
    {
        // rms = 0.00001 exacto NO pasa el ">" del original: sigue en 0.
        Assert.Equal(0.0, VuMeterState.Normalize(0.00001f));
    }

    [Fact]
    public void CeroDbfsDaElTechoDeLaEscala()
    {
        Assert.Equal(1.0, VuMeterState.Normalize(1.0f), precision: 9);
    }

    [Fact]
    public void MenosQuinceDbLuceAlSetentaYCincoPorCiento()
    {
        // -15 dB es el ejemplo que documenta VuMeterControl.SetLevel: la
        // razón de ser de la escala en dB en vez de lineal.
        float rms = (float)Math.Pow(10.0, -15.0 / 20.0);
        Assert.Equal(0.75, VuMeterState.Normalize(rms), precision: 4);
    }

    [Fact]
    public void PorEncimaDeCeroDbfsSeRecortaAlTecho()
    {
        Assert.Equal(1.0, VuMeterState.Normalize(2.0f));
    }

    [Fact]
    public void PorDebajoDeMenosSesentaDbSeRecortaAlSuelo()
    {
        Assert.Equal(0.0, VuMeterState.Normalize(0.0005f));
    }

    // ═════════════════════════ Report: ataque / release ═════════════════════

    [Fact]
    public void ElAtaqueEsInstantaneo()
    {
        var state = new VuMeterState();
        state.Report(1.0f, Ticks(0)); // norm = 1.0, level parte de 0 → ataque
        Assert.Equal(1.0, state.Level, precision: 9);
    }

    [Fact]
    public void ElReleaseSeSuavizaConLaMismaMezclaDeHoy()
    {
        var state = new VuMeterState();
        state.Report(1.0f, Ticks(0));           // Level = 1.0
        double before = state.Level;

        // rms de ~-60 dB → norm ≈ 0.0, por debajo del nivel actual: release.
        double norm = VuMeterState.Normalize(0.001f);
        state.Report(0.001f, Ticks(10));
        double expected = before * 0.55 + norm * 0.45;
        Assert.Equal(expected, state.Level, precision: 9);
    }

    [Fact]
    public void ElPicoSoloSubeConElNivelMasAlto()
    {
        var state = new VuMeterState();
        state.Report(1.0f, Ticks(0));   // Peak = 1.0
        state.Report(0.001f, Ticks(10)); // norm menor: el pico NO baja aquí
        Assert.Equal(1.0, state.Peak, precision: 9);
    }

    [Fact]
    public void ElClipSeEngachaEnElUmbralDeHoy()
    {
        // Ticks(1), no Ticks(0): _clipTicks usa 0 como centinela de "nunca
        // ha saturado" (igual que el _clipTicks del original), así que un
        // "ahora" de 0 sería indistinguible de "nunca".
        var state = new VuMeterState();
        state.Report(0.985f, Ticks(1));
        Assert.True(state.IsClipped(Ticks(1)));
    }

    [Fact]
    public void JustoPorDebajoDelUmbralDeClipNoEngancha()
    {
        var state = new VuMeterState();
        state.Report(0.9849f, Ticks(1));
        Assert.False(state.IsClipped(Ticks(1)));
    }

    // ═════════════════════════ Decay: nivel, pico, testigo ═══════════════════

    [Fact]
    public void ElNivelNoCaeAntesDeLosCientoVeinteMilisegundos()
    {
        var state = new VuMeterState();
        state.Report(1.0f, Ticks(0));
        bool dirty = state.Decay(Ticks(120));
        Assert.False(dirty);
        Assert.Equal(1.0, state.Level, precision: 9);
    }

    [Fact]
    public void ElNivelCaePorElFactorDeHoyPasadosCientoVeinteMilisegundos()
    {
        var state = new VuMeterState();
        state.Report(1.0f, Ticks(0));
        bool dirty = state.Decay(Ticks(121));
        Assert.True(dirty);
        Assert.Equal(1.0 * 0.78, state.Level, precision: 9);
    }

    [Fact]
    public void ElNivelSeRedondeaACeroAlAcercarseAlSuelo()
    {
        var state = new VuMeterState();
        // norm = 0.0012: por encima del suelo de redondeo (0.001), pero
        // *0.78 lo deja por debajo — el caso exacto que fuerza el "snap".
        const double norm = 0.0012;
        float rms = (float)Math.Pow(10.0, (norm * 60.0 - 60.0) / 20.0);
        state.Report(rms, Ticks(0));   // ataque: Level ≈ 0.0012
        Assert.True(state.Level > 0.001);

        state.Decay(Ticks(121));
        Assert.Equal(0.0, state.Level);
    }

    [Fact]
    public void ElPicoSeRetieneMilDoscientosMilisegundos()
    {
        var state = new VuMeterState();
        state.Report(1.0f, Ticks(0));
        // A los 1200 ms EXACTOS el pico todavía no ha cumplido la retención
        // ("> 1200", no ">="): sigue intacto. (El nivel sí decae aparte —eso
        // lo fija ElNivelCaePorElFactorDeHoyPasadosCientoVeinteMilisegundos—,
        // así que aquí solo se comprueba el PICO.)
        state.Decay(Ticks(1_200));
        Assert.Equal(1.0, state.Peak, precision: 9);
    }

    [Fact]
    public void ElPicoCaeConElFactorDeHoyPasadaLaRetencion()
    {
        var state = new VuMeterState();
        state.Report(1.0f, Ticks(0));            // Level = Peak = 1.0
        // El nivel decae DENTRO del mismo Decay antes de que el pico lo lea:
        // por eso el pico no cae a 1.0*0.90 sino que respeta ese orden.
        bool dirty = state.Decay(Ticks(2_000));  // pasa retención de nivel y de pico
        Assert.True(dirty);
        Assert.Equal(1.0 * 0.78, state.Level, precision: 9);
        Assert.Equal(Math.Max(1.0 * 0.90, 1.0 * 0.78), state.Peak, precision: 9);
    }

    [Fact]
    public void ElPicoNoBajaDelNivelActualAlDecaer()
    {
        var state = new VuMeterState();
        state.Report(0.2f, Ticks(0));      // arranque: Level = Peak = norm(0.2)
        state.Report(1.0f, Ticks(1));      // pico sube a 1.0 (ataque)
        state.Report(0.2f, Ticks(2));      // nivel baja por release; el pico no se toca

        double norm02 = VuMeterState.Normalize(0.2f);
        double levelBeforeDecay = 1.0 * 0.55 + norm02 * 0.45;
        Assert.Equal(levelBeforeDecay, state.Level, precision: 9);

        state.Decay(Ticks(2 + 1_201));     // pasa la retención de nivel Y de pico
        double levelAfterDecay = levelBeforeDecay * 0.78;
        Assert.Equal(levelAfterDecay, state.Level, precision: 9);
        Assert.Equal(Math.Max(1.0 * 0.90, levelAfterDecay), state.Peak, precision: 9);
    }

    [Fact]
    public void ElTestigoDeClipDuraTresSegundos()
    {
        var state = new VuMeterState();
        state.Report(1.0f, Ticks(1));
        Assert.True(state.IsClipped(Ticks(1 + 2_999)));
        Assert.False(state.IsClipped(Ticks(1 + 3_000)));
    }

    [Fact]
    public void SinHaberSaturadoNuncaElTestigoDeClipEstaApagado()
    {
        var state = new VuMeterState();
        state.Report(0.5f, Ticks(0));
        Assert.False(state.IsClipped(Ticks(0)));
    }

    // ═════════════════════════ Geometría: marco, LED, barra, pico ═══════════

    [Fact]
    public void ElMarcoQuedaInsetioMedioPixel()
    {
        var r = VuMeterLayout.FrameRect(100, 20);
        Assert.Equal(new VuMeterRect(0.5, 0.5, 99, 19), r);
    }

    [Fact]
    public void SinClipNoHayLed()
    {
        Assert.Null(VuMeterLayout.ClipLedRect(100, 20, vertical: false, clipped: false));
    }

    [Fact]
    public void LedVerticalEsUnaFranjaArriba()
    {
        var r = VuMeterLayout.ClipLedRect(100, 20, vertical: true, clipped: true);
        Assert.Equal(new VuMeterRect(0.5, 0.5, 99, 5), r);
    }

    [Fact]
    public void LedHorizontalEsUnaFranjaALaDerecha()
    {
        var r = VuMeterLayout.ClipLedRect(100, 20, vertical: false, clipped: true);
        Assert.Equal(new VuMeterRect(94.5, 0.5, 5, 19), r);
    }

    [Fact]
    public void SinNivelNoHayRectanguloDeBarra()
    {
        Assert.Null(VuMeterLayout.LevelBarClipRect(100, 20, vertical: false, level: 0));
    }

    [Fact]
    public void LaBarraHorizontalCreceDesdeLaIzquierda()
    {
        // 0.25 es exacto en binario (evita ruido de coma flotante en el test).
        var r = VuMeterLayout.LevelBarClipRect(100, 20, vertical: false, level: 0.25);
        Assert.Equal(new VuMeterRect(0, 0, 25, 20), r);
    }

    [Fact]
    public void LaBarraVerticalCreceDesdeAbajo()
    {
        var r = VuMeterLayout.LevelBarClipRect(100, 20, vertical: true, level: 0.25);
        Assert.Equal(new VuMeterRect(0, 15, 100, 5), r);
    }

    [Fact]
    public void SinPicoSignificativoNoSePintaLaMarca()
    {
        Assert.Null(VuMeterLayout.PeakRect(100, 20, vertical: false, peak: 0.01));
    }

    [Fact]
    public void LaMarcaDePicoHorizontalNoSeSaleDelControl()
    {
        var r = VuMeterLayout.PeakRect(100, 20, vertical: false, peak: 1.0);
        Assert.Equal(new VuMeterRect(98, 0, 2, 20), r);
    }

    [Fact]
    public void LaMarcaDePicoVerticalNoBajaDeCero()
    {
        var r = VuMeterLayout.PeakRect(100, 20, vertical: true, peak: 1.0);
        Assert.Equal(new VuMeterRect(0, 0, 100, 2), r);
    }

    [Fact]
    public void LasMarcasDeEscalaVerticalSonLasDeHoy()
    {
        Assert.Equal(new[] { 0.25, 0.5, 0.75 }, VuMeterLayout.VerticalTickFractions);
    }

    // ═════════════════════════ Colores por zona ═════════════════════════════

    [Fact]
    public void ElDegradadoHorizontalEsVerdeAmbarRojo()
    {
        Assert.Equal(new[]
        {
            new VuMeterGradientStop(0.00, Palette.Go),
            new VuMeterGradientStop(0.62, Palette.Go),
            new VuMeterGradientStop(0.80, Palette.Standby),
            new VuMeterGradientStop(1.00, Palette.Pgm),
        }, VuMeterPalette.HorizontalBar);
    }

    [Fact]
    public void ElDegradadoVerticalEsRojoAmbarVerde()
    {
        Assert.Equal(new[]
        {
            new VuMeterGradientStop(0.00, Palette.Pgm),
            new VuMeterGradientStop(0.20, Palette.Standby),
            new VuMeterGradientStop(0.38, Palette.Go),
            new VuMeterGradientStop(1.00, Palette.Go),
        }, VuMeterPalette.VerticalBar);
    }

    [Fact]
    public void LosColoresSueltosSonLosDeHoy()
    {
        Assert.Equal(Palette.MeterBg, VuMeterPalette.Background);
        Assert.Equal(Palette.BorderMeter, VuMeterPalette.Frame);
        Assert.Equal(Palette.TextDimmer, VuMeterPalette.Tick);
        Assert.Equal((byte)90, VuMeterPalette.TickAlpha);
        Assert.Equal(Palette.White, VuMeterPalette.Peak);
        Assert.Equal(Palette.Clip, VuMeterPalette.Clip);
    }
}
