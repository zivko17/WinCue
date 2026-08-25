// =============================================================================
// GlDxInterop.cs — Puente OpenGL ↔ Direct3D 9Ex para el compositor GPU
// =============================================================================
// mpv solo sabe renderizar por OpenGL (o software). WPF solo sabe componer
// superficies Direct3D 9 (D3DImage). Este archivo une ambos mundos con la
// extensión WGL_NV_DX_interop2 (NVIDIA/AMD/Intel la exponen desde hace una
// década): una render target de D3D9Ex COMPARTIDA se registra como
// renderbuffer de GL, mpv dibuja en él, y WPF/DWM lo compone en GPU con
// opacidad por capa. CERO copias por CPU.
//
// El COM de D3D9Ex se llama a mano por vtable (4 métodos): así no se añade
// ninguna dependencia NuGet al proyecto.
// =============================================================================

using System.Runtime.InteropServices;

namespace WinCue.Video;

// ---------------------------------------------------------------------------
// Direct3D 9Ex mínimo (COM por vtable, sin dependencias)
// ---------------------------------------------------------------------------

internal static class D3D9Minimal
{
    private const uint D3D_SDK_VERSION = 32;
    private const uint D3DCREATE_HARDWARE_VERTEXPROCESSING = 0x40;
    private const uint D3DCREATE_MULTITHREADED = 0x4;
    private const uint D3DCREATE_FPU_PRESERVE = 0x2;
    private const uint D3DFMT_A8R8G8B8 = 21;
    private const uint D3DSWAPEFFECT_DISCARD = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DPRESENT_PARAMETERS
    {
        public uint BackBufferWidth, BackBufferHeight, BackBufferFormat, BackBufferCount;
        public uint MultiSampleType, MultiSampleQuality;
        public uint SwapEffect;
        public nint hDeviceWindow;
        public int Windowed;
        public int EnableAutoDepthStencil;
        public uint AutoDepthStencilFormat;
        public uint Flags;
        public uint FullScreen_RefreshRateInHz, PresentationInterval;
    }

    [DllImport("d3d9.dll")]
    private static extern int Direct3DCreate9Ex(uint sdkVersion, out nint d3d9ex);

    // Slots de vtable (IUnknown = 0..2)
    private const int VtblCreateDeviceEx = 20;      // IDirect3D9Ex
    private const int VtblCreateRenderTarget = 28;  // IDirect3DDevice9
    private const int VtblStretchRect = 34;         // IDirect3DDevice9
    // ColorFill: contando IUnknown (3) mas los metodos de IDirect3DDevice9 en
    // el orden de d3d9.h hasta llegar a el, da 35 — no 42 como decia el plan.
    // Se confirma con los dos vecinos de arriba, que ya funcionan en
    // produccion: con esa misma cuenta CreateRenderTarget cae en 28 y
    // StretchRect en 34; 42 caeria en EndScene (firma sin argumentos), lo que
    // habria corrompido la pila con los argumentos de ColorFill.
    private const int VtblColorFill = 35;           // IDirect3DDevice9
    // CreateQuery: siguiendo la misma cuenta de d3d9.h (comentario de arriba)
    // hasta el ultimo metodo de IDirect3DDevice9, cae en 118.
    private const int VtblCreateQuery = 118;        // IDirect3DDevice9
    private const int VtblRelease = 2;              // IUnknown
    // IDirect3DQuery9 es OTRA interfaz COM (la que crea CreateQuery), con su
    // PROPIA vtable: IUnknown (0..2) + GetDevice(3) + GetType(4) +
    // GetDataSize(5) + Issue(6) + GetData(7).
    private const int VtblQueryIssue = 6;           // IDirect3DQuery9
    private const int VtblQueryGetData = 7;         // IDirect3DQuery9

