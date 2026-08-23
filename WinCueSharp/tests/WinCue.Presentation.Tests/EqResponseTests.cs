// =============================================================================
// EqResponseTests.cs — respuesta en frecuencia de la cadena de EQ (biquads
// RBJ), extraída de WinCue.Audio.EqChain para compartirla con la curva del
// editor (WinCue.App/Secuence.App).
// =============================================================================
// La respuesta de los filtros es matemática: estos valores fijan que
// EqResponse da EXACTAMENTE lo que EqChain.Configure/Process aplican de
// verdad (ver también tests/WinCue.Audio.Tests/EqChainTests.cs, que sigue
// verificando el comportamiento de audio en sí — aquí se fija la curva).
// =============================================================================

using WinCue.Core;
using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class EqResponseTests
{
    // ═════════════════════════ Sin nada activo ══════════════════════════════

    [Fact]
    public void SinEqNiFiltros_RespuestaPlanaAGananciaDeClip()
    {
        var eq = new AudioEqParams(GainDb: 3.0);

        Assert.Equal(3.0, EqResponse.ResponseDb(eq, 20), 6);
        Assert.Equal(3.0, EqResponse.ResponseDb(eq, 1000), 6);
        Assert.Equal(3.0, EqResponse.ResponseDb(eq, 20000), 6);
    }

    // ═════════════════════════ Banda paramétrica (peaking) ══════════════════

    [Fact]
    public void BandaPeaking_SubeExactamenteSuGananciaEnElCentro()
    {
        var eq = new AudioEqParams(EqEnabled: true, Bands: new[] { new EqBand(1000, 6.0, 1.0) });

        Assert.Equal(6.0, EqResponse.ResponseDb(eq, 1000), 1);
    }

    [Fact]
    public void BandaPeaking_EsTransparenteLejosDeSuFrecuencia()
    {
        var eq = new AudioEqParams(EqEnabled: true, Bands: new[] { new EqBand(1000, 6.0, 1.0) });

        Assert.Equal(0.0, EqResponse.ResponseDb(eq, 20), 1);
        Assert.Equal(0.0, EqResponse.ResponseDb(eq, 20000), 0);
    }

    [Fact]
    public void BandaConGananciaDespreciable_NiSeInstancia()
    {
        // |gain| <= 0.01 dB no genera etapa (ver Coefficients) — comprobado
        // indirectamente: la respuesta es 0 dB en su propia frecuencia.
        var eq = new AudioEqParams(EqEnabled: true, Bands: new[] { new EqBand(1000, 0.005, 1.0) });

        Assert.Equal(0.0, EqResponse.ResponseDb(eq, 1000), 3);
    }

    [Fact]
    public void EqDeshabilitada_IgnoraLasBandasAunqueTenganGanancia()
    {
        var eq = new AudioEqParams(EqEnabled: false, Bands: new[] { new EqBand(1000, 12.0, 1.0) });

        Assert.Equal(0.0, EqResponse.ResponseDb(eq, 1000), 6);
    }

    [Fact]
    public void CuatroBandasSeSuman()
    {
        // Cuatro bandas EN LA MISMA frecuencia con Q bajo (poco solapamiento
        // con ellas mismas): la respuesta en esa frecuencia es, a efectos
        // prácticos, la suma de las cuatro ganancias.
        var eq = new AudioEqParams(EqEnabled: true, Bands: new[]
        {
            new EqBand(1000, 3.0, 2.0),
            new EqBand(1000, 3.0, 2.0),
            new EqBand(1000, 3.0, 2.0),
            new EqBand(1000, 3.0, 2.0),
        });

        Assert.Equal(12.0, EqResponse.ResponseDb(eq, 1000), 1);
    }

    // ═════════════════════════ HPF / LPF ═════════════════════════════════════

    [Fact]
    public void Hpf_AtenuaMuyPorDebajoDeSuCorte()
    {
        var eq = new AudioEqParams(HpfOn: true, HpfFreq: 200);

        // Butterworth Q=0.7071, 12 dB/oct: dos octavas por debajo (50 Hz) ya
        // debería estar claramente atenuado.
        Assert.True(EqResponse.ResponseDb(eq, 50) < -15.0);
        // Muy por encima del corte, prácticamente plano (0 dB).
        Assert.True(Math.Abs(EqResponse.ResponseDb(eq, 5000)) < 0.5);
    }

    [Fact]
    public void Hpf_MenosTresDbEnLaFrecuenciaDeCorte()
    {
        // -3 dB (±0.5) es el punto de corte de un filtro Butterworth Q=0.7071.
        var eq = new AudioEqParams(HpfOn: true, HpfFreq: 200);

        Assert.InRange(EqResponse.ResponseDb(eq, 200), -3.5, -2.5);
    }

    [Fact]
    public void Lpf_AtenuaMuyPorEncimaDeSuCorte()
    {
        var eq = new AudioEqParams(LpfOn: true, LpfFreq: 500);

        Assert.True(EqResponse.ResponseDb(eq, 8000) < -30.0);
        Assert.True(Math.Abs(EqResponse.ResponseDb(eq, 50)) < 0.5);
    }

    [Fact]
    public void Lpf_MenosTresDbEnLaFrecuenciaDeCorte()
    {
        var eq = new AudioEqParams(LpfOn: true, LpfFreq: 500);

        Assert.InRange(EqResponse.ResponseDb(eq, 500), -3.5, -2.5);
    }

    [Fact]
    public void HpfYLpfActuanAunqueLaEqEsteApagada()
    {
        // Mismo comportamiento que EqChain.Coefficients: HPF/LPF no dependen
        // de eq_enabled, solo de su propio on/off (ver también
        // MpvAfChainTests.HpfLpf_ApplyEvenWithEqDisabled_LikeEqChain, del lado
        // del motor de vídeo).
        var eq = new AudioEqParams(EqEnabled: false, HpfOn: true, HpfFreq: 200);

        Assert.True(EqResponse.ResponseDb(eq, 50) < -10.0);
    }

    // ═════════════════════════ Cadena completa ═══════════════════════════════

    [Fact]
    public void CadenaCompleta_SumaGananciaDeClipMasFiltrosMasBandas()
    {
        var eq = new AudioEqParams(
            GainDb: 2.0,
            EqEnabled: true,
            HpfOn: true, HpfFreq: 40,      // muy grave: transparente a 5 kHz
            Bands: new[] { new EqBand(5000, 4.0, 2.0) });

        Assert.Equal(6.0, EqResponse.ResponseDb(eq, 5000), 1);   // 2 (clip) + 4 (banda)
    }
}
