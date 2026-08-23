// =============================================================================
// GeometryCanvas.cs — lienzo interactivo de geometría de vídeo, en Win2D.
// =============================================================================
// Equivalente de src/WinCue.App/GeometryCanvasControl.cs (WPF) para esta
// interfaz, NO una copia (regla 2 del proyecto). El CÁLCULO (mapeo de
// coordenadas SALIDA↔LIENZO, encaje del vídeo según el modo, esquinas
// rotadas, hit-test de los tiradores, arrastre —traslación y escala— e imán a
// bordes/centro) es EXACTAMENTE el mismo que usa WinCue: vive en
// WinCue.Presentation.GeometryLayout, sin un solo tipo de framework de
// interfaz. Este archivo SOLO pinta e interpreta el puntero — mismo reparto
// que TimelineCanvas.cs/WaveformCanvas.cs.
//
// ALCANCE DE ESTA PASADA (deliberado, no descuido — mismo patrón que el
// timeline/mixer/waveform "primera pasada"): sin fotograma fijo de referencia
// (GeometryCanvasControl.SetFrame). WinCue lo consigue capturando un still del
// visor de EDICIÓN de mpv (InspectorPanel.RefreshGeometryFrame,
// TryGrabStillAsync) — Secuence no tiene todavía ese visor de edición
// separado del de SALIDA (VideoOutputWindow.xaml.cs, que ocupa el monitor
// completo y no un hueco del inspector), así que este lienzo encuadra contra
// el relleno ámbar de siempre, igual que hacía WinCue antes de tener visor.
// Cuando Secuence tenga un visor de edición, SetFrame sube igual que
// ShowPaused/TryGrabStillAsync subirán entonces.
//
// CONEXIÓN A LA SALIDA REAL — la novedad de esta pasada: Translated/Scaled se
// comprometen por el MISMO camino que ya usan number/volume/trim en
// InspectorPanel.cs (Commit → CommitField → MainController.
// OnCueEditCommitted). MainController.ApplyLiveEdits ya sabe aplicar
// geo_mode/translate_x/translate_y/scale_x/scale/rotate_z EN VIVO
// (_video.ApplyGeometry) porque es el MISMO controlador que usa WinCue — no
// hay nada nuevo que enchufar en el motor, solo comprometer los mismos
// nombres de campo desde este inspector. Ver InspectorPanel.cs para el
// cableado completo.
//
// TRADUCCIÓN DE INTERACCIÓN WPF → WinUI 3:
//   OnMouseDown/Move/Up (MouseButtonEventArgs) → PointerPressed/Moved/
//   Released (PointerRoutedEventArgs); CaptureMouse()/ReleaseMouseCapture()
//   → CapturePointer(e.Pointer)/ReleasePointerCapture(e.Pointer); Cursor de
//   FrameworkElement → ProtectedCursor (ver constructor); Keyboard.Modifiers
//   → PointerRoutedEventArgs.KeyModifiers (llega YA resuelto con el propio
//   evento de puntero, sin encuesta aparte); DrawingContext.PushTransform
//   (RotateTransform) → CanvasDrawingSession.Transform (Matrix3x2.
//   CreateRotation), mismo patrón que MixerPanel.cs ya usa para el texto
//   girado de los strips verticales; PushClip(RectangleGeometry)/Pop →
//   CreateLayer(opacidad, rectángulo), mismo patrón que VuMeterCanvas.cs.
// =============================================================================

using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using WinCue.Presentation;

namespace Secuence.App;

/// <summary>Lienzo interactivo de geometría de vídeo, en Win2D. Equivalente de
/// <c>WinCue.App.GeometryCanvasControl</c> (WPF) para WinUI 3, sin fotograma
/// fijo de referencia en esta pasada (ver la cabecera del archivo).
/// UserControl que aloja un CanvasControl — CanvasControl sigue sellada (ver
/// VuMeterCanvas.cs).</summary>
public sealed class GeometryCanvas : UserControl
{
    private readonly CanvasControl _canvas = new();

