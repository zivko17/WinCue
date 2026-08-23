// =============================================================================
// VuMeterControl.cs — VU con peak-hold (sustituye al ProgressBar plano;
// equivalente al vu_meter.py de Python)
// =============================================================================
// EL CÁLCULO (mapeo de RMS a posición, ataque/release, retención de pico,
// testigo de clip, geometría en píxeles, colores por zona) vive en
// WinCue.Presentation.VuMeterLayout — piloto del traslado a Win2D en Secuence
// (ver el encargo, hitos-0-1). Este archivo YA NO calcula nada: solo pide el
// estado y la geometría y los pinta con DrawingContext. El comportamiento en
// pantalla es EXACTAMENTE el de antes — la extracción no tocó un número.
// =============================================================================

using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using WinCue.Presentation;

namespace WinCue.App;

public sealed class VuMeterControl : FrameworkElement
{
    private static readonly Brush BgBrush = Make(new SolidColorBrush(Theme.Hex(VuMeterPalette.Background)));
    private static readonly Pen FramePen = MakePen(Theme.Hex(VuMeterPalette.Frame), 1);
    private static readonly Pen TickPen = MakePen(
        Theme.Alpha(Theme.Hex(VuMeterPalette.Tick), VuMeterPalette.TickAlpha), 1);
    private static readonly Brush BarBrush = Make(new LinearGradientBrush(
        ToGradientStops(VuMeterPalette.HorizontalBar), 0));
    private static readonly Brush PeakBrush = Make(new SolidColorBrush(Theme.Hex(VuMeterPalette.Peak)));

    private static readonly Brush BarBrushVertical = Make(new LinearGradientBrush(
        ToGradientStops(VuMeterPalette.VerticalBar), 90));

    private static readonly Brush ClipBrush = Make(new SolidColorBrush(Theme.Hex(VuMeterPalette.Clip)));

    private readonly bool _vertical;
    private readonly VuMeterState _state = new();
    private readonly DispatcherTimer _decay;

    /// <summary>Constructor sin parámetros para XAML (horizontal).</summary>
    public VuMeterControl() : this(false) { }

    public VuMeterControl(bool vertical)
    {
        _vertical = vertical;
        _decay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _decay.Tick += (_, _) => Decay();
        _decay.Start();
    }

    /// <summary>Nivel RMS instantáneo (0..1 lineal). Se muestra en escala dB
    /// (−60…0 dBFS): es como se lee un VU de verdad — un tono a −15 dB luce
    /// al ~75 %, no al 17 % que daría la escala lineal. La ley exacta vive en
    /// VuMeterState.Normalize (WinCue.Presentation).</summary>
    public void SetLevel(float rms)
    {
        _state.Report(rms, System.Diagnostics.Stopwatch.GetTimestamp());
        InvalidateVisual();
    }

    private void Decay()
    {
        if (_state.Decay(System.Diagnostics.Stopwatch.GetTimestamp()))
            InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();

        // Fondo + marco: el medidor se ve SIEMPRE, también en silencio
        dc.DrawRectangle(BgBrush, FramePen, ToRect(VuMeterLayout.FrameRect(w, h)));

        // Clip-hold: testigo ROJO enganchado 3 s tras tocar 0 dBFS
        var led = VuMeterLayout.ClipLedRect(w, h, _vertical, _state.IsClipped(now));
        if (led is { } ledRect)
            dc.DrawRectangle(ClipBrush, null, ToRect(ledRect));

        if (_vertical)
        {
            // Marcas de escala (25/50/75 %)
            foreach (double f in VuMeterLayout.VerticalTickFractions)
                dc.DrawLine(TickPen, new Point(1, h * f), new Point(w - 1, h * f));

            // Barra de abajo hacia arriba (verde abajo, rojo arriba)
            var barClip = VuMeterLayout.LevelBarClipRect(w, h, _vertical, _state.Level);
            if (barClip is { } bar)
            {
                dc.PushClip(new RectangleGeometry(ToRect(bar)));
                dc.DrawRectangle(BarBrushVertical, null, new Rect(0, 0, w, h));
                dc.Pop();
            }
            var peak = VuMeterLayout.PeakRect(w, h, _vertical, _state.Peak);
            if (peak is { } peakRect)
                dc.DrawRectangle(PeakBrush, null, ToRect(peakRect));
            return;
        }

        var hBarClip = VuMeterLayout.LevelBarClipRect(w, h, _vertical, _state.Level);
        if (hBarClip is { } hBar)
        {
            dc.PushClip(new RectangleGeometry(ToRect(hBar)));
            dc.DrawRectangle(BarBrush, null, new Rect(0, 0, w, h));
            dc.Pop();
        }
        var hPeak = VuMeterLayout.PeakRect(w, h, _vertical, _state.Peak);
        if (hPeak is { } hPeakRect)
            dc.DrawRectangle(PeakBrush, null, ToRect(hPeakRect));
    }

    private static Rect ToRect(VuMeterRect r) => new(r.X, r.Y, r.Width, r.Height);

    private static GradientStopCollection ToGradientStops(VuMeterGradientStop[] stops)
    {
        var collection = new GradientStopCollection();
        foreach (var s in stops)
            collection.Add(new GradientStop(Theme.Hex(s.Color), s.Offset));
        return collection;
    }

    private static Brush Make(Brush b) { b.Freeze(); return b; }

    private static Pen MakePen(Color c, double w)
    {
        var p = new Pen(new SolidColorBrush(c), w);
        p.Freeze();
        return p;
    }
}
