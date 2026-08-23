// =============================================================================
// GeometryCanvasControl.cs — Lienzo interactivo de geometría de vídeo
// =============================================================================
// Dibuja el CANVAS de salida (marco azul, rejilla de tercios, cruz central) y
// el VIDEO como un rectángulo ámbar posicionado/escalado/rotado según la
// geometría del cue. En modo CUSTOM el rectángulo se ARRASTRA con el ratón
// para posicionarlo (emite Translated con las nuevas coordenadas en px de
// canvas, ya en el convenio del cue: +Y hacia arriba).
// =============================================================================

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WinCue.Presentation;

namespace WinCue.App;

public sealed class GeometryCanvasControl : FrameworkElement
{
    private static readonly Brush BgBrush = Frozen(new SolidColorBrush(Theme.GeometryBg));
    private static readonly Brush CanvasBrush = Frozen(new SolidColorBrush(Theme.Black));
    private static readonly Pen CanvasPen = FrozenPen(Theme.GeometryFrame, 2);
    private static readonly Pen GridPen = FrozenPen(Theme.Alpha(Theme.GeometryGrid, 70), 1);
    private static readonly Pen CrossPen = FrozenPen(Theme.Alpha(Theme.GeometryCross, 110), 1);
    private static readonly Brush VideoBrush = Frozen(new SolidColorBrush(Theme.Alpha(Theme.Warn, 70)));
    private static readonly Pen VideoPen = FrozenPen(Theme.Warn, 2);
    private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Theme.TextMuted));
    // Tiradores: relleno claro y filo del color del vídeo, para que se lean
    // sobre el negro del canvas y sobre el ámbar del propio vídeo.
    private static readonly Brush HandleFill = Frozen(new SolidColorBrush(Theme.TextBright));
    private static readonly Pen HandlePen = FrozenPen(Theme.Warn, 1.5);
    // Guía del imán: ámbar de atención, el mismo que ya avisa en el resto
    private static readonly Pen SnapPen = FrozenPen(Theme.Standby, 1);

    /// <summary>Arrastre del vídeo: (translateX, translateY) en px de canvas.</summary>
    public event Action<double, double>? Translated;

    /// <summary>Arrastre de un tirador de esquina: nueva escala (×).</summary>
    public event Action<double>? Scaled;

    // HandleSize, Min/MaxScale y SurfW/SurfH viven ahora en GeometryLayout
    // (WinCue.Presentation): son parte del CÁLCULO compartido con Secuence, no
    // de esta interfaz. Alias local de solo lectura para no reescribir los
    // sitios que ya lo usaban por su nombre corto (mismo patrón que
    // WaveformControl.RulerH/HandleHit) — SurfW/SurfH ya no hacía falta
    // nombrarlos aquí: CanvasRect()/VideoRect() delegan en GeometryLayout con
    // sus valores por defecto.
    private const double HandleSize = GeometryLayout.HandleSize;

    private bool _custom;
    private bool _preserveAspect = true;
    private double _fullScalePct = 100.0;
    private double _scaleX = 1.0;
    private double _tx, _ty;
    private long _rot;

    private bool _dragging;
    private Point _dragStart;
    private double _txStart, _tyStart;

    // Arrastre de tirador (escala). -1 = ninguno; 0..3 = esquinas.
    private int _handle = -1;
    private double _scaleStart;
    private double _grabDist;

    // Ejes en los que el imán está pegando ahora mismo (solo para dibujarlo)
    private bool _snapX, _snapY;

    public GeometryCanvasControl()
    {
        MinHeight = 120;
        ClipToBounds = true;
    }

    /// <summary>Fotograma real que se pinta dentro del rectángulo del vídeo.
    /// null → se pinta el relleno ámbar de siempre.
    ///
    /// Es una IMAGEN FIJA y no el visor en vivo por una razón de WPF: el visor
    /// es mpv dentro de un HWND nativo, y WPF no puede dibujar encima de un
    /// HWND (airspace). Con el vídeo en vivo aquí, el marco del canvas, la
    /// rejilla y los tiradores quedarían DEBAJO y no se verían. Un mapa de bits
    /// se comporta como cualquier otro dibujo, así que todo eso vuelve a ir
    /// encima — y para encuadrar, un fotograma quieto es justo lo que hace
    /// falta.</summary>
    public void SetFrame(ImageSource? frame)
    {
        _frame = frame;
        InvalidateVisual();
    }

    private ImageSource? _frame;

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
        Cursor = _custom ? Cursors.SizeAll : Cursors.Arrow;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------
    // Geometría interna — el CÁLCULO (mapeo de coordenadas, encaje del vídeo,
    // esquinas rotadas, hit-test) vive en WinCue.Presentation.GeometryLayout,
    // compartido con Secuence.App.GeometryCanvas. Aquí solo quedan estos
    // wrappers: convierten entre System.Windows.Point/Rect (WPF) y
    // GeoPoint/GeoRect (neutrales) en el borde, y leen el estado privado del
    // control (_custom, _tx, _ty…) que GeometryLayout no conoce.
    // ------------------------------------------------------------------

    private static GeoRect ToGeoRect(Rect r) => new(r.X, r.Y, r.Width, r.Height);
    private static Rect ToRect(GeoRect r) => new(r.X, r.Y, r.Width, r.Height);
    private static GeoPoint ToGeoPoint(Point p) => new(p.X, p.Y);
    private static Point ToPoint(GeoPoint p) => new(p.X, p.Y);

    /// <summary>Rect del canvas ajustado al control (con margen).</summary>
    private Rect CanvasRect() => ToRect(GeometryLayout.CanvasRect(ActualWidth, ActualHeight));

    private Rect VideoRect(Rect canvas) => ToRect(GeometryLayout.VideoRect(
        ToGeoRect(canvas), _custom, _fullScalePct, _preserveAspect, _scaleX, _tx, _ty));

    /// <summary>Centro del vídeo en pantalla.</summary>
    private static Point VideoCenter(Rect v) => ToPoint(GeometryLayout.VideoCenter(ToGeoRect(v)));

    /// <summary>Las cuatro esquinas del vídeo, YA ROTADAS. Se rota en el
    /// cálculo compartido y no en el DrawingContext porque estos puntos se
    /// usan también para acertar con el ratón: si se dibujaran con un
    /// RotateTransform pero se comprobaran sin rotar, con el vídeo a 90° los
    /// tiradores se verían en un sitio y responderían en otro.</summary>
    private Point[] HandlePoints(Rect v) =>
        Array.ConvertAll(GeometryLayout.HandlePoints(ToGeoRect(v), _rot), ToPoint);

    /// <summary>Índice del tirador bajo el punto dado, o -1.</summary>
    private int HitHandle(Point p) =>
        GeometryLayout.HitHandle(ToGeoPoint(p), _custom, ToGeoRect(VideoRect(CanvasRect())), _rot);

    private static double Dist(Point a, Point b) => GeometryLayout.Dist(ToGeoPoint(a), ToGeoPoint(b));

    // ------------------------------------------------------------------
    // Pintado
    // ------------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(BgBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var canvas = CanvasRect();

        // Canvas + rejilla de tercios + cruz central
        dc.DrawRectangle(CanvasBrush, null, canvas);
        for (int i = 1; i <= 2; i++)
        {
            double gx = canvas.Left + canvas.Width * i / 3.0;
            double gy = canvas.Top + canvas.Height * i / 3.0;
            dc.DrawLine(GridPen, new Point(gx, canvas.Top), new Point(gx, canvas.Bottom));
            dc.DrawLine(GridPen, new Point(canvas.Left, gy), new Point(canvas.Right, gy));
        }
        double mx = canvas.Left + canvas.Width / 2, my = canvas.Top + canvas.Height / 2;
        dc.DrawLine(CrossPen, new Point(mx - 12, my), new Point(mx + 12, my));
        dc.DrawLine(CrossPen, new Point(mx, my - 12), new Point(mx, my + 12));

        // Vídeo (recortado por el canvas, igual que en la salida)
        var video = VideoRect(canvas);
        dc.PushClip(new RectangleGeometry(canvas));
        if (_rot != 0)
            dc.PushTransform(new RotateTransform(_rot,
                video.Left + video.Width / 2, video.Top + video.Height / 2));
        if (_frame is not null)
        {
            // El fotograma va estirado al rectángulo del vídeo A PROPÓSITO: ese
            // rectángulo YA lleva aplicada la relación de aspecto que tendrá en
            // la salida, así que respetar aquí la del mapa de bits volvería a
            // aplicarla y mentiría sobre el recorte.
            dc.DrawImage(_frame, video);
            dc.DrawRectangle(null, VideoPen, video);
        }
        else
        {
            dc.DrawRectangle(VideoBrush, VideoPen, video);
            // Marca de orientación (esquina superior izquierda del vídeo).
            // Con fotograma no hace falta: la propia imagen dice hacia dónde va.
            dc.DrawRectangle(VideoPen.Brush, null,
                new Rect(video.Left + 3, video.Top + 3, 10, 10));
        }
        if (_rot != 0)
            dc.Pop();
        dc.Pop();

        // Marco del canvas SIEMPRE por encima (el recorte se lee al instante)
        dc.DrawRectangle(null, CanvasPen, canvas);

        // Guías del imán: se resalta el EJE que está pegando, no el borde
        // concreto, porque pegar por el centro no tiene un borde que señalar.
        if (_snapX)
        {
            double sx = canvas.Left + canvas.Width / 2;
            dc.DrawLine(SnapPen, new Point(sx, canvas.Top), new Point(sx, canvas.Bottom));
            dc.DrawLine(SnapPen, new Point(canvas.Left, canvas.Top), new Point(canvas.Left, canvas.Bottom));
            dc.DrawLine(SnapPen, new Point(canvas.Right, canvas.Top), new Point(canvas.Right, canvas.Bottom));
        }
        if (_snapY)
        {
            double sy = canvas.Top + canvas.Height / 2;
            dc.DrawLine(SnapPen, new Point(canvas.Left, sy), new Point(canvas.Right, sy));
            dc.DrawLine(SnapPen, new Point(canvas.Left, canvas.Top), new Point(canvas.Right, canvas.Top));
            dc.DrawLine(SnapPen, new Point(canvas.Left, canvas.Bottom), new Point(canvas.Right, canvas.Bottom));
        }

        // Tiradores de escala. Van FUERA del PushClip del canvas a propósito:
        // con el vídeo más grande que la superficie las esquinas caen fuera, y
        // recortadas no se podrían agarrar justo cuando más falta hace
        // (reducir un vídeo que se sale).
        if (_custom)
        {
            const double h = HandleSize / 2;
            foreach (var p in HandlePoints(video))
                dc.DrawRectangle(HandleFill, HandlePen,
                    new Rect(p.X - h, p.Y - h, HandleSize, HandleSize));
        }

        var label = new FormattedText(
            _custom ? $"CUSTOM · {_scaleX:0.##}× · ({_tx:0},{_ty:0}) · {_rot}°"
                    : $"FULL · {_fullScalePct:0}%{(_preserveAspect ? "" : " · estirado")}",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Consolas"), 10, LabelBrush, 1.0);
        dc.DrawText(label, new Point(canvas.Left + 4, canvas.Bottom + 2));

        if (_custom)
        {
            var hint = new FormattedText("arrastra el vídeo para posicionarlo",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, LabelBrush, 1.0);
            dc.DrawText(hint, new Point(canvas.Right - hint.Width - 4, canvas.Bottom + 2));
        }
    }

    // ------------------------------------------------------------------
    // Arrastre (solo CUSTOM)
    // ------------------------------------------------------------------

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (!_custom)
            return;
        var pos = e.GetPosition(this);

        // El tirador gana al movimiento: las esquinas están DENTRO del área
        // arrastrable del vídeo, así que si se comprobara el movimiento primero
        // los tiradores no llegarían a agarrarse nunca.
        _handle = HitHandle(pos);
        if (_handle >= 0)
        {
            var video = VideoRect(CanvasRect());
            var c = VideoCenter(video);
            _scaleStart = _scaleX;
            _grabDist = Dist(pos, c);
            // Agarrar justo en el centro daría una división por cero y una
            // escala infinita al primer píxel de movimiento.
            if (_grabDist < 1)
            {
                _handle = -1;
                return;
            }
            CaptureMouse();
            return;
        }

        _dragging = true;
        _dragStart = pos;
        _txStart = _tx;
        _tyStart = _ty;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pos = e.GetPosition(this);

        // Cursor: en reposo, avisar de que la esquina se puede agarrar.
        if (!_dragging && _handle < 0)
            Cursor = !_custom ? Cursors.Arrow
                   : HitHandle(pos) >= 0 ? Cursors.SizeNWSE
                   : Cursors.SizeAll;

        if (_handle >= 0)
        {
            // Escala UNIFORME respecto al centro: la razón entre la distancia
            // actual al centro y la que había al agarrar. Escalar respecto al
            // centro y no respecto a la esquina opuesta es lo que hace que el
            // vídeo no se desplace mientras se redimensiona — en un encuadre,
            // mover y escalar a la vez es justo lo que no quieres.
            var c = ToGeoPoint(VideoCenter(VideoRect(CanvasRect())));
            _scaleX = GeometryLayout.ScaleFromDrag(_scaleStart, _grabDist, ToGeoPoint(pos), c);
            InvalidateVisual();
            Scaled?.Invoke(Math.Round(_scaleX, 3));
            return;
        }

        if (!_dragging)
            return;
        double pxPerUnit = GeometryLayout.PxPerUnit(ToGeoRect(CanvasRect()));
        if (pxPerUnit <= 0)
            return;
        var (tx, ty) = GeometryLayout.TranslateFromDrag(
            _txStart, _tyStart, ToGeoPoint(_dragStart), ToGeoPoint(pos), pxPerUnit);
        _tx = tx;
        _ty = ty;

        // Imán a los bordes y al centro del canvas. Se aplica DESPUÉS de mover
        // y sobre el valor ya calculado, no acumulando: si se acumulara, cada
        // fotograma pegado volvería a corregir y el vídeo se quedaría clavado
        // sin poder despegarlo.
        var canvasRect = CanvasRect();
        bool shiftPressed = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var (dx, dy, sx, sy) = GeometryLayout.SnapDelta(
            ToGeoRect(canvasRect), ToGeoRect(VideoRect(canvasRect)), _rot, shiftPressed);
        _tx += dx / pxPerUnit;
        _ty -= dy / pxPerUnit;
        _snapX = sx;
        _snapY = sy;

        InvalidateVisual();
        Translated?.Invoke(Math.Round(_tx), Math.Round(_ty));
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_dragging || _handle >= 0)
        {
            _dragging = false;
            _handle = -1;
            // El aviso del imán solo tiene sentido MIENTRAS arrastras; dejarlo
            // encendido al soltar se leería como un estado, y no lo es.
            _snapX = _snapY = false;
            ReleaseMouseCapture();
            InvalidateVisual();
        }
    }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    private static Pen FrozenPen(Color c, double w)
    {
        var p = new Pen(new SolidColorBrush(c), w);
        p.Freeze();
        return p;
    }
}
