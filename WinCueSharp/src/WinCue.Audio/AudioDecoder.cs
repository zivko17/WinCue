// =============================================================================
// AudioDecoder.cs — Capa de compatibilidad universal de audio
// =============================================================================
// Port de audio_decoder.py. Decodifica cualquier archivo de audio a un
// AudioBuffer float32 normalizado, independientemente del formato de origen.
//
// FLUJO DE DECISIÓN
//  1. ¿Existe el archivo?            → No → FileNotFoundException
//  2. ¿Extensión nativa NAudio?      → Sí → AudioFileReader/MediaFoundation
//     (.wav .aiff .aif .mp3 .m4a .aac .wma .mp4 — MediaFoundation también
//      decodifica .flac en Windows 10+)
//  3. Resto (.ogg .opus .ac3 .dts u otros) → FFmpeg por pipe (pcm_f32le)
//  4. Si la ruta nativa falla        → reintento con FFmpeg antes de rendirse
//
// A diferencia de pydub (Python), el pipe de FFmpeg entrega directamente
// float32 normalizado: no hay que dividir por 2^(bits-1) a mano.
// =============================================================================

using System.Diagnostics;
using NAudio.Wave;

namespace WinCue.Audio;

/// <summary>Error de decodificación (formato sin soporte, archivo corrupto…).</summary>
public class AudioDecoderException : Exception
{
    public AudioDecoderException(string message) : base(message) { }
    public AudioDecoderException(string message, Exception inner) : base(message, inner) { }
}

