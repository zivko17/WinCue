// =============================================================================
// MpvSyncPllTests.cs — Tabla de decisiones del PLL híbrido (lógica pura)
// =============================================================================
// MpvSyncPll no toca mpv ni timers: (videoPos, audioElapsed, duración) →
// {speed, escribir speed, hard-seek}. Aquí se verifica la tabla completa
// documentada en la clase: zona muerta, corrección proporcional con clamp,
// umbral de escritura, frenada del espejo tardío, hard-seek sostenido,
// audio null (free-run), guarda de frontera de loop y reposo tras seek.
// =============================================================================

using WinCue.Video;

namespace WinCue.Video.Tests;

public class MpvSyncPllTests
{
    private const double Dur = 60.0;   // duración holgada: sin guarda de loop

    // ------------------------------------------------------------------
    // Audio null → free-run
    // ------------------------------------------------------------------

    [Fact]
    public void SinRelojDeAudio_FreeRun_SpeedUno_SinSeek()
    {
        var pll = new MpvSyncPll();
        var d = pll.Tick(videoPos: 5.0, audioElapsed: null, Dur);
        Assert.Equal(1.0, d.Speed);
        Assert.Null(d.HardSeekTo);
        Assert.False(d.WriteSpeed);   // mpv ya nace a speed 1: no escribir
    }

    [Fact]
    public void SinTimePosDeVideo_TambienFreeRun()
    {
        var pll = new MpvSyncPll();
        var d = pll.Tick(videoPos: null, audioElapsed: 3.0, Dur);
        Assert.Equal(1.0, d.Speed);
        Assert.Null(d.HardSeekTo);
    }

    [Fact]
    public void AudioTerminado_VuelveASpeedUno()
    {
        var pll = new MpvSyncPll();
        // Con drift moderado el PLL corrige…
        var c = pll.Tick(10.0, 10.1, Dur);
        Assert.True(c.WriteSpeed);
        Assert.NotEqual(1.0, c.Speed);
        // …y al morir el audio (espejo terminó) vuelve a 1.0 y lo escribe
        var d = pll.Tick(10.5, null, Dur);
        Assert.Equal(1.0, d.Speed);
        Assert.True(d.WriteSpeed);
        Assert.Null(d.HardSeekTo);
    }

    // ------------------------------------------------------------------
    // Zona muerta y corrección proporcional
    // ------------------------------------------------------------------

    [Fact]
    public void ZonaMuerta_MenosDe15ms_NoTocaNada()
    {
        var pll = new MpvSyncPll();
        var d = pll.Tick(10.010, 10.0, Dur);   // +10 ms
        Assert.Equal(1.0, d.Speed);
        Assert.False(d.WriteSpeed);
        Assert.Null(d.HardSeekTo);
    }

    [Theory]
    [InlineData(0.05, 0.985)]    // vídeo adelantado 50 ms → frenar: 1 − 0.05·0.3
    [InlineData(-0.05, 1.015)]   // vídeo atrasado 50 ms → acelerar
    public void DriftModerado_CorreccionProporcional(double drift, double expected)
    {
        var pll = new MpvSyncPll();
        var d = pll.Tick(10.0 + drift, 10.0, Dur);
        Assert.Equal(expected, d.Speed, 3);
        Assert.True(d.WriteSpeed);
        Assert.Null(d.HardSeekTo);
    }

    [Theory]
    [InlineData(0.25, 0.98)]     // clamp inferior: 1 − 0.25·0.3 = 0.925 → 0.98
    [InlineData(-0.25, 1.02)]    // clamp superior
    public void DriftModerado_ClampAlDosPorCiento(double drift, double expected)
    {
        var pll = new MpvSyncPll();
        var d = pll.Tick(10.0 + drift, 10.0, Dur);
        Assert.Equal(expected, d.Speed, 3);
    }

    [Fact]
    public void SpeedSoloSeEscribeSiCambiaUnaMilesima()
    {
        var pll = new MpvSyncPll();
        var first = pll.Tick(10.05, 10.0, Dur);
        Assert.True(first.WriteSpeed);
        // Mismo drift → misma speed → NO re-escribir la propiedad
        var second = pll.Tick(10.05, 10.0, Dur);
        Assert.Equal(first.Speed, second.Speed);
        Assert.False(second.WriteSpeed);
        // Cambio microscópico del drift (< 1 milésima de speed) → tampoco
        var third = pll.Tick(10.051, 10.0, Dur);
        Assert.False(third.WriteSpeed);
    }

    // ------------------------------------------------------------------
    // Espejo tardío: vídeo ADELANTADO (el caso real del arranque)
    // ------------------------------------------------------------------

    [Fact]
    public void AudioLlegaTarde_MenosDe2s_FrenadaSuave_SinSeek()
    {
        // El espejo decodifica el archivo entero a RAM y aparece 1 s por
        // detrás del vídeo: FRENAR (suelo 0.9), nunca rebobinar en pantalla.
        var pll = new MpvSyncPll();
        var d = pll.Tick(videoPos: 1.2, audioElapsed: 0.2, Dur);
        Assert.Null(d.HardSeekTo);
        Assert.Equal(0.9, d.Speed, 3);   // 1 − 1.0·0.3 = 0.7 → suelo 0.9
        Assert.True(d.WriteSpeed);
    }

    [Fact]
    public void AudioLlegaTarde_DriftPequeno_FrenadaProporcional()
    {
        var pll = new MpvSyncPll();
        var d = pll.Tick(videoPos: 0.7, audioElapsed: 0.4, Dur);   // +0.3 s
        Assert.Null(d.HardSeekTo);
        Assert.Equal(1.0 - 0.3 * 0.3, d.Speed, 3);   // 0.91: dentro del suelo
    }

