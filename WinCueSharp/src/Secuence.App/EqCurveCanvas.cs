// =============================================================================
// EqCurveCanvas.cs — lienzo interactivo del editor de EQ (curva + RTA +
// manejadores), en Win2D.
// =============================================================================
// Equivalente de la clase EqCurveCanvas de src/WinCue.App/EqEditorControl.cs
// (WPF) para esta interfaz, NO una copia (regla 2 del proyecto). El CÁLCULO
// (mapeo de ejes frecuencia↔píxel LOGARÍTMICO y dB↔píxel, muestreo de la
// curva de respuesta —WinCue.Presentation.EqResponse, biquads RBJ, la MISMA
// fórmula que aplica el motor de audio—, balística del RTA y hit-test/
// arrastre de los manejadores) es EXACTAMENTE el mismo que usa WinCue: vive
// en WinCue.Presentation.EqCurveLayout. Este archivo SOLO pinta e interpreta
// el puntero — mismo reparto que GeometryCanvas.cs/WaveformCanvas.cs.
//
// TRADUCCIÓN DE INTERACCIÓN WPF → WinUI 3 (mismo patrón que GeometryCanvas.cs):
// OnMouseDown/Move/Up/Wheel → PointerPressed/Moved/Released/WheelChanged;
// CaptureMouse()/ReleaseMouseCapture() → CapturePointer(e.Pointer)/
// ReleasePointerCapture(e.Pointer); Cursor → ProtectedCursor;
// DrawingContext.DrawGeometry(StreamGeometry) → CanvasDrawingSession.
// DrawGeometry/FillGeometry(CanvasGeometry, ver TimelineCanvas.
// WarningTriangle para el mismo patrón de CanvasPathBuilder).
//
// RTA/GR EN VIVO: la balística (SetSpectrum) y el medidor GR del compresor
// (CompCurveCanvas.SetGainReduction) los alimenta InspectorPanel con un
// DispatcherQueueTimer a ~12 Hz, mismo periodo que el DispatcherTimer de
// WinCue.App.EqEditorControl — MainController.GetAudioSpectrum(cueId)/
// GetAudioGainReduction(cueId)/CueHasAudioAnalysis(cueId) ya son PÚBLICOS y
// los usa WinCue.App.MainWindow tal cual: no hace falta nada nuevo en el
// motor, solo llamarlos desde aquí (ver InspectorPanel.cs para el cableado).
// =============================================================================

using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;
using WinCue.Core;
using WinCue.Presentation;

namespace Secuence.App;

/// <summary>Lienzo interactivo del editor de EQ (curva + RTA + manejadores),
/// en Win2D. Equivalente de <c>WinCue.App.EqCurveCanvas</c> para WinUI 3.</summary>
public sealed class EqCurveCanvas : UserControl
{
    private readonly CanvasControl _canvas = new();

    public Action<string, object?>? Commit;
    public event Action<string>? ReadoutChanged;

    /// <summary>Ganancia de VISUALIZACIÓN del RTA (solo pantalla, nunca el
    /// audio) — estática como en WPF: compartida por sesión, no por cue.</summary>
    public static double RtaGainDb { get; set; }

    private Cue? _cue;
    private float[]? _smooth;
    private float[]? _peakHold;
    private int _drag = EqHandle.None;
    private int _hover = EqHandle.None;

    public EqCurveCanvas()
    {
        MinHeight = 120;
        Content = _canvas;
        _canvas.Draw += OnDraw;
        _canvas.SizeChanged += (_, _) => _canvas.Invalidate();

        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
        PointerCaptureLost += (_, _) =>
        {
            _drag = EqHandle.None;
            _canvas.Invalidate();
        };
    }

    public void SetCue(Cue? cue)
    {
        _cue = cue;
        _smooth = null;
        _peakHold = null;
        _canvas.Invalidate();
    }

    /// <summary>Repinta sin tocar el estado del RTA — para cuando un campo
    /// numérico del inspector cambia un parámetro de ESTE MISMO cue (el
    /// arrastre en el propio lienzo ya se repinta solo).</summary>
    public void Refresh() => _canvas.Invalidate();

