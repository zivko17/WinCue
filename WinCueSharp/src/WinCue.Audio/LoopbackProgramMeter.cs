// =============================================================================
// LoopbackProgramMeter.cs — VU de PROGRAMA por captura loopback WASAPI
// =============================================================================
//
// QUÉ MIDE
//   Lo que REALMENTE sale por el dispositivo de render: cues de audio, audio
//   de los vídeos (mpv), audiciones y cualquier otra fuente del sistema. Es un
//   medidor de programa de verdad, no una suma de streams internos.
//
// LETRA PEQUEÑA (deliberada y documentada en settings)
//   · ASIO puentea el mixer de Windows → sin loopback. El AudioEngine no crea
//     este medidor con ASIO activo y cae al VU basado en mixer interno.
//   · Se mide TODO el dispositivo: una notificación de Windows también se ve.
//     En un PC de FOH dedicado eso es una ventaja (si suena, se ve).
//
// MODELO DE HILOS
//   · Start/Dispose: main thread (o timer de reintento). Estado bajo _gate.
//   · DataAvailable: hilo de captura de NAudio. Solo acumula y emite
//     LevelReport (el AudioEngine lo remite al dispatcher).
//   · Un capture muerto NUNCA se libera en su propio hilo (WasapiCapture.
//     Dispose hace Join del hilo de captura) → se libera en el ThreadPool.
// =============================================================================

using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WinCue.Audio;

public sealed class LoopbackProgramMeter : IDisposable
{
    /// <summary>RMS estéreo (L, R) del dispositivo, ~20 Hz. HILO DE CAPTURA.</summary>
    public event Action<float, float>? LevelReport;

    private readonly string? _deviceId;   // null/vacío = render por defecto
    private readonly object _gate = new();

    private WasapiLoopbackCapture? _capture;
    private System.Threading.Timer? _retry;
    private bool _disposed;

    // Formato del capture activo (fijado en Start, leído en el hilo de captura)
    private int _channels;
    private bool _float32;
    private int _framesPerEmit;

    // Acumulador RMS — solo lo toca el hilo de captura (DataAvailable es serie)
    private double _sumL, _sumR;
    private int _frames;

    public LoopbackProgramMeter(string? deviceId = null) => _deviceId = deviceId;

    /// <summary>Abre la captura loopback. Si falla (sin dispositivo, formato
    /// raro…) programa reintentos cada 2 s: el medidor se recupera solo al
    /// enchufar/cambiar el dispositivo.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _capture is not null)
                return;
            try
            {
                // CA2000: la propiedad pasa a _capture; el catch de fuera cubre
                // los fallos de StartRecording (el capture aún no está asignado)
#pragma warning disable CA2000
                var cap = CreateCapture();
#pragma warning restore CA2000
                try
                {
                    var fmt = cap.WaveFormat;
                    _channels = Math.Max(1, fmt.Channels);
                    _float32 = fmt.BitsPerSample == 32;
                    _framesPerEmit = Math.Max(1, fmt.SampleRate / 20);   // ~50 ms
                    _sumL = _sumR = 0.0;
                    _frames = 0;

                    cap.DataAvailable += OnData;
                    cap.RecordingStopped += OnStopped;
                    cap.StartRecording();
                    _capture = cap;
                }
                catch
                {
                    // Nunca Dispose bajo _gate: hace Join del hilo de captura
                    // y este puede estar esperando el lock en OnStopped
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { cap.Dispose(); } catch (Exception) { }
                    });
                    throw;
                }
                _retry?.Dispose();
                _retry = null;
            }
            catch (Exception)
            {
                ScheduleRetryLocked();
            }
        }
    }

    private WasapiLoopbackCapture CreateCapture()
    {
        if (_deviceId is { Length: > 0 } id)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                // El MMDevice queda referenciado por el capture: no se libera aquí
                return new WasapiLoopbackCapture(enumerator.GetDevice(id));
            }
            catch (Exception) { /* id obsoleto → dispositivo por defecto */ }
        }
        return new WasapiLoopbackCapture();
    }

    /// <summary>HILO DE CAPTURA: RMS acumulado y emisión cada ~50 ms. En
    /// silencio total WASAPI no entrega paquetes → no se emite nada y el VU
    /// decae solo (comportamiento ya implementado en VuMeterControl).</summary>
    private void OnData(object? sender, WaveInEventArgs e)
    {
        int ch = _channels;
        int bytesPerSample = _float32 ? 4 : 2;
        int stride = bytesPerSample * ch;
        if (stride == 0 || e.BytesRecorded < stride)
            return;

        int frames = e.BytesRecorded / stride;
        for (int f = 0; f < frames; f++)
        {
            float l, r;
            if (_float32)
            {
                l = BitConverter.ToSingle(e.Buffer, f * stride);
                r = ch > 1 ? BitConverter.ToSingle(e.Buffer, f * stride + 4) : l;
            }
            else
            {
                l = BitConverter.ToInt16(e.Buffer, f * stride) / 32768f;
                r = ch > 1 ? BitConverter.ToInt16(e.Buffer, f * stride + 2) / 32768f : l;
            }
            _sumL += (double)l * l;
            _sumR += (double)r * r;
        }
        _frames += frames;

        if (_frames >= _framesPerEmit)
        {
            float rmsL = (float)Math.Sqrt(_sumL / _frames);
            float rmsR = (float)Math.Sqrt(_sumR / _frames);
            _sumL = _sumR = 0.0;
            _frames = 0;
            LevelReport?.Invoke(rmsL, rmsR);
        }
    }

    /// <summary>La captura murió (dispositivo desenchufado, formato cambiado,
    /// servicio de audio reiniciado…) → soltar el capture y reintentar.</summary>
    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        WasapiLoopbackCapture? dead;
        lock (_gate)
        {
            if (_disposed)
                return;
            dead = _capture;
            _capture = null;
            ScheduleRetryLocked();
        }
        // Puede llegar en el propio hilo de captura → liberar fuera de él
        // (Dispose hace Join del hilo y se colgaría a sí mismo)
        if (dead is not null)
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { dead.Dispose(); } catch (Exception) { }
            });
    }

    private void ScheduleRetryLocked() =>
        _retry ??= new System.Threading.Timer(_ => Start(), null, 2_000, 2_000);

    public void Dispose()
    {
        WasapiLoopbackCapture? cap;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _retry?.Dispose();
            _retry = null;
            cap = _capture;
            _capture = null;
        }
        if (cap is not null)
        {
            try { cap.StopRecording(); } catch (Exception) { }
            try { cap.Dispose(); } catch (Exception) { }
        }
    }
}
