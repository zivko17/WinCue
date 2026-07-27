// =============================================================================
// LicenseCheck.cs — ¿La build de FFmpeg cargada es LGPL de verdad?
// =============================================================================
// Promocionado de tools/prores-lab (paso 1). BLOQUEANTE DE VENTA: WinCue se
// distribuye cerrado, así que una DLL GPL colada en un paquete no es un bug de
// software, es un problema legal. Por eso esto no vive solo en un laboratorio:
// lo ejecuta el SMOKE DE PUBLICACIÓN (tools/verificar-ffmpeg) y falla el
// empaquetado si el veredicto no es LGPL.
//
// Amplía el enfoque de tools/libmpv-lgpl/verificar-licencia (que solo mira la
// línea de `configure`): aquí se cruzan DOS fuentes independientes y solo se da
// por bueno si coinciden —
//   1. avcodec_configuration(): no puede traer --enable-gpl ni --enable-nonfree;
//   2. avutil_license() / avcodec_license() / avformat_license(): cada librería
//      declara la suya.
// Y sí, la trampa obvia está contemplada: "LGPL" CONTIENE "GPL".
// =============================================================================

using FFmpeg.AutoGen;

namespace WinCue.Video.ProRes;

/// <summary>Veredicto de licencia de una build de FFmpeg.</summary>
public enum LicenseVerdict
{
    /// <summary>LGPL: apto para enlazar dinámicamente desde software propietario.</summary>
    Lgpl,

    /// <summary>GPL: contamina; NO apto para distribución propietaria.</summary>
    Gpl,

    /// <summary>nonfree: ni siquiera redistribuible.</summary>
    NonFree,

    /// <summary>No se pudo determinar (no debe tratarse como apto).</summary>
    Unknown,
}

public static class LicenseCheck
{
    /// <summary>Resultado completo de la comprobación.</summary>
    /// <param name="Verdict">Veredicto agregado (el PEOR de todas las librerías).</param>
    /// <param name="Configuration">Línea de <c>configure</c> con la que se compiló.</param>
    /// <param name="Licenses">Licencia declarada por cada librería.</param>
    /// <param name="Reason">Explicación en una línea.</param>
    public sealed record Result(
        LicenseVerdict Verdict,
        string Configuration,
        IReadOnlyDictionary<string, string> Licenses,
        string Reason);

    /// <summary>
    /// Decide el veredicto SOLO a partir de texto: función pura, testeable sin
    /// tener FFmpeg cargado.
    /// </summary>
    /// <param name="configuration">Línea de <c>configure</c>.</param>
    /// <param name="licenses">Cadenas de <c>*_license()</c> por librería.</param>
    public static Result Evaluate(string configuration, IReadOnlyDictionary<string, string> licenses)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(licenses);

        // 1) La línea de configure: los dos interruptores que lo estropean todo.
        bool cfgGpl = Has(configuration, "--enable-gpl");
        bool cfgNonFree = Has(configuration, "--enable-nonfree");

        // 2) Lo que declara cada librería por su cuenta.
        bool libNonFree = false;
        bool libGpl = false;
        bool libLgpl = false;
        foreach (string lic in licenses.Values)
        {
            if (Has(lic, "nonfree"))
            {
                libNonFree = true;
            }
            else if (Has(lic, "GPL") && !Has(lic, "LGPL"))
            {
                // OJO: "LGPL" contiene "GPL" — sin esta guarda, una build
                // perfectamente LGPL se rechazaría como GPL.
                libGpl = true;
            }
            else if (Has(lic, "LGPL"))
            {
                libLgpl = true;
            }
        }

        if (cfgNonFree || libNonFree)
        {
            return new Result(LicenseVerdict.NonFree, configuration, licenses,
                "nonfree: ni siquiera es redistribuible.");
        }

        if (cfgGpl || libGpl)
        {
            string quien = cfgGpl && libGpl
                ? "configure trae --enable-gpl y alguna librería declara GPL"
                : cfgGpl ? "configure trae --enable-gpl" : "alguna librería declara GPL";
            return new Result(LicenseVerdict.Gpl, configuration, licenses,
                $"GPL ({quien}): NO apto para distribución propietaria.");
        }

        if (!libLgpl)
        {
            return new Result(LicenseVerdict.Unknown, configuration, licenses,
                "No se pudo leer ninguna licencia de librería: indeterminado, se trata como NO apto.");
        }

        return new Result(LicenseVerdict.Lgpl, configuration, licenses,
            "LGPL (sin --enable-gpl ni --enable-nonfree): apto para enlace DINÁMICO desde software propietario.");
    }

    /// <summary>
    /// Interroga a las librerías REALMENTE cargadas. Requiere <see cref="FFmpegLoader.Load"/>.
    /// </summary>
    /// <remarks>
    /// Cada librería se consulta por separado y con red: WinCue solo distribuye
    /// avcodec + avutil + swresample (avformat no se usa — el demuxer es
    /// nuestro), así que preguntarle a avformat lanzaría <c>DllNotFound</c> en
    /// una instalación correcta. Una librería ausente no cuenta ni a favor ni
    /// en contra; el veredicto sale de las que sí están.
    /// </remarks>
    public static Result Inspect()
    {
        FFmpegLoader.Load();
        string configuration = ffmpeg.avcodec_configuration() ?? "";
        Dictionary<string, string> licenses = new(StringComparer.Ordinal);
        Ask(licenses, "avutil", static () => ffmpeg.avutil_license());
        Ask(licenses, "avcodec", static () => ffmpeg.avcodec_license());
        Ask(licenses, "avformat", static () => ffmpeg.avformat_license());

        return Evaluate(configuration, licenses);
    }

    private static void Ask(Dictionary<string, string> into, string name, Func<string?> read)
    {
        try
        {
            if (read() is { Length: > 0 } lic)
                into[name] = lic;
        }
        catch (DllNotFoundException)
        {
            // Librería no distribuida (avformat): no se usa, no se juzga.
        }
        catch (EntryPointNotFoundException)
        {
            // ABI distinta de la esperada: tampoco se juzga por ella.
        }
        catch (NotSupportedException)
        {
            // Lo que lanza DE VERDAD FFmpeg.AutoGen cuando la DLL de esa
            // librería no está en RootPath (envuelve el fallo de resolución en
            // un delegado "método no soportado"). Comprobado con avformat, que
            // WinCue no distribuye.
        }
    }

    private static bool Has(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
