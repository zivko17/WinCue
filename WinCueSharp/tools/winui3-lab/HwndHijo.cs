// =============================================================================
// HwndHijo.cs — ventana hija Win32 bajo el HWND de un AppWindow de WinUI 3.
// =============================================================================
// En WPF esto lo daba HwndHost. WinUI 3 no tiene equivalente, así que la
// ventana hija se crea a mano — que es lo que HwndHost hacía por dentro
// (ver src/WinCue.App/SwapChainHost.cs).
//
// Sobre esta ventana se le pasa el --wid a mpv. Igual que en WPF, una ventana
// hija Win32 se pinta SIEMPRE encima del contenido XAML: el airspace sigue
// existiendo por esta ruta. Solo desaparece con SwapChainPanel (Fase 2b).
// =============================================================================

using System.Runtime.InteropServices;

namespace WinUi3Lab;

internal static class HwndHijo
{
    private const int WsChild = unchecked((int)0x40000000);
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        int exStyle, string clase, string? titulo, int estilo,
        int x, int y, int ancho, int alto,
        nint padre, nint menu, nint instancia, nint param);

    /// <summary>Crea la ventana hija y devuelve su HWND. 0 = fallo.</summary>
    internal static nint Crear(nint padre, int x, int y, int ancho, int alto)
    {
        nint h = CreateWindowExW(
            0, "static", null,
            WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            x, y, ancho, alto, padre, 0, 0, 0);

        if (h == 0)
            throw new InvalidOperationException(
                $"CreateWindowExW falló: {Marshal.GetLastWin32Error()}");

        return h;
    }
}
