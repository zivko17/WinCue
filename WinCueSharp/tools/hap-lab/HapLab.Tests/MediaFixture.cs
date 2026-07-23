using System.Diagnostics;

namespace HapLab.Tests;

/// <summary>
/// Genera (una sola vez, si no existen) los clips HAP de prueba con ffmpeg en
/// tools/hap-lab/media/. Clips pequeños (320x240, 1 s) para que los tests sean rápidos.
/// Requiere ffmpeg en el PATH.
/// </summary>
public sealed class MediaFixture
{
    public MediaFixture()
    {
        MediaDir = Path.Combine(FindLabRoot(), "media");
        Directory.CreateDirectory(MediaDir);

        Generate("lab-hap1.mov", "-f lavfi -i testsrc2=size=320x240:rate=30:duration=1 -c:v hap -format hap");
        Generate("lab-hap5.mov", "-f lavfi -i testsrc2=size=320x240:rate=30:duration=1 -c:v hap -format hap_alpha");
        Generate("lab-hapy.mov", "-f lavfi -i testsrc2=size=320x240:rate=30:duration=1 -c:v hap -format hap_q");
        Generate("lab-chunked.mov", "-f lavfi -i testsrc2=size=320x240:rate=30:duration=1 -c:v hap -format hap -chunks 4");
        Generate(
            "lab-audio.mov",
            "-f lavfi -i testsrc2=size=320x240:rate=30:duration=1 -f lavfi -i sine=frequency=440:duration=1 -c:v hap -format hap -c:a pcm_s16le -shortest");
    }

    /// <summary>Carpeta tools/hap-lab/media con los .mov generados.</summary>
    public string MediaDir { get; }

    public string PathOf(string fileName) => Path.Combine(MediaDir, fileName);

    private static string FindLabRoot()
    {
        // Desde bin/Debug/net8.0 del proyecto de tests, subir hasta la carpeta
        // que contiene HapLab.csproj (tools/hap-lab)
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "HapLab.csproj")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("No se encontró la carpeta tools/hap-lab desde " + AppContext.BaseDirectory);
    }

    private void Generate(string fileName, string ffmpegArgs)
    {
        string output = PathOf(fileName);
        if (File.Exists(output))
        {
            return;
        }

        using Process proc = new();
        proc.StartInfo.FileName = "ffmpeg";
        proc.StartInfo.Arguments = $"-hide_banner -loglevel error -y {ffmpegArgs} \"{output}\"";
        proc.StartInfo.UseShellExecute = false;
        proc.StartInfo.RedirectStandardError = true;
        proc.Start();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0 || !File.Exists(output))
        {
            throw new InvalidOperationException($"ffmpeg falló generando {fileName}: {stderr}");
        }
    }
}

[CollectionDefinition("media")]
public sealed class MediaCollectionDefinition : ICollectionFixture<MediaFixture>
{
}
