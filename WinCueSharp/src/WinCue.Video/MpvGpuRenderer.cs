// =============================================================================
// MpvGpuRenderer.cs — Pipeline GPU PERSISTENTE del compositor (auditoría 05)
// =============================================================================
// Une las piezas del compositor: mpv (render API OpenGL) dibuja cada frame en
// un FBO cuyo color attachment ES una render target de Direct3D 9Ex
// compartida (WGL_NV_DX_interop). WPF muestra esa superficie con D3DImage y
// el DWM compone las capas con opacidad — cero copias por CPU.
//
// PERSISTENTE (auditoría 05): la sesión de estrés demostró que crear y
// destruir este pipeline POR CUE fuga la VRAM del ciclo entero — con
// D3DImage presentando via dirty-rects, el desmontaje del interop GL↔D3D no
// libera de verdad (driver). Por eso el pipeline (hilo + contexto GL + device
// D3D9Ex + superficies compartidas) se crea UNA vez y vive toda la sesión;
// por cue solo se crea/libera el mpv_render_context via AttachPlayer/
// DetachPlayer. VideoEngine mantiene el pool de pipelines por (salida,
// pista, spec).
//
// HILOS: un hilo dedicado ("mpv-gpu") posee el contexto GL de principio a
// fin: crea todo, ejecuta attach/detach de sesiones mpv, renderiza en cada
// aviso y destruye todo al morir el pipeline. El constructor BLOQUEA hasta
// que el pipeline está listo (o lanza si el driver no puede, p.ej. sin
// WGL_NV_DX_interop) para que el engine haga fallback limpio.
// =============================================================================

using System.Runtime.InteropServices;
using WinCue.Core;
using static WinCue.Video.MpvRenderNative;

namespace WinCue.Video;

public sealed class MpvGpuRenderer : IDisposable
{
    private readonly int _width, _height;          // tamaño del canvas (FBO de mpv)
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly AutoResetEvent _opDone = new(false);
    private volatile bool _shutdown;
    private Exception? _initError;

    // CA2213: _gl SÍ se libera, pero en CleanupOnThread() (hilo de render, el
    // único que puede tocar el contexto GL) — el analizador no puede verlo.
#pragma warning disable CA2213
    private GlContext? _gl;
#pragma warning restore CA2213
    private nint _renderCtx;
    private nint _d3dDevice;

    // Sesión por cue (attach/detach): el hilo procesa la petición pendiente
    private MpvPlayer? _attachRequest;
    private volatile bool _detachRequest;
    private Exception? _attachError;
    private readonly object _opLock = new();       // serializa attach/detach

    // Vistas de salida: cada una tiene DOS superficies D3D (auditoría 05):
    //   _viewSurfaces   → registrada en el interop GL (mpv blitea aquí);
    //                     WPF JAMÁS la toca (tocarla fugaba la VRAM)
    //   _presentSurfaces→ copia por StretchRect que presenta el D3DImage
    private readonly IReadOnlyList<CanvasView> _viewSpecs;
    private readonly nint[] _viewSurfaces;
    private readonly nint[] _presentSurfaces;

    // Delegados nativos: deben permanecer vivos mientras mpv los use
    private readonly GetProcAddressFn _getProc;
    private readonly RenderUpdateFn _updateCb;

    /// <summary>Nuevo frame en las superficies (¡llega en el hilo de render!).</summary>
    public event Action? FrameRendered;

    /// <summary>Puntero IDirect3DSurface9 de PRESENTACIÓN de la primera vista
    /// (modo clásico de una sola salida) para D3DImage.SetBackBuffer.</summary>
    public nint Surface => _presentSurfaces[0];

    /// <summary>Superficie de PRESENTACIÓN de la vista i (mismo orden que las
    /// vistas pasadas al constructor). Nunca la registrada en el interop.</summary>
    public nint ViewSurface(int i) => _presentSurfaces[i];

    public IReadOnlyList<CanvasView> Views => _viewSpecs;

    public int Width => _width;
    public int Height => _height;

    /// <summary>true si hay una sesión mpv activa (entre Attach y Detach).</summary>
    public bool HasSession { get; private set; }

    // Serializa el blit GL→interop (hilo de render) con la copia de
    // presentación (hilo de UI, dentro del Lock del D3DImage)
    private readonly object _copySync = new();

    /// <summary>Copia GPU interop→presentación de la vista i. DEBE llamarse
    /// con el D3DImage.Lock() tomado (contrato de D3DImage: toda escritura al
    /// back buffer va dentro del Lock — escribir sin él provoca renaming del
    /// driver: UNA asignación de VRAM del tamaño del frame POR FRAME, la fuga
    /// de la auditoría 05).</summary>
    public void CopyPresent(int i)
    {
        lock (_copySync)
        {
            if (_shutdown || _viewSurfaces[i] == 0 || _presentSurfaces[i] == 0)
                return;
            _ = D3D9Minimal.StretchRect(_d3dDevice, _viewSurfaces[i], _presentSurfaces[i]);
        }
    }

