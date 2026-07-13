// =============================================================================
// VideoOutputWindow.xaml.cs — Ventana de salida de vídeo (destino de mpv)
// =============================================================================
// mpv recibe el HWND de esta ventana (wid) y crea su ventana hija cubriéndola.
// Cerrarla no la destruye: se oculta (el show puede volver a usarla).
// =============================================================================

using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;

namespace WinCue.App;

public partial class VideoOutputWindow : Window
{
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

    private bool _shutdown;

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
