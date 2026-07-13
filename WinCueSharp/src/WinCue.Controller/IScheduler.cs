// =============================================================================
// IScheduler.cs — Temporización inyectable (sustituto de QTimer)
// =============================================================================
// El controlador programa retardos (pre_wait, post_wait, duration, cadenas)
// a través de esta interfaz. En producción TimerScheduler usa
// System.Threading.Timer y remite el callback por el dispatcher (hilo de UI);
// en tests un scheduler virtual avanza el tiempo de forma determinista.
// =============================================================================

namespace WinCue.Controller;

public interface IScheduler
{
    /// <summary>Programa fn una sola vez tras ms milisegundos.
    /// Dispose del handle cancela el disparo.</summary>
    IDisposable Schedule(int ms, Action fn);
}

/// <summary>Scheduler real: System.Threading.Timer + dispatcher opcional.</summary>
public sealed class TimerScheduler : IScheduler
{
    private readonly Action<Action>? _dispatch;

    /// <param name="dispatch">Remite el callback al main thread (en WPF:
    /// a => dispatcher.BeginInvoke(a)). null → se invoca en el hilo del timer.</param>
    public TimerScheduler(Action<Action>? dispatch = null) => _dispatch = dispatch;

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
