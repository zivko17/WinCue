using System.Diagnostics;
using System.Globalization;

namespace ProResLab.Gpu;

/// <summary>
/// Referencia externa: el mismo frame decodificado y convertido a BGRA por el
/// <c>ffmpeg.exe</c> del PATH (SUBPROCESO, no enlazado).
/// </summary>
/// <remarks>
/// <para>
/// Es la vara de medir honesta: si nuestro camino GPU coincide con lo que
/// produce ffmpeg —y por tanto mpv, que usa el mismo swscale— la imagen es
/// correcta. Se pide <c>rawvideo</c> en vez de PNG a propósito: así no se mete
/// de por medio el codificador de PNG ni su redondeo.
/// </para>
/// <para>
/// Que el ffmpeg del PATH sea GPL da igual aquí: es una HERRAMIENTA externa de
/// verificación, no código enlazado en el producto.
/// </para>
/// </remarks>
internal static class FfmpegReference
{
    /// <summary>Devuelve el frame como BGRA, o null si no hay ffmpeg disponible.</summary>
    public static byte[]? Decode(string path, int frameIndex, int width, int height)
    {
        string args = string.Create(CultureInfo.InvariantCulture,
            $"-hide_banner -loglevel error -i \"{path}\" -vf select=eq(n\\,{frameIndex}) -vsync 0 "
            + $"-frames:v 1 -f rawvideo -pix_fmt bgra -");

        ProcessStartInfo psi = new("ffmpeg", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using Process? proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            using MemoryStream ms = new();
            proc.StandardOutput.BaseStream.CopyTo(ms);
            _ = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            byte[] data = ms.ToArray();
            long expected = (long)width * height * 4;
            return data.LongLength == expected ? data : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // no hay ffmpeg en el PATH
        }
        catch (IOException)
        {
            return null;
        }
    }
}
