// =============================================================================
// MpvNative.cs — Bindings P/Invoke mínimos del client API de libmpv
// =============================================================================
// Sustituye al paquete python-mpv. Solo se vinculan las funciones que WinCue
// necesita; el render API (mpv_render_context_*) se añadirá en la fase del
// compositor D3D11.
//
// LOCALIZACIÓN DE libmpv-2.dll (en orden):
//   1. Directorio del ejecutable / PATH estándar de Windows
//   2. Variable de entorno WINCUE_LIBMPV (ruta completa al .dll)
//   3. Búsqueda hacia arriba desde el directorio de la app de una carpeta
//      "WinCue/libmpv-2.dll" (el proyecto Python hermano, útil en desarrollo)
//
// El API de libmpv es thread-safe: cualquier hilo puede llamar a comandos y
// propiedades. mpv_wait_event debe usarse desde UN solo hilo por handle.
// =============================================================================

using System.Runtime.InteropServices;

namespace WinCue.Video;

internal static partial class MpvNative
{
    private const string Dll = "libmpv-2.dll";

    // ------------------------------------------------------------------
    // Resolución del DLL
    // ------------------------------------------------------------------

    static MpvNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(MpvNative).Assembly, Resolve);
    }

    private static nint Resolve(string name, System.Reflection.Assembly assembly,
                                DllImportSearchPath? searchPath)
    {
        if (!name.Equals(Dll, StringComparison.OrdinalIgnoreCase))
            return 0;

        // 1. Carga estándar (directorio de la app, PATH…)
        if (NativeLibrary.TryLoad(name, assembly, searchPath, out nint handle))
            return handle;

        // 2. Variable de entorno explícita
        string? env = Environment.GetEnvironmentVariable("WINCUE_LIBMPV");
        if (!string.IsNullOrEmpty(env) && File.Exists(env) &&
            NativeLibrary.TryLoad(env, out handle))
            return handle;

        // 3. Buscar WinCue/libmpv-2.dll subiendo desde el directorio de la app
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "WinCue", Dll);
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
                return handle;
        }

        return 0;   // dejar que DllNotFoundException suba con su mensaje normal
    }

    /// <summary>True si libmpv-2.dll se puede cargar (modo degradado si no).</summary>
    internal static bool ProbeAvailable()
    {
        try
        {
            _ = mpv_client_api_version();
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    // ------------------------------------------------------------------
    // IDs de evento (subset de mpv/client.h)
    // ------------------------------------------------------------------

    internal enum MpvEventId
    {
        None = 0,
        Shutdown = 1,
        LogMessage = 2,
        GetPropertyReply = 3,
        SetPropertyReply = 4,
        CommandReply = 5,
        StartFile = 6,
        EndFile = 7,
        FileLoaded = 8,
        PropertyChange = 22,
        PlaybackRestart = 21,
        QueueOverflow = 24,
    }

    // mpv_format (subset)
    internal enum MpvFormat
    {
        None = 0,
        String = 1,
        Flag = 3,
        Int64 = 4,
        Double = 5,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvEvent
    {
        public MpvEventId EventId;
        public int Error;
        public ulong ReplyUserdata;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvEventProperty
    {
        public nint Name;      // const char*
        public MpvFormat Format;
        public nint Data;      // void* al valor (int* si Format == Flag)
    }

    // ------------------------------------------------------------------
    // Funciones del client API
    // ------------------------------------------------------------------

    [LibraryImport(Dll)]
    internal static partial ulong mpv_client_api_version();

    [LibraryImport(Dll)]
    internal static partial nint mpv_create();

    [LibraryImport(Dll)]
    internal static partial int mpv_initialize(nint handle);

    [LibraryImport(Dll)]
    internal static partial void mpv_terminate_destroy(nint handle);

    [LibraryImport(Dll)]
    internal static partial void mpv_wakeup(nint handle);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_set_option_string(nint handle, string name, string value);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_set_property_string(nint handle, string name, string value);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint mpv_get_property_string(nint handle, string name);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_set_property(nint handle, string name, MpvFormat format, ref double data);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_set_property(nint handle, string name, MpvFormat format, ref long data);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_set_property(nint handle, string name, MpvFormat format, ref int data);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_get_property(nint handle, string name, MpvFormat format, out double data);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_get_property(nint handle, string name, MpvFormat format, out long data);

    [LibraryImport(Dll)]
    internal static partial int mpv_command(nint handle, nint args);

    [LibraryImport(Dll)]
    internal static partial nint mpv_wait_event(nint handle, double timeout);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_observe_property(nint handle, ulong userdata, string name, MpvFormat format);

    [LibraryImport(Dll)]
    internal static partial void mpv_free(nint data);

    [LibraryImport(Dll)]
    internal static partial nint mpv_error_string(int error);

    // ------------------------------------------------------------------
    // Helpers de marshaling
    // ------------------------------------------------------------------

    internal static string ErrorString(int code)
    {
        nint p = mpv_error_string(code);
        return Marshal.PtrToStringUTF8(p) ?? $"mpv error {code}";
    }

    /// <summary>Ejecuta un comando mpv (array de strings UTF-8 null-terminated).</summary>
    internal static int Command(nint handle, params string[] args)
    {
        var ptrs = new nint[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++)
                ptrs[i] = StringToUtf8(args[i]);
            ptrs[args.Length] = 0;

            unsafe
            {
                fixed (nint* pp = ptrs)
                    return mpv_command(handle, (nint)pp);
            }
        }
        finally
        {
            for (int i = 0; i < args.Length; i++)
                if (ptrs[i] != 0) Marshal.FreeHGlobal(ptrs[i]);
        }
    }

    private static nint StringToUtf8(string s)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s);
        nint mem = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, mem, bytes.Length);
        Marshal.WriteByte(mem, bytes.Length, 0);
        return mem;
    }

    /// <summary>Lee una propiedad string y libera la memoria de mpv.</summary>
    internal static string? GetPropertyString(nint handle, string name)
    {
        nint p = mpv_get_property_string(handle, name);
        if (p == 0) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { mpv_free(p); }
    }
}
