// =============================================================================
// TimelinePanel.cs — Editor de línea de tiempo estilo Watchout para grupos
// TIMELINE
// =============================================================================
// Un grupo en modo TIMELINE dispara todos sus hijos a la vez y cada hijo
// respeta su pre_wait como offset: este panel lo hace VISIBLE y EDITABLE.
// Los hijos directos del grupo son bloques sobre 3–5 capas horizontales:
//   · eje X = tiempo (pre_wait → posición; duración efectiva → anchura)
//   · eje Y = capa (campo timeline_layer, solo organización visual)
//   · arrastrar horizontal = cambiar pre_wait (snap 0,1 s)
//   · arrastrar vertical   = cambiar de capa
//   · Ctrl+rueda = zoom · rueda = desplazamiento · clic = seleccionar en tabla
// El número de capas visibles es dinámico: mínimo 3, crece hasta 5 según se
// usen. Un cursor rojo recorre el timeline mientras el grupo suena.
// =============================================================================

using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinCue.Core;

namespace WinCue.App;

/// <summary>Cabecera (título, zoom, GO/STOP del grupo) + lienzo del timeline.</summary>
public sealed class TimelinePanel : Border
{
    private readonly TextBlock _title = new()
    {
        FontWeight = FontWeights.Bold,
        Foreground = new SolidColorBrush(Color.FromRgb(0xe8, 0xc0, 0x00)),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(4, 0, 12, 0),
    };
    private readonly TimelineCanvas _canvas = new();

    /// <summary>Commit de un campo de un cue (mismo contrato que el inspector).</summary>
    public Action<int, string, object?>? CommitField
    {
        get => _canvas.CommitField;
        set => _canvas.CommitField = value;
    }

    /// <summary>Ids de cues en ejecución (para el cursor y el resaltado).</summary>
    public Func<IReadOnlyCollection<int>>? RunningIds
    {
        get => _canvas.RunningIds;
        set => _canvas.RunningIds = value;
    }

    /// <summary>Clic en un bloque: seleccionar ese cue en la tabla.</summary>
    public event Action<int>? CueSelected
    {
        add => _canvas.CueSelected += value;
        remove => _canvas.CueSelected -= value;
    }

    /// <summary>▶ de la cabecera: disparar el grupo.</summary>
    public event Action<int>? GoGroupRequested;

    /// <summary>■ de la cabecera: detener los cues del grupo.</summary>
    public event Action<int>? StopGroupRequested;

    private Cue? _group;

    public TimelinePanel()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x22));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x3e));
        BorderThickness = new Thickness(0, 1, 0, 0);

        var header = new DockPanel { Margin = new Thickness(6, 4, 6, 2) };

        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(HeaderButton("▶ GO grupo", "#1e7e34",
            () => { if (_group is not null) GoGroupRequested?.Invoke(_group.Id); }));
        right.Children.Add(HeaderButton("■", "#5a2a2a",
            () => { if (_group is not null) StopGroupRequested?.Invoke(_group.Id); }));
        right.Children.Add(HeaderButton("−", "#3c3c3c", () => _canvas.ZoomStep(1 / 1.4)));
        right.Children.Add(HeaderButton("＋", "#3c3c3c", () => _canvas.ZoomStep(1.4)));
        right.Children.Add(HeaderButton("Ajustar", "#3c3c3c", () => _canvas.ZoomToFit()));
        DockPanel.SetDock(right, Dock.Right);
        header.Children.Add(right);

        var hint = new TextBlock
        {
            Text = "arrastrar: tiempo · vertical: capa · Ctrl+rueda: zoom",
            Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(hint, Dock.Right);
        header.Children.Add(hint);
        header.Children.Add(_title);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_canvas);
        Child = root;
    }

    private static Button HeaderButton(string text, string bg, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(8, 1, 8, 1),
            Margin = new Thickness(3, 0, 0, 0),
            Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(bg)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xdd, 0xdd, 0xdd)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            FontSize = 12,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Apunta el panel a un grupo TIMELINE (null = sin grupo).</summary>
    public void SetGroup(Cue? group, CueModel? model)
    {
        _group = group;
        _canvas.SetGroup(group, model);
        if (group is not null)
        {
            _title.Text = $"⏱ TIMELINE · {group.DisplayNumber} · {group.Name}";
            _canvas.ZoomToFitIfNew();
        }
    }

    /// <summary>Repintar tras un cambio en el modelo (edición, undo, carga…).</summary>
    public void RefreshFromModel() => _canvas.InvalidateVisual();
}

