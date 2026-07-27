using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ProResLab.Gpu;

/// <summary>Cómo se muestrea el croma al ampliarlo a resolución completa.</summary>
public enum ChromaSampling
{
    /// <summary>Replicación (lo que hace swscale de FFmpeg, y por tanto mpv).</summary>
    Point,

    /// <summary>Interpolación bilineal por hardware (mejor imagen, distinta a ffmpeg).</summary>
    Linear,
}

/// <summary>
/// Pipeline GPU de planos YUV de 10/12 bits → RGB.
/// </summary>
/// <remarks>
/// <para>
/// Un plano = una textura <c>R16_UNORM</c>. Es el formato que mejor encaja:
/// las muestras de FFmpeg ya vienen como <c>uint16</c> little-endian, así que
/// la subida es una copia de filas SIN tocar los datos (nada de desempaquetar
/// ni reescalar en CPU), y la GPU normaliza al muestrear. La corrección de
/// profundidad (10 o 12 bits) y de rango se hace con una multiplicación y una
/// suma dentro del shader (ver <see cref="ColorParams"/>).
/// </para>
/// <para>
/// Alternativas descartadas: <c>R16_UINT</c> (obliga a <c>Load</c> en vez de
/// <c>Sample</c>, adiós al filtrado por hardware del croma), y empaquetar a
/// <c>R10G10B10A2</c> en CPU (coste por píxel en el hilo que menos sobra).
/// </para>
/// <para>
/// El render target es <c>B8G8R8A8_UNorm</c> creado COMPARTIDO, exactamente
/// como en <c>hap-lab\HapLab.View</c>: el lado D3D9Ex/<c>D3DImage</c> del
/// compositor real no nota la diferencia entre HAP y ProRes.
/// </para>
/// </remarks>
public sealed class YuvGpuPipeline : IDisposable
{
    private const int RingSize = 3;
    private const int MaxPlanes = 4;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Texture2D?[,] _planes = new ID3D11Texture2D?[RingSize, MaxPlanes];
    private readonly ID3D11ShaderResourceView?[,] _srv = new ID3D11ShaderResourceView?[RingSize, MaxPlanes];
    private readonly ID3D11Texture2D _renderTarget;
    private readonly ID3D11RenderTargetView _rtv;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11SamplerState _pointSampler;
    private readonly ID3D11SamplerState _chromaSampler;
    private readonly ID3D11Buffer _constants;
    private readonly PixelLayout _layout;
    private readonly int _width;
    private readonly int _height;

    /// <summary>Crea el pipeline para un tamaño y layout concretos.</summary>
    public YuvGpuPipeline(
        int width, int height, PixelLayout layout, ColorParams parameters, ChromaSampling chroma, bool keepAlpha)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        _width = width;
        _height = height;
        _layout = layout;

