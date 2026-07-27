// =============================================================================
// TimerSchedulerTests.cs — El scheduler REAL de producción (auditoría 03)
// =============================================================================
// Estaba a 0%: toda la temporización de un show en directo pasa por esta
// clase y jamás se había ejecutado en un test (el reloj virtual la sustituye).
// Tests de tiempo real con esperas cortas y márgenes GENEROSOS (nunca se
// asserta sobre precisión de milisegundos, solo sobre "dispara / no dispara").
// =============================================================================

namespace WinCue.Controller.Tests;

public class TimerSchedulerTests
{
    [Fact]
    public void Schedule_DisparaUnaSolaVez()
    {
        var scheduler = new TimerScheduler();
        using var fired = new ManualResetEventSlim(false);
        int count = 0;

        scheduler.Schedule(30, () => { Interlocked.Increment(ref count); fired.Set(); });

        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)), "el timer no disparó");
        Thread.Sleep(120);                    // margen: ¿re-dispara? (one-shot)
        Assert.Equal(1, count);
    }

    [Fact]
    public void Dispose_AntesDeVencer_CancelaElDisparo()
    {
        var scheduler = new TimerScheduler();
        int count = 0;

        var handle = scheduler.Schedule(80, () => Interlocked.Increment(ref count));
        handle.Dispose();                     // cancelar en caliente

        Thread.Sleep(250);                    // margen sobrado tras el vencimiento
        Assert.Equal(0, count);
    }

    [Fact]
    public void Dispatch_RemiteElCallbackPorElDespachador()
    {
        // En producción el dispatch es dispatcher.BeginInvoke (hilo de UI);
        // aquí un despachador de mentira que registra por dónde pasó.
        using var fired = new ManualResetEventSlim(false);
        int dispatched = 0;
        var scheduler = new TimerScheduler(action =>
        {
            Interlocked.Increment(ref dispatched);
            action();
        });

        scheduler.Schedule(20, fired.Set);

        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)), "el timer no disparó");
        Assert.Equal(1, dispatched);          // pasó por el despachador
    }

    [Fact]
    public void Dispose_TrasElDisparo_EsInofensivo()
    {
        var scheduler = new TimerScheduler();
        using var fired = new ManualResetEventSlim(false);

        var handle = scheduler.Schedule(10, fired.Set);
        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)));

        handle.Dispose();                     // no lanza ni rompe nada
    }

    // ------------------------------------------------------------------
    // La pausa global, en el planificador DE PRODUCCIÓN
    // ------------------------------------------------------------------

    /// <summary>Con el show en pausa, un plazo vivo se desarma: no puede
    /// vencer con los motores parados. Al reanudar se re-arma con lo que le
    /// faltaba. Márgenes generosos: solo se comprueba "dispara / no dispara".</summary>
    [Fact]
    public void PausaGlobal_SuspendeUnPlazoVivo_YReanudarLoRearma()
    {
        var scheduler = new TimerScheduler();
        using var fired = new ManualResetEventSlim(false);

        using var handle = scheduler.Schedule(600, fired.Set);
        scheduler.Clock.Freeze();             // muy dentro de los 600 ms

        Assert.False(fired.Wait(TimeSpan.FromSeconds(1)),
            "disparó con el show en pausa");
        scheduler.Clock.Resume();
        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)),
            "no se re-armó al reanudar");
    }

    /// <summary>Un plazo programado YA en pausa nace congelado (un cue
    /// disparado durante la pausa no gasta su pre_wait con el sonido
    /// parado). Sin esperas de por medio: el estado se decide al crearlo.</summary>
    [Fact]
    public void PlazoProgramadoEnPausa_NaceCongelado()
    {
        var scheduler = new TimerScheduler();
        scheduler.Clock.Freeze();
        using var fired = new ManualResetEventSlim(false);

        using var handle = scheduler.Schedule(10, fired.Set);

        Assert.False(fired.Wait(TimeSpan.FromMilliseconds(300)),
            "disparó con el show en pausa");
        scheduler.Clock.Resume();
        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)),
            "no se re-armó al reanudar");
    }

    /// <summary>La excepción: un plazo de 0 ms es un despacho al hilo del
    /// controlador, no un plazo del show. La pausa no lo toca.</summary>
    [Fact]
    public void DespachoDeCeroMs_NoLoCongelaLaPausa()
    {
        var scheduler = new TimerScheduler();
        scheduler.Clock.Freeze();
        using var fired = new ManualResetEventSlim(false);

        scheduler.Schedule(0, fired.Set);

        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)),
            "el despacho se quedó colgado en la pausa");
    }

    /// <summary>Cancelar un plazo suspendido no lo resucita al reanudar.</summary>
    [Fact]
    public void DisposeDeUnPlazoSuspendido_NoResucitaAlReanudar()
    {
        var scheduler = new TimerScheduler();
        int count = 0;

        var handle = scheduler.Schedule(120, () => Interlocked.Increment(ref count));
        scheduler.Clock.Freeze();
        handle.Dispose();
        scheduler.Clock.Resume();

        Thread.Sleep(400);                    // margen sobrado tras el vencimiento
        Assert.Equal(0, count);
    }
}
