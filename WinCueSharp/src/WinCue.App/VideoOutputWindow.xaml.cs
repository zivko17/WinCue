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
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace WinCue.App;

/// <summary>Patrón de test de una salida (alineado de proyectores).</summary>
public enum TestPattern { None, Grid, Bars, Border }

// CA1001: ventana WPF de vida completa de la app (se OCULTA en vez de cerrarse
// porque mpv puede seguir apuntando a su HWND); el WindowsFormsHost lo
// desmonta WPF en CloseForShutdown() al salir.
#pragma warning disable CA1001
public partial class VideoOutputWindow : Window
{
#pragma warning restore CA1001
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    private bool _shutdown;

    // MODO CLÁSICO: 8 superficies HWND apiladas (una por pista): el índice 0
    // es la de ARRIBA. mpv renderiza sobre el HWND del panel de su pista;
    // las vacías se ocultan (un panel WinForms pinta su fondo negro).
    private readonly WF.Panel[] _layers = new WF.Panel[8];
    private readonly System.Windows.Forms.Integration.WindowsFormsHost _hwndHost;

    // MODO COMPOSITOR GPU: una superficie POR CUE, apiladas por pista (z) —
    // dos cues pueden convivir en la misma pista durante un crossfade
    private readonly Dictionary<int, CompositorLayerControl> _gpuByCue = new();
    private int _gpuSeq;   // desempate de z dentro de la misma pista
    private readonly Grid _gpuHost;

    // Overlays de operación: patrón de test, identificación y blackout.
    // OJO airspace de WPF: en modo clásico el host WinForms tapa cualquier
    // overlay → el patrón OCULTA los hosts mientras está activo, y el
    // blackout oculta el host clásico al completar el fundido.
    private readonly Grid _patternHost;
    private readonly Border _identifyOverlay;
    private readonly TextBlock _identifyText;
    private readonly Border _blackoutVeil;
    private readonly DispatcherTimer _identifyTimer;
    private TestPattern _pattern = TestPattern.None;
    private bool _blackout;
    private bool _hwndMode;   // último modo pedido por el motor

    // Imagen de respaldo del PANIC (cartela de cortesía): overlay WPF con
    // fundido de entrada. En modo clásico el host WinForms taparía el overlay
    // (airspace) → mientras la cartela está activa se oculta ese host, igual
    // que hacen los patrones de test.
    private readonly Grid _slateHost;
    private readonly Image _slateImage;
    private bool _slate;

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

        // Compositor: host vacío — los controles se crean POR CUE al arrancar
        // cada vídeo y se retiran solos cuando el motor suelta la superficie
        _gpuHost = new Grid { Background = System.Windows.Media.Brushes.Black };

