using System.Diagnostics;

using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HapLab.View;

/// <summary>Estrategia de subida del DXT a la textura D3D11.</summary>
internal enum UploadMode
{
    /// <summary>Textura DYNAMIC + Map WRITE_DISCARD (ring de 3).</summary>
    Dynamic,

    /// <summary>Textura DEFAULT + UpdateSubresource (ring de 3).</summary>
    Update,
}

/// <summary>
/// Renderer D3D11 del laboratorio (Vortice, MIT — decisión de lab; la
/// integración final decidirá Vortice vs P/Invoke). Pipeline: textura BC1/BC3
/// (la GPU descomprime el DXT al muestrear) → triángulo fullscreen con pixel
/// shader (passthrough o YCoCg→RGB) → render target B8G8R8A8 creado
/// COMPARTIDO cuyo shared handle abre luego el lado D3D9Ex para el D3DImage.
/// </summary>
internal sealed class D3D11Renderer : IDisposable
{
    private const int RingSize = 3;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Texture2D[] _ring = new ID3D11Texture2D[RingSize];
    private readonly ID3D11ShaderResourceView[] _ringSrv = new ID3D11ShaderResourceView[RingSize];
    private readonly ID3D11Texture2D _renderTarget;
    private readonly ID3D11RenderTargetView _rtv;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11SamplerState _sampler;
    private readonly int _width;
    private readonly int _height;
    private readonly int _blockRows;     // filas de bloques 4x4 del DXT
    private readonly int _rowBytes;      // bytes por fila de bloques (pitch "apretado")

    public D3D11Renderer(int width, int height, HapTextureFormat format, UploadMode requestedMode)
    {
        _width = width;
        _height = height;
        int blocksX = (width + 3) / 4;
        _blockRows = (height + 3) / 4;
        _rowBytes = blocksX * (format == HapTextureFormat.RgbDxt1 ? 8 : 16);

        // Adaptador de alto rendimiento (esta máquina tiene iGPU AMD + RTX):
        // el device D3D9Ex del presentador debe caer en el MISMO adaptador
        // (se empareja por LUID).
        using IDXGIFactory6 factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
        using IDXGIAdapter1 adapter =
            factory.EnumAdapterByGpuPreference<IDXGIAdapter1>(0, GpuPreference.HighPerformance);
        AdapterDescription1 desc = adapter.Description1;
        AdapterName = desc.Description;
        AdapterLuid = ((long)desc.Luid.HighPart << 32) | desc.Luid.LowPart;

        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            levels, out _device!, out _context!).CheckError();

        // --- Ring de texturas BC + SRVs -----------------------------------
        Format texFormat = format == HapTextureFormat.RgbDxt1 ? Format.BC1_UNorm : Format.BC3_UNorm;
        Mode = requestedMode;
        var texDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = texFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = requestedMode == UploadMode.Dynamic ? ResourceUsage.Dynamic : ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = requestedMode == UploadMode.Dynamic ? CpuAccessFlags.Write : CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        try
        {
            CreateRing(texDesc);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            // Callejón documentado en el INFORME: si el driver rechaza BC
            // dinámico, caemos a DEFAULT + UpdateSubresource.
            if (requestedMode != UploadMode.Dynamic)
            {
                throw;
            }

            Mode = UploadMode.Update;
            texDesc.Usage = ResourceUsage.Default;
            texDesc.CPUAccessFlags = CpuAccessFlags.None;
            CreateRing(texDesc);
        }

        // --- Render target COMPARTIDO (lo abre D3D9Ex por shared handle) --
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
        using (IDXGIResource dxgiResource = _renderTarget.QueryInterface<IDXGIResource>())
        {
            SharedHandle = dxgiResource.SharedHandle;
        }

        // --- Shaders + sampler --------------------------------------------
        _vs = _device.CreateVertexShader(Shaders.CompileVertexShader(), null);
        byte[] psBlob = format == HapTextureFormat.YCoCgDxt5
            ? Shaders.CompileScaledYCoCgPixelShader()
            : Shaders.CompilePassthroughPixelShader();
        _ps = _device.CreatePixelShader(psBlob, null);
        _sampler = _device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipPoint, TextureAddressMode.Clamp,
            TextureAddressMode.Clamp, TextureAddressMode.Clamp));
    }

    /// <summary>Estrategia de subida REAL (puede degradar de Dynamic a Update).</summary>
    public UploadMode Mode { get; private set; }

    /// <summary>Nombre del adaptador donde se creó el device.</summary>
    public string AdapterName { get; }

    /// <summary>LUID del adaptador (para crear el device D3D9Ex en el mismo).</summary>
    public long AdapterLuid { get; }

    /// <summary>Shared handle del render target (lo abre el presentador D3D9Ex).</summary>
    public nint SharedHandle { get; }

    private void CreateRing(Texture2DDescription desc)
    {
        for (int i = 0; i < RingSize; i++)
        {
            _ring[i] = _device.CreateTexture2D(desc);
            _ringSrv[i] = _device.CreateShaderResourceView(_ring[i], null);
        }
    }

    /// <summary>Sube el DXT de un frame al slot <paramref name="slot"/> del ring.</summary>
    public void Upload(int slot, ReadOnlySpan<byte> dxt)
    {
        ID3D11Texture2D tex = _ring[slot];
        if (Mode == UploadMode.Dynamic)
        {
            MappedSubresource mapped = _context.Map(tex, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            try
            {
                CopyRows(dxt, mapped.DataPointer, (int)mapped.RowPitch);
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
                    _context.UpdateSubresource(tex, 0, null, (nint)src, (uint)_rowBytes, 0);
                }
            }
        }
    }

    private unsafe void CopyRows(ReadOnlySpan<byte> dxt, nint dest, int destPitch)
    {
        if (destPitch == _rowBytes)
        {
            dxt.CopyTo(new Span<byte>((void*)dest, dxt.Length));
            return;
        }

        for (int row = 0; row < _blockRows; row++)
        {
            dxt.Slice(row * _rowBytes, _rowBytes)
               .CopyTo(new Span<byte>((byte*)dest + ((long)row * destPitch), _rowBytes));
        }
    }

    /// <summary>Dibuja el slot al render target compartido y hace Flush del
    /// contexto (requisito antes del StretchRect del lado D3D9).</summary>
    public void Render(int slot)
    {
        _context.OMSetRenderTargets(_rtv, null);
        _context.RSSetViewport(0, 0, _width, _height, 0, 1);
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vs);
        _context.PSSetShader(_ps);
        _context.PSSetShaderResource(0, _ringSrv[slot]);
        _context.PSSetSampler(0, _sampler);
        _context.Draw(3, 0);
        _context.Flush();
    }

    /// <summary>Lee el render target a CPU (BGRA de arriba a abajo). Solo para
    /// el volcado de verificación (--dump-frame); fuera del camino caliente.</summary>
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

        byte[] pixels = new byte[_width * _height * 4];
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

    public void Dispose()
    {
        Debug.Assert(_context is not null, "contexto creado en el ctor");
        for (int i = 0; i < RingSize; i++)
        {
            _ringSrv[i]?.Dispose();
            _ring[i]?.Dispose();
        }

        _sampler.Dispose();
        _ps.Dispose();
        _vs.Dispose();
        _rtv.Dispose();
        _renderTarget.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
