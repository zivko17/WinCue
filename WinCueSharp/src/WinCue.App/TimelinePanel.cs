// =============================================================================
// TimelinePanel.cs — Línea de tiempo del SHOW completo, estilo Watchout
// =============================================================================
// Vive como pestaña junto al inspector (en modo SHOW se selecciona sola).
// Proyecta la lista de cues en tiempo, correlativa con el orden de la tabla:
//   · cada cue empieza donde termina el anterior, o donde manden sus
//     auto-continue/auto-follow (pre_wait y post_wait incluidos)
//   · en los encadenados manuales se dibuja un marcador GO: a partir de ahí
//     el tiempo es una PROYECCIÓN (el operador dispara cuando quiere)
//   · los grupos se dibujan como un RECUADRO que abraza sus bloques, con
//     nombre y modo; arrastrarlo mueve el grupo entero (pre_wait)
//   · capas: -1 = automática (se apila sola al solaparse); arrastrar un
//     bloque en vertical lo fija a mano en esa pista (timeline_layer 0-4)
// Interacción: arrastre horizontal = pre_wait (snap 0,1 s; el resto del show
// se desplaza en vivo); arrastrar más allá del vecino = reordenar la lista;
// clic = seleccionar en la tabla (y viceversa); Ctrl+rueda = zoom. Cursor
// rojo y progreso verde con los ActiveCueInfo reales del controlador.
// =============================================================================

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinCue.Controller;
using WinCue.Core;

namespace WinCue.App;

/// <summary>Cabecera (zoom) + lienzo de la línea de tiempo.</summary>
public sealed class TimelinePanel : Border
{
    private readonly TimelineCanvas _canvas = new();

    public Action<int, string, object?>? CommitField
    {
        get => _canvas.CommitField;
        set => _canvas.CommitField = value;
    }

    /// <summary>Reordenación pedida desde el timeline (fromRow, toRow).</summary>
    public Action<int, int>? MoveCueRequested
    {
        get => _canvas.MoveCueRequested;
        set => _canvas.MoveCueRequested = value;
    }

    /// <summary>Clic en un bloque o grupo: seleccionar ese cue en la tabla.</summary>
    public event Action<int>? CueSelected
    {
        add => _canvas.CueSelected += value;
        remove => _canvas.CueSelected -= value;
    }

    /// <summary>Doble clic en un bloque: abrir ese cue en el inspector.</summary>
    public event Action<int>? CueActivated
    {
        add => _canvas.CueActivated += value;
        remove => _canvas.CueActivated -= value;
    }

    /// <summary>Menú contextual: disparar un cue por id.</summary>
    public Action<int>? TriggerRequested
    {
        get => _canvas.TriggerRequested;
        set => _canvas.TriggerRequested = value;
    }

    /// <summary>Menú contextual: detener un cue (o los hijos de un grupo).</summary>
    public Action<int>? StopRequested
    {
        get => _canvas.StopRequested;
        set => _canvas.StopRequested = value;
    }

    /// <summary>false en modo SHOW: los items de edición se deshabilitan.</summary>
    public Func<bool>? IsEditAllowed
    {
        get => _canvas.IsEditAllowed;
        set => _canvas.IsEditAllowed = value;
    }

    public TimelinePanel()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x22));

        var header = new DockPanel { Margin = new Thickness(6, 3, 6, 2) };

        var title = new TextBlock
        {
            Text = "⏱ TIMELINE DEL SHOW",
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xe8, 0xc0, 0x00)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 12, 0),
        };
        header.Children.Add(title);

        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(HeaderButton("−", () => _canvas.ZoomStep(1 / 1.4)));
        right.Children.Add(HeaderButton("＋", () => _canvas.ZoomStep(1.4)));
        right.Children.Add(HeaderButton("Ajustar", () => _canvas.ZoomToFit()));
        DockPanel.SetDock(right, Dock.Right);
        header.Children.Add(right);

        var hint = new TextBlock
        {
            Text = "arrastrar: tiempo · sobre el anterior = SOLAPAR (paralelo) · " +
                   "vertical: pista · Ctrl+rueda: zoom · ▸GO = disparo manual",
            Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 10, 0),
        };
        header.Children.Add(hint);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_canvas);
        Child = root;
    }

    private static Button HeaderButton(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(8, 0, 8, 1),
            Margin = new Thickness(3, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x3c, 0x3c, 0x3c)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xdd, 0xdd, 0xdd)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            FontSize = 12,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    public void SetModel(CueModel model) => _canvas.SetModel(model);

    /// <summary>Selección de la tabla → resaltar (y hacer visible) el bloque.</summary>
    public void SetSelected(int cueId) => _canvas.SetSelected(cueId);

    /// <summary>Cues activos del tick del controlador (cursor + progreso).</summary>
    public void SetActive(IReadOnlyList<ActiveCueInfo> rows) => _canvas.SetActive(rows);

    /// <summary>Recalcular la proyección tras cualquier cambio del modelo.</summary>
    public void RefreshFromModel() => _canvas.RefreshFromModel();
}

/// <summary>Lienzo: proyección temporal de la lista, bloques, grupos y cursor.</summary>
public sealed class TimelineCanvas : FrameworkElement
{
    private const double RulerH = 20;
    private const int MaxLayers = 5;
    private const int MinLayers = 3;
    private const double SnapSeconds = 0.1;
    private const double InstantFootprint = 0.3;   // huella temporal de un cue instantáneo

