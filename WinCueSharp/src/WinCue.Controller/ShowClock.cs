// =============================================================================
// ShowClock.cs — LA base de tiempo del SHOW (una sola, para todo el proyecto)
// =============================================================================
//
// POR QUÉ EXISTE
//   Antes convivían tres familias de reloj que no se hablaban:
//     1. el planificador (IScheduler) decidía CUÁNDO se dispara cada cosa,
//     2. el controlador medía elapsed/remaining con Stopwatch por su cuenta,
//     3. la UI contaba con DispatcherTimer (prioridad baja en WPF).
//   En un PC ocioso los tres coinciden; en un PC CARGADO —que es donde va a
//   correr el show del cliente— divergen: lo que se ve en pantalla y lo que
//   hace el secuenciador dejan de ser lo mismo. Este reloj es el único origen
//   de tiempo del SHOW: secuencia (plazos) y presentación (cuentas) leen de
//   aquí, así que no pueden discrepar.
//
// LA FRONTERA CON LOS RELOJES DE MEDIA — LÍMITE INNEGOCIABLE
//   Este reloj NO gobierna la reproducción. El audio no se puede esclavizar a
//   un reloj de software: el cristal de la tarjeta decide cuándo salen las
//   muestras, y el vídeo se disciplina a ese audio. Siguen mandando en lo suyo,
//   sin tocar:
//     · AudioEngine.GetElapsedThreadSafe — reloj maestro del motor HAP
//     · HapMediaClock                    — extrapolación monotónica del cue
//     · MpvSyncPll / ObservedTimePosSmooth — PLL de la ruta híbrida
//   El ShowClock gobierna la SECUENCIA (cuándo dispara cada cue) y la
//   PRESENTACIÓN (qué números se ven). La POSICIÓN DENTRO DE UN MEDIA la sigue
//   dando el motor: SnapshotActiveCues prefiere el elapsed del motor cuando lo
//   hay, y los plazos de fin de un media se anclan a su arranque REAL, no al
//   ideal (ver MainController.StartAction). Anclar el fin de un audio al
//   instante teórico lo cortaría antes de que suene entero.
//
// DOS LECTURAS, A PROPÓSITO
//   · NowRaw — segundos monótonos, NO se congela nunca. Es el eje de los
//     PLAZOS del planificador y de los antirrebotes de la botonera (doble GO,
//     doble PANIC): son gestos de mano, cuentan en tiempo de pared.
//   · Now — segundos de SHOW: NowRaw menos lo que el show ha estado en pausa.
//     Es el eje de la MEDICIÓN y la PRESENTACIÓN (elapsed/remaining de los
//     cues, reloj grande, cursor del timeline). Al congelarse en la pausa, los
//     números se quedan quietos sin tener que corregir marcas hacia atrás.
//   La pausa global NO congela los plazos ya programados: es exactamente el
//   comportamiento que había antes de este reloj y se conserva a propósito
//   (ver docs/arquitectura-reloj.md, "Lo que NO hace").
//
// MONOTONÍA
//   La base es Stopwatch (contador de alta resolución del sistema), no
//   DateTime: un cambio de hora del sistema o un salto de NTP en mitad del
//   show no mueve ni un milisegundo lo que ya está programado.
// =============================================================================

using System.Diagnostics;

namespace WinCue.Controller;

/// <summary>Base de tiempo única del show. Inyectable: en producción
/// <see cref="ShowClock"/> (Stopwatch), en tests <see cref="ManualShowClock"/>
/// (tiempo virtual, determinista).</summary>
public interface IShowClock
{
    /// <summary>Segundos monótonos desde el arranque del reloj. NUNCA se
    /// congela: es el eje de los PLAZOS absolutos del planificador.</summary>
    double NowRaw { get; }

    /// <summary>Segundos de SHOW: <see cref="NowRaw"/> menos el tiempo que el
    /// show ha pasado en pausa. Es el eje de la MEDICIÓN y la PRESENTACIÓN.</summary>
    double Now { get; }

    /// <summary>true si el tiempo de show está congelado (pausa global).</summary>
    bool IsFrozen { get; }

    /// <summary>Congela el tiempo de show. Idempotente.</summary>
    void Freeze();

    /// <summary>Reanuda el tiempo de show SIN saltos: al reanudar,
    /// <see cref="Now"/> vale exactamente lo que valía al congelar.</summary>
    void Resume();
}

/// <summary>Contabilidad común de la pausa. Las implementaciones solo aportan
/// <see cref="NowRaw"/>.</summary>
public abstract class ShowClockBase : IShowClock
{
    private readonly object _gate = new();
    private double _pausedTotal;   // segundos acumulados en pausa
    private double _frozenAt;      // valor de Now en el instante de congelar
    private bool _frozen;

    public abstract double NowRaw { get; }

    public double Now
    {
        get { lock (_gate) return _frozen ? _frozenAt : NowRaw - _pausedTotal; }
    }

    public bool IsFrozen
    {
        get { lock (_gate) return _frozen; }
    }

    public void Freeze()
    {
        lock (_gate)
        {
            if (_frozen)
                return;
            _frozenAt = NowRaw - _pausedTotal;
            _frozen = true;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_frozen)
                return;
            // Todo lo que ha corrido el reloj crudo desde el origen menos lo
            // que valía el show al congelar = tiempo total en pausa. Así Now
            // retoma EXACTAMENTE donde se quedó: ni salto ni retroceso.
            _pausedTotal = NowRaw - _frozenAt;
            _frozen = false;
        }
    }
}

/// <summary>Reloj real: Stopwatch de alta resolución. Seguro entre hilos (el
/// planificador lo consulta desde el hilo de sus timers).</summary>
public sealed class ShowClock : ShowClockBase
{
    private readonly long _t0 = Stopwatch.GetTimestamp();

    public override double NowRaw =>
        (double)(Stopwatch.GetTimestamp() - _t0) / Stopwatch.Frequency;
}

/// <summary>Reloj de tiempo VIRTUAL: solo avanza cuando alguien lo empuja. Es
/// la costura de determinismo de los tests (la usa el scheduler virtual) y del
/// banco de deriva. Vive en el ensamblado de producción por la misma razón que
/// <see cref="IScheduler"/>: la costura es parte del diseño, no del test.</summary>
public sealed class ManualShowClock : ShowClockBase
{
    private double _raw;

    public override double NowRaw => _raw;

    /// <summary>Avanza el reloj virtual (nunca hacia atrás).</summary>
    public void Advance(double seconds) => _raw += Math.Max(0.0, seconds);

    /// <summary>Coloca el reloj virtual en un instante absoluto. Ignora los
    /// intentos de retroceder: un reloj monótono no vuelve atrás.</summary>
    public void SetRaw(double seconds) => _raw = Math.Max(_raw, seconds);
}
