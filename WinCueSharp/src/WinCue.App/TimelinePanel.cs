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
//     bloque en vertical lo fija a mano en esa pista (timeline_layer 0-7)
// Interacción: arrastre horizontal = pre_wait (snap 0,1 s; el resto del show
// se desplaza en vivo); arrastrar más allá del vecino = reordenar la lista;
// clic = seleccionar en la tabla (y viceversa); Ctrl+rueda = zoom. Cursor
// rojo y progreso verde con los ActiveCueInfo reales del controlador.
//
// Segunda pasada del traslado a WinCue.Presentation: el CÁLCULO de la
// interacción también es compartido — TimelineViewport (zoom/scroll),
// TimelineHitTest (qué hay bajo el punto), TimelineDragGesture (el arrastre
// con su umbral, snap y topes) y TimelineEditing.CommitDrag (qué se escribe
// al soltar). Aquí quedan el ratón/teclado de WPF, el pintado, el cursor de
// edición con su scrub y el menú contextual.
// =============================================================================

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinCue.Controller;
using WinCue.Core;
using WinCue.Presentation;
// El CÁLCULO de la proyección (bloques, recuadros de grupo, arrastre en
// curso) vive en WinCue.Presentation.TimelineLayout (ver el encargo,
// hitos-0-1): estos alias dejan que TODO el código de interacción de más
// abajo (arrastre, hit-test, menú contextual) siga escribiendo "Block"/
// "Span"/"DragMode" tal cual, sin un solo cambio de nombre, sobre los tipos
// AHORA compartidos con Secuence.
using Block = WinCue.Presentation.TimelineBlock;
using Span = WinCue.Presentation.TimelineSpan;
using DragMode = WinCue.Presentation.TimelineDragMode;

namespace WinCue.App;

/// <summary>Cabecera (zoom) + lienzo de la línea de tiempo.</summary>
public sealed class TimelinePanel : Border
{
    private readonly TimelineCanvas _canvas = new();
    private CheckBox _chkXfade = null!;
    private bool _xfadeLoading;

    /// <summary>Notifica el cambio del interruptor (para persistirlo).</summary>
    public Action<bool>? AutoCrossfadeToggled;

    /// <summary>Fija el estado del crossfade automático (desde settings).</summary>
    public void SetAutoCrossfade(bool enabled)
    {
        _xfadeLoading = true;
        _chkXfade.IsChecked = enabled;
        _canvas.AutoCrossfade = enabled;
        _xfadeLoading = false;
    }

    private void OnXfadeToggle(bool enabled)
    {
        _canvas.AutoCrossfade = enabled;
        if (!_xfadeLoading)
            AutoCrossfadeToggled?.Invoke(enabled);
    }

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

    /// <summary>Scrub del cursor de edición (cue de vídeo o null, posición s).</summary>
    public event Action<Cue?, double>? EditScrubbed
    {
        add => _canvas.EditScrubbed += value;
        remove => _canvas.EditScrubbed -= value;
    }

    /// <summary>Menú contextual: disparar un cue por id.</summary>
    public Action<int>? TriggerRequested
    {
        get => _canvas.TriggerRequested;
        set => _canvas.TriggerRequested = value;
    }

    /// <summary>Reproducir el show desde el cursor de edición (doble clic en
    /// la regla / menú): lista de cues del segmento con su offset o espera.</summary>
    public Action<IReadOnlyList<MainController.TimelinePlayItem>>? PlayFromRequested
    {
        get => _canvas.PlayFromRequested;
        set => _canvas.PlayFromRequested = value;
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
        // Gris de la barra del mezclador: el MISMO valor que ya tenía (#202022)
        Background = new SolidColorBrush(Theme.MeterBgAlt);

        var header = new DockPanel { Margin = new Thickness(6, 3, 6, 2) };

        var title = new TextBlock
        {
            Text = $"{Icons.Timeline} {Loc.T("TIMELINE DEL SHOW", "SHOW TIMELINE")}",
            FontWeight = FontWeights.Bold,
            Foreground = Theme.StandbyBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 12, 0),
        };
        header.Children.Add(title);

        var right = new StackPanel { Orientation = Orientation.Horizontal };
        _chkXfade = new CheckBox
        {
            Content = $"{Icons.Crossfade} {Loc.T("Crossfade al solapar", "Crossfade on overlap")}",
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Foreground = Theme.TextTertiaryBrush,
            FontSize = 11,
            ToolTip = Loc.T(
                "ACTIVADO: dos medias consecutivas de la MISMA capa que se " +
                "solapan se cruzan (fade out/in = solape; el vídeo funde " +
                "por opacidad con el compositor GPU) y al separarlas se " +
                "quita. Las capas distintas son independientes: nunca se " +
                "funden solas — usa clic derecho → \"Crossfade con…\" " +
                "para linkarlas.\n" +
                "DESACTIVADO: el timeline no toca ningún fade (todo a mano " +
                "en el inspector).",
                "ON: two consecutive clips on the SAME layer that overlap " +
                "crossfade (fade out/in = overlap; the video fades by " +
                "opacity with the GPU compositor) and separating them " +
                "removes it. Different layers are independent: they never " +
                "fade on their own — use right-click → \"Crossfade with…\" " +
                "to link them.\n" +
                "OFF: the timeline touches no fade (all by hand in the " +
                "inspector)."),
        };
        _chkXfade.Checked += (_, _) => OnXfadeToggle(true);
        _chkXfade.Unchecked += (_, _) => OnXfadeToggle(false);
        right.Children.Add(_chkXfade);
        right.Children.Add(HeaderButton(Icons.ZoomOut, () => _canvas.ZoomStep(1 / 1.4)));
        right.Children.Add(HeaderButton(Icons.ZoomIn, () => _canvas.ZoomStep(1.4)));
        right.Children.Add(HeaderButton(Loc.T("Ajustar", "Fit"), () => _canvas.ZoomToFit()));
        DockPanel.SetDock(right, Dock.Right);
        header.Children.Add(right);

