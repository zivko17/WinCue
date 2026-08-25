// =============================================================================
// ProResGpuPipeline.cs — Pipeline GPU PERSISTENTE del motor ProRes propio
// =============================================================================
// Gemelo de HapGpuPipeline y con la MISMA salida: render target B8G8R8A8
// COMPARTIDO (tamaño de la SUPERFICIE, no del vídeo) → D3D9Ex abre el shared
// handle → superficie de PRESENTACIÓN separada que consume el D3DImage del
// compositor. Del lado del compositor, un cue ProRes y uno HAP son
// indistinguibles: por eso los crossfades entre ambos funcionan sin nada
// especial.
//
// Lo que cambia respecto a HAP es SOLO la entrada al shader:
//   HAP    → una textura BC1/BC3; la GPU descomprime al muestrear.
//   ProRes → N texturas R16_UNORM, una por plano (3 en 4:2:2, 4 en 4444 con
//            alfa). Las muestras de FFmpeg ya son uint16 little-endian, así que
//            la subida es una COPIA DE FILAS sin tocar los datos; la corrección
//            de profundidad (10/12 bits) y de rango la hace el shader con una
//            multiplicación y una suma (ColorParams).
//
// PERSISTENTE (auditoría 05, misma regla que MpvGpuRenderer y HapGpuPipeline):
// devices, RT compartida, superficie de presentación y shaders se crean UNA vez
// por slot (salida, pista, spec) y viven toda la sesión. Crear y destruir esto
// por cue fugaba la VRAM del ciclo entero.
//
// HILOS: el device D3D11 es thread-safe para crear recursos; el CONTEXTO no —
// solo lo toca el hilo de presentación del ProResCuePlayer (Upload/Render).
// CopyPresent (StretchRect D3D9) llega del hilo de UI DENTRO del Lock del
// D3DImage; _sync serializa la copia con el draw.
//
// GPUs: D3D11 en el adaptador HighPerformance y D3D9Ex en el MISMO adaptador
// emparejando por LUID — con iGPU + dGPU el ordinal 0 de D3D9 no es fiable y
// sin esto el shared handle no abre (hallazgo del lab de HAP).
// =============================================================================

using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.DXGI;

namespace WinCue.Video.ProRes;

/// <summary>
/// Recursos POR CUE sobre un pipeline persistente: ring de texturas de PLANO
/// del tamaño del vídeo, más el constant buffer con las constantes de color.
/// </summary>
/// <remarks>
/// La SPEC de estas texturas depende del FORMATO DE PÍXEL, no solo del tamaño:
/// un 4:2:2 de 10 bits son 3 planos con el croma a media anchura; un 4444 de 12
/// bits son 4 planos a anchura completa. Por eso <see cref="Spec"/> incluye el
/// nombre del layout y el slot del pool solo reutiliza su sesión cuando coincide
/// exactamente (ver VideoEngine.AcquireProResSlot).
/// </remarks>
internal sealed class ProResVideoSession : IDisposable
{
    internal const int RingSize = 3;
    internal const int MaxPlanes = 4;

    private readonly ID3D11Texture2D?[,] _planes = new ID3D11Texture2D?[RingSize, MaxPlanes];
    private readonly ID3D11ShaderResourceView?[,] _srv = new ID3D11ShaderResourceView?[RingSize, MaxPlanes];
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11DeviceContext _context;
    private ColorParams _current;
    private bool _colorWritten;

    private ProResVideoSession(ID3D11Device device, ID3D11DeviceContext context,
        int videoWidth, int videoHeight, PixelLayout layout)
    {
        _context = context;
        Width = videoWidth;
        Height = videoHeight;
        Layout = layout;

        for (int slot = 0; slot < RingSize; slot++)
        {
            for (int p = 0; p < layout.PlaneCount; p++)
            {
                var desc = new Texture2DDescription
                {
                    Width = (uint)layout.PlaneWidth(p, videoWidth),
                    Height = (uint)layout.PlaneHeight(p, videoHeight),
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Vortice.DXGI.Format.R16_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Dynamic,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.Write,
                    MiscFlags = ResourceOptionFlags.None,
                };
                ID3D11Texture2D tex = device.CreateTexture2D(desc);
                _planes[slot, p] = tex;
                _srv[slot, p] = device.CreateShaderResourceView(tex, null);
            }
        }

        _constants = device.CreateBuffer(new BufferDescription(
            (uint)ColorParams.SizeInBytes, BindFlags.ConstantBuffer, ResourceUsage.Default));
    }

