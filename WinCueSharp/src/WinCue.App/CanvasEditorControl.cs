// =============================================================================
// CanvasEditorControl.cs — Editor visual del canvas y sus salidas (Settings)
// =============================================================================
// Estilo Watchout/Resolume: se ve el CANVAS global y encima los recortes de
// las 4 salidas como rectángulos arrastrables y redimensionables (asa en la
// esquina inferior derecha), con snap a los bordes/mitades del canvas y a los
// bordes de las otras salidas. Debajo, campos numéricos por salida y el
// tamaño del canvas con presets.
//
// El control NO toca settings: SettingsWindow lee/escribe vía las propiedades
// (CanvasW/H, GetRegion/SetRegion "x,y,w,h", GetEnabled/SetEnabled) usando su
// registro de campos estándar.
// =============================================================================

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinCue.App;

public sealed class CanvasEditorControl : StackPanel
{
    private const int Outputs = 4;
    private const double ViewW = 480, ViewH = 270;
    private const int MinSize = 16;          // px de canvas
    private const double SnapPx = 10;        // umbral de snap en px de PANTALLA

    /// <summary>Colores de identidad de las 4 salidas (compartidos con el
    /// overlay "Identificar" y el menú del botón de salida).</summary>
    internal static readonly Color[] OutColors =
    {
        Color.FromRgb(0x2e, 0xcc, 0x71),   // S1 verde
        Color.FromRgb(0x35, 0x98, 0xdb),   // S2 azul
        Color.FromRgb(0xe8, 0xc0, 0x00),   // S3 ámbar
        Color.FromRgb(0xc9, 0x5e, 0xd6),   // S4 magenta
    };

    private int _cw = 1920, _ch = 1080;
    private readonly int[,] _regions = new int[Outputs, 4];   // x, y, w, h
    private readonly bool[] _enabled = { true, false, false, false };

    private readonly Canvas _surface;
    private readonly Rectangle _canvasRect;
    private readonly Border[] _rects = new Border[Outputs];
    private readonly TextBox _wBox, _hBox;
    private readonly CheckBox[] _chk = new CheckBox[Outputs];
    private readonly TextBox[][] _num = new TextBox[Outputs][];
    private bool _loading;   // evita re-entradas al sincronizar cajas

    // Estado de arrastre
    private int _dragOutput = -1;
    private bool _dragResize;
    private Point _dragStart;
    private (int X, int Y, int W, int H) _dragOrig;