    [Fact]
    public void AudioLlegaTarde_MasDe2s_UnHardSeekAtras()
    {
        // Desfase enorme (≥ 2 s): frenar tardaría una eternidad — UN seek
        // atrás visible que deja el lipsync perfecto (el audio ES el programa).
        var pll = new MpvSyncPll();
        var d1 = pll.Tick(3.0, 0.2, Dur);
        Assert.Null(d1.HardSeekTo);          // sostenido: 1er tick no actúa
        var d2 = pll.Tick(3.4, 0.6, Dur);
        Assert.Equal(0.6, d2.HardSeekTo!.Value, 3);   // a la posición del audio
        Assert.Equal(1.0, d2.Speed);
    }

    // ------------------------------------------------------------------
    // Vídeo ATRASADO: hard-seek adelante sostenido
    // ------------------------------------------------------------------

    [Fact]
    public void VideoAtrasado_Sostenido2Ticks_HardSeekAdelante()
    {
        var pll = new MpvSyncPll();
        var d1 = pll.Tick(9.0, 10.0, Dur);
        Assert.Null(d1.HardSeekTo);          // 1er tick: pudo ser espurio
        Assert.Equal(1.0, d1.Speed);
        var d2 = pll.Tick(9.4, 10.4, Dur);
        Assert.Equal(10.4, d2.HardSeekTo!.Value, 3);
        Assert.Equal(1.0, d2.Speed);
    }

    [Fact]
    public void DriftGrandeTransitorio_QueSeCorrigeSolo_NoHaceSeek()
    {
        var pll = new MpvSyncPll();
        var d1 = pll.Tick(9.0, 10.0, Dur);   // lectura espuria
        Assert.Null(d1.HardSeekTo);
        var d2 = pll.Tick(10.0, 10.0, Dur);  // la siguiente ya es sana
        Assert.Null(d2.HardSeekTo);
        Assert.Equal(1.0, d2.Speed);
    }

    // ------------------------------------------------------------------
    // Guarda de frontera de loop
    // ------------------------------------------------------------------

    [Fact]
    public void FronteraDeLoop_Espera3Ticks_AntesDeActuar()
    {
        // Clip de 4 s en loop: el vídeo dio la vuelta (0.2 s) y el audio aún
        // no (3.8 s) → |d| = 3.6 > min(1, 4/2) = 1: probable cruce de pasada.
        var pll = new MpvSyncPll();
        var d1 = pll.Tick(0.2, 3.8, 4.0, loop: true);
        Assert.Null(d1.HardSeekTo);          // guarda: no rebobinar la pantalla
        // Al tick siguiente el audio también cruzó: drift real minúsculo —
        // el falso drift gigante se resolvió SOLO, sin seek.
        var d2 = pll.Tick(0.61, 0.60, 4.0, loop: true);
        Assert.Null(d2.HardSeekTo);
        Assert.Equal(1.0, d2.Speed);         // 10 ms: zona muerta
    }

    [Fact]
    public void FronteraDeLoop_SiPersiste_CorrigeAlTercerTick()
    {
        var pll = new MpvSyncPll();
        var d1 = pll.Tick(0.2, 3.0, 4.0, loop: true);   // |d| = 2.8 > 1 → guarda
        Assert.Null(d1.HardSeekTo);
        var d2 = pll.Tick(0.6, 3.4, 4.0, loop: true);
        Assert.Null(d2.HardSeekTo);          // 2º tick: la guarda sigue
        var d3 = pll.Tick(1.0, 3.8, 4.0, loop: true);   // 3º: no era un cruce
        Assert.Equal(3.8, d3.HardSeekTo!.Value, 3);
    }

    [Fact]
    public void SinLoop_LaGuardaNoAplica_SeekAlSegundoTick()
    {
        // El mismo drift gigante en un cue SIN loop no puede ser un cruce de
        // pasada: el ciclo sostenido normal (2 ticks) basta.
        var pll = new MpvSyncPll();
        var d1 = pll.Tick(0.2, 3.0, 4.0);
        Assert.Null(d1.HardSeekTo);
        var d2 = pll.Tick(0.6, 3.4, 4.0);
        Assert.Equal(3.4, d2.HardSeekTo!.Value, 3);
    }

    // ------------------------------------------------------------------
    // Reposo tras un hard-seek
    // ------------------------------------------------------------------

    [Fact]
    public void TrasUnSeek_DosTicksDeReposo_AunqueElDriftParezcaEnorme()
    {
        var pll = new MpvSyncPll();
        pll.Tick(9.0, 10.0, Dur);
        var seek = pll.Tick(9.4, 10.4, Dur);
        Assert.NotNull(seek.HardSeekTo);

        // mpv aún asentando el seek: time-pos rancia → NO reaccionar
        var c1 = pll.Tick(9.4, 10.8, Dur);
        Assert.Null(c1.HardSeekTo);
        Assert.Equal(1.0, c1.Speed);
        var c2 = pll.Tick(9.4, 11.2, Dur);
        Assert.Null(c2.HardSeekTo);

        // Pasado el reposo, si el drift sigue, vuelve el ciclo sostenido
        var c3 = pll.Tick(11.0, 12.0, Dur);
        Assert.Null(c3.HardSeekTo);          // 1er tick del nuevo ciclo
        var c4 = pll.Tick(11.4, 12.4, Dur);
        Assert.NotNull(c4.HardSeekTo);
    }
}
