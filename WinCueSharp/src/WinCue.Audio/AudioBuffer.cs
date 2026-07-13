// =============================================================================
// AudioBuffer.cs — Buffer de audio decodificado en RAM
// =============================================================================
// Equivale al ndarray float32 (frames, channels) de la versión Python, pero
// en formato entrelazado (el nativo de NAudio ISampleProvider).
//
// GARANTÍA DE DIRECTO: durante PLAYING el audio sale íntegramente de este
// buffer en RAM. Un fallo de disco posterior NO interrumpe la reproducción.
// =============================================================================

namespace WinCue.Audio;

/// <summary>Audio decodificado: float32 entrelazado, normalizado −1.0…1.0.</summary>
public sealed class AudioBuffer
{
    /// <summary>Muestras entrelazadas (frame0: ch0,ch1…, frame1: ch0,ch1…).</summary>
    public required float[] Samples { get; init; }

    public required int SampleRate { get; init; }

    public required int Channels { get; init; }

    /// <summary>Número de frames (muestras por canal).</summary>
    public int FrameCount => Channels > 0 ? Samples.Length / Channels : 0;

    /// <summary>Duración total en segundos.</summary>
    public double DurationSeconds => SampleRate > 0 ? (double)FrameCount / SampleRate : 0.0;
}