        var hint = new TextBlock
        {
            Text = Loc.T(
                "arrastrar: tiempo · sobre el anterior = SOLAPAR (paralelo) · " +
                $"vertical: pista · Ctrl+rueda: zoom · {Icons.Go}GO = disparo manual",
                "drag: time · over the previous = OVERLAP (parallel) · " +
                $"vertical: track · Ctrl+wheel: zoom · {Icons.Go}GO = manual trigger"),
            Foreground = Theme.TextDimmerBrush,
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
            Background = Theme.ButtonBgBrush,
            Foreground = Theme.TextPrimaryBrush,
            BorderBrush = Theme.BorderBrush,
            FontSize = 12,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    public void SetModel(CueModel model) => _canvas.SetModel(model);

    /// <summary>Reloj del show (el del controlador): el cursor extrapola entre
    /// ticks con la MISMA base de tiempo con la que se disparan los cues.</summary>
    public IShowClock? Clock
    {
        get => _canvas.Clock;
        set => _canvas.Clock = value;
    }

    /// <summary>Selección de la tabla → resaltar (y hacer visible) el bloque.</summary>
    public void SetSelected(int cueId) => _canvas.SetSelected(cueId);

    /// <summary>Cues activos del tick del controlador (cursor + progreso).</summary>
    public void SetActive(IReadOnlyList<ActiveCueInfo> rows) => _canvas.SetActive(rows);

    /// <summary>Recalcular la proyección tras cualquier cambio del modelo.</summary>
    public void RefreshFromModel() => _canvas.RefreshFromModel();

    /// <summary>Segundos que le quedan al vídeo SALIENTE de la misma pista
    /// cuando el cue dado entra (null = no hay cruce). Lo consume el aviso de
    /// fundido incoherente del inspector.</summary>
    public double? OutgoingRemaining(int cueId) => _canvas.OutgoingRemaining(cueId);
}

/// <summary>Lienzo: proyección temporal de la lista, bloques, grupos y cursor.</summary>
public sealed class TimelineCanvas : FrameworkElement
{
    // RulerH, MaxLayers, MinLayers e InstantFootprint viven ahora en
    // TimelineLayout (WinCue.Presentation): son parte del CÁLCULO compartido,
    // no de esta interfaz. Los alias locales de solo lectura evitan reescribir
    // los ~40 sitios que ya los usaban por su nombre corto.
    private const double RulerH = TimelineLayout.RulerH;
    private const int MaxLayers = TimelineLayout.MaxLayers;

