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
// AIRSPACE — LO QUE ESTE COMENTARIO DECÍA ANTES ERA FALSO, Y COSTÓ UNA TANDA
// ENTERA. Decía que esta ventana «se pinta SIEMPRE por encima del XAML de su
// padre, confirmado a ojo en el hito 0». Eso se comprobó en
// tools/winui3-lab —un laboratorio con una ventana casi vacía— y NUNCA en la
// aplicación final. En MainWindow pasa justo lo contrario.
//
// LO QUE PASA DE VERDAD, MEDIDO (auditoria-monitores.md):
// el `Microsoft.UI.Content.DesktopChildSiteBridge` —la ventana donde WinUI 3
// compone TODO el XAML de su padre— puede quedar POR ENCIMA de este hijo y
// taparlo del todo. En MainWindow quedaba encima y los huecos del PGM y del
// PVW salían NEGROS con mpv pintando perfectamente dentro; en
// VideoOutputWindow quedaba debajo y por eso la salida a sala sí se veía.
// El código que los crea es el mismo: lo que cambia es CUÁNDO nace el hijo
// respecto al ciclo de vida del puente XAML. O sea que **el orden Z de estos
// hijos es accidental**, y nada lo estaba garantizando.
//
// Y hay un agravante medido: `CreateWindowExW` NO deja arriba al hijo nuevo.
// Un hijo creado en caliente, con el puente ya existiendo, nace AL FONDO. Por
// eso Crear, Mostrar y Colocar fuerzan HWND_TOP explícitamente — no es un
// cinturón, es lo que hace que se vea. (La subida AGUANTA los
// redimensionados, medido, así que no hace falta re-asertarla en cada tic; por
// eso Redimensionar puede seguir con SWP_NOZORDER, que PRESERVA el orden y no
// degrada a nadie.)
//
// El airspace de verdad (que ningún velo ni diálogo XAML se pinte ENCIMA de
// este hijo) sigue existiendo, y ahora se nota más: ver Ocultar/Mostrar, y la
// mitigación del ContentDialog en MainWindow.MostrarDialogoAsync. Solo
// desaparece del todo con SwapChainPanel (fase 2b).
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
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const int SwHide = 0;
    private const int SwShow = 5;

    /// <summary>Arriba del todo entre los hermanos. Es lo que hay que forzar
    /// para que el DesktopChildSiteBridge de WinUI 3 no tape a este hijo —
    /// ver la cabecera del archivo.</summary>
    private static readonly nint HwndTop = 0;

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

        // OBLIGATORIO, no cosmético: está MEDIDO que un hijo recién creado bajo
        // una ventana WinUI 3 nace AL FONDO, por debajo del
        // DesktopChildSiteBridge, y entonces es invisible aunque mpv esté
        // pintando dentro. Sin esta línea, el hueco del PGM y el del PVW salen
        // negros (luma 0). Ver la cabecera del archivo.
        Subir(h);
        return h;
    }

    /// <summary>Sube el hijo por encima de sus hermanos —entre ellos el
    /// DesktopChildSiteBridge de WinUI 3— sin moverlo, sin cambiarle el tamaño
    /// y sin robar el foco.</summary>
    private static void Subir(nint hijo) =>
        SetWindowPos(hijo, HwndTop, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate);

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
    /// Redimensionar). SWP_NOACTIVATE por el mismo motivo.
    ///
    /// A HWND_TOP y NO SWP_NOZORDER (que es lo que había): el hueco se recoloca
    /// al arrastrar el separador y al cambiar de DPI, y ésos son justo los
    /// momentos en los que WinUI 3 rehace su puente. Cinturón sobre el tirante
    /// de Crear/Mostrar — ver la cabecera del archivo.</summary>
    internal static void Colocar(nint hijo, int x, int y, int ancho, int alto) =>
        SetWindowPos(hijo, HwndTop, x, y, Math.Max(1, ancho), Math.Max(1, alto),
            SwpNoActivate);

    /// <summary>Blackout: ocultar/mostrar el HWND hijo. En modo clásico HWND
    /// (sin compositor — restricción explícita de esta tarea) no hay fundido
    /// posible de verdad: mientras este hijo esté por encima del puente XAML
    /// —que es lo que Crear/Mostrar/Colocar fuerzan ahora a propósito—, la
    /// superficie de mpv se pinta por encima de cualquier velo XAML, así que un
    /// "fundido" ahí sería un efecto de mentira (WPF lo resuelve igual: el velo
    /// anima por debajo, invisible, y el corte real llega de golpe al terminar
    /// la animación — ver VideoOutputWindow.xaml.cs de WPF,
    /// ApplyHostVisibility/SetBlackout). Aquí el corte es honesto:
    /// instantáneo, sin fingir un fundido que el airspace no deja ver.
    ///
    /// Y por ese mismo airspace, ocultar es el ÚNICO camino que hay para que
    /// algo de XAML se vea encima del hueco: es lo que usan Clear, el blackout
    /// y la mitigación del ContentDialog de Ajustes.</summary>
    internal static void Ocultar(nint hijo) => ShowWindow(hijo, SwHide);

    /// <summary>Muestra el hijo Y LO VUELVE A SUBIR. Las dos cosas: ShowWindow
    /// NO toca el orden Z, y éste es el camino por el que el monitor sale del
    /// blackout y por el que se conmuta entre el hijo de mpv y el del espejo —
    /// un hijo que llevaba oculto desde antes de que WinUI rehiciera su puente
    /// reaparecería debajo, o sea negro. Medido; ver la cabecera.</summary>
    internal static void Mostrar(nint hijo)
    {
        ShowWindow(hijo, SwShow);
        Subir(hijo);
    }

    internal static void Destruir(nint hijo) => DestroyWindow(hijo);
}