    /// <summary>Espectro nuevo (~12 Hz); balística en EqCurveLayout.RtaStep —
    /// ver la cabecera del archivo.</summary>
    public void SetSpectrum(float[]? spectrum)
    {
        if (spectrum is null)
        {
            _smooth = null;
            _peakHold = null;
        }
        else
        {
            (_smooth, _peakHold) = EqCurveLayout.RtaStep(_smooth, _peakHold, spectrum);
        }
        _canvas.Invalidate();
    }

    private (double Freq, double Gain, double Q) Band(int i) => _cue is null
        ? (1000, 0, 1)
        : i switch
        {
            0 => (_cue.Eq1Freq, _cue.Eq1Gain, _cue.Eq1Q),
            1 => (_cue.Eq2Freq, _cue.Eq2Gain, _cue.Eq2Q),
            2 => (_cue.Eq3Freq, _cue.Eq3Gain, _cue.Eq3Q),
            _ => (_cue.Eq4Freq, _cue.Eq4Gain, _cue.Eq4Q),
        };

    private int HitHandle(Point p)
    {
        if (_cue is null)
            return EqHandle.None;
        var bands = new (double Freq, double Gain)[4];
        for (int i = 0; i < 4; i++)
        {
            var (f, g, _) = Band(i);
            bands[i] = (f, g);
        }
        return EqCurveLayout.HitHandle(new GeoPoint(p.X, p.Y), bands,
            _cue.HpfFreq, _cue.LpfFreq, _canvas.ActualWidth, _canvas.ActualHeight);
    }

    // ------------------------------------------------------------------
    // Pintado — colores directos de Palette (mismo convenio que
    // GeometryCanvas.cs/WaveformCanvas.cs: Win2D no pasa por Theme.XxxBrush).
    // ------------------------------------------------------------------

    private static readonly uint[] BandColorRgb = { Palette.Pgm, Palette.Warn, Palette.Go, Palette.Blue };

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        double w = sender.ActualWidth, h = sender.ActualHeight;
        ds.Clear(ToColor(Palette.EqBg));
        if (_cue is null || w < 40)
            return;

        using var gridFormat = new CanvasTextFormat { FontSize = 9 };

        // Rejilla: décadas + subdivisiones y líneas de dB
        foreach (double f in EqCurveLayout.GridFrequencies)
        {
            float x = (float)EqCurveLayout.XFromFreq(f, w);
            ds.DrawLine(x, 0, x, (float)h, ToColor(Palette.EqGrid));
            string label = f >= 1000 ? $"{f / 1000:0}k" : $"{f:0}";
            ds.DrawText(label, x + 2, (float)(h - 13), ToColor(Palette.TextDimmer), gridFormat);
        }
        foreach (double db in EqCurveLayout.GridDbLines)
        {
            float y = (float)EqCurveLayout.YFromDb(db, h);
            ds.DrawLine(0, y, (float)w, y, ToColor(Palette.EqGrid));
            ds.DrawText($"{db:+0;-0}", 2, y - 12, ToColor(Palette.TextDimmer), gridFormat);
        }
        float y0f = (float)EqCurveLayout.YFromDb(0, h);
        ds.DrawLine(0, y0f, (float)w, y0f, ToColor(Palette.EqZero));

