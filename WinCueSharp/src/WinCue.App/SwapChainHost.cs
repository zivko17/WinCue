// =============================================================================
// SwapChainHost.cs — La ventana de salida hospedando un swapchain (capa L4)
// =============================================================================
// El compositor propio presenta a un HWND, no a un D3DImage. Este control es
// ese HWND dentro del árbol de WPF: una ventana hija sobre la que DXGI crea el
// swapchain.
//
// POR QUÉ IMPORTA: con esto desaparece de raíz toda la clase de fallos de la
// auditoría 05 — no hay D3DImage, ni dirty-rects, ni la regla de escribir solo
// dentro del Lock, ni la pareja de superficies por capa. WPF deja de estar en
// el camino del vídeo.
//
// LO QUE TRAE A CAMBIO — el "airspace": una ventana hija Win32 SIEMPRE se
// pinta por encima del contenido WPF, así que nada de WPF puede superponerse.
// Por eso el slate y el velo de blackout pasan a ser nodos del grafo
// (SolidNode) en vez de elementos WPF con Opacity.
// =============================================================================

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinCue.App;

/// <summary>Ventana hija Win32 para que DXGI cree su swapchain encima.</summary>
// CA2216: HwndHost (la base) YA trae el finalizador y el ciclo de vida del
// HWND se cierra en DestroyWindowCore — que es el contrato de WPF para esto.
// Un finalizador propio aquí sería redundante y peligroso (destruiría la
// ventana desde el hilo del GC, no desde el de UI).
#pragma warning disable CA2216
public sealed class SwapChainHost : HwndHost
#pragma warning restore CA2216
{
    private const int WsChild = unchecked((int)0x40000000);
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className,
        string? windowName, uint style, int x, int y, int w, int h,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    private nint _hwnd;

    /// <summary>HWND de la superficie de salida. 0 hasta que el control entra
    /// en el árbol visual — el compositor debe crearse DESPUÉS de eso.</summary>
    public nint SurfaceHandle => _hwnd;

    /// <summary>Se dispara cuando el HWND ya existe: el momento de crear el
    /// SwapChainSink y arrancar el compositor.</summary>
    public event EventHandler? SurfaceReady;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = CreateWindowExW(
            0, "STATIC", null,
            (uint)(WsChild | WsVisible | WsClipChildren | WsClipSiblings),
            0, 0,
            (int)Math.Max(1, ActualWidth), (int)Math.Max(1, ActualHeight),
            hwndParent.Handle, 0, 0, 0);

        if (_hwnd == 0)
            throw new InvalidOperationException(
                $"No se pudo crear la superficie de salida (0x{Marshal.GetLastWin32Error():X}).");

        SurfaceReady?.Invoke(this, EventArgs.Empty);
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (_hwnd != 0)
        {
            DestroyWindow(_hwnd);
            _hwnd = 0;
        }
    }
}