public static class AudioDecoder
{
    // Formatos que NAudio decodifica sin FFmpeg (WaveFileReader / AiffFileReader /
    // Mp3FileReader / MediaFoundationReader).
    private static readonly HashSet<string> NativeExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".aiff", ".aif", ".mp3", ".m4a", ".aac", ".wma", ".mp4", ".flac",
    };

    // Detección de FFmpeg — una sola vez por proceso (igual que en Python)
    private static readonly Lazy<bool> FFmpegProbe = new(() =>
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (p is null) return false;
            if (!p.WaitForExit(5_000)) { try { p.Kill(); } catch { } return false; }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    });

    /// <summary>True si FFmpeg está accesible en PATH (comprobado una vez).</summary>
    public static bool IsFFmpegAvailable => FFmpegProbe.Value;

    /// <summary>Todos los formatos conocidos con su disponibilidad actual.</summary>
    public static Dictionary<string, bool> GetSupportedFormats()
    {
        bool ff = IsFFmpegAvailable;
        return new Dictionary<string, bool>
        {
            // Nativos NAudio/MediaFoundation — siempre disponibles en Windows
            [".wav"] = true, [".aiff"] = true, [".aif"] = true, [".mp3"] = true,
            [".m4a"] = true, [".aac"] = true, [".wma"] = true, [".mp4"] = true,
            [".flac"] = true,
            // Requieren FFmpeg
            [".ogg"] = ff, [".opus"] = ff, [".ac3"] = ff, [".dts"] = ff,
        };
    }

    /// <summary>
    /// Decodifica un archivo de audio a AudioBuffer float32 (−1.0…1.0).
    /// Lanza FileNotFoundException si no existe y AudioDecoderException si el
    /// formato no tiene soporte o el archivo está corrupto.
    /// </summary>
    public static AudioBuffer Decode(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Archivo no encontrado: '{filePath}'", filePath);

        string ext = Path.GetExtension(filePath);

        if (NativeExts.Contains(ext))
        {
            try
            {
                return DecodeNative(filePath);
            }
            catch (Exception) when (IsFFmpegAvailable)
            {
                // La ruta nativa falló (códec raro dentro de un contenedor
                // conocido, MediaFoundation sin el códec…): reintentar con FFmpeg
                return DecodeFFmpeg(filePath);
            }
        }

        if (!IsFFmpegAvailable)
            throw new AudioDecoderException(
                $"El formato '{ext}' requiere FFmpeg.\n" +
                "Instálalo desde https://ffmpeg.org/download.html\n" +
                "y añádelo al PATH del sistema.");

        return DecodeFFmpeg(filePath);
    }

    // ------------------------------------------------------------------
    // Ruta nativa: NAudio (AudioFileReader elige el reader por extensión
    // y ya entrega float32 normalizado vía ISampleProvider)
    // ------------------------------------------------------------------

    private static AudioBuffer DecodeNative(string filePath)
    {
        try
        {
            using var reader = new AudioFileReader(filePath);
            int sampleRate = reader.WaveFormat.SampleRate;
            int channels = reader.WaveFormat.Channels;

            var chunks = new List<float[]>();
            long total = 0;
            var block = new float[sampleRate * channels];  // ~1 s por lectura
            int read;
            while ((read = reader.Read(block, 0, block.Length)) > 0)
            {
                var copy = new float[read];
                Array.Copy(block, copy, read);
                chunks.Add(copy);
                total += read;
            }

            var samples = new float[total];
            int pos = 0;
            foreach (var c in chunks)
            {
                Array.Copy(c, 0, samples, pos, c.Length);
                pos += c.Length;
            }

            return new AudioBuffer { Samples = samples, SampleRate = sampleRate, Channels = channels };
        }
        catch (FileNotFoundException) { throw; }
        catch (Exception exc)
        {
            throw new AudioDecoderException($"No se pudo decodificar '{filePath}': {exc.Message}", exc);
        }
    }

    // ------------------------------------------------------------------
    // Ruta FFmpeg: decodifica a pcm_f32le por pipe (ya normalizado)
    // ------------------------------------------------------------------

    private static AudioBuffer DecodeFFmpeg(string filePath)
    {
        try
        {
            var (sampleRate, channels) = ProbeStreamInfo(filePath);

            var psi = new ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(filePath);
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("f32le");
            psi.ArgumentList.Add("-acodec"); psi.ArgumentList.Add("pcm_f32le");
            psi.ArgumentList.Add("pipe:1");

            using var proc = Process.Start(psi)
                ?? throw new AudioDecoderException("No se pudo lanzar ffmpeg.");

            using var mem = new MemoryStream();
            proc.StandardOutput.BaseStream.CopyTo(mem);
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode != 0 || mem.Length == 0)
                throw new AudioDecoderException(
                    $"FFmpeg no pudo decodificar '{filePath}': {stderr.Trim()}");

            byte[] raw = mem.ToArray();
            int sampleCount = raw.Length / sizeof(float);
            // Descartar bytes sueltos y frames incompletos al final del pipe
            sampleCount -= sampleCount % channels;
            var samples = new float[sampleCount];
            Buffer.BlockCopy(raw, 0, samples, 0, sampleCount * sizeof(float));

            return new AudioBuffer { Samples = samples, SampleRate = sampleRate, Channels = channels };
        }
        catch (FileNotFoundException) { throw; }
        catch (AudioDecoderException) { throw; }
        catch (Exception exc)
        {
            throw new AudioDecoderException(
                $"No se pudo decodificar '{filePath}' con FFmpeg: {exc.Message}", exc);
        }
    }

    private static (int sampleRate, int channels) ProbeStreamInfo(string filePath)
    {
        var psi = new ProcessStartInfo("ffprobe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-select_streams"); psi.ArgumentList.Add("a:0");
        psi.ArgumentList.Add("-show_entries"); psi.ArgumentList.Add("stream=sample_rate,channels");
        psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("csv=p=0");
        psi.ArgumentList.Add(filePath);

        using var proc = Process.Start(psi)
            ?? throw new AudioDecoderException("No se pudo lanzar ffprobe.");
        string output = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit();

        // Formato esperado: "48000,2"
        var parts = output.Split(',');
        if (parts.Length < 2 ||
            !int.TryParse(parts[0], out int rate) ||
            !int.TryParse(parts[1], out int chans) ||
            rate <= 0 || chans <= 0)
        {
            throw new AudioDecoderException(
                $"ffprobe no devolvió información de stream válida para '{filePath}' (salida: '{output}')");
        }
        return (rate, chans);
    }
}