        // RTA detrás: área suavizada + picos (puntos ya en píxeles). Relleno
        // PLANO en esta primera pasada (WPF usa un degradado vertical) — el
        // contorno (RtaLine) ya marca la traza con nitidez; el degradado es
        // terminado decorativo, no cálculo, y queda para cuando compense el
        // CanvasLinearGradientBrush completo.
        if (_smooth is { Length: > 1 } spec)
        {
            var areaPts = EqCurveLayout.RtaAreaPoints(spec, RtaGainDb, w, h);
            if (areaPts.Count > 0)
            {
                using var pathBuilder = new CanvasPathBuilder(sender);
                pathBuilder.BeginFigure(ToVector(areaPts[0]));
                for (int i = 1; i < areaPts.Count; i++)
                    pathBuilder.AddLine(ToVector(areaPts[i]));
                pathBuilder.EndFigure(CanvasFigureLoop.Closed);
                using var area = CanvasGeometry.CreatePath(pathBuilder);
                ds.FillGeometry(area, ToColor(Palette.Go, 0x40));
                ds.DrawGeometry(area, ToColor(Palette.RtaLine, 0xdd), 1.3f);
            }

            if (_peakHold is { } pk)
                foreach (var (a, b) in EqCurveLayout.RtaPeakSegments(pk, RtaGainDb, w, h))
                    ds.DrawLine((float)a.X, (float)a.Y, (float)b.X, (float)b.Y, ToColor(Palette.RtaPeak, 0xb4));
        }

        // Curva de respuesta de los filtros: relleno hasta 0 dB + trazo
        var eq = EqCurveLayout.EqParamsFromCue(_cue);
        bool eqOn = _cue.EqEnabled || _cue.HpfOn || _cue.LpfOn;
        var curvePts = EqCurveLayout.ResponseCurve(eq, w, h);
        if (curvePts.Count > 1)
        {
            using var linePath = new CanvasPathBuilder(sender);
            linePath.BeginFigure(ToVector(curvePts[0]));
            for (int i = 1; i < curvePts.Count; i++)
                linePath.AddLine(ToVector(curvePts[i]));
            linePath.EndFigure(CanvasFigureLoop.Open);
            using var lineGeo = CanvasGeometry.CreatePath(linePath);

            if (eqOn)
            {
                using var areaPath = new CanvasPathBuilder(sender);
                areaPath.BeginFigure((float)0, y0f);
                foreach (var p in curvePts)
                    areaPath.AddLine(ToVector(p));
                areaPath.AddLine((float)w, y0f);
                areaPath.EndFigure(CanvasFigureLoop.Closed);
                using var areaGeo = CanvasGeometry.CreatePath(areaPath);
                ds.FillGeometry(areaGeo, ToColor(Palette.Standby, 0x22));
                ds.DrawGeometry(lineGeo, ToColor(Palette.Standby, 0x46), 5.5f);
            }
            ds.DrawGeometry(lineGeo, eqOn ? ToColor(Palette.Standby) : ToColor(Palette.EqCurveOff), eqOn ? 2.2f : 1.5f);
        }

        // Manejadores HPF/LPF (cuadrados en la línea de 0 dB)
        DrawFilterHandle(ds, EqHandle.Hpf, _cue.HpfFreq, _cue.HpfOn, "HPF", w, h);
        DrawFilterHandle(ds, EqHandle.Lpf, _cue.LpfFreq, _cue.LpfOn, "LPF", w, h);

        // Manejadores de banda: anillo de color + centro oscuro
        using var numFormat = new CanvasTextFormat
        {
            FontSize = 9.5f,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        for (int i = 0; i < 4; i++)
        {
            var (f, g, _) = Band(i);
            var center = EqCurveLayout.BandPoint(f, g, w, h);
            byte alpha = _cue.EqEnabled ? (byte)235 : (byte)90;
            uint c = BandColorRgb[i];
            bool hot = i == _drag || i == _hover;
            float r = hot ? 8.5f : 7f;
            if (hot)
                ds.FillCircle((float)center.X, (float)center.Y, r + 5, ToColor(c, 0x38));
            ds.FillCircle((float)center.X, (float)center.Y, r, ToColor(Palette.EqHandleCore, _cue.EqEnabled ? (byte)0xdd : (byte)0x66));
            ds.DrawCircle((float)center.X, (float)center.Y, r, ToColor(c, alpha), 2.4f);
            ds.DrawText($"{i + 1}", (float)center.X, (float)center.Y, ToColor(Palette.White, alpha), numFormat);
        }
    }

