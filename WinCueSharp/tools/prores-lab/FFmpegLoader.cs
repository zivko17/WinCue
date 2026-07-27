using System.Diagnostics;
using FFmpeg.AutoGen;

namespace ProResLab;

/// <summary>
/// Localiza y carga las DLL de FFmpeg **por enlace dinámico explícito**.
/// </summary>
/// <remarks>
/// <para>
/// Requisito de licencia, no de comodidad: WinCue se distribuye CERRADO, así
/// que FFmpeg solo puede usarse bajo LGPL y con enlace DINÁMICO — el usuario
/// tiene que poder sustituir las DLL por otras suyas. Por eso NUNCA se enlaza
/// estáticamente ni se empotran los binarios: se fija
/// <c>ffmpeg.RootPath</c> a una carpeta y de ahí salen.
/// </para>
/// <para>
/// Orden de búsqueda: (1) variable de entorno <c>WINCUE_FFMPEG_DIR</c>,
/// (2) <c>ffmpeg-lgpl\bin</c> junto al ejecutable, (3) subiendo por los
/// directorios padre hasta encontrar <c>ffmpeg-lgpl\bin</c> (para
/// <c>dotnet run</c> desde el repo). Deliberadamente NO se cae al PATH del
/// sistema: en esta máquina el ffmpeg del PATH es GPL y enlazarlo sin querer
/// sería justo el fallo que este laboratorio existe para evitar.
/// </para>
/// </remarks>
public static class FFmpegLoader
{
    private static readonly object Gate = new();
    private static bool _loaded;

    /// <summary>Carpeta desde la que se han cargado las DLL (válida tras <see cref="Load"/>).</summary>
    public static string? RootPath { get; private set; }

    /// <summary>Nombre de la variable de entorno que fuerza la carpeta de DLL.</summary>
    public const string DirEnvVar = "WINCUE_FFMPEG_DIR";

    /// <summary>Subcarpeta convencional del laboratorio donde viven las DLL LGPL.</summary>
    public const string DefaultSubdir = "ffmpeg-lgpl";

    /// <summary>
    /// Fija <c>ffmpeg.RootPath</c> y fuerza la carga de las librerías.
    /// Idempotente. Lanza si no encuentra una carpeta con <c>avcodec-*.dll</c>.
    /// </summary>
    public static void Load()
    {
        lock (Gate)
        {
            if (_loaded)
            {
                return;
            }

            string dir = Locate()
                ?? throw new DllNotFoundException(
                    $"No se encontró la carpeta de DLL de FFmpeg. Ejecuta obtener-ffmpeg-lgpl.ps1 " +
                    $"o fija {DirEnvVar} a una carpeta con avcodec-*.dll.");

            ffmpeg.RootPath = dir;

            // Toca la API para forzar la resolución AHORA (y fallar aquí, con un
            // mensaje claro, en vez de en medio de un decode).
            _ = ffmpeg.avcodec_version();

            // Por defecto FFmpeg escupe sus avisos a stderr sin pedir permiso.
            // En una aplicación de directo eso no vale: el log del decodificador
            // nativo tiene que ir a NUESTRO log, no a la consola del usuario.
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);

            RootPath = dir;
            _loaded = true;
        }
    }

    /// <summary>Nivel de verbosidad del log nativo (constantes <c>AV_LOG_*</c>).</summary>
    public static void SetLogLevel(int level)
    {
        Load();
        ffmpeg.av_log_set_level(level);
    }

    /// <summary>Busca la carpeta de DLL sin cargarla. Devuelve null si no la encuentra.</summary>
    public static string? Locate()
    {
        string? env = Environment.GetEnvironmentVariable(DirEnvVar);
        if (!string.IsNullOrWhiteSpace(env) && HasAvcodec(env))
        {
            return env;
        }

        // Junto al ejecutable, y luego subiendo (bin/Release/net8.0 → ... → lab)
        string? probe = AppContext.BaseDirectory;
        for (int depth = 0; depth < 8 && probe is not null; depth++)
        {
            string candidate = Path.Combine(probe, DefaultSubdir, "bin");
            if (HasAvcodec(candidate))
            {
                return candidate;
            }

            probe = Path.GetDirectoryName(probe.TrimEnd(Path.DirectorySeparatorChar));
        }

        return null;
    }

    private static bool HasAvcodec(string dir)
    {
        try
        {
            return Directory.Exists(dir) && Directory.GetFiles(dir, "avcodec-*.dll").Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Rutas reales de los módulos nativos de FFmpeg cargados en el proceso.</summary>
    /// <remarks>
    /// Sin ambigüedad: puede haber varias avcodec-*.dll en el sistema con
    /// licencias distintas. Esta es la lista de las que de verdad están dentro.
    /// </remarks>
    public static IReadOnlyList<string> LoadedModules()
    {
        List<string> modules = [];
        foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
        {
            string name = m.ModuleName ?? "";
            if (name.StartsWith("av", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("sw", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("postproc", StringComparison.OrdinalIgnoreCase))
            {
                modules.Add(m.FileName ?? name);
            }
        }

        modules.Sort(StringComparer.OrdinalIgnoreCase);
        return modules;
    }
}
