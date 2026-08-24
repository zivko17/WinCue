// =============================================================================
// TimelineCanvas.cs — timeline del show en Win2D, ya INTERACTIVO.
// =============================================================================
// Segunda pasada del traslado del timeline a Secuence (ver el encargo,
// hitos-0-1). La primera dejó regla, pistas, bloques, playhead y aviso de
// solapamiento; esta añade la interacción: seleccionar (bidireccional con la
// tabla), arrastrar bloques en tiempo y pista, redimensionar por los bordes
// (trim/duración), zoom con Ctrl+rueda y scroll con la rueda.
//
// El CÁLCULO — proyección, geometría, zoom/scroll (TimelineViewport),
// hit-test (TimelineHitTest), el gesto con su umbral/snap/topes
// (TimelineDragGesture) y la política del soltar (TimelineEditing.CommitDrag:
// reordenar, encadenar en paralelo, anclar a los demás, reconciliar
// crossfades) — es EXACTAMENTE el que usa WinCue: vive en WinCue.Presentation
// sin un solo tipo de framework de interfaz. Este archivo solo traduce
// punteros de WinUI 3 y pinta — mismo reparto que VuMeterCanvas.cs.
//
// UserControl + CanvasControl (no herencia): CanvasControl sigue sellada — ver
// la cabecera de VuMeterCanvas.cs para la razón completa.
//
// SIMPLIFICACIONES DELIBERADAS DE ESTA PASADA (documentadas, no descuidos):
//   · La etiqueta del bloque YA lleva sus prefijos ↗/∞/📌: Win2D no resuelve
//     la fuente de iconos por NOMBRE ni por "ms-appx:///" (eso lanza), pero sí
//     por un URI de ARCHIVO — IconFont.Win2DFamily, comprobado al arrancar.
//     El aviso de solapamiento sigue siendo la marca triangular lisa.
//   · Los recuadros de grupo (spans) y los marcadores GO YA SE PINTAN, con el
//     mismo reparto que WinCue (relleno debajo, marco y asa por ENCIMA de los
//     bloques, para que con un solo hijo el bloque no tape el recuadro).
//     AGARRAR el grupo por su asa sigue pendiente: el hit-test compartido
//     (TimelineHitTest.SpanChip) ya está listo y probado, falta el gesto.
//   · Sin cursor de edición, scrub, doble clic ni menú contextual: el menú
//     contextual de Secuence es un pendiente aparte del encargo, y el doble
//     clic (inspector / reproducir desde la regla) irá con él.
// =============================================================================

using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.UI;
using WinCue.Controller;
using WinCue.Core;
using WinCue.Presentation;

namespace Secuence.App;

/// <summary>Timeline interactivo en Win2D. Equivalente de
/// <c>WinCue.App.TimelinePanel</c> (WPF) para WinUI 3: regla, pistas, bloques
/// con color y etiqueta, playhead, selección, arrastre (tiempo/pista/bordes)
/// y zoom — con el cálculo compartido de WinCue.Presentation.</summary>
public sealed class TimelineCanvas : UserControl
{
    private readonly CanvasControl _canvas = new();
    private readonly TimelinePlayhead _playhead = new();

    // Zoom y scroll: estado y operaciones compartidos con WinCue
    // (TimelineViewport) — zoom anclado al puntero, fit, topes del scroll.
    private readonly TimelineViewport _vp = new();

    private CueModel? _model;
    private TimelineProjection _projection = new();
    private bool _fitDone;
    private bool _renderHooked;
    private DispatcherQueueTimer? _frameTimer;

    private int _selectedId = -1;

    // Arrastre en curso (umbral, snap y topes compartidos). null = sin gesto.
    private TimelineDragGesture? _drag;

    /// <summary>Commit de un campo (cueId, campo, valor) → el producto lo
    /// cablea a MainController.OnCueEditCommitted. Mismo contrato que
    /// WinCue.App.TimelineCanvas.CommitField.</summary>
    public Action<int, string, object?>? CommitField;