    private const uint D3DQUERYTYPE_EVENT = 8;
    private const uint D3DISSUE_END = 2;
    private const uint D3DGETDATA_FLUSH = 1;
    private const int S_FALSE = 1;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDeviceExFn(nint self, uint adapter, uint deviceType,
        nint focusWindow, uint behaviorFlags, ref D3DPRESENT_PARAMETERS pp,
        nint fullscreenDisplayMode, out nint device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateRenderTargetFn(nint self, uint width, uint height,
        uint format, uint multiSample, uint quality, int lockable,
        out nint surface, ref nint sharedHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int StretchRectFn(nint self, nint srcSurface, nint srcRect,
        nint dstSurface, nint dstRect, uint filter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ColorFillFn(nint self, nint surface, nint rect, uint color);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateQueryFn(nint self, uint type, out nint query);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryIssueFn(nint self, uint issueFlags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryGetDataFn(nint self, nint data, uint size, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseFn(nint self);

    private static T Method<T>(nint comObject, int slot) where T : Delegate
    {
        nint vtbl = Marshal.ReadIntPtr(comObject);
        nint fn = Marshal.ReadIntPtr(vtbl, slot * nint.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    internal static void Release(nint comObject)
    {
        if (comObject != 0)
            Method<ReleaseFn>(comObject, VtblRelease)(comObject);
    }

    /// <summary>Copia GPU src→dst completa (D3DTEXF_NONE), mismo device.
    /// Aísla la superficie que WPF presenta de la registrada en el interop
    /// GL — WPF jamás debe tocar esa (auditoría 05: fuga de VRAM).</summary>
    internal static int StretchRect(nint device, nint src, nint dst) =>
        Method<StretchRectFn>(device, VtblStretchRect)(device, src, 0, dst, 0, 0);

    /// <summary>Rellena una superficie D3D9 entera de un color plano (D3DCOLOR
    /// = 0xAARRGGBB, mismo layout de bytes que B8G8R8A8). Solo lo usan las
    /// pruebas, para pintar la superficie compartida de un color conocido
    /// antes de verificar el puente por píxel.</summary>
    internal static int ColorFill(nint device, nint surface, uint color) =>
        Method<ColorFillFn>(device, VtblColorFill)(device, surface, 0, color);

    /// <summary>Fuerza a la GPU a terminar TODO lo pendiente en <paramref
    /// name="device"/> y espera (acotado) a que sea visible. SOLO lo llama el
    /// PRODUCTOR — quien acaba de escribir en la superficie compartida — justo
    /// antes de avisar al consumidor. Nunca el consumidor: eso convertiría un
    /// muestreo en una espera de GPU ajena.
    ///
    /// POR QUE HACE FALTA — el hallazgo caro de esta tarea: una superficie
    /// D3D9Ex compartida NO trae sincronización implícita entre devices. Un
    /// IDirect3DDevice9Ex y un ID3D11Device son colas de comandos DISTINTAS
    /// sobre la MISMA GPU física; que ColorFill/StretchRect devuelvan S_OK
    /// solo dice que el comando se ENCOLÓ, no que la GPU ya lo ejecutó. Medido
    /// en esta máquina (RTX 5070 Ti): sin este flush, leer desde D3D11
    /// inmediatamente después de escribir por D3D9 falla de forma NO
    /// determinista incluso con un Thread.Sleep(100) de por medio — así que no
    /// es "cuestión de darle tiempo", es la ausencia de una barrera real.
    ///
    /// CÓMO: un event query D3D9 (D3DQUERYTYPE_EVENT) es la forma estándar de
    /// pedirle al driver "avísame cuando la GPU haya vaciado la cola hasta
    /// aquí" — Issue(END) marca el punto, GetData(D3DGETDATA_FLUSH) fuerza el
    /// flush del command buffer y se sondea hasta que deja de devolver
    /// S_FALSE (pendiente).
    ///
    /// ESPERA ACOTADA, NO INDEFINIDA: esto puede acabar corriendo en el hilo
    /// que presenta durante un show en directo. Preferimos un fotograma
    /// repetido (el consumidor no ve bandera nueva y reenseña el anterior) a
    /// colgar ese hilo si el driver tarda o la query nunca se resuelve. Si no
    /// se pudo crear la query, o el plazo expira, se vuelve sin más: el
    /// llamante decide si aun así publica.</summary>
    internal static void FlushAndWait(nint device, int timeoutMs = 100)
    {
        int hrQuery = Method<CreateQueryFn>(device, VtblCreateQuery)(
            device, D3DQUERYTYPE_EVENT, out nint query);
        if (hrQuery < 0 || query == 0)
            return;   // sin query no hay barrera que esperar; el llamante decide

        try
        {
            Method<QueryIssueFn>(query, VtblQueryIssue)(query, D3DISSUE_END);

            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                int hrData = Method<QueryGetDataFn>(query, VtblQueryGetData)(
                    query, 0, 0, D3DGETDATA_FLUSH);
                if (hrData != S_FALSE)
                    return;   // S_OK (terminó) o error: en ambos casos, dejar de esperar
            }
        }
        finally
        {
            Release(query);
        }
    }

    /// <summary>Crea el dispositivo D3D9Ex (uno por renderer; las render
    /// targets compartidas se crean sobre él). Lanza si algo falla.</summary>
    internal static nint CreateDevice(nint focusHwnd)
    {
        int hr = Direct3DCreate9Ex(D3D_SDK_VERSION, out nint d3d);
        if (hr < 0 || d3d == 0)
            throw new InvalidOperationException($"Direct3DCreate9Ex falló (0x{hr:X8}).");

        try
        {
            var pp = new D3DPRESENT_PARAMETERS
            {
                Windowed = 1,
                SwapEffect = D3DSWAPEFFECT_DISCARD,
                BackBufferWidth = 1,
                BackBufferHeight = 1,
                BackBufferFormat = 0,   // D3DFMT_UNKNOWN
                BackBufferCount = 1,
                hDeviceWindow = focusHwnd,
            };
            hr = Method<CreateDeviceExFn>(d3d, VtblCreateDeviceEx)(
                d3d, 0 /*adapter*/, 1 /*D3DDEVTYPE_HAL*/, focusHwnd,
                D3DCREATE_HARDWARE_VERTEXPROCESSING | D3DCREATE_MULTITHREADED
                    | D3DCREATE_FPU_PRESERVE,
                ref pp, 0, out nint device);
            if (hr < 0)
                throw new InvalidOperationException($"CreateDeviceEx falló (0x{hr:X8}).");
            return device;
        }
        finally
        {
            Release(d3d);   // el device retiene lo que necesita
        }
    }

    /// <summary>Render target COMPARTIDA (w×h ARGB) sobre un device existente.
    /// Devuelve (surface, sharedHandle). Lanza si algo falla.</summary>
    internal static (nint Surface, nint SharedHandle) CreateSharedRenderTarget(
        nint device, int width, int height)
    {
        nint shared = 0;
        int hr = Method<CreateRenderTargetFn>(device, VtblCreateRenderTarget)(
            device, (uint)width, (uint)height, D3DFMT_A8R8G8B8,
            0 /*sin multisample*/, 0, 0 /*no lockable*/,
            out nint surface, ref shared);
        if (hr < 0)
            throw new InvalidOperationException($"CreateRenderTarget falló (0x{hr:X8}).");
        return (surface, shared);
    }
}

// ---------------------------------------------------------------------------
// WGL: ventana oculta + contexto GL + extensión WGL_NV_DX_interop2 + FBO
// ---------------------------------------------------------------------------

// CA2216 (finalizador de respaldo): NO añadir. El contexto GL tiene afinidad
// estricta de hilo (todo se crea y destruye en el hilo de render con el
// contexto current); un finalizador correría en el hilo del GC → comportamiento
// indefinido en el driver. La liberación es determinista vía Dispose().
#pragma warning disable CA2216
internal sealed class GlContext : IDisposable
{
#pragma warning restore CA2216
    // ── user32 / gdi32 / opengl32 ──────────────────────────────────────
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className,
        string windowName, uint style, int x, int y, int w, int h,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint dc);

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
    private static extern bool SetPixelFormat(nint dc, int format, ref PIXELFORMATDESCRIPTOR pfd);

    [DllImport("opengl32.dll")]
    private static extern nint wglCreateContext(nint dc);

    [DllImport("opengl32.dll")]
    private static extern bool wglDeleteContext(nint ctx);

    [DllImport("opengl32.dll")]
    private static extern bool wglMakeCurrent(nint dc, nint ctx);

    // CA2101: marshaling ANSI explícito y sin best-fit (los nombres de proc GL
    // son ASCII puro; best-fit podría mapear caracteres inesperados).
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi,
        BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern nint wglGetProcAddress(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi,
        BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern nint GetProcAddress(nint module, string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string name);

    [DllImport("opengl32.dll")]
    internal static extern void glFlush();

    // ── Extensiones (se resuelven con el contexto current) ─────────────
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate nint DXOpenDeviceFn(nint dxDevice);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate bool DXCloseDeviceFn(nint hDevice);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate nint DXRegisterObjectFn(nint hDevice, nint dxObject, uint name, uint type, uint access);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate bool DXUnregisterObjectFn(nint hDevice, nint hObject);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate bool DXLockObjectsFn(nint hDevice, int count, nint[] objects);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate bool DXSetShareHandleFn(nint dxObject, nint shareHandle);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GenFn(int n, out uint name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void DeleteFn(int n, ref uint name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void BindFramebufferFn(uint target, uint fbo);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void BindRenderbufferFn(uint target, uint rb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void RenderbufferStorageFn(uint target, uint internalFormat, int w, int h);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void BlitFramebufferFn(int sx0, int sy0, int sx1, int sy1, int dx0, int dy0, int dx1, int dy1, uint mask, uint filter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FramebufferRenderbufferFn(uint target, uint attachment, uint rbTarget, uint rb);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint CheckFramebufferStatusFn(uint target);

    private const uint GL_FRAMEBUFFER = 0x8D40;
    private const uint GL_READ_FRAMEBUFFER = 0x8CA8;
    private const uint GL_DRAW_FRAMEBUFFER = 0x8CA9;
    private const uint GL_RENDERBUFFER = 0x8D41;
    private const uint GL_COLOR_ATTACHMENT0 = 0x8CE0;
    private const uint GL_FRAMEBUFFER_COMPLETE = 0x8CD5;
    private const uint GL_RGBA8 = 0x8058;
    private const uint GL_COLOR_BUFFER_BIT = 0x4000;
    private const uint GL_NEAREST = 0x2600;
    private const uint WGL_ACCESS_READ_WRITE_NV = 0x0001;

    private DXOpenDeviceFn _dxOpen = null!;
    private DXCloseDeviceFn _dxClose = null!;
    private DXRegisterObjectFn _dxRegister = null!;
    private DXUnregisterObjectFn _dxUnregister = null!;
    private DXLockObjectsFn _dxLock = null!;
    private DXLockObjectsFn _dxUnlock = null!;
    private DXSetShareHandleFn _dxSetShare = null!;
    private GenFn _genFramebuffers = null!, _genRenderbuffers = null!;
    private DeleteFn _delFramebuffers = null!, _delRenderbuffers = null!;
    private BindFramebufferFn _bindFramebuffer = null!;
    private BindRenderbufferFn _bindRenderbuffer = null!;
    private RenderbufferStorageFn _renderbufferStorage = null!;
    private BlitFramebufferFn _blitFramebuffer = null!;
    private FramebufferRenderbufferFn _fbRenderbuffer = null!;
    private CheckFramebufferStatusFn _checkStatus = null!;

    private nint _hwnd, _dc, _glCtx;
    private readonly nint _opengl32 = GetModuleHandleW("opengl32.dll");

    private nint _interopDevice;
    private uint _privateRb;
    private int _canvasW, _canvasH;

    /// <summary>Una VISTA de salida: FBO ligado a su superficie D3D compartida
    /// (destino del blit) + el sub-rectángulo del canvas que muestra.</summary>
    private sealed class View
    {
        public nint InteropObject;
        public uint Renderbuffer;
        public uint Fbo;
        public int W, H;        // tamaño de la superficie de la vista
        public int SrcX, SrcY;  // origen del recorte en el canvas (arriba-izq)
    }

    private readonly List<View> _views = new();
    private nint[] _lockObjects = Array.Empty<nint>();   // cacheado para el lock

    /// <summary>FBO PRIVADO (tamaño del canvas) donde renderiza mpv: las
    /// superficies compartidas solo reciben frames COMPLETOS vía blit (sin
    /// él, el DWM podía capturar el lienzo recién limpiado por mpv → cortes
    /// a negro).</summary>
    public uint PrivateFbo { get; private set; }

    /// <summary>Crea ventana oculta + contexto GL y lo deja CURRENT en el
    /// hilo llamante. Lanza si el driver no da OpenGL.</summary>
    public GlContext()
    {
        _hwnd = CreateWindowExW(0, "STATIC", "WinCueGL", 0, 0, 0, 1, 1,
            0, 0, 0, 0);
        if (_hwnd == 0)
            throw new InvalidOperationException("No se pudo crear la ventana GL oculta.");
        _dc = GetDC(_hwnd);

        var pfd = new PIXELFORMATDESCRIPTOR
        {
            nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(),
            nVersion = 1,
            dwFlags = 0x4 /*DRAW_TO_WINDOW*/ | 0x20 /*SUPPORT_OPENGL*/,
            iPixelType = 0 /*RGBA*/,
            cColorBits = 32,
        };
        int fmt = ChoosePixelFormat(_dc, ref pfd);
        if (fmt == 0 || !SetPixelFormat(_dc, fmt, ref pfd))
            throw new InvalidOperationException("SetPixelFormat falló.");

        _glCtx = wglCreateContext(_dc);
        if (_glCtx == 0 || !wglMakeCurrent(_dc, _glCtx))
            throw new InvalidOperationException("No se pudo crear el contexto OpenGL.");

        _dxOpen = Ext<DXOpenDeviceFn>("wglDXOpenDeviceNV");
        _dxClose = Ext<DXCloseDeviceFn>("wglDXCloseDeviceNV");
        _dxRegister = Ext<DXRegisterObjectFn>("wglDXRegisterObjectNV");
        _dxUnregister = Ext<DXUnregisterObjectFn>("wglDXUnregisterObjectNV");
        _dxLock = Ext<DXLockObjectsFn>("wglDXLockObjectsNV");
        _dxUnlock = Ext<DXLockObjectsFn>("wglDXUnlockObjectsNV");
        _dxSetShare = Ext<DXSetShareHandleFn>("wglDXSetResourceShareHandleNV");
        _genFramebuffers = Ext<GenFn>("glGenFramebuffersEXT", "glGenFramebuffers");
        _genRenderbuffers = Ext<GenFn>("glGenRenderbuffersEXT", "glGenRenderbuffers");
        _delFramebuffers = Ext<DeleteFn>("glDeleteFramebuffersEXT", "glDeleteFramebuffers");
        _delRenderbuffers = Ext<DeleteFn>("glDeleteRenderbuffersEXT", "glDeleteRenderbuffers");
        _bindFramebuffer = Ext<BindFramebufferFn>("glBindFramebufferEXT", "glBindFramebuffer");
        _bindRenderbuffer = Ext<BindRenderbufferFn>(
            "glBindRenderbufferEXT", "glBindRenderbuffer");
        _renderbufferStorage = Ext<RenderbufferStorageFn>(
            "glRenderbufferStorageEXT", "glRenderbufferStorage");
        _blitFramebuffer = Ext<BlitFramebufferFn>(
            "glBlitFramebuffer", "glBlitFramebufferEXT");
        _fbRenderbuffer = Ext<FramebufferRenderbufferFn>(
            "glFramebufferRenderbufferEXT", "glFramebufferRenderbuffer");
        _checkStatus = Ext<CheckFramebufferStatusFn>(
            "glCheckFramebufferStatusEXT", "glCheckFramebufferStatus");
    }

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
            $"El driver OpenGL no expone {names[0]} (¿sin WGL_NV_DX_interop?).");
    }

    /// <summary>get_proc_address para mpv (mismo mecanismo).</summary>
    public nint GetGlProc(string name)
    {
        nint p = wglGetProcAddress(name);
        return p != 0 ? p : GetProcAddress(_opengl32, name);
    }

    /// <summary>Crea el FBO PRIVADO del tamaño del canvas donde renderiza mpv.
    /// Llamar UNA vez, con el contexto current (mismo hilo del ctor).</summary>
    public void CreatePrivateFbo(int canvasW, int canvasH)
    {
        _canvasW = canvasW;
        _canvasH = canvasH;
        _genRenderbuffers(1, out _privateRb);
        _bindRenderbuffer(GL_RENDERBUFFER, _privateRb);
        _renderbufferStorage(GL_RENDERBUFFER, GL_RGBA8, canvasW, canvasH);
        _genFramebuffers(1, out uint priv);
        PrivateFbo = priv;
        _bindFramebuffer(GL_FRAMEBUFFER, priv);
        _fbRenderbuffer(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_RENDERBUFFER, _privateRb);
        uint privStatus = _checkStatus(GL_FRAMEBUFFER);
        _bindFramebuffer(GL_FRAMEBUFFER, 0);
        if (privStatus != GL_FRAMEBUFFER_COMPLETE)
            throw new NotSupportedException($"FBO privado incompleto (0x{privStatus:X}).");
    }

    /// <summary>Registra una superficie D3D9 compartida como VISTA: recibirá
    /// por blit el recorte (srcX, srcY, w, h) del canvas (origen arriba-izq).
    /// Devuelve el índice de la vista. Contexto current requerido.</summary>
    public int AddView(nint d3dDevice, nint d3dSurface, nint sharedHandle,
        int w, int h, int srcX, int srcY)
    {
        if (_interopDevice == 0)
        {
            _interopDevice = _dxOpen(d3dDevice);
            if (_interopDevice == 0)
                throw new NotSupportedException(
                    "wglDXOpenDeviceNV falló: el driver no soporta interop GL↔D3D.");
        }

        if (sharedHandle != 0)
            _dxSetShare(d3dSurface, sharedHandle);

        var view = new View { W = w, H = h, SrcX = srcX, SrcY = srcY };
        _genRenderbuffers(1, out view.Renderbuffer);
        view.InteropObject = _dxRegister(_interopDevice, d3dSurface, view.Renderbuffer,
            GL_RENDERBUFFER, WGL_ACCESS_READ_WRITE_NV);
        if (view.InteropObject == 0)
            throw new NotSupportedException("wglDXRegisterObjectNV falló.");

        _genFramebuffers(1, out view.Fbo);
        _bindFramebuffer(GL_FRAMEBUFFER, view.Fbo);
        LockOne(view.InteropObject);
        _fbRenderbuffer(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_RENDERBUFFER, view.Renderbuffer);
        uint status = _checkStatus(GL_FRAMEBUFFER);
        UnlockOne(view.InteropObject);
        _bindFramebuffer(GL_FRAMEBUFFER, 0);
        if (status != GL_FRAMEBUFFER_COMPLETE)
            throw new NotSupportedException($"FBO de vista incompleto (0x{status:X}).");

        _views.Add(view);
        _lockObjects = _views.Select(v => v.InteropObject).ToArray();
        return _views.Count - 1;
    }

    /// <summary>Copia el frame del FBO privado (canvas) a las superficies D3D
    /// de todas las vistas, cada una con su recorte, bajo UN solo lock del
    /// interop. El DWM nunca ve un frame a medias y las salidas presentan el
    /// MISMO frame (sync perfecta entre monitores).</summary>
    public void PresentFrame()
    {
        if (_lockObjects.Length == 0)
            return;
        _dxLock(_interopDevice, _lockObjects.Length, _lockObjects);
        _bindFramebuffer(GL_READ_FRAMEBUFFER, PrivateFbo);
        foreach (var v in _views)
        {
            // glBlitFramebuffer usa origen ABAJO-izquierda: la Y del recorte
            // (canvas, arriba-izquierda) se convierte a Y de GL.
            int srcY0 = _canvasH - v.SrcY - v.H;
            _bindFramebuffer(GL_DRAW_FRAMEBUFFER, v.Fbo);
            _blitFramebuffer(v.SrcX, srcY0, v.SrcX + v.W, srcY0 + v.H,
                0, 0, v.W, v.H, GL_COLOR_BUFFER_BIT, GL_NEAREST);
        }
        _bindFramebuffer(GL_READ_FRAMEBUFFER, 0);
        _bindFramebuffer(GL_DRAW_FRAMEBUFFER, 0);
        glFlush();
        _dxUnlock(_interopDevice, _lockObjects.Length, _lockObjects);
    }

    private void LockOne(nint obj) => _dxLock(_interopDevice, 1, new[] { obj });
    private void UnlockOne(nint obj) => _dxUnlock(_interopDevice, 1, new[] { obj });

    public void MakeCurrent() => wglMakeCurrent(_dc, _glCtx);

    public void Dispose()
    {
        MakeCurrent();
        foreach (var v in _views)
        {
            if (v.InteropObject != 0)
                _dxUnregister(_interopDevice, v.InteropObject);
            if (v.Renderbuffer != 0)
                _delRenderbuffers(1, ref v.Renderbuffer);
            if (v.Fbo != 0)
            {
                uint fbo = v.Fbo;
                _delFramebuffers(1, ref fbo);
            }
        }
        _views.Clear();
        _lockObjects = Array.Empty<nint>();
        if (_privateRb != 0)
            _delRenderbuffers(1, ref _privateRb);
        if (PrivateFbo != 0)
        {
            uint priv = PrivateFbo;
            _delFramebuffers(1, ref priv);
        }
        if (_interopDevice != 0)
            _dxClose(_interopDevice);
        wglMakeCurrent(0, 0);
        if (_glCtx != 0)
            wglDeleteContext(_glCtx);
        if (_dc != 0)
            _ = ReleaseDC(_hwnd, _dc);   // 0 = fallo; en Dispose no hay recuperación posible
        if (_hwnd != 0)
            DestroyWindow(_hwnd);
        _glCtx = 0;
        _dc = 0;
        _hwnd = 0;
    }
}
