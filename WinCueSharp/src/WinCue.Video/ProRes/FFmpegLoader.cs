// =============================================================================
// FFmpegLoader.cs — Carga de las DLL de FFmpeg por ENLACE DINÁMICO EXPLÍCITO
// =============================================================================
// Promocionado de tools/prores-lab (paso 1). Requisito de LICENCIA, no de
// comodidad: WinCue se distribuye CERRADO, así que FFmpeg solo puede usarse
// bajo LGPL y con enlace DINÁMICO — el usuario tiene que poder sustituir las
// DLL por otras suyas (LGPL §6). Por eso nunca se enlaza estáticamente ni se
// empotran los binarios: se fija ffmpeg.RootPath a una carpeta y de ahí salen.
//
// Orden de búsqueda: (1) WINCUE_FFMPEG_DIR, (2) ffmpeg-lgpl\bin junto al
// ejecutable (lo que deja publish.ps1), (3) subiendo por los directorios padre
// (para correr desde el repo con dotnet run). DELIBERADAMENTE NO cae al PATH
// del sistema: ahí vive un ffmpeg GPL en la mayoría de máquinas de desarrollo
// —incluida la de Andrés— y enlazarlo sin querer sería justo el accidente que
// esta disciplina existe para evitar.
//
// OJO: el ffmpeg/ffprobe del PATH SÍ se sigue usando, pero por SUBPROCESO
// (conversión a HAP, sondeo de códec, duraciones). Eso no enlaza nada y la
// licencia de esa build es asunto del usuario.
// =============================================================================

using System.Diagnostics;
using FFmpeg.AutoGen;

namespace WinCue.Video.ProRes;

public static class FFmpegLoader
{
    private static readonly object Gate = new();
    private static bool _loaded;
    private static bool _failed;
    private static string? _failure;

    /// <summary>Carpeta desde la que se han cargado las DLL (válida tras <see cref="Load"/>).</summary>
    public static string? RootPath { get; private set; }

    /// <summary>Variable de entorno que fuerza la carpeta de DLL.</summary>
    public const string DirEnvVar = "WINCUE_FFMPEG_DIR";

    /// <summary>Subcarpeta convencional donde viven las DLL LGPL.</summary>
    public const string DefaultSubdir = "ffmpeg-lgpl";

    /// <summary>
    /// Fija <c>ffmpeg.RootPath</c> y fuerza la carga de las librerías.
    /// Idempotente y thread-safe.
    /// </summary>
    /// <exception cref="DllNotFoundException">No hay carpeta con <c>avcodec-*.dll</c>.</exception>
    public static void Load()
    {
        lock (Gate)
        {
            if (_loaded)
                return;
            if (_failed)
                throw new DllNotFoundException(_failure);

            try
            {
                string dir = Locate()
                    ?? throw new DllNotFoundException(
                        $"No se encontró la carpeta de DLL de FFmpeg (se buscó {DirEnvVar}, " +
                        $"{DefaultSubdir}\\bin junto al ejecutable y en los directorios padre). " +
                        "Ejecuta tools\\ffmpeg-lgpl\\obtener-ffmpeg-lgpl.ps1.");

                ffmpeg.RootPath = dir;

                // Tocar la API para forzar la resolución AHORA (y fallar aquí,
                // con un mensaje claro, en vez de en medio de un decode).
                _ = ffmpeg.avcodec_version();

                // Por defecto FFmpeg escupe sus avisos a stderr sin pedir
                // permiso. En una aplicación de directo eso no vale: solo los
                // errores, y a nuestro log si hay carpeta de diagnóstico.
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);

                RootPath = dir;
                _loaded = true;
            }
            catch (Exception exc)
            {
                // Cachear el fallo: sin esto cada GO de un cue ProRes vuelve a
                // barrer el disco buscando DLL que no están.
                _failed = true;
                _failure = exc.Message;
                throw;
            }
        }
    }

    /// <summary>¿Están las DLL disponibles? NO lanza: es la pregunta que hace la
    /// elegibilidad antes de decidir el enrutado de un cue.</summary>
    public static bool IsAvailable
    {
        get
        {
            lock (Gate)
            {
                if (_loaded)
                    return true;
                if (_failed)
                    return false;
            }

            try
            {
                Load();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
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
            return env;

        // Junto al ejecutable, y luego subiendo (bin/Debug/net8.0-windows → …)
        string? probe = AppContext.BaseDirectory;
        for (int depth = 0; depth < 8 && probe is not null; depth++)
        {
            string candidate = Path.Combine(probe, DefaultSubdir, "bin");
            if (HasAvcodec(candidate))
                return candidate;

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

    /// <summary>Rutas REALES de los módulos nativos de FFmpeg cargados en el
    /// proceso. Sin ambigüedad: puede haber varias avcodec-*.dll en el sistema
    /// con licencias distintas; esta es la lista de las que de verdad están
    /// dentro. Lo usa la verificación de licencia del smoke de publicación.</summary>
    public static IReadOnlyList<string> LoadedModules()
    {
        List<string> modules = new();
        using Process self = Process.GetCurrentProcess();
        foreach (ProcessModule m in self.Modules)
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
