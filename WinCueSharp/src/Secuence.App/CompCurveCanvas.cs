// =============================================================================
// CompCurveCanvas.cs — gráfica del compresor (transferencia + medidor GR), en
// Win2D.
// =============================================================================
// Equivalente de la clase CompCurveCanvas de src/WinCue.App/EqEditorControl.cs
// (WPF) para esta interfaz, NO una copia. El CÁLCULO (ejes dB↔píxel,
// transferencia entrada→salida, muestreo de la curva, arrastre del umbral y
// rueda del ratio) vive en WinCue.Presentation.CompCurveLayout. Este archivo
// SOLO pinta e interpreta el puntero — mismo reparto que EqCurveCanvas.cs.
// =============================================================================

using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.UI;
using WinCue.Core;
using WinCue.Presentation;

namespace Secuence.App;

/// <summary>Gráfica del compresor: curva de transferencia arrastrable (umbral
/// en X, rueda = ratio) con el medidor GR en vivo a la derecha. Equivalente de
/// <c>WinCue.App.CompCurveCanvas</c> para WinUI 3.</summary>
public sealed class CompCurveCanvas : UserControl
{
    private readonly CanvasControl _canvas = new();

    public Action<string, object?>? Commit;

    private Cue? _cue;
    private float _gr;
    private bool _drag;

    public CompCurveCanvas()
    {
        MinHeight = 90;
        Content = _canvas;
        _canvas.Draw += OnDraw;
        _canvas.SizeChanged += (_, _) => _canvas.Invalidate();

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
        PointerCaptureLost += (_, _) =>
        {
            _drag = false;
            _canvas.Invalidate();
        };
    }

    public void SetCue(Cue? cue)
    {
        _cue = cue;
        _gr = 0f;
        _canvas.Invalidate();
    }

    /// <summary>Repinta sin tocar el GR — para cuando un campo numérico del
    /// inspector cambia un parámetro de ESTE MISMO cue.</summary>
    public void Refresh() => _canvas.Invalidate();

    /// <summary>Reducción de ganancia actual (dB ≥ 0) — solo repinta si cambia.</summary>
    public void SetGainReduction(float grDb)
    {
        if (Math.Abs(grDb - _gr) < 0.15f)
            return;
        _gr = Math.Max(0f, grDb);
        _canvas.Invalidate();
    }

    private double PlotW => CompCurveLayout.PlotWidth(_canvas.ActualWidth);

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        double w = sender.ActualWidth, h = sender.ActualHeight;
        ds.Clear(ToColor(Palette.EqBg));
        if (_cue is null || w < 60)
            return;
        double pw = PlotW;

        using var gridFormat = new CanvasTextFormat { FontSize = 8 };

        // Rejilla cada 12 dB + diagonal 1:1 de referencia
        foreach (double db in CompCurveLayout.GridDbLines)
        {
            float x = (float)CompCurveLayout.XFromDb(db, pw), y = (float)CompCurveLayout.YFromDb(db, h);
            ds.DrawLine(x, 0, x, (float)h, ToColor(Palette.EqGrid));
            ds.DrawLine(0, y, (float)pw, y, ToColor(Palette.EqGrid));
        }
        ds.DrawLine(
            (float)CompCurveLayout.XFromDb(CompCurveLayout.DbMin, pw), (float)CompCurveLayout.YFromDb(CompCurveLayout.DbMin, h),
            (float)CompCurveLayout.XFromDb(0, pw), (float)CompCurveLayout.YFromDb(0, h),
            ToColor(Palette.EqZero));
        ds.DrawText("COMP  in→out dB", 4, 2, ToColor(Palette.TextDimmer), gridFormat);

        // Curva de transferencia
        var pts = CompCurveLayout.TransferCurve(_cue.CompThreshold, _cue.CompRatio, _cue.CompMakeup, pw, h);
        if (pts.Count > 1)
        {
            using var pathBuilder = new CanvasPathBuilder(sender);
            pathBuilder.BeginFigure(ToVector(pts[0]));
            for (int i = 1; i < pts.Count; i++)
                pathBuilder.AddLine(ToVector(pts[i]));
            pathBuilder.EndFigure(CanvasFigureLoop.Open);
            using var geo = CanvasGeometry.CreatePath(pathBuilder);
            ds.DrawGeometry(geo, _cue.CompEnabled ? ToColor(Palette.Warn) : ToColor(Palette.CompCurveOff),
                _cue.CompEnabled ? 2f : 1.5f);
        }

