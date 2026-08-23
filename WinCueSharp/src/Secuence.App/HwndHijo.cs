// =============================================================================
// HwndHijo.cs — ventana hija Win32 bajo el HWND de un AppWindow de WinUI 3.
// =============================================================================
// WinUI 3 no tiene el HwndHost de WPF: la ventana hija se crea a mano, que es
// lo que HwndHost hacía por dentro (ver src/WinCue.App/SwapChainHost.cs, WPF).
// Patrón probado en el hito 0 (tools/winui3-lab/HwndHijo.cs — no se reutiliza
// ese archivo porque es desechable, pero SÍ el patrón: control negativo a
// cuatro bandas con EnumChildWindows demostró que mpv crea su superficie de
// render DENTRO de este HWND cuando se le pasa como wid, y en ningún otro
// caso). El wid se fija ANTES de mpv_initialize — después no tiene efecto.
//
// AIRSPACE: esta ventana se pinta SIEMPRE por encima del XAML de su padre,
// confirmado a ojo por Andrés en el hito 0 (barras de mpv tapando el botón
// donde se solapaban, no el texto que iba por encima en la disposición). No
// es un fallo: solo desaparece con SwapChainPanel (fase 2b, fuera de esta
// tarea — "nada de compositor" es una restricción explícita del encargo).
//
// DOS CONSUMIDORES (ver el porqué de x/y en Crear/Colocar, más abajo):
//   · VideoOutputWindow.xaml.cs — el hijo ocupa TODA la ventana de salida
//     (pantalla completa): nace en (0,0) y solo cambia de TAMAÑO
//     (Redimensionar), nunca de posición.
//   · LiveMonitorControl.cs — el hijo es un HUECO dentro de MainWindow (el
//     previo PGM o PVW, no toda la ventana): nace en el punto donde mide su
//     hueco y tiene que poder MOVERSE si ese hueco se recoloca (Colocar).
// =============================================================================

using System.Runtime.InteropServices;

namespace Secuence.App;

/// <summary>Envoltorio Win32 mínimo de la ventana hija que hospeda a mpv:
/// crear, redimensionar (para seguir el tamaño de la ventana de salida —
/// el laboratorio del hito 0 se quedaba clavado en 640×360 por no tener
/// esto), mostrar/ocultar (blackout) y destruir.</summary>
internal static class HwndHijo
{
    private const int WsChild = unchecked((int)0x40000000);
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int SwHide = 0;
    private const int SwShow = 5;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        int exStyle, string clase, string? titulo, int estilo,
        int x, int y, int ancho, int alto,
        nint padre, nint menu, nint instancia, nint param);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    /// <summary>Crea la ventana hija (clase "static", igual que el hito 0) y
    /// devuelve su HWND. (x, y) por defecto = (0,0) — el caso de
    /// VideoOutputWindow, que llena toda la ventana de salida; LiveMonitorControl
    /// pasa el punto donde mide su hueco dentro de MainWindow. Lanza si
    /// CreateWindowExW falla — un HWND que no se pudo crear no es un caso
    /// silencioso: sin él no hay superficie donde mpv pueda pintar nada.</summary>
    internal static nint Crear(nint padre, int ancho, int alto, int x = 0, int y = 0)
    {
        nint h = CreateWindowExW(
            0, "static", null,
            WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            x, y, Math.Max(1, ancho), Math.Max(1, alto), padre, 0, 0, 0);

        if (h == 0)
            throw new InvalidOperationException(
                $"CreateWindowExW falló: {Marshal.GetLastWin32Error()}");

        return h;
    }

    /// <summary>Sigue el tamaño de la ventana de salida (punto 4 del encargo).
    /// Mantiene la posición en (0,0) — VideoOutputWindow siempre llena toda la
    /// ventana de salida, nunca se mueve dentro de ella. SWP_NOACTIVATE: un
    /// simple cambio de tamaño no debe robarle el foco a la ventana de salida
    /// ni, de rebote, a la de control.</summary>
    internal static void Redimensionar(nint hijo, int ancho, int alto) =>
        SetWindowPos(hijo, 0, 0, 0, Math.Max(1, ancho), Math.Max(1, alto),
            SwpNoZOrder | SwpNoActivate);

    /// <summary>Mueve Y redimensiona en una sola llamada — lo que necesita
    /// LiveMonitorControl cuando su hueco (el previo PGM/PVW dentro de
    /// MainWindow) cambia de sitio o de tamaño, a diferencia de
    /// VideoOutputWindow (que nunca se mueve, solo cambia de tamaño — ver
    /// Redimensionar). Mismos flags: SWP_NOACTIVATE por el mismo motivo.</summary>
    internal static void Colocar(nint hijo, int x, int y, int ancho, int alto) =>
        SetWindowPos(hijo, 0, x, y, Math.Max(1, ancho), Math.Max(1, alto),
            SwpNoZOrder | SwpNoActivate);

    /// <summary>Blackout: ocultar/mostrar el HWND hijo. En modo clásico HWND
    /// (sin compositor — restricción explícita de esta tarea) no hay fundido
    /// posible de verdad: el airspace pinta la superficie de mpv SIEMPRE por
    /// encima de cualquier velo XAML, así que un "fundido" ahí sería un
    /// efecto de mentira (WPF lo resuelve igual: el velo anima por debajo,
    /// invisible, y el corte real llega de golpe al terminar la animación —
    /// ver VideoOutputWindow.xaml.cs de WPF, ApplyHostVisibility/SetBlackout).
    /// Aquí el corte es honesto: instantáneo, sin fingir un fundido que el
    /// airspace no deja ver.</summary>
    internal static void Ocultar(nint hijo) => ShowWindow(hijo, SwHide);
    internal static void Mostrar(nint hijo) => ShowWindow(hijo, SwShow);

    internal static void Destruir(nint hijo) => DestroyWindow(hijo);
}