    private static readonly Brush BgBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1a)));
    private static readonly Brush LayerABrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x21)));
    private static readonly Brush LayerBBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x25)));
    private static readonly Brush RulerBg = Frozen(new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x29)));
    private static readonly Brush TickBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)));
    private static readonly Pen TickPen = FrozenPen(Color.FromRgb(0x55, 0x55, 0x58), 1);
    private static readonly Pen TickMinorPen = FrozenPen(Color.FromRgb(0x30, 0x30, 0x33), 1);
    private static readonly Pen PlayheadPen = FrozenPen(Color.FromRgb(0xc0, 0x39, 0x2b), 2);
    private static readonly Pen SelectedPen = FrozenPen(Color.FromRgb(0xff, 0xff, 0xff), 1.5);
    private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xee, 0xee, 0xee)));
    private static readonly Brush FadeBrush = Frozen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)));
    private static readonly Brush MsgBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)));
    private static readonly Brush ProgressBrush = Frozen(new SolidColorBrush(Color.FromArgb(110, 0x2e, 0xcc, 0x71)));
    private static readonly Brush GoBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2e, 0xcc, 0x71)));
    private static readonly Pen GoPen = MakeGoPen();

    private static Pen MakeGoPen()
    {
        var p = new Pen(new SolidColorBrush(Color.FromArgb(150, 0x2e, 0xcc, 0x71)), 1)
        { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
        p.Freeze();
        return p;
    }

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
    public Action<int, int>? MoveCueRequested;
    public Action<int>? TriggerRequested;
    public Action<int>? StopRequested;
    public Func<bool>? IsEditAllowed;
    public event Action<int>? CueSelected;

    /// <summary>Doble clic en un bloque: abrir ese cue en el inspector.</summary>
    public event Action<int>? CueActivated;

    private CueModel? _model;
    private int _selectedId = -1;

    private double _pxPerSec = 20;
    private double _scrollX;
    private bool _fitDone;

    // Proyección calculada de la lista
    private sealed class Block
    {
        public required Cue Cue;
        public double Start;
        public double Dur;            // 0 = instantáneo
        public int Layer;             // asignada (manual o auto)
        public bool Dimmed;           // desarmado / rama que no se disparará
        public bool ManualLayer;      // timeline_layer >= 0 (colocado a mano)
    }

    private sealed class Span
    {
        public required Cue Group;
        public double Start;
        public double End;
        public int MinLayer;
        public int MaxLayer;
    }

    private readonly List<Block> _blocks = new();
    private readonly List<Span> _spans = new();
    private readonly List<double> _goMarks = new();
    private readonly Dictionary<int, double> _startById = new();   // cue → inicio proyectado

    // Cues activos (cursor + progreso), del tick real del controlador
    private readonly Dictionary<int, double> _activeElapsed = new();

    // Arrastre (mover bloque/grupo, o redimensionar por los bordes)
    private enum DragMode { Move, ResizeL, ResizeR }

    private Cue? _dragCue;
    private DragMode _dragMode;
    private bool _dragIsGroup;
    private bool _dragMoved;
    private Point _dragStart;
    private double _dragOrigPreWait;
    private int _dragOrigLayer;       // timeline_layer original (-1 = auto)
    private double _dragPreWait;
    private int _dragLayer;
    // Redimensionado: borde izq = pre_wait+trim_start (deslizar punto de
    // entrada), borde dcho = trim_end (media) o duration (wait/fade)
    private double _dragOrigTrimStart, _dragOrigTrimEnd, _dragOrigDuration, _dragOrigEff;
    private double _dragTrimStart, _dragTrimEnd, _dragDuration;

    // Mini-onda de los bloques de audio (picos por archivo, calculados aparte)
    private readonly Dictionary<string, (float[] Mins, float[] Maxs, double Dur)> _peaks = new();
    private readonly HashSet<string> _peaksLoading = new();
    private static readonly Pen WavePen = FrozenPen(Color.FromArgb(110, 0xff, 0xff, 0xff), 1);

    public TimelineCanvas()
    {
        ClipToBounds = true;
        MinHeight = 90;
    }

    /// <summary>Alto de cada pista: reparte el alto disponible (estilo Watchout).</summary>
    private double LayerH =>
        Math.Max(24, (Math.Max(ActualHeight, 90) - RulerH) / VisibleLayers());

    public void SetModel(CueModel model)
    {
        _model = model;
        _fitDone = false;
        RefreshFromModel();
    }

    public void SetSelected(int cueId)
    {
        if (_selectedId == cueId)
            return;
        _selectedId = cueId;
        EnsureVisible(cueId);
        InvalidateVisual();
    }

    public void SetActive(IReadOnlyList<ActiveCueInfo> rows)
    {
        bool wasEmpty = _activeElapsed.Count == 0;
        _activeElapsed.Clear();
        foreach (var r in rows)
            _activeElapsed[r.Id] = r.Elapsed;
        if (!(wasEmpty && rows.Count == 0))
            InvalidateVisual();
    }

    public void RefreshFromModel()
    {
        RebuildLayout();
        InvalidateVisual();
    }

    // ------------------------------------------------------------------
    // Proyección: lista de cues → bloques con tiempo y capa
    // ------------------------------------------------------------------

    private double DisplayPreWait(Cue c) =>
        _dragCue is not null && c.Id == _dragCue.Id ? _dragPreWait : c.PreWait;

    /// <summary>timeline_layer efectivo (-1 = auto), con el override del arrastre.</summary>
    private int DisplayLayerRaw(Cue c) =>
        _dragCue is not null && c.Id == _dragCue.Id && !_dragIsGroup
            ? _dragLayer
            : Math.Clamp(c.TimelineLayer, -1, MaxLayers - 1);

    private bool IsDragging(Cue c, DragMode mode) =>
        _dragCue is not null && c.Id == _dragCue.Id && _dragMode == mode;

    private double DisplayTrimStart(Cue c) =>
        IsDragging(c, DragMode.ResizeL) ? _dragTrimStart : c.TrimStart;

    private double DisplayTrimEnd(Cue c) =>
        IsDragging(c, DragMode.ResizeR) && c.IsMedia() ? _dragTrimEnd : c.TrimEnd;

    private double DisplayDuration(Cue c) =>
        IsDragging(c, DragMode.ResizeR) && !c.IsMedia() ? _dragDuration : c.Duration;

    /// <summary>Duración de la acción del cue (0 = instantáneo), con los
    /// overrides del redimensionado en curso.</summary>
    private double EffectiveDuration(Cue cue)
    {
        switch (cue.Type)
        {
            case CueType.Audio:
            case CueType.Video:
                double ts = DisplayTrimStart(cue);
                double te = DisplayTrimEnd(cue);
                double end = te > 0
                    ? (cue.Duration > 0 ? Math.Min(te, cue.Duration) : te)
                    : cue.Duration;
                return Math.Max(0, end - ts);
            case CueType.Wait:
            case CueType.Fade:
                return Math.Max(0, DisplayDuration(cue));
            default:
                return 0;
        }
    }

    private void RebuildLayout()
    {
        _blocks.Clear();
        _spans.Clear();
        _goMarks.Clear();
        _startById.Clear();
        if (_model is null)
            return;
        var roots = _model.Cues.Where(c => c.ParentId is null).ToList();
        LayoutSequence(roots, 0.0, dimmed: false);
        AssignLayers();

        // Los recuadros de grupo abrazan las capas de sus bloques
        foreach (var span in _spans)
        {
            int min = MaxLayers - 1, max = 0;
            foreach (var b in _blocks)
                if (_model.IsDescendant(b.Cue, span.Group.Id))
                {
                    min = Math.Min(min, b.Layer);
                    max = Math.Max(max, b.Layer);
                }
            span.MinLayer = Math.Min(min, max);
            span.MaxLayer = Math.Max(min, max);
        }
    }

    /// <summary>Coloca una secuencia (nivel raíz o hijos de un grupo ENTER)
    /// encadenando triggers según continue_mode. Devuelve el fin de la última
    /// acción de la secuencia.</summary>
    private double LayoutSequence(List<Cue> cues, double t0, bool dimmed)
    {
        double trigger = t0;
        double lastEnd = t0;
        for (int i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            double actionStart = trigger + DisplayPreWait(cue);
            double end = LayoutCue(cue, actionStart, dimmed);
            lastEnd = Math.Max(lastEnd, end);
            if (i == cues.Count - 1)
                break;
            switch (cue.ContinueMode)
            {
                case ContinueMode.AutoContinue:
                    trigger = actionStart + cue.PostWait;
                    break;
                case ContinueMode.AutoFollow:
                    trigger = end;
                    break;
                default:   // GO manual: proyección "dispara cuando termina"
                    trigger = Math.Max(trigger, end);
                    if (!dimmed)
                        _goMarks.Add(trigger);
                    break;
            }
        }
        return lastEnd;
    }

    /// <summary>Coloca un cue (y sus hijos si es grupo) con la acción empezando
    /// en <paramref name="start"/>. Devuelve cuándo termina su acción.</summary>
    private double LayoutCue(Cue cue, double start, bool dimmed)
    {
        _startById[cue.Id] = start;

        if (cue.Type == CueType.Group)
        {
            var children = _model!.DirectChildren(cue.Id);
            bool dim = dimmed || !cue.Armed;
            double end = start;
            switch (cue.GroupMode)
            {
                case GroupMode.Timeline:
                    // Tiempo REAL: todos a la vez, cada uno a su offset
                    foreach (var child in children)
                        end = Math.Max(end, LayoutCue(
                            child, start + DisplayPreWait(child), dim));
                    break;
                case GroupMode.Enter:
                    end = LayoutSequence(children, start, dim);
                    break;
                default:   // GO_NEXT: solo el primero · RANDOM: cualquiera
                    for (int i = 0; i < children.Count; i++)
                    {
                        bool branchDim = dim ||
                            (cue.GroupMode == GroupMode.GoNext && i > 0);
                        double e = LayoutCue(children[i],
                            start + DisplayPreWait(children[i]), branchDim);
                        if (cue.GroupMode == GroupMode.Random || i == 0)
                            end = Math.Max(end, e);
                    }
                    break;
            }
            if (children.Count > 0)
                _spans.Add(new Span
                {
                    Group = cue,
                    Start = start,
                    End = Math.Max(end, start + 0.2),
                });
            return cue.Armed ? end : start;
        }

        int raw = DisplayLayerRaw(cue);
        _blocks.Add(new Block
        {
            Cue = cue,
            Start = start,
            Dur = EffectiveDuration(cue),
            Layer = raw,
            Dimmed = dimmed || !cue.Armed,
            ManualLayer = raw >= 0,
        });
        return cue.Armed ? start + EffectiveDuration(cue) : start;
    }

    /// <summary>Capas: la fijada a mano se respeta; el resto se apila greedy
    /// en la primera pista libre para su intervalo.</summary>
    private void AssignLayers()
    {
        var ends = new double[MaxLayers];
        for (int i = 0; i < MaxLayers; i++)
            ends[i] = double.NegativeInfinity;

        foreach (var b in _blocks.OrderBy(b => b.Start))
        {
            double foot = b.Start + Math.Max(b.Dur, InstantFootprint);
            if (b.ManualLayer)
            {
                b.Layer = Math.Clamp(b.Layer, 0, MaxLayers - 1);
                ends[b.Layer] = Math.Max(ends[b.Layer], foot);
                continue;
            }
            int chosen = MaxLayers - 1;
            for (int l = 0; l < MaxLayers; l++)
            {
                if (ends[l] <= b.Start + 1e-6)
                {
                    chosen = l;
                    break;
                }
            }
            b.Layer = chosen;
            ends[chosen] = Math.Max(ends[chosen], foot);
        }
    }

    private int VisibleLayers()
    {
        int maxUsed = 0;
        foreach (var b in _blocks)
            maxUsed = Math.Max(maxUsed, b.Layer);
        return Math.Clamp(maxUsed + 2, MinLayers, MaxLayers);
    }

    private double TotalSeconds()
    {
        double max = 0;
        foreach (var b in _blocks)
            max = Math.Max(max, b.Start + Math.Max(b.Dur, InstantFootprint));
        return max;
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
        _pxPerSec = Math.Clamp(_pxPerSec * factor, 1, 400);
        _scrollX = Math.Max(0, sec * _pxPerSec - anchorX);
        InvalidateVisual();
    }

    public void ZoomToFit()
    {
        double total = Math.Max(10, TotalSeconds() + 2);
        double w = Math.Max(200, ActualWidth > 0 ? ActualWidth : 900);
        _pxPerSec = Math.Clamp((w - 20) / total, 1, 400);
        _scrollX = 0;
        InvalidateVisual();
    }

    private void EnsureVisible(int cueId)
    {
        if (ActualWidth <= 0 || !_startById.TryGetValue(cueId, out double start))
            return;
        double x = SecToX(start);
        if (x < 0 || x > ActualWidth * 0.95)
            _scrollX = Math.Max(0, start * _pxPerSec - ActualWidth * 0.3);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
            ZoomAt(e.Delta > 0 ? 1.25 : 1 / 1.25, e.GetPosition(this).X);
        else
        {
            double maxScroll = Math.Max(0, (TotalSeconds() + 10) * _pxPerSec - ActualWidth);
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
        if (_model is null)
            return;

        if (!_fitDone && w > 0 && _blocks.Count > 0)
        {
            _fitDone = true;
            double total = Math.Max(10, TotalSeconds() + 2);
            _pxPerSec = Math.Clamp((w - 20) / total, 1, 400);
            _scrollX = 0;
        }

        int layers = VisibleLayers();
        double lh = LayerH;

        // Fondo de pistas alternado + número
        for (int l = 0; l < layers; l++)
        {
            double y = RulerH + l * lh;
            dc.DrawRectangle(l % 2 == 0 ? LayerABrush : LayerBBrush, null,
                new Rect(0, y, w, lh));
            dc.DrawText(Label($"{l + 1}", MsgBrush, 9), new Point(4, y + 2));
        }

        // Regla de tiempo
        dc.DrawRectangle(RulerBg, null, new Rect(0, 0, w, RulerH));
        double step = RulerStep();
        double firstTick = Math.Floor(XToSec(0) / step) * step;
        for (double t = Math.Max(0, firstTick); SecToX(t) < w; t += step)
        {
            double x = SecToX(t);
            dc.DrawLine(TickPen, new Point(x, RulerH - 6), new Point(x, RulerH));
            dc.DrawLine(TickMinorPen, new Point(x, RulerH), new Point(x, h));
            dc.DrawText(Label(FmtTime(t), TickBrush, 10), new Point(x + 3, 2));
        }

        if (_blocks.Count == 0)
        {
            var msg = Label("Show vacío — añade o importa cues", MsgBrush, 12);
            dc.DrawText(msg, new Point((w - msg.Width) / 2, (h + RulerH - msg.Height) / 2));
            return;
        }

        // Recuadros de grupo: primero el relleno (debajo de los bloques)
        foreach (var span in _spans)
        {
            var (_, _, box, color) = SpanGeometry(span, lh);
            if (box.Right < 0 || box.Left > w)
                continue;
            var fill = new SolidColorBrush(Color.FromArgb(30, color.R, color.G, color.B));
            fill.Freeze();
            dc.DrawRoundedRectangle(fill, null, box, 4, 4);
        }

        // Marcadores GO (a partir de aquí el tiempo es estimado)
        foreach (double t in _goMarks)
        {
            double x = SecToX(t);
            if (x < -20 || x > w)
                continue;
            dc.DrawLine(GoPen, new Point(x, RulerH), new Point(x, h));
            dc.DrawText(Label("▸GO", GoBrush, 9), new Point(x + 2, h - 13));
        }

        // Bloques (el arrastrado, encima)
        foreach (var b in _blocks.Where(b => _dragCue is null || b.Cue.Id != _dragCue.Id))
            DrawBlock(dc, b, lh);
        var dragBlock = _dragCue is null
            ? null
            : _blocks.FirstOrDefault(b => b.Cue.Id == _dragCue.Id);
        if (dragBlock is not null)
            DrawBlock(dc, dragBlock, lh);

        // Recuadros de grupo: marco + ASA con el nombre, POR ENCIMA de los
        // bloques (con un solo hijo el bloque taparía el recuadro y no habría
        // de dónde agarrar el grupo)
        foreach (var span in _spans)
            DrawSpanFrame(dc, span, lh, w);

        // Cursor: posición real más avanzada de los cues sonando (limitada al
        // final de su bloque: con trim, el contador nunca debe salirse de él)
        double? cursor = null;
        foreach (var b in _blocks)
            if (_activeElapsed.TryGetValue(b.Cue.Id, out double elapsed))
            {
                double lim = b.Dur > 0 ? b.Dur : elapsed;
                double pos = b.Start + Math.Min(Math.Max(0, elapsed), lim);
                cursor = Math.Max(cursor ?? 0, pos);
            }
        if (cursor is double cs)
        {
            double x = SecToX(cs);
            if (x >= 0 && x <= w)
            {
                dc.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, h));
                dc.DrawText(Label(FmtTime(cs), Frozen(
                    new SolidColorBrush(Color.FromRgb(0xff, 0x8a, 0x7a))), 10),
                    new Point(x + 4, RulerH + 1));
            }
        }
    }

    /// <summary>Geometría del recuadro de un grupo: caja, asa (chip con el
    /// nombre) y color. La misma para pintar y para el hit-test del arrastre.</summary>
    private (FormattedText Lbl, Rect Chip, Rect Box, Color Color)
        SpanGeometry(Span span, double lh)
    {
        var group = span.Group;
        string hex = group.Color.Length > 0
            && CueConstants.CueColors.TryGetValue(group.Color, out var gc) && gc.Length > 0
            ? gc
            : TypeColors[CueType.Group];
        var color = (Color)ColorConverter.ConvertFromString(hex);

        double x0 = SecToX(span.Start) - 4;
        double x1 = SecToX(span.End) + 4;
        double y = RulerH + span.MinLayer * lh + 1;
        double bh = (span.MaxLayer - span.MinLayer + 1) * lh - 2;
        var box = new Rect(x0, y, Math.Max(12, x1 - x0), bh);

        string mode = group.GroupMode.ToString().ToUpperInvariant();
        var lbl = Label($"▸ {group.DisplayNumber} · {group.Name} · {mode}",
            Frozen(new SolidColorBrush(Colors.White)), 10);
        double cw = Math.Min(lbl.WidthIncludingTrailingWhitespace + 12,
            Math.Max(30, box.Width));
        lbl.MaxTextWidth = Math.Max(1, cw - 10);
        lbl.MaxLineCount = 1;
        lbl.Trimming = TextTrimming.CharacterEllipsis;
        var chip = new Rect(box.X, box.Y, cw, 16);
        return (lbl, chip, box, color);
    }

    /// <summary>Marco + asa del grupo, por encima de los bloques.</summary>
    private void DrawSpanFrame(DrawingContext dc, Span span, double lh, double w)
    {
        var (lbl, chip, box, color) = SpanGeometry(span, lh);
        if (box.Right < 0 || box.Left > w)
            return;

        Pen border;
        if (span.Group.Id == _selectedId)
        {
            border = SelectedPen;
        }
        else
        {
            border = new Pen(new SolidColorBrush(
                Color.FromArgb(200, color.R, color.G, color.B)), 1.6);
            border.Freeze();
        }
        dc.DrawRoundedRectangle(null, border, box, 4, 4);

        // Asa: chip sólido con el nombre — de aquí se arrastra el grupo entero
        var chipFill = new SolidColorBrush(Color.FromArgb(235, color.R, color.G, color.B));
        chipFill.Freeze();
        dc.DrawRoundedRectangle(chipFill, null, chip, 3, 3);
        dc.DrawText(lbl, new Point(chip.X + 5, chip.Y + 1));
    }

    /// <summary>Asa de grupo bajo el punto (la del grupo más interno).</summary>
    private Span? HitSpanChip(Point p)
    {
        double lh = LayerH;
        Span? best = null;
        foreach (var s in _spans)
        {
            var (_, chip, _, _) = SpanGeometry(s, lh);
            if (!chip.Contains(p))
                continue;
            if (best is null || s.End - s.Start < best.End - best.Start)
                best = s;
        }
        return best;
    }

    private void DrawBlock(DrawingContext dc, Block b, double lh)
    {
        var cue = b.Cue;
        double x = SecToX(b.Start);
        double y = RulerH + b.Layer * lh + 4;
        double bh = lh - 8;
        bool instant = b.Dur <= 0;
        double bw = instant ? 12 : Math.Max(4, b.Dur * _pxPerSec);

        if (x + bw < 0 || x > ActualWidth)
            return;

        string hex = cue.Color.Length > 0
            && CueConstants.CueColors.TryGetValue(cue.Color, out var ch) && ch.Length > 0
            ? ch
            : TypeColors.GetValueOrDefault(cue.Type, "#555555");
        var baseColor = (Color)ColorConverter.ConvertFromString(hex);
        var fill = new LinearGradientBrush(baseColor, Darken(baseColor, 0.65), 90);
        fill.Freeze();

        Pen? border = cue.Id == _selectedId ? SelectedPen : null;
        var rect = new Rect(x, y, bw, bh);
        dc.DrawRoundedRectangle(fill, border, rect, 3, 3);

        if (b.Dimmed)
            dc.DrawRoundedRectangle(FadeBrush, null, rect, 3, 3);

        // Progreso real dentro del bloque mientras suena
        if (!instant && _activeElapsed.TryGetValue(cue.Id, out double elapsed))
        {
            double pw = Math.Clamp(elapsed / Math.Max(0.001, b.Dur), 0, 1) * bw;
            if (pw > 1)
                dc.DrawRoundedRectangle(ProgressBrush, null,
                    new Rect(x, y, pw, bh), 3, 3);
        }

        // Mini-onda dentro de los bloques de audio (picos del archivo)
        if (cue.Type == CueType.Audio && !instant && bw >= 40
            && cue.FilePath.Length > 0)
        {
            if (_peaks.TryGetValue(cue.FilePath, out var pk)
                && pk.Mins.Length > 0 && pk.Dur > 0)
            {
                double t0 = DisplayTrimStart(cue);
                double mid = y + bh / 2, half = bh / 2 - 2;
                int n = pk.Mins.Length;
                double px0 = Math.Max(x, 0), px1 = Math.Min(x + bw, ActualWidth);
                for (double px = px0; px < px1; px += 2)
                {
                    double sec = t0 + (px - x) / bw * b.Dur;
                    int i = Math.Clamp((int)(sec / pk.Dur * n), 0, n - 1);
                    dc.DrawLine(WavePen,
                        new Point(px + 0.5, mid - pk.Maxs[i] * half),
                        new Point(px + 0.5, mid - pk.Mins[i] * half));
                }
            }
            else
            {
                EnsurePeaks(cue.FilePath);
            }
        }

        // Fades de media como triángulos (estilo Watchout)
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

        if (bw > 30)
        {
            string text = $"{cue.DisplayNumber} · {cue.Name}";
            if (cue.Loop || cue.PlayCount > 1)
                text += cue.Loop ? " ∞" : $" ×{cue.PlayCount}";
            // 📌 = pista fijada a mano
            if (b.ManualLayer)
                text = "📌 " + text;
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
    // Interacción: seleccionar, arrastrar (tiempo/pista) y reordenar
    // ------------------------------------------------------------------

    private Block? HitBlock(Point p)
    {
        if (p.Y < RulerH)
            return null;
        double lh = LayerH;
        int layer = (int)((p.Y - RulerH) / lh);
        double sec = XToSec(p.X);
        for (int i = _blocks.Count - 1; i >= 0; i--)
        {
            var b = _blocks[i];
            if (b.Layer != layer)
                continue;
            double wSec = Math.Max(b.Dur, 12 / _pxPerSec);
            if (sec >= b.Start && sec <= b.Start + wSec)
                return b;
        }
        return null;
    }

    /// <summary>Recuadro de grupo bajo el punto (el más interno si hay anidados).</summary>
    private Span? HitSpan(Point p)
    {
        if (p.Y < RulerH)
            return null;
        double lh = LayerH;
        double sec = XToSec(p.X);
        int layer = (int)((p.Y - RulerH) / lh);
        Span? best = null;
        foreach (var s in _spans)
        {
            if (sec < s.Start - 3 / _pxPerSec || sec > s.End + 3 / _pxPerSec)
                continue;
            if (layer < s.MinLayer || layer > s.MaxLayer)
                continue;
            if (best is null || s.End - s.Start < best.End - best.Start)
                best = s;
        }
        return best;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _model is null)
            return;
        var p = e.GetPosition(this);
        // Prioridad: asa del grupo → bloque → cuerpo del recuadro
        Cue? cue;
        bool isGroup;
        Block? hitBlock = null;
        if (HitSpanChip(p) is Span chipSpan)
        {
            cue = chipSpan.Group;
            isGroup = true;
        }
        else if (HitBlock(p) is Block block)
        {
            cue = block.Cue;
            isGroup = false;
            hitBlock = block;
        }
        else if (HitSpan(p) is Span bodySpan)
        {
            cue = bodySpan.Group;
            isGroup = true;
        }
        else
        {
            return;
        }

        _selectedId = cue.Id;
        CueSelected?.Invoke(cue.Id);

        if (e.ClickCount == 2)
        {
            CueActivated?.Invoke(cue.Id);   // doble clic → inspector
            InvalidateVisual();
            return;
        }

        // En modo SHOW se selecciona pero no se edita (igual que la tabla)
        if (!(IsEditAllowed?.Invoke() ?? true))
        {
            InvalidateVisual();
            return;
        }

        _dragCue = cue;
        _dragIsGroup = isGroup;
        _dragMoved = false;
        _dragStart = p;
        _dragOrigPreWait = cue.PreWait;
        _dragOrigLayer = Math.Clamp(cue.TimelineLayer, -1, MaxLayers - 1);
        _dragPreWait = _dragOrigPreWait;
        _dragLayer = _dragOrigLayer;

        // ¿Agarró un borde? → redimensionar en vez de mover
        _dragMode = EdgeModeAt(hitBlock, p.X);
        _dragOrigTrimStart = cue.TrimStart;
        _dragOrigTrimEnd = cue.TrimEnd > 0 ? cue.TrimEnd : cue.Duration;
        _dragOrigDuration = cue.Duration;
        _dragOrigEff = hitBlock?.Dur ?? 0;
        _dragTrimStart = _dragOrigTrimStart;
        _dragTrimEnd = _dragOrigTrimEnd;
        _dragDuration = _dragOrigDuration;

        CaptureMouse();
        InvalidateVisual();
    }

    /// <summary>Modo de arrastre según el punto: bordes (±5 px) = trim/duración.
    /// Izquierdo solo para media (desliza el punto de entrada); derecho para
    /// media (trim de salida) y WAIT/FADE (duración).</summary>
    private DragMode EdgeModeAt(Block? b, double px)
    {
        if (b is null || b.Dur <= 0)
            return DragMode.Move;
        double x = SecToX(b.Start);
        double bw = Math.Max(4, b.Dur * _pxPerSec);
        if (bw < 18)
            return DragMode.Move;
        bool media = b.Cue.IsMedia();
        bool durable = b.Cue.Type is CueType.Wait or CueType.Fade;
        if (media && Math.Abs(px - x) <= 5)
            return DragMode.ResizeL;
        if ((media || durable) && Math.Abs(px - (x + bw)) <= 5)
            return DragMode.ResizeR;
        return DragMode.Move;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_dragCue is null)
        {
            var over = HitSpanChip(p) is null ? HitBlock(p) : null;
            if (over is not null && EdgeModeAt(over, p.X) != DragMode.Move)
                Cursor = Cursors.SizeWE;
            else
                Cursor = HitSpanChip(p) is not null || over is not null
                    || HitSpan(p) is not null
                    ? Cursors.SizeAll : Cursors.Arrow;
            return;
        }
        if (!_dragMoved &&
            Math.Abs(p.X - _dragStart.X) < 4 && Math.Abs(p.Y - _dragStart.Y) < 4)
            return;
        _dragMoved = true;

        double dSec = Math.Round(
            (p.X - _dragStart.X) / _pxPerSec / SnapSeconds) * SnapSeconds;

        switch (_dragMode)
        {
            case DragMode.Move:
                // Se permite "negativo" en la vista previa: arrastrar sobre el
                // cue anterior = solapar (al soltar se encadena en paralelo)
                _dragPreWait = _dragOrigPreWait + dSec;
                // Vertical: fijar la pista (los grupos solo se mueven en tiempo)
                if (!_dragIsGroup && Math.Abs(p.Y - _dragStart.Y) > LayerH * 0.5)
                    _dragLayer = Math.Clamp(
                        (int)((p.Y - RulerH) / LayerH), 0, MaxLayers - 1);
                break;

            case DragMode.ResizeL:
                // Deslizar el punto de entrada: el borde derecho no se mueve
                double dl = Math.Max(dSec,
                    Math.Max(-_dragOrigTrimStart, -_dragOrigPreWait));
                dl = Math.Min(dl, _dragOrigEff - 0.1);
                _dragPreWait = _dragOrigPreWait + dl;
                _dragTrimStart = _dragOrigTrimStart + dl;
                break;

            case DragMode.ResizeR:
                if (_dragCue.IsMedia())
                {
                    double ne = _dragOrigTrimEnd + dSec;
                    ne = Math.Max(ne, _dragTrimStart + 0.1);
                    if (_dragOrigDuration > 0)
                        ne = Math.Min(ne, _dragOrigDuration);
                    _dragTrimEnd = ne;
                }
                else
                {
                    _dragDuration = Math.Max(0.1, _dragOrigDuration + dSec);
                }
                break;
        }

        // Reproyección en vivo: el resto del show se desplaza con el arrastre
        RebuildLayout();
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_dragCue is null)
            return;
        var cue = _dragCue;
        var mode = _dragMode;
        bool isGroup = _dragIsGroup;
        bool moved = _dragMoved;
        double preWait = _dragPreWait;
        int layer = _dragLayer;
        double trimStart = _dragTrimStart, trimEnd = _dragTrimEnd;
        double duration = _dragDuration;
        // Pista donde el usuario VE el bloque al soltar (incluye el apilado
        // automático del layout del arrastre)
        int shownLayer = _blocks.FirstOrDefault(b => b.Cue.Id == cue.Id)?.Layer ?? -1;
        _dragCue = null;
        ReleaseMouseCapture();

        // Proyección LIMPIA (sin el arrastre): base para detectar cruces
        RebuildLayout();

        if (moved)
        {
            switch (mode)
            {
                case DragMode.Move:
                    if (!TryReorder(cue, preWait - _dragOrigPreWait))
                    {
                        bool linked = false;
                        double committed = preWait < -1e-9
                            ? LinkOverlapWithPrev(cue, preWait, shownLayer, out linked)
                            : preWait;
                        if (Math.Abs(committed - _dragOrigPreWait) > 1e-9)
                            CommitField?.Invoke(cue.Id, "pre_wait",
                                Math.Max(0, committed));
                        // Si el solape ya fijó las pistas, no pisarlas aquí
                        if (!isGroup && !linked && layer != _dragOrigLayer)
                            CommitField?.Invoke(cue.Id, "timeline_layer", layer);
                    }
                    break;
                case DragMode.ResizeL:
                    if (Math.Abs(preWait - _dragOrigPreWait) > 1e-9)
                        CommitField?.Invoke(cue.Id, "pre_wait", preWait);
                    if (Math.Abs(trimStart - _dragOrigTrimStart) > 1e-9)
                        CommitField?.Invoke(cue.Id, "trim_start", trimStart);
                    break;
                case DragMode.ResizeR:
                    if (cue.IsMedia())
                        CommitField?.Invoke(cue.Id, "trim_end", trimEnd);
                    else
                        CommitField?.Invoke(cue.Id, "duration", duration);
                    break;
            }
        }
        RefreshFromModel();
    }

    /// <summary>El bloque se soltó ANTES de su punto de disparo (montado sobre
    /// el cue anterior): encadenarlos en PARALELO — el anterior pasa a
    /// AUTO_CONTINUE y este cue recibe el pre_wait que lo deja exactamente
    /// donde se soltó. Es lo que hace que "las capas se junten" en el timeline.
    /// Dentro de un grupo TIMELINE los hijos ya son paralelos: ahí solo se
    /// recorta el offset a 0 (encadenar duplicaría disparos).</summary>
    private double LinkOverlapWithPrev(Cue cue, double draggedPre, int shownLayer,
        out bool linked)
    {
        linked = false;
        if (_model is null)
            return 0;

        // Hijo de grupo TIMELINE: el paralelismo ya existe vía pre_wait
        if (cue.ParentId is int pid &&
            _model.GetById(pid) is { Type: CueType.Group, GroupMode: GroupMode.Timeline })
            return Math.Max(0, draggedPre);

        var siblings = _model.Cues.Where(c => c.ParentId == cue.ParentId).ToList();
        int pos = siblings.FindIndex(c => c.Id == cue.Id);
        if (pos <= 0
            || !_startById.TryGetValue(cue.Id, out double cleanStart)
            || !_startById.TryGetValue(siblings[pos - 1].Id, out double prevStart))
            return 0;

        var prev = siblings[pos - 1];
        double desired = cleanStart + (draggedPre - _dragOrigPreWait);
        double newTrigger = prevStart + prev.PostWait;   // disparo con AUTO_CONTINUE

        if (prev.ContinueMode != ContinueMode.AutoContinue)
            CommitField?.Invoke(prev.Id, "continue_mode", "AUTO_CONTINUE");
        linked = true;

        // Dos VÍDEOS solapados: fijar AMBOS en las pistas donde se ven — si
        // quedaran en pista "auto" irían los dos a la misma superficie y el
        // segundo sustituiría al primero en vez de convivir/mezclarse.
        if (cue.Type == CueType.Video && prev.Type == CueType.Video)
        {
            int prevShown = _blocks.FirstOrDefault(b => b.Cue.Id == prev.Id)?.Layer ?? 2;
            if (prev.TimelineLayer < 0)
                CommitField?.Invoke(prev.Id, "timeline_layer", prevShown);
            int prevEff = prev.TimelineLayer >= 0 ? prev.TimelineLayer : prevShown;
            int mine = shownLayer >= 0 ? shownLayer : prevEff - 1;
            if (mine == prevEff)
                mine = prevEff > 0 ? prevEff - 1 : prevEff + 1;
            mine = Math.Clamp(mine, 0, MaxLayers - 1);
            if (cue.TimelineLayer != mine)
                CommitField?.Invoke(cue.Id, "timeline_layer", mine);
        }

        return Math.Max(0, desired - newTrigger);
    }

    /// <summary>Si el bloque (o grupo) se soltó más allá de su hermano
    /// anterior/siguiente, reordena la lista y devuelve true. Es el "viceversa"
    /// de la correlación lista↔timeline. Compara contra la proyección SIN el
    /// arrastre: en una cadena secuencial los vecinos posteriores se desplazan
    /// junto al bloque y nunca se cruzarían.</summary>
    private bool TryReorder(Cue cue, double delta)
    {
        if (_model is null || Math.Abs(delta) < 0.05)
            return false;

        var siblings = _model.Cues.Where(c => c.ParentId == cue.ParentId).ToList();
        int pos = siblings.FindIndex(c => c.Id == cue.Id);
        if (pos < 0 || !_startById.TryGetValue(cue.Id, out double myStart))
            return false;
        double droppedStart = Math.Max(0, myStart + delta);

        if (delta < 0 && pos > 0)
        {
            var prev = siblings[pos - 1];
            if (_startById.TryGetValue(prev.Id, out double prevStart)
                && droppedStart < prevStart)
            {
                MoveCueRequested?.Invoke(
                    _model.IndexOfId(cue.Id), _model.IndexOfId(prev.Id));
                return true;
            }
        }
        else if (delta > 0 && pos < siblings.Count - 1)
        {
            var next = siblings[pos + 1];
            if (_startById.TryGetValue(next.Id, out double nextStart)
                && droppedStart > nextStart)
            {
                // Aterrizar DESPUÉS del bloque completo del vecino (grupos incl.)
                int nextRow = _model.IndexOfId(next.Id);
                int toRow = Math.Min(_model.Count - 1,
                    _model.GroupExtent(nextRow) - 1);
                MoveCueRequested?.Invoke(_model.IndexOfId(cue.Id), toRow);
                return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------
    // Picos de audio para la mini-onda (decodificación en segundo plano)
    // ------------------------------------------------------------------

    private void EnsurePeaks(string path)
    {
        if (_peaks.ContainsKey(path) || !_peaksLoading.Add(path))
            return;
        Task.Run(() =>
        {
            (float[] Mins, float[] Maxs, double Dur) result;
            try
            {
                var buf = WinCue.Audio.AudioDecoder.Decode(path);
                const int buckets = 300;
                int frames = buf.FrameCount, ch = buf.Channels;
                int n = Math.Max(1, Math.Min(buckets, frames));
                var mins = new float[n];
                var maxs = new float[n];
                for (int bkt = 0; bkt < n; bkt++)
                {
                    long f0 = (long)bkt * frames / n;
                    long f1 = (long)(bkt + 1) * frames / n;
                    float mn = 0, mx = 0;
                    for (long f = f0; f < f1; f++)
                    {
                        float sum = 0;
                        long idx = f * ch;
                        for (int c = 0; c < ch; c++)
                            sum += buf.Samples[idx + c];
                        float v = sum / ch;
                        if (v < mn) mn = v;
                        if (v > mx) mx = v;
                    }
                    mins[bkt] = mn;
                    maxs[bkt] = mx;
                }
                result = (mins, maxs, buf.DurationSeconds);
            }
            catch (Exception)
            {
                // Sin onda (archivo ilegible): marcar para no reintentar en bucle
                result = (Array.Empty<float>(), Array.Empty<float>(), 0);
            }
            Dispatcher.BeginInvoke(() =>
            {
                _peaks[path] = result;
                InvalidateVisual();
            });
        });
    }

    // ------------------------------------------------------------------
    // Menú contextual (botón derecho)
    // ------------------------------------------------------------------

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (_model is null)
            return;
        var p = e.GetPosition(this);
        Cue? cue = HitSpanChip(p)?.Group ?? HitBlock(p)?.Cue ?? HitSpan(p)?.Group;

        var menu = new ContextMenu();
        bool edit = IsEditAllowed?.Invoke() ?? true;
        if (cue is not null)
        {
            _selectedId = cue.Id;
            CueSelected?.Invoke(cue.Id);
            InvalidateVisual();

            bool active = _activeElapsed.ContainsKey(cue.Id) ||
                (cue.Type == CueType.Group && _activeElapsed.Keys.Any(id =>
                    _model.GetById(id) is Cue c && _model.IsDescendant(c, cue.Id)));
            menu.Items.Add(MainWindow.Mi("▶ Disparar", "",
                () => TriggerRequested?.Invoke(cue.Id)));
            menu.Items.Add(MainWindow.Mi("■ Detener", "",
                () => StopRequested?.Invoke(cue.Id), active));
            menu.Items.Add(new Separator());
            if (cue.TimelineLayer >= 0)
                menu.Items.Add(MainWindow.Mi("📌 Soltar a capa automática", "",
                    () => CommitField?.Invoke(cue.Id, "timeline_layer", -1), edit));
            menu.Items.Add(MainWindow.ColorSubmenu(
                color => CommitField?.Invoke(cue.Id, "color", color), edit));
            menu.Items.Add(MainWindow.Mi(cue.Armed ? "✕ Desarmar" : "✓ Armar", "",
                () => CommitField?.Invoke(cue.Id, "armed", !cue.Armed), edit));
        }
        else
        {
            menu.Items.Add(MainWindow.Mi("Ajustar a la ventana", "", ZoomToFit));
            menu.Items.Add(MainWindow.Mi("Acercar", "Ctrl+rueda", () => ZoomStep(1.4)));
            menu.Items.Add(MainWindow.Mi("Alejar", "Ctrl+rueda", () => ZoomStep(1 / 1.4)));
        }
        menu.PlacementTarget = this;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    private static Pen FrozenPen(Color c, double width)
    {
        var p = new Pen(new SolidColorBrush(c), width);
        p.Freeze();
        return p;
    }
}