    internal int Width { get; }

    internal int Height { get; }

    internal PixelLayout Layout { get; }

    /// <summary>Firma de la sesión: tamaño del vídeo Y formato de píxel. Dos
    /// cues solo pueden compartir estas texturas si coinciden las dos cosas.</summary>
    internal string Spec => SpecOf(Width, Height, Layout);

    /// <summary>Firma de una sesión que aún no existe (para decidir reutilización).</summary>
    internal static string SpecOf(int videoWidth, int videoHeight, PixelLayout layout) =>
        $"{videoWidth}x{videoHeight}|{layout.Signature}";

    internal static ProResVideoSession Create(ID3D11Device device, ID3D11DeviceContext context,
        int videoWidth, int videoHeight, PixelLayout layout)
    {
        var session = new ProResVideoSession(device, context, videoWidth, videoHeight, layout);
        return session;
    }

    /// <summary>Vistas de los planos de un slot (null en los planos que no usa
    /// este layout: el shader los ignora por la bandera de alfa).</summary>
    internal ID3D11ShaderResourceView? Srv(int slot, int plane) => _srv[slot % RingSize, plane];

    internal ID3D11Buffer Constants => _constants;

    /// <summary>
    /// Fija las constantes de color del CLIP. Se escriben una sola vez (y solo
    /// se reescriben si cambian, que en un clip no pasa): el bitstream de ProRes
    /// no cambia de matriz a mitad de fichero.
    /// </summary>
    internal void SetColor(ColorParams parameters)
    {
        if (_colorWritten && _current == parameters)
            return;
        _context.UpdateSubresource(parameters, _constants);
        _current = parameters;
        _colorWritten = true;
    }

    /// <summary>
    /// Sube los planos del frame al slot del ring. Solo el hilo de presentación
    /// (el contexto D3D11 no es thread-safe).
    /// </summary>
    /// <remarks>
    /// Aquí es donde se va el presupuesto con ProRes, no en el shader: 7,91 MB
    /// por frame en 1080p 4:2:2 y 63,28 MB en 4K 4444. Medido: 0,28 ms y
    /// 3,47 ms respectivamente. Por eso la copia es fila a fila sin conversión
    /// —los uint16 de FFmpeg valen tal cual para R16_UNORM— y se salta a un
    /// memcpy único cuando los strides coinciden.
    /// </remarks>
    internal unsafe void Upload(int slot, ProResFrameSlot frame)
    {
        int index = slot % RingSize;
        for (int p = 0; p < Layout.PlaneCount; p++)
        {
            ID3D11Texture2D tex = _planes[index, p]!;
            int rows = Layout.PlaneHeight(p, Height);
            int rowBytes = Layout.PlaneWidth(p, Width) * 2;
            int srcStride = frame.PlaneStride(p);
            byte* src = (byte*)frame.PlanePointer(p);

            MappedSubresource mapped = _context.Map(tex, 0, MapMode.WriteDiscard,
                Vortice.Direct3D11.MapFlags.None);
            try
            {
                byte* dst = (byte*)mapped.DataPointer;
                int dstStride = (int)mapped.RowPitch;
                if (dstStride == srcStride && dstStride == rowBytes)
                {
                    Buffer.MemoryCopy(src, dst, (long)rowBytes * rows, (long)rowBytes * rows);
                }
                else
                {
                    for (int y = 0; y < rows; y++)
                    {
                        Buffer.MemoryCopy(src + ((long)y * srcStride), dst + ((long)y * dstStride),
                            rowBytes, rowBytes);
                    }
                }
            }
            finally
            {
                _context.Unmap(tex, 0);
            }
        }
    }

    public void Dispose()
    {
        for (int slot = 0; slot < RingSize; slot++)
        {
            for (int p = 0; p < MaxPlanes; p++)
            {
                _srv[slot, p]?.Dispose();
                _planes[slot, p]?.Dispose();
            }
        }

        _constants.Dispose();
    }
}