    /// <summary>Reordenar la lista (fila origen, fila destino) → OnMoveCue.</summary>
    public Action<int, int>? MoveCueRequested;

    /// <summary>true → el timeline GESTIONA los fades al arrastrar (solape =
    /// crossfade; separar = quitarlo) — mismo interruptor 🔀 que WinCue.</summary>
    public bool AutoCrossfade = true;

    /// <summary>En modo SHOW se selecciona pero no se edita (igual que la
    /// tabla). Sin delegado, se permite editar.</summary>
    public Func<bool>? IsEditAllowed;

    /// <summary>Clic en un bloque: selección hacia la tabla (y viceversa,
    /// ver SetSelected).</summary>
    public event Action<int>? CueSelected;

    // ── Cursor de EDICIÓN ──────────────────────────────────────────────
    // Ámbar y arrastrable: se coloca pinchando en la regla, se agarra cerca de
    // su línea, y con doble clic reproduce el show desde ahí. Las dos cuentas
    // que hay detrás —a qué fotograma mirar y qué arranca— son compartidas
    // (WinCue.Presentation.TimelineCursor).
    private double? _editCursor;
    private bool _dragCursor;

    /// <summary>Scrub: el cue de vídeo bajo el cursor (o null) y su posición
    /// DENTRO del archivo. Mismo contrato que WinCue.App.TimelinePanel.
    /// EditScrubbed: MainWindow lo enseña pausado en el PVW, que es el visor
    /// de preparación — mirar ahí no toca la sala.</summary>
    public event Action<Cue?, double>? EditScrubbed;

    /// <summary>Reproducir desde el cursor: la lista de cues del segmento con
    /// su offset o su espera.</summary>
    public Action<IReadOnlyList<MainController.TimelinePlayItem>>? PlayFromRequested;

    public TimelineCanvas()
    {
        MinHeight = 90;
        Content = _canvas;
        _canvas.Draw += OnDraw;
        _canvas.SizeChanged += (_, _) =>
        {
            _fitDone = false;
            _canvas.Invalidate();
        };
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerCaptureLost += (_, _) => CancelDrag();
        _canvas.PointerCanceled += (_, _) => CancelDrag();
        _canvas.PointerWheelChanged += OnPointerWheel;
        _canvas.DoubleTapped += OnDoubleTapped;
        Unloaded += (_, _) => _frameTimer?.Stop();
    }

    /// <summary>Reloj del show (el del controlador). Sin él, el playhead no
    /// extrapola entre ticks — mismo contrato que TimelinePanel.Clock.</summary>
    public IShowClock? Clock { get; set; }

    public void SetModel(CueModel model)
    {
        _model = model;
        _fitDone = false;
        RefreshFromModel();
    }

    /// <summary>Recalcula la proyección tras cualquier cambio del modelo.
    /// Mismo cálculo compartido que WinCue — TimelineLayout.Project(), con la
    /// vista previa del arrastre en curso si lo hay.</summary>
    public void RefreshFromModel()
    {
        _projection = _model is null
            ? new TimelineProjection()
            : TimelineLayout.Project(_model, _drag?.ToOverride());
        _canvas.Invalidate();
    }

    /// <summary>Zoom desde los botones de la cabecera: ancla en el CENTRO,
    /// no bajo el puntero (ahí no hay puntero). Mismo factor y mismo ancla
    /// que WinCue.App.TimelineCanvas.ZoomStep.</summary>
    public void ZoomStep(double factor)
    {
        _vp.ZoomAt(factor, _canvas.ActualWidth / 2);
        _canvas.Invalidate();
    }

    /// <summary>Que quepa el show entero, como el botón «Ajustar» de WinCue.</summary>
    public void ZoomToFit()
    {
        _vp.ZoomToFit(TimelineLayout.TotalSeconds(_projection.Blocks), _canvas.ActualWidth);
        _canvas.Invalidate();
    }

