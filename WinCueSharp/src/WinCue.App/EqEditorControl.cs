// =============================================================================
// EqEditorControl.cs — Pestaña EQ del inspector: ganancia de clip + EQ
// paramétrica de 4 bandas con HPF/LPF y RTA
// =============================================================================
// Editor estilo consola: curva de respuesta arrastrable sobre eje log
// 20 Hz–20 kHz / ±18 dB con el analizador (RTA) detrás. Cada banda es un
// punto de color: arrastrar = frecuencia+ganancia, rueda = Q. HPF/LPF son
// manejadores en la línea de 0 dB (solo frecuencia). Los commits van por el
// flujo normal (undo con coalescencia) y el controlador los aplica EN VIVO
// al cue sonando y a la audición → se ecualiza escuchando.
// =============================================================================

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinCue.Audio;
using WinCue.Core;

namespace WinCue.App;

public sealed class EqEditorControl : ContentControl
{
    /// <summary>Commit de un campo snake_case del cue actual.</summary>
    public Action<string, object?>? Commit;

    /// <summary>RTA: espectro en dBFS del cue actual (null = no suena).</summary>
    public Func<float[]?>? SpectrumProvider;

    private Cue? _cue;
    private bool _loading;

    private readonly EqCurveCanvas _canvas = new();
    private readonly Slider _gain;
    private readonly TextBlock _gainLabel;
    private readonly CheckBox _eqOn, _hpfOn, _lpfOn;
    private readonly TextBox _hpfFreq, _lpfFreq;
    private readonly TextBlock _readout;
    private readonly TextBlock _noAudio;
    private readonly Grid _root;
    private readonly DispatcherTimer _rta;

    public EqEditorControl()
    {
        _root = new Grid { Margin = new Thickness(10, 6, 10, 8) };
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
        _root.ColumnDefinitions.Add(new ColumnDefinition());
        _root.RowDefinitions.Add(new RowDefinition());
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── Fader de ganancia del clip (dB) ────────────────────────────
        var gainPanel = new DockPanel { Margin = new Thickness(0, 0, 10, 0) };
        var gainTitle = new TextBlock
        {
            Text = "GANANCIA",
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        DockPanel.SetDock(gainTitle, Dock.Top);
        gainPanel.Children.Add(gainTitle);
        _gainLabel = new TextBlock
        {
            Text = "0.0 dB",
            FontFamily = new FontFamily("Consolas"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xe8, 0xc0, 0x00)),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        DockPanel.SetDock(_gainLabel, Dock.Bottom);
        gainPanel.Children.Add(_gainLabel);
        _gain = new Slider
        {
            Orientation = Orientation.Vertical,
            Minimum = -24,
            Maximum = 12,
            TickFrequency = 3,
            IsSnapToTickEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _gain.ValueChanged += (_, _) =>
        {
            _gainLabel.Text = $"{_gain.Value:+0.0;-0.0;0.0} dB";
            if (!_loading)
                Commit?.Invoke("gain_db", Math.Round(_gain.Value, 1));
            _canvas.InvalidateVisual();
        };
        _gain.MouseDoubleClick += (_, _) => _gain.Value = 0.0;   // reset rápido
        gainPanel.Children.Add(_gain);
        Grid.SetColumn(gainPanel, 0);
        Grid.SetRowSpan(gainPanel, 2);
        _root.Children.Add(gainPanel);

        // ── Curva + RTA ────────────────────────────────────────────────
        Grid.SetColumn(_canvas, 1);
        Grid.SetRow(_canvas, 0);
        _root.Children.Add(_canvas);
        _canvas.Commit = (k, v) => { if (!_loading) Commit?.Invoke(k, v); };
        _canvas.ReadoutChanged += text => _readout!.Text = text;

        // ── Fila inferior: toggles + frecuencias + lectura de banda ────
        var bottom = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        _eqOn = MakeCheck("EQ", "eq_enabled");
        _hpfOn = MakeCheck("HPF", "hpf_on");
        _hpfFreq = MakeFreqBox("hpf_freq");
        _lpfOn = MakeCheck("LPF", "lpf_on");
        _lpfFreq = MakeFreqBox("lpf_freq");
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(_eqOn);
        left.Children.Add(_hpfOn);
        left.Children.Add(_hpfFreq);
        left.Children.Add(Hz());
        left.Children.Add(_lpfOn);
        left.Children.Add(_lpfFreq);
        left.Children.Add(Hz());

        // Ganancia de VISUALIZACIÓN del RTA (no toca el audio)
        left.Children.Add(new TextBlock
        {
            Text = "RTA +",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 3, 0),
        });
        var rtaGain = new Slider
        {
            Minimum = 0,
            Maximum = 48,
            Value = EqCurveCanvas.RtaGainDb,
            Width = 90,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Ganancia de visualización del analizador (solo pantalla)",
        };
        var rtaLabel = new TextBlock
        {
            Text = $"{EqCurveCanvas.RtaGainDb:0} dB",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 0, 0),
        };
        rtaGain.ValueChanged += (_, _) =>
        {
            EqCurveCanvas.RtaGainDb = rtaGain.Value;
            rtaLabel.Text = $"{rtaGain.Value:0} dB";
            _canvas.InvalidateVisual();
        };
        left.Children.Add(rtaGain);
        left.Children.Add(rtaLabel);
        bottom.Children.Add(left);
        _readout = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Text = "arrastra un punto: frec/ganancia · rueda: Q",
        };
        bottom.Children.Add(_readout);
        Grid.SetColumn(bottom, 1);
        Grid.SetRow(bottom, 1);
        _root.Children.Add(bottom);

