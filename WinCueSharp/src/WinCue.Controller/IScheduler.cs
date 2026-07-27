// =============================================================================
// IScheduler.cs — Temporización inyectable (sustituto de QTimer)
// =============================================================================
// El controlador programa retardos (pre_wait, post_wait, duration, cadenas)
// a través de esta interfaz. En producción TimerScheduler usa
// System.Threading.Timer y remite el callback por el dispatcher (hilo de UI);
// en tests un scheduler virtual avanza el tiempo de forma determinista.
//
// PLAZOS ABSOLUTOS (ScheduleAt) — por qué hay dos formas de programar
//   System.Threading.Timer dispara TARDE: unos pocos ms en un PC ocioso, mucho
//   más en el PC cargado del cliente. Si cada eslabón de una cadena se programa
//   "dentro de N ms" DESDE EL DISPARO ANTERIOR (que ya llegó tarde), el retraso
//   se ACUMULA: cien cues encadenados derivan segundos.
//   ScheduleAt recibe el instante ABSOLUTO (segundos de IShowClock.NowRaw) en
//   el que la cosa DEBÍA ocurrir. Cada eslabón se ancla al ideal del anterior,
//   no a su disparo real: el error de un timer no se hereda, y si un plazo
//   llega ya vencido se dispara de inmediato (recuperar, no arrastrar).
//   Schedule(ms) sigue existiendo para lo que NO es secuencia: reintentos
//   corregidos contra la posición real de un motor y despachos a 0 ms.
// =============================================================================

namespace WinCue.Controller;

public interface IScheduler
{
    /// <summary>Reloj del show al que están anclados los plazos. Es el MISMO
    /// que usan el controlador y la UI para medir y presentar tiempos.</summary>
    IShowClock Clock { get; }

    /// <summary>Programa fn una sola vez tras ms milisegundos.
    /// Dispose del handle cancela el disparo.</summary>
    IDisposable Schedule(int ms, Action fn);

    /// <summary>Programa fn para el instante ABSOLUTO deadline (segundos de
    /// <see cref="IShowClock.NowRaw"/>). Un plazo ya vencido dispara cuanto
    /// antes. Dispose del handle cancela el disparo.</summary>
    IDisposable ScheduleAt(double deadline, Action fn);
}

/// <summary>Scheduler real: System.Threading.Timer + dispatcher opcional.</summary>
public sealed class TimerScheduler : IScheduler
{
    private readonly Action<Action>? _dispatch;

    public IShowClock Clock { get; }

    /// <param name="dispatch">Remite el callback al main thread (en WPF:
    /// a => dispatcher.BeginInvoke(a)). null → se invoca en el hilo del timer.</param>
    /// <param name="clock">Reloj del show. null → uno propio (lo normal: la app
    /// crea el scheduler primero y todo lo demás lee su Clock).</param>
    public TimerScheduler(Action<Action>? dispatch = null, IShowClock? clock = null)
    {
        _dispatch = dispatch;
        Clock = clock ?? new ShowClock();
    }

    public IDisposable Schedule(int ms, Action fn)
    {
        var handle = new TimerHandle();
        var timer = new Timer(_ =>
        {
            if (handle.Cancelled) return;
            if (_dispatch is not null)
                _dispatch(() => { if (!handle.Cancelled) fn(); });
            else
                fn();
        }, null, Math.Max(0, ms), Timeout.Infinite);
        handle.Timer = timer;
        return handle;
    }

    public IDisposable ScheduleAt(double deadline, Action fn) =>
        Schedule(DelayMs(deadline, Clock), fn);

    /// <summary>Milisegundos que faltan para un plazo absoluto, saneados: ya
    /// vencido → 0 (disparar cuanto antes), y sin desbordar el int del timer.</summary>
    internal static int DelayMs(double deadline, IShowClock clock)
    {
        double ms = (deadline - clock.NowRaw) * 1000.0;
        if (double.IsNaN(ms) || ms <= 0.0)
            return 0;
        return ms >= int.MaxValue ? int.MaxValue : (int)Math.Round(ms);
    }

    private sealed class TimerHandle : IDisposable
    {
        public volatile bool Cancelled;
        public Timer? Timer;

        public void Dispose()
        {
            Cancelled = true;
            Timer?.Dispose();
        }
    }
}