    /// <param name="views">Recortes del canvas por salida (modo canvas);
    /// null = una única vista con el canvas completo (modo clásico).</param>
    public MpvGpuRenderer(int width, int height,
        IReadOnlyList<CanvasView>? views = null)
    {
        _width = Math.Max(16, width);
        _height = Math.Max(16, height);
        _viewSpecs = views is { Count: > 0 }
            ? views
            : new[] { new CanvasView(0, 0, 0, _width, _height) };
        _viewSurfaces = new nint[_viewSpecs.Count];
        _presentSurfaces = new nint[_viewSpecs.Count];
        _getProc = OnGetProcAddress;
        _updateCb = _ => _wake.Set();

        _thread = new Thread(RenderThread)
        {
            IsBackground = true,
            Name = "mpv-gpu",
        };
        _thread.Start();

        // Esperar el pipeline (GL + D3D + interop)
        if (!_ready.Wait(TimeSpan.FromSeconds(8)))
        {
            Dispose();
            throw new InvalidOperationException("Compositor: timeout inicializando GL/D3D.");
        }
        if (_initError is not null)
        {
            var exc = _initError;
            Dispose();
            throw exc;
        }
    }

    // ------------------------------------------------------------------
    // Sesión por cue (main thread; la creación/liberación real del
    // mpv_render_context ocurre EN el hilo de render — regla de mpv)
    // ------------------------------------------------------------------

    /// <summary>Crea el mpv_render_context del player en el hilo del pipeline
    /// y empieza a renderizarlo. Bloquea hasta que está listo (o lanza).</summary>
    public void AttachPlayer(MpvPlayer player)
    {
        lock (_opLock)
        {
            if (HasSession)
                throw new InvalidOperationException("El pipeline ya tiene una sesión activa.");
            _attachError = null;
            _attachRequest = player;
            _wake.Set();
            if (!_opDone.WaitOne(TimeSpan.FromSeconds(8)))
            {
                _attachRequest = null;
                throw new InvalidOperationException("Compositor: timeout creando el render context.");
            }
            // CA1508: _attachError lo escribe el HILO DE RENDER antes de
            // señalar _opDone (el WaitOne es la barrera) — el analizador no ve
            // el flujo entre hilos y cree que siempre es null.
#pragma warning disable CA1508
            if (_attachError is not null)
                throw _attachError;
#pragma warning restore CA1508
            HasSession = true;
        }
    }

    /// <summary>Libera el mpv_render_context en el hilo del pipeline.
    /// SIEMPRE antes de destruir el MpvPlayer (regla de mpv). Devuelve false
    /// si el hilo no respondió (pipeline dañado: el llamante debe descartarlo
    /// con Dispose y NO reutilizarlo).</summary>
    public bool DetachPlayer()
    {
        lock (_opLock)
        {
            if (!HasSession)
                return true;
            _detachRequest = true;
            _wake.Set();
            bool ok = _opDone.WaitOne(TimeSpan.FromSeconds(4));
            HasSession = false;
            return ok;
        }
    }

    private nint OnGetProcAddress(nint ctx, nint namePtr)
    {
        string? name = Marshal.PtrToStringUTF8(namePtr);
        return name is null ? 0 : _gl!.GetGlProc(name);
    }

    // ------------------------------------------------------------------
    // Hilo de render: dueño del contexto GL de principio a fin
    // ------------------------------------------------------------------

