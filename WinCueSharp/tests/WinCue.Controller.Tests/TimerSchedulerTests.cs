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
}