        _noAudio = new TextBlock
        {
            Text = "La EQ paramétrica es para cues de AUDIO.",
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Content = _root;

        // RTA ~12 Hz mientras la pestaña está visible
        _rta = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _rta.Tick += (_, _) =>
        {
            if (IsVisible && _cue is { Type: CueType.Audio })
                _canvas.SetSpectrum(SpectrumProvider?.Invoke());
        };
        _rta.Start();
    }

    private CheckBox MakeCheck(string label, string key)
    {
        var cb = new CheckBox
        {
            Content = label,
            Foreground = new SolidColorBrush(Color.FromRgb(0xdd, 0xdd, 0xdd)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        cb.Checked += (_, _) => { if (!_loading) Commit?.Invoke(key, true); _canvas.InvalidateVisual(); };
        cb.Unchecked += (_, _) => { if (!_loading) Commit?.Invoke(key, false); _canvas.InvalidateVisual(); };
        return cb;
    }

    private TextBox MakeFreqBox(string key)
    {
        var box = new TextBox
        {
            Width = 52,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xdd, 0xdd, 0xdd)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
        };
        void CommitBox()
        {
            if (_loading || !double.TryParse(box.Text,
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double f))
                return;
            Commit?.Invoke(key, Math.Clamp(f, 20.0, 20000.0));
            _canvas.InvalidateVisual();
        }
        box.LostFocus += (_, _) => CommitBox();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitBox(); };
        return box;
    }

    private static TextBlock Hz() => new()
    {
        Text = "Hz",
        Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(2, 0, 10, 0),
    };

    /// <summary>Repuebla el editor con el cue dado (solo al cambiar selección).</summary>
    public void SetCue(Cue? cue)
    {
        _cue = cue;
        _canvas.SetCue(cue);
        if (cue is not { Type: CueType.Audio })
        {
            Content = _noAudio;
            return;
        }
        Content = _root;
        _loading = true;
        try
        {
            _gain.Value = cue.GainDb;
            _gainLabel.Text = $"{cue.GainDb:+0.0;-0.0;0.0} dB";
            _eqOn.IsChecked = cue.EqEnabled;
            _hpfOn.IsChecked = cue.HpfOn;
            _lpfOn.IsChecked = cue.LpfOn;
            _hpfFreq.Text = cue.HpfFreq.ToString("0", CultureInfo.InvariantCulture);
            _lpfFreq.Text = cue.LpfFreq.ToString("0", CultureInfo.InvariantCulture);
        }
        finally
        {
            _loading = false;
        }
    }
}

