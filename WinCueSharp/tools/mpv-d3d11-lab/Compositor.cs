// =============================================================================
// Compositor.cs — El compositor que HOY no existe: un único paso D3D11 que
// mezcla N capas en UNA render target
// =============================================================================
// Hoy cada capa acaba en su propio D3DImage y la mezcla la hace el DWM con
// Opacity de WPF. Aquí la mezcla es NUESTRA: mismo device, un draw por capa,
// alpha por BLEND FACTOR (src*f + dst*(1-f)) — la misma aritmética que aplica
// el DWM, pero bajo nuestro control y con un punto de extracción (la RT final)
// del que se puede sacar multiviewer, grabación o NDI.
//
// Sin constant buffer a propósito: la opacidad viaja en el blend factor de
// OMSetBlendState, así el shader es un passthrough puro.
// =============================================================================

using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace MpvD3D11Lab;

internal sealed class Compositor : IDisposable
{
    private const string Source = """
        struct VsOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

        VsOut VsFullscreen(uint id : SV_VertexID)
        {
            VsOut o;
            float2 uv = float2((id << 1) & 2, id & 2);
            o.pos = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
            o.uv = uv;
            return o;
        }

        Texture2D tex0 : register(t0);
        SamplerState smp0 : register(s0);

        float4 PsLayer(VsOut i) : SV_Target
        {
            return float4(tex0.Sample(smp0, i.uv).rgb, 1.0);
        }
        """;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly ID3D11Texture2D _rt;
    private readonly ID3D11RenderTargetView _rtv;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11BlendState _blend;

    public int Width { get; }
    public int Height { get; }
    public ID3D11Texture2D Output => _rt;

    public Compositor(ID3D11Device device, ID3D11DeviceContext ctx, int w, int h)
    {
        _device = device;
        _ctx = ctx;
        Width = w;
        Height = h;

        _rt = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _rtv = device.CreateRenderTargetView(_rt, null);

        _vs = device.CreateVertexShader(
            Compiler.Compile(Source, "VsFullscreen", "lab", "vs_5_0").ToArray(), null);
        _ps = device.CreatePixelShader(
            Compiler.Compile(Source, "PsLayer", "lab", "ps_5_0").ToArray(), null);
        _sampler = device.CreateSamplerState(new SamplerDescription(
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
        _blend = device.CreateBlendState(bd);
    }

    /// <summary>Arranca el frame: fija la RT y la limpia (el "fondo" del show).</summary>
    public void Begin(float r, float g, float b)
    {
        _ctx.OMSetRenderTargets(_rtv);
        _ctx.RSSetViewport(new Viewport(0, 0, Width, Height));
        _ctx.ClearRenderTargetView(_rtv, new Color4(r, g, b, 1.0f));
    }

    /// <summary>Pinta una capa con su opacidad. El orden de llamada ES el orden
    /// de las pistas.</summary>
    public void DrawLayer(ID3D11ShaderResourceView srv, float opacity)
    {
        _ctx.VSSetShader(_vs);
        _ctx.PSSetShader(_ps);
        _ctx.PSSetShaderResource(0, srv);
        _ctx.PSSetSampler(0, _sampler);
        _ctx.IASetInputLayout(null);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.OMSetBlendState(_blend, new Color4(opacity, opacity, opacity, opacity));
        _ctx.Draw(3, 0);
        // Soltar el SRV: la textura vuelve a estar libre para el interop GL
        _ctx.PSSetShaderResource(0, null!);
    }

    public void Dispose()
    {
        _blend.Dispose();
        _sampler.Dispose();
        _ps.Dispose();
        _vs.Dispose();
        _rtv.Dispose();
        _rt.Dispose();
    }
}

/// <summary>Utilidades de lectura de vuelta (verificación por píxel).</summary>
internal static class Gpu
{
    /// <summary>Copia la textura a una staging y devuelve sus bytes BGRA
    /// compactados (sin row pitch). Esto es lo que prueba que los píxeles
    /// CRUZARON de verdad de OpenGL a Direct3D — no que la API no diera error.
    /// </summary>
    public static byte[] ReadBack(ID3D11Device device, ID3D11DeviceContext ctx,
        ID3D11Texture2D texture, int w, int h)
    {
        using ID3D11Texture2D staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });

        ctx.CopyResource(staging, texture);
        MappedSubresource map = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            byte[] outBytes = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                nint row = map.DataPointer + (int)(y * map.RowPitch);
                System.Runtime.InteropServices.Marshal.Copy(row, outBytes, y * w * 4, w * 4);
            }
            return outBytes;
        }
        finally
        {
            ctx.Unmap(staging, 0);
        }
    }

    /// <summary>Píxel (x,y) como (B,G,R,A).</summary>
    public static (byte B, byte G, byte R, byte A) Pixel(byte[] bgra, int w, int x, int y)
    {
        int i = (y * w + x) * 4;
        return (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]);
    }

    /// <summary>Media de luma aproximada — sirve para decir "esto no es negro".</summary>
    public static double MeanLuma(byte[] bgra)
    {
        double sum = 0;
        for (int i = 0; i < bgra.Length; i += 4)
            sum += 0.114 * bgra[i] + 0.587 * bgra[i + 1] + 0.299 * bgra[i + 2];
        return sum / (bgra.Length / 4.0);
    }
}
