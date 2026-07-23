using System.Globalization;
using System.IO;
using System.Windows;

namespace HapLab.View;

/// <summary>
/// CLI del laboratorio de presentación GPU (PASO 2):
/// <c>hap-lab-view &lt;archivo.mov&gt; [--bench-seconds N] [--upload dynamic|update] [--pace thread|compositor]</c>.
/// Reproduce el clip en una ventana WPF con overlay de stats; al acabar
/// (N segundos o el clip entero) imprime el resumen + una línea JSON por
/// stdout y sale con código 0.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args is null || args.Length == 0)
        {
            Console.WriteLine("Uso: hap-lab-view <archivo.mov> [--bench-seconds N] [--upload dynamic|update] [--pace thread|compositor]");
            return 2;
        }

        string path = args[0];
        double benchSeconds = 0;
        UploadMode mode = UploadMode.Dynamic;
        PaceMode pace = PaceMode.Thread;
        string? dumpFrame = null;
        int dumpIndex = 30;
        for (int i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--bench-seconds", StringComparison.OrdinalIgnoreCase)
                && i + 1 < args.Length
                && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
            {
                benchSeconds = s;
                i++;
            }
            else if (string.Equals(args[i], "--upload", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                mode = args[i + 1].ToUpperInvariant() switch
                {
                    "UPDATE" => UploadMode.Update,
                    _ => UploadMode.Dynamic,
                };
                i++;
            }
            else if (string.Equals(args[i], "--pace", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                pace = args[i + 1].ToUpperInvariant() switch
                {
                    "COMPOSITOR" => PaceMode.Compositor,
                    _ => PaceMode.Thread,
                };
                i++;
            }
            else if (string.Equals(args[i], "--dump-frame", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                dumpFrame = args[i + 1];
                i++;
            }
            else if (string.Equals(args[i], "--dump-index", StringComparison.OrdinalIgnoreCase)
                && i + 1 < args.Length
                && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int di))
            {
                dumpIndex = di;
                i++;
            }
        }

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"No existe el fichero: {path}");
            return 1;
        }

        try
        {
            MovInfo info = MovDemuxer.Open(path);
            HapTextureFormat format = info.VideoFourcc switch
            {
                "Hap1" => HapTextureFormat.RgbDxt1,
                "Hap5" => HapTextureFormat.RgbaDxt5,
                "HapY" => HapTextureFormat.YCoCgDxt5,
                _ => throw new InvalidDataException($"Códec no soportado: {info.VideoFourcc}"),
            };

            Console.WriteLine($"Clip: {Path.GetFileName(path)} — {info.Width}x{info.Height} {info.VideoFourcc} @ {info.Fps:F2} fps, {info.FrameCount} frames ({info.DurationSeconds:F1} s)");

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new MainWindow(path, info, format, benchSeconds, mode, pace)
            {
                DumpFramePath = dumpFrame,
                DumpFrameIndex = dumpIndex,
            };
            return app.Run(window);
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"Error de formato: {ex.Message}");
            return 1;
        }
    }
}
