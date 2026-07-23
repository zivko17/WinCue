// =============================================================================
// VuMeterControl.cs — VU con peak-hold (sustituye al ProgressBar plano;
// equivalente al vu_meter.py de Python)
// =============================================================================

using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinCue.App;

public sealed class VuMeterControl : FrameworkElement
{
    private static readonly Brush BgBrush = Make(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)));
    private static readonly Pen FramePen = MakePen(Color.FromRgb(0x4a, 0x4a, 0x4a), 1);
    private static readonly Pen TickPen = MakePen(Color.FromArgb(90, 0x77, 0x77, 0x77), 1);
    private static readonly Brush BarBrush = Make(new LinearGradientBrush(
        new GradientStopCollection
        {
            new GradientStop(Color.FromRgb(0x2e, 0xcc, 0x71), 0.0),
            new GradientStop(Color.FromRgb(0x2e, 0xcc, 0x71), 0.62),
            new GradientStop(Color.FromRgb(0xe8, 0xc0, 0x00), 0.80),
            new GradientStop(Color.FromRgb(0xc0, 0x39, 0x2b), 1.0),
        }, 0));
    private static readonly Brush PeakBrush = Make(new SolidColorBrush(Colors.White));

    private static readonly Brush BarBrushVertical = Make(new LinearGradientBrush(
        new GradientStopCollection
        {
            new GradientStop(Color.FromRgb(0xc0, 0x39, 0x2b), 0.0),
            new GradientStop(Color.FromRgb(0xe8, 0xc0, 0x00), 0.20),
            new GradientStop(Color.FromRgb(0x2e, 0xcc, 0x71), 0.38),
            new GradientStop(Color.FromRgb(0x2e, 0xcc, 0x71), 1.0),
        }, 90));

    private static readonly Brush ClipBrush = Make(new SolidColorBrush(Color.FromRgb(0xff, 0x20, 0x20)));

    private readonly bool _vertical;
    private double _level;          // 0..1
    private double _peak;           // 0..1 con hold
    private long _peakTicks;
    private long _lastUpdateTicks;
    private long _clipTicks;        // clip-hold: enganchado 3 s tras saturar
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
    /// al ~75 %, no al 17 % que daría la escala lineal.</summary>
    public void SetLevel(float rms)
    {
        double norm = 0.0;
        if (rms > 0.00001)
        {
            double db = 20.0 * Math.Log10(rms);
            norm = Math.Clamp((db + 60.0) / 60.0, 0.0, 1.0);
        }

        // Ataque instantáneo, release suave POR CADA report: la barra baila
        // con la señal (antes solo subía y se clavaba en el máximo visto)
        if (norm >= _level)
            _level = norm;
        else
            _level = _level * 0.55 + norm * 0.45;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        _lastUpdateTicks = now;
        if (norm >= _peak)
        {
            _peak = norm;
            _peakTicks = now;
        }
        if (rms >= 0.985f)   // ~0 dBFS: saturación → clip enganchado
            _clipTicks = now;
        InvalidateVisual();
    }

    private void Decay()
    {
        bool dirty = false;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();

        // Nivel: caída suave cuando dejan de llegar reports (silencio/parada)
        if (_level > 0.001 &&
            System.Diagnostics.Stopwatch.GetElapsedTime(_lastUpdateTicks, now).TotalMilliseconds > 120)
        {
            _level *= 0.78;
            if (_level < 0.001) _level = 0;
            dirty = true;
        }

        // Pico: retención de 1.2 s y después caída
        if (_peak > 0.001 &&
            System.Diagnostics.Stopwatch.GetElapsedTime(_peakTicks, now).TotalMilliseconds > 1_200)
        {
            _peak = Math.Max(_peak * 0.90, _level);
            if (_peak < 0.001) _peak = 0;
            dirty = true;
        }

        if (dirty)
            InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        // Fondo + marco: el medidor se ve SIEMPRE, también en silencio
        dc.DrawRectangle(BgBrush, FramePen, new Rect(0.5, 0.5, w - 1, h - 1));

        // Clip-hold: testigo ROJO enganchado 3 s tras tocar 0 dBFS
        bool clipped = _clipTicks != 0 && System.Diagnostics.Stopwatch
            .GetElapsedTime(_clipTicks).TotalMilliseconds < 3_000;
        if (clipped)
        {
            var led = _vertical
                ? new Rect(0.5, 0.5, w - 1, 5)
                : new Rect(w - 5.5, 0.5, 5, h - 1);
            dc.DrawRectangle(ClipBrush, null, led);
        }

        if (_vertical)
        {
            // Marcas de escala en dB (−6/−12/−24/−40), alineadas con la
            // regla del mixer: y = 1 − (db+60)/60 sobre la escala de −60…0
            foreach (double f in new[] { 0.1, 0.2, 0.4, 2.0 / 3.0 })
                dc.DrawLine(TickPen, new Point(1, h * f), new Point(w - 1, h * f));

            // Barra de abajo hacia arriba (verde abajo, rojo arriba)
            if (_level > 0)
            {
                dc.PushClip(new RectangleGeometry(
                    new Rect(0, h * (1 - _level), w, h * _level)));
                dc.DrawRectangle(BarBrushVertical, null, new Rect(0, 0, w, h));
                dc.Pop();
            }
            if (_peak > 0.01)
            {
                double y = Math.Max(h * (1 - _peak), 0);
                dc.DrawRectangle(PeakBrush, null, new Rect(0, y, w, 2));
            }
            return;
        }

        if (_level > 0)
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, _level * w, h)));
            dc.DrawRectangle(BarBrush, null, new Rect(0, 0, w, h));
            dc.Pop();
        }
        if (_peak > 0.01)
        {
            double x = Math.Min(_peak * w, w - 2);
            dc.DrawRectangle(PeakBrush, null, new Rect(x, 0, 2, h));
        }
    }

    private static Brush Make(Brush b) { b.Freeze(); return b; }

    private static Pen MakePen(Color c, double w)
    {
        var p = new Pen(new SolidColorBrush(c), w);
        p.Freeze();
        return p;
    }
}
