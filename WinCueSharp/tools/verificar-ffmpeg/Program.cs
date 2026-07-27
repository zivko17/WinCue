// =============================================================================
// verificar-ffmpeg — ¿las DLL de FFmpeg que se van a distribuir son LGPL?
// =============================================================================
// GUARDIÁN DEL EMPAQUETADO. Lo ejecuta publish.ps1 y hace fallar la publicación
// si el veredicto no es LGPL: WinCue se distribuye cerrado y una avcodec GPL
// colada en un paquete no es un bug, es un problema legal. Hermano de
// tools/libmpv-lgpl/verificar-licencia (que hace lo propio con libmpv), con una
// comprobación más: además de la línea de `configure`, interroga a cada
// librería por su licencia declarada y exige que las dos fuentes coincidan.
//
// Uso:
//   dotnet run --project tools\verificar-ffmpeg\verificar-ffmpeg.csproj
//   dotnet run ... -- <carpeta-con-las-dll>      (p. ej. la de publish\)
//
// Salida: 0 solo si el veredicto es LGPL y el decodificador 'prores' está.
// =============================================================================

using WinCue.Video.ProRes;

namespace WinCue.Tools.VerificarFFmpeg;

internal static class Program
{
    private static int Main(string[] args)
    {
        // Una carpeta explícita gana: es como se verifica el CONTENIDO DEL
        // PAQUETE recién publicado, no el del árbol de desarrollo.
        if (args.Length > 0 && args[0].Length > 0)
            Environment.SetEnvironmentVariable(FFmpegLoader.DirEnvVar, Path.GetFullPath(args[0]));

        try
        {
            FFmpegLoader.Load();
        }
        catch (DllNotFoundException exc)
        {
            Console.Error.WriteLine($"FALLO: {exc.Message}");
            return 2;
        }

        Console.WriteLine($"RootPath fijado = {FFmpegLoader.RootPath}");
        Console.WriteLine();
        Console.WriteLine("Módulos nativos REALMENTE cargados en el proceso:");
        foreach (string m in FFmpegLoader.LoadedModules())
            Console.WriteLine($"  {m}");
        Console.WriteLine();

        LicenseCheck.Result r = LicenseCheck.Inspect();

        Console.WriteLine("configuración =");
        foreach (string chunk in Wrap(r.Configuration, 100))
            Console.WriteLine($"  {chunk}");
        Console.WriteLine();

        foreach ((string lib, string lic) in r.Licenses)
            Console.WriteLine($"  {lib,-10} license = {lic}");
        Console.WriteLine();

        Console.WriteLine($"VEREDICTO: {r.Verdict} — {r.Reason}");

        bool hasProres = ProResDecoderPresent(out string decoderNote);
        Console.WriteLine($"decoder 'prores' = {decoderNote}");

        if (r.Verdict != LicenseVerdict.Lgpl)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("RECHAZADO: esta build NO es apta para distribuir con WinCue.");
            return 1;
        }

        if (!hasProres)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("RECHAZADO: la build es LGPL pero no trae el decodificador 'prores'.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("APTO: LGPL con enlace dinámico + decodificador ProRes presente.");
        return 0;
    }

    /// <summary>Abrir un contexto de 16x16 es la prueba de que el decodificador
    /// está DE VERDAD (no basta con que la cadena de configure lo mencione).</summary>
    private static bool ProResDecoderPresent(out string note)
    {
        try
        {
            using var decoder = new ProResDecoder(16, 16, 1, ThreadMode.SingleThread, "apcn");
            note = $"presente (hilos={decoder.ThreadCount}, thread_type={decoder.ActiveThreadType})";
            return true;
        }
        catch (Exception exc)
        {
            note = $"AUSENTE ({exc.Message})";
            return false;
        }
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        for (int i = 0; i < text.Length; i += width)
            yield return text.Substring(i, Math.Min(width, text.Length - i));
    }
}
