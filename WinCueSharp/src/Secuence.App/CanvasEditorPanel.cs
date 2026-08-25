// =============================================================================
// CanvasEditorPanel.cs — Editor visual del canvas y sus salidas (Ajustes → Vídeo)
// =============================================================================
// Equivalente de src/WinCue.App/CanvasEditorControl.cs (WPF) para esta interfaz,
// NO una copia (regla del proyecto: lo compartido sube a WinCue.Presentation/
// Controller, no se copia entre productos). El CÁLCULO (escala, encaje en la
// vista, imanes, mover/redimensionar) vive en WinCue.Presentation.
// CanvasEditorLayout, compartido con WinCue.App: este fichero solo pinta con
// XAML plano de WinUI 3 (sin Win2D — es un formulario, no hace falta redibujar
// a 60 fps) y traduce eventos de puntero a ese cálculo.
//
// SettingsPanel no toca este control por dentro: lee/escribe vía las
// propiedades (CanvasW/H, GetRegion/SetRegion "x,y,w,h", GetEnabled/SetEnabled)
// con su registro de campos estándar — MISMAS firmas que
// WinCue.App.CanvasEditorControl, así el registro de campos se escribe igual
// en los dos productos (ver SettingsPanel.BuildVideo).
// =============================================================================

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using WinCue.Core;
using WinCue.Presentation;

namespace Secuence.App;

public sealed class CanvasEditorPanel : StackPanel
{
    private const int Outputs = 4;
    private const double AnchoVista = 480, AltoVista = 270;

    private int _cw = 1920, _ch = 1080;
    private readonly CanvasRegion[] _regiones = new CanvasRegion[Outputs];
    private readonly bool[] _habilitadas = { true, false, false, false };

    private readonly Canvas _lienzo;
    private readonly Rectangle _canvasRect;
    private readonly Border[] _rects = new Border[Outputs];
    private readonly NumberBox _wBox, _hBox;
    private readonly CheckBox[] _chk = new CheckBox[Outputs];
    private readonly NumberBox[][] _num = new NumberBox[Outputs][];
    private bool _loading;   // evita re-entradas al sincronizar cajas: WinUI 3
                              // relanza ValueChanged/Checked al realizar el panel

    // Estado de arrastre
    private int _arrastrando = -1;
    private bool _redimensionando;
    private Point _inicio;
    private CanvasRegion _original;