    public CanvasEditorControl()
    {
        for (int s = 0; s < Outputs; s++)
        {
            _regions[s, 0] = 0; _regions[s, 1] = 0;
            _regions[s, 2] = _cw; _regions[s, 3] = _ch;
        }

        // ── Tamaño del canvas + presets ─────────────────────────────────
        var sizeRow = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        sizeRow.Children.Add(new TextBlock
        { Text = "Canvas:", VerticalAlignment = VerticalAlignment.Center, Width = 60 });
        _wBox = NumBox(56);
        _hBox = NumBox(56);
        sizeRow.Children.Add(_wBox);
        sizeRow.Children.Add(new TextBlock
        { Text = "×", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
        sizeRow.Children.Add(_hBox);
        var presets = new ComboBox { Margin = new Thickness(12, 0, 0, 0), MinWidth = 190 };
        foreach (var (w, h, label) in new[]
        {
            (1920, 1080, "1920 × 1080 (FHD)"),
            (3840, 1080, "3840 × 1080 (2×FHD horizontal)"),
            (5760, 1080, "5760 × 1080 (3×FHD horizontal)"),
            (1920, 2160, "1920 × 2160 (2×FHD vertical)"),
            (3840, 2160, "3840 × 2160 (4K)"),
        })
            presets.Items.Add(new ComboBoxItem { Content = label, Tag = (w, h) });
        presets.SelectionChanged += (_, _) =>
        {
            if (_loading || (presets.SelectedItem as ComboBoxItem)?.Tag is not ValueTuple<int, int> t)
                return;
            (CanvasW, CanvasH) = t;
            presets.SelectedIndex = -1;   // el combo actúa como botón
        };
        sizeRow.Children.Add(presets);
        Children.Add(sizeRow);

        // ── Lienzo ──────────────────────────────────────────────────────
        _surface = new Canvas
        {
            Width = ViewW, Height = ViewH,
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _canvasRect = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            StrokeThickness = 1,
            Fill = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
        };
        _surface.Children.Add(_canvasRect);

        for (int s = 0; s < Outputs; s++)
        {
            int idx = s;
            var color = OutColors[s];
            var label = new TextBlock
            {
                Text = $"S{s + 1}",
                Foreground = new SolidColorBrush(color),
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(4, 2, 0, 0),
            };
            var handle = new Rectangle
            {
                Width = 10, Height = 10,
                Fill = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            var grid = new Grid();
            grid.Children.Add(label);
            grid.Children.Add(handle);
            var rect = new Border
            {
                BorderBrush = new SolidColorBrush(color),
                BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(Color.FromArgb(0x30,
                    color.R, color.G, color.B)),
                Child = grid,
                Cursor = Cursors.SizeAll,
            };
            rect.MouseLeftButtonDown += (sender, e) => StartDrag(idx, e, resize: false);
            handle.MouseLeftButtonDown += (sender, e) =>
            {
                StartDrag(idx, e, resize: true);
                e.Handled = true;
            };
            rect.MouseMove += OnDragMove;
            rect.MouseLeftButtonUp += OnDragEnd;
            _rects[s] = rect;
            _surface.Children.Add(rect);
        }
        Children.Add(new Border
        {
            Child = _surface,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3c, 0x3c, 0x3c)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
        });

        // ── Filas numéricas por salida ──────────────────────────────────
        for (int s = 0; s < Outputs; s++)
        {
            int idx = s;
            var row = new StackPanel
            { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            _chk[s] = new CheckBox
            {
                Content = $"Salida {s + 1}",
                Foreground = new SolidColorBrush(OutColors[s]),
                FontWeight = FontWeights.Bold,
                Width = 90,
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = s == 0,
                IsEnabled = s != 0,   // la 1 siempre habilitada
            };
            _chk[s].Checked += (_, _) => { if (!_loading) { _enabled[idx] = true; Redraw(); } };
            _chk[s].Unchecked += (_, _) => { if (!_loading) { _enabled[idx] = false; Redraw(); } };
            row.Children.Add(_chk[s]);

            _num[s] = new TextBox[4];
            string[] tags = { "X", "Y", "An", "Al" };
            for (int f = 0; f < 4; f++)
            {
                int fi = f;
                row.Children.Add(new TextBlock
                {
                    Text = $"{tags[f]}:", VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(f == 0 ? 0 : 8, 0, 2, 0),
                    Foreground = Brushes.Gray,
                });
                var box = NumBox(48);
                box.LostFocus += (_, _) => CommitNumBox(idx, fi);
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitNumBox(idx, fi); };
                _num[s][f] = box;
                row.Children.Add(box);
            }
            Children.Add(row);
        }

        _wBox.LostFocus += (_, _) => CommitCanvasSize();
        _hBox.LostFocus += (_, _) => CommitCanvasSize();
        _wBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitCanvasSize(); };
        _hBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitCanvasSize(); };

        Redraw();
    }

