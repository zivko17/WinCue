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
//
// LA PAUSA GLOBAL CONGELA LOS PLAZOS
//   El planificador escucha el Freeze/Resume del reloj del show. Al congelar
//   guarda lo que le falta a cada timer vivo y lo desarma; al reanudar lo
//   vuelve a armar con ese resto. Sin esto, la pausa paraba los motores pero
//   NO los relojes que los cortan: un cue con duration fija al que le pillaba
//   una pausa larga se cortaba antes de tiempo.
//   Se congela TODO lo que es tiempo de show, tanto los plazos absolutos de la
//   secuencia como los relativos que cuelgan de un motor (fin de un media,
//   reintentos corregidos contra su posición): con el motor parado, su reloj
//   tampoco avanza. La FRONTERA no se toca — el fin de un media se sigue
//   anclando a su arranque REAL y corrigiendo contra la posición del motor; lo
//   único que cambia es que ese plazo deja de correr mientras el show está en
//   pausa.
//   ÚNICA EXCEPCIÓN: Schedule(0). Un plazo de cero no es un plazo, es un
//   despacho al hilo del controlador (HapConvertService.Post). Congelarlo
//   dejaría colgado hasta la reanudación un mensaje de servicio que no tiene
//   nada que ver con el show.
// =============================================================================

namespace WinCue.Controller;

public interface IScheduler
{
    /// <summary>Reloj del show al que están anclados los plazos. Es el MISMO
    /// que usan el controlador y la UI para medir y presentar tiempos.</summary>
    IShowClock Clock { get; }

    /// <summary>Programa fn una sola vez tras ms milisegundos.
    /// Dispose del handle cancela el disparo. Se congela con la pausa global,
    /// salvo ms = 0, que es un despacho al hilo del controlador y no un plazo.</summary>
    IDisposable Schedule(int ms, Action fn);

    /// <summary>Programa fn para el instante ABSOLUTO deadline (segundos de
    /// <see cref="IShowClock.NowRaw"/>). Un plazo ya vencido dispara cuanto
    /// antes. Se congela con la pausa global. Dispose del handle cancela el
    /// disparo.</summary>
    IDisposable ScheduleAt(double deadline, Action fn);
}

/// <summary>Scheduler real: System.Threading.Timer + dispatcher opcional.</summary>
public sealed class TimerScheduler : IScheduler
{
    private readonly Action<Action>? _dispatch;

    // Plazos VIVOS (los congelables que aún no han disparado ni se han
    // cancelado). El cerrojo los protege del hilo de los timers: al congelar
    // hay que decidir "este plazo se suspende" o "este ya disparó" sin que
    // ambas cosas ocurran a la vez — si no, un plazo que vence justo al pausar
    // dispararía ahora Y otra vez al reanudar.
    private readonly object _gate = new();
    private readonly HashSet<TimerHandle> _vivos = new();
    private bool _congelado;

    public IShowClock Clock { get; }

    /// <param name="dispatch">Remite el callback al main thread (en WPF:
    /// a => dispatcher.BeginInvoke(a)). null → se invoca en el hilo del timer.</param>
    /// <param name="clock">Reloj del show. null → uno propio (lo normal: la app
    /// crea el scheduler primero y todo lo demás lee su Clock).</param>
    public TimerScheduler(Action<Action>? dispatch = null, IShowClock? clock = null)
    {
        _dispatch = dispatch;
        Clock = clock ?? new ShowClock();
        // Suscribirse ANTES de leer el estado: al revés, una pausa que cayera
        // justo en medio no la vería nadie y el planificador se quedaría
        // creyendo que el show corre.
        Clock.Frozen += Congelar;
        Clock.Resumed += Descongelar;
        _congelado = Clock.IsFrozen;
    }

    public IDisposable Schedule(int ms, Action fn) =>
        // ms = 0 no es un plazo: es un despacho al hilo del controlador. La
        // pausa congela plazos, no despachos (ver la cabecera).
        Armar(Clock.NowRaw + Math.Max(0, ms) / 1000.0, fn, congelable: ms > 0);

    public IDisposable ScheduleAt(double deadline, Action fn) =>
        Armar(deadline, fn, congelable: true);

