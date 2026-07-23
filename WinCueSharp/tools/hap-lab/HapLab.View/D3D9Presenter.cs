using Vortice.Direct3D9;

namespace HapLab.View;

/// <summary>
/// Lado D3D9Ex de la presentación — la MISMA técnica que el compositor real de
/// WinCue (GlDxInterop + CompositorLayerControl): abre el render target
/// compartido de D3D11 como textura D3D9 (A8R8G8B8 ↔ B8G8R8A8) y mantiene una
/// superficie de PRESENTACIÓN separada que es la que consume el D3DImage.
/// Lección de la auditoría 05: WPF jamás toca la superficie compartida —
/// recibe una copia (StretchRect) hecha SIEMPRE dentro del Lock del D3DImage,
/// y todas las superficies se crean UNA vez (el ciclo crear/destruir por cue
/// fugaba VRAM vía interop).
/// </summary>
internal sealed class D3D9Presenter : IDisposable
{
    private readonly IDirect3D9Ex _d3d9;
    private readonly IDirect3DDevice9Ex _device;
    private readonly IDirect3DTexture9 _sharedTexture;
    private readonly IDirect3DSurface9 _sharedSurface;
    private readonly IDirect3DSurface9 _presentSurface;
    private readonly Rect _fullRect;

    /// <param name="hwnd">Ventana de foco del device (la ventana WPF).</param>
    /// <param name="width">Ancho del render target compartido.</param>
    /// <param name="height">Alto del render target compartido.</param>
    /// <param name="d3d11SharedHandle">Shared handle del RT creado en D3D11.</param>
    /// <param name="adapterLuid">LUID del adaptador del device D3D11 (mismo adaptador o el interop falla).</param>
    public D3D9Presenter(nint hwnd, int width, int height, nint d3d11SharedHandle, long adapterLuid)
    {
        _fullRect = new Rect(0, 0, width, height);
        _d3d9 = D3D9.Direct3DCreate9Ex();

        // Mismo adaptador que el device D3D11 (emparejado por LUID; en esta
        // máquina conviven la iGPU AMD y la RTX y el ordinal 0 no es fiable)
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

        var pp = new PresentParameters
        {
            Windowed = true,
            SwapEffect = SwapEffect.Discard,
            BackBufferWidth = 1,
            BackBufferHeight = 1,
            BackBufferFormat = Format.Unknown,
            BackBufferCount = 1,
            DeviceWindowHandle = hwnd,
        };
        _device = _d3d9.CreateDeviceEx(ordinal, DeviceType.Hardware, hwnd,
            CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
            pp);

        // Abrir el shared handle de D3D11 como textura D3D9: pasar el handle
        // EXISTENTE en el parámetro pSharedHandle de CreateTexture (ruta
        // documentada de interop D3D9Ex; A8R8G8B8 es el espejo de B8G8R8A8).
        nint shared = d3d11SharedHandle;
        _sharedTexture = _device.CreateTexture((uint)width, (uint)height, 1,
            Usage.RenderTarget, Format.A8R8G8B8, Pool.Default, ref shared);
        _sharedSurface = _sharedTexture.GetSurfaceLevel(0);

        // Superficie de PRESENTACIÓN separada (no compartida): el back buffer
        // que ve WPF. Creada UNA vez y viva toda la sesión (auditoría 05).
        _presentSurface = _device.CreateRenderTarget((uint)width, (uint)height,
            Format.A8R8G8B8, MultisampleType.None, 0, lockable: false);
    }

    /// <summary>Puntero nativo de la superficie de presentación, para
    /// <c>D3DImage.SetBackBuffer(IDirect3DSurface9, …)</c> (binding único).</summary>
    public nint PresentSurfacePointer => _presentSurface.NativePointer;

    /// <summary>Copia GPU compartida→presentación. Llamar SIEMPRE dentro del
    /// Lock del D3DImage y con el contexto D3D11 ya flusheado.</summary>
    public void CopyPresent() =>
        _device.StretchRect(_sharedSurface, _fullRect, _presentSurface, _fullRect, TextureFilter.None);

    public void Dispose()
    {
        _presentSurface.Dispose();
        _sharedSurface.Dispose();
        _sharedTexture.Dispose();
        _device.Dispose();
        _d3d9.Dispose();
    }
}
