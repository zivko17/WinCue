// =============================================================================
// ShowClockTests.cs — El reloj único del show, en aislamiento
// =============================================================================
// La clase es pura: se prueba con el reloj VIRTUAL (ManualShowClock), sin
// esperas reales y sin margen de tolerancia. Lo que se comprueba aquí es el
// contrato del que dependen el secuenciador y la UI:
//   · monotonía (no retrocede),
//   · la pausa congela el TIEMPO DE SHOW pero NO el de pared,
//   · reanudar no salta ni retrocede: Now retoma donde se quedó.
// =============================================================================

namespace WinCue.Controller.Tests;

public class ShowClockTests
{
    [Fact]
    public void SinPausas_ElTiempoDeShowEsElDePared()
    {
        var clock = new ManualShowClock();
        clock.Advance(2.5);

        Assert.Equal(2.5, clock.NowRaw, 9);
        Assert.Equal(2.5, clock.Now, 9);
        Assert.False(clock.IsFrozen);
    }

    [Fact]
    public void Freeze_CongelaElShow_PeroElRelojDeParedSigue()
    {
        var clock = new ManualShowClock();
        clock.Advance(2.0);
        clock.Freeze();

        clock.Advance(5.0);

        Assert.True(clock.IsFrozen);
        Assert.Equal(2.0, clock.Now, 9);      // el show no se movió
        Assert.Equal(7.0, clock.NowRaw, 9);   // la pared sí (plazos y antirrebotes)
    }

    [Fact]
    public void Resume_RetomaSinSaltoNiRetroceso()
    {
        var clock = new ManualShowClock();
        clock.Advance(2.0);
        clock.Freeze();
        clock.Advance(5.0);

        clock.Resume();
        Assert.Equal(2.0, clock.Now, 9);      // justo donde se congeló

        clock.Advance(1.0);
        Assert.Equal(3.0, clock.Now, 9);      // y sigue contando desde ahí
    }

    [Fact]
    public void VariasPausas_SeAcumulanBien()
    {
        var clock = new ManualShowClock();
        clock.Advance(1.0);

        for (int i = 0; i < 3; i++)
        {
            clock.Freeze();
            clock.Advance(10.0);   // 30 s de pausa en total
            clock.Resume();
            clock.Advance(1.0);    // 3 s de show en total
        }

        Assert.Equal(4.0, clock.Now, 9);
        Assert.Equal(34.0, clock.NowRaw, 9);
    }

    [Fact]
    public void FreezeYResume_SonIdempotentes()
    {
        var clock = new ManualShowClock();
        clock.Advance(1.0);

        clock.Resume();            // no estaba congelado: no hace nada
        Assert.Equal(1.0, clock.Now, 9);

        clock.Freeze();
        clock.Advance(3.0);
        clock.Freeze();            // segunda congelación: no reinicia la cuenta
        Assert.Equal(1.0, clock.Now, 9);

        clock.Resume();
        clock.Resume();            // segunda reanudación: inofensiva
        Assert.Equal(1.0, clock.Now, 9);
    }

    /// <summary>El reloj AVISA de cada transición: es por donde el planificador
    /// se entera de que tiene que suspender o re-armar sus plazos. Solo en la
    /// transición — un Freeze repetido no vuelve a avisar, o el planificador
    /// recalcularía el resto de cada plazo contra un ahora ya pasado.</summary>
    [Fact]
    public void LasTransiciones_AvisanUnaSolaVez()
    {
        var clock = new ManualShowClock();
        int congelados = 0, reanudados = 0;
        clock.Frozen += () => congelados++;
        clock.Resumed += () => reanudados++;

        clock.Resume();            // no estaba congelado: no hay transición
        Assert.Equal(0, reanudados);

        clock.Freeze();
        clock.Freeze();            // idempotente: un solo aviso
        Assert.Equal(1, congelados);

        clock.Resume();
        clock.Resume();
        Assert.Equal(1, reanudados);
    }

    /// <summary>El aviso llega con el reloj YA en su nuevo estado: el
    /// planificador consulta NowRaw dentro del manejador para calcular lo que
    /// le falta a cada plazo.</summary>
    [Fact]
    public void ElAviso_LlegaConElRelojYaEnSuNuevoEstado()
    {
        var clock = new ManualShowClock();
        clock.Advance(3.0);
        bool congeladoAlAvisar = false, descongeladoAlAvisar = false;
        clock.Frozen += () => congeladoAlAvisar = clock.IsFrozen;
        clock.Resumed += () => descongeladoAlAvisar = !clock.IsFrozen;

        clock.Freeze();
        clock.Resume();

        Assert.True(congeladoAlAvisar);
        Assert.True(descongeladoAlAvisar);
    }

    [Fact]
    public void ElRelojNuncaRetrocede()
    {
        var clock = new ManualShowClock();
        clock.Advance(5.0);

        clock.Advance(-3.0);       // avance negativo: ignorado
        clock.SetRaw(1.0);         // colocar en el pasado: ignorado

        Assert.Equal(5.0, clock.NowRaw, 9);
    }

    [Fact]
    public void RelojReal_EsMonotonoYAvanza()
    {
        var clock = new ShowClock();
        double a = clock.NowRaw;
        Thread.Sleep(20);
        double b = clock.NowRaw;

        Assert.True(b > a, "el reloj real no avanzó");
        Assert.True(a >= 0.0);
    }

    [Fact]
    public void RelojReal_LaPausaCongelaElTiempoDeShow()
    {
        var clock = new ShowClock();
        clock.Freeze();
        double congelado = clock.Now;
        Thread.Sleep(40);

        Assert.Equal(congelado, clock.Now, 6);   // el show, quieto
        clock.Resume();
        Assert.Equal(congelado, clock.Now, 3);   // y retoma donde estaba
    }
}