        // Adaptador de alto rendimiento: esta máquina tiene iGPU AMD + RTX.
        using IDXGIFactory6 factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
        using IDXGIAdapter1 adapter =
            factory.EnumAdapterByGpuPreference<IDXGIAdapter1>(0, GpuPreference.HighPerformance);
        AdapterName = adapter.Description1.Description;

        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            levels, out _device!, out _context!).CheckError();

        // --- Ring de texturas por plano -----------------------------------
        for (int slot = 0; slot < RingSize; slot++)
        {
            for (int p = 0; p < layout.PlaneCount; p++)
            {
                var desc = new Texture2DDescription
                {
                    Width = (uint)layout.PlaneWidth(p, width),
                    Height = (uint)layout.PlaneHeight(p, height),
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.R16_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Dynamic,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.Write,
                    MiscFlags = ResourceOptionFlags.None,
                };
                ID3D11Texture2D tex = _device.CreateTexture2D(desc);
                _planes[slot, p] = tex;
                _srv[slot, p] = _device.CreateShaderResourceView(tex, null);
            }
        }

        // --- Render target COMPARTIDO (lo abriría D3D9Ex en el compositor) --
        var rtDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.Shared,
        };
        _renderTarget = _device.CreateTexture2D(rtDesc);
        _rtv = _device.CreateRenderTargetView(_renderTarget, null);
        using (IDXGIResource dxgi = _renderTarget.QueryInterface<IDXGIResource>())
        {
            SharedHandle = dxgi.SharedHandle;
        }

        // --- Shaders, samplers y constantes --------------------------------
        _vs = _device.CreateVertexShader(Shaders.CompileVertexShader(), null);
        _ps = _device.CreatePixelShader(Shaders.CompilePixelShader(keepAlpha), null);
        _pointSampler = _device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipPoint, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
        _chromaSampler = _device.CreateSamplerState(new SamplerDescription(
            chroma == ChromaSampling.Linear ? Filter.MinMagMipLinear : Filter.MinMagMipPoint,
            TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));

        _constants = _device.CreateBuffer(
            new BufferDescription((uint)ColorParams.SizeInBytes, BindFlags.ConstantBuffer, ResourceUsage.Default));
        _context.UpdateSubresource(parameters, _constants);
        Chroma = chroma;
    }

    /// <summary>Nombre del adaptador donde se creó el device.</summary>
    public string AdapterName { get; }

    /// <summary>Shared handle del render target (para el lado D3D9Ex del compositor).</summary>
    public nint SharedHandle { get; }

    /// <summary>Muestreo de croma en uso.</summary>
    public ChromaSampling Chroma { get; }

    /// <summary>Bytes que se suben por frame (suma de los planos, sin padding).</summary>
    public long UploadBytesPerFrame => _layout.FrameBytes(_width, _height);

    /// <summary>Sube los planos del frame retenido por el decodificador al slot indicado.</summary>
    public unsafe void Upload(int slot, ProResDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        int index = slot % RingSize;

        for (int p = 0; p < _layout.PlaneCount; p++)
        {
            ID3D11Texture2D tex = _planes[index, p]!;
            int rows = _layout.PlaneHeight(p, _height);
            int rowBytes = _layout.PlaneWidth(p, _width) * 2;
            int srcStride = decoder.PlaneStride(p);
            byte* src = (byte*)decoder.PlanePointer(p);

            MappedSubresource mapped = _context.Map(tex, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
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
                        Buffer.MemoryCopy(src + ((long)y * srcStride), dst + ((long)y * dstStride), rowBytes, rowBytes);
                    }
                }
            }
            finally
            {
                _context.Unmap(tex, 0);
            }
        }
    }

    /// <summary>Dibuja el slot al render target compartido y hace <c>Flush</c>.</summary>
    public void Render(int slot)
    {
        int index = slot % RingSize;
        _context.OMSetRenderTargets(_rtv, null);
        _context.RSSetViewport(0, 0, _width, _height, 0, 1);
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vs);
        _context.PSSetShader(_ps);
        _context.PSSetConstantBuffer(0, _constants);
        for (int p = 0; p < MaxPlanes; p++)
        {
            _context.PSSetShaderResource((uint)p, p < _layout.PlaneCount ? _srv[index, p] : null);
        }

        _context.PSSetSampler(0, _pointSampler);
        _context.PSSetSampler(1, _chromaSampler);
        _context.Draw(3, 0);
        _context.Flush();
    }

    /// <summary>Lee el render target a CPU (BGRA, de arriba a abajo). Solo para verificación.</summary>
    public byte[] ReadbackRenderTarget()
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };
        using ID3D11Texture2D staging = _device.CreateTexture2D(desc);
        _context.CopyResource(staging, _renderTarget);

        byte[] pixels = new byte[(long)_width * _height * 4];
        MappedSubresource mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            unsafe
            {
                int rowBytes = _width * 4;
                for (int y = 0; y < _height; y++)
                {
                    new ReadOnlySpan<byte>((byte*)mapped.DataPointer + ((long)y * mapped.RowPitch), rowBytes)
                        .CopyTo(pixels.AsSpan(y * rowBytes, rowBytes));
                }
            }
        }
        finally
        {
            _context.Unmap(staging, 0);
        }

        return pixels;
    }

    /// <summary>
    /// Espera a que la GPU termine lo enviado.
    /// </summary>
    /// <remarks>
    /// Sin esto los tiempos de CPU mienten: <c>Draw</c> y <c>Flush</c> solo
    /// ENCOLAN. Se usa una query de evento (la valla más barata de D3D11) y
    /// solo en los puntos de medición, nunca en el camino caliente de
    /// presentación (allí la sincronía la da el <c>StretchRect</c> del D3D9Ex).
    /// </remarks>
    public unsafe void WaitForGpu()
    {
        using ID3D11Query query = _device.CreateQuery(new QueryDescription(QueryType.Event, QueryFlags.None));
        _context.End(query);
        int done = 0;
        while (_context.GetData(query, (nint)(&done), sizeof(int), AsyncGetDataFlags.None).Failure || done == 0)
        {
            Thread.SpinWait(64);
        }
    }

    /// <inheritdoc/>
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
        _chromaSampler.Dispose();
        _pointSampler.Dispose();
        _ps.Dispose();
        _vs.Dispose();
        _rtv.Dispose();
        _renderTarget.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
