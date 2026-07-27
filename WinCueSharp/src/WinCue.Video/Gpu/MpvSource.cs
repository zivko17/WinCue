// =============================================================================
// MpvSource.cs — mpv como fuente de textura D3D11 (capa L1)
// =============================================================================
// Sustituye, para la ruta nueva, a MpvGpuRenderer: en vez de blitear a una
// superficie D3D9Ex que consume un D3DImage de WPF, mpv renderiza DENTRO de
// los slots del anillo, que son texturas D3D11 del device del motor. A partir
// de ahí es una fuente más para el compositor, indistinguible de HAP.
//
// HILO PROPIO ("mpv-src"): posee el contexto GL de principio a fin — lo exige
// tanto el render API de mpv como el lock del interop. El constructor BLOQUEA
// hasta que el puente está listo (o lanza) para que el llamante pueda hacer
// fallback limpio a la ruta vieja.
//
// PTS: se toma el time-pos de mpv tras cada render. mpv decide qué fotograma
// muestra con su propio reloj; nosotros solo lo etiquetamos con su tiempo de
// medio para que el compositor pueda pedir "el de el instante T" (FrameCadence).
// =============================================================================

using System.Runtime.InteropServices;
using static WinCue.Video.MpvRenderNative;

namespace WinCue.Video.Gpu;

public sealed class MpvSource : IVideoSource
{
    private readonly GpuDevice _gpu;
    private readonly MpvPlayer _player;
    private readonly FrameRing _ring;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _shutdown;
    private Exception? _initError;

    // Los delegados nativos deben seguir vivos mientras mpv los use
    private readonly GetProcAddressFn _getProc;
    private readonly RenderUpdateFn _updateCb;

    // CA2213: _gl SÍ se libera, pero en Cleanup() dentro del hilo GL — el
    // único que puede tocar el contexto. El analizador no puede verlo.
#pragma warning disable CA2213
    private GlD3D11Interop? _gl;
#pragma warning restore CA2213
    private nint _renderCtx;
    private double _lastPublishedPts = double.NaN;

    // Estado del dibujo en campos + delegado CACHEADO: una lambda que capture
    // locales asigna un delegado por vuelta del bucle, y este bucle es el hilo
    // de render — donde la regla es cero asignaciones por fotograma.
    private nint _fboMem, _renderParams;
    private readonly Action<uint> _drawFrame;

    private void DrawFrame(uint fbo)
    {
        Marshal.StructureToPtr(
            new MpvOpenGlFbo { Fbo = (int)fbo, W = Width, H = Height, InternalFormat = 0 },
            _fboMem, false);
        _ = mpv_render_context_render(_renderCtx, _renderParams);
    }

    public int Width { get; }
    public int Height { get; }
    public bool HasFrames => _ring.Published > 0;

    private readonly VideoSourceStats _stats = new();

    /// <summary>Los contadores del anillo se leen AL CONSULTAR, no al entregar
    /// un fotograma: si no, quedan a cero mientras la fuente ya está
    /// produciendo y el diagnóstico miente.</summary>
    public VideoSourceStats Stats
    {
        get
        {
            _stats.Published = _ring.Published;
            _stats.WriteStalls = _ring.WriteStalls;
            return _stats;
        }
    }

    /// <summary>Identificación del driver GL (diagnóstico en máquina ajena).</summary>
    public string GlRenderer => _gl?.GlRenderer ?? "?";

    public MpvSource(GpuDevice gpu, MpvPlayer player, int width, int height,
        int ringCapacity = 3)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(player);
        _gpu = gpu;
        _player = player;
        Width = width;
        Height = height;

        // El anillo se crea aquí (no en el hilo GL) para que el llamante tenga
        // tamaños y pueda consultar en cuanto vuelva el constructor. Las
        // texturas van SHARED: es la variante de registro validada en el lab.
        _ring = new FrameRing(gpu, width, height, ringCapacity, sharedTextures: true);

        _getProc = OnGetProcAddress;
        _updateCb = _ => _wake.Set();
        _drawFrame = DrawFrame;

