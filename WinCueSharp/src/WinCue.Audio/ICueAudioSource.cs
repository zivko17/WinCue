// =============================================================================
// ICueAudioSource.cs — Fuente de samples de un cue (RAM o streaming) + RAM impl
// =============================================================================
// PASO 4 DEL MOTOR: CueSampleProvider deja de leer directamente del AudioBuffer
// y pasa a consumir esta interfaz mínima. Dos implementaciones:
//   · RamAudioSource       — sobre el AudioBuffer entero en RAM (los cues de
//     AUDIO normales siguen EXACTAMENTE igual: pre-decode y cero I/O en RT).
//   · StreamingAudioSource — ffmpeg subproceso → ring acotado (el AUDIO ESPEJO
//     de los cues de vídeo arranca en ~centenas de ms y con RAM constante).
//
// CONTRATO DEL HILO RT (ReadFrames/PassEnded/BeginNextPass se llaman desde el
// hilo de audio de NAudio):
//   · ReadFrames JAMÁS bloquea. Devuelve los frames escritos:
//       0 con PassEnded == true  → fin real de la pasada (región agotada);
//       0 con PassEnded == false → UNDERRUN (streaming: el ring se quedó
//         vacío con el proceso vivo) — el provider emite silencio SIN avanzar
//         la posición (el reloj del vídeo se congela y el PLL frena suave).
//   · El TRIM (in/out) vive DENTRO de la fuente: RAM recorta índices, el
//     streaming lo traduce a -ss/-t de ffmpeg. El provider solo ve la región.
//   · Las pasadas (loop/play_count) las pide el provider: PrepareNextPass
//     precalienta la siguiente (streaming: pre-spawn del subproceso; RAM:
//     no-op) y BeginNextPass conmuta al empezar la pasada nueva.
// =============================================================================

namespace WinCue.Audio;

/// <summary>Fuente de frames float32 intercalados de la región reproducible
/// de un cue (con trim aplicado). Ver el contrato RT en la cabecera.</summary>
public interface ICueAudioSource : IDisposable
{
    int SampleRate { get; }

    int Channels { get; }

    /// <summary>Frames de la región reproducible (con trim). El streaming lo
    /// ESTIMA por ffprobe (el fin real lo marca PassEnded); −1 = desconocido.</summary>
    long TotalFrames { get; }

    /// <summary>Escribe hasta <paramref name="frames"/> frames intercalados en
    /// dest desde la posición actual de la pasada. NO bloquea (hilo RT).</summary>
    int ReadFrames(float[] dest, int offset, int frames);

    /// <summary>true cuando la pasada actual terminó de verdad (región agotada
    /// o subproceso muerto con el ring drenado).</summary>
    bool PassEnded { get; }

    /// <summary>Precalienta la SIGUIENTE pasada (loop/play_count) sin tocar la
    /// actual. Idempotente y no bloqueante; RAM: no-op.</summary>
    void PrepareNextPass();

    /// <summary>Conmuta a la siguiente pasada (posición 0 de la región).
    /// false = imposible (fallo fatal del streaming) → terminar el cue.</summary>
    bool BeginNextPass();
}

/// <summary>Fuente en RAM: el AudioBuffer entero pre-decodificado, con el trim
/// resuelto a índices — la ruta de SIEMPRE de los cues de audio (cero cambios
/// de comportamiento: mismos clamps defensivos que tenía CueSampleProvider).</summary>
public sealed class RamAudioSource : ICueAudioSource
{
    private readonly AudioBuffer _buf;
    private readonly int _startFrame;   // trim in (frames absolutos del buffer)
    private readonly int _endFrame;     // trim out (exclusivo)
    private int _position;              // relativo a _startFrame

    public RamAudioSource(AudioBuffer buffer, double trimStart = 0.0, double trimEnd = 0.0)
    {
        _buf = buffer;

        // Recorte (in/out) dentro del archivo, estilo QLab — la misma
        // aritmética que vivía en CueSampleProvider (trim inválido → completo)
        int n = buffer.FrameCount;
        int s = trimStart > 0.0 ? (int)(trimStart * buffer.SampleRate) : 0;
        int e = trimEnd > 0.0 ? (int)(trimEnd * buffer.SampleRate) : n;
        s = Math.Clamp(s, 0, n);
        e = Math.Clamp(e, s, n);
        if (e <= s) { s = 0; e = n; }
        _startFrame = s;
        _endFrame = e;
    }

    public int SampleRate => _buf.SampleRate;

    public int Channels => _buf.Channels;

    public long TotalFrames => _endFrame - _startFrame;

    public bool PassEnded => _position >= _endFrame - _startFrame;

    public int ReadFrames(float[] dest, int offset, int frames)
    {
        int remaining = (_endFrame - _startFrame) - _position;
        int chunk = Math.Min(frames, remaining);
        if (chunk <= 0)
            return 0;
        Array.Copy(_buf.Samples, (_startFrame + _position) * _buf.Channels,
                   dest, offset, chunk * _buf.Channels);
        _position += chunk;
        return chunk;
    }

    public void PrepareNextPass() { /* RAM: nada que precalentar */ }

    public bool BeginNextPass()
    {
        _position = 0;
        return true;
    }

    public void Dispose() { /* sin recursos nativos */ }
}
