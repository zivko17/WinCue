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
//
// El CÁLCULO (escala, encaje en la vista, imanes, mover/redimensionar) vive
// en WinCue.Presentation.CanvasEditorLayout, compartido con Secuence.App:
// este fichero solo pinta con WPF y traduce eventos de ratón a ese cálculo.
// =============================================================================

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WinCue.Presentation;

namespace WinCue.App;

public sealed class CanvasEditorControl : StackPanel
{
    private const int Outputs = 4;
    private const double ViewW = 480, ViewH = 270;

    private int _cw = 1920, _ch = 1080;
    private readonly CanvasRegion[] _regions = new CanvasRegion[Outputs];
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
    private CanvasRegion _dragOrig;

    public CanvasEditorControl()
    {
        for (int s = 0; s < Outputs; s++)
            _regions[s] = new CanvasRegion(0, 0, _cw, _ch);

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
        foreach (var (w, h, label) in CanvasEditorLayout.SizePresets)
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
            Background = new SolidColorBrush(Theme.GeometryBg),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _canvasRect = new Rectangle
        {
            Stroke = new SolidColorBrush(Theme.GeometryGrid),
            StrokeThickness = 1,
            Fill = new SolidColorBrush(Theme.WindowBg),
        };
        _surface.Children.Add(_canvasRect);

        for (int s = 0; s < Outputs; s++)
        {
            int idx = s;
            var color = Theme.Hex(CanvasEditorLayout.OutputColors[s]);
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
                Background = new SolidColorBrush(Theme.Alpha(color, 0x30)),
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
            // Gris #3c3c3c: sin token de BORDE con ese valor, se toma el de
            // fondo de botón (mismo gris exacto) hasta que la paleta tenga uno
            BorderBrush = new SolidColorBrush(Theme.ButtonBg),
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
                Foreground = new SolidColorBrush(Theme.Hex(CanvasEditorLayout.OutputColors[s])),
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
                    Foreground = Theme.TextNoteBrush,
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

    public string GetRegion(int s) => CanvasEditorLayout.Format(_regions[s]);

    public void SetRegion(int s, string? text)
    {
        var v = WinCue.Controller.CanvasLayout.ParseRegion(text ?? "", s, _cw, _ch);
        _regions[s] = CanvasEditorLayout.Clamp(new CanvasRegion(v.X, v.Y, v.W, v.H), _cw, _ch);
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
    // Lienzo: redibujado y arrastre (el cálculo vive en CanvasEditorLayout)
    // ==================================================================

    private void Redraw()
    {
        double s = CanvasEditorLayout.Scale(_cw, _ch, ViewW, ViewH);
        var (ox, oy) = CanvasEditorLayout.Origin(_cw, _ch, ViewW, ViewH);
        Canvas.SetLeft(_canvasRect, ox);
        Canvas.SetTop(_canvasRect, oy);
        _canvasRect.Width = _cw * s;
        _canvasRect.Height = _ch * s;

        for (int i = 0; i < Outputs; i++)
        {
            bool on = GetEnabled(i);
            _rects[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            var view = CanvasEditorLayout.ToView(_regions[i], _cw, _ch, ViewW, ViewH);
            Canvas.SetLeft(_rects[i], view.X);
            Canvas.SetTop(_rects[i], view.Y);
            _rects[i].Width = Math.Max(6, view.W);
            _rects[i].Height = Math.Max(6, view.H);
            // Z: la salida arrastrada, encima
            Panel.SetZIndex(_rects[i], i == _dragOutput ? 10 : 1 + i);

            _loading = true;
            var r = _regions[i];
            int[] vals = { r.X, r.Y, r.W, r.H };
            for (int f = 0; f < 4; f++)
            {
                _num[i][f].Text = vals[f].ToString(CultureInfo.InvariantCulture);
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
        _dragOrig = _regions[output];
        _rects[output].CaptureMouse();
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_dragOutput < 0 || e.LeftButton != MouseButtonState.Pressed)
            return;
        var p = e.GetPosition(_surface);
        double s = CanvasEditorLayout.Scale(_cw, _ch, ViewW, ViewH);
        int dx = CanvasEditorLayout.ToCanvasDelta(p.X - _dragStart.X, s);
        int dy = CanvasEditorLayout.ToCanvasDelta(p.Y - _dragStart.Y, s);
        int i = _dragOutput;

        _regions[i] = _dragResize
            ? CanvasEditorLayout.Resize(_dragOrig, dx, dy, _regions, _enabled, i, _cw, _ch, s)
            : CanvasEditorLayout.Move(_dragOrig, dx, dy, _regions, _enabled, i, _cw, _ch, s);

        Redraw();
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (_dragOutput >= 0)
            _rects[_dragOutput].ReleaseMouseCapture();
        _dragOutput = -1;
        Redraw();
    }

    // ==================================================================
    // Campos numéricos
    // ==================================================================

    private void CommitNumBox(int output, int field)
    {
        if (_loading)
            return;
        if (int.TryParse(_num[output][field].Text.Trim(), out int v))
        {
            var r = _regions[output];
            _regions[output] = field switch
            {
                0 => r with { X = v },
                1 => r with { Y = v },
                2 => r with { W = v },
                _ => r with { H = v },
            };
        }
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
            _regions[i] = CanvasEditorLayout.Clamp(_regions[i], _cw, _ch);
    }
}
