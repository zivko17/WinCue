// =============================================================================
// OutputSinks.cs — A dónde presenta el compositor (capa L4)
// =============================================================================
// El TICK DE SALIDA que hoy no existe vive aquí: el sink es quien decide
// cuándo toca el siguiente fotograma. Con swapchain eso es el vsync de la
// pantalla; sin él, un reloj de software.
//
//   SwapChainSink — pantalla física, flip model, Present(1).
//   TextureSink   — fuera de pantalla. Es lo que usan las pruebas, y la base
//                   del multiviewer y de la grabación de F4 (ahí hará falta
//                   readback ASÍNCRONO: leer síncrono ahoga el hilo de render).
//
// Con el swapchain desaparece de raíz toda la clase de fallos de la auditoría
// 05: no hay D3DImage, ni dirty-rects, ni escritura fuera del Lock, ni la
// pareja de superficies por capa.
// =============================================================================

using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace WinCue.Video.Gpu;

public interface IOutputSink : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>Destino del fotograma en curso.</summary>
    ID3D11RenderTargetView Rtv { get; }

    /// <summary>Bloquea hasta que toque el siguiente fotograma. Devuelve false
    /// si el sink está cerrando.</summary>
    bool WaitForNextFrame();

    /// <summary>Publica el fotograma.</summary>
    void Present();
}

/// <summary>Salida a pantalla: swapchain flip-model sobre un HWND.</summary>
public sealed class SwapChainSink : IOutputSink
{
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    private readonly IDXGISwapChain1 _swapChain;
    private readonly IDXGISwapChain2? _swapChain2;
    private readonly ID3D11RenderTargetView _rtv;
    private readonly ID3D11Texture2D _backBuffer;
    private readonly nint _waitable;
    private volatile bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public ID3D11RenderTargetView Rtv => _rtv;

    /// <summary>Intervalo de presentación: 1 = a vsync (lo normal en sala),
    /// 0 = tan rápido como pueda (para medir).</summary>
    public int SyncInterval { get; set; } = 1;

    public SwapChainSink(GpuDevice gpu, nint hwnd, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        if (hwnd == 0)
            throw new ArgumentException("HWND de salida no válido.", nameof(hwnd));

        Width = Math.Max(16, width);
        Height = Math.Max(16, height);

        using IDXGIDevice dxgiDevice = gpu.Device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();

        var desc = new SwapChainDescription1
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = gpu.TextureFormat,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            // FLIP_DISCARD: el modelo moderno. Sin bitblt intermedio, sin la
            // copia extra del DWM y con latencia acotada.
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            Flags = SwapChainFlags.FrameLatencyWaitableObject,
        };

        _swapChain = factory.CreateSwapChainForHwnd(gpu.Device, hwnd, desc);

        // Sin ALT+ENTER: una salida de espectáculo no cambia de modo porque el
        // usuario roce el teclado.
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);

        try
        {
            _swapChain2 = _swapChain.QueryInterface<IDXGISwapChain2>();
            _swapChain2.MaximumFrameLatency = 1;
            _waitable = _swapChain2.FrameLatencyWaitableObject;
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            _swapChain2 = null;
            _waitable = 0;   // sin objeto de espera: Present(1) ya bloquea a vsync
        }

        _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _rtv = gpu.Device.CreateRenderTargetView(_backBuffer, null);
    }

    public bool WaitForNextFrame()
    {
        if (_disposed)
            return false;
        if (_waitable != 0)
            _ = WaitForSingleObject(_waitable, 1000);
        return !_disposed;
    }

    public void Present()
    {
        if (_disposed)
            return;
        _ = _swapChain.Present((uint)Math.Max(0, SyncInterval), PresentFlags.None);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _rtv.Dispose();
        _backBuffer.Dispose();
        _swapChain2?.Dispose();
        _swapChain.Dispose();
    }
}

/// <summary>Salida fuera de pantalla, con reloj de software. Base del
/// multiviewer y de la grabación, y lo que permite probar el compositor entero
/// sin ventana.</summary>
public sealed class TextureSink : IOutputSink
{
    private readonly ID3D11Texture2D _texture;
    private readonly ID3D11RenderTargetView _rtv;
    private readonly double _framePeriodMs;
    private long _nextFrameTick;
    private volatile bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public ID3D11RenderTargetView Rtv => _rtv;

    /// <summary>La textura compuesta. De aquí sale el readback de los sinks
    /// de F4 (asíncrono allí) y la verificación por píxel de las pruebas.</summary>
    public ID3D11Texture2D Texture => _texture;

    public long FramesPresented { get; private set; }

    public TextureSink(GpuDevice gpu, int width, int height, double fps = 60.0)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        _framePeriodMs = 1000.0 / (fps > 0 ? fps : 60.0);
        _texture = gpu.CreateWorkTexture(Width, Height);
        try
        {
            _rtv = gpu.Device.CreateRenderTargetView(_texture, null);
        }
        catch
        {
            _texture.Dispose();
            throw;
        }
        _nextFrameTick = Environment.TickCount64;
    }

    public bool WaitForNextFrame()
    {
        if (_disposed)
            return false;
        long now = Environment.TickCount64;
        int wait = (int)(_nextFrameTick - now);
        if (wait > 0)
            Thread.Sleep(Math.Min(wait, 100));
        _nextFrameTick = Math.Max(now, _nextFrameTick) + (long)Math.Round(_framePeriodMs);
        return !_disposed;
    }

    public void Present() => FramesPresented++;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _rtv.Dispose();
        _texture.Dispose();
    }
}