        // Overlays (de abajo a arriba): patrón de test → identificar → blackout
        _patternHost = new Grid { Visibility = Visibility.Collapsed };
        _identifyText = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 120,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
        };
        _identifyOverlay = new Border
        {
            Visibility = Visibility.Collapsed,
            Background = new SolidColorBrush(Color.FromArgb(0xa0, 0, 0, 0)),
            BorderThickness = new Thickness(14),
            Child = _identifyText,
        };
        _identifyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _identifyTimer.Tick += (_, _) =>
        {
            _identifyTimer.Stop();
            _identifyOverlay.Visibility = Visibility.Collapsed;
        };
        _blackoutVeil = new Border
        {
            Background = Brushes.Black,
            Opacity = 0.0,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        _slateImage = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _slateHost = new Grid
        {
            Background = Brushes.Black,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _slateHost.Children.Add(_slateImage);

        var root = new Grid { Background = System.Windows.Media.Brushes.Black };
        root.Children.Add(_hwndHost);
        root.Children.Add(_gpuHost);
        root.Children.Add(_slateHost);
        root.Children.Add(_patternHost);
        root.Children.Add(_identifyOverlay);
        root.Children.Add(_blackoutVeil);
        _gpuHost.Visibility = Visibility.Collapsed;
        Content = root;
    }

    // ==================================================================
    // Imagen de respaldo del PANIC
    // ==================================================================

    /// <summary>Muestra la cartela de respaldo con fundido de entrada. En modo
    /// compositor es un crossfade real sobre el vídeo que se apaga; en clásico
    /// el host nativo se oculta (airspace) y la cartela entra sobre negro.</summary>
    public void ShowBackupSlate(string imagePath, double fadeSeconds)
    {
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(imagePath, UriKind.Absolute);
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            _slateImage.Source = bmp;
        }
        catch (Exception)
        {
            return;   // imagen ilegible: mejor negro que romper el PANIC
        }
        _slate = true;
        _slateHost.Opacity = 0.0;
        _slateHost.Visibility = Visibility.Visible;
        _slateHost.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0,
                TimeSpan.FromSeconds(Math.Max(0.05, fadeSeconds))));
        ApplyHostVisibility();
    }

    /// <summary>Quita la cartela (al siguiente GO de vídeo o a mano).</summary>
    public void HideBackupSlate()
    {
        if (!_slate)
            return;
        _slate = false;
        _slateHost.BeginAnimation(OpacityProperty, null);
        _slateHost.Visibility = Visibility.Collapsed;
        _slateImage.Source = null;
        ApplyHostVisibility();
    }

    /// <summary>La ventana se adapta al modo que USA el motor: pedir un HWND
    /// activa el modo clásico; pedir una superficie GPU, el compositor.</summary>
    private void ShowHwndMode(bool hwndMode)
    {
        _hwndMode = hwndMode;
        ApplyHostVisibility();
    }

    /// <summary>Visibilidad efectiva de los hosts de vídeo: el modo del motor
    /// combinado con el patrón de test y el blackout (airspace del clásico).</summary>
    private void ApplyHostVisibility()
    {
        bool videoVisible = _pattern == TestPattern.None && !_blackout;
        // La cartela de respaldo tapa el host CLÁSICO (airspace WinForms);
        // sobre el compositor (WPF) convive y el fundido es un crossfade real.
        _hwndHost.Visibility = _hwndMode && videoVisible && !_slate
            ? Visibility.Visible : Visibility.Collapsed;
        _gpuHost.Visibility = !_hwndMode && (_pattern == TestPattern.None)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==================================================================
    // Overlays: identificar, patrón de test, blackout
    // ==================================================================

    /// <summary>Muestra "SALIDA N" en grande con el color de la salida
    /// durante unos segundos (para saber qué monitor es cuál).</summary>
    public void ShowIdentify(int screenIndex, Color color)
    {
        if (!IsVisible)
            Show();
        _identifyText.Text = $"SALIDA {screenIndex + 1}";
        _identifyText.Foreground = new SolidColorBrush(color);
        _identifyOverlay.BorderBrush = new SolidColorBrush(color);
        _identifyOverlay.Visibility = Visibility.Visible;
        _identifyTimer.Stop();
        _identifyTimer.Start();
    }

    public TestPattern Pattern => _pattern;

    /// <summary>Activa un patrón de test (o lo quita con None). El patrón
    /// SUSTITUYE al vídeo mientras está activo (evita el airspace WPF).</summary>
    public void SetTestPattern(TestPattern pattern)
    {
        _pattern = pattern;
        _patternHost.Children.Clear();
        _patternHost.Visibility = pattern == TestPattern.None
            ? Visibility.Collapsed : Visibility.Visible;
        if (pattern != TestPattern.None)
        {
            if (!IsVisible)
                Show();
            _patternHost.Children.Add(BuildPattern(pattern));
        }
        ApplyHostVisibility();
    }

    private static UIElement BuildPattern(TestPattern pattern)
    {
        switch (pattern)
        {
            case TestPattern.Bars:
            {
                // Barras EBU de 8 columnas
                var bars = new System.Windows.Controls.Primitives.UniformGrid
                { Rows = 1, Columns = 8 };
                foreach (var c in new[]
                {
                    Colors.White, Colors.Yellow, Colors.Cyan, Colors.Lime,
                    Colors.Magenta, Colors.Red, Colors.Blue, Colors.Black,
                })
                    bars.Children.Add(new Rectangle { Fill = new SolidColorBrush(c) });
                return bars;
            }

            case TestPattern.Border:
            {
                // Borde perimetral + diagonales + círculo central (geometría)
                var grid = new Grid { Background = Brushes.Black };
                grid.Children.Add(new Border
                {
                    BorderBrush = Brushes.Red,
                    BorderThickness = new Thickness(6),
                });
                foreach (bool second in new[] { false, true })
                    grid.Children.Add(new Line
                    {
                        X1 = 0, Y1 = second ? 1 : 0, X2 = 1, Y2 = second ? 0 : 1,
                        Stretch = Stretch.Fill,
                        Stroke = Brushes.White, StrokeThickness = 1,
                    });
                grid.Children.Add(new Ellipse
                {
                    Stroke = Brushes.White, StrokeThickness = 2,
                    Width = 300, Height = 300,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                return grid;
            }

            default:   // Grid
            {
                // Rejilla de 100 px reales + cruz central
                var lines = new GeometryGroup();
                lines.Children.Add(new LineGeometry(new Point(0, 0), new Point(100, 0)));
                lines.Children.Add(new LineGeometry(new Point(0, 0), new Point(0, 100)));
                var brush = new DrawingBrush(new GeometryDrawing(
                    null, new Pen(Brushes.Gray, 1), lines))
                {
                    TileMode = TileMode.Tile,
                    Viewport = new Rect(0, 0, 100, 100),
                    ViewportUnits = BrushMappingMode.Absolute,
                    Stretch = Stretch.None,
                };
                var grid = new Grid { Background = Brushes.Black };
                grid.Children.Add(new Rectangle { Fill = brush });
                foreach (bool vertical in new[] { false, true })
                    grid.Children.Add(new Rectangle
                    {
                        Fill = Brushes.White,
                        Width = vertical ? 2 : double.NaN,
                        Height = vertical ? double.NaN : 2,
                        HorizontalAlignment = vertical
                            ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
                        VerticalAlignment = vertical
                            ? VerticalAlignment.Stretch : VerticalAlignment.Center,
                    });
                return grid;
            }
        }
    }

    public bool Blackout => _blackout;

    /// <summary>Blackout de operador: funde un velo negro por ENCIMA de todo
    /// sin tocar los cues. En modo clásico el velo no puede tapar al HWND
    /// (airspace) → al completar el fundido se oculta el host.</summary>
    public void SetBlackout(bool on, double fadeSeconds)
    {
        _blackout = on;
        _blackoutVeil.Visibility = Visibility.Visible;
        if (!on)
            ApplyHostVisibility();   // re-mostrar el vídeo ANTES de fundir a la vista

        var anim = new System.Windows.Media.Animation.DoubleAnimation(
            on ? 1.0 : 0.0, TimeSpan.FromSeconds(Math.Max(0.0, fadeSeconds)))
        {
            FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd,
        };
        anim.Completed += (_, _) =>
        {
            if (_blackout == on)
                ApplyHostVisibility();
            if (!on && !_blackout)
                _blackoutVeil.Visibility = Visibility.Collapsed;
        };
        _blackoutVeil.BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>Superficie PERSISTENTE del compositor para un SLOT del pool
    /// (auditoría 05): el control y su binding D3DImage viven toda la sesión.
    /// Los cues pasan por él vía AssignCue/ReleaseCue — al asignar sube el z
    /// de su pista (dentro de la misma pista, el más reciente encima:
    /// crossfade A→B) y se mapea para la opacidad por cue.</summary>
    public CompositorLayerControl GetCompositorSurface(int layer, int slotSeq)
    {
        if (!IsVisible)
            Show();
        ShowHwndMode(false);

        var ctl = new CompositorLayerControl();
        ctl.CueAssigned = cueId =>
        {
            _gpuByCue[cueId] = ctl;
            int z = (7 - Math.Clamp(layer, 0, 7)) * 100_000 + (++_gpuSeq & 0xFFFF);
            Panel.SetZIndex(ctl, z);
        };
        ctl.CueReleased = cueId =>
        {
            if (_gpuByCue.TryGetValue(cueId, out var cur) && ReferenceEquals(cur, ctl))
                _gpuByCue.Remove(cueId);
        };
        ctl.Cleared = () => _gpuHost.Children.Remove(ctl);   // pipeline muerto
        _gpuHost.Children.Add(ctl);
        return ctl;
    }

    /// <summary>Opacidad (0..1) del vídeo de un cue en ESTA salida.
    /// fadeSeconds &gt; 0 = transición animada (fundidos y crossfades).
    /// Sin superficie de ese cue aquí, no-op.</summary>
    public void SetCueOpacity(int cueId, double opacity, double fadeSeconds = 0.0)
    {
        if (!_gpuByCue.TryGetValue(cueId, out var target))
            return;
        double value = Math.Clamp(opacity, 0.0, 1.0);
        if (fadeSeconds > 0.0)
        {
            target.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(
                    value, TimeSpan.FromSeconds(fadeSeconds))
                {
                    FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd,
                });
        }
        else
        {
            target.BeginAnimation(OpacityProperty, null);   // cortar fade previo
            target.Opacity = value;
        }
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
    /// Screen.AllScreens; fuera de rango → principal). savedGeometry
    /// ("x,y,w,h" en px físicos) restaura la posición de la sesión anterior
    /// en modo ventana; se ignora si no cae dentro de ese monitor.</summary>
    public void PlaceOnScreen(int screenIndex, bool fullscreen, string? savedGeometry = null)
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
            Cursor = System.Windows.Input.Cursors.None;   // sin flecha en el proyector
            SetWindowPos(hwnd, 0, b.X, b.Y, b.Width, b.Height,
                SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            Cursor = null;

            // Geometría recordada de la sesión anterior (si sigue en el monitor)
            if (ParseGeometry(savedGeometry) is var (gx, gy, gw, gh)
                && gw >= 200 && gh >= 120
                && gx >= b.X - 40 && gy >= b.Y - 40
                && gx < b.X + b.Width && gy < b.Y + b.Height)
            {
                SetWindowPos(hwnd, 0, gx, gy, gw, gh,
                    SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
                return;
            }

            // Ventana de 960×540 px físicos, algo separada del borde del monitor
            int w = Math.Min(960, b.Width - 120);
            int h = Math.Min(540, b.Height - 120);
            SetWindowPos(hwnd, 0, b.X + 60, b.Y + 60, w, h,
                SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }
    }

    private static (int X, int Y, int W, int H)? ParseGeometry(string? text)
    {
        var parts = (text ?? "").Split(',');
        return parts.Length == 4
            && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y)
            && int.TryParse(parts[2], out int w) && int.TryParse(parts[3], out int h)
            ? (x, y, w, h) : null;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>Geometría actual "x,y,w,h" en px FÍSICOS (para recordarla),
    /// o null en fullscreen / sin ventana.</summary>
    public string? CurrentGeometry()
    {
        if (WindowStyle == WindowStyle.None)
            return null;   // fullscreen: no recordar
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || !GetWindowRect(hwnd, out var r))
            return null;
        return $"{r.Left},{r.Top},{r.Right - r.Left},{r.Bottom - r.Top}";
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
