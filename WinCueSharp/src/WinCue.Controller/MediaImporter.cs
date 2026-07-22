// =============================================================================
// MediaImporter.cs — Validación y asignación de archivos de media a cues
// =============================================================================
// Port de media_import.py. Clase estática pura: sin estado, sin hilos, sin UI.
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller;

public sealed record MediaValidation(
    bool Valid,
    string FilePath,       // ruta absoluta normalizada (vacía si inválida)
    string FileName,
    string Format,         // extensión en minúsculas (ej. ".mp3")
    string MediaType,      // "AUDIO" | "VIDEO" | "UNKNOWN"
    string? Error);

public static class MediaImporter
{
    public static readonly IReadOnlySet<string> AudioFormats =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".wav", ".flac", ".aiff", ".aif", ".ogg", ".mp3", ".aac", ".m4a", ".opus" };

    public static readonly IReadOnlySet<string> ImageFormats =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

    /// <summary>Las imágenes se tratan como cues de VÍDEO (mpv las muestra).</summary>
    public static readonly IReadOnlySet<string> VideoFormats =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mov", ".avi", ".mkv", ".webm", ".prores", ".mxf",
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp",
        };

    public static readonly IReadOnlySet<string> AllFormats =
        new HashSet<string>(AudioFormats.Concat(VideoFormats), StringComparer.OrdinalIgnoreCase);

    /// <summary>Valida que filePath sea un archivo de media utilizable.</summary>
    public static MediaValidation ValidateFile(string filePath)
    {
        string name = Path.GetFileName(filePath);
        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (!File.Exists(filePath))
        {
            return Directory.Exists(filePath)
                ? new(false, "", name, ext, "UNKNOWN", $"La ruta no es un archivo: '{filePath}'")
                : new(false, "", name, ext, "UNKNOWN", $"El archivo no existe: '{filePath}'");
        }

        if (!AllFormats.Contains(ext))
        {
            string accepted = string.Join(", ", AllFormats.OrderBy(x => x, StringComparer.Ordinal));
            return new(false, "", name, ext, "UNKNOWN",
                $"Formato no soportado: '{ext}'.\nExtensiones aceptadas: {accepted}");
        }

        string mediaType = AudioFormats.Contains(ext) ? "AUDIO"
                         : VideoFormats.Contains(ext) ? "VIDEO"
                         : "UNKNOWN";

        return new(true, Path.GetFullPath(filePath), name, ext, mediaType, null);
    }

    /// <summary>Valida filePath y, si es válido, actualiza cue.FilePath, cue.Type
    /// y cue.Duration (leída con ffprobe). La latencia medida se invalida si el
    /// archivo cambió.</summary>
    public static (bool Ok, string Error) AssignToCue(Cue cue, string filePath)
    {
        var result = ValidateFile(filePath);
        if (!result.Valid)
            return (false, result.Error ?? "Error desconocido");

        string oldPath = cue.FilePath;
        cue.FilePath = result.FilePath;

        if (result.MediaType == "AUDIO") cue.Type = CueType.Audio;
        else if (result.MediaType == "VIDEO") cue.Type = CueType.Video;

        if (!string.Equals(cue.FilePath, oldPath, StringComparison.OrdinalIgnoreCase))
        {
            cue.LatencyMs = null;
            double dur = ProbeDurationSeconds(cue.FilePath);
            if (dur > 0.0)
                cue.Duration = dur;
        }

        return (true, "");
    }

    /// <summary>Extensiones de audio COMPRIMIDO/lossy: candidatas a convertirse
    /// a WAV al exportar un show portable (wav/flac/aiff ya son lossless).</summary>
    public static readonly IReadOnlySet<string> LossyAudioFormats =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".aac", ".m4a", ".ogg", ".opus" };

    /// <summary>Transcodifica el audio de src a WAV PCM 24-bit (mismo sample
    /// rate y canales) vía ffmpeg. LENTO: no llamar en el hilo de UI con
    /// archivos grandes sin avisar. ffmpegExe vacío → «ffmpeg» del PATH.
    /// Devuelve false si ffmpeg no está o la conversión falla (el llamante
    /// decide el fallback, típicamente copiar el original).</summary>
    public static bool ConvertToWav(string src, string dest, string ffmpegExe = "")
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(
                ffmpegExe.Length > 0 ? ffmpegExe : "ffmpeg")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-nostats");
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(src);
            psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:a:0");
            psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("pcm_s24le");
            psi.ArgumentList.Add(dest);
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
                return false;
            proc.StandardError.ReadToEnd();   // drenar para no bloquear el pipe
            if (!proc.WaitForExit(600_000))
            {
                try { proc.Kill(); } catch (Exception) { }
                return false;
            }
            if (proc.ExitCode != 0 || !File.Exists(dest))
            {
                try { if (File.Exists(dest)) File.Delete(dest); } catch (Exception) { }
                return false;
            }
            return true;
        }
        catch (Exception)
        {
            return false;   // sin ffmpeg / src ilegible
        }
    }

    /// <summary>Sonoridad integrada (LUFS) del archivo vía ffmpeg loudnorm.
    /// LENTO (decodifica el archivo entero): llamar en segundo plano.
    /// null si ffmpeg no está disponible o el archivo no tiene audio.</summary>
    public static double? MeasureLufs(string filePath)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-nostats");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(filePath);
            psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:a:0");
            psi.ArgumentList.Add("-af");
            psi.ArgumentList.Add("loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("null");
            psi.ArgumentList.Add("-");
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
                return null;
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(120_000);

            // loudnorm imprime un JSON al final: buscar "input_i" : "-23.1"
            var match = System.Text.RegularExpressions.Regex.Match(stderr,
                "\"input_i\"\\s*:\\s*\"(?<v>-?[0-9.]+)\"");
            if (!match.Success)
                return null;
            return double.Parse(match.Groups["v"].Value,
                System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return null;   // sin ffmpeg / archivo ilegible
        }
    }

    /// <summary>Duración del media en segundos vía ffprobe (0 si no disponible).
    /// Las imágenes no tienen duración → 0.</summary>
    public static double ProbeDurationSeconds(string filePath)
    {
        if (ImageFormats.Contains(Path.GetExtension(filePath)))
            return 0.0;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ffprobe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-show_entries"); psi.ArgumentList.Add("format=duration");
            psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("default=nw=1:nk=1");
            psi.ArgumentList.Add(filePath);
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return 0.0;
            string output = proc.StandardOutput.ReadToEnd().Trim();
            if (!proc.WaitForExit(8_000))
            {
                try { proc.Kill(); } catch (Exception) { }
                return 0.0;
            }
            return double.TryParse(output, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double d) && d > 0
                ? Math.Round(d, 3) : 0.0;
        }
        catch (Exception) { return 0.0; }
    }
}
