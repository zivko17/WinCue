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
using System.Windows.Controls;
using System.Windows.Interop;
using WF = System.Windows.Forms;

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

    // MODO CLÁSICO: 5 superficies HWND apiladas (una por pista): el índice 0
    // es la de ARRIBA. mpv renderiza sobre el HWND del panel de su pista;
    // las vacías se ocultan (un panel WinForms pinta su fondo negro).
    private readonly WF.Panel[] _layers = new WF.Panel[5];
    private readonly System.Windows.Forms.Integration.WindowsFormsHost _hwndHost;

    // MODO COMPOSITOR GPU: 5 D3DImage apiladas con opacidad real por capa
    private readonly CompositorLayerControl[] _gpuLayers = new CompositorLayerControl[5];
    private readonly Grid _gpuHost;

    public VideoOutputWindow()
    {
        InitializeComponent();

        var container = new WF.Panel
        {
            BackColor = System.Drawing.Color.Black,
            Dock = WF.DockStyle.Fill,
        };
        for (int i = 0; i < _layers.Length; i++)
        {
            _layers[i] = new WF.Panel
            {
                BackColor = System.Drawing.Color.Black,
                Dock = WF.DockStyle.Fill,
                Visible = false,
            };
            container.Controls.Add(_layers[i]);
            // En WinForms el índice 0 del z-order es el FRENTE: pista 0 arriba
            container.Controls.SetChildIndex(_layers[i], i);
        }
        _hwndHost = new System.Windows.Forms.Integration.WindowsFormsHost
        {
            Child = container,
        };

        // Compositor: en WPF el ÚLTIMO hijo queda encima → añadir 4..0
        _gpuHost = new Grid { Background = System.Windows.Media.Brushes.Black };
        for (int i = _gpuLayers.Length - 1; i >= 0; i--)
        {
            _gpuLayers[i] = new CompositorLayerControl();
            _gpuHost.Children.Add(_gpuLayers[i]);
        }

        var root = new Grid { Background = System.Windows.Media.Brushes.Black };
        root.Children.Add(_hwndHost);
        root.Children.Add(_gpuHost);
        _gpuHost.Visibility = Visibility.Collapsed;
        Content = root;
    }

    /// <summary>La ventana se adapta al modo que USA el motor: pedir un HWND
    /// activa el modo clásico; pedir una superficie GPU, el compositor.</summary>
    private void ShowHwndMode(bool hwndMode)
    {
        _hwndHost.Visibility = hwndMode ? Visibility.Visible : Visibility.Collapsed;
        _gpuHost.Visibility = hwndMode ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Superficie del compositor GPU para la pista dada.</summary>
    public CompositorLayerControl GetCompositorSurface(int layer)
    {
        if (!IsVisible)
            Show();
        ShowHwndMode(false);
        return _gpuLayers[Math.Clamp(layer, 0, _gpuLayers.Length - 1)];
    }

    /// <summary>Opacidad (0..1) de una pista del compositor.</summary>
    public void SetLayerOpacity(int layer, double opacity)
    {
        if (layer >= 0 && layer < _gpuLayers.Length)
            _gpuLayers[layer].Opacity = Math.Clamp(opacity, 0.0, 1.0);
    }

    /// <summary>HWND de la superficie de la pista dada (la hace visible).</summary>
    public nint EnsureLayerHandle(int layer)
    {
        if (!IsVisible)
            Show();
        ShowHwndMode(true);
        _ = new WindowInteropHelper(this).EnsureHandle();
        var panel = _layers[Math.Clamp(layer, 0, _layers.Length - 1)];
        panel.Visible = true;
        return panel.Handle;   // fuerza la creación del HWND si hace falta
    }

    /// <summary>Oculta las superficies sin contenido (para que su fondo negro
    /// no tape a las pistas de abajo).</summary>
    public void SetActiveLayers(IReadOnlyCollection<int> activeLayers)
    {
        for (int i = 0; i < _layers.Length; i++)
            _layers[i].Visible = activeLayers.Contains(i);
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
