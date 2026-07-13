// =============================================================================
// VideoOutputWindow.xaml.cs — Ventana de salida de vídeo (destino de mpv)
// =============================================================================
// mpv recibe el HWND de esta ventana (wid) y crea su ventana hija cubriéndola.
// Cerrarla no la destruye: se oculta (el show puede volver a usarla).
//
// COLOCACIÓN EN PANTALLA
//   PlaceOnScreen posiciona la ventana en el monitor configurado
//   (Settings → Vídeo → Pantalla de salida) usando SetWindowPos con PÍXELES
//   FÍSICOS del monitor: así se evita la conversión DIP↔px de WPF, que se
//   rompe con monitores de distinto DPI. Con fullscreen activo, la ventana
//   pasa a sin-bordes cubriendo el monitor completo (blackout real).
// =============================================================================

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinCue.App;

public partial class VideoOutputWindow : Window
{
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    private bool _shutdown;

    public VideoOutputWindow()
    {
        InitializeComponent();
    }

    /// <summary>HWND nativo de la ventana (0 si aún no se ha creado).</summary>
    public nint Handle => new WindowInteropHelper(this).Handle;

    /// <summary>Garantiza que la ventana existe y es visible, y devuelve su HWND.</summary>
    public nint EnsureVisibleHandle()
    {
        if (!IsVisible)
            Show();
        return new WindowInteropHelper(this).EnsureHandle();
    }

    /// <summary>Coloca la ventana en el monitor indicado (índice de
    /// Screen.AllScreens; fuera de rango → principal).</summary>
    public void PlaceOnScreen(int screenIndex, bool fullscreen)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screenIndex < 0 || screenIndex >= screens.Length)
            screenIndex = 0;
        var b = screens[screenIndex].Bounds;   // píxeles físicos

        if (!IsVisible)
            Show();
        nint hwnd = new WindowInteropHelper(this).EnsureHandle();

        if (fullscreen)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            SetWindowPos(hwnd, 0, b.X, b.Y, b.Width, b.Height,
                SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            // Ventana de 960×540 px físicos, algo separada del borde del monitor
            int w = Math.Min(960, b.Width - 120);
            int h = Math.Min(540, b.Height - 120);
            SetWindowPos(hwnd, 0, b.X + 60, b.Y + 60, w, h,
                SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_shutdown)
            return;
        // Ocultar en vez de destruir: mpv puede seguir apuntando aquí
        e.Cancel = true;
        Hide();
    }

    /// <summary>Cierre real al salir de la aplicación.</summary>
    public void CloseForShutdown()
    {
        _shutdown = true;
        Close();
    }
}