    private void RenderThread()
    {
        try
        {
            _gl = new GlContext();
            _d3dDevice = D3D9Minimal.CreateDevice(0);
            for (int i = 0; i < _viewSpecs.Count; i++)
            {
                var v = _viewSpecs[i];
                var (surface, shared) =
                    D3D9Minimal.CreateSharedRenderTarget(_d3dDevice, v.W, v.H);
                _viewSurfaces[i] = surface;
                _gl.AddView(_d3dDevice, surface, shared, v.W, v.H, v.X, v.Y);
                // Superficie de PRESENTACIÓN (sin registro GL): es la única
                // que ve WPF — tocar la del interop fugaba VRAM (auditoría 05)
                var (present, _) =
                    D3D9Minimal.CreateSharedRenderTarget(_d3dDevice, v.W, v.H);
                _presentSurfaces[i] = present;
            }
            _gl.CreatePrivateFbo(_width, _height);
            _ready.Set();
        }
        catch (Exception exc)
        {
            _initError = exc;
            _ready.Set();
            CleanupOnThread();
            return;
        }

        // Bucle: attach/detach de sesiones + render. mpv avisa por el update
        // callback → renderiza en el FBO PRIVADO y se presenta el frame
        // completo con un blit a la superficie compartida (así el DWM nunca
        // captura un frame a medias → sin cortes a negro). flip_y=0: la
        // orientación la fija el propio pipeline D3D.
        var fbo = new MpvOpenGlFbo { Fbo = (int)_gl.PrivateFbo, W = _width, H = _height };
        nint fboMem = Marshal.AllocHGlobal(Marshal.SizeOf<MpvOpenGlFbo>());
        nint flipMem = Marshal.AllocHGlobal(sizeof(int));
        Marshal.StructureToPtr(fbo, fboMem, false);
        Marshal.WriteInt32(flipMem, 0);
        nint renderParams = BuildParams(
            (ParamOpenGlFbo, fboMem),
            (ParamFlipY, flipMem));

        try
        {
            while (!_shutdown)
            {
                _wake.WaitOne(100);
                if (_shutdown)
                    break;

                // ── Attach: crear el render context de la nueva sesión ──
                if (_attachRequest is { } player)
                {
                    _attachRequest = null;
                    try
                    {
                        CreateMpvContext(player);
                    }
                    catch (Exception exc)
                    {
                        _attachError = exc;
                    }
                    _opDone.Set();
                    continue;
                }

                // ── Detach: liberar el render context de la sesión ──────
                if (_detachRequest)
                {
                    _detachRequest = false;
                    FreeMpvContext();
                    _opDone.Set();
                    continue;
                }

                if (_renderCtx == 0)
                    continue;   // pipeline en reposo (sin sesión)

                ulong flags = mpv_render_context_update(_renderCtx);
                if ((flags & UpdateFrame) == 0)
                    continue;

                _ = mpv_render_context_render(_renderCtx, renderParams);
                lock (_copySync)
                    _gl.PresentFrame();
                try { FrameRendered?.Invoke(); } catch (Exception) { }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(fboMem);
            Marshal.FreeHGlobal(flipMem);
            Marshal.FreeHGlobal(renderParams);
            CleanupOnThread();
        }
    }

    private void CreateMpvContext(MpvPlayer player)
    {
        var initParams = new MpvOpenGlInitParams
        {
            GetProcAddress = Marshal.GetFunctionPointerForDelegate(_getProc),
            GetProcAddressCtx = 0,
        };
        nint initMem = Marshal.AllocHGlobal(Marshal.SizeOf<MpvOpenGlInitParams>());
        nint apiMem = Marshal.StringToHGlobalAnsi("opengl");
        Marshal.StructureToPtr(initParams, initMem, false);
        nint createParams = BuildParams(
            (ParamApiType, apiMem),
            (ParamOpenGlInitParams, initMem));
        try
        {
            int rc = mpv_render_context_create(out _renderCtx, player.NativeHandle,
                createParams);
            if (rc < 0)
                throw new InvalidOperationException(
                    $"mpv_render_context_create falló ({rc}).");
            mpv_render_context_set_update_callback(_renderCtx, _updateCb, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(createParams);
            Marshal.FreeHGlobal(initMem);
            Marshal.FreeHGlobal(apiMem);
        }
    }

    private void FreeMpvContext()
    {
        if (_renderCtx != 0)
        {
            mpv_render_context_free(_renderCtx);
            _renderCtx = 0;
        }
    }

    /// <summary>Libera TODO en el hilo de render (regla de mpv: el render
    /// context se destruye en el hilo que lo creó, ANTES que el core).</summary>
    private void CleanupOnThread()
    {
        FreeMpvContext();
        _gl?.Dispose();
        _gl = null;
        for (int i = 0; i < _viewSurfaces.Length; i++)
        {
            D3D9Minimal.Release(_viewSurfaces[i]);
            _viewSurfaces[i] = 0;
            D3D9Minimal.Release(_presentSurfaces[i]);
            _presentSurfaces[i] = 0;
        }
        D3D9Minimal.Release(_d3dDevice);
        _d3dDevice = 0;
    }

    /// <summary>Destruye el PIPELINE entero (solo al salir de la app o al
    /// cambiar la spec de superficies — nunca por cue: ver banner).
    /// Si hay sesión activa, llamar DetachPlayer + destruir su player ANTES.</summary>
    public void Dispose()
    {
        if (_shutdown)
            return;
        _shutdown = true;
        _wake.Set();
        if (Thread.CurrentThread != _thread && _thread.Join(4_000))
        {
            // El hilo terminó (ya pasó por CleanupOnThread): liberar los wait
            // handles del SO. Si el Join expira se dejan vivos A PROPÓSITO:
            // el hilo colgado podría seguir usándolos y un Dispose aquí
            // provocaría ObjectDisposedException en pleno render.
            _wake.Dispose();
            _ready.Dispose();
            _opDone.Dispose();
        }
    }
}
