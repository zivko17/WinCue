// =============================================================================
// HapGpuPipeline.cs — Pipeline GPU PERSISTENTE del motor HAP propio
// =============================================================================
// La técnica validada en tools/hap-lab (paso 2): textura BC1/BC3 (la GPU
// descomprime el DXT al muestrear) → triángulo fullscreen con pixel shader
// (passthrough o YCoCg→RGB) → render target B8G8R8A8 COMPARTIDO (tamaño de la
// SUPERFICIE de salida, no del vídeo) → D3D9Ex abre el shared handle →
// superficie de PRESENTACIÓN separada que consume el D3DImage del compositor.
//
// PERSISTENTE (lección de la auditoría 05, misma regla que MpvGpuRenderer):
// el pipeline (devices D3D11+D3D9Ex, RT compartida, superficie de
// presentación, shaders) se crea UNA vez por slot (salida, pista, spec) y
// vive toda la sesión; por cue solo se crea/libera una HapVideoSession (el
// ring de texturas BC del tamaño del vídeo — recursos corrientes, sin
// interop). VideoEngine mantiene el pool de pipelines.
//
// HILOS: el device D3D11 es thread-safe (creación de recursos); el CONTEXTO
// no — solo lo toca el hilo de presentación del HapCuePlayer (Upload/Render).
// CopyPresent (StretchRect D3D9) llega del hilo de UI DENTRO del Lock del
// D3DImage; _sync serializa la copia con el draw para no presentar frames a
// medias (equivalente del _copySync de MpvGpuRenderer).
//
// GPUs: el device D3D11 se crea en el adaptador HighPerformance y el D3D9Ex
// en el MISMO adaptador emparejando por LUID (con iGPU+dGPU el ordinal 0 de
// D3D9 no es fiable; sin esto el shared handle no abre — hallazgo del lab).
// =============================================================================

using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.DXGI;

namespace WinCue.Video.Hap;

/// <summary>Recursos POR CUE sobre un pipeline persistente: ring de texturas
/// BC del tamaño del vídeo + su modo de subida. Se crea en AttachSession y se
/// libera al terminar el cue (recursos corrientes: sin riesgo de la fuga de
/// interop de la auditoría 05).</summary>
internal sealed class HapVideoSession : IDisposable
{
    internal const int RingSize = 3;

    internal ID3D11Texture2D[] Ring { get; } = new ID3D11Texture2D[RingSize];
    internal ID3D11ShaderResourceView[] RingSrv { get; } = new ID3D11ShaderResourceView[RingSize];
    internal required bool DynamicUpload { get; init; }
    internal required bool YCoCg { get; init; }
    internal required int BlockRows { get; init; }    // filas de bloques 4x4
    internal required int RowBytes { get; init; }     // bytes por fila de bloques

    public void Dispose()
    {
        for (int i = 0; i < RingSize; i++)
        {
            RingSrv[i]?.Dispose();
            Ring[i]?.Dispose();
        }
    }
}