    /// <summary>Milisegundos que faltan para un plazo absoluto, saneados: ya
    /// vencido → 0 (disparar cuanto antes), y sin desbordar el int del timer.</summary>
    internal static int DelayMs(double deadline, IShowClock clock)
    {
        double ms = (deadline - clock.NowRaw) * 1000.0;
        if (double.IsNaN(ms) || ms <= 0.0)
            return 0;
        return ms >= int.MaxValue ? int.MaxValue : (int)Math.Round(ms);
    }

    /// <summary>Crea el timer del plazo. Si el show ya está en pausa, el plazo
    /// nace SUSPENDIDO: un cue disparado durante la pausa no debe gastar su
    /// pre_wait mientras los motores están parados.</summary>
    private IDisposable Armar(double deadline, Action fn, bool congelable)
    {
        var handle = new TimerHandle(this, fn);
        lock (_gate)
        {
            handle.Deadline = deadline;
            bool naceEnPausa = congelable && _congelado;
            if (congelable)
                _vivos.Add(handle);
            if (naceEnPausa)
            {
                handle.Restante = Math.Max(0.0, deadline - Clock.NowRaw);
                handle.Suspendido = true;
            }
            else
            {
                handle.Rearmar(DelayMs(deadline, Clock));
            }
        }
        return handle;
    }

    /// <summary>Vence un plazo. Bajo cerrojo decide si de verdad le toca: si
    /// entretanto lo cancelaron o lo congelaron, no dispara (y en el segundo
    /// caso sigue en la lista, así que se re-arma al reanudar).</summary>
    private void Disparar(TimerHandle handle)
    {
        lock (_gate)
        {
            if (handle.Cancelled || handle.Suspendido)
                return;
            _vivos.Remove(handle);   // plazo gastado: ya no es re-armable
        }
        // El fn va FUERA del cerrojo: casi siempre vuelve a programar cosas.
        if (_dispatch is not null)
            _dispatch(() => { if (!handle.Cancelled) handle.Fn(); });
        else
            handle.Fn();
    }

    /// <summary>Pausa global: cada plazo vivo guarda lo que le faltaba y se
    /// desarma. El instante absoluto se recalcula al reanudar — el eje de los
    /// plazos (NowRaw) NO se congela, así que el plazo se DESPLAZA con la
    /// pausa en vez de dejarse contar.</summary>
    private void Congelar()
    {
        lock (_gate)
        {
            if (_congelado)
                return;
            _congelado = true;
            double ahora = Clock.NowRaw;
            foreach (var h in _vivos)
            {
                h.Restante = Math.Max(0.0, h.Deadline - ahora);
                h.Suspendido = true;
                h.Rearmar(Timeout.Infinite);
            }
        }
    }

    private void Descongelar()
    {
        lock (_gate)
        {
            if (!_congelado)
                return;
            _congelado = false;
            double ahora = Clock.NowRaw;
            foreach (var h in _vivos)
            {
                if (!h.Suspendido)
                    continue;
                h.Suspendido = false;
                h.Deadline = ahora + h.Restante;
                h.Rearmar(DelayMs(h.Deadline, Clock));
            }
        }
    }

    private sealed class TimerHandle : IDisposable
    {
        private readonly TimerScheduler _owner;
        private readonly Timer _timer;

        public readonly Action Fn;
        public volatile bool Cancelled;

        // Bajo _owner._gate:
        public double Deadline;    // instante absoluto (NowRaw) del disparo
        public double Restante;    // segundos que le quedaban al congelar
        public bool Suspendido;    // desarmado por la pausa global

        public TimerHandle(TimerScheduler owner, Action fn)
        {
            _owner = owner;
            Fn = fn;
            // Nace parado y se arma después: si se creara ya con su retardo,
            // un plazo de 0 ms podría disparar antes de que el campo _timer
            // estuviera asignado y el Dispose no tendría a quién cancelar.
            _timer = new Timer(_ => _owner.Disparar(this), null,
                Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>(Re)programa el timer. Solo bajo _owner._gate: fuera de él
        /// podría cruzarse con un Dispose y tocar un timer ya liberado.</summary>
        public void Rearmar(int ms) => _timer.Change(ms, Timeout.Infinite);

        public void Dispose()
        {
            Cancelled = true;
            lock (_owner._gate)
            {
                _owner._vivos.Remove(this);
                _timer.Dispose();
            }
        }
    }
}
