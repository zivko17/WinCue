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
    private static readonly Brush BgBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)));
    private static readonly Brush CanvasBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00)));
    private static readonly Pen CanvasPen = FrozenPen(Color.FromRgb(0x2a, 0x6f, 0xb0), 2);
    private static readonly Pen GridPen = FrozenPen(Color.FromArgb(70, 0x66, 0x66, 0x66), 1);
    private static readonly Pen CrossPen = FrozenPen(Color.FromArgb(110, 0x88, 0x88, 0x88), 1);
    private static readonly Brush VideoBrush = Frozen(new SolidColorBrush(Color.FromArgb(70, 0xe8, 0xa2, 0x00)));
    private static readonly Pen VideoPen = FrozenPen(Color.FromRgb(0xe8, 0xa2, 0x00), 2);
    private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)));

    /// <summary>Arrastre del vídeo: (translateX, translateY) en px de canvas.</summary>
    public event Action<double, double>? Translated;

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
        _dragging = true;
        _dragStart = e.GetPosition(this);
        _txStart = _tx;
        _tyStart = _ty;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging)
            return;
        var pos = e.GetPosition(this);
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
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
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
