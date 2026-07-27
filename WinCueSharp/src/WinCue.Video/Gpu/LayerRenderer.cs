// =============================================================================
// LayerRenderer.cs — El único primitivo de dibujo del compositor (capa L3)
// =============================================================================
// Todo lo que pinta el motor nuevo pasa por aquí: una textura, en un rectángulo
// del destino, con un recorte del origen y una opacidad. Los nodos del grafo
// solo deciden QUÉ dibujar y en qué orden; el CÓMO está en un sitio.
//
// La opacidad viaja en el BLEND FACTOR (src*f + dst*(1-f)) — la misma
// aritmética que aplica hoy el DWM con Opacity de WPF, pero bajo nuestro
// control, en nuestro espacio de color y en nuestro instante.
//
// CERO ASIGNACIONES por dibujo: los descriptores son structs y el constant
// buffer se reescribe con Map/WriteDiscard. Esto vive en el hilo de render.
// =============================================================================

using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace WinCue.Video.Gpu;

/// <summary>Recorte del ORIGEN en coordenadas normalizadas.</summary>
public readonly record struct UvRect(float X, float Y, float W, float H)
{
    /// <summary>La textura entera.</summary>
    public static UvRect Full => new(0f, 0f, 1f, 1f);
}

/// <summary>Rectángulo de DESTINO en píxeles del lienzo.</summary>
public readonly record struct DestRect(int X, int Y, int W, int H);

internal sealed class LayerRenderer : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11BlendState _blend;
    private readonly ID3D11Buffer _cb;
    private bool _disposed;

    public LayerRenderer(GpuDevice gpu)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        _gpu = gpu;

        _vs = gpu.Device.CreateVertexShader(CompositorShaders.CompileVertexShader(), null);
        _ps = gpu.Device.CreatePixelShader(CompositorShaders.CompilePixelShader(), null);

        // Bilineal: el compositor escala de verdad (letterbox, PiP, canvas) y
        // el punto más cercano se ve como escalera en material real.
        _sampler = gpu.Device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipLinear, TextureAddressMode.Clamp,
            TextureAddressMode.Clamp, TextureAddressMode.Clamp));

        var bd = new BlendDescription();
        bd.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.BlendFactor,
            DestinationBlend = Blend.InverseBlendFactor,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.Zero,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _blend = gpu.Device.CreateBlendState(bd);

        _cb = gpu.Device.CreateBuffer(new BufferDescription
        {
            ByteWidth = 16,
            BindFlags = BindFlags.ConstantBuffer,
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
        });
    }

    /// <summary>Limpia el destino (el "fondo del show").</summary>
    public void Clear(ID3D11RenderTargetView rtv, float r, float g, float b)
    {
        _gpu.Context.ClearRenderTargetView(rtv, new Color4(r, g, b, 1f));
    }

    /// <summary>Dibuja una capa. <paramref name="opacity"/> 1 = opaca.</summary>
    public void Draw(ID3D11RenderTargetView target, ID3D11ShaderResourceView source,
        in DestRect dest, in UvRect uv, float opacity)
    {
        if (_disposed || opacity <= 0f)
            return;

        ID3D11DeviceContext ctx = _gpu.Context;

        unsafe
        {
            MappedSubresource m = ctx.Map(_cb, 0, MapMode.WriteDiscard,
                Vortice.Direct3D11.MapFlags.None);
            float* p = (float*)m.DataPointer;
            p[0] = uv.W;   // escala
            p[1] = uv.H;
            p[2] = uv.X;   // desplazamiento
            p[3] = uv.Y;
            ctx.Unmap(_cb, 0);
        }

        ctx.OMSetRenderTargets(target);
        ctx.RSSetViewport(dest.X, dest.Y, Math.Max(1, dest.W), Math.Max(1, dest.H), 0f, 1f);
        ctx.IASetInputLayout(null);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_ps);
        ctx.PSSetConstantBuffer(0, _cb);
        ctx.PSSetShaderResource(0, source);
        ctx.PSSetSampler(0, _sampler);
        float f = Math.Clamp(opacity, 0f, 1f);
        ctx.OMSetBlendState(_blend, new Color4(f, f, f, f));
        ctx.Draw(3, 0);

        // Soltar la vista: si el recurso es un slot de anillo o una RT del
        // pool, el productor debe poder volver a escribirlo sin que siga
        // enlazado como entrada del pipeline.
        ctx.PSSetShaderResource(0, null!);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cb.Dispose();
        _blend.Dispose();
        _sampler.Dispose();
        _ps.Dispose();
        _vs.Dispose();
    }
}