        // Manejador del umbral (sobre la curva, arrastrable en X)
        var thr = CompCurveLayout.ThresholdPoint(_cue.CompThreshold, _cue.CompRatio, _cue.CompMakeup, pw, h);
        float thrR = _drag ? 6 : 5;
        ds.FillCircle((float)thr.X, (float)thr.Y, thrR, ToColor(Palette.TextPrimary));
        using var thrFormat = new CanvasTextFormat { FontSize = 8 };
        ds.DrawText($"{_cue.CompThreshold:0} dB · {_cue.CompRatio:0.#}:1",
            (float)Math.Min(thr.X + 6, pw - 70), (float)Math.Max(2, thr.Y - 14), ToColor(Palette.TextDimmer), thrFormat);

        // ── Medidor GR (derecha): cuánta reducción aplica AHORA ─────────
        var meterRect = new Rect(pw + 4, 0, CompCurveLayout.MeterW, h);
        ds.FillRectangle(meterRect, ToColor(Palette.EqGrBg));
        if (_cue.CompEnabled && _gr > 0.05f)
        {
            double frac = CompCurveLayout.GrFraction(_gr);
            ds.FillRectangle(new Rect(pw + 4, 0, CompCurveLayout.MeterW, frac * h), ToColor(Palette.WarnDeep));
        }
        foreach (double mark in CompCurveLayout.GrMarks)
        {
            float y = (float)(mark / CompCurveLayout.GrScaleDb * h);
            ds.DrawLine((float)(pw + 4), y, (float)(pw + 4 + CompCurveLayout.MeterW), y, ToColor(Palette.EqGrid));
        }
        ds.DrawText("GR", (float)(pw + 6), (float)(h - 12), ToColor(Palette.TextDimmer), gridFormat);
        if (_cue.CompEnabled && _gr > 0.05f)
            ds.DrawText($"-{_gr:0.0}", (float)(pw + 2), 2, ToColor(Palette.Warn), gridFormat);
    }

    private static System.Numerics.Vector2 ToVector(GeoPoint p) => new((float)p.X, (float)p.Y);

    private static Color ToColor(uint rgb, byte alpha = 0xff) => Color.FromArgb(
        alpha, (byte)((rgb >> 16) & 0xff), (byte)((rgb >> 8) & 0xff), (byte)(rgb & 0xff));

    // ------------------------------------------------------------------
    // Interacción — cálculo (umbral de arrastre, ratio de rueda) en
    // CompCurveLayout; aquí solo la traducción de eventos de puntero.
    // ------------------------------------------------------------------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_cue is null)
            return;
        var p = e.GetCurrentPoint(this).Position;
        if (p.X > PlotW)
            return;   // el medidor no se arrastra
        _drag = true;
        CapturePointer(e.Pointer);
        Commit?.Invoke("comp_threshold", CompCurveLayout.ThresholdFromDragX(p.X, PlotW));
        _canvas.Invalidate();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_drag || _cue is null)
            return;
        Commit?.Invoke("comp_threshold",
            CompCurveLayout.ThresholdFromDragX(e.GetCurrentPoint(this).Position.X, PlotW));
        _canvas.Invalidate();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag)
        {
            _drag = false;
            ReleasePointerCapture(e.Pointer);
            _canvas.Invalidate();
        }
    }

    /// <summary>Rueda sobre la gráfica: ajustar el RATIO.</summary>
    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_cue is null)
            return;
        bool up = e.GetCurrentPoint(this).Properties.MouseWheelDelta > 0;
        double newRatio = CompCurveLayout.RatioFromWheel(_cue.CompRatio, up);
        Commit?.Invoke("comp_ratio", newRatio);
        _canvas.Invalidate();
        e.Handled = true;
    }
}