    private static void DrawFilterHandle(CanvasDrawingSession ds, int id, double freq, bool on, string label, double w, double h)
    {
        var p = EqCurveLayout.FilterPoint(freq, w, h);
        var color = ToColor(Palette.TextTertiary, on ? (byte)220 : (byte)80);
        float s = 5;
        ds.FillRectangle(new Rect(p.X - s, p.Y - s, s * 2, s * 2), color);
        using var format = new CanvasTextFormat { FontSize = 8 };
        ds.DrawText(label, (float)(p.X - 9), (float)(p.Y + s + 1), ToColor(Palette.TextDimmer), format);
    }

    private static System.Numerics.Vector2 ToVector(GeoPoint p) => new((float)p.X, (float)p.Y);

    private static Color ToColor(uint rgb, byte alpha = 0xff) => Color.FromArgb(
        alpha, (byte)((rgb >> 16) & 0xff), (byte)((rgb >> 8) & 0xff), (byte)(rgb & 0xff));

    // ------------------------------------------------------------------
    // Interacción — cálculo (freq/gain de arrastre, Q de rueda) en
    // EqCurveLayout; aquí solo la traducción de eventos de puntero.
    // ------------------------------------------------------------------

    private void OnPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_cue is null)
            return;
        var p = e.GetCurrentPoint(this).Position;
        _drag = HitHandle(p);
        if (_drag != EqHandle.None)
        {
            CapturePointer(e.Pointer);
            UpdateReadout();
            _canvas.Invalidate();
        }
    }

    private void OnPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(this).Position;
        if (_drag == EqHandle.None)
        {
            int h = HitHandle(p);
            if (h != _hover)
            {
                _hover = h;
                _canvas.Invalidate();
            }
            ProtectedCursor = InputSystemCursor.Create(
                h != EqHandle.None ? InputSystemCursorShape.SizeAll : InputSystemCursorShape.Arrow);
            return;
        }
        if (_cue is null)
            return;

        double freq = EqCurveLayout.FreqFromDragX(p.X, _canvas.ActualWidth);
        if (_drag >= 0)
        {
            double gain = EqCurveLayout.GainFromDragY(p.Y, _canvas.ActualHeight);
            Commit?.Invoke($"eq{_drag + 1}_freq", freq);
            Commit?.Invoke($"eq{_drag + 1}_gain", gain);
        }
        else if (_drag == EqHandle.Hpf)
        {
            Commit?.Invoke("hpf_freq", EqCurveLayout.ClampHpfFreq(freq));
        }
        else
        {
            Commit?.Invoke("lpf_freq", EqCurveLayout.ClampLpfFreq(freq));
        }
        UpdateReadout();
        _canvas.Invalidate();
    }

    private void OnPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_drag != EqHandle.None)
        {
            _drag = EqHandle.None;
            ReleasePointerCapture(e.Pointer);
            _canvas.Invalidate();
        }
    }

    /// <summary>Rueda sobre una banda (o arrastrándola): ajustar su Q.</summary>
    private void OnPointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        int target = _drag != EqHandle.None ? _drag : HitHandle(pt.Position);
        if (_cue is null || target < 0)
            return;
        var (_, _, q) = Band(target);
        double newQ = EqCurveLayout.QFromWheel(q, pt.Properties.MouseWheelDelta > 0);
        Commit?.Invoke($"eq{target + 1}_q", newQ);
        UpdateReadout(target);
        _canvas.Invalidate();
        e.Handled = true;
    }

    private void UpdateReadout(int? which = null)
    {
        if (_cue is null)
            return;
        int id = which ?? _drag;
        string text = id switch
        {
            >= 0 and <= 3 => FormatBand(id),
            EqHandle.Hpf => $"HPF {_cue.HpfFreq:0} Hz",
            EqHandle.Lpf => $"LPF {_cue.LpfFreq:0} Hz",
            _ => "",
        };
        if (text.Length > 0)
            ReadoutChanged?.Invoke(text);
    }

    private string FormatBand(int i)
    {
        var (f, g, q) = Band(i);
        return $"Banda {i + 1}: {f:0} Hz · {g:+0.0;-0.0;0.0} dB · Q {q:0.00}";
    }
}