/// <summary>Lienzo del timeline: regla, capas, bloques y cursor de reproducción.</summary>
public sealed class TimelineCanvas : FrameworkElement
{
    private const double RulerH = 20;
    private const double LayerH = 34;
    private const int MaxLayers = 5;
    private const int MinLayers = 3;
    private const double SnapSeconds = 0.1;
    private const double MinTailSeconds = 10;   // aire tras el último bloque

    private static readonly Brush BgBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1a)));
    private static readonly Brush LayerABrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x21)));
    private static readonly Brush LayerBBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x25)));
    private static readonly Brush RulerBg = Frozen(new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x29)));
    private static readonly Brush TickBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)));
    private static readonly Pen TickPen = FrozenPen(Color.FromRgb(0x55, 0x55, 0x58), 1);
    private static readonly Pen TickMinorPen = FrozenPen(Color.FromRgb(0x33, 0x33, 0x36), 1);
    private static readonly Pen PlayheadPen = FrozenPen(Color.FromRgb(0xc0, 0x39, 0x2b), 2);
    private static readonly Pen RunningPen = FrozenPen(Color.FromRgb(0x2e, 0xcc, 0x71), 2);
    private static readonly Pen SelectedPen = FrozenPen(Color.FromRgb(0xff, 0xff, 0xff), 1.5);
    private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xee, 0xee, 0xee)));
    private static readonly Brush FadeBrush = Frozen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)));
    private static readonly Brush MsgBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)));

    /// <summary>Colores de bloque por tipo (espejo de la columna Tipo de la tabla).</summary>
    private static readonly Dictionary<CueType, string> TypeColors = new()
    {
        [CueType.Audio] = "#4a86b8",
        [CueType.Video] = "#9b59b6",
        [CueType.Wait] = "#666666",
        [CueType.Fade] = "#e8a200",
        [CueType.Start] = "#2ecc71",
        [CueType.Stop] = "#c0392b",
        [CueType.Script] = "#16a085",
        [CueType.Memo] = "#777777",
        [CueType.Group] = "#c9a227",
    };

    public Action<int, string, object?>? CommitField;
    public Func<IReadOnlyCollection<int>>? RunningIds;
    public event Action<int>? CueSelected;

    private Cue? _group;
    private CueModel? _model;
    private int _lastGroupId = -1;

    private double _pxPerSec = 20;
    private double _scrollX;
    private int _selectedId = -1;

    // Arrastre de un bloque
    private int _dragId = -1;
    private bool _dragMoved;
    private Point _dragStart;
    private double _dragOrigPreWait;
    private int _dragOrigLayer;
    private double _dragPreWait;
    private int _dragLayer;

    // Cursor de reproducción: instante en que el grupo empezó a sonar
    private long? _playT0Ticks;
    private readonly DispatcherTimer _playTimer;

    public TimelineCanvas()
    {
        ClipToBounds = true;
        MinHeight = RulerH + MinLayers * LayerH;
        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) };
        _playTimer.Tick += (_, _) => TickPlayhead();
    }

    public void SetGroup(Cue? group, CueModel? model)
    {
        _group = group;
        _model = model;
        if (group is null)
        {
            _playTimer.Stop();
            _playT0Ticks = null;
        }
        else
        {
            _playTimer.Start();
        }
        UpdateHeight();
        InvalidateVisual();
    }

    /// <summary>Ajusta el zoom SOLO si el grupo mostrado es otro (no pisar el
    /// zoom del usuario al refrescar la selección dentro del mismo grupo).</summary>
    public void ZoomToFitIfNew()
    {
        if (_group is null || _group.Id == _lastGroupId)
            return;
        _lastGroupId = _group.Id;
        ZoomToFit();
    }

    // ------------------------------------------------------------------
    // Modelo → bloques
    // ------------------------------------------------------------------

    private List<Cue> Children() =>
        _group is null || _model is null ? new() : _model.DirectChildren(_group.Id);

    /// <summary>Duración efectiva de la ACCIÓN del cue en segundos
    /// (0 = instantáneo: START/STOP/MEMO/SCRIPT).</summary>
    private double EffectiveDuration(Cue cue, int depth = 0)
    {
        if (depth > 8)
            return 0;
        switch (cue.Type)
        {
            case CueType.Audio:
            case CueType.Video:
                double end = cue.TrimEnd > 0
                    ? (cue.Duration > 0 ? Math.Min(cue.TrimEnd, cue.Duration) : cue.TrimEnd)
                    : cue.Duration;
                return Math.Max(0, end - cue.TrimStart);
            case CueType.Wait:
            case CueType.Fade:
                return Math.Max(0, cue.Duration);
            case CueType.Group:
                // Un grupo anidado dura hasta que acaba su último hijo
                double max = 0;
                if (_model is not null)
                    foreach (var child in _model.DirectChildren(cue.Id))
                        max = Math.Max(max, child.PreWait + EffectiveDuration(child, depth + 1));
                return max;
            default:
                return 0;
        }
    }

    private int VisibleLayers()
    {
        int maxUsed = 0;
        foreach (var c in Children())
            maxUsed = Math.Max(maxUsed, Math.Clamp(c.TimelineLayer, 0, MaxLayers - 1));
        if (_dragId >= 0)
            maxUsed = Math.Max(maxUsed, _dragLayer);
        return Math.Clamp(maxUsed + 2, MinLayers, MaxLayers);
    }

    private double TotalSeconds()
    {
        double max = 0;
        foreach (var c in Children())
            max = Math.Max(max, DisplayPreWait(c) + EffectiveDuration(c));
        return max;
    }

    // Durante el arrastre, el bloque activo usa los valores provisionales
    private double DisplayPreWait(Cue c) => c.Id == _dragId ? _dragPreWait : c.PreWait;
    private int DisplayLayer(Cue c) =>
        Math.Clamp(c.Id == _dragId ? _dragLayer : c.TimelineLayer, 0, MaxLayers - 1);

    private void UpdateHeight()
    {
        double h = RulerH + VisibleLayers() * LayerH;
        if (double.IsNaN(Height) || Math.Abs(Height - h) > 0.5)
            Height = h;
    }

    private double SecToX(double sec) => sec * _pxPerSec - _scrollX;
    private double XToSec(double x) => (x + _scrollX) / _pxPerSec;

    // ------------------------------------------------------------------
    // Zoom / scroll
    // ------------------------------------------------------------------

    public void ZoomStep(double factor) => ZoomAt(factor, ActualWidth / 2);

    private void ZoomAt(double factor, double anchorX)
    {
        double sec = XToSec(anchorX);
        _pxPerSec = Math.Clamp(_pxPerSec * factor, 2, 400);
        _scrollX = Math.Max(0, sec * _pxPerSec - anchorX);
        InvalidateVisual();
    }

    public void ZoomToFit()
    {
        double total = Math.Max(5, TotalSeconds() + 2);
        double w = Math.Max(200, ActualWidth > 0 ? ActualWidth : 800);
        _pxPerSec = Math.Clamp((w - 20) / total, 2, 400);
        _scrollX = 0;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
            ZoomAt(e.Delta > 0 ? 1.25 : 1 / 1.25, e.GetPosition(this).X);
        else
        {
            double maxScroll = Math.Max(0,
                (TotalSeconds() + MinTailSeconds) * _pxPerSec - ActualWidth);
            _scrollX = Math.Clamp(_scrollX - e.Delta * 0.6, 0, maxScroll);
            InvalidateVisual();
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------
    // Pintado
    // ------------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(BgBrush, null, new Rect(0, 0, w, h));
        if (_group is null || _model is null)
            return;

        int layers = VisibleLayers();
        var children = Children();

        // Fondo de capas alternado + número de capa
        for (int l = 0; l < layers; l++)
        {
            double y = RulerH + l * LayerH;
            dc.DrawRectangle(l % 2 == 0 ? LayerABrush : LayerBBrush, null,
                new Rect(0, y, w, LayerH));
            var num = Label($"{l + 1}", MsgBrush, 9);
            dc.DrawText(num, new Point(4, y + 2));
        }

        // Regla de tiempo con paso adaptativo
        dc.DrawRectangle(RulerBg, null, new Rect(0, 0, w, RulerH));
        double step = RulerStep();
        double firstTick = Math.Floor(XToSec(0) / step) * step;
        for (double t = Math.Max(0, firstTick); SecToX(t) < w; t += step)
        {
            double x = SecToX(t);
            dc.DrawLine(TickPen, new Point(x, RulerH - 6), new Point(x, RulerH));
            dc.DrawLine(TickMinorPen, new Point(x, RulerH), new Point(x, h));
            var label = Label(FmtTime(t), TickBrush, 10);
            dc.DrawText(label, new Point(x + 3, 2));
        }

        if (children.Count == 0)
        {
            var msg = Label("Grupo TIMELINE vacío — añade cues dentro del grupo",
                MsgBrush, 12);
            dc.DrawText(msg, new Point((w - msg.Width) / 2, (h + RulerH - msg.Height) / 2));
            return;
        }

        var running = RunningIds?.Invoke() ?? Array.Empty<int>();

        // Bloques (el arrastrado se pinta el último, por encima)
        foreach (var cue in children.Where(c => c.Id != _dragId))
            DrawBlock(dc, cue, running);
        var dragging = children.FirstOrDefault(c => c.Id == _dragId);
        if (dragging is not null)
            DrawBlock(dc, dragging, running);

        // Cursor de reproducción
        if (_playT0Ticks is long t0)
        {
            double sec = Stopwatch.GetElapsedTime(t0).TotalSeconds;
            double x = SecToX(sec);
            if (x >= 0 && x <= w)
            {
                dc.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, h));
                var lbl = Label(FmtTime(sec), Frozen(
                    new SolidColorBrush(Color.FromRgb(0xff, 0x8a, 0x7a))), 10);
                dc.DrawText(lbl, new Point(x + 4, RulerH + 1));
            }
        }
    }

    private void DrawBlock(DrawingContext dc, Cue cue, IReadOnlyCollection<int> running)
    {
        double dur = EffectiveDuration(cue);
        double x = SecToX(DisplayPreWait(cue));
        double y = RulerH + DisplayLayer(cue) * LayerH + 3;
        double bh = LayerH - 6;
        bool instant = dur <= 0;
        double bw = instant ? 12 : Math.Max(4, dur * _pxPerSec);

        if (x + bw < 0 || x > ActualWidth)
            return;

        string hex = cue.Color.Length > 0
            && CueConstants.CueColors.TryGetValue(cue.Color, out var ch) && ch.Length > 0
            ? ch
            : TypeColors.GetValueOrDefault(cue.Type, "#555555");
        var baseColor = (Color)ColorConverter.ConvertFromString(hex);
        var fill = new LinearGradientBrush(
            baseColor, Darken(baseColor, 0.65), 90);
        fill.Freeze();

        Pen? border = running.Contains(cue.Id) ? RunningPen
            : cue.Id == _selectedId ? SelectedPen
            : null;
        var rect = new Rect(x, y, bw, bh);
        dc.DrawRoundedRectangle(fill, border, rect, 3, 3);

        if (!cue.Armed)
            dc.DrawRoundedRectangle(FadeBrush, null, rect, 3, 3);

        // Fades de media como triángulos oscurecidos (estilo Watchout)
        if (!instant && cue.IsMedia())
        {
            double fi = Math.Min(cue.FadeIn * _pxPerSec, bw);
            double fo = Math.Min(cue.FadeOut * _pxPerSec, bw);
            if (fi > 2)
                dc.DrawGeometry(FadeBrush, null, Triangle(
                    new Point(x, y), new Point(x + fi, y), new Point(x, y + bh)));
            if (fo > 2)
                dc.DrawGeometry(FadeBrush, null, Triangle(
                    new Point(x + bw, y), new Point(x + bw - fo, y), new Point(x + bw, y + bh)));
        }

        // Etiqueta: número · nombre (+ ∞ si loop)
        if (bw > 30)
        {
            string text = $"{cue.DisplayNumber} · {cue.Name}";
            if (cue.Loop || cue.PlayCount > 1)
                text += cue.Loop ? " ∞" : $" ×{cue.PlayCount}";
            var label = Label(text, LabelBrush, 11);
            label.MaxTextWidth = Math.Max(1, bw - 10);
            label.MaxLineCount = 1;
            label.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(label, new Point(x + 5, y + (bh - label.Height) / 2));
        }
    }

    private static Geometry Triangle(Point a, Point b, Point c)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(a, true, true);
            ctx.LineTo(b, true, false);
            ctx.LineTo(c, true, false);
        }
        g.Freeze();
        return g;
    }

    private double RulerStep()
    {
        foreach (double s in new[] { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 })
            if (s * _pxPerSec >= 64)
                return s;
        return 600;
    }

    private static string FmtTime(double s)
    {
        int m = (int)(s / 60);
        double rest = s - m * 60;
        return s >= 60 ? $"{m}:{rest:00.#}" : $"{rest:0.#}s";
    }

    private static FormattedText Label(string text, Brush brush, double size) => new(
        text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI"), size, brush, 1.0);

    private static Color Darken(Color c, double f) => Color.FromRgb(
        (byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    // ------------------------------------------------------------------
    // Interacción: seleccionar y arrastrar bloques
    // ------------------------------------------------------------------

    private Cue? HitBlock(Point p)
    {
        if (p.Y < RulerH)
            return null;
        int layer = (int)((p.Y - RulerH) / LayerH);
        double sec = XToSec(p.X);
        // Recorrer al revés: el último pintado queda encima
        var children = Children();
        for (int i = children.Count - 1; i >= 0; i--)
        {
            var c = children[i];
            if (DisplayLayer(c) != layer)
                continue;
            double dur = EffectiveDuration(c);
            double start = DisplayPreWait(c);
            double wSec = Math.Max(dur, 12 / _pxPerSec);   // mínimo clicable
            if (sec >= start && sec <= start + wSec)
                return c;
        }
        return null;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _group is null)
            return;
        var p = e.GetPosition(this);
        var cue = HitBlock(p);
        if (cue is null)
            return;

        _selectedId = cue.Id;
        CueSelected?.Invoke(cue.Id);

        _dragId = cue.Id;
        _dragMoved = false;
        _dragStart = p;
        _dragOrigPreWait = cue.PreWait;
        _dragOrigLayer = Math.Clamp(cue.TimelineLayer, 0, MaxLayers - 1);
        _dragPreWait = _dragOrigPreWait;
        _dragLayer = _dragOrigLayer;
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_dragId < 0)
        {
            Cursor = HitBlock(p) is not null ? Cursors.SizeAll : Cursors.Arrow;
            return;
        }
        if (!_dragMoved &&
            Math.Abs(p.X - _dragStart.X) < 4 && Math.Abs(p.Y - _dragStart.Y) < 4)
            return;   // umbral: distinguir clic de arrastre
        _dragMoved = true;

        double dSec = (p.X - _dragStart.X) / _pxPerSec;
        _dragPreWait = Math.Max(0,
            Math.Round((_dragOrigPreWait + dSec) / SnapSeconds) * SnapSeconds);

        int layer = (int)((p.Y - RulerH) / LayerH);
        _dragLayer = Math.Clamp(layer, 0, MaxLayers - 1);

        UpdateHeight();
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_dragId < 0)
            return;
        int id = _dragId;
        bool moved = _dragMoved;
        double preWait = _dragPreWait;
        int layer = _dragLayer;
        _dragId = -1;
        ReleaseMouseCapture();

        if (moved)
        {
            if (Math.Abs(preWait - _dragOrigPreWait) > 1e-9)
                CommitField?.Invoke(id, "pre_wait", preWait);
            if (layer != _dragOrigLayer)
                CommitField?.Invoke(id, "timeline_layer", layer);
        }
        UpdateHeight();
        InvalidateVisual();
    }

    // ------------------------------------------------------------------
    // Cursor de reproducción
    // ------------------------------------------------------------------

    private void TickPlayhead()
    {
        if (_group is null || _model is null || RunningIds is null)
            return;
        bool any = false;
        foreach (int id in RunningIds())
        {
            if (id == _group.Id ||
                (_model.GetById(id) is Cue c && _model.IsDescendant(c, _group.Id)))
            {
                any = true;
                break;
            }
        }
        if (any && _playT0Ticks is null)
            _playT0Ticks = Stopwatch.GetTimestamp();
        else if (!any && _playT0Ticks is not null)
            _playT0Ticks = null;

        if (any || _playT0Ticks is not null)
            InvalidateVisual();
        else if (any != _lastAnyRunning)
            InvalidateVisual();
        _lastAnyRunning = any;
    }

    private bool _lastAnyRunning;

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    private static Pen FrozenPen(Color c, double width)
    {
        var p = new Pen(new SolidColorBrush(c), width);
        p.Freeze();
        return p;
    }
}
