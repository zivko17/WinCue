// =============================================================================
// HapMediaClockTests.cs — Reloj de media del motor HAP (puro, sin hilos)
// =============================================================================
// La semántica elegida en el paso 3: el AUDIO manda mientras entregue valores
// (re-anclaje + extrapolación monotónica), congelar-y-seguir si desaparece,
// pasadas de loop marcadas por el reset del audio con envolvido por tiempo
// como red de seguridad. El monotónico y el externo se inyectan: determinista.
// =============================================================================

using WinCue.Video.Hap;

namespace WinCue.Video.Tests;

public class HapMediaClockTests
{
    [Fact]
    public void SinAudio_AvanzaConElMonotonico()
    {
        double mono = 0.0;
        var clock = new HapMediaClock(10.0, 1, external: null, monotonic: () => mono);
        clock.Start();

        mono = 1.25;
        var s = clock.Now();
        Assert.Equal(0, s.Pass);
        Assert.Equal(1.25, s.LocalSeconds, 3);
    }

    [Fact]
    public void ElAudioManda_ReanclaCadaValorNuevo()
    {
        double mono = 0.0;
        double? ext = 0.0;
        var clock = new HapMediaClock(10.0, 1, () => ext, () => mono);
        clock.Start();

        mono = 1.0;
        ext = 0.9;               // el audio va un pelín por detrás del reloj
        var s = clock.Now();
        Assert.Equal(0.9, s.LocalSeconds, 3);
    }

    [Fact]
    public void EntreValoresDelAudio_ExtrapolaConElMonotonico()
    {
        double mono = 0.0;
        double? ext = 0.5;
        var clock = new HapMediaClock(10.0, 1, () => ext, () => mono);
        clock.Start();

        _ = clock.Now();          // ancla en 0.5
        mono = 0.08;              // el audio aún no publicó el siguiente búfer
        var s = clock.Now();
        Assert.Equal(0.58, s.LocalSeconds, 3);
    }

    [Fact]
    public void AudioDesaparece_CongelaAnclajeYSigueConElMonotonico()
    {
        double mono = 0.0;
        double? ext = 0.0;
        var clock = new HapMediaClock(30.0, 1, () => ext, () => mono);
        clock.Start();

        mono = 2.0;
        ext = 2.0;
        _ = clock.Now();          // ancla en 2.0

        ext = null;               // el audio acabó antes que el vídeo
        mono = 5.0;
        var s = clock.Now();
        Assert.Equal(5.0, s.LocalSeconds, 3);   // 2.0 + (5.0 − 2.0)
    }

    [Fact]
    public void SaltoAtrasDelAudio_ConLaPasadaAvanzada_EsNuevaPasada()
    {
        double mono = 0.0;
        double? ext = 0.0;
        var clock = new HapMediaClock(10.0, int.MaxValue, () => ext, () => mono);
        clock.Start();

        mono = 9.8;
        ext = 9.8;
        _ = clock.Now();

        mono = 10.1;
        ext = 0.1;                // el loop del audio reinició su elapsed
        var s = clock.Now();
        Assert.Equal(1, s.Pass);
        Assert.Equal(0.1, s.LocalSeconds, 3);
    }

    [Fact]
    public void SinSenalDeAudio_LaPasadaEnvuelvePorTiempo()
    {
        double mono = 0.0;
        var clock = new HapMediaClock(10.0, int.MaxValue, external: null, monotonic: () => mono);
        clock.Start();

        mono = 10.30;             // margen de envolvido superado (10 + 0.25)
        var s = clock.Now();
        Assert.Equal(1, s.Pass);
        Assert.Equal(0.30, s.LocalSeconds, 2);
    }

    [Fact]
    public void UltimaPasada_NoEnvuelve_ElLocalSigueCreciendo()
    {
        double mono = 0.0;
        var clock = new HapMediaClock(10.0, 2, external: null, monotonic: () => mono);
        clock.Start();

        mono = 10.5;
        Assert.Equal(1, clock.Now().Pass);
        mono = 21.0;              // fin de la segunda (y última) pasada
        var s = clock.Now();
        Assert.Equal(1, s.Pass);                    // no hay pasada 2
        Assert.True(s.LocalSeconds > 10.0);         // el pacer verá Finished
    }

    [Fact]
    public void ResetDelAudio_JustoTrasEnvolverPorTiempo_NoDuplicaPasada()
    {
        double mono = 0.0;
        double? ext = 0.0;
        var clock = new HapMediaClock(10.0, int.MaxValue, () => ext, () => mono);
        clock.Start();

        mono = 9.9; ext = 9.9;
        _ = clock.Now();

        ext = null;               // el audio calla justo en el límite
        mono = 10.4;
        Assert.Equal(1, clock.Now().Pass);          // envolvido por tiempo

        ext = 0.2;                // y ahora llega el reset del audio
        mono = 10.5;
        var s = clock.Now();
        Assert.Equal(1, s.Pass);                    // MISMA pasada, re-anclada
        Assert.Equal(0.2, s.LocalSeconds, 3);
    }

    [Fact]
    public void PausaCongelaYReanudaDesdeElMismoPunto()
    {
        double mono = 0.0;
        var clock = new HapMediaClock(10.0, 1, external: null, monotonic: () => mono);
        clock.Start();

        mono = 3.0;
        clock.Pause();
        mono = 7.0;               // el tiempo de pared corre; el reloj no
        Assert.Equal(3.0, clock.Now().LocalSeconds, 3);

        clock.Resume();
        mono = 8.5;
        Assert.Equal(4.5, clock.Now().LocalSeconds, 3);   // 3.0 + 1.5
    }

    [Fact]
    public void SaltoGrandeDelAudio_CuentaComoResync()
    {
        double mono = 0.0;
        double? ext = 0.0;
        var clock = new HapMediaClock(60.0, 1, () => ext, () => mono);
        clock.Start();

        mono = 3.0;
        ext = 1.0;                // el audio arrancó tarde (decode inicial)
        _ = clock.Now();
        Assert.Equal(1, clock.Resyncs);
        Assert.Equal(1.0, clock.Now().LocalSeconds, 3);
    }
}
