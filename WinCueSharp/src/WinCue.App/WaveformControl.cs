// =============================================================================
// WaveformControl.cs — Barra de trim (in/out) con onda (port de
// waveform_widget.py)
// =============================================================================
// Barra horizontal cuya anchura = duración del clip, con dos manejadores
// arrastrables (entrada/salida). La región seleccionada se resalta y el resto
// se atenúa. Regla de tiempo arriba. En AUDIO dibuja la forma de onda
// (1600 buckets min/max decodificados con AudioDecoder); en VÍDEO una barra
// lisa con la duración leída por ffprobe. Dispara MarkersChanged al soltar.
// =============================================================================

using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WinCue.Audio;

namespace WinCue.App;

public sealed class WaveformControl : FrameworkElement
{
    private const int Buckets = 1600;
    private const double RulerH = 16;
    private const double HandleHit = 8;

    // Pinceles (espejo de los colores Python), congelados
    private static readonly Brush BgBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x0e, 0x0e, 0x0e)));
    private static readonly Pen WavePen = FrozenPen(Color.FromRgb(0x4a, 0x86, 0xb8), 1);
    private static readonly Pen WaveDimPen = FrozenPen(Color.FromRgb(0x24, 0x30, 0x38), 1);
    private static readonly Pen MarkPen = FrozenPen(Color.FromRgb(0xe8, 0xc0, 0x00), 2);
    private static readonly Brush MarkBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xe8, 0xc0, 0x00)));
    private static readonly Brush RegionBrush = Frozen(new SolidColorBrush(Color.FromArgb(34, 232, 192, 0)));
    private static readonly Brush DimBrush = Frozen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)));
    private static readonly Brush VideoBarBrush = Frozen(new LinearGradientBrush(
        Color.FromRgb(0x2a, 0x35, 0x40), Color.FromRgb(0x1a, 0x22, 0x2a), 90));
    private static readonly Brush RulerBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)));
    private static readonly Brush MsgBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)));

    /// <summary>Manejadores soltados: (startS, endS).</summary>
    public event Action<double, double>? MarkersChanged;

    private float[]? _mins, _maxs;
    private double _duration;
    private double _start, _end;
    private string _msg = "— sin media —";
    private bool _isAudio = true;
    private int _generation;          // cancela tareas de carga viejas
    private string? _drag;            // "start" | "end" | null

    public WaveformControl()
    {
        MinHeight = 96;
        MinWidth = 240;
        Cursor = Cursors.Hand;
        ClipToBounds = true;
    }

    // ------------------------------------------------------------------
    // API
    // ------------------------------------------------------------------

    public async void LoadAudio(string path, double trimStart, double trimEnd)
    {
        _isAudio = true;
        _mins = _maxs = null;
        _duration = 0.0;
        _start = Math.Max(0.0, trimStart);
        _end = Math.Max(0.0, trimEnd);
        _msg = "cargando…";
        InvalidateVisual();

        int gen = ++_generation;
        try
        {
            var result = await Task.Run(() => ComputePeaks(path));
            if (gen != _generation) return;
            (_mins, _maxs, _duration) = result;
            if (_end <= 0.0) _end = _duration;
            _msg = "";
        }
        catch (Exception)
        {
            if (gen != _generation) return;
            _msg = "no disponible";
        }
        InvalidateVisual();
    }

    public async void LoadVideo(string path, double trimStart, double trimEnd)
    {
        _isAudio = false;
        _mins = _maxs = null;
        _duration = 0.0;
        _start = Math.Max(0.0, trimStart);
        _end = Math.Max(0.0, trimEnd);
        _msg = "leyendo duración…";
        InvalidateVisual();

        int gen = ++_generation;
        try
        {
            double dur = await Task.Run(() => ProbeDuration(path));
            if (gen != _generation) return;
            _duration = dur;
            if (_end <= 0.0) _end = dur;
            _msg = "";
        }
        catch (Exception)
        {
            if (gen != _generation) return;
            _msg = "no disponible";
        }
        InvalidateVisual();
    }

    public void Clear(string msg = "— sin media —")
    {
        _generation++;
        _mins = _maxs = null;
        _duration = 0.0;
        _msg = msg;
        InvalidateVisual();
    }

    public void SetMarkers(double start, double end)
    {
        _start = Math.Max(0.0, start);
        _end = Math.Max(0.0, end);
        InvalidateVisual();
    }

    // ------------------------------------------------------------------
    // Cálculo en segundo plano
    // ------------------------------------------------------------------

    private static (float[] Mins, float[] Maxs, double Duration) ComputePeaks(string path)
    {
        var buf = AudioDecoder.Decode(path);
        int frames = buf.FrameCount;
        int ch = buf.Channels;
        if (frames == 0)
            throw new InvalidOperationException("sin audio");

        int buckets = Math.Min(Buckets, frames);
        var mins = new float[buckets];
        var maxs = new float[buckets];
        for (int b = 0; b < buckets; b++)
        {
            long f0 = (long)b * frames / buckets;
            long f1 = (long)(b + 1) * frames / buckets;
            float mn = float.MaxValue, mx = float.MinValue;
            for (long f = f0; f < f1; f++)
            {
                // mezcla mono: media de canales del frame
                float sum = 0;
                long idx = f * ch;
                for (int c = 0; c < ch; c++) sum += buf.Samples[idx + c];
                float v = sum / ch;
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
            mins[b] = mn == float.MaxValue ? 0 : mn;
            maxs[b] = mx == float.MinValue ? 0 : mx;
        }
        return (mins, maxs, buf.DurationSeconds);
    }

    private static double ProbeDuration(string path)
    {
        var psi = new ProcessStartInfo("ffprobe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries"); psi.ArgumentList.Add("format=duration");
        psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("default=nw=1:nk=1");
        psi.ArgumentList.Add(path);
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("ffprobe no disponible");
        string output = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit(8_000);
        return double.Parse(output, CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------
    // Geometría interna
    // ------------------------------------------------------------------

    private double SecToX(double sec) =>
        _duration <= 0 ? 0.0 : Math.Clamp(sec / _duration, 0.0, 1.0) * ActualWidth;

    private double XToSec(double x) =>
        _duration <= 0 ? 0.0 : Math.Clamp(x / Math.Max(1.0, ActualWidth), 0.0, 1.0) * _duration;

    private static string Fmt(double s)
    {
        int m = (int)s / 60;
        return $"{m}:{s - m * 60:00.0}";
    }

    // ------------------------------------------------------------------
    // Pintado
    // ------------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(BgBrush, null, new Rect(0, 0, w, h));

        if (_duration <= 0.0)
        {
            var msg = Label(_msg, MsgBrush);
            dc.DrawText(msg, new Point((w - msg.Width) / 2, (h - msg.Height) / 2));
            return;
        }

        double bodyTop = RulerH, bodyH = h - RulerH;
        double sX = SecToX(_start);
        double eX = SecToX(_end > 0 ? _end : _duration);

        // Cuerpo: onda (audio) o barra lisa (vídeo)
        if (_isAudio && _mins is not null && _maxs is not null)
        {
            double mid = bodyTop + bodyH / 2;
            double half = bodyH / 2 - 2;
            int n = _mins.Length;
            var sel = new StreamGeometry();
            var dim = new StreamGeometry();
            using (var gSel = sel.Open())
            using (var gDim = dim.Open())
            {
                for (int x = 0; x < (int)w; x++)
                {
                    int i = Math.Min(n - 1, (int)((double)x / Math.Max(1.0, w) * n));
                    double y0 = mid - _maxs[i] * half;
                    double y1 = mid - _mins[i] * half;
                    var ctx = x >= sX && x <= eX ? gSel : gDim;
                    ctx.BeginFigure(new Point(x + 0.5, y0), false, false);
                    ctx.LineTo(new Point(x + 0.5, y1), true, false);
                }
            }
            sel.Freeze(); dim.Freeze();
            dc.DrawGeometry(null, WaveDimPen, dim);
            dc.DrawGeometry(null, WavePen, sel);
        }
        else
        {
            dc.DrawRectangle(VideoBarBrush, null, new Rect(0, bodyTop, w, bodyH));
        }

        // Región seleccionada + zonas recortadas atenuadas
        dc.DrawRectangle(RegionBrush, null, new Rect(sX, bodyTop, Math.Max(0, eX - sX), bodyH));
        if (sX > 0)
            dc.DrawRectangle(DimBrush, null, new Rect(0, bodyTop, sX, bodyH));
        if (eX < w)
            dc.DrawRectangle(DimBrush, null, new Rect(eX, bodyTop, w - eX, bodyH));

        // Regla de tiempo (0/25/50/75/100 %)
        var rulerPen = new Pen(RulerBrush, 1);
        rulerPen.Freeze();
        foreach (double frac in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            double x = Math.Clamp(frac * w, 1, w - 1);
            dc.DrawLine(rulerPen, new Point(x, 0), new Point(x, RulerH - 4));
            var label = Label(Fmt(frac * _duration), RulerBrush);
            double tx = frac == 0 ? x + 2
                      : frac == 1 ? x - label.Width - 2
                      : x - label.Width / 2;
            dc.DrawText(label, new Point(tx, 0));
        }

        // Manejadores in/out
        DrawHandle(dc, sX, h, left: true);
        DrawHandle(dc, eX, h, left: false);
    }

    private static void DrawHandle(DrawingContext dc, double x, double h, bool left)
    {
        dc.DrawLine(MarkPen, new Point(x, RulerH), new Point(x, h));
        const double gw = 5;
        dc.DrawRectangle(MarkBrush, null,
            new Rect(left ? x - gw : x, RulerH, gw, 10));
    }

    private static FormattedText Label(string text, Brush brush) => new(
        text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface("Consolas"), 10, brush, 1.0);

    // ------------------------------------------------------------------
    // Interacción
    // ------------------------------------------------------------------

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (_duration <= 0.0)
            return;
        double x = e.GetPosition(this).X;
        double sX = SecToX(_start);
        double eX = SecToX(_end > 0 ? _end : _duration);
        if (Math.Abs(x - sX) <= HandleHit && Math.Abs(x - sX) <= Math.Abs(x - eX))
            _drag = "start";
        else if (Math.Abs(x - eX) <= HandleHit)
            _drag = "end";
        else
            _drag = Math.Abs(x - sX) <= Math.Abs(x - eX) ? "start" : "end";
        CaptureMouse();
        UpdateDrag(x);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag is not null)
            UpdateDrag(e.GetPosition(this).X);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_drag is not null)
        {
            _drag = null;
            ReleaseMouseCapture();
            MarkersChanged?.Invoke(_start, _end > 0 ? _end : _duration);
        }
    }

    private void UpdateDrag(double x)
    {
        double sec = XToSec(x);
        if (_drag == "start")
            _start = Math.Min(sec, _end > 0 ? _end : _duration);
        else
            _end = Math.Max(sec, _start);
        InvalidateVisual();
    }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    private static Pen FrozenPen(Color c, double width)
    {
        var p = new Pen(new SolidColorBrush(c), width);
        p.Freeze();
        return p;
    }
}
