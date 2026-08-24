using System.Diagnostics;
using System.Globalization;

namespace WinCue.Audio;

/// <summary>Preguntarle a ffprobe cuánto dura un archivo.
///
/// Vive aquí, y no en cada interfaz, porque los DOS productos necesitan lo
/// mismo para la barra de trim de un cue de VÍDEO: ahí no se dibuja onda —no
/// se decodifica el audio de un vídeo solo para pintar una barra— pero sí
/// hace falta la duración para colocar los manejadores.
///
/// Está partida en dos a propósito: <see cref="ParseDuration"/> es pura y
/// tiene pruebas; <see cref="DurationSeconds"/> es la que lanza el proceso y
/// no se puede probar sin ffprobe instalado. Lo frágil de verdad es el
/// parseo —la salida viene con punto decimal SIEMPRE, así que leerla con la
/// cultura del sistema la rompería en un Windows en español— y eso queda
/// cubierto.</summary>
public static class MediaProbe
{
    /// <summary>Duración en segundos. Lanza si ffprobe no está, no responde o
    /// devuelve algo que no es un número: quien llama lo traduce a «no
    /// disponible» en la barra.</summary>
    public static double DurationSeconds(string path)
    {
        var psi = new ProcessStartInfo("ffprobe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries"); psi.ArgumentList.Add("format=duration");
        psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("default=nw=1:nk=1");
        psi.ArgumentList.Add(path);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("ffprobe no disponible");
        string output = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit(8_000);
        return ParseDuration(output);
    }

    /// <summary>La salida de ffprobe, en segundos. SIEMPRE con punto decimal:
    /// por eso va con InvariantCulture y no con la del sistema.</summary>
    public static double ParseDuration(string output) =>
        double.Parse(output.Trim(), CultureInfo.InvariantCulture);
}