    public CanvasEditorPanel()
    {
        for (int s = 0; s < Outputs; s++)
            _regiones[s] = new CanvasRegion(0, 0, _cw, _ch);

        // ── Tamaño del canvas + presets ─────────────────────────────────
        var sizeRow = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        sizeRow.Children.Add(new TextBlock
        {
            Text = Loc.T("Canvas:", "Canvas:"),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 60,
        });
        _wBox = NumBox(56);
        _hBox = NumBox(56);
        sizeRow.Children.Add(_wBox);
        sizeRow.Children.Add(new TextBlock
        {
            Text = Loc.T("×", "×"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
        });
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
        _lienzo = new Canvas
        {
            Width = AnchoVista, Height = AltoVista,
            Background = Theme.GeometryBgBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _canvasRect = new Rectangle
        {
            Stroke = Theme.GeometryGridBrush,
            StrokeThickness = 1,
            Fill = Theme.WindowBgBrush,
        };
        _lienzo.Children.Add(_canvasRect);

        for (int s = 0; s < Outputs; s++)
        {
            int idx = s;
            var color = Theme.Hex(CanvasEditorLayout.OutputColors[s]);
            var brush = Theme.Solid(color);
            var label = new TextBlock
            {
                Text = Loc.T($"S{s + 1}", $"S{s + 1}"),
                Foreground = brush,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(4, 2, 0, 0),
            };
            var handle = new Rectangle
            {
                Width = 10, Height = 10,
                Fill = brush,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            var grid = new Grid();
            grid.Children.Add(label);
            grid.Children.Add(handle);
            var rect = new Border
            {
                BorderBrush = brush,
                BorderThickness = new Thickness(2),
                // Relleno translúcido: la misma opacidad (0x30/255) que
                // Theme.Alpha(color, 0x30) le daba en WPF, vía Brush.Opacity
                // en vez de un segundo Color — no hace falta un helper Alpha
                // aquí para un único uso.
                Background = new SolidColorBrush(color) { Opacity = 0x30 / 255.0 },
                Child = grid,
            };
            rect.PointerPressed += (_, e) => AlPulsar(idx, e, redimensionar: false);
            handle.PointerPressed += (_, e) =>
            {
                AlPulsar(idx, e, redimensionar: true);
                e.Handled = true;
            };
            rect.PointerMoved += AlMover;
            rect.PointerReleased += AlSoltar;
            _rects[s] = rect;
            _lienzo.Children.Add(rect);
        }
        Children.Add(new Border
        {
            Child = _lienzo,
            BorderBrush = Theme.ButtonBgBrush,
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
                Content = Loc.T($"Salida {s + 1}", $"Output {s + 1}"),
                Foreground = Theme.Solid(Theme.Hex(CanvasEditorLayout.OutputColors[s])),
                FontWeight = FontWeights.Bold,
                MinWidth = 0,
                Width = 90,
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = s == 0,
                IsEnabled = s != 0,   // la 1 siempre habilitada
            };
            _chk[s].Checked += (_, _) => { if (!_loading) { _habilitadas[idx] = true; Redibujar(); } };
            _chk[s].Unchecked += (_, _) => { if (!_loading) { _habilitadas[idx] = false; Redibujar(); } };
            row.Children.Add(_chk[s]);

            _num[s] = new NumberBox[4];
            string[] tags =
            {
                Loc.T("X:", "X:"), Loc.T("Y:", "Y:"),
                Loc.T("An:", "W:"), Loc.T("Al:", "H:"),
            };
            for (int f = 0; f < 4; f++)
            {
                int fi = f;
                row.Children.Add(new TextBlock
                {
                    Text = tags[f],
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(f == 0 ? 0 : 8, 0, 2, 0),
                    Foreground = Theme.TextDimBrush,
                });
                var box = NumBox(48);
                box.ValueChanged += (_, _) => { if (!_loading) CommitNumBox(idx, fi); };
                _num[s][f] = box;
                row.Children.Add(box);
            }
            Children.Add(row);
        }

        _wBox.ValueChanged += (_, _) => { if (!_loading) CommitCanvasSize(); };
        _hBox.ValueChanged += (_, _) => { if (!_loading) CommitCanvasSize(); };

        Redibujar();
    }

    private static NumberBox NumBox(double width) => new()
    {
        Width = width,
        MinWidth = 0,
        VerticalAlignment = VerticalAlignment.Center,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
    };

    // ==================================================================
    // API para SettingsPanel (registro de campos) — mismas firmas que
    // WinCue.App.CanvasEditorControl.
    // ==================================================================

    public int CanvasW
    {
        get => _cw;
        set { _cw = Math.Clamp(value <= 0 ? 1920 : value, 320, 16384); ClampAll(); Redibujar(); }
    }

    public int CanvasH
    {
        get => _ch;
        set { _ch = Math.Clamp(value <= 0 ? 1080 : value, 240, 16384); ClampAll(); Redibujar(); }
    }

    public string GetRegion(int s) => CanvasEditorLayout.Format(_regiones[s]);

    public void SetRegion(int s, string? text)
    {
        var v = WinCue.Controller.CanvasLayout.ParseRegion(text ?? "", s, _cw, _ch);
        _regiones[s] = CanvasEditorLayout.Clamp(new CanvasRegion(v.X, v.Y, v.W, v.H), _cw, _ch);
        Redibujar();
    }

    public bool GetEnabled(int s) => s == 0 || _habilitadas[s];

    public void SetEnabled(int s, bool on)
    {
        _habilitadas[s] = s == 0 || on;
        _loading = true;
        _chk[s].IsChecked = _habilitadas[s];
        _loading = false;
        Redibujar();
    }

    // ==================================================================
    // Lienzo: redibujado y arrastre (el cálculo vive en CanvasEditorLayout)
    // ==================================================================

    private void Redibujar()
    {
        double s = CanvasEditorLayout.Scale(_cw, _ch, AnchoVista, AltoVista);
        var (ox, oy) = CanvasEditorLayout.Origin(_cw, _ch, AnchoVista, AltoVista);
        Canvas.SetLeft(_canvasRect, ox);
        Canvas.SetTop(_canvasRect, oy);
        _canvasRect.Width = _cw * s;
        _canvasRect.Height = _ch * s;

        for (int i = 0; i < Outputs; i++)
        {
            bool on = GetEnabled(i);
            _rects[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            var view = CanvasEditorLayout.ToView(_regiones[i], _cw, _ch, AnchoVista, AltoVista);
            Canvas.SetLeft(_rects[i], view.X);
            Canvas.SetTop(_rects[i], view.Y);
            _rects[i].Width = Math.Max(6, view.W);
            _rects[i].Height = Math.Max(6, view.H);
            // Z: la salida arrastrada, encima.
            Canvas.SetZIndex(_rects[i], i == _arrastrando ? 10 : 1 + i);

            _loading = true;
            var r = _regiones[i];
            double[] vals = { r.X, r.Y, r.W, r.H };
            for (int f = 0; f < 4; f++)
            {
                _num[i][f].Value = vals[f];
                _num[i][f].IsEnabled = on;
            }
            _loading = false;
        }

        _loading = true;
        _wBox.Value = _cw;
        _hBox.Value = _ch;
        _loading = false;
    }

    private void AlPulsar(int salida, PointerRoutedEventArgs e, bool redimensionar)
    {
        _arrastrando = salida;
        _redimensionando = redimensionar;
        _inicio = e.GetCurrentPoint(_lienzo).Position;
        _original = _regiones[salida];
        _rects[salida].CapturePointer(e.Pointer);
    }

    private void AlMover(object sender, PointerRoutedEventArgs e)
    {
        if (_arrastrando < 0)
            return;

        var p = e.GetCurrentPoint(_lienzo).Position;
        double escala = CanvasEditorLayout.Scale(_cw, _ch, AnchoVista, AltoVista);
        int dx = CanvasEditorLayout.ToCanvasDelta(p.X - _inicio.X, escala);
        int dy = CanvasEditorLayout.ToCanvasDelta(p.Y - _inicio.Y, escala);

        _regiones[_arrastrando] = _redimensionando
            ? CanvasEditorLayout.Resize(_original, dx, dy, _regiones, _habilitadas,
                                        _arrastrando, _cw, _ch, escala)
            : CanvasEditorLayout.Move(_original, dx, dy, _regiones, _habilitadas,
                                      _arrastrando, _cw, _ch, escala);
        Redibujar();
    }

    private void AlSoltar(object sender, PointerRoutedEventArgs e)
    {
        if (_arrastrando >= 0)
            _rects[_arrastrando].ReleasePointerCapture(e.Pointer);
        _arrastrando = -1;
        Redibujar();
    }

    // ==================================================================
    // Campos numéricos
    // ==================================================================

    private void CommitNumBox(int output, int field)
    {
        if (double.IsNaN(_num[output][field].Value))
            return;
        int v = (int)Math.Round(_num[output][field].Value);
        var r = _regiones[output];
        _regiones[output] = field switch
        {
            0 => r with { X = v },
            1 => r with { Y = v },
            2 => r with { W = v },
            _ => r with { H = v },
        };
        ClampAll();
        Redibujar();
    }

    private void CommitCanvasSize()
    {
        if (!double.IsNaN(_wBox.Value)) CanvasW = (int)Math.Round(_wBox.Value);
        if (!double.IsNaN(_hBox.Value)) CanvasH = (int)Math.Round(_hBox.Value);
    }

    /// <summary>Mantiene todas las regiones dentro del canvas.</summary>
    private void ClampAll()
    {
        for (int i = 0; i < Outputs; i++)
            _regiones[i] = CanvasEditorLayout.Clamp(_regiones[i], _cw, _ch);
    }
}