    private static TextBox NumBox(double width) => new()
    {
        Width = width,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ==================================================================
    // API para SettingsWindow (registro de campos)
    // ==================================================================

    public int CanvasW
    {
        get => _cw;
        set { _cw = Math.Clamp(value <= 0 ? 1920 : value, 320, 16384); ClampAll(); Redraw(); }
    }

    public int CanvasH
    {
        get => _ch;
        set { _ch = Math.Clamp(value <= 0 ? 1080 : value, 240, 16384); ClampAll(); Redraw(); }
    }

    public string GetRegion(int s) =>
        $"{_regions[s, 0]},{_regions[s, 1]},{_regions[s, 2]},{_regions[s, 3]}";

    public void SetRegion(int s, string? text)
    {
        var parts = (text ?? "").Split(',');
        if (parts.Length == 4
            && int.TryParse(parts[0].Trim(), out int x)
            && int.TryParse(parts[1].Trim(), out int y)
            && int.TryParse(parts[2].Trim(), out int w)
            && int.TryParse(parts[3].Trim(), out int h)
            && w > 0 && h > 0)
        {
            _regions[s, 0] = x; _regions[s, 1] = y;
            _regions[s, 2] = w; _regions[s, 3] = h;
        }
        else
        {
            _regions[s, 0] = 0; _regions[s, 1] = 0;
            _regions[s, 2] = _cw; _regions[s, 3] = _ch;
        }
        ClampAll();
        Redraw();
    }

    public bool GetEnabled(int s) => s == 0 || _enabled[s];

    public void SetEnabled(int s, bool on)
    {
        _enabled[s] = s == 0 || on;
        _loading = true;
        _chk[s].IsChecked = _enabled[s];
        _loading = false;
        Redraw();
    }

    // ==================================================================
    // Lienzo: escala, redibujado y arrastre
    // ==================================================================

    private double Scale => Math.Min((ViewW - 4) / _cw, (ViewH - 4) / _ch);

    private Point Origin
    {
        get
        {
            double s = Scale;
            return new Point((ViewW - _cw * s) / 2.0, (ViewH - _ch * s) / 2.0);
        }
    }

    private void Redraw()
    {
        double s = Scale;
        var o = Origin;
        Canvas.SetLeft(_canvasRect, o.X);
        Canvas.SetTop(_canvasRect, o.Y);
        _canvasRect.Width = _cw * s;
        _canvasRect.Height = _ch * s;

        for (int i = 0; i < Outputs; i++)
        {
            bool on = GetEnabled(i);
            _rects[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetLeft(_rects[i], o.X + _regions[i, 0] * s);
            Canvas.SetTop(_rects[i], o.Y + _regions[i, 1] * s);
            _rects[i].Width = Math.Max(6, _regions[i, 2] * s);
            _rects[i].Height = Math.Max(6, _regions[i, 3] * s);
            // Z: la salida arrastrada, encima
            Panel.SetZIndex(_rects[i], i == _dragOutput ? 10 : 1 + i);

            _loading = true;
            for (int f = 0; f < 4; f++)
            {
                _num[i][f].Text = _regions[i, f].ToString(CultureInfo.InvariantCulture);
                _num[i][f].IsEnabled = on;
            }
            _loading = false;
        }

        _loading = true;
        _wBox.Text = _cw.ToString(CultureInfo.InvariantCulture);
        _hBox.Text = _ch.ToString(CultureInfo.InvariantCulture);
        _loading = false;
    }

    private void StartDrag(int output, MouseButtonEventArgs e, bool resize)
    {
        _dragOutput = output;
        _dragResize = resize;
        _dragStart = e.GetPosition(_surface);
        _dragOrig = (_regions[output, 0], _regions[output, 1],
                     _regions[output, 2], _regions[output, 3]);
        _rects[output].CaptureMouse();
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_dragOutput < 0 || e.LeftButton != MouseButtonState.Pressed)
            return;
        var p = e.GetPosition(_surface);
        double s = Scale;
        int dx = (int)Math.Round((p.X - _dragStart.X) / s);
        int dy = (int)Math.Round((p.Y - _dragStart.Y) / s);
        int i = _dragOutput;

        if (_dragResize)
        {
            int w = Math.Clamp(_dragOrig.W + dx, MinSize, _cw - _dragOrig.X);
            int h = Math.Clamp(_dragOrig.H + dy, MinSize, _ch - _dragOrig.Y);
            (w, h) = SnapSize(i, _dragOrig.X, _dragOrig.Y, w, h);
            _regions[i, 2] = w;
            _regions[i, 3] = h;
        }
        else
        {
            int x = Math.Clamp(_dragOrig.X + dx, 0, _cw - _dragOrig.W);
            int y = Math.Clamp(_dragOrig.Y + dy, 0, _ch - _dragOrig.H);
            (x, y) = SnapPos(i, x, y, _dragOrig.W, _dragOrig.H);
            _regions[i, 0] = x;
            _regions[i, 1] = y;
        }
        Redraw();
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (_dragOutput >= 0)
            _rects[_dragOutput].ReleaseMouseCapture();
        _dragOutput = -1;
        Redraw();
    }

    /// <summary>Imanes de X e Y para MOVER: bordes y mitades del canvas +
    /// bordes de las otras salidas habilitadas.</summary>
    private (int X, int Y) SnapPos(int self, int x, int y, int w, int h)
    {
        int snap = (int)Math.Max(1, SnapPx / Scale);
        foreach (int t in SnapTargetsX(self))
        {
            if (Math.Abs(x - t) <= snap) { x = t; break; }
            if (Math.Abs(x + w - t) <= snap) { x = t - w; break; }
        }
        foreach (int t in SnapTargetsY(self))
        {
            if (Math.Abs(y - t) <= snap) { y = t; break; }
            if (Math.Abs(y + h - t) <= snap) { y = t - h; break; }
        }
        return (Math.Clamp(x, 0, _cw - w), Math.Clamp(y, 0, _ch - h));
    }

    /// <summary>Imanes del borde derecho/inferior al REDIMENSIONAR.</summary>
    private (int W, int H) SnapSize(int self, int x, int y, int w, int h)
    {
        int snap = (int)Math.Max(1, SnapPx / Scale);
        foreach (int t in SnapTargetsX(self))
            if (Math.Abs(x + w - t) <= snap && t - x >= MinSize) { w = t - x; break; }
        foreach (int t in SnapTargetsY(self))
            if (Math.Abs(y + h - t) <= snap && t - y >= MinSize) { h = t - y; break; }
        return (Math.Min(w, _cw - x), Math.Min(h, _ch - y));
    }

    private IEnumerable<int> SnapTargetsX(int self)
    {
        yield return 0;
        yield return _cw / 2;
        yield return _cw;
        for (int i = 0; i < Outputs; i++)
        {
            if (i == self || !GetEnabled(i)) continue;
            yield return _regions[i, 0];
            yield return _regions[i, 0] + _regions[i, 2];
        }
    }

    private IEnumerable<int> SnapTargetsY(int self)
    {
        yield return 0;
        yield return _ch / 2;
        yield return _ch;
        for (int i = 0; i < Outputs; i++)
        {
            if (i == self || !GetEnabled(i)) continue;
            yield return _regions[i, 1];
            yield return _regions[i, 1] + _regions[i, 3];
        }
    }

    // ==================================================================
    // Campos numéricos
    // ==================================================================

    private void CommitNumBox(int output, int field)
    {
        if (_loading)
            return;
        if (int.TryParse(_num[output][field].Text.Trim(), out int v))
            _regions[output, field] = v;
        ClampAll();
        Redraw();
    }

    private void CommitCanvasSize()
    {
        if (_loading)
            return;
        if (int.TryParse(_wBox.Text.Trim(), out int w)) CanvasW = w;
        if (int.TryParse(_hBox.Text.Trim(), out int h)) CanvasH = h;
    }

    /// <summary>Mantiene todas las regiones dentro del canvas.</summary>
    private void ClampAll()
    {
        for (int i = 0; i < Outputs; i++)
        {
            _regions[i, 0] = Math.Clamp(_regions[i, 0], 0, Math.Max(0, _cw - MinSize));
            _regions[i, 1] = Math.Clamp(_regions[i, 1], 0, Math.Max(0, _ch - MinSize));
            _regions[i, 2] = Math.Clamp(_regions[i, 2], MinSize, _cw - _regions[i, 0]);
            _regions[i, 3] = Math.Clamp(_regions[i, 3], MinSize, _ch - _regions[i, 1]);
        }
    }
}
