namespace ProResLab.Tests;

/// <summary>
/// El veredicto de licencia como función pura: nada de esto necesita FFmpeg
/// cargado, y es la comprobación de la que depende poder distribuir WinCue.
/// </summary>
public sealed class LicenseCheckTests
{
    private static Dictionary<string, string> Lgpl() => new(StringComparer.Ordinal)
    {
        ["avutil"] = "LGPL version 3 or later",
        ["avcodec"] = "LGPL version 3 or later",
        ["avformat"] = "LGPL version 3 or later",
    };

    [Fact]
    public void BuildLimpia_SinGplNiNonfree_EsLgpl()
    {
        LicenseCheck.Result r = LicenseCheck.Evaluate("--enable-version3 --enable-shared --enable-libopus", Lgpl());

        Assert.Equal(LicenseVerdict.Lgpl, r.Verdict);
    }

    [Fact]
    public void ConfigureConEnableGpl_EsGpl()
    {
        LicenseCheck.Result r = LicenseCheck.Evaluate("--enable-gpl --enable-libx264", Lgpl());

        Assert.Equal(LicenseVerdict.Gpl, r.Verdict);
        Assert.Contains("--enable-gpl", r.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigureConEnableNonfree_EsNonFree()
    {
        LicenseCheck.Result r = LicenseCheck.Evaluate("--enable-nonfree --enable-libfdk-aac", Lgpl());

        Assert.Equal(LicenseVerdict.NonFree, r.Verdict);
    }

    [Fact]
    public void NonfreeGanaAGpl()
    {
        // Si están las dos cosas, el veredicto tiene que ser el PEOR.
        LicenseCheck.Result r = LicenseCheck.Evaluate("--enable-gpl --enable-nonfree", Lgpl());

        Assert.Equal(LicenseVerdict.NonFree, r.Verdict);
    }

    [Fact]
    public void LibreriaQueDeclaraGpl_MandaAunqueElConfigureParezcaLimpio()
    {
        // El caso que la comprobación de libmpv NO cubría: alguien nos da una
        // DLL cuya línea de configure no delata nada pero la librería sí.
        Dictionary<string, string> licenses = Lgpl();
        licenses["avcodec"] = "GPL version 2 or later";

        LicenseCheck.Result r = LicenseCheck.Evaluate("--enable-version3 --enable-shared", licenses);

        Assert.Equal(LicenseVerdict.Gpl, r.Verdict);
    }

    [Fact]
    public void SinNingunaLicenciaLegible_EsIndeterminadoYNoApto()
    {
        LicenseCheck.Result r = LicenseCheck.Evaluate(
            "--enable-shared", new Dictionary<string, string>(StringComparer.Ordinal) { ["avcodec"] = "" });

        Assert.Equal(LicenseVerdict.Unknown, r.Verdict);
        Assert.NotEqual(LicenseVerdict.Lgpl, r.Verdict);
    }

    [Fact]
    public void LgplNoSeConfundeConGpl_PorSerSubcadena()
    {
        // "LGPL" contiene "GPL": el detector no puede caer en esa trampa.
        LicenseCheck.Result r = LicenseCheck.Evaluate("--enable-version3", Lgpl());

        Assert.Equal(LicenseVerdict.Lgpl, r.Verdict);
    }

    [Fact]
    public void LaBuildQueUsaElLaboratorio_EsLgplDeVerdad()
    {
        // Prueba de INTEGRACIÓN: interroga a las DLL realmente cargadas.
        // Sin ellas no hay laboratorio, así que aquí no se salta: se falla.
        Assert.NotNull(FFmpegLoader.Locate());
        FFmpegLoader.Load();

        LicenseCheck.Result r = LicenseCheck.Inspect();

        Assert.Equal(LicenseVerdict.Lgpl, r.Verdict);
        Assert.DoesNotContain("--enable-gpl", r.Configuration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--enable-nonfree", r.Configuration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--enable-shared", r.Configuration, StringComparison.OrdinalIgnoreCase);
    }
}
