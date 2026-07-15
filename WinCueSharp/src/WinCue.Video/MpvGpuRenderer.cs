// =============================================================================
// MpvGpuRenderer.cs — Render de UN cue de vídeo a una superficie D3D9 (GPU)
// =============================================================================
// Une las piezas del compositor: mpv (render API OpenGL) dibuja cada frame en
// un FBO cuyo color attachment ES una render target de Direct3D 9Ex
// compartida (WGL_NV_DX_interop). WPF muestra esa superficie con D3DImage y
// el DWM compone las capas con opacidad — cero copias por CPU.
//
// HILOS: un hilo dedicado ("mpv-gpu-N") posee el contexto GL: crea todo,
// renderiza en cada aviso de mpv y destruye todo al salir. El constructor
// BLOQUEA hasta que el pipeline está listo (o lanza si el driver no puede,
// p.ej. sin WGL_NV_DX_interop) para que el engine haga fallback limpio.
// =============================================================================

using System.Runtime.InteropServices;
using WinCue.Core;
using static WinCue.Video.MpvRenderNative;

namespace WinCue.Video;

public sealed class MpvGpuRenderer : IDisposable
{
    private readonly MpvPlayer _player;
    private readonly int _width, _height;          // tamaño del canvas (FBO de mpv)
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _shutdown;
    private Exception? _initError;

    private GlContext? _gl;
    private nint _renderCtx;
    private nint _d3dDevice;

    // Vistas de salida: cada una es una superficie D3D compartida que recibe
    // su recorte del canvas. Sin lista explícita: una vista = canvas completo.
    private readonly IReadOnlyList<CanvasView> _viewSpecs;
    private readonly nint[] _viewSurfaces;

    // Delegados nativos: deben permanecer vivos mientras mpv los use
    private readonly GetProcAddressFn _getProc;
    private readonly RenderUpdateFn _updateCb;

    /// <summary>Nuevo frame en las superficies (¡llega en el hilo de render!).</summary>
    public event Action? FrameRendered;

    /// <summary>Puntero IDirect3DSurface9 de la PRIMERA vista (modo clásico
    /// de una sola salida) para D3DImage.SetBackBuffer.</summary>
    public nint Surface => _viewSurfaces[0];

    /// <summary>Superficie D3D9 de la vista i (mismo orden que las vistas
    /// pasadas al constructor).</summary>
    public nint ViewSurface(int i) => _viewSurfaces[i];

    public IReadOnlyList<CanvasView> Views => _viewSpecs;

    public int Width => _width;
    public int Height => _height;

    /// <param name="views">Recortes del canvas por salida (modo canvas);
    /// null = una única vista con el canvas completo (modo clásico).</param>
    public MpvGpuRenderer(MpvPlayer player, int width, int height,
        IReadOnlyList<CanvasView>? views = null)
    {
        _player = player;
        _width = Math.Max(16, width);
        _height = Math.Max(16, height);
        _viewSpecs = views is { Count: > 0 }
            ? views
            : new[] { new CanvasView(0, 0, 0, _width, _height) };
        _viewSurfaces = new nint[_viewSpecs.Count];
        _getProc = OnGetProcAddress;
        _updateCb = _ => _wake.Set();

        _thread = new Thread(RenderThread)
        {
            IsBackground = true,
            Name = "mpv-gpu",
        };
        _thread.Start();

        // Esperar el pipeline (GL + D3D + interop + mpv_render_context)
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
            }
            _gl.CreatePrivateFbo(_width, _height);
            CreateMpvContext();
            _ready.Set();
        }
        catch (Exception exc)
        {
            _initError = exc;
            _ready.Set();
            CleanupOnThread();
            return;
        }

        // Bucle: mpv avisa por el update callback → renderiza en el FBO
        // PRIVADO y se presenta el frame completo con un blit a la superficie
        // compartida (así el DWM nunca captura un frame a medias → sin cortes
        // a negro). flip_y=0: la orientación la fija el propio pipeline D3D.
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
                ulong flags = mpv_render_context_update(_renderCtx);
                if ((flags & UpdateFrame) == 0)
                    continue;

                _ = mpv_render_context_render(_renderCtx, renderParams);
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

    private void CreateMpvContext()
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
            int rc = mpv_render_context_create(out _renderCtx, _player.NativeHandle,
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

    /// <summary>Libera TODO en el hilo de render (regla de mpv: el render
    /// context se destruye en el hilo que lo creó, ANTES que el core).</summary>
    private void CleanupOnThread()
    {
        if (_renderCtx != 0)
        {
            mpv_render_context_free(_renderCtx);
            _renderCtx = 0;
        }
        _gl?.Dispose();
        _gl = null;
        for (int i = 0; i < _viewSurfaces.Length; i++)
        {
            D3D9Minimal.Release(_viewSurfaces[i]);
            _viewSurfaces[i] = 0;
        }
        D3D9Minimal.Release(_d3dDevice);
        _d3dDevice = 0;
    }

    /// <summary>Detiene el render. Llamar ANTES de destruir el MpvPlayer.</summary>
    public void Dispose()
    {
        if (_shutdown)
            return;
        _shutdown = true;
        _wake.Set();
        if (Thread.CurrentThread != _thread)
            _thread.Join(4_000);
    }
}
