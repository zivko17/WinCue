// =============================================================================
// MpvPlayer.cs — Wrapper seguro de UNA instancia de libmpv
// =============================================================================
// Sustituye a mpv.MPV de python-mpv. Cada player tiene su propio handle mpv y
// su hilo de eventos (mpv_wait_event). Los eventos se exponen como eventos C#
// que se disparan EN EL HILO DE EVENTOS de mpv — el consumidor (VideoEngine)
// es responsable de remitirlos al main thread.
//
// CICLO DE VIDA
//   new MpvPlayer(options) → mpv_create + opciones + mpv_initialize + hilo
//   Play(path)             → loadfile
//   Terminate()/Dispose()  → stop + señal al hilo + mpv_terminate_destroy
// =============================================================================

using static WinCue.Video.MpvNative;

namespace WinCue.Video;

// CA2216 (finalizador de respaldo): NO añadir. mpv_terminate_destroy exige que
// antes haya parado el hilo de eventos (Dispose lo hace con Join); un
// finalizador en el hilo del GC correría en paralelo con ese hilo → crash
// nativo. La liberación es determinista vía Dispose()/Terminate().
#pragma warning disable CA2216
public sealed class MpvPlayer : IDisposable
{
#pragma warning restore CA2216
    private readonly nint _handle;
    private readonly Thread _eventThread;
    private volatile bool _shutdown;
    private bool _disposed;

    private const ulong EofReachedUserdata = 0xE0F;

    // Eventos (¡se disparan en el hilo de eventos de mpv!)
    public event Action? FileLoaded;
    public event Action? PlaybackRestart;   // primer frame visible / tras seek
    /// <summary>Fin natural de archivo. Con keep-open=yes mpv NO emite
    /// MPV_EVENT_END_FILE al llegar al final (pausa en el último frame), así
    /// que también se observa la propiedad eof-reached y se unifican ambas
    /// señales aquí.</summary>
    public event Action? EndFile;

    /// <summary>True si libmpv-2.dll está disponible en este sistema.</summary>
    public static bool IsAvailable { get; } = ProbeAvailable();

    /// <summary>Handle nativo del core (para el render context del compositor).</summary>
    internal nint NativeHandle => _handle;

    /// <summary>
    /// Crea e inicializa una instancia mpv con las opciones dadas.
    /// Lanza InvalidOperationException si mpv_create/initialize fallan.
    /// </summary>
    public MpvPlayer(IReadOnlyDictionary<string, string> options)
    {
        _handle = mpv_create();
        if (_handle == 0)
            throw new InvalidOperationException("mpv_create devolvió NULL.");

        foreach (var (name, value) in options)
        {
            int rc = mpv_set_option_string(_handle, name, value);
            if (rc < 0)
            {
                // Opción no crítica inválida: continuar (igual que python-mpv
                // con opciones desconocidas en versiones antiguas de libmpv)
                if (name is "wid" or "vo")
                {
                    mpv_terminate_destroy(_handle);
                    throw new InvalidOperationException(
                        $"mpv rechazó la opción {name}={value}: {ErrorString(rc)}");
                }
            }
        }

        int init = mpv_initialize(_handle);
        if (init < 0)
        {
            mpv_terminate_destroy(_handle);
            throw new InvalidOperationException($"mpv_initialize falló: {ErrorString(init)}");
        }

        // eof-reached: única señal fiable de fin natural con keep-open=yes
        _ = mpv_observe_property(_handle, EofReachedUserdata, "eof-reached", MpvFormat.Flag);

        _eventThread = new Thread(EventLoop)
        {
            IsBackground = true,
            Name = "mpv-events",
        };
        _eventThread.Start();
    }

    // ------------------------------------------------------------------
    // Comandos y propiedades (thread-safe, cualquier hilo)
    // ------------------------------------------------------------------

    public void Play(string filePath) => Command("loadfile", filePath);

    public void Command(params string[] args)
    {
        ThrowIfDisposed();
        int rc = MpvNative.Command(_handle, args);
        if (rc < 0)
            throw new InvalidOperationException(
                $"mpv command [{string.Join(' ', args)}] falló: {ErrorString(rc)}");
    }

    public void SetProperty(string name, string value)
    {
        ThrowIfDisposed();
        mpv_set_property_string(_handle, name, value);
    }

    public void SetProperty(string name, double value)
    {
        ThrowIfDisposed();
        mpv_set_property(_handle, name, MpvFormat.Double, ref value);
    }

    public void SetProperty(string name, long value)
    {
        ThrowIfDisposed();
        mpv_set_property(_handle, name, MpvFormat.Int64, ref value);
    }

    public void SetProperty(string name, bool value)
    {
        ThrowIfDisposed();
        int v = value ? 1 : 0;
        mpv_set_property(_handle, name, MpvFormat.Flag, ref v);
    }

    /// <summary>Lee una propiedad double; null si mpv aún no la conoce.</summary>
    public double? GetPropertyDouble(string name)
    {
        if (_disposed) return null;
        int rc = mpv_get_property(_handle, name, MpvFormat.Double, out double v);
        return rc >= 0 ? v : null;
    }

    public long? GetPropertyLong(string name)
    {
        if (_disposed) return null;
        int rc = mpv_get_property(_handle, name, MpvFormat.Int64, out long v);
        return rc >= 0 ? v : null;
    }

    public string? GetPropertyString(string name) =>
        _disposed ? null : MpvNative.GetPropertyString(_handle, name);

    // ------------------------------------------------------------------
    // Hilo de eventos
    // ------------------------------------------------------------------

    private void EventLoop()
    {
        while (!_shutdown)
        {
            nint evPtr = mpv_wait_event(_handle, 0.2);
            if (evPtr == 0) continue;
            var ev = System.Runtime.InteropServices.Marshal.PtrToStructure<MpvEvent>(evPtr);

            switch (ev.EventId)
            {
                case MpvEventId.Shutdown:
                    return;
                case MpvEventId.FileLoaded:
                    SafeRaise(FileLoaded);
                    break;
                case MpvEventId.PlaybackRestart:
                    SafeRaise(PlaybackRestart);
                    break;
                case MpvEventId.EndFile:
                    SafeRaise(EndFile);
                    break;
                case MpvEventId.PropertyChange when ev.ReplyUserdata == EofReachedUserdata:
                    var prop = System.Runtime.InteropServices.Marshal
                        .PtrToStructure<MpvEventProperty>(ev.Data);
                    if (prop.Format == MpvFormat.Flag && prop.Data != 0 &&
                        System.Runtime.InteropServices.Marshal.ReadInt32(prop.Data) == 1)
                    {
                        SafeRaise(EndFile);
                    }
                    break;
            }
        }
    }

    private static void SafeRaise(Action? handler)
    {
        try { handler?.Invoke(); }
        catch (Exception) { /* un handler roto no debe matar el event loop */ }
    }

    // ------------------------------------------------------------------
    // Terminación
    // ------------------------------------------------------------------

    /// <summary>Detiene la reproducción y destruye la instancia mpv.
    /// Equivale a command('stop') + terminate() de python-mpv.</summary>
    public void Terminate() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { MpvNative.Command(_handle, "stop"); } catch (Exception) { }

        _shutdown = true;
        mpv_wakeup(_handle);
        if (Thread.CurrentThread != _eventThread)
            _eventThread.Join(2_000);

        mpv_terminate_destroy(_handle);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MpvPlayer));
    }
}