        _thread = new Thread(RenderThread)
        {
            Name = "mpv-src",
            IsBackground = true,
        };
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
        {
            _shutdown = true;
            throw new InvalidOperationException("MpvSource: timeout creando el puente GL↔D3D11.");
        }
        if (_initError is not null)
        {
            _shutdown = true;
            _ring.Dispose();
            throw _initError;
        }
    }

    public bool TryAcquire(double mediaTime, out FrameLease lease)
    {
        bool ok = _ring.TryAcquire(mediaTime, out lease);
        if (!ok)
        {
            _stats.Starvations++;
            return false;
        }
        if (lease.Pts == _lastAcquiredPts)
            _stats.Repeats++;
        _lastAcquiredPts = lease.Pts;
        return true;
    }

    private double _lastAcquiredPts = double.NaN;

    private nint OnGetProcAddress(nint ctx, nint namePtr)
    {
        string? name = Marshal.PtrToStringUTF8(namePtr);
        return name is null ? 0 : _gl!.GetGlProc(name);
    }

    // ------------------------------------------------------------------
    // Hilo GL: dueño del contexto y del render context de mpv
    // ------------------------------------------------------------------

    private void RenderThread()
    {
        nint initMem = 0, apiMem = 0, createParams = 0;
        try
        {
            _gl = new GlD3D11Interop();
            _gl.OpenDevice(_gpu.Device.NativePointer);
            for (int i = 0; i < _ring.Capacity; i++)
            {
                using var res = _ring.TextureAt(i).QueryInterface<Vortice.DXGI.IDXGIResource>();
                _gl.Register(_ring.TextureAt(i).NativePointer, res.SharedHandle);
            }

            var init = new MpvOpenGlInitParams
            {
                GetProcAddress = Marshal.GetFunctionPointerForDelegate(_getProc),
                GetProcAddressCtx = 0,
            };
            initMem = Marshal.AllocHGlobal(Marshal.SizeOf<MpvOpenGlInitParams>());
            Marshal.StructureToPtr(init, initMem, false);
            apiMem = Marshal.StringToHGlobalAnsi("opengl");
            createParams = BuildParams((ParamApiType, apiMem), (ParamOpenGlInitParams, initMem));

            int rc = mpv_render_context_create(out _renderCtx, _player.NativeHandle, createParams);
            if (rc < 0)
                throw new InvalidOperationException($"mpv_render_context_create falló ({rc}).");
            mpv_render_context_set_update_callback(_renderCtx, _updateCb, 0);

            _ready.Set();
        }
        catch (Exception exc)
        {
            _initError = exc;
            _ready.Set();
            Cleanup(initMem, apiMem, createParams);
            return;
        }

        // Parámetros de render reutilizados; el FBO cambia por slot
        _fboMem = Marshal.AllocHGlobal(Marshal.SizeOf<MpvOpenGlFbo>());
        nint flipMem = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(flipMem, 1);   // GL pinta de abajo arriba; D3D no
        _renderParams = BuildParams((ParamOpenGlFbo, _fboMem), (ParamFlipY, flipMem));
        nint fboMem = _fboMem, renderParams = _renderParams;   // para el finally

        try
        {
            while (!_shutdown)
            {
                _wake.WaitOne(50);
                if (_shutdown)
                    break;
                if ((mpv_render_context_update(_renderCtx) & UpdateFrame) == 0)
                    continue;

                int slot = _ring.BeginWrite();
                if (slot < 0)
                    continue;   // anillo lleno: se salta el fotograma, nadie espera

                _gl!.RenderInto(slot, _drawFrame);

                // PTS del fotograma recién pintado. Si mpv aún no lo publica,
                // se avanza sintéticamente para no romper la monotonía del
                // anillo (un fotograma sin tiempo es peor que uno estimado).
                double pts = _player.GetPropertyDouble("time-pos")
                    ?? (double.IsNaN(_lastPublishedPts) ? 0.0 : _lastPublishedPts + 1e-3);
                if (!double.IsNaN(_lastPublishedPts) && pts <= _lastPublishedPts)
                    pts = _lastPublishedPts + 1e-4;   // estrictamente creciente
                _lastPublishedPts = pts;
                _ring.Commit(slot, pts);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(renderParams);
            Marshal.FreeHGlobal(fboMem);
            Marshal.FreeHGlobal(flipMem);
            Cleanup(initMem, apiMem, createParams);
        }
    }

    /// <summary>Libera TODO en el hilo GL (regla de mpv: el render context se
    /// destruye en el hilo que lo creó, y ANTES que el core del player).</summary>
    private void Cleanup(nint initMem, nint apiMem, nint createParams)
    {
        if (_renderCtx != 0)
        {
            mpv_render_context_free(_renderCtx);
            _renderCtx = 0;
        }
        _gl?.Dispose();
        _gl = null;
        if (createParams != 0) Marshal.FreeHGlobal(createParams);
        if (initMem != 0) Marshal.FreeHGlobal(initMem);
        if (apiMem != 0) Marshal.FreeHGlobal(apiMem);
    }

    public void Dispose()
    {
        if (_shutdown)
            return;
        _shutdown = true;
        _wake.Set();
        if (Thread.CurrentThread != _thread && _thread.Join(4_000))
        {
            _wake.Dispose();
            _ready.Dispose();
        }
        _ring.Dispose();
    }
}
