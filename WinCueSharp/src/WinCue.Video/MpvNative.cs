// =============================================================================
// MpvNative.cs — Bindings P/Invoke mínimos del client API de libmpv
// =============================================================================
// Sustituye al paquete python-mpv. Solo se vinculan las funciones que WinCue
// necesita; el render API (mpv_render_context_*) se añadirá en la fase del
// compositor D3D11.
//
// LOCALIZACIÓN DE libmpv-2.dll (en orden). El orden va de lo MÁS explícito a lo
// más ambiental, no al revés: la DLL que se carga determina qué licencia se está
// probando, y eso no puede depender del PATH de la máquina.
//   1. Variable de entorno WINCUE_LIBMPV (ruta completa al .dll)
//   2. Junto al ejecutable — la disposición del paquete distribuido
//   3. Hacia arriba, "libmpv-lgpl/libmpv-2.dll" — LA BUILD LGPL que se
//      distribuye (ver tools/libmpv-lgpl/obtener-libmpv-lgpl.ps1)
//   4. Hacia arriba, "WinCue/libmpv-2.dll" (el proyecto Python hermano). Es
//      GPL: sirve para probar en local, NUNCA para distribuir.
//   5. Carga estándar de Windows (PATH). Último recurso — ver el comentario
//      en Resolve: ponerlo primero enmascaraba la DLL del repo.
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

        nint handle;

        // 1. Variable de entorno explícita: si alguien la fija, manda.
        string? env = Environment.GetEnvironmentVariable("WINCUE_LIBMPV");
        if (!string.IsNullOrEmpty(env) && File.Exists(env) &&
            NativeLibrary.TryLoad(env, out handle))
            return handle;

        // 2. Junto al ejecutable: ES la disposición del paquete que se
        //    distribuye (publish.ps1 deja ahí la DLL ya verificada como LGPL).
        string juntoAlExe = Path.Combine(AppContext.BaseDirectory, Dll);
        if (File.Exists(juntoAlExe) && NativeLibrary.TryLoad(juntoAlExe, out handle))
            return handle;

        // 3 y 4. Subir por los directorios padre buscando, EN ESTE ORDEN, la
        // build LGPL (la que se distribuye) y solo después la GPL del proyecto
        // Python hermano. El orden importa: si en desarrollo ganara la GPL,
        // estaríamos probando contra una DLL distinta de la que va al cliente.
        foreach (string sub in new[] { "libmpv-lgpl", "WinCue" })
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, sub, Dll);
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
                    return handle;
            }
        }

        // 5. ÚLTIMO recurso: carga estándar (PATH del sistema). Va al final a
        //    propósito. Antes iba primero, y eso hacía que una libmpv cualquiera
        //    del PATH —en esta misma máquina hay una GPL en %USERPROFILE%\
        //    .local\bin— ganara a la LGPL del repo: se probaba en desarrollo una
        //    DLL distinta de la que recibe el cliente, sin avisar. De paso
        //    estrecha el hueco de DLL planting anotado como CA5392 en la
        //    auditoría 01.
        if (NativeLibrary.TryLoad(name, assembly, searchPath, out handle))
            return handle;

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
