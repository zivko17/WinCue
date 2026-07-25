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

    /// <summary>Arrastre del vídeo: (translateX, translateY) en px de canvas.</summary>
    public event Action<double, double>? Translated;

    /// <summary>Arrastre de un tirador de esquina: nueva escala (×).</summary>
    public event Action<double>? Scaled;

    /// <summary>Lado del tirador de esquina, en px de pantalla.</summary>
    private const double HandleSize = 9;

    /// <summary>Límites de la escala por ratón. Coinciden a propósito con los
    /// del deslizador de «Escala (×)» del inspector: si el ratón pudiera pasar
    /// de ahí, soltar el tirador dejaría el deslizador tope y el valor real
    /// fuera de rango, y los dos controles dirían cosas distintas.</summary>
    private const double MinScale = 0.1, MaxScale = 4.0;

    private const double SurfW = 1920.0, SurfH = 1080.0;

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

    public GeometryCanvasControl()
    {
        MinHeight = 120;
        ClipToBounds = true;
    }

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
    // Geometría interna
    // ------------------------------------------------------------------

    /// <summary>Rect del canvas ajustado al control (con margen).</summary>
    private Rect CanvasRect()
    {
        const double margin = 14;
        double w = Math.Max(40, ActualWidth - margin * 2);
        double h = Math.Max(24, ActualHeight - margin * 2);
        double s = Math.Min(w / SurfW, h / SurfH);
        double cw = SurfW * s, ch = SurfH * s;
        return new Rect((ActualWidth - cw) / 2, (ActualHeight - ch) / 2, cw, ch);
    }

    private Rect VideoRect(Rect canvas)
    {
        // Vídeo 16:9 "contain" dentro del canvas (aprox. — la resolución real
        // la muestra el visor de EDICIÓN), multiplicado por la escala activa
        double scale = _custom ? _scaleX : _fullScalePct / 100.0;
        double vw, vh;
        if (!_custom && !_preserveAspect)
        {
            vw = canvas.Width; vh = canvas.Height;   // FULL sin aspecto = estirar
        }
        else
        {
            double s = Math.Min(canvas.Width / 16.0, canvas.Height / 9.0);
            vw = 16.0 * s; vh = 9.0 * s;
        }
        vw *= scale; vh *= scale;

        double pxPerUnit = canvas.Width / SurfW;
        double cx = canvas.Left + canvas.Width / 2 + (_custom ? _tx * pxPerUnit : 0);
        double cy = canvas.Top + canvas.Height / 2 - (_custom ? _ty * pxPerUnit : 0);
        return new Rect(cx - vw / 2, cy - vh / 2, vw, vh);
    }

    /// <summary>Centro del vídeo en pantalla.</summary>
    private static Point VideoCenter(Rect v) =>
        new(v.Left + v.Width / 2, v.Top + v.Height / 2);

    /// <summary>Las cuatro esquinas del vídeo, YA ROTADAS. Se rota aquí y no en
    /// el DrawingContext porque estos puntos se usan también para acertar con el
    /// ratón: si se dibujaran con un RotateTransform pero se comprobaran sin
    /// rotar, con el vídeo a 90° los tiradores se verían en un sitio y
    /// responderían en otro.</summary>
    private Point[] HandlePoints(Rect v)
    {
        var c = VideoCenter(v);
        var corners = new[]
        {
            new Point(v.Left,  v.Top),
            new Point(v.Right, v.Top),
            new Point(v.Right, v.Bottom),
            new Point(v.Left,  v.Bottom),
        };
        if (_rot == 0)
            return corners;

        double rad = _rot * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        for (int i = 0; i < corners.Length; i++)
        {
            double dx = corners[i].X - c.X, dy = corners[i].Y - c.Y;
            corners[i] = new Point(c.X + dx * cos - dy * sin,
                                   c.Y + dx * sin + dy * cos);
        }
        return corners;
    }

    /// <summary>Índice del tirador bajo el punto dado, o -1.</summary>
    private int HitHandle(Point p)
    {
        if (!_custom)
            return -1;
        var pts = HandlePoints(VideoRect(CanvasRect()));
        // Radio de agarre algo mayor que el dibujo: un tirador de 9 px es
        // difícil de acertar si hay que clavarlo al píxel.
        const double grab = HandleSize;
        for (int i = 0; i < pts.Length; i++)
            if (Math.Abs(p.X - pts[i].X) <= grab && Math.Abs(p.Y - pts[i].Y) <= grab)
                return i;
        return -1;
    }

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
        dc.DrawRectangle(VideoBrush, VideoPen, video);
        // Marca de orientación (esquina superior izquierda del vídeo)
        dc.DrawRectangle(VideoPen.Brush, null,
            new Rect(video.Left + 3, video.Top + 3, 10, 10));
        if (_rot != 0)
            dc.Pop();
        dc.Pop();

        // Marco del canvas SIEMPRE por encima (el recorte se lee al instante)
        dc.DrawRectangle(null, CanvasPen, canvas);

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
            var c = VideoCenter(VideoRect(CanvasRect()));
            double factor = Dist(pos, c) / _grabDist;
            _scaleX = Math.Clamp(_scaleStart * factor, MinScale, MaxScale);
            InvalidateVisual();
            Scaled?.Invoke(Math.Round(_scaleX, 3));
            return;
        }

        if (!_dragging)
            return;
        double pxPerUnit = CanvasRect().Width / SurfW;
        if (pxPerUnit <= 0)
            return;
        _tx = _txStart + (pos.X - _dragStart.X) / pxPerUnit;
        _ty = _tyStart - (pos.Y - _dragStart.Y) / pxPerUnit;   // +Y hacia arriba
        InvalidateVisual();
        Translated?.Invoke(Math.Round(_tx), Math.Round(_ty));
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_dragging || _handle >= 0)
        {
            _dragging = false;
            _handle = -1;
            ReleaseMouseCapture();
        }
    }

    private static double Dist(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    private static Pen FrozenPen(Color c, double w)
    {
        var p = new Pen(new SolidColorBrush(c), w);
        p.Freeze();
        return p;
    }
}
