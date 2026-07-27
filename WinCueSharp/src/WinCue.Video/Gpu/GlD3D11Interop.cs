// =============================================================================
// GlD3D11Interop.cs — Puente OpenGL ↔ Direct3D 11 (capa L1)
// =============================================================================
// Promovido de tools/mpv-d3d11-lab tras el informe en verde. Sustituye, para
// la ruta nueva, al puente GL↔D3D9Ex de GlDxInterop.cs.
//
// DIFERENCIA CLAVE CON LA RUTA VIEJA: WGL_NV_DX_interop2 admite objetos D3D11,
// pero un ID3D11Texture2D SOLO puede registrarse como GL_TEXTURE_2D (con D3D9
// se usaba GL_RENDERBUFFER). El FBO se ata con glFramebufferTexture2D.
// La variante validada en el lab: textura creada Shared, handle sacado por
// IDXGIResource y declarado con wglDXSetResourceShareHandleNV antes de
// registrar.
//
// AFINIDAD DE HILO: crear, registrar, lock/render/unlock y destruir van SIEMPRE
// en el hilo que tiene el contexto GL current.
// =============================================================================

using System.Runtime.InteropServices;

namespace WinCue.Video.Gpu;

// CA2216: sin finalizador de respaldo a propósito — el contexto GL tiene
// afinidad estricta de hilo y un finalizador correría en el del GC.
#pragma warning disable CA2216
internal sealed class GlD3D11Interop : IDisposable
{
#pragma warning restore CA2216
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
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi,
        BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern nint wglGetProcAddress(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi,
        BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadLibraryW(string name);

    [DllImport("opengl32.dll")] private static extern nint glGetString(uint name);
    [DllImport("opengl32.dll")] private static extern void glGenTextures(int n, out uint tex);
    [DllImport("opengl32.dll")] private static extern void glDeleteTextures(int n, ref uint tex);
    [DllImport("opengl32.dll")] private static extern void glFlush();
    [DllImport("opengl32.dll")] private static extern void glFinish();

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

    private const uint GL_TEXTURE_2D = 0x0DE1;
    private const uint GL_FRAMEBUFFER = 0x8D40;
    private const uint GL_COLOR_ATTACHMENT0 = 0x8CE0;
    private const uint GL_FRAMEBUFFER_COMPLETE = 0x8CD5;
    private const uint WGL_ACCESS_READ_WRITE_NV = 0x0001;
    private const uint GL_RENDERER = 0x1F01, GL_VERSION = 0x1F02;

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

    /// <summary>NO puede ser inicializador de campo: corren ANTES del cuerpo
    /// del constructor, y hasta que wglCreateContext no carga opengl32.dll en
    /// el proceso, GetModuleHandleW devuelve 0 — y con él quedan sin resolver
    /// TODAS las funciones core de GL 1.1, que wglGetProcAddress no devuelve
    /// nunca por diseño. mpv pide glGetString lo primero y rechaza el contexto
    /// con "not supported". (Hallazgo nº 4 del informe del lab; la ruta vieja
    /// se libra por casualidad de orden de carga, no por diseño.)</summary>
    private nint _opengl32;

    private sealed class Registration
    {
        public nint Handle;
        public uint GlTexture;
        public uint Fbo;

        /// <summary>Array de UN elemento para lock/unlock, creado al registrar.
        /// El API los pide como array y construirlo por fotograma sería basura
        /// gen0 en el hilo de render — justo lo que no puede haber si se quiere
        /// que el GC no aparezca nunca en mitad de un frame.</summary>
        public nint[] LockSet = Array.Empty<nint>();
    }

    private readonly List<Registration> _regs = new();

    public string GlRenderer { get; private set; } = "?";
    public string GlVersion { get; private set; } = "?";
    public string WglExtensions { get; private set; } = "";

    public GlD3D11Interop()
    {
        _hwnd = CreateWindowExW(0, "STATIC", "WinCueGL11", 0, 0, 0, 1, 1, 0, 0, 0, 0);
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

        _opengl32 = GetModuleHandleW("opengl32.dll");
        if (_opengl32 == 0)
            _opengl32 = LoadLibraryW("opengl32.dll");

        GlRenderer = Str(glGetString(GL_RENDERER));
        GlVersion = Str(glGetString(GL_VERSION));

        nint p = wglGetProcAddress("wglGetExtensionsStringARB");
        if (p == 0)
            p = wglGetProcAddress("wglGetExtensionsStringEXT");
        if (p != 0)
            WglExtensions = Str(Marshal.GetDelegateForFunctionPointer<GetExtStringFn>(p)(_dc));

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

    public bool HasInterop2 =>
        WglExtensions.Split(' ').Contains("WGL_NV_DX_interop2", StringComparer.Ordinal);

    private T Ext<T>(params string[] names) where T : Delegate
    {
        foreach (string name in names)
        {
            nint p = wglGetProcAddress(name);
            if (p == 0)
                p = GetProcAddress(_opengl32, name);
            if (p != 0)
                return Marshal.GetDelegateForFunctionPointer<T>(p);
        }
        throw new NotSupportedException(
            $"El driver OpenGL no expone {names[0]} (¿sin WGL_NV_DX_interop2?).");
    }

    /// <summary>get_proc_address para mpv.</summary>
    public nint GetGlProc(string name)
    {
        nint p = wglGetProcAddress(name);
        return p != 0 ? p : GetProcAddress(_opengl32, name);
    }

    /// <summary>Abre el interop sobre el device D3D11 del motor. Una vez por
    /// puente. Lanza si el driver no puede (fallback limpio del llamante).</summary>
    public void OpenDevice(nint d3d11Device)
    {
        _interopDevice = _dxOpen(d3d11Device);
        if (_interopDevice == 0)
        {
            // DOS causas, y conviene distinguirlas en el mensaje porque llevan
            // a arreglos opuestos:
            //   · el driver no trae interop2 → no hay motor nuevo en esa máquina
            //   · máquina híbrida: el contexto GL salió de OTRA GPU distinta de
            //     la del device D3D11 (Windows decide qué GPU le da al proceso).
            //     Ahí el driver sí puede; lo que falla es el emparejado.
            string extra = HasInterop2
                ? $" El driver SÍ expone WGL_NV_DX_interop2 (GL: {GlRenderer}), " +
                  "así que lo más probable es que el contexto OpenGL y el device " +
                  "D3D11 estén en GPUs distintas (equipo con integrada + dedicada)."
                : " El driver no expone WGL_NV_DX_interop2.";
            throw new NotSupportedException(
                "wglDXOpenDeviceNV falló contra el device D3D11." + extra);
        }
    }

    /// <summary>Registra una textura D3D11 como GL_TEXTURE_2D y le ata un FBO.
    /// Devuelve el índice de registro (paralelo al slot del anillo).</summary>
    public int Register(nint texture, nint sharedHandle)
    {
        if (sharedHandle != 0 && !_dxSetShare(texture, sharedHandle))
            throw new NotSupportedException("wglDXSetResourceShareHandleNV devolvió false.");

        var reg = new Registration();
        glGenTextures(1, out reg.GlTexture);
        reg.Handle = _dxRegister(_interopDevice, texture, reg.GlTexture,
            GL_TEXTURE_2D, WGL_ACCESS_READ_WRITE_NV);
        if (reg.Handle == 0)
        {
            glDeleteTextures(1, ref reg.GlTexture);
            throw new NotSupportedException(
                $"wglDXRegisterObjectNV falló (0x{Marshal.GetLastWin32Error():X}).");
        }

        reg.LockSet = new[] { reg.Handle };
        _genFramebuffers(1, out reg.Fbo);
        _bindFramebuffer(GL_FRAMEBUFFER, reg.Fbo);
        _dxLock(_interopDevice, 1, reg.LockSet);
        _fbTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, reg.GlTexture, 0);
        uint status = _checkStatus(GL_FRAMEBUFFER);
        _dxUnlock(_interopDevice, 1, reg.LockSet);
        _bindFramebuffer(GL_FRAMEBUFFER, 0);

        if (status != GL_FRAMEBUFFER_COMPLETE)
        {
            _dxUnregister(_interopDevice, reg.Handle);
            glDeleteTextures(1, ref reg.GlTexture);
            throw new NotSupportedException($"FBO de la textura interop incompleto (0x{status:X}).");
        }

        _regs.Add(reg);
        return _regs.Count - 1;
    }

    /// <summary>Ejecuta el dibujo sobre el FBO del registro, con el objeto
    /// lockeado: entre Lock y Unlock la textura es de GL; al soltarla vuelve a
    /// D3D11, que es quien la muestrea.</summary>
    public void RenderInto(int index, Action<uint> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        Registration reg = _regs[index];
        _dxLock(_interopDevice, 1, reg.LockSet);
        try
        {
            draw(reg.Fbo);
            glFlush();
            glFinish();
        }
        finally
        {
            _dxUnlock(_interopDevice, 1, reg.LockSet);
        }
    }

    public void Dispose()
    {
        wglMakeCurrent(_dc, _glCtx);
        foreach (Registration r in _regs)
        {
            if (r.Handle != 0)
                _dxUnregister(_interopDevice, r.Handle);
            if (r.Fbo != 0)
            {
                uint f = r.Fbo;
                _delFramebuffers(1, ref f);
            }
            if (r.GlTexture != 0)
            {
                uint t = r.GlTexture;
                glDeleteTextures(1, ref t);
            }
        }
        _regs.Clear();
        if (_interopDevice != 0)
            _dxClose(_interopDevice);
        wglMakeCurrent(0, 0);
        if (_glCtx != 0)
            wglDeleteContext(_glCtx);
        if (_dc != 0)
            _ = ReleaseDC(_hwnd, _dc);
        if (_hwnd != 0)
            DestroyWindow(_hwnd);
        _glCtx = _dc = _hwnd = _interopDevice = 0;
    }
}
