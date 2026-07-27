// =============================================================================
// GpuDevice.cs — El ÚNICO device D3D11 del motor (capa L0)
// =============================================================================
// Regla de la arquitectura nueva: todo —fuentes, grafo, salidas— vive en un
// solo ID3D11Device. Hoy no es así (HapGpuPipeline crea el suyo por slot y la
// ruta mpv va por D3D9Ex), y por eso no puede haber compositor: no se pueden
// muestrear en un mismo paso texturas de devices distintos.
//
// MULTITHREAD-PROTECTED de verdad: lo tocan el hilo GL de las fuentes mpv, los
// hilos de decodificación de cada fuente y el hilo de render del compositor.
// Sin esto, D3D11 no serializa el contexto inmediato y el comportamiento es
// indefinido.
//
// FORMATO DE TRABAJO como parámetro (no constante): RGBA8 por defecto — lo que
// hace todo el mundo—, con RGBA16F en luz lineal contemplado desde el diseño.
// Encenderlo se pospone a propósito hasta tener paridad con la ruta vieja:
// cambia el aspecto de TODOS los fundidos y haría imposible comparar.
// =============================================================================

using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace WinCue.Video.Gpu;

/// <summary>Espacio de trabajo del compositor.</summary>
public enum GpuWorkingFormat
{
    /// <summary>B8G8R8A8 sRGB. Barato; la mezcla es matemáticamente incorrecta
    /// (los fundidos pierden luz a mitad de recorrido) pero es el estándar de
    /// facto del sector.</summary>
    Rgba8,

    /// <summary>R16G16B16A16 float en LUZ LINEAL. Mezcla correcta, sin bache
    /// en el centro del cruce; abre 10-bit/HDR sin rediseño. Cuesta el doble
    /// de ancho de banda.</summary>
    Rgba16Float,
}

public sealed class GpuDevice : IDisposable
{
    private static readonly object SharedLock = new();
    private static GpuDevice? _shared;

    private readonly ID3D11Multithread? _multithread;
    private bool _disposed;

    public ID3D11Device Device { get; }

    /// <summary>Contexto INMEDIATO. Protegido para multihilo, pero sigue siendo
    /// un cuello: las fuentes deben subir por su cuenta y el compositor es el
    /// único que graba dibujo.</summary>
    public ID3D11DeviceContext Context { get; }

    /// <summary>LUID del adaptador. Lo necesita cualquier interop que tenga que
    /// emparejar dispositivos (D3D9Ex de la ruta vieja, y el diagnóstico de
    /// máquinas con iGPU + dGPU, donde el ordinal 0 no es fiable).</summary>
    public long AdapterLuid { get; }

    public string AdapterName { get; }

    public GpuWorkingFormat WorkingFormat { get; }

    /// <summary>Formato DXGI de las render targets intermedias y del anillo.</summary>
    public Format TextureFormat => WorkingFormat == GpuWorkingFormat.Rgba16Float
        ? Format.R16G16B16A16_Float
        : Format.B8G8R8A8_UNorm;

    /// <summary>Device compartido del proceso. Las pruebas pueden crear el suyo.</summary>
    public static GpuDevice Shared
    {
        get
        {
            lock (SharedLock)
                return _shared ??= new GpuDevice();
        }
    }

    public GpuDevice(GpuWorkingFormat workingFormat = GpuWorkingFormat.Rgba8)
    {
        WorkingFormat = workingFormat;

        using IDXGIFactory6 factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
        using IDXGIAdapter1 adapter =
            factory.EnumAdapterByGpuPreference<IDXGIAdapter1>(0, GpuPreference.HighPerformance);
        AdapterDescription1 desc = adapter.Description1;
        AdapterName = desc.Description.Trim();
        AdapterLuid = ((long)desc.Luid.HighPart << 32) | (uint)desc.Luid.LowPart;

        FeatureLevel[] levels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            levels, out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
        Device = device!;
        Context = context!;

        try
        {
            _multithread = Device.QueryInterface<ID3D11Multithread>();
            _multithread.SetMultithreadProtected(true);
        }
        catch (SharpGenException)
        {
            // Sin ID3D11Multithread el device NO es seguro para el modelo de
            // hilos del motor: mejor caer aquí que corromper el contexto en
            // producción tres horas después.
            Dispose();
            throw new NotSupportedException(
                "El driver no expone ID3D11Multithread; el motor necesita el " +
                "contexto protegido para multihilo.");
        }
    }

    /// <summary>Textura de trabajo (render target + muestreable) del formato
    /// del motor. Es el ladrillo del anillo de frames y de las RT intermedias.
    /// </summary>
    public ID3D11Texture2D CreateWorkTexture(int width, int height, bool shared = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Math.Max(1, width),
            Height = (uint)Math.Max(1, height),
            MipLevels = 1,
            ArraySize = 1,
            Format = TextureFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = shared ? ResourceOptionFlags.Shared : ResourceOptionFlags.None,
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _multithread?.Dispose();
        Context?.Dispose();
        Device?.Dispose();
    }
}