    /// <summary>Segundos que le QUEDAN al vídeo SALIENTE de la misma pista
    /// cuando este cue entra, según la proyección. null = no hay saliente vivo
    /// (no hay cruce) o el saliente no acaba nunca (loop). Es el dato que
    /// convierte "fade_in de 8 s" en "el cruce no cabe" — lo consume el aviso
    /// de fundido del inspector, igual que en WinCue.</summary>
    public double? OutgoingRemaining(int cueId) =>
        TimelineLayout.OutgoingRemaining(_projection.Blocks, cueId);

    /// <summary>Selección desde la tabla (-1 = nada): resalta el bloque y lo
    /// trae a la vista si quedó fuera — mismo contrato que WinCue.</summary>
    public void SetSelected(int cueId)
    {
        if (_selectedId == cueId)
            return;
        _selectedId = cueId;
        if (_projection.StartById.TryGetValue(cueId, out double start))
            _vp.EnsureVisible(start, _canvas.ActualWidth);
        _canvas.Invalidate();
    }

    /// <summary>Cues activos del tick del controlador (playhead). Mismo
    /// reparto que TimelinePanel.SetActive: repinta a 60 Hz SOLO mientras hay
    /// actividad.</summary>
    public void SetActive(IReadOnlyList<ActiveCueInfo> rows)
    {
        bool wasEmpty = !_playhead.HasActive;
        _playhead.SetActive(rows, Clock?.Now ?? 0.0);
        HookRender(_playhead.HasActive);
        if (!(wasEmpty && rows.Count == 0))
            _canvas.Invalidate();
    }

