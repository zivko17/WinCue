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
//   · La etiqueta del bloque sigue siendo "número · nombre" a secas — los
//     prefijos ↗/∞/📌 son glifos de Icons (Phosphor) y Win2D/DirectWrite no
//     resuelve por NOMBRE una fuente cargada solo para XAML (ver IconFont.cs).
//     El aviso de solapamiento sigue siendo la marca triangular lisa.
//   · Los recuadros de grupo (spans) y los marcadores GO siguen sin pintarse;
//     por coherencia, aquí tampoco se AGARRAN los grupos (no se puede
//     arrastrar lo que no se ve — en WinCue sí: asa → bloque → marco). El día
//     que se pinten, el hit-test compartido (TimelineHitTest.SpanChip/Span)
//     ya está listo y probado.
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

    /// <summary>Segundos que le QUEDAN al vídeo SALIENTE de la misma pista
    /// cuando este cue entra, según la proyección. null = no hay saliente vivo
    /// (no hay cruce) o el saliente no acaba nunca (loop). Es el dato que
    /// convierte "fade_in de 8 s" en "el cruce no cabe" — lo consume el aviso
    /// de fundido del inspector, igual que en WinCue.</summary>
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

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_model is null)
            return;
        var pt = e.GetCurrentPoint(_canvas);
        if (!pt.Properties.IsLeftButtonPressed)
            return;
        var p = pt.Position;

        // Solo bloques: los recuadros de grupo no se pintan todavía en
        // Secuence, así que tampoco se agarran (ver la cabecera).
        var block = TimelineHitTest.Block(_projection.Blocks, p.X, p.Y,
            _vp.PxPerSec, _vp.ScrollX, LayerH);
        if (block is null)
            return;

        _selectedId = block.Cue.Id;
        CueSelected?.Invoke(block.Cue.Id);

        // En modo SHOW se selecciona pero no se edita (igual que la tabla)
        if (!(IsEditAllowed?.Invoke() ?? true))
        {
            _canvas.Invalidate();
            return;
        }

        // El gesto (umbral, snap, topes, anclas) es cálculo compartido:
        // ¿agarró un borde? → redimensionar en vez de mover
        var mode = TimelineHitTest.EdgeMode(block, p.X, _vp.PxPerSec, _vp.ScrollX);
        _drag = new TimelineDragGesture(block.Cue, isGroup: false, mode,
            block, p.X, p.Y, _projection.StartById);

        _canvas.CapturePointer(e.Pointer);
        _canvas.Invalidate();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_canvas).Position;

        if (_drag is null)
        {
            // Cursor según lo que hay debajo, como WPF: ↔ en un borde
            // redimensionable, ✥ sobre un bloque, flecha en el vacío
            var over = TimelineHitTest.Block(_projection.Blocks, p.X, p.Y,
                _vp.PxPerSec, _vp.ScrollX, LayerH);
            var shape = over is null
                ? InputSystemCursorShape.Arrow
                : TimelineHitTest.EdgeMode(over, p.X, _vp.PxPerSec, _vp.ScrollX)
                    != TimelineDragMode.Move
                    ? InputSystemCursorShape.SizeWestEast
                    : InputSystemCursorShape.SizeAll;
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
        if (_drag is null)
            return;
        _drag = null;
        RefreshFromModel();
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
            string text = $"{b.Cue.DisplayNumber} · {b.Cue.Name}";
            using var format = new CanvasTextFormat
            {
                FontSize = 11,
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