/// <summary>Lienzo del editor: rejilla log, RTA, curva de respuesta y
/// manejadores arrastrables (4 bandas + HPF/LPF).</summary>
public sealed class EqCurveCanvas : FrameworkElement
{
    private const double DbRange = 18.0;   // eje Y: ±18 dB
    private const double FMin = 20.0, FMax = 20000.0;

    private static readonly Brush BgBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x16)));
    private static readonly Pen GridPen = FrozenPen(Color.FromRgb(0x2c, 0x2c, 0x30), 1);
    private static readonly Pen ZeroPen = FrozenPen(Color.FromRgb(0x4a, 0x4a, 0x50), 1);
    private static readonly Brush GridText = Frozen(new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)));
    private static readonly Brush RtaBar = Frozen(new SolidColorBrush(Color.FromArgb(120, 0x2e, 0xcc, 0x71)));
    private static readonly Brush RtaBarTop = Frozen(new SolidColorBrush(Color.FromArgb(220, 0x5f, 0xf2, 0x9e)));

    /// <summary>Ganancia de VISUALIZACIÓN del RTA en dB (compartida por
    /// sesión; solo afecta a la pantalla, nunca al audio).</summary>
    public static double RtaGainDb = 18.0;
    private static readonly Pen CurvePen = FrozenPen(Color.FromRgb(0xe8, 0xc0, 0x00), 2);
    private static readonly Pen CurveOffPen = FrozenPen(Color.FromRgb(0x66, 0x5e, 0x30), 1.5);

    private static readonly Color[] BandColors =
    {
        Color.FromRgb(0xc0, 0x39, 0x2b),   // 1 grave — rojo
        Color.FromRgb(0xe8, 0xa2, 0x00),   // 2 medio-grave — ámbar
        Color.FromRgb(0x2e, 0xcc, 0x71),   // 3 medio-agudo — verde
        Color.FromRgb(0x2a, 0x6f, 0xb0),   // 4 agudo — azul
    };

    public Action<string, object?>? Commit;
    public event Action<string>? ReadoutChanged;

    private Cue? _cue;
    private float[]? _spectrum;
    private int _drag = int.MinValue;      // 0..3 banda · -2 HPF · -3 LPF
    private int _hover = int.MinValue;

    public EqCurveCanvas()
    {
        ClipToBounds = true;
        MinHeight = 120;
    }

    public void SetCue(Cue? cue)
    {
        _cue = cue;
        _spectrum = null;
        InvalidateVisual();
    }

    public void SetSpectrum(float[]? spectrum)
    {
        _spectrum = spectrum;
        InvalidateVisual();
    }

    // ── Mapeos de ejes ─────────────────────────────────────────────────
    private double XFromFreq(double f) =>
        Math.Log10(Math.Clamp(f, FMin, FMax) / FMin) / 3.0 * ActualWidth;
    private double FreqFromX(double x) =>
        FMin * Math.Pow(10.0, 3.0 * Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1));
    private double YFromDb(double db) =>
        ActualHeight / 2.0 - db / DbRange * (ActualHeight / 2.0 - 6);
    private double DbFromY(double y) =>
        (ActualHeight / 2.0 - y) / (ActualHeight / 2.0 - 6) * DbRange;

    private static AudioEqParams ParamsFrom(Cue c) => new(
        GainDb: 0.0,   // la curva muestra solo filtros; la ganancia va al fader
        EqEnabled: c.EqEnabled,
        HpfOn: c.HpfOn, HpfFreq: c.HpfFreq,
        LpfOn: c.LpfOn, LpfFreq: c.LpfFreq,
        Bands: new[]
        {
            new EqBand(c.Eq1Freq, c.Eq1Gain, c.Eq1Q),
            new EqBand(c.Eq2Freq, c.Eq2Gain, c.Eq2Q),
            new EqBand(c.Eq3Freq, c.Eq3Gain, c.Eq3Q),
            new EqBand(c.Eq4Freq, c.Eq4Gain, c.Eq4Q),
        });

    private (double Freq, double Gain, double Q) Band(int i) => _cue is null
        ? (1000, 0, 1)
        : i switch
        {
            0 => (_cue.Eq1Freq, _cue.Eq1Gain, _cue.Eq1Q),
            1 => (_cue.Eq2Freq, _cue.Eq2Gain, _cue.Eq2Q),
            2 => (_cue.Eq3Freq, _cue.Eq3Gain, _cue.Eq3Q),
            _ => (_cue.Eq4Freq, _cue.Eq4Gain, _cue.Eq4Q),
        };

    // ── Pintado ────────────────────────────────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(BgBrush, null, new Rect(0, 0, w, h));
        if (_cue is null || w < 40)
            return;

        // Rejilla: décadas + subdivisiones y líneas de dB
        foreach (double f in new[] { 20.0, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 })
        {
            double x = XFromFreq(f);
            dc.DrawLine(GridPen, new Point(x, 0), new Point(x, h));
            string label = f >= 1000 ? $"{f / 1000:0}k" : $"{f:0}";
            dc.DrawText(Text(label, GridText, 9), new Point(x + 2, h - 13));
        }
        foreach (double db in new[] { -12.0, -6, 6, 12 })
        {
            double y = YFromDb(db);
            dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));
            dc.DrawText(Text($"{db:+0;-0}", GridText, 9), new Point(2, y - 12));
        }
        dc.DrawLine(ZeroPen, new Point(0, YFromDb(0)), new Point(w, YFromDb(0)));

        // RTA detrás: BARRAS por banda (dBFS -90..0 + ganancia de pantalla)
        if (_spectrum is { Length: > 0 } spec)
        {
            double bw = w / spec.Length;
            for (int i = 0; i < spec.Length; i++)
            {
                double db = spec[i] + RtaGainDb;
                double y = h * (1.0 - Math.Clamp((db + 90.0) / 90.0, 0.0, 1.0));
                if (h - y < 1)
                    continue;
                dc.DrawRectangle(RtaBar, null,
                    new Rect(i * bw + 0.5, y, Math.Max(1, bw - 1), h - y));
                // Remate más brillante en la punta de la barra
                dc.DrawRectangle(RtaBarTop, null,
                    new Rect(i * bw + 0.5, y, Math.Max(1, bw - 1), 2));
            }
        }

        // Curva de respuesta de los filtros
        var eq = ParamsFrom(_cue);
        var pen = _cue.EqEnabled || _cue.HpfOn || _cue.LpfOn ? CurvePen : CurveOffPen;
        var curve = new StreamGeometry();
        using (var ctx = curve.Open())
        {
            bool started = false;
            for (int px = 0; px <= (int)w; px += 3)
            {
                double db = EqChain.ResponseDb(eq, FreqFromX(px));
                var pt = new Point(px, Math.Clamp(YFromDb(db), -20, h + 20));
                if (!started) { ctx.BeginFigure(pt, false, false); started = true; }
                else ctx.LineTo(pt, true, false);
            }
        }
        curve.Freeze();
        dc.DrawGeometry(null, pen, curve);

        // Manejadores HPF/LPF (cuadrados en la línea de 0 dB)
        DrawFilterHandle(dc, -2, _cue.HpfFreq, _cue.HpfOn, "HPF");
        DrawFilterHandle(dc, -3, _cue.LpfFreq, _cue.LpfOn, "LPF");

        // Manejadores de banda
        for (int i = 0; i < 4; i++)
        {
            var (f, g, _) = Band(i);
            var center = new Point(XFromFreq(f), YFromDb(g));
            var fill = new SolidColorBrush(Color.FromArgb(
                _cue.EqEnabled ? (byte)230 : (byte)90,
                BandColors[i].R, BandColors[i].G, BandColors[i].B));
            fill.Freeze();
            double r = i == _drag || i == _hover ? 8 : 6;
            dc.DrawEllipse(fill, null, center, r, r);
            dc.DrawText(Text($"{i + 1}", Frozen(new SolidColorBrush(Colors.White)), 9),
                new Point(center.X - 3, center.Y - 7));
        }
    }

    private void DrawFilterHandle(DrawingContext dc, int id, double freq, bool on, string label)
    {
        double x = XFromFreq(freq), y = YFromDb(0);
        var brush = new SolidColorBrush(Color.FromArgb(on ? (byte)220 : (byte)80,
            0xbb, 0xbb, 0xbb));
        brush.Freeze();
        double s = id == _drag || id == _hover ? 7 : 5;
        dc.DrawRectangle(brush, null, new Rect(x - s, y - s, s * 2, s * 2));
        dc.DrawText(Text(label, GridText, 8), new Point(x - 9, y + s + 1));
    }

    private static FormattedText Text(string t, Brush b, double size) => new(
        t, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI"), size, b, 1.0);

    // ── Interacción ────────────────────────────────────────────────────

    private int HitHandle(Point p)
    {
        if (_cue is null)
            return int.MinValue;
        for (int i = 0; i < 4; i++)
        {
            var (f, g, _) = Band(i);
            if ((new Point(XFromFreq(f), YFromDb(g)) - p).Length <= 10)
                return i;
        }
        if ((new Point(XFromFreq(_cue.HpfFreq), YFromDb(0)) - p).Length <= 10)
            return -2;
        if ((new Point(XFromFreq(_cue.LpfFreq), YFromDb(0)) - p).Length <= 10)
            return -3;
        return int.MinValue;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _cue is null)
            return;
        _drag = HitHandle(e.GetPosition(this));
        if (_drag != int.MinValue)
        {
            CaptureMouse();
            UpdateReadout();
            InvalidateVisual();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_drag == int.MinValue)
        {
            int h = HitHandle(p);
            if (h != _hover)
            {
                _hover = h;
                InvalidateVisual();
            }
            Cursor = h != int.MinValue ? Cursors.SizeAll : Cursors.Arrow;
            return;
        }
        if (_cue is null)
            return;

        double freq = Math.Round(FreqFromX(p.X));
        if (_drag >= 0)
        {
            double gain = Math.Round(Math.Clamp(DbFromY(p.Y), -DbRange, DbRange), 1);
            Commit?.Invoke($"eq{_drag + 1}_freq", freq);
            Commit?.Invoke($"eq{_drag + 1}_gain", gain);
        }
        else if (_drag == -2)
        {
            Commit?.Invoke("hpf_freq", Math.Clamp(freq, 20, 2000));
        }
        else
        {
            Commit?.Invoke("lpf_freq", Math.Clamp(freq, 200, 20000));
        }
        UpdateReadout();
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_drag != int.MinValue)
        {
            _drag = int.MinValue;
            ReleaseMouseCapture();
            InvalidateVisual();
        }
    }

    /// <summary>Rueda sobre una banda (o arrastrándola): ajustar su Q.</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        int target = _drag != int.MinValue ? _drag : HitHandle(e.GetPosition(this));
        if (_cue is null || target < 0)
            return;
        var (_, _, q) = Band(target);
        double newQ = Math.Round(Math.Clamp(
            q * (e.Delta > 0 ? 1.12 : 1 / 1.12), 0.1, 12.0), 2);
        Commit?.Invoke($"eq{target + 1}_q", newQ);
        UpdateReadout(target);
        InvalidateVisual();
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
            -2 => $"HPF {_cue.HpfFreq:0} Hz",
            -3 => $"LPF {_cue.LpfFreq:0} Hz",
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

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    private static Pen FrozenPen(Color c, double width)
    {
        var p = new Pen(new SolidColorBrush(c), width);
        p.Freeze();
        return p;
    }
}
