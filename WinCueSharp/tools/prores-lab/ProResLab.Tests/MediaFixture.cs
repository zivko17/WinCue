using System.Diagnostics;
using System.Globalization;

namespace ProResLab.Tests;

/// <summary>
/// Media pequeña de test (320x240), generada con el ffmpeg del PATH la
/// primera vez y reutilizada después.
/// </summary>
/// <remarks>
/// No se versiona (ver <c>.gitignore</c> del laboratorio). Si no hay ffmpeg,
/// <see cref="Available"/> queda a false y los tests que la necesitan se
/// SALTAN en vez de fallar: la máquina de otro puede no tenerlo.
/// </remarks>
public sealed class MediaFixture
{
    /// <summary>Ancho de todos los clips de test.</summary>
    public const int Width = 320;

    /// <summary>Alto de todos los clips de test.</summary>
    public const int Height = 240;

    /// <summary>Frames de cada clip (1 s a 30 fps).</summary>
    public const int Frames = 30;

    private static readonly Lazy<MediaFixture> Shared = new(() => new MediaFixture());

    private MediaFixture()
    {
        MediaDir = Path.Combine(LabRoot(), "media", "test");
        Directory.CreateDirectory(MediaDir);

        Generate("t-422.mov", "-c:v prores_ks -profile:v 2");
        Generate("t-422hq.mov", "-c:v prores_ks -profile:v 3");
        Generate("t-4444.mov", "-c:v prores_ks -profile:v 4 -pix_fmt yuva444p10le", alpha: true);
        Generate("t-4444xq.mov", "-c:v prores_ks -profile:v 5 -pix_fmt yuva444p10le", alpha: true);
        Generate("t-proxy.mov", "-c:v prores_ks -profile:v 0");
        Generate("t-lt.mov", "-c:v prores_ks -profile:v 1");
        Generate("t-audio.mov", "-c:v prores_ks -profile:v 2 -c:a pcm_s16le -shortest", audio: true);
        Generate("t-2020.mov", "-c:v prores_ks -profile:v 3", matrix: "bt2020nc", colr: true);
        Generate("t-709colr.mov", "-c:v prores_ks -profile:v 3", matrix: "bt709", colr: true);
        Generate("t-h264.mp4", "-c:v libx264 -pix_fmt yuv420p");
    }

    /// <summary>Instancia compartida (la generación es cara y se hace una vez).</summary>
    public static MediaFixture Instance => Shared.Value;

    /// <summary>Carpeta donde viven los clips.</summary>
    public string MediaDir { get; }

    /// <summary>Ruta completa de un clip de la fixture.</summary>
    public string Clip(string name) => Path.Combine(MediaDir, name);

    /// <summary>Raíz del laboratorio, subiendo desde el binario de test.</summary>
    public static string LabRoot()
    {
        string? probe = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && probe is not null; i++)
        {
            if (File.Exists(Path.Combine(probe, "ProResLab.csproj")))
            {
                return probe;
            }

            probe = Path.GetDirectoryName(probe.TrimEnd(Path.DirectorySeparatorChar));
        }

        return AppContext.BaseDirectory;
    }

    private void Generate(string name, string codecArgs, bool alpha = false, bool audio = false,
        string matrix = "bt709", bool colr = false)
    {
        string output = Clip(name);
        if (File.Exists(output))
        {
            return;
        }

        string source = alpha
            ? string.Create(CultureInfo.InvariantCulture,
                $"-f lavfi -i testsrc2=size={Width}x{Height}:rate=30:duration=1 "
                + $"-f lavfi -i gradients=size={Width}x{Height}:rate=30:duration=1 "
                + $"-filter_complex [0][1]alphamerge[v] -map [v]")
            : string.Create(CultureInfo.InvariantCulture,
                $"-f lavfi -i testsrc2=size={Width}x{Height}:rate=30:duration=1");

        if (audio)
        {
            source += " -f lavfi -i sine=frequency=440:duration=1";
        }

        string color = $"-colorspace {matrix} -color_range tv" + (colr ? " -movflags +write_colr" : "");
        string args = $"-hide_banner -loglevel error -y {source} {codecArgs} {color} \"{output}\"";

        using Process p = Process.Start(new ProcessStartInfo("ffmpeg", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("No se pudo lanzar ffmpeg.");

        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 || !File.Exists(output))
        {
            throw new InvalidOperationException(
                $"ffmpeg falló generando {name} (código {p.ExitCode}). ¿Está ffmpeg en el PATH?\n{stderr}");
        }
    }
}