    private void HookRender(bool on)
    {
        if (on == _renderHooked)
            return;
        _renderHooked = on;
        if (!on)
        {
            _frameTimer?.Stop();
            return;
        }
        _frameTimer ??= _canvas.DispatcherQueue?.CreateTimer();
        if (_frameTimer is null)
        {
            _renderHooked = false;   // sin DispatcherQueue todavía: reintentará en el próximo tick
            return;
        }
        _frameTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / 60.0);
        _frameTimer.Tick -= OnFrameTick;
        _frameTimer.Tick += OnFrameTick;
        _frameTimer.Start();
    }

    private void OnFrameTick(DispatcherQueueTimer sender, object args) => _canvas.Invalidate();

    // ------------------------------------------------------------------
    // Interacción: seleccionar, arrastrar y zoom (cálculo compartido)
    // ------------------------------------------------------------------

    private double LayerH => TimelineLayout.LayerHeight(
        _canvas.ActualHeight, TimelineLayout.VisibleLayers(_projection.Blocks));

    /// <summary>Formato del rótulo del asa. Se crea al vuelo en vez de vivir
    /// en un campo porque CanvasTextFormat es descartable y este control no
    /// lo es (CA1001); la medida ya aloja un CanvasTextLayout de todas
    /// formas.</summary>
    private static CanvasTextFormat SpanFormat() =>
        new() { FontSize = 10, FontFamily = IconFont.Win2DFamily };

    /// <summary>Asa de grupo bajo el punto (la del grupo MÁS INTERNO, que lo
    /// decide el cálculo compartido). El ancho del rótulo lo mide Win2D:
    /// CanvasControl es un ICanvasResourceCreator, así que se puede medir
    /// fuera de una sesión de dibujo.</summary>
    private TimelineSpan? HitSpanChip(Point p)
    {
        if (_projection.Spans.Count == 0)
            return null;
        using var format = SpanFormat();
        return TimelineHitTest.SpanChip(_projection.Spans, p.X, p.Y,
            _vp.PxPerSec, _vp.ScrollX, LayerH,
            span => MeasureText(_canvas, TimelineLayout.SpanLabelText(span), format));
    }

    /// <summary>Cuerpo del recuadro de un grupo bajo el punto.</summary>
    private TimelineSpan? HitSpan(Point p) =>
        TimelineHitTest.Span(_projection.Spans, p.X, p.Y,
            _vp.PxPerSec, _vp.ScrollX, LayerH);

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_model is null)
            return;
        var pt = e.GetCurrentPoint(_canvas);
        if (!pt.Properties.IsLeftButtonPressed)
            return;
        var p = pt.Position;

        // Cursor de EDICIÓN, ANTES que nada: pinchar en la regla lo coloca, y
        // cerca de su línea lo agarra. Solo en modo EDIT, igual que WinCue —
        // en SHOW el timeline es de solo lectura.
        if (IsEditAllowed?.Invoke() ?? true)
        {
            bool onRuler = p.Y < TimelineLayout.RulerH;
            bool nearCursor = _editCursor is double c
                && Math.Abs(_vp.SecToX(c) - p.X) <= 6;
            if (onRuler || nearCursor)
            {
                _dragCursor = true;
                _canvas.CapturePointer(e.Pointer);
                UpdateEditCursor(p.X);
                return;
            }
        }

        // Prioridad: ASA del grupo → bloque → CUERPO del recuadro. La misma
        // que WinCue.App.TimelinePanel.OnMouseDown, y en ese orden por algo:
        // el asa está por encima de los bloques justo para poder agarrar un
        // grupo de un solo hijo, y el cuerpo va el último para que agarrar
        // dentro del recuadro no le gane a los bloques que contiene.
        TimelineBlock? block = null;
        Cue cue;
        bool isGroup;
        if (HitSpanChip(p) is TimelineSpan chipSpan)
        {
            cue = chipSpan.Group;
            isGroup = true;
        }
        else if (TimelineHitTest.Block(_projection.Blocks, p.X, p.Y,
                     _vp.PxPerSec, _vp.ScrollX, LayerH) is TimelineBlock hit)
        {
            cue = hit.Cue;
            isGroup = false;
            block = hit;
        }
        else if (HitSpan(p) is TimelineSpan bodySpan)
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

        // En modo SHOW se selecciona pero no se edita (igual que la tabla)
        if (!(IsEditAllowed?.Invoke() ?? true))
        {
            _canvas.Invalidate();
            return;
        }

        // El gesto (umbral, snap, topes, anclas) es cálculo compartido:
        // ¿agarró un borde? → redimensionar en vez de mover
        var mode = TimelineHitTest.EdgeMode(block, p.X, _vp.PxPerSec, _vp.ScrollX);
        _drag = new TimelineDragGesture(cue, isGroup, mode,
            block, p.X, p.Y, _projection.StartById);

        _canvas.CapturePointer(e.Pointer);
        _canvas.Invalidate();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_canvas).Position;

        if (_dragCursor)
        {
            UpdateEditCursor(p.X);
            return;
        }

        if (_drag is null)
        {
            // Cursor según lo que hay debajo, como WPF: ↔ en un borde
            // redimensionable, ✥ sobre algo agarrable, flecha en el vacío.
            // Sobre el ASA de un grupo NO se mira el borde: un asa no se
            // redimensiona, se arrastra entera (mismo criterio que WinCue).
            bool onChip = HitSpanChip(p) is not null;
            var over = onChip
                ? null
                : TimelineHitTest.Block(_projection.Blocks, p.X, p.Y,
                    _vp.PxPerSec, _vp.ScrollX, LayerH);
            InputSystemCursorShape shape;
            if (over is not null
                && TimelineHitTest.EdgeMode(over, p.X, _vp.PxPerSec, _vp.ScrollX)
                    != TimelineDragMode.Move)
                shape = InputSystemCursorShape.SizeWestEast;
            else if (onChip || over is not null || HitSpan(p) is not null)
                shape = InputSystemCursorShape.SizeAll;
            else
                shape = InputSystemCursorShape.Arrow;
            ProtectedCursor = InputSystemCursor.Create(shape);
            return;
        }

        // El gesto aplica el umbral, el snap y los topes (cálculo compartido)
        _drag.Update(p.X, p.Y, _vp.PxPerSec, LayerH);
        if (!_drag.Moved)
            return;

        // Reproyección en vivo: el resto del show se desplaza con el arrastre
        RefreshFromModel();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragCursor)
        {
            _dragCursor = false;
            _canvas.ReleasePointerCaptures();
            return;
        }
        if (_drag is null)
            return;
        var g = _drag;
        _drag = null;
        _canvas.ReleasePointerCaptures();

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

    /// <summary>El capture se perdió a medio gesto (cambio de foco, pantalla
    /// táctil…): descartar la vista previa sin escribir nada.</summary>
    private void CancelDrag()
    {
        _dragCursor = false;
        if (_drag is null)
            return;
        _drag = null;
        RefreshFromModel();
    }

    /// <summary>Mueve el cursor de edición y emite el SCRUB. A qué fotograma
    /// mirar lo decide el cálculo compartido (TimelineCursor.ScrubTarget).</summary>
    private void UpdateEditCursor(double px)
    {
        double t = Math.Max(0, _vp.XToSec(px));
        _editCursor = t;
        var (video, mediaPos) = TimelineCursor.ScrubTarget(_projection.Blocks, t);
        EditScrubbed?.Invoke(video, mediaPos);
        _canvas.Invalidate();
    }

    /// <summary>Doble clic en la regla (o sobre el cursor): reproducir el show
    /// desde ahí. Va por DoubleTapped y no contando pulsaciones a mano porque
    /// WinUI 3 no da ClickCount en los eventos de puntero como WPF.</summary>
    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_model is null || !(IsEditAllowed?.Invoke() ?? true))
            return;
        var p = e.GetPosition(_canvas);
        bool onRuler = p.Y < TimelineLayout.RulerH;
        bool nearCursor = _editCursor is double c
            && Math.Abs(_vp.SecToX(c) - p.X) <= 6;
        if (!onRuler && !nearCursor)
            return;

        UpdateEditCursor(p.X);
        if (_editCursor is double t)
            PlayFromRequested?.Invoke(
                TimelineCursor.PlayItems(_projection.Blocks, _projection.GoMarks, t));
        e.Handled = true;
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_canvas);
        _vp.Wheel(pt.Properties.MouseWheelDelta,
            e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control),
            pt.Position.X, TimelineLayout.TotalSeconds(_projection.Blocks),
            _canvas.ActualWidth);
        _canvas.Invalidate();
        e.Handled = true;
    }

    // ------------------------------------------------------------------
    // Pintado
    // ------------------------------------------------------------------

    /// <summary>Caja del recuadro de un grupo, en píxeles. El cálculo es
    /// compartido (TimelineLayout.SpanBoxRect); esto solo lo pasa al Rect de
    /// WinUI.</summary>
    private Rect SpanBox(TimelineSpan span, double layerHeight)
    {
        var r = TimelineLayout.SpanBoxRect(span, _vp.PxPerSec, _vp.ScrollX, layerHeight);
        return new Rect(r.X, r.Y, r.Width, r.Height);
    }

    /// <summary>Caja del asa. También es cálculo compartido
    /// (TimelineHitTest.SpanChipRect) salvo el ANCHO DEL TEXTO, que solo lo
    /// sabe medir el framework de quien pinta — aquí, Win2D.</summary>
    private Rect SpanChipBox(ICanvasResourceCreator rc, TimelineSpan span,
                             double layerHeight, string text, CanvasTextFormat format)
    {
        var r = TimelineHitTest.SpanChipRect(span, _vp.PxPerSec, _vp.ScrollX,
            layerHeight, MeasureText(rc, text, format));
        return new Rect(r.X, r.Y, r.Width, r.Height);
    }

    /// <summary>Ancho del texto. Toma un ICanvasResourceCreator y no la sesión
    /// de dibujo para que valga TAMBIÉN desde el hit-test, que corre fuera de
    /// OnDraw — el propio CanvasControl lo es.</summary>
    private static double MeasureText(ICanvasResourceCreator rc, string text, CanvasTextFormat format)
    {
        using var layout = new CanvasTextLayout(rc, text, format, float.MaxValue, float.MaxValue);
        return layout.LayoutBounds.Width;
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        double w = sender.ActualWidth, h = sender.ActualHeight;
        ds.Clear(ToColor(Palette.TimelineBg));
        if (w <= 0 || h <= 0 || _model is null)
            return;

        var blocks = _projection.Blocks;

        if (!_fitDone && w > 0 && blocks.Count > 0)
        {
            _fitDone = true;
            _vp.Fit(TimelineLayout.TotalSeconds(blocks), w);
        }

        int layers = TimelineLayout.VisibleLayers(blocks);
        double lh = TimelineLayout.LayerHeight(h, layers);

        // Fondo de pistas alternado + NÚMERO de pista, como WinCue: sin el
        // número, «vertical: pista» no dice a qué pista has llevado el bloque.
        using var layerFormat = new CanvasTextFormat { FontSize = 9 };
        for (int l = 0; l < layers; l++)
        {
            double y = TimelineLayout.RulerH + l * lh;
            var brush = l % 2 == 0 ? ToColor(Palette.TimelineLayerA) : ToColor(Palette.TimelineLayerB);
            ds.FillRectangle(new Rect(0, y, w, lh), brush);
            ds.DrawText($"{l + 1}", 4, (float)(y + 2), ToColor(Palette.TextFaint), layerFormat);
        }

        // Regla de tiempo
        ds.FillRectangle(new Rect(0, 0, w, TimelineLayout.RulerH), ToColor(Palette.TimelineRulerBg));
        using var tickFormat = new CanvasTextFormat { FontSize = 10 };
        foreach (double t in TimelineLayout.RulerTicks(_vp.PxPerSec, _vp.ScrollX, w))
        {
            float x = (float)_vp.SecToX(t);
            ds.DrawLine(new Vector2(x, (float)(TimelineLayout.RulerH - 6)), new Vector2(x, (float)TimelineLayout.RulerH),
                ToColor(Palette.TimelineTick));
            ds.DrawLine(new Vector2(x, (float)TimelineLayout.RulerH), new Vector2(x, (float)h),
                ToColor(Palette.TimelineTickMinor));
            ds.DrawText(TimelineLayout.FormatTime(t), x + 3, 2, ToColor(Palette.TextDim), tickFormat);
        }

        if (blocks.Count == 0)
            return;

        // ── Recuadros de grupo: RELLENO, debajo de los bloques ──
        // La caja y su etiqueta son cálculo COMPARTIDO (TimelineLayout.
        // SpanBoxRect / SpanLabelText); aquí solo se pinta. El orden es el de
        // WinCue: relleno → marcas GO → bloques → marcos y asas.
        using var spanFormat = new CanvasTextFormat
        { FontSize = 10, FontFamily = IconFont.Win2DFamily };
        foreach (var span in _projection.Spans)
        {
            var box = SpanBox(span, lh);
            if (box.Right < 0 || box.Left > w)
                continue;
            uint color = TimelinePalette.ResolveCueColor(span.Group);
            ds.FillRoundedRectangle(box, 4, 4, ToColor(color, 30));
        }

        // ── Marcadores GO: a partir de ahí el tiempo es ESTIMADO ──
        // El rótulo lleva el glifo delante, como en WinCue. Win2D no resuelve
        // "ms-appx://…" —eso lanza ArgumentException— pero sí un URI de
        // ARCHIVO: IconFont.Win2DFamily.
        using var goFormat = new CanvasTextFormat
        { FontSize = 9, FontFamily = IconFont.Win2DFamily };
        foreach (double t in _projection.GoMarks)
        {
            float x = (float)_vp.SecToX(t);
            if (x < -20 || x > w)
                continue;
            ds.DrawLine(new Vector2(x, (float)TimelineLayout.RulerH), new Vector2(x, (float)h),
                ToColor(Palette.Go), 1.4f);
            // El glifo y la palabra van JUNTOS en el mismo texto porque aquí
            // se pinta con Win2D contra la fuente de iconos, no con un
            // TextBlock: la trampa del cuadradito es de los controles de
            // XAML, no de esto.
            ds.DrawText($"{Icons.Go}GO", x + 2, (float)(h - 15),
                ToColor(Palette.Go), goFormat);
        }

        // Bloques (el arrastrado, encima — como WPF)
        foreach (var b in blocks)
            if (_drag is null || b.Cue.Id != _drag.Cue.Id)
                DrawBlock(ds, b, lh, w);
        if (_drag is not null)
        {
            var dragBlock = blocks.FirstOrDefault(b => !b.Ghost && b.Cue.Id == _drag.Cue.Id);
            if (dragBlock is not null)
                DrawBlock(ds, dragBlock, lh, w);
        }

        // ── Recuadros de grupo: MARCO + ASA, POR ENCIMA de los bloques ──
        // Encima a propósito: con un solo hijo, el bloque taparía el recuadro
        // y no quedaría de dónde agarrar el grupo (misma razón que en WinCue).
        foreach (var span in _projection.Spans)
        {
            var box = SpanBox(span, lh);
            if (box.Right < 0 || box.Left > w)
                continue;
            uint color = TimelinePalette.ResolveCueColor(span.Group);
            bool selected = span.Group.Id == _selectedId;
            ds.DrawRoundedRectangle(box, 4, 4,
                selected ? ToColor(Palette.White) : ToColor(color, 200),
                selected ? 2f : 1.6f);

            string text = TimelineLayout.SpanLabelText(span);
            var chip = SpanChipBox(ds, span, lh, text, spanFormat);
            ds.FillRoundedRectangle(chip, 3, 3, ToColor(color, 235));
            // Recortado al asa: un nombre largo no puede salirse del grupo.
            using var layout = new CanvasTextLayout(ds, text, spanFormat,
                (float)Math.Max(1, chip.Width - 10), (float)chip.Height)
            {
                TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
                TrimmingSign = CanvasTrimmingSign.Ellipsis,
            };
            ds.DrawTextLayout(layout, (float)(chip.X + 5), (float)(chip.Y + 1),
                ToColor(Palette.White));
        }

        // ── Cursor de EDICIÓN: línea ámbar, asa triangular en la regla y la
        //    hora al lado. Mismo aspecto que el de WinCue. ──
        if (_editCursor is double ec)
        {
            float x = (float)_vp.SecToX(ec);
            if (x >= -10 && x <= w + 10)
            {
                var amber = ToColor(Palette.Standby);
                ds.DrawLine(new Vector2(x, 0), new Vector2(x, (float)h), amber, 2f);
                using var handle = CanvasGeometry.CreatePolygon(ds, new[]
                {
                    new Vector2(x - 6, 0), new Vector2(x + 6, 0), new Vector2(x, 12),
                });
                ds.FillGeometry(handle, amber);
                using var timeFormat = new CanvasTextFormat { FontSize = 10 };
                ds.DrawText(TimelineLayout.FormatTime(ec), x + 5, 2, amber, timeFormat);
            }
        }

        // Playhead: cálculo compartido (WinCue.Presentation.TimelinePlayhead)
        double? cursor = _playhead.CursorSeconds(blocks, Clock?.Now ?? 0.0);
        if (cursor is double cs)
        {
            float x = (float)_vp.SecToX(cs);
            if (x >= 0 && x <= w)
                ds.DrawLine(new Vector2(x, 0), new Vector2(x, (float)h), ToColor(Palette.Pgm), 2f);
        }
    }

    private void DrawBlock(CanvasDrawingSession ds, TimelineBlock b, double lh, double w)
    {
        var geom = TimelineLayout.BlockGeometry(b, _vp.PxPerSec, _vp.ScrollX, lh);
        var r = geom.Rect;
        if (r.X + r.Width < 0 || r.X > w)
            return;
        var rect = new Rect(r.X, r.Y, r.Width, r.Height);

        uint colorToken = TimelinePalette.ResolveCueColor(b.Cue);
        var top = ToColor(colorToken);
        var bottom = ToColor(TimelinePalette.Darken(colorToken, TimelinePalette.BlockGradientDarken));

        using var layer = ds.CreateLayer(b.Ghost ? 0.45f : 1f);

        using (var fill = MakeVerticalGradient(ds, top, bottom, rect))
        using (var geometry = CanvasGeometry.CreateRoundedRectangle(ds, rect, 3, 3))
        {
            ds.FillGeometry(geometry, fill);

            if (b.Dimmed)
                ds.FillGeometry(geometry, ToColor(Palette.Black, 90));

            // Selección: borde blanco, como el SelectedPen de WPF
            if (!b.Ghost && b.Cue.Id == _selectedId)
                ds.DrawGeometry(geometry, ToColor(Palette.White), 1.5f);
        }

        // Indicador de solapamiento (aviso de fundido incoherente): sale del
        // propio cálculo (TimelineBlock.Issues, vía FadeSanity). Marca
        // triangular lisa — ver la cabecera del archivo para el porqué de no
        // usar aquí el glifo ⚠ de Icons.Warning.
        if (b.Issues != FadeIssues.None && rect.Width > 14)
        {
            const double size = 8;
            bool atStart = b.Issues.HasFlag(FadeIssues.CrossfadeTooLong)
                || b.Issues.HasFlag(FadeIssues.FadeInTooLong);
            double wx = atStart ? rect.X + 2 : rect.X + rect.Width - size - 2;
            using var warn = WarningTriangle(ds, wx, rect.Y + 2, size);
            ds.FillGeometry(warn, ToColor(Palette.Warn));
        }

        if (rect.Width > 30)
        {
            // Etiqueta COMPARTIDA (TimelineLayout.BlockLabelText): trae los
            // prefijos ↗ fantasma, ∞/×N repeticiones y 📌 pista fijada. Antes
            // se construía a mano y sin ellos porque Win2D no resolvía la
            // fuente de iconos; con IconFont.Win2DFamily ya se puede.
            string text = TimelineLayout.BlockLabelText(b);
            using var format = new CanvasTextFormat
            {
                FontSize = 11,
                FontFamily = IconFont.Win2DFamily,
                WordWrapping = CanvasWordWrapping.NoWrap,
                TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
                TrimmingSign = CanvasTrimmingSign.Ellipsis,
            };
            using var layout = new CanvasTextLayout(ds, text, format,
                (float)Math.Max(1, rect.Width - 10), (float)rect.Height);
            double ty = rect.Y + (rect.Height - layout.LayoutBounds.Height) / 2;
            ds.DrawTextLayout(layout, (float)(rect.X + 5), (float)ty, ToColor(Palette.TextBright));
        }
    }

    private static CanvasLinearGradientBrush MakeVerticalGradient(
        ICanvasResourceCreator creator, Color top, Color bottom, Rect rect)
    {
        var stops = new[]
        {
            new CanvasGradientStop { Position = 0f, Color = top },
            new CanvasGradientStop { Position = 1f, Color = bottom },
        };
        return new CanvasLinearGradientBrush(creator, stops)
        {
            StartPoint = new Vector2((float)rect.X, (float)rect.Y),
            EndPoint = new Vector2((float)rect.X, (float)(rect.Y + rect.Height)),
        };
    }

    /// <summary>Marca triangular lisa apuntando hacia arriba, en un cuadrado
    /// de lado <paramref name="size"/> con esquina superior izquierda en
    /// (x, y) — el mismo hueco donde WinCue pinta el glifo ⚠.</summary>
    private static CanvasGeometry WarningTriangle(ICanvasResourceCreator creator, double x, double y, double size)
    {
        using var pathBuilder = new CanvasPathBuilder(creator);
        pathBuilder.BeginFigure((float)x, (float)(y + size));
        pathBuilder.AddLine((float)(x + size), (float)(y + size));
        pathBuilder.AddLine((float)(x + size / 2), (float)y);
        pathBuilder.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(pathBuilder);
    }

    private static Color ToColor(uint rgb, byte alpha = 0xff) => Color.FromArgb(
        alpha, (byte)((rgb >> 16) & 0xff), (byte)((rgb >> 8) & 0xff), (byte)(rgb & 0xff));
}
