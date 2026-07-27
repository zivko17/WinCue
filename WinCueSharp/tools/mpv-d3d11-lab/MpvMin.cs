// =============================================================================
// MpvMin.cs — Bindings mínimos de libmpv para el lab (client API + render API)
// =============================================================================
// Copia reducida de src/WinCue.Video/MpvNative.cs + MpvRenderNative.cs: el lab
// es un proyecto paralelo (misma convención que tools/hap-lab) y no debe
// arrastrar WinCue.Video ni sus tipos internos.
//
// Reglas de hilo del render API: el contexto GL debe estar CURRENT en el hilo
// que llama a create/render/free, y debe ser SIEMPRE el mismo hilo. Aquí todo
// ocurre en el hilo de Main.
// =============================================================================

using System.Runtime.InteropServices;
using System.Text;

namespace MpvD3D11Lab;

internal static class MpvMin
{
    private const string Dll = "libmpv-2.dll";

    [DllImport(Dll)] internal static extern nint mpv_create();
    [DllImport(Dll)] internal static extern int mpv_initialize(nint ctx);
    [DllImport(Dll)] internal static extern void mpv_terminate_destroy(nint ctx);
    [DllImport(Dll)] internal static extern int mpv_set_option_string(nint ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Dll)] internal static extern int mpv_command(nint ctx, nint args);
    [DllImport(Dll)] internal static extern nint mpv_error_string(int error);
    [DllImport(Dll)] internal static extern int mpv_get_property(nint ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out double data);

    internal const int FormatDouble = 5;

    [DllImport(Dll)] internal static extern int mpv_render_context_create(
        out nint renderCtx, nint mpv, nint paramsArray);
    [DllImport(Dll)] internal static extern void mpv_render_context_free(nint renderCtx);
    [DllImport(Dll)] internal static extern int mpv_render_context_render(nint renderCtx, nint paramsArray);
    [DllImport(Dll)] internal static extern ulong mpv_render_context_update(nint renderCtx);

    // mpv_render_param_type
    internal const int ParamApiType = 1;
    internal const int ParamOpenGlInitParams = 2;
    internal const int ParamOpenGlFbo = 3;
    internal const int ParamFlipY = 4;

    internal const ulong UpdateFrame = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RenderParam { public nint Type; public nint Data; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct OpenGlInitParams { public nint GetProcAddress; public nint Ctx; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct OpenGlFbo { public int Fbo; public int W; public int H; public int InternalFormat; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate nint GetProcAddressFn(nint ctx, nint name);

    internal static string Err(int code) =>
        Marshal.PtrToStringAnsi(mpv_error_string(code)) ?? code.ToString();

    /// <summary>Array nativo de mpv_render_param con terminador. El llamante
    /// libera; los punteros de Data deben seguir vivos mientras se use.</summary>
    internal static nint BuildParams(params (int Type, nint Data)[] entries)
    {
        int size = Marshal.SizeOf<RenderParam>();
        nint mem = Marshal.AllocHGlobal(size * (entries.Length + 1));
        for (int i = 0; i < entries.Length; i++)
            Marshal.StructureToPtr(
                new RenderParam { Type = entries[i].Type, Data = entries[i].Data },
                mem + i * size, false);
        Marshal.StructureToPtr(new RenderParam(), mem + entries.Length * size, false);
        return mem;
    }

    /// <summary>mpv_command con array de cadenas UTF-8 (evita el escapado de
    /// mpv_command_string, que se come las barras invertidas de las rutas).</summary>
    internal static int Command(nint ctx, params string[] args)
    {
        nint[] ptrs = new nint[args.Length + 1];
        for (int i = 0; i < args.Length; i++)
            ptrs[i] = Utf8(args[i]);
        ptrs[args.Length] = 0;

        nint block = Marshal.AllocHGlobal(nint.Size * ptrs.Length);
        try
        {
            Marshal.Copy(ptrs, 0, block, ptrs.Length);
            return mpv_command(ctx, block);
        }
        finally
        {
            Marshal.FreeHGlobal(block);
            for (int i = 0; i < args.Length; i++)
                Marshal.FreeHGlobal(ptrs[i]);
        }
    }

    internal static nint Utf8(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        nint p = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        Marshal.WriteByte(p, bytes.Length, 0);
        return p;
    }
}