    private static readonly Brush BgBrush = Frozen(new SolidColorBrush(Theme.TimelineBg));
    private static readonly Brush LayerABrush = Frozen(new SolidColorBrush(Theme.TimelineLayerA));
    private static readonly Brush LayerBBrush = Frozen(new SolidColorBrush(Theme.TimelineLayerB));
    private static readonly Brush RulerBg = Frozen(new SolidColorBrush(Theme.TimelineRulerBg));
    private static readonly Brush TickBrush = Frozen(new SolidColorBrush(Theme.TextDim));
    private static readonly Pen TickPen = FrozenPen(Theme.TimelineTick, 1);
    private static readonly Pen TickMinorPen = FrozenPen(Theme.TimelineTickMinor, 1);
    private static readonly Pen PlayheadPen = FrozenPen(Theme.Pgm, 2);
    private static readonly Pen EditCursorPen = FrozenPen(Theme.Standby, 2);
    private static readonly Brush EditCursorBrush = Frozen(new SolidColorBrush(Theme.Standby));
    private static readonly Pen SelectedPen = FrozenPen(Theme.White, 1.5);
    private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Theme.TextBright));
    private static readonly Brush FadeBrush = Frozen(new SolidColorBrush(Theme.Alpha(Theme.Black, 90)));
    private static readonly Brush MsgBrush = Frozen(new SolidColorBrush(Theme.TextFaint));
    // Ámbar de AVISO (fundido que no cabe): el mismo tono del inspector
    private static readonly Brush WarnBrush = Frozen(new SolidColorBrush(Theme.Warn));
    private static readonly Brush ProgressBrush = Frozen(new SolidColorBrush(Theme.Alpha(Theme.Go, 110)));
    private static readonly Brush GoBrush = Frozen(new SolidColorBrush(Theme.Go));
    private static readonly Pen GoPen = MakeGoPen();
    private static readonly Pen CutPen = FrozenPen(Theme.Pgm, 3);
    private static readonly Pen GhostPen = MakeGhostPen();

    private static Pen MakeGhostPen()
    {
        var p = new Pen(new SolidColorBrush(Theme.Alpha(Theme.TextPrimary, 200)), 1)
        { DashStyle = new DashStyle(new double[] { 3, 2 }, 0) };
        p.Freeze();
        return p;
    }

    private static Pen MakeGoPen()
    {
        var p = new Pen(new SolidColorBrush(Theme.Alpha(Theme.Go, 150)), 1)
        { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
        p.Freeze();
        return p;
    }

    public Action<int, string, object?>? CommitField;
    public Action<int, int>? MoveCueRequested;
    public Action<int>? TriggerRequested;
    public Action<int>? StopRequested;
    public Action<IReadOnlyList<MainController.TimelinePlayItem>>? PlayFromRequested;

    /// <summary>true → el timeline GESTIONA los fades al arrastrar (solape =
    /// crossfade; separar = quitarlo). false → nunca toca los fades: capas
    /// libres sin fundido (los fades se editan a mano en el inspector).</summary>
    public bool AutoCrossfade = true;
    public Func<bool>? IsEditAllowed;
    public event Action<int>? CueSelected;

    /// <summary>Doble clic en un bloque: abrir ese cue en el inspector.</summary>
    public event Action<int>? CueActivated;

    /// <summary>Scrub del cursor de edición: (cue de vídeo bajo el cursor o
    /// null, posición dentro del media en s). La vista lo manda al PVW.</summary>
    public event Action<Cue?, double>? EditScrubbed;

    private CueModel? _model;
    private int _selectedId = -1;

    // Cursor de EDICIÓN (ámbar): se coloca/arrastra libremente en modo EDIT
    private double? _editCursor;
    private bool _dragCursor;

    // Zoom y scroll: estado y operaciones compartidos con Secuence
    // (TimelineViewport, WinCue.Presentation) — zoom anclado al puntero,
    // fit, topes del scroll y auto-desplazamiento al seleccionar.
    private readonly TimelineViewport _vp = new();
    private double _pxPerSec => _vp.PxPerSec;
    private double _scrollX => _vp.ScrollX;
    private bool _fitDone;

    // Proyección calculada de la lista: Block/Span son ahora alias de
    // WinCue.Presentation.TimelineBlock/TimelineSpan (ver los "using" al
    // principio del archivo) — el CÁLCULO que los produce vive en
    // TimelineLayout.Project(), no aquí. Ver RebuildLayout más abajo.
    private readonly List<Block> _blocks = new();
    private readonly List<Span> _spans = new();
    private readonly List<double> _goMarks = new();
    private readonly Dictionary<int, double> _startById = new();   // cue → inicio proyectado

    // Cues activos (cursor + progreso), del tick real del controlador.
    // El tick llega a 10 Hz → saltos de varios píxeles: entre ticks se
    // EXTRAPOLA con el RELOJ DEL SHOW y se repinta por frame (60 Hz) vía
    // CompositionTarget.Rendering, solo mientras hay cues activos.
    // El reloj es el del controlador (ShowClock), no un Stopwatch propio: el
    // cursor y el disparo de los cues se miden con la misma vara, y con el
    // show en pausa el reloj se congela y el cursor deja de deslizar solo.
    //
    // El CÁLCULO (extrapolación, persecución del valor dibujado, posición del
    // cursor) vive en WinCue.Presentation.TimelinePlayhead — mismo motivo que
    // TimelineLayout: es EXACTAMENTE lo que hoy pinta Secuence también. Sin
    // reloj (_clock null), se pasa 0.0 en los dos puntos de contacto (aquí y
    // en SetActive) para que la resta quede siempre en 0 y el resultado sea
    // el mismo "sin extrapolar" que devolvía el LiveElapsed original.
    private readonly TimelinePlayhead _playhead = new();
    private IShowClock? _clock;
    private bool _renderHooked;

    /// <summary>Reloj del show (el del controlador). Sin él, el cursor no
    /// extrapola entre ticks: se dibuja el valor crudo, nunca uno inventado.</summary>
    public IShowClock? Clock
    {
        get => _clock;
        set => _clock = value;
    }

    private double DrawnElapsed(int cueId, double raw) =>
        _playhead.DrawnElapsed(cueId, raw, _clock?.Now ?? 0.0);

    private void HookRender(bool on)
    {
        if (on == _renderHooked)
            return;
        _renderHooked = on;
        if (on)
            System.Windows.Media.CompositionTarget.Rendering += OnFrameRendering;
        else
            System.Windows.Media.CompositionTarget.Rendering -= OnFrameRendering;
    }

    private void OnFrameRendering(object? sender, EventArgs e) => InvalidateVisual();

    // Arrastre (mover bloque/grupo, o redimensionar por los bordes): el
    // estado del gesto — umbral, snap, topes — es cálculo compartido
    // (TimelineDragGesture, WinCue.Presentation). null = sin gesto en curso.
    private TimelineDragGesture? _drag;

    // Mini-onda de los bloques de audio (picos por archivo, calculados aparte)
    private readonly Dictionary<string, (float[] Mins, float[] Maxs, double Dur)> _peaks = new();
    private readonly HashSet<string> _peaksLoading = new();
    private static readonly Pen WavePen = FrozenPen(Theme.Alpha(Theme.White, 110), 1);

    public TimelineCanvas()
    {
        ClipToBounds = true;
        MinHeight = 90;
    }

    /// <summary>Alto de cada pista: reparte el alto disponible (estilo Watchout).</summary>
    private double LayerH => TimelineLayout.LayerHeight(ActualHeight, VisibleLayers());

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
        bool wasEmpty = !_playhead.HasActive;
        _playhead.SetActive(rows, _clock?.Now ?? 0.0);
        // Repintado por frame SOLO mientras hay actividad (60 Hz ≈ cursor fluido)
        HookRender(_playhead.HasActive);
        if (!(wasEmpty && rows.Count == 0))
            InvalidateVisual();
    }

    public void RefreshFromModel()
    {
        RebuildLayout();
        InvalidateVisual();
    }

    /// <summary>Segundos que le QUEDAN al vídeo SALIENTE de la misma pista
    /// cuando este cue entra, según la proyección del timeline. null = no hay
    /// saliente vivo (no hay cruce) o el saliente no acaba nunca (loop).
    /// Es el dato que convierte "fade_in de 8 s" en "el cruce no cabe".</summary>
    public double? OutgoingRemaining(int cueId) => TimelineLayout.OutgoingRemaining(_blocks, cueId);

    // ------------------------------------------------------------------
    // Proyección: lista de cues → bloques con tiempo y capa
    // ------------------------------------------------------------------
    // El CÁLCULO (recorrido de la jerarquía, efectos de los cues de control,
    // asignación de pistas, avisos de fundido) vive en
    // WinCue.Presentation.TimelineLayout.Project() — ver el encargo,
    // hitos-0-1. Esta clase solo arma el TimelineDragOverride con el gesto en
    // curso (null fuera de un arrastre) y pinta lo que Project() devuelve.
    // Es el mismo cálculo, exacto, que usa Secuence — ni un número repetido.

    private void RebuildLayout()
    {
        _blocks.Clear();
        _spans.Clear();
        _goMarks.Clear();
        _startById.Clear();
        if (_model is null)
            return;

        var proj = TimelineLayout.Project(_model, _drag?.ToOverride());
        _blocks.AddRange(proj.Blocks);
        _spans.AddRange(proj.Spans);
        _goMarks.AddRange(proj.GoMarks);
        foreach (var kv in proj.StartById)
            _startById[kv.Key] = kv.Value;
    }

    /// <summary>trim_start efectivo durante un arrastre de ResizeL, para la
    /// mini-onda del bloque (decoración de PINTADO, fuera del alcance de esta
    /// pasada: la mini-onda no entra en el cálculo compartido — ver el
    /// encargo, hitos-0-1 — así que sigue resuelta aquí, tal cual antes.</summary>
    private double CurrentTrimStart(Cue cue) =>
        _drag is { Mode: DragMode.ResizeL } d && d.Cue.Id == cue.Id
            ? d.TrimStart
            : cue.TrimStart;

    private int VisibleLayers() => TimelineLayout.VisibleLayers(_blocks);

    private double TotalSeconds() => TimelineLayout.TotalSeconds(_blocks);

    private double SecToX(double sec) => TimelineLayout.SecToX(sec, _pxPerSec, _scrollX);
    private double XToSec(double x) => TimelineLayout.XToSec(x, _pxPerSec, _scrollX);

    // ------------------------------------------------------------------
    // Zoom / scroll
    // ------------------------------------------------------------------

    public void ZoomStep(double factor) => ZoomAt(factor, ActualWidth / 2);

    private void ZoomAt(double factor, double anchorX)
    {
        _vp.ZoomAt(factor, anchorX);
        InvalidateVisual();
    }

    public void ZoomToFit()
    {
        _vp.ZoomToFit(TotalSeconds(), ActualWidth);
        InvalidateVisual();
    }

    private void EnsureVisible(int cueId)
    {
        if (_startById.TryGetValue(cueId, out double start))
            _vp.EnsureVisible(start, ActualWidth);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        _vp.Wheel(e.Delta, Keyboard.Modifiers == ModifierKeys.Control,
            e.GetPosition(this).X, TotalSeconds(), ActualWidth);
        InvalidateVisual();
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
            _vp.Fit(TotalSeconds(), w);
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
            var fill = new SolidColorBrush(Theme.Alpha(color, 30));
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
            dc.DrawText(Label($"{Icons.Go}GO", GoBrush, 9), new Point(x + 2, h - 13));
        }

        // Bloques (el arrastrado, encima)
        foreach (var b in _blocks.Where(b => _drag is null || b.Cue.Id != _drag.Cue.Id))
            DrawBlock(dc, b, lh);
        var dragBlock = _drag is null
            ? null
            : _blocks.FirstOrDefault(b => !b.Ghost && b.Cue.Id == _drag.Cue.Id);
        if (dragBlock is not null)
            DrawBlock(dc, dragBlock, lh);

        // Recuadros de grupo: marco + ASA con el nombre, POR ENCIMA de los
        // bloques (con un solo hijo el bloque taparía el recuadro y no habría
        // de dónde agarrar el grupo)
        foreach (var span in _spans)
            DrawSpanFrame(dc, span, lh, w);

        // Cue de control SELECCIONADO: línea hasta su objetivo (un START
        // apunta a su instancia fantasma; STOP/FADE al bloque que afectan)
        if (_selectedId >= 0 && _model?.GetById(_selectedId) is Cue selCtl
            && selCtl.TargetsOther() && selCtl.TargetId is int selTid)
        {
            var src = _blocks.FirstOrDefault(b => !b.Ghost && b.Cue.Id == _selectedId);
            var dst = (selCtl.Type == CueType.Start
                    ? _blocks.FirstOrDefault(b => b.Ghost && b.Cue.Id == selTid)
                    : null)
                ?? _blocks.FirstOrDefault(b => b.Cue.Id == selTid);
            if (src is not null && dst is not null)
                dc.DrawLine(GoPen,
                    new Point(SecToX(src.Start) + 6, RulerH + src.Layer * lh + lh / 2),
                    new Point(SecToX(dst.Start) + 6, RulerH + dst.Layer * lh + lh / 2));
        }

        // Cursor: posición real más avanzada de los cues sonando (limitada al
        // final de su bloque: con trim, el contador nunca debe salirse de él).
        // Los FANTASMAS de un START no cuentan: comparten id con su cue real
        // y teletransportarían el cursor al final del show. Cálculo compartido
        // (WinCue.Presentation.TimelinePlayhead) — es el mismo playhead de Secuence.
        double? cursor = _playhead.CursorSeconds(_blocks, _clock?.Now ?? 0.0);
        if (cursor is double cs)
        {
            double x = SecToX(cs);
            if (x >= 0 && x <= w)
            {
                dc.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, h));
                dc.DrawText(Label(FmtTime(cs), Frozen(
                    new SolidColorBrush(Theme.Danger)), 10),
                    new Point(x + 4, RulerH + 1));
            }
        }

        // Cursor de EDICIÓN (ámbar, arrastrable): scrub del show
        if (_editCursor is double ec)
        {
            double x = SecToX(ec);
            if (x >= -10 && x <= w + 10)
            {
                dc.DrawLine(EditCursorPen, new Point(x, 0), new Point(x, h));
                // Asa triangular en la regla
                dc.DrawGeometry(EditCursorBrush, null, Triangle(
                    new Point(x - 6, 0), new Point(x + 6, 0), new Point(x, 12)));
                dc.DrawText(Label(FmtTime(ec), EditCursorBrush, 10),
                    new Point(x + 5, 2));
            }
        }
    }

    /// <summary>Geometría del recuadro de un grupo: caja, asa (chip con el
    /// nombre) y color. La misma para pintar y para el hit-test del arrastre.</summary>
    private (FormattedText Lbl, Rect Chip, Rect Box, Color Color)
        SpanGeometry(Span span, double lh)
    {
        var group = span.Group;
        // Color: cálculo compartido (WinCue.Presentation.TimelinePalette) —
        // el mismo que resuelve la columna «Tipo» de la tabla, sin que este
        // sitio pueda volver a llevar su propia copia del switch.
        var color = ToColor(TimelinePalette.ResolveCueColor(group));

        var boxRect = TimelineLayout.SpanBoxRect(span, _pxPerSec, _scrollX, lh);
        var box = new Rect(boxRect.X, boxRect.Y, boxRect.Width, boxRect.Height);

        var lbl = Label(TimelineLayout.SpanLabelText(span),
            Frozen(new SolidColorBrush(Colors.White)), 10);
        // Caja del asa: cálculo compartido (solo el ancho del texto lo mide
        // WPF — el framework de quien pinta, ver TimelineHitTest.SpanChipRect)
        var chipRect = TimelineHitTest.SpanChipRect(span, _pxPerSec, _scrollX,
            lh, lbl.WidthIncludingTrailingWhitespace);
        lbl.MaxTextWidth = Math.Max(1, chipRect.Width - 10);
        lbl.MaxLineCount = 1;
        lbl.Trimming = TextTrimming.CharacterEllipsis;
        var chip = new Rect(chipRect.X, chipRect.Y, chipRect.Width, chipRect.Height);
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
            border = new Pen(new SolidColorBrush(Theme.Alpha(color, 200)), 1.6);
            border.Freeze();
        }
        dc.DrawRoundedRectangle(null, border, box, 4, 4);

        // Asa: chip sólido con el nombre — de aquí se arrastra el grupo entero
        var chipFill = new SolidColorBrush(Theme.Alpha(color, 235));
        chipFill.Freeze();
        dc.DrawRoundedRectangle(chipFill, null, chip, 3, 3);
        dc.DrawText(lbl, new Point(chip.X + 5, chip.Y + 1));
    }

    /// <summary>Asa de grupo bajo el punto (la del grupo más interno).</summary>
    private Span? HitSpanChip(Point p) =>
        TimelineHitTest.SpanChip(_spans, p.X, p.Y, _pxPerSec, _scrollX, LayerH,
            span => Label(TimelineLayout.SpanLabelText(span),
                Frozen(new SolidColorBrush(Colors.White)), 10)
                .WidthIncludingTrailingWhitespace);

    private void DrawBlock(DrawingContext dc, Block b, double lh)
    {
        var cue = b.Cue;
        // Geometría (posición, ancho recortado por un CutAt) y color: cálculo
        // compartido con Secuence — ver TimelineLayout.BlockGeometry y
        // TimelinePalette.ResolveCueColor.
        var geom = TimelineLayout.BlockGeometry(b, _pxPerSec, _scrollX, lh);
        double dur = geom.DrawnDuration;
        double x = geom.Rect.X;
        double y = geom.Rect.Y;
        double bh = geom.Rect.Height;
        bool instant = geom.Instant;
        double bw = geom.Rect.Width;

        if (x + bw < 0 || x > ActualWidth)
            return;

        var baseColor = ToColor(TimelinePalette.ResolveCueColor(cue));
        var fill = new LinearGradientBrush(baseColor,
            ToColor(TimelinePalette.Darken(TimelinePalette.ResolveCueColor(cue), TimelinePalette.BlockGradientDarken)), 90);
        fill.Freeze();

        if (b.Ghost)
            dc.PushOpacity(0.45);   // instancia lanzada por un START

        Pen? border = b.Ghost ? GhostPen : cue.Id == _selectedId ? SelectedPen : null;
        var rect = new Rect(x, y, bw, bh);
        dc.DrawRoundedRectangle(fill, border, rect, 3, 3);

        if (b.Dimmed)
            dc.DrawRoundedRectangle(FadeBrush, null, rect, 3, 3);

        // Progreso real dentro del bloque mientras suena
        if (!instant && _playhead.TryGetElapsed(cue.Id, out double rawElapsed))
        {
            double elapsed = DrawnElapsed(cue.Id, rawElapsed);
            double pw = Math.Clamp(elapsed / Math.Max(0.001, dur), 0, 1) * bw;
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
                double t0 = CurrentTrimStart(cue);
                double mid = y + bh / 2, half = bh / 2 - 2;
                int n = pk.Mins.Length;
                double px0 = Math.Max(x, 0), px1 = Math.Min(x + bw, ActualWidth);
                for (double px = px0; px < px1; px += 2)
                {
                    double sec = t0 + (px - x) / bw * dur;
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

        // Rampa de un cue FADE que actúa sobre este bloque
        if (!instant && b.RampStart >= 0)
        {
            double rx0 = x + Math.Clamp((b.RampStart - b.Start), 0, dur) * _pxPerSec;
            double rx1 = x + Math.Clamp((b.RampStart - b.Start + b.RampLen), 0, dur) * _pxPerSec;
            if (rx1 - rx0 > 2)
                dc.DrawGeometry(FadeBrush, null, Triangle(
                    new Point(rx1, y), new Point(rx0, y), new Point(rx1, y + bh)));
            // FADE a volumen 0 sin parar: lo que sigue queda en silencio
            if (b.RampToSilence && x + bw - rx1 > 2)
                dc.DrawRectangle(FadeBrush, null,
                    new Rect(rx1, y, x + bw - rx1, bh));
        }

        // Tope rojo del corte (STOP / FADE con parada)
        if (b.CutAt is not null)
            dc.DrawLine(CutPen, new Point(x + bw, y - 1), new Point(x + bw, y + bh + 1));

        // AVISO SUAVE de fundido incoherente: un ⚠ ámbar sobre la esquina del
        // fundido que no cabe. No cambia nada del comportamiento — la
        // explicación está en el tooltip y en el inspector.
        if (b.Issues != FadeIssues.None && bw > 14)
        {
            var warn = Label(Icons.Warning, WarnBrush, 12);
            bool atStart = b.Issues.HasFlag(FadeIssues.CrossfadeTooLong)
                || b.Issues.HasFlag(FadeIssues.FadeInTooLong);
            double wx = atStart ? x + 2 : x + bw - warn.Width - 2;
            dc.DrawText(warn, new Point(wx, y + 1));
        }

        if (bw > 30)
        {
            string text = TimelineLayout.BlockLabelText(b);
            var label = Label(text, LabelBrush, 11);
            label.MaxTextWidth = Math.Max(1, bw - 10);
            label.MaxLineCount = 1;
            label.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(label, new Point(x + 5, y + (bh - label.Height) / 2));
        }

        if (b.Ghost)
            dc.Pop();
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

    /// <summary>Separación de las marcas de la regla — cálculo compartido
    /// (TimelineLayout.RulerStep). La escalera llega hasta las 4 horas porque
    /// el zoom puede alejarse mucho más que antes: con el tope anterior
    /// (10 min) un show largo pintaba una marca cada 12 px y la regla se
    /// convertía en un borrón de números encimados.</summary>
    private double RulerStep() => TimelineLayout.RulerStep(_pxPerSec);

    private static string FmtTime(double s) => TimelineLayout.FormatTime(s);

    // IconFont.Font y no "Segoe UI": estas etiquetas llevan glifos de icono
    // mezclados con el texto (el tipo de cue, la marca de GO). FormattedText no
    // hereda la fuente por defecto de la app — es dibujo directo, fuera del
    // árbol visual — así que aquí hay que nombrarla. La fuente es compuesta:
    // el icono sale de Phosphor y las letras de Segoe UI, como antes.
    private static FormattedText Label(string text, Brush brush, double size) => new(
        text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface(IconFont.Font, FontStyles.Normal, FontWeights.Normal,
                     FontStretches.Normal), size, brush, 1.0);

    /// <summary>Token de color de Palette (0xRRGGBB) → Color de WPF. Traduce
    /// lo que TimelinePalette resuelve en WinCue.Presentation, sin que ese
    /// proyecto necesite saber qué es un System.Windows.Media.Color.</summary>
    private static Color ToColor(uint rgb) => Color.FromRgb(
        (byte)((rgb >> 16) & 0xff), (byte)((rgb >> 8) & 0xff), (byte)(rgb & 0xff));

    // ------------------------------------------------------------------
    // Interacción: seleccionar, arrastrar (tiempo/pista) y reordenar
    // ------------------------------------------------------------------

    // Hit-test: cálculo compartido (TimelineHitTest, WinCue.Presentation) —
    // el pintado encima gana, los fantasmas no se editan, el asa del grupo
    // más interno primero. Aquí solo se traduce el Point de WPF.
    private Block? HitBlock(Point p) =>
        TimelineHitTest.Block(_blocks, p.X, p.Y, _pxPerSec, _scrollX, LayerH);

    /// <summary>Recuadro de grupo bajo el punto (el más interno si hay anidados).</summary>
    private Span? HitSpan(Point p) =>
        TimelineHitTest.Span(_spans, p.X, p.Y, _pxPerSec, _scrollX, LayerH);

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _model is null)
            return;
        var p = e.GetPosition(this);

        // Cursor de EDICIÓN: clic en la regla lo coloca; cerca de su línea lo
        // agarra. Solo en modo EDIT (en SHOW el timeline es de solo lectura).
        if (IsEditAllowed?.Invoke() ?? true)
        {
            bool onRuler = p.Y < RulerH;
            bool nearCursor = _editCursor is double c
                && Math.Abs(SecToX(c) - p.X) <= 6;
            if (onRuler || nearCursor)
            {
                // Doble clic en la regla = REPRODUCIR desde ese punto
                if (e.ClickCount == 2)
                {
                    UpdateEditCursor(p.X);
                    PlayFromCursor();
                    return;
                }
                _dragCursor = true;
                CaptureMouse();
                UpdateEditCursor(p.X);
                return;
            }
        }

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

        // El gesto (umbral, snap, topes, anclas) es cálculo compartido:
        // ¿agarró un borde? → redimensionar en vez de mover
        _drag = new TimelineDragGesture(cue, isGroup,
            EdgeModeAt(hitBlock, p.X), hitBlock, p.X, p.Y, _startById);

        CaptureMouse();
        InvalidateVisual();
    }

    /// <summary>Modo de arrastre según el punto: bordes (±5 px) = trim/duración
    /// — cálculo compartido (TimelineHitTest.EdgeMode).</summary>
    private DragMode EdgeModeAt(Block? b, double px) =>
        TimelineHitTest.EdgeMode(b, px, _pxPerSec, _scrollX);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_dragCursor)
        {
            UpdateEditCursor(p.X);
            return;
        }
        if (_drag is null)
        {
            var over = HitSpanChip(p) is null ? HitBlock(p) : null;
            if (over is not null && EdgeModeAt(over, p.X) != DragMode.Move)
                Cursor = Cursors.SizeWE;
            else
                Cursor = HitSpanChip(p) is not null || over is not null
                    || HitSpan(p) is not null
                    ? Cursors.SizeAll : Cursors.Arrow;
            UpdateFadeTip(over);
            return;
        }

        // El gesto aplica el umbral, el snap y los topes (cálculo compartido)
        _drag.Update(p.X, p.Y, _pxPerSec, LayerH);
        if (!_drag.Moved)
            return;

        // Reproyección en vivo: el resto del show se desplaza con el arrastre
        RebuildLayout();
        InvalidateVisual();
    }

    /// <summary>Tooltip del bloque bajo el ratón: solo lo pone cuando hay un
    /// aviso de fundido que explicar (si no, el timeline no lleva tooltips).</summary>
    private void UpdateFadeTip(Block? over)
    {
        string? tip = over is null || over.Issues == FadeIssues.None
            ? null
            : FadeSanity.Explain(over.Issues,
                FadeSanity.EffectiveDuration(over.Cue), over.Headroom);
        if (!Equals(ToolTip, tip))
            ToolTip = tip;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_dragCursor)
        {
            _dragCursor = false;
            ReleaseMouseCapture();
            return;
        }
        if (_drag is null)
            return;
        var g = _drag;
        _drag = null;
        ReleaseMouseCapture();

        // Qué escribe el soltar (reordenar, encadenar en paralelo, anclar a
        // los demás, reconciliar crossfades) es política compartida:
        // TimelineEditing.CommitDrag, WinCue.Presentation. Sin gesto real
        // (un clic), no escribe nada.
        if (_model is not null)
            TimelineEditing.CommitDrag(_model, g,
                (id, field, value) => CommitField?.Invoke(id, field, value),
                (from, to) => MoveCueRequested?.Invoke(from, to),
                AutoCrossfade);
        RefreshFromModel();
    }

    /// <summary>Mueve el cursor de edición y hace SCRUB: busca el vídeo de la
    /// pista más alta bajo el cursor y emite su posición dentro del media
    /// (la vista lo enseña pausado en el PVW, fotograma exacto).</summary>
    private void UpdateEditCursor(double px)
    {
        double t = Math.Max(0, XToSec(px));
        _editCursor = t;

        Cue? video = null;
        int bestLayer = int.MaxValue;
        double mediaPos = 0;
        foreach (var b in _blocks)
        {
            if (b.Cue.Type != CueType.Video || b.Dur <= 0 || b.Ghost)
                continue;
            if (t < b.Start || t > b.Start + b.Dur || b.Layer >= bestLayer)
                continue;
            bestLayer = b.Layer;
            video = b.Cue;
            mediaPos = b.Cue.TrimStart + (t - b.Start);
        }
        EditScrubbed?.Invoke(video, mediaPos);
        InvalidateVisual();
    }

    /// <summary>REPRODUCIR desde el cursor de edición: los bloques que cruzan
    /// ese instante arrancan con seek a su offset, y los que empiezan después
    /// (dentro del mismo segmento, hasta el próximo ▸GO manual) se programan
    /// a su hora. Los pre-wait ya están resueltos en la proyección — nada se
    /// queda "esperando el vídeo entero".</summary>
    private void PlayFromCursor()
    {
        if (_editCursor is not double t || _model is null)
            return;

        // Frontera del segmento: el primer salto manual posterior al cursor
        double limit = double.PositiveInfinity;
        foreach (double g in _goMarks)
            if (g > t + 1e-6)
                limit = Math.Min(limit, g);

        var items = new List<MainController.TimelinePlayItem>();
        foreach (var b in _blocks)
        {
            // Ghost: lo dispara su cue START (incluirlo duplicaría)
            if (b.Dimmed || b.Ghost || b.Cue.Type == CueType.Group)
                continue;
            double end = b.Start + Math.Max(b.Dur, 0.0);
            if (end <= t + 1e-6 || b.Start >= limit - 1e-6)
                continue;   // ya pasó · pertenece al siguiente segmento manual
            if (b.Start <= t)
            {
                // Cruza el cursor: solo media arranca a mitad (un WAIT/FADE a
                // medias no aporta — la cadena ya viene resuelta)
                if (b.Cue.Type is CueType.Audio or CueType.Video && b.Dur > 0)
                    items.Add(new MainController.TimelinePlayItem(
                        b.Cue.Id, t - b.Start, 0.0));
            }
            else if (b.Cue.Type is CueType.Audio or CueType.Video
                     or CueType.Fade or CueType.Start or CueType.Stop
                     or CueType.Script)
            {
                items.Add(new MainController.TimelinePlayItem(
                    b.Cue.Id, 0.0, b.Start - t));
            }
        }
        PlayFromRequested?.Invoke(items);
    }

    /// <summary>Crossfade EXPLÍCITO entre capas (menú contextual): el que
    /// empieza antes funde su salida y el que entra funde su entrada, ambos
    /// con la duración del solape. Es el "link" entre capas que el
    /// automático nunca escribe solo.</summary>
    private void ApplyCrossLayerFade(Cue first, Cue second, double overlap)
    {
        double xf = Math.Round(Math.Max(0.1, overlap), 1);
        CommitField?.Invoke(first.Id, "fade_out", xf);
        CommitField?.Invoke(second.Id, "fade_in", xf);
        RefreshFromModel();
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

            bool active = _playhead.IsActive(cue.Id) ||
                (cue.Type == CueType.Group && _playhead.ActiveCueIds.Any(id =>
                    _model.GetById(id) is Cue c && _model.IsDescendant(c, cue.Id)));
            menu.Items.Add(MainWindow.Mi($"{Icons.Play} Disparar", "",
                () => TriggerRequested?.Invoke(cue.Id)));
            menu.Items.Add(MainWindow.Mi($"{Icons.Stop} Detener", "",
                () => StopRequested?.Invoke(cue.Id), active));
            menu.Items.Add(new Separator());
            if (cue.TimelineLayer >= 0)
                menu.Items.Add(MainWindow.Mi($"{Icons.Pin} Soltar a capa automática", "",
                    () => CommitField?.Invoke(cue.Id, "timeline_layer", -1), edit));
            if (cue.IsMedia() && (cue.FadeIn > 0.01 || cue.FadeOut > 0.01))
                menu.Items.Add(MainWindow.Mi($"{Icons.Fade} Quitar fades (in/out) de este cue", "",
                    () =>
                    {
                        CommitField?.Invoke(cue.Id, "fade_in", 0.0);
                        CommitField?.Invoke(cue.Id, "fade_out", 0.0);
                    }, edit));

            // Crossfade EXPLÍCITO con los bloques solapados de OTRAS capas
            // (el automático solo actúa dentro de la misma capa)
            if (cue.IsMedia())
            {
                var mine = _blocks.FirstOrDefault(b => !b.Ghost && b.Cue.Id == cue.Id);
                if (mine is { Dur: > 0 })
                {
                    foreach (var other in _blocks)
                    {
                        if (other.Cue.Id == cue.Id || other.Layer == mine.Layer
                            || !other.Cue.IsMedia() || other.Dur <= 0 || other.Ghost)
                            continue;
                        var first = mine.Start <= other.Start ? mine : other;
                        var second = ReferenceEquals(first, mine) ? other : mine;
                        double overlap = first.Start + first.Dur - second.Start;
                        if (overlap < 0.05)
                            continue;
                        overlap = Math.Min(overlap, Math.Min(first.Dur, second.Dur));
                        double xf = Math.Round(overlap, 1);
                        var f = first.Cue;
                        var s = second.Cue;
                        menu.Items.Add(MainWindow.Mi(
                            $"{Icons.Crossfade} Crossfade con «{other.Cue.Name}» ({xf:0.#} s, capa {other.Layer + 1})",
                            "", () => ApplyCrossLayerFade(f, s, xf), edit));
                    }
                }
            }
            menu.Items.Add(MainWindow.ColorSubmenu(
                color => CommitField?.Invoke(cue.Id, "color", color), edit));
            menu.Items.Add(MainWindow.Mi(
                cue.Armed ? $"{Icons.Cross} Desarmar" : $"{Icons.Check} Armar", "",
                () => CommitField?.Invoke(cue.Id, "armed", !cue.Armed), edit));
        }
        else
        {
            if (_editCursor is not null)
            {
                menu.Items.Add(MainWindow.Mi(
                    $"{Icons.Play} Reproducir desde el cursor", "doble clic en la regla",
                    PlayFromCursor));
                menu.Items.Add(new Separator());
            }
            menu.Items.Add(MainWindow.Mi("Ajustar a la ventana", "", ZoomToFit));
            menu.Items.Add(MainWindow.Mi("Acercar", "Ctrl+rueda", () => ZoomStep(1.4)));
            menu.Items.Add(MainWindow.Mi("Alejar", "Ctrl+rueda", () => ZoomStep(1 / 1.4)));
            if (_editCursor is not null)
                menu.Items.Add(MainWindow.Mi("Quitar cursor de edición", "", () =>
                {
                    _editCursor = null;
                    InvalidateVisual();
                }));
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
