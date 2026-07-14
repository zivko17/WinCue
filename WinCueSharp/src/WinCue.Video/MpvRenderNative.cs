// =============================================================================
// MpvRenderNative.cs — Bindings P/Invoke del RENDER API de libmpv (OpenGL)
// =============================================================================
// Fase compositor: en vez de darle un HWND a mpv (wid), se crea un
// mpv_render_context de tipo "opengl" y mpv dibuja cada frame en el FBO que
// le pasemos. Reglas de hilos del API: el contexto GL debe estar CURRENT en
// el hilo que llama a create/render/free, y ese hilo debe ser siempre el
// mismo. El update callback llega en un hilo interno de mpv: solo señaliza.
// =============================================================================

using System.Runtime.InteropServices;

namespace WinCue.Video;

internal static partial class MpvRenderNative
{
    private const string Dll = "libmpv-2.dll";

    // mpv_render_param_type (subset)
    internal const int ParamInvalid = 0;
    internal const int ParamApiType = 1;
    internal const int ParamOpenGlInitParams = 2;
    internal const int ParamOpenGlFbo = 3;
    internal const int ParamFlipY = 4;
    internal const int ParamAdvancedControl = 10;

    internal const int UpdateFrame = 1;   // flag de mpv_render_context_update

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvRenderParam
    {
        public nint Type;    // mpv_render_param_type (tamaño de puntero por alineación)
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvOpenGlInitParams
    {
        public nint GetProcAddress;      // void* (*)(void* ctx, const char* name)
        public nint GetProcAddressCtx;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvOpenGlFbo
    {
        public int Fbo;
        public int W;
        public int H;
        public int InternalFormat;   // 0 = desconocido/por defecto
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate nint GetProcAddressFn(nint ctx, nint name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void RenderUpdateFn(nint ctx);

    [LibraryImport(Dll)]
    internal static partial int mpv_render_context_create(
        out nint ctx, nint mpv, nint paramsArray);

    [LibraryImport(Dll)]
    internal static partial void mpv_render_context_free(nint ctx);

    [LibraryImport(Dll)]
    internal static partial int mpv_render_context_render(nint ctx, nint paramsArray);

    [LibraryImport(Dll)]
    internal static partial ulong mpv_render_context_update(nint ctx);

    [LibraryImport(Dll)]
    internal static partial void mpv_render_context_set_update_callback(
        nint ctx, RenderUpdateFn callback, nint userdata);

    /// <summary>Construye un array nativo de mpv_render_param (con terminador).
    /// El llamante libera con FreeParams. Los Data deben seguir vivos.</summary>
    internal static nint BuildParams(params (int Type, nint Data)[] entries)
    {
        int size = Marshal.SizeOf<MpvRenderParam>();
        nint mem = Marshal.AllocHGlobal(size * (entries.Length + 1));
        for (int i = 0; i < entries.Length; i++)
        {
            var p = new MpvRenderParam { Type = entries[i].Type, Data = entries[i].Data };
            Marshal.StructureToPtr(p, mem + i * size, false);
        }
        Marshal.StructureToPtr(new MpvRenderParam(), mem + entries.Length * size, false);
        return mem;
    }
}
