// =============================================================================
// GlD3D11Interop.cs — Puente OpenGL ↔ Direct3D 11 (lo que este lab prueba)
// =============================================================================
// Variante D3D11 de src/WinCue.Video/GlDxInterop.cs, que hoy va contra D3D9Ex.
// Diferencia clave del registro: WGL_NV_DX_interop2 admite objetos D3D10/D3D11,
// pero un Texture2D SOLO puede registrarse como GL_TEXTURE_2D (con D3D9 se
// usaba GL_RENDERBUFFER). Por eso el FBO se ata con glFramebufferTexture2D.
//
// La extensión no está garantizada: interop v1 solo obliga a soportar D3D9.
// El soporte de D3D11 llega con WGL_NV_DX_interop2 — de ahí que el lab
// compruebe la cadena de extensiones ANTES de intentar nada.
//
// AFINIDAD DE HILO: todo (crear, registrar, lock/render/unlock, destruir) va
// en el hilo que tiene el contexto GL current. Igual que en producción.
// =============================================================================

using System.Runtime.InteropServices;

namespace MpvD3D11Lab;

internal sealed class GlD3D11Interop : IDisposable
{
    // ── user32 / gdi32 / opengl32 ──────────────────────────────────────────
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className,
        string windowName, uint style, int x, int y, int w, int h,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);

    [StructLayout(LayoutKind.Sequential)]
    private struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize, nVersion;
        public uint dwFlags;
        public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits,
            cGreenShift, cBlueBits, cBlueShift, cAlphaBits, cAlphaShift,
            cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits,
            cAccumAlphaBits, cDepthBits, cStencilBits, cAuxBuffers, iLayerType,
            bReserved;
        public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    [DllImport("gdi32.dll")]
    private static extern int ChoosePixelFormat(nint dc, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")]
    private static extern bool SetPixelFormat(nint dc, int fmt, ref PIXELFORMATDESCRIPTOR pfd);

    [DllImport("opengl32.dll")] private static extern nint wglCreateContext(nint dc);
    [DllImport("opengl32.dll")] private static extern bool wglDeleteContext(nint ctx);
    [DllImport("opengl32.dll")] private static extern bool wglMakeCurrent(nint dc, nint ctx);
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern nint wglGetProcAddress(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadLibraryW(string name);

    // GL 1.1 (exportado directamente por opengl32.dll)
    [DllImport("opengl32.dll")] private static extern nint glGetString(uint name);
    [DllImport("opengl32.dll")] private static extern void glGenTextures(int n, out uint tex);
    [DllImport("opengl32.dll")] private static extern void glDeleteTextures(int n, ref uint tex);
    [DllImport("opengl32.dll")] private static extern void glClearColor(float r, float g, float b, float a);
    [DllImport("opengl32.dll")] private static extern void glClear(uint mask);
    [DllImport("opengl32.dll")] private static extern void glFlush();
    [DllImport("opengl32.dll")] private static extern void glFinish();
    [DllImport("opengl32.dll")] private static extern uint glGetError();
    [DllImport("opengl32.dll")] private static extern void glViewport(int x, int y, int w, int h);

    // ── Extensiones ────────────────────────────────────────────────────────
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate nint DXOpenDeviceFn(nint dxDevice);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate bool DXCloseDeviceFn(nint hDevice);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate nint DXRegisterObjectFn(nint hDevice, nint dxObject, uint name, uint type, uint access);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate bool DXUnregisterObjectFn(nint hDevice, nint hObject);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate bool DXObjectsFn(nint hDevice, int count, nint[] objects);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate bool DXSetShareHandleFn(nint dxObject, nint shareHandle);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate nint GetExtStringFn(nint dc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GenFn(int n, out uint name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void DeleteFn(int n, ref uint name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void BindFramebufferFn(uint target, uint fbo);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FramebufferTexture2DFn(uint target, uint attachment, uint texTarget, uint tex, int level);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint CheckFramebufferStatusFn(uint target);

    internal const uint GL_TEXTURE_2D = 0x0DE1;
    private const uint GL_FRAMEBUFFER = 0x8D40;
    private const uint GL_COLOR_ATTACHMENT0 = 0x8CE0;
    private const uint GL_FRAMEBUFFER_COMPLETE = 0x8CD5;
    private const uint GL_COLOR_BUFFER_BIT = 0x4000;
    private const uint WGL_ACCESS_READ_WRITE_NV = 0x0001;
    private const uint GL_VENDOR = 0x1F00, GL_RENDERER = 0x1F01, GL_VERSION = 0x1F02;

    private DXOpenDeviceFn _dxOpen = null!;
    private DXCloseDeviceFn _dxClose = null!;
    private DXRegisterObjectFn _dxRegister = null!;
    private DXUnregisterObjectFn _dxUnregister = null!;
    private DXObjectsFn _dxLock = null!, _dxUnlock = null!;
    private DXSetShareHandleFn _dxSetShare = null!;
    private GenFn _genFramebuffers = null!;
    private DeleteFn _delFramebuffers = null!;
    private BindFramebufferFn _bindFramebuffer = null!;
    private FramebufferTexture2DFn _fbTexture2D = null!;
    private CheckFramebufferStatusFn _checkStatus = null!;

    private nint _hwnd, _dc, _glCtx, _interopDevice;

    /// <summary>HALLAZGO DEL LAB: esto NO puede ser un inicializador de campo.
    /// Los inicializadores corren ANTES del cuerpo del constructor, y en ese
    /// momento opengl32.dll aún no está cargada en el proceso (el primer
    /// P/Invoke que la carga es wglCreateContext, más abajo) → GetModuleHandleW
    /// devuelve 0 y TODAS las funciones core de GL 1.1 quedan sin resolver
    /// (wglGetProcAddress no las devuelve nunca, por diseño). mpv pide
    /// glGetString lo primero y rechaza el contexto con "not supported".
    /// Se resuelve DESPUÉS de tener contexto, con LoadLibraryW de respaldo.</summary>
    private nint _opengl32;

    public string GlVendor { get; private set; } = "?";
    public string GlRenderer { get; private set; } = "?";
    public string GlVersion { get; private set; } = "?";
    public string WglExtensions { get; private set; } = "";

    /// <summary>Textura D3D11 registrada en el interop + su nombre GL y su FBO.</summary>
    internal sealed class Registration
    {
        public nint Handle;      // objeto de interop
        public uint GlTexture;
        public uint Fbo;
        public int W, H;
    }

    private readonly List<Registration> _regs = new();

    /// <summary>Ventana oculta + contexto GL, current en el hilo llamante.</summary>
    public GlD3D11Interop()
    {
        _hwnd = CreateWindowExW(0, "STATIC", "MpvD3D11Lab", 0, 0, 0, 1, 1, 0, 0, 0, 0);
        if (_hwnd == 0)
            throw new InvalidOperationException("No se pudo crear la ventana GL oculta.");
        _dc = GetDC(_hwnd);

        var pfd = new PIXELFORMATDESCRIPTOR
        {
            nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(),
            nVersion = 1,
            dwFlags = 0x4 | 0x20,   // DRAW_TO_WINDOW | SUPPORT_OPENGL
            iPixelType = 0,
            cColorBits = 32,
        };
        int fmt = ChoosePixelFormat(_dc, ref pfd);
        if (fmt == 0 || !SetPixelFormat(_dc, fmt, ref pfd))
            throw new InvalidOperationException("SetPixelFormat falló.");

        _glCtx = wglCreateContext(_dc);
        if (_glCtx == 0 || !wglMakeCurrent(_dc, _glCtx))
            throw new InvalidOperationException("No se pudo crear el contexto OpenGL.");

        // Ya con el contexto vivo: ahora opengl32.dll SÍ está en el proceso
        _opengl32 = GetModuleHandleW("opengl32.dll");
        if (_opengl32 == 0)
            _opengl32 = LoadLibraryW("opengl32.dll");

        GlVendor = Str(glGetString(GL_VENDOR));
        GlRenderer = Str(glGetString(GL_RENDERER));
        GlVersion = Str(glGetString(GL_VERSION));

        // Cadena de extensiones WGL (donde vive WGL_NV_DX_interop2)
        nint p = wglGetProcAddress("wglGetExtensionsStringARB");
        if (p == 0) p = wglGetProcAddress("wglGetExtensionsStringEXT");
        if (p != 0)
        {
            var fn = Marshal.GetDelegateForFunctionPointer<GetExtStringFn>(p);
            WglExtensions = Str(fn(_dc));
        }

        _dxOpen = Ext<DXOpenDeviceFn>("wglDXOpenDeviceNV");
        _dxClose = Ext<DXCloseDeviceFn>("wglDXCloseDeviceNV");
        _dxRegister = Ext<DXRegisterObjectFn>("wglDXRegisterObjectNV");
        _dxUnregister = Ext<DXUnregisterObjectFn>("wglDXUnregisterObjectNV");
        _dxLock = Ext<DXObjectsFn>("wglDXLockObjectsNV");
        _dxUnlock = Ext<DXObjectsFn>("wglDXUnlockObjectsNV");
        _dxSetShare = Ext<DXSetShareHandleFn>("wglDXSetResourceShareHandleNV");
        _genFramebuffers = Ext<GenFn>("glGenFramebuffers", "glGenFramebuffersEXT");
        _delFramebuffers = Ext<DeleteFn>("glDeleteFramebuffers", "glDeleteFramebuffersEXT");
        _bindFramebuffer = Ext<BindFramebufferFn>("glBindFramebuffer", "glBindFramebufferEXT");
        _fbTexture2D = Ext<FramebufferTexture2DFn>("glFramebufferTexture2D", "glFramebufferTexture2DEXT");
        _checkStatus = Ext<CheckFramebufferStatusFn>("glCheckFramebufferStatus", "glCheckFramebufferStatusEXT");
    }

    private static string Str(nint p) => p == 0 ? "?" : Marshal.PtrToStringAnsi(p) ?? "?";

    public bool HasExtension(string name) =>
        WglExtensions.Split(' ').Contains(name, StringComparer.Ordinal);

    private T Ext<T>(params string[] names) where T : Delegate
    {
        foreach (string name in names)
        {
            nint p = wglGetProcAddress(name);
            if (p == 0) p = GetProcAddress(_opengl32, name);
            if (p != 0) return Marshal.GetDelegateForFunctionPointer<T>(p);
        }
        throw new NotSupportedException($"El driver no expone {names[0]}.");
    }

    public nint GetGlProc(string name)
    {
        nint p = wglGetProcAddress(name);
        return p != 0 ? p : GetProcAddress(_opengl32, name);
    }

    /// <summary>Abre el device de interop sobre el device D3D11. Devuelve false
    /// si el driver no puede (el go/no-go nº1 del lab).</summary>
    public bool OpenDevice(nint d3d11Device)
    {
        _interopDevice = _dxOpen(d3d11Device);
        return _interopDevice != 0;
    }

    /// <summary>Registra un ID3D11Texture2D como GL_TEXTURE_2D y le ata un FBO.
    /// sharedHandle != 0 → se declara antes con wglDXSetResourceShareHandleNV.
    /// Devuelve null si el driver rechaza el registro o el FBO sale incompleto.
    /// </summary>
    public Registration? Register(nint texture, nint sharedHandle, int w, int h,
        out string detail)
    {
        detail = "";
        if (sharedHandle != 0 && !_dxSetShare(texture, sharedHandle))
        {
            detail = "wglDXSetResourceShareHandleNV devolvió false";
            return null;
        }

        var reg = new Registration { W = w, H = h };
        glGenTextures(1, out reg.GlTexture);
        reg.Handle = _dxRegister(_interopDevice, texture, reg.GlTexture,
            GL_TEXTURE_2D, WGL_ACCESS_READ_WRITE_NV);
        if (reg.Handle == 0)
        {
            detail = $"wglDXRegisterObjectNV falló (GetLastError=0x{Marshal.GetLastWin32Error():X})";
            glDeleteTextures(1, ref reg.GlTexture);
            return null;
        }

        _genFramebuffers(1, out reg.Fbo);
        _bindFramebuffer(GL_FRAMEBUFFER, reg.Fbo);
        Lock(reg);
        _fbTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, reg.GlTexture, 0);
        uint status = _checkStatus(GL_FRAMEBUFFER);
        Unlock(reg);
        _bindFramebuffer(GL_FRAMEBUFFER, 0);

        if (status != GL_FRAMEBUFFER_COMPLETE)
        {
            detail = $"FBO incompleto (0x{status:X})";
            _dxUnregister(_interopDevice, reg.Handle);
            glDeleteTextures(1, ref reg.GlTexture);
            return null;
        }

        _regs.Add(reg);
        return reg;
    }

    public void Lock(Registration r) => _dxLock(_interopDevice, 1, new[] { r.Handle });
    public void Unlock(Registration r) => _dxUnlock(_interopDevice, 1, new[] { r.Handle });

    /// <summary>Limpia la textura registrada a un color conocido desde GL.
    /// Entre Lock y Unlock: mientras está lockeada la posee GL, al soltarla
    /// vuelve a D3D11 (que es quien luego la muestrea o la lee).</summary>
    public void ClearFromGl(Registration r, float red, float green, float blue, float alpha)
    {
        Lock(r);
        _bindFramebuffer(GL_FRAMEBUFFER, r.Fbo);
        glViewport(0, 0, r.W, r.H);
        glClearColor(red, green, blue, alpha);
        glClear(GL_COLOR_BUFFER_BIT);
        glFinish();
        _bindFramebuffer(GL_FRAMEBUFFER, 0);
        Unlock(r);
    }

    /// <summary>Ejecuta un render arbitrario (mpv) contra el FBO de la textura,
    /// con el objeto lockeado. El callback recibe el id del FBO.</summary>
    public void RenderInto(Registration r, Action<uint> draw)
    {
        Lock(r);
        draw(r.Fbo);
        glFlush();
        glFinish();
        Unlock(r);
    }

    public uint LastGlError() => glGetError();

    public void MakeCurrent() => wglMakeCurrent(_dc, _glCtx);

    public void Dispose()
    {
        MakeCurrent();
        foreach (var r in _regs)
        {
            if (r.Handle != 0) _dxUnregister(_interopDevice, r.Handle);
            if (r.Fbo != 0) { uint f = r.Fbo; _delFramebuffers(1, ref f); }
            if (r.GlTexture != 0) { uint t = r.GlTexture; glDeleteTextures(1, ref t); }
        }
        _regs.Clear();
        if (_interopDevice != 0) _dxClose(_interopDevice);
        wglMakeCurrent(0, 0);
        if (_glCtx != 0) wglDeleteContext(_glCtx);
        if (_dc != 0) _ = ReleaseDC(_hwnd, _dc);
        if (_hwnd != 0) DestroyWindow(_hwnd);
        _glCtx = _dc = _hwnd = _interopDevice = 0;
    }
}