/// <summary>A dónde dibuja el motor ProRes (gemelo de IHapRenderTarget: extraer
/// el contrato permite que el compositor propio reutilice el player entero).</summary>
internal interface IProResRenderTarget
{
    /// <summary>Tamaño de la superficie de destino (para el letterbox).</summary>
    int Width { get; }

    int Height { get; }

    void Upload(ProResVideoSession session, int slot, ProResFrameSlot frame);

    void Render(ProResVideoSession session, int slot, int destX, int destY, int destW, int destH);

    /// <summary>El fotograma está dibujado y listo.</summary>
    void NotifyFrame(double pts);
}

internal sealed class ProResGpuPipeline : IProResRenderTarget, IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Texture2D _renderTarget;      // COMPARTIDA (tamaño superficie)
    private readonly ID3D11RenderTargetView _rtv;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11SamplerState _sampler;

    private readonly IDirect3D9Ex _d3d9;
    private readonly IDirect3DDevice9Ex _d3d9Device;
    private readonly IDirect3DTexture9 _sharedTexture;   // el RT de D3D11 visto desde D3D9
    private readonly IDirect3DSurface9 _sharedSurface;
    private readonly IDirect3DSurface9 _presentSurface;  // la ÚNICA que ve WPF
    private readonly nint _presentSharedHandle;
    private readonly Rect _fullRect;

    // Query de evento PERSISTENTE (auditoría 05: crear/destruir un objeto COM
    // por fotograma fuga VRAM — se crea UNA vez con el pipeline y la reutiliza
    // CopyPresentFenced). Null si el driver no soporta D3DQUERYTYPE_EVENT:
    // el llamante trata "sin query" igual que "sin barrera que esperar".
    private readonly IDirect3DQuery9? _presentQuery;

    private readonly object _sync = new();
    private bool _disposed;

    /// <summary>Nuevo frame en la superficie compartida (¡hilo de presentación
    /// del player!). El sink del compositor coalesce y copia en el UI.</summary>
    public event Action? FrameRendered;

    public int Width { get; }

    public int Height { get; }

    /// <summary>Puntero IDirect3DSurface9 de PRESENTACIÓN para
    /// IVideoLayerSurface.SetSurface (binding único por vida del pipeline).</summary>
    public nint PresentSurfacePointer => _presentSurface.NativePointer;

    /// <summary>Handle COMPARTIDO de la superficie de PRESENTACIÓN, para quien
    /// no pueda hospedarla directamente y tenga que abrirla en otro device
    /// (Secuence: WinUI 3 sin D3DImage).</summary>
    public nint PresentSharedHandle => _presentSharedHandle;

    public ProResGpuPipeline(int surfaceWidth, int surfaceHeight)
    {
        Width = Math.Max(16, surfaceWidth);
        Height = Math.Max(16, surfaceHeight);
        _fullRect = new Rect(0, 0, Width, Height);

        // --- D3D11 en el adaptador de alto rendimiento --------------------
        using IDXGIFactory6 factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
        using IDXGIAdapter1 adapter =
            factory.EnumAdapterByGpuPreference<IDXGIAdapter1>(0, GpuPreference.HighPerformance);
        AdapterDescription1 desc = adapter.Description1;
        long adapterLuid = ((long)desc.Luid.HighPart << 32) | desc.Luid.LowPart;

        FeatureLevel[] levels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            levels, out _device!, out _context!).CheckError();

        try
        {
            // --- Render target COMPARTIDO (tamaño de la superficie) -------
            var rtDesc = new Texture2DDescription
            {
                Width = (uint)Width,
                Height = (uint)Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.Shared,
            };
            _renderTarget = _device.CreateTexture2D(rtDesc);
            _rtv = _device.CreateRenderTargetView(_renderTarget, null);
            nint sharedHandle;
            using (IDXGIResource dxgiResource = _renderTarget.QueryInterface<IDXGIResource>())
            {
                sharedHandle = dxgiResource.SharedHandle;
            }

            // --- Shaders + sampler (compilados una vez por pipeline) ------
            _vs = _device.CreateVertexShader(ProResShaders.CompileVertexShader(), null);
            // ALFA FORZADO A 1, incluso en ProRes 4444 con canal alfa. Es la
            // MISMA decisión que en HAP5 (que también trae alfa y usa el shader
            // passthrough): la mezcla entre capas la hace el compositor por
            // OPACIDAD DE SUPERFICIE, y es lo que hace mpv hoy. Honrar el alfa
            // del medio sería una capacidad nueva —keying por cue— pero
            // cambiaría lo que el operador ve en los clips que ya usa, que es
            // justo lo que este paso no debe hacer. El shader que lo conserva
            // está escrito y probado (ProResShaders.CompilePixelShader(true)):
            // encenderlo el día que se decida es una línea.
            _ps = _device.CreatePixelShader(ProResShaders.CompilePixelShader(keepAlpha: false), null);
            // Croma POINT: es lo que hace swscale (y por tanto mpv). Ver
            // ProResShaders — con muestreo lineal la imagen deja de ser
            // comparable con lo que el operador veía hasta hoy.
            _sampler = _device.CreateSamplerState(new SamplerDescription(
                Filter.MinMagMipPoint, TextureAddressMode.Clamp,
                TextureAddressMode.Clamp, TextureAddressMode.Clamp));

            // --- Lado D3D9Ex (mismo adaptador, emparejado por LUID) -------
            _d3d9 = D3D9.Direct3DCreate9Ex();
            uint ordinal = 0;
            for (uint i = 0; i < _d3d9.AdapterCount; i++)
            {
                Luid luid = _d3d9.GetAdapterLuid(i);
                if ((((long)luid.HighPart << 32) | luid.LowPart) == adapterLuid)
                {
                    ordinal = i;
                    break;
                }
            }

            var pp = new Vortice.Direct3D9.PresentParameters
            {
                Windowed = true,
                SwapEffect = Vortice.Direct3D9.SwapEffect.Discard,
                BackBufferWidth = 1,
                BackBufferHeight = 1,
                BackBufferFormat = Vortice.Direct3D9.Format.Unknown,
                BackBufferCount = 1,
                DeviceWindowHandle = 0,
            };
            _d3d9Device = _d3d9.CreateDeviceEx(ordinal, DeviceType.Hardware, 0,
                CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
                pp);

            nint shared = sharedHandle;
            _sharedTexture = _d3d9Device.CreateTexture((uint)Width, (uint)Height, 1,
                Vortice.Direct3D9.Usage.RenderTarget, Vortice.Direct3D9.Format.A8R8G8B8,
                Pool.Default, ref shared);
            _sharedSurface = _sharedTexture.GetSurfaceLevel(0);

            // Superficie de PRESENTACIÓN: el back buffer que ve WPF. TAMBIÉN
            // pide handle compartido (Task 5): quien no pueda hospedar el
            // IDirect3DSurface9 directamente (Secuence, WinUI 3 sin D3DImage)
            // la abre por su cuenta con OpenSharedResource — no cambia nada
            // para WPF, que sigue usando PresentSurfacePointer tal cual.
            nint presentShared = 0;
            _presentSurface = _d3d9Device.CreateRenderTarget((uint)Width, (uint)Height,
                Vortice.Direct3D9.Format.A8R8G8B8, MultisampleType.None, 0, lockable: false,
                ref presentShared);
            _presentSharedHandle = presentShared;

            // Query de evento PERSISTENTE para CopyPresentFenced (ver el
            // comentario del campo). Si el driver no soporta
            // D3DQUERYTYPE_EVENT, CreateQuery lanza: se trata igual que la
            // ausencia de query — no hay barrera que esperar.
            try
            {
                _presentQuery = _d3d9Device.CreateQuery(Vortice.Direct3D9.QueryType.Event);
            }
            catch (SharpGenException)
            {
                _presentQuery = null;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Crea el ring de texturas de plano del cue.</summary>
    public ProResVideoSession CreateSession(int videoWidth, int videoHeight, PixelLayout layout) =>
        ProResVideoSession.Create(_device, _context, videoWidth, videoHeight, layout);

    /// <summary>Sube los planos de un frame al slot del ring (¡solo el hilo de
    /// presentación del player: el contexto D3D11 no es thread-safe!).</summary>
    public void Upload(ProResVideoSession session, int slot, ProResFrameSlot frame)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Upload(slot, frame);
    }

    /// <summary>Dibuja el slot del ring al RT compartido: fondo negro + vídeo en
    /// el rectángulo destino, y Flush (requisito antes del StretchRect de D3D9).
    /// Solo el hilo de presentación del player.</summary>
    public void Render(ProResVideoSession session, int slot, int destX, int destY, int destW, int destH)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_sync)
        {
            if (_disposed)
                return;
            _context.OMSetRenderTargets(_rtv, null);
            _context.ClearRenderTargetView(_rtv, new Vortice.Mathematics.Color4(0f, 0f, 0f, 1f));
            _context.RSSetViewport(destX, destY, Math.Max(1, destW), Math.Max(1, destH), 0, 1);
            _context.IASetInputLayout(null);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.VSSetShader(_vs);
            _context.PSSetShader(_ps);
            _context.PSSetConstantBuffer(0, session.Constants);
            for (int p = 0; p < ProResVideoSession.MaxPlanes; p++)
            {
                // Los planos que este layout no usa (el alfa en 4:2:2) se
                // DESLIGAN pasando null. Vortice anota el parámetro como no
                // nulo, pero null es el valor válido de D3D11 para soltar el
                // slot — y dejar atada la textura del cue anterior sería peor.
                _context.PSSetShaderResource((uint)p, session.Srv(slot, p)!);
            }

            _context.PSSetSampler(0, _sampler);
            _context.PSSetSampler(1, _sampler);
            _context.Draw(3, 0);
            _context.Flush();
        }
    }

    /// <summary>Copia GPU compartida→presentación. DEBE llamarse con el Lock del
    /// D3DImage tomado (contrato de la auditoría 05: escribir al back buffer
    /// fuera del Lock provoca renaming del driver = fuga de VRAM).</summary>
    public void CopyPresent()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _d3d9Device.StretchRect(_sharedSurface, _fullRect, _presentSurface, _fullRect,
                TextureFilter.None);
        }
    }

    /// <summary>CopyPresent() MAS el fence entre devices D3D9Ex y D3D11.
    /// Variante SEPARADA a propósito: la usa quien vaya a anunciar el
    /// fotograma a un consumidor D3D11 (SharedSurfaceSource, Secuence) sobre
    /// <see cref="PresentSharedHandle"/> — WinCue sigue llamando CopyPresent
    /// tal cual, sin fence. Ver el porqué del fence en GlDxInterop.FlushQuery:
    /// una superficie D3D9Ex compartida no trae sincronización implícita
    /// entre devices.</summary>
    public void CopyPresentFenced()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _d3d9Device.StretchRect(_sharedSurface, _fullRect, _presentSurface, _fullRect,
                TextureFilter.None);
            FlushPresentQuery();
        }
    }

    /// <summary>Issue(END) + espera ACOTADA (nunca indefinida) sobre la query
    /// persistente del pipeline. Mismo contrato que GlDxInterop.FlushQuery:
    /// si Issue falla, si no hay query, o si el plazo expira, se vuelve sin
    /// más — un fotograma repetido es preferible a colgar el hilo que
    /// presenta.</summary>
    private void FlushPresentQuery(int timeoutMs = 100)
    {
        if (_presentQuery is null)
            return;
        try
        {
            if (_presentQuery.Issue(Issue.End).Failure)
                return;

            long deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (_presentQuery.GetData(out bool _, true))
                    return;   // dato disponible: la GPU ya vació la cola hasta aquí
                Thread.Yield();
            }
        }
        catch (SharpGenException)
        {
            // Device lost u otro fallo real del driver: no hay barrera que
            // esperar, igual que "sin query" — el llamante decide si publica.
        }
    }

    /// <summary>El player terminó de dibujar un frame: avisar a los sinks.</summary>
    public void NotifyFrame(double pts)
    {
        try { FrameRendered?.Invoke(); } catch (Exception) { }
    }

    /// <summary>Destruye el PIPELINE entero (solo al salir de la app — nunca por
    /// cue: las superficies son estables, auditoría 05).</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _presentQuery?.Dispose();
        _presentSurface?.Dispose();
        _sharedSurface?.Dispose();
        _sharedTexture?.Dispose();
        _d3d9Device?.Dispose();
        _d3d9?.Dispose();
        _sampler?.Dispose();
        _ps?.Dispose();
        _vs?.Dispose();
        _rtv?.Dispose();
        _renderTarget?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