internal sealed class HapGpuPipeline : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Texture2D _renderTarget;      // COMPARTIDA (tamaño superficie)
    private readonly ID3D11RenderTargetView _rtv;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _psPassthrough;
    private readonly ID3D11PixelShader _psYCoCg;
    private readonly ID3D11SamplerState _sampler;

    private readonly IDirect3D9Ex _d3d9;
    private readonly IDirect3DDevice9Ex _d3d9Device;
    private readonly IDirect3DTexture9 _sharedTexture;   // el RT de D3D11 visto desde D3D9
    private readonly IDirect3DSurface9 _sharedSurface;
    private readonly IDirect3DSurface9 _presentSurface;  // la ÚNICA que ve WPF
    private readonly Rect _fullRect;

    // Serializa el draw del hilo de presentación con la copia de presentación
    // del hilo de UI (dentro del Lock del D3DImage)
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

    public HapGpuPipeline(int surfaceWidth, int surfaceHeight)
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
            _vs = _device.CreateVertexShader(HapShaders.CompileVertexShader(), null);
            _psPassthrough = _device.CreatePixelShader(HapShaders.CompilePassthroughPixelShader(), null);
            _psYCoCg = _device.CreatePixelShader(HapShaders.CompileScaledYCoCgPixelShader(), null);
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

            // Sin ventana de foco (device headless para superficies, igual que
            // D3D9Minimal.CreateDevice(0) del interop mpv, probado en producción)
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

            // Abrir el shared handle de D3D11 como textura D3D9 (A8R8G8B8 es
            // el espejo de B8G8R8A8; el handle EXISTENTE va en pSharedHandle)
            nint shared = sharedHandle;
            _sharedTexture = _d3d9Device.CreateTexture((uint)Width, (uint)Height, 1,
                Vortice.Direct3D9.Usage.RenderTarget, Vortice.Direct3D9.Format.A8R8G8B8,
                Pool.Default, ref shared);
            _sharedSurface = _sharedTexture.GetSurfaceLevel(0);

            // Superficie de PRESENTACIÓN separada (no compartida): el back
            // buffer que ve WPF. Creada UNA vez, viva toda la sesión (aud. 05).
            _presentSurface = _d3d9Device.CreateRenderTarget((uint)Width, (uint)Height,
                Vortice.Direct3D9.Format.A8R8G8B8, MultisampleType.None, 0, lockable: false);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Crea los recursos por cue: ring de texturas BC del tamaño del
    /// vídeo. Intenta DYNAMIC + Map WRITE_DISCARD (validado en el lab) y cae a
    /// DEFAULT + UpdateSubresource si el driver rechaza BC dinámico.</summary>
    public HapVideoSession CreateSession(int videoWidth, int videoHeight, HapTextureFormat format)
    {
        int blocksX = (videoWidth + 3) / 4;
        int blockRows = (videoHeight + 3) / 4;
        int rowBytes = blocksX * (format == HapTextureFormat.RgbDxt1 ? 8 : 16);
        Vortice.DXGI.Format texFormat = format == HapTextureFormat.RgbDxt1
            ? Vortice.DXGI.Format.BC1_UNorm
            : Vortice.DXGI.Format.BC3_UNorm;

        var texDesc = new Texture2DDescription
        {
            Width = (uint)videoWidth,
            Height = (uint)videoHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = texFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None,
        };

        var session = new HapVideoSession
        {
            DynamicUpload = true,
            YCoCg = format == HapTextureFormat.YCoCgDxt5,
            BlockRows = blockRows,
            RowBytes = rowBytes,
        };
        try
        {
            FillRing(session, texDesc);
            return session;
        }
        catch (SharpGenException)
        {
            // Fallback documentado en el INFORME del lab: BC dinámico no
            // soportado por este driver → DEFAULT + UpdateSubresource
            session.Dispose();
            texDesc.Usage = ResourceUsage.Default;
            texDesc.CPUAccessFlags = CpuAccessFlags.None;
            var fallback = new HapVideoSession
            {
                DynamicUpload = false,
                YCoCg = format == HapTextureFormat.YCoCgDxt5,
                BlockRows = blockRows,
                RowBytes = rowBytes,
            };
            FillRing(fallback, texDesc);
            return fallback;
        }
    }

    private void FillRing(HapVideoSession s, Texture2DDescription desc)
    {
        for (int i = 0; i < HapVideoSession.RingSize; i++)
        {
            s.Ring[i] = _device.CreateTexture2D(desc);
            s.RingSrv[i] = _device.CreateShaderResourceView(s.Ring[i], null);
        }
    }

    /// <summary>Sube el DXT de un frame al slot del ring (¡solo el hilo de
    /// presentación del player: el contexto D3D11 no es thread-safe!).</summary>
    public void Upload(HapVideoSession s, int slot, ReadOnlySpan<byte> dxt)
    {
        ID3D11Texture2D tex = s.Ring[slot];
        if (s.DynamicUpload)
        {
            MappedSubresource mapped = _context.Map(tex, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            try
            {
                CopyRows(s, dxt, mapped.DataPointer, (int)mapped.RowPitch);
            }
            finally
            {
                _context.Unmap(tex, 0);
            }
        }
        else
        {
            unsafe
            {
                fixed (byte* src = dxt)
                {
                    // El pitch de un BC es bytes por FILA DE BLOQUES 4x4
                    _context.UpdateSubresource(tex, 0, null, (nint)src, (uint)s.RowBytes, 0);
                }
            }
        }
    }

    private static unsafe void CopyRows(HapVideoSession s, ReadOnlySpan<byte> dxt, nint dest, int destPitch)
    {
        if (destPitch == s.RowBytes)
        {
            dxt.CopyTo(new Span<byte>((void*)dest, dxt.Length));
            return;
        }

        for (int row = 0; row < s.BlockRows; row++)
        {
            dxt.Slice(row * s.RowBytes, s.RowBytes)
               .CopyTo(new Span<byte>((byte*)dest + ((long)row * destPitch), s.RowBytes));
        }
    }

    /// <summary>Dibuja el slot del ring al RT compartido: fondo negro + vídeo
    /// en el rectángulo destino (letterbox/escala, calculado por el player) y
    /// Flush del contexto (requisito antes del StretchRect del lado D3D9).
    /// Solo el hilo de presentación del player.</summary>
    public void Render(HapVideoSession s, int slot, int destX, int destY, int destW, int destH)
    {
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
            _context.PSSetShader(s.YCoCg ? _psYCoCg : _psPassthrough);
            _context.PSSetShaderResource(0, s.RingSrv[slot]);
            _context.PSSetSampler(0, _sampler);
            _context.Draw(3, 0);
            _context.Flush();
        }
    }

    /// <summary>Copia GPU compartida→presentación. DEBE llamarse con el Lock
    /// del D3DImage tomado (contrato de la auditoría 05: escribir al back
    /// buffer fuera del Lock provoca renaming del driver = fuga de VRAM).</summary>
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

    /// <summary>El player terminó de dibujar un frame: avisar a los sinks.</summary>
    public void NotifyFrame()
    {
        try { FrameRendered?.Invoke(); } catch (Exception) { }
    }

    /// <summary>Destruye el PIPELINE entero (solo al salir de la app — nunca
    /// por cue: las superficies son estables, auditoría 05). Las sesiones de
    /// los cues deben estar liberadas antes.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _presentSurface?.Dispose();
        _sharedSurface?.Dispose();
        _sharedTexture?.Dispose();
        _d3d9Device?.Dispose();
        _d3d9?.Dispose();
        _sampler?.Dispose();
        _psYCoCg?.Dispose();
        _psPassthrough?.Dispose();
        _vs?.Dispose();
        _rtv?.Dispose();
        _renderTarget?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
