// =============================================================================
// verificar-licencia — ¿es GPL o LGPL esta libmpv-2.dll?
// =============================================================================
// P/Invoke directo a libmpv (sin depender de WinCue): crea un contexto mpv,
// lee "mpv-configuration" (la línea de meson con que se compiló) y comprueba
// si lleva -Dgpl=false. Portátil: vale para cualquier DLL candidata.
//
// Uso:  set WINCUE_LIBMPV=C:\ruta\a\libmpv-2.dll   (o déjala junto al exe)
//       dotnet run --project tools\libmpv-lgpl\verificar-licencia
// =============================================================================

using System.Runtime.InteropServices;

// Resolver: usa WINCUE_LIBMPV si está; si no, "libmpv-2.dll" del PATH/cwd
NativeLibrary.SetDllImportResolver(typeof(Mpv).Assembly, (name, asm, path) =>
{
    if (name != "libmpv")
        return IntPtr.Zero;
    string? env = Environment.GetEnvironmentVariable("WINCUE_LIBMPV");
    if (!string.IsNullOrEmpty(env) && File.Exists(env))
        return NativeLibrary.Load(env);
    return NativeLibrary.Load("libmpv-2.dll", asm, path);
});

nint ctx = Mpv.mpv_create();
if (ctx == 0)
{
    Console.WriteLine("ERROR: no se pudo crear el contexto mpv (¿DLL no encontrada?).");
    Console.WriteLine("Fija WINCUE_LIBMPV a la ruta de la libmpv-2.dll a probar.");
    Environment.Exit(2);
}
Mpv.mpv_initialize(ctx);

// Qué DLL se ha cargado REALMENTE (sin ambigüedad: puede haber varias
// libmpv-2.dll en el sistema con versiones/licencias distintas)
string loadedFrom = "(no identificada)";
foreach (System.Diagnostics.ProcessModule m in
    System.Diagnostics.Process.GetCurrentProcess().Modules)
{
    if (m.ModuleName?.Contains("libmpv", StringComparison.OrdinalIgnoreCase) == true)
    {
        loadedFrom = m.FileName ?? m.ModuleName;
        break;
    }
}
Console.WriteLine($"DLL comprobada = {loadedFrom}");

string version = Get(ctx, "mpv-version");
string config = Get(ctx, "mpv-configuration");
string ffmpeg = Get(ctx, "ffmpeg-version");

Console.WriteLine($"mpv-version    = {version}");
Console.WriteLine($"ffmpeg-version = {ffmpeg}");
Console.WriteLine($"configuración  = {config}");
Console.WriteLine();

bool lgpl = config.Contains("gpl=false", StringComparison.OrdinalIgnoreCase);
bool gplExplicit = config.Contains("gpl=true", StringComparison.OrdinalIgnoreCase);
// Componentes que solo existen con GPL activado (delatan una build GPL)
bool gplFeatures = config.Contains("dvdnav=enabled", StringComparison.OrdinalIgnoreCase);

string veredicto = lgpl
    ? "LGPL (gpl=false) — APTO para distribución propietaria ✓"
    : (gplExplicit || gplFeatures)
        ? "GPL — NO apto para distribución propietaria ✗"
        : "GPL por defecto (sin -Dgpl=false) — NO apto ✗";

Console.WriteLine($"VEREDICTO: {veredicto}");
Environment.Exit(lgpl ? 0 : 1);

static string Get(nint ctx, string prop)
{
    nint p = Mpv.mpv_get_property_string(ctx, prop);
    return p == 0 ? "(null)" : (Marshal.PtrToStringUTF8(p) ?? "(null)");
}

internal static partial class Mpv
{
    [LibraryImport("libmpv")] internal static partial nint mpv_create();
    [LibraryImport("libmpv")] internal static partial int mpv_initialize(nint ctx);
    [LibraryImport("libmpv", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint mpv_get_property_string(nint ctx, string name);
}
