using FFmpeg.AutoGen;

namespace ProResLab;

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

/// <summary>
/// Comprobación de licencia de las librerías de FFmpeg cargadas.
/// </summary>
/// <remarks>
/// Mismo enfoque que <c>tools\libmpv-lgpl\verificar-licencia</c> (que lee
/// <c>mpv-configuration</c>), AMPLIADO: aquí no basta con la línea de
/// <c>configure</c>, se cruza además con la cadena de licencia que declara
/// CADA librería (<c>avcodec_license()</c> y hermanas). Que las dos fuentes
/// coincidan es lo que da confianza; que discrepen es motivo de rechazo.
/// </remarks>
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
    /// tener FFmpeg cargado. <paramref name="configuration"/> es la línea de
    /// configure; <paramref name="licenses"/> son las cadenas de
    /// <c>*_license()</c> por librería.
    /// </summary>
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

    /// <summary>Interroga a las librerías realmente cargadas. Requiere <see cref="FFmpegLoader.Load"/>.</summary>
    public static Result Inspect()
    {
        string configuration = ffmpeg.avcodec_configuration() ?? "";
        Dictionary<string, string> licenses = new(StringComparer.Ordinal)
        {
            ["avutil"] = ffmpeg.avutil_license() ?? "",
            ["avcodec"] = ffmpeg.avcodec_license() ?? "",
            ["avformat"] = ffmpeg.avformat_license() ?? "",
        };

        return Evaluate(configuration, licenses);
    }

    private static bool Has(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
