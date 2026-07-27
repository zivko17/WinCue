// =============================================================================
// HapRingTarget.cs — El motor HAP dibujando en el anillo (capa L1)
// =============================================================================
// Mismo dibujo que HapGpuPipeline (textura BC → triángulo fullscreen con el
// pixel shader passthrough o YCoCg→RGB), pero el destino ya no es una RT
// compartida que acaba en un D3DImage: es el slot del anillo que el compositor
// va a muestrear. Aquí desaparece TODA la mitad D3D9Ex — shared handles,
// StretchRect, superficie de presentación — porque ya no hay que salir del
// mundo D3D11.
//
// La lógica de reproducción (decodificación, reloj, pacing, loop, trims) no se
// duplica: la sigue poniendo HapCuePlayer a través de IHapRenderTarget.
//
// HILOS: Upload/Render/NotifyFrame llegan del hilo de presentación del player.
// El contexto de GpuDevice está protegido para multihilo.
// =============================================================================

using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using WinCue.Video.Hap;

namespace WinCue.Video.Gpu;

internal sealed class HapRingTarget : IHapRenderTarget, IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly ID3D11RenderTargetView[] _rtvs;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _psPassthrough;
    private readonly ID3D11PixelShader _psYCoCg;
    private readonly ID3D11SamplerState _sampler;
    private readonly object _sync = new();

    /// <summary>Slot reservado por el Render en curso, a la espera de que
    /// NotifyFrame lo publique con su PTS. -1 = el anillo estaba lleno y este
    /// fotograma se descarta (nadie espera a nadie).</summary>
    private int _pending = -1;

    private bool _disposed;

    public FrameRing Ring { get; }

    public int Width => Ring.Width;
    public int Height => Ring.Height;

    public HapRingTarget(GpuDevice gpu, int surfaceWidth, int surfaceHeight,
        int ringCapacity = 3)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        _gpu = gpu;
        Ring = new FrameRing(gpu, surfaceWidth, surfaceHeight, ringCapacity);
        _rtvs = new ID3D11RenderTargetView[Ring.Capacity];
        try
        {
            for (int i = 0; i < Ring.Capacity; i++)
                _rtvs[i] = gpu.Device.CreateRenderTargetView(Ring.TextureAt(i), null);

            _vs = gpu.Device.CreateVertexShader(HapShaders.CompileVertexShader(), null);
            _psPassthrough = gpu.Device.CreatePixelShader(
                HapShaders.CompilePassthroughPixelShader(), null);
            _psYCoCg = gpu.Device.CreatePixelShader(
                HapShaders.CompileScaledYCoCgPixelShader(), null);
            _sampler = gpu.Device.CreateSamplerState(new SamplerDescription(
                Filter.MinMagMipPoint, TextureAddressMode.Clamp,
                TextureAddressMode.Clamp, TextureAddressMode.Clamp));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public HapVideoSession CreateSession(int videoWidth, int videoHeight, HapTextureFormat format) =>
        HapVideoSession.Create(_gpu.Device, videoWidth, videoHeight, format);

    public void Upload(HapVideoSession session, int slot, ReadOnlySpan<byte> dxt)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Upload(_gpu.Context, slot, dxt);
    }

    public void Render(HapVideoSession session, int slot, int destX, int destY,
        int destW, int destH)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_sync)
        {
            if (_disposed)
                return;

            _pending = Ring.BeginWrite();
            if (_pending < 0)
                return;   // anillo lleno: se salta este fotograma

            ID3D11DeviceContext ctx = _gpu.Context;
            ctx.OMSetRenderTargets(_rtvs[_pending]);
            ctx.ClearRenderTargetView(_rtvs[_pending], new Color4(0f, 0f, 0f, 1f));
            ctx.RSSetViewport(destX, destY, Math.Max(1, destW), Math.Max(1, destH), 0, 1);
            ctx.IASetInputLayout(null);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.VSSetShader(_vs);
            ctx.PSSetShader(session.YCoCg ? _psYCoCg : _psPassthrough);
            ctx.PSSetShaderResource(0, session.RingSrv[slot]);
            ctx.PSSetSampler(0, _sampler);
            ctx.Draw(3, 0);
            // Soltar la vista: la textura BC vuelve a estar libre para el
            // siguiente Upload del player sobre ese mismo slot del ring.
            ctx.PSSetShaderResource(0, null!);
            ctx.Flush();
        }
    }

    public void NotifyFrame(double pts)
    {
        lock (_sync)
        {
            if (_disposed || _pending < 0)
                return;
            Ring.Commit(_pending, pts);
            _pending = -1;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _sampler?.Dispose();
        _psYCoCg?.Dispose();
        _psPassthrough?.Dispose();
        _vs?.Dispose();
        foreach (ID3D11RenderTargetView? rtv in _rtvs)
            rtv?.Dispose();
        Ring.Dispose();
    }
}
