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
//   · NINGÚN capture se libera en el hilo llamante: WasapiCapture.Dispose hace
//     Join del hilo de captura y bloquearía a quien llame (el hilo de UI al
//     guardar Ajustes → tirones de la barra de progreso y los relojes). TODA
//     liberación —incluida la de Dispose()— va al ThreadPool.
// =============================================================================

using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WinCue.Audio;

public sealed class LoopbackProgramMeter : IDisposable
{
    /// <summary>RMS estéreo (L, R) del dispositivo, ~20 Hz. HILO DE CAPTURA.</summary>
    public event Action<float, float>? LevelReport;

    private readonly string? _deviceId;   // null/vacío = render por defecto
    private readonly bool _fallbackToDefault;
    private readonly object _gate = new();

    // CA2213: SÍ se libera, pero SIEMPRE en el ThreadPool (ReleaseOffThread) —
    // su Dispose hace Join del hilo de captura y no puede correr aquí.
#pragma warning disable CA2213
    private WasapiLoopbackCapture? _capture;
#pragma warning restore CA2213
    private System.Threading.Timer? _retry;
    private bool _disposed;
    private volatile bool _capturing;

    // Formato del capture activo (fijado en Start, leído en el hilo de captura)
    private int _channels;
    private bool _float32;
    private int _framesPerEmit;

    // Acumulador RMS — solo lo toca el hilo de captura (DataAvailable es serie)
    private double _sumL, _sumR;
    private int _frames;

    /// <param name="fallbackToDefault">true (medidor de programa): si el id no
    /// existe se mide el render por defecto — igual que hace la salida del
    /// engine. false (medidor de audición): sin ese dispositivo NO se mide
    /// nada (medir otro dispositivo sería mentir); se reintenta hasta que
    /// aparezca.</param>
    public LoopbackProgramMeter(string? deviceId = null, bool fallbackToDefault = true)
    {
        _deviceId = deviceId;
        _fallbackToDefault = fallbackToDefault;
    }

    /// <summary>true SOLO mientras hay una captura viva de verdad. Es el
    /// interruptor del VU de programa: si esto es false el AudioEngine mide
    /// por la mezcla interna, porque «existe el objeto medidor» NO significa
    /// «el medidor está midiendo» (reintentos, dispositivo caído, ASIO…).
    /// Legible desde cualquier hilo.</summary>
    public bool IsCapturing => _capturing;

    /// <summary>Libera un capture SIEMPRE fuera del hilo llamante: su Dispose
    /// hace Join del hilo de captura (colgaría al propio hilo de captura y
    /// bloquearía al de UI).</summary>
    private static void ReleaseOffThread(WasapiLoopbackCapture? cap)
    {
        if (cap is null)
            return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { cap.StopRecording(); } catch (Exception) { }
            try { cap.Dispose(); } catch (Exception) { }
        });
    }

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
                    _capturing = true;
                }
                catch
                {
                    // Nunca Dispose bajo _gate: hace Join del hilo de captura
                    // y este puede estar esperando el lock en OnStopped
                    ReleaseOffThread(cap);
                    throw;
                }
                _retry?.Dispose();
                _retry = null;
            }
            catch (Exception)
            {
                _capturing = false;
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
            catch (Exception) when (_fallbackToDefault)
            {
                /* id obsoleto → dispositivo por defecto */
            }
        }
        else if (!_fallbackToDefault)
        {
            throw new InvalidOperationException("Medidor sin dispositivo asignado.");
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
            _capturing = false;
            if (_disposed)
                return;
            dead = _capture;
            _capture = null;
            ScheduleRetryLocked();
        }
        // Puede llegar en el propio hilo de captura → liberar fuera de él
        // (Dispose hace Join del hilo y se colgaría a sí mismo)
        ReleaseOffThread(dead);
    }

    private void ScheduleRetryLocked() =>
        _retry ??= new System.Threading.Timer(_ => Start(), null, 2_000, 2_000);

    /// <summary>Cierra el medidor SIN bloquear al que llama: el StopRecording +
    /// Dispose del capture (que hace Join del hilo de captura, ~10 ms medidos)
    /// se hace en el ThreadPool. Antes esto corría en el hilo de UI cada vez
    /// que se guardaban Ajustes y era parte del tirón del cambio en caliente.
    /// Tras volver, IsCapturing ya es false: el VU cae a la mezcla interna en
    /// el acto, sin ventana ciega.</summary>
    public void Dispose()
    {
        WasapiLoopbackCapture? cap;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _capturing = false;
            _retry?.Dispose();
            _retry = null;
            cap = _capture;
            _capture = null;
        }
        ReleaseOffThread(cap);
    }
}