    /// <summary>Arrastre del vídeo: (translateX, translateY) en px de canvas.
    /// Mismo contrato que WinCue.App.GeometryCanvasControl.Translated.</summary>
    public event Action<double, double>? Translated;

    /// <summary>Arrastre de un tirador de esquina: nueva escala (×). Mismo
    /// contrato que WinCue.App.GeometryCanvasControl.Scaled.</summary>
    public event Action<double>? Scaled;

    private bool _custom;
    private bool _preserveAspect = true;
    private double _fullScalePct = 100.0;
    private double _scaleX = 1.0;
    private double _tx, _ty;
    private long _rot;

    private bool _dragging;
    private GeoPoint _dragStart;
    private double _txStart, _tyStart;

    // Arrastre de tirador (escala). -1 = ninguno; 0..3 = esquinas.
    private int _handle = -1;
    private double _scaleStart;
    private double _grabDist;

    // Ejes en los que el imán está pegando ahora mismo (solo para dibujarlo)
    private bool _snapX, _snapY;

    public GeometryCanvas()
    {
        MinHeight = 120;
        Content = _canvas;
        _canvas.Draw += OnDraw;
        _canvas.SizeChanged += (_, _) => _canvas.Invalidate();

        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) =>
        {
            _dragging = false;
            _handle = -1;
            _snapX = _snapY = false;
            _canvas.Invalidate();
        };
    }

    /// <summary>Mismo contrato que
    /// WinCue.App.GeometryCanvasControl.SetValues.</summary>
    public void SetValues(bool custom, double fullScalePct, bool preserveAspect,
        double scaleX, double tx, double ty, long rot)
    {
        _custom = custom;
        _fullScalePct = fullScalePct;
        _preserveAspect = preserveAspect;
        _scaleX = scaleX;
        _tx = tx;
        _ty = ty;
        _rot = ((rot % 360) + 360) % 360;
        ProtectedCursor = InputSystemCursor.Create(
            _custom ? InputSystemCursorShape.SizeAll : InputSystemCursorShape.Arrow);
        _canvas.Invalidate();
    }

    // ------------------------------------------------------------------
    // Geometría interna — todo el CÁLCULO vive en GeometryLayout
    // (WinCue.Presentation); aquí solo se lee el estado privado del control
    // (_custom, _tx, _ty…) que GeometryLayout no conoce.
    // ------------------------------------------------------------------

    private GeoRect CanvasRect() => GeometryLayout.CanvasRect(_canvas.ActualWidth, _canvas.ActualHeight);

    private GeoRect VideoRect(GeoRect canvas) => GeometryLayout.VideoRect(
        canvas, _custom, _fullScalePct, _preserveAspect, _scaleX, _tx, _ty);

    private int HitHandle(GeoPoint p) =>
        GeometryLayout.HitHandle(p, _custom, VideoRect(CanvasRect()), _rot);

    // ------------------------------------------------------------------
    // Pintado
    // ------------------------------------------------------------------

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        double w = sender.ActualWidth, h = sender.ActualHeight;
        ds.Clear(ToColor(Palette.GeometryBg));
        if (w <= 0 || h <= 0)
            return;

        var canvas = GeometryLayout.CanvasRect(w, h);
        var canvasRect = ToRect(canvas);

        // Canvas + rejilla de tercios + cruz central
        ds.FillRectangle(canvasRect, ToColor(Palette.Black));
        for (int i = 1; i <= 2; i++)
        {
            float gx = (float)(canvas.Left + canvas.Width * i / 3.0);
            float gy = (float)(canvas.Top + canvas.Height * i / 3.0);
            ds.DrawLine(gx, (float)canvas.Top, gx, (float)canvas.Bottom, ToColor(Palette.GeometryGrid, 70));
            ds.DrawLine((float)canvas.Left, gy, (float)canvas.Right, gy, ToColor(Palette.GeometryGrid, 70));
        }
        float mx = (float)(canvas.Left + canvas.Width / 2), my = (float)(canvas.Top + canvas.Height / 2);
        ds.DrawLine(mx - 12, my, mx + 12, my, ToColor(Palette.GeometryCross, 110));
        ds.DrawLine(mx, my - 12, mx, my + 12, ToColor(Palette.GeometryCross, 110));

        // Vídeo (recortado por el canvas, igual que en la salida). Sin
        // fotograma en esta pasada (ver la cabecera del archivo): siempre el
        // relleno ámbar + marca de orientación.
        var video = VideoRect(canvas);
        using (ds.CreateLayer(1f, canvasRect))
        {
            var prevTransform = ds.Transform;
            if (_rot != 0)
            {
                float rad = (float)(_rot * Math.PI / 180.0);
                ds.Transform = Matrix3x2.CreateRotation(rad,
                    new Vector2((float)video.Center.X, (float)video.Center.Y));
            }
            ds.FillRectangle(ToRect(video), ToColor(Palette.Warn, 70));
            ds.DrawRectangle(ToRect(video), ToColor(Palette.Warn), 2f);
            // Marca de orientación (esquina superior izquierda del vídeo).
            ds.FillRectangle(new Rect(video.Left + 3, video.Top + 3, 10, 10), ToColor(Palette.Warn));
            ds.Transform = prevTransform;
        }

        // Marco del canvas SIEMPRE por encima (el recorte se lee al instante)
        ds.DrawRectangle(canvasRect, ToColor(Palette.GeometryFrame), 2f);

        // Guías del imán: se resalta el EJE que está pegando, no el borde
        // concreto, porque pegar por el centro no tiene un borde que señalar.
        var snapColor = ToColor(Palette.Standby);
        if (_snapX)
        {
            float sx = (float)(canvas.Left + canvas.Width / 2);
            ds.DrawLine(sx, (float)canvas.Top, sx, (float)canvas.Bottom, snapColor);
            ds.DrawLine((float)canvas.Left, (float)canvas.Top, (float)canvas.Left, (float)canvas.Bottom, snapColor);
            ds.DrawLine((float)canvas.Right, (float)canvas.Top, (float)canvas.Right, (float)canvas.Bottom, snapColor);
        }
        if (_snapY)
        {
            float sy = (float)(canvas.Top + canvas.Height / 2);
            ds.DrawLine((float)canvas.Left, sy, (float)canvas.Right, sy, snapColor);
            ds.DrawLine((float)canvas.Left, (float)canvas.Top, (float)canvas.Right, (float)canvas.Top, snapColor);
            ds.DrawLine((float)canvas.Left, (float)canvas.Bottom, (float)canvas.Right, (float)canvas.Bottom, snapColor);
        }

        // Tiradores de escala. FUERA de la capa recortada al canvas a
        // propósito: con el vídeo más grande que la superficie las esquinas
        // caen fuera, y recortadas no se podrían agarrar justo cuando más
        // falta hace (reducir un vídeo que se sale).
        if (_custom)
        {
            const float hs = (float)GeometryLayout.HandleSize;
            var handleFill = ToColor(Palette.TextBright);
            var handleStroke = ToColor(Palette.Warn);
            foreach (var p in GeometryLayout.HandlePoints(video, _rot))
            {
                var r = new Rect(p.X - hs / 2, p.Y - hs / 2, hs, hs);
                ds.FillRectangle(r, handleFill);
                ds.DrawRectangle(r, handleStroke, 1.5f);
            }
        }

        string label = _custom
            ? $"CUSTOM · {_scaleX:0.##}× · ({_tx:0},{_ty:0}) · {_rot}°"
            : $"FULL · {_fullScalePct:0}%{(_preserveAspect ? "" : " · estirado")}";
        using var labelFormat = new CanvasTextFormat { FontSize = 10, FontFamily = "Consolas" };
        ds.DrawText(label, (float)(canvas.Left + 4), (float)(canvas.Bottom + 2),
            ToColor(Palette.TextMuted), labelFormat);

        if (_custom)
        {
            const string hint = "arrastra el vídeo para posicionarlo";
            using var hintFormat = new CanvasTextFormat { FontSize = 10 };
            using var layout = new CanvasTextLayout(ds, hint, hintFormat, (float)w, 20);
            ds.DrawTextLayout(layout,
                (float)(canvas.Right - layout.LayoutBounds.Width - 4), (float)(canvas.Bottom + 2),
                ToColor(Palette.TextMuted));
        }
    }

    // ------------------------------------------------------------------
    // Arrastre (solo CUSTOM) — cálculo compartido, ver la cabecera del
    // archivo.
    // ------------------------------------------------------------------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_custom)
            return;
        var pos = ToGeoPoint(e.GetCurrentPoint(this).Position);

        // El tirador gana al movimiento: las esquinas están DENTRO del área
        // arrastrable del vídeo, así que si se comprobara el movimiento
        // primero los tiradores no llegarían a agarrarse nunca.
        _handle = HitHandle(pos);
        if (_handle >= 0)
        {
            var video = VideoRect(CanvasRect());
            var c = GeometryLayout.VideoCenter(video);
            _scaleStart = _scaleX;
            _grabDist = GeometryLayout.Dist(pos, c);
            // Agarrar justo en el centro daría una división por cero y una
            // escala infinita al primer píxel de movimiento.
            if (_grabDist < 1)
            {
                _handle = -1;
                return;
            }
            CapturePointer(e.Pointer);
            return;
        }

        _dragging = true;
        _dragStart = pos;
        _txStart = _tx;
        _tyStart = _ty;
        CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = ToGeoPoint(e.GetCurrentPoint(this).Position);

        // Cursor: en reposo, avisar de que la esquina se puede agarrar.
        if (!_dragging && _handle < 0)
            ProtectedCursor = InputSystemCursor.Create(!_custom ? InputSystemCursorShape.Arrow
                : HitHandle(pos) >= 0 ? InputSystemCursorShape.SizeNorthwestSoutheast
                : InputSystemCursorShape.SizeAll);

        if (_handle >= 0)
        {
            // Escala UNIFORME respecto al centro (ver GeometryLayout.
            // ScaleFromDrag): mover y escalar a la vez es justo lo que no
            // quieres en un encuadre.
            var c = GeometryLayout.VideoCenter(VideoRect(CanvasRect()));
            _scaleX = GeometryLayout.ScaleFromDrag(_scaleStart, _grabDist, pos, c);
            _canvas.Invalidate();
            Scaled?.Invoke(Math.Round(_scaleX, 3));
            return;
        }

        if (!_dragging)
            return;
        double pxPerUnit = GeometryLayout.PxPerUnit(CanvasRect());
        if (pxPerUnit <= 0)
            return;
        var (tx, ty) = GeometryLayout.TranslateFromDrag(_txStart, _tyStart, _dragStart, pos, pxPerUnit);
        _tx = tx;
        _ty = ty;

        // Imán a los bordes y al centro del canvas. Se aplica DESPUÉS de
        // mover y sobre el valor ya calculado, no acumulando: si se
        // acumulara, cada fotograma pegado volvería a corregir y el vídeo se
        // quedaría clavado sin poder despegarlo.
        var canvasRect = CanvasRect();
        bool shiftPressed = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
        var (dx, dy, sx, sy) = GeometryLayout.SnapDelta(canvasRect, VideoRect(canvasRect), _rot, shiftPressed);
        _tx += dx / pxPerUnit;
        _ty -= dy / pxPerUnit;
        _snapX = sx;
        _snapY = sy;

        _canvas.Invalidate();
        Translated?.Invoke(Math.Round(_tx), Math.Round(_ty));
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging || _handle >= 0)
        {
            _dragging = false;
            _handle = -1;
            // El aviso del imán solo tiene sentido MIENTRAS arrastras; dejarlo
            // encendido al soltar se leería como un estado, y no lo es.
            _snapX = _snapY = false;
            ReleasePointerCapture(e.Pointer);
            _canvas.Invalidate();
        }
    }

    private static GeoPoint ToGeoPoint(Point p) => new(p.X, p.Y);

    private static Rect ToRect(GeoRect r) => new(r.X, r.Y, r.Width, r.Height);

    private static Color ToColor(uint rgb, byte alpha = 0xff) => Color.FromArgb(
        alpha, (byte)((rgb >> 16) & 0xff), (byte)((rgb >> 8) & 0xff), (byte)(rgb & 0xff));
}
