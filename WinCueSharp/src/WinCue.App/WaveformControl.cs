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
using WinCue.Core;
using WinCue.Presentation;

namespace WinCue.App;

public sealed class WaveformControl : FrameworkElement
{
    // Buckets, RulerH y HandleHit viven ahora en WaveformLayout
    // (WinCue.Presentation): son parte del CÁLCULO compartido con Secuence,
    // no de esta interfaz. Alias locales de solo lectura para no reescribir
    // los sitios que ya los usaban por su nombre corto.
    private const double RulerH = WaveformLayout.RulerH;
    private const double HandleHit = WaveformLayout.HandleHit;

    // Pinceles (espejo de los colores Python), congelados
    private static readonly Brush BgBrush = Frozen(new SolidColorBrush(Theme.WaveBg));
    private static readonly Pen WavePen = FrozenPen(Theme.Wave, 1);
    private static readonly Pen WaveDimPen = FrozenPen(Theme.WaveDim, 1);
    private static readonly Pen MarkPen = FrozenPen(Theme.Standby, 2);
    private static readonly Brush MarkBrush = Frozen(new SolidColorBrush(Theme.Standby));
    private static readonly Brush RegionBrush = Frozen(new SolidColorBrush(Theme.Alpha(Theme.Standby, 34)));
    private static readonly Brush DimBrush = Frozen(new SolidColorBrush(Theme.Alpha(Theme.Black, 150)));
    private static readonly Brush VideoBarBrush = Frozen(new LinearGradientBrush(
        Theme.VideoBarA, Theme.VideoBarB, 90));
    private static readonly Brush RulerBrush = Frozen(new SolidColorBrush(Theme.TextDimmer));
    private static readonly Brush MsgBrush = Frozen(new SolidColorBrush(Theme.TextFaint));

    /// <summary>Manejadores soltados: (startS, endS).</summary>
    public event Action<double, double>? MarkersChanged;

    /// <summary>Arrastre EN CURSO: segundos del manejador activo (para scrub).</summary>
    public event Action<double>? Scrubbing;

    private float[]? _mins, _maxs;
    private double _duration;
    private double _start, _end;
    private string _msg = Loc.T("— sin media —", "— no media —");
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

    public void Clear(string? msg = null)
    {
        _generation++;
        _mins = _maxs = null;
        _duration = 0.0;
        _msg = msg ?? Loc.T("— sin media —", "— no media —");
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
        // La decodificación (pipe a ffmpeg si hace falta) ya vivía en
        // WinCue.Audio.AudioDecoder — compartida, no de esta interfaz. El
        // submuestreo a buckets de min/max SÍ era cálculo de interfaz
        // duplicable y ahora vive en WaveformLayout (WinCue.Presentation).
        var buf = AudioDecoder.Decode(path);
        var peaks = WaveformLayout.ComputePeaks(buf.Samples, buf.Channels, buf.FrameCount);
        return (peaks.Mins, peaks.Maxs, buf.DurationSeconds);
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

    private double SecToX(double sec) => WaveformLayout.SecToX(sec, _duration, ActualWidth);

    private double XToSec(double x) => WaveformLayout.XToSec(x, _duration, ActualWidth);

    private static string Fmt(double s) => WaveformLayout.FormatTime(s);

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
        double eX = SecToX(WaveformLayout.EffectiveEnd(_end, _duration));

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
                    int i = WaveformLayout.ColumnBucketIndex(x, w, n);
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
        foreach (double frac in WaveformLayout.RulerFractions)
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
        double eX = SecToX(WaveformLayout.EffectiveEnd(_end, _duration));
        _drag = WaveformLayout.PickHandle(x, sX, eX, HandleHit) == WaveformHandle.Start ? "start" : "end";
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
            MarkersChanged?.Invoke(_start, WaveformLayout.EffectiveEnd(_end, _duration));
        }
    }

    private void UpdateDrag(double x)
    {
        double sec = XToSec(x);
        if (_drag == "start")
            _start = WaveformLayout.DragStart(sec, _end, _duration);
        else
            _end = WaveformLayout.DragEnd(sec, _start);
        Scrubbing?.Invoke(_drag == "start" ? _start : _end);
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
