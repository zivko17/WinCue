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
