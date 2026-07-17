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

    /// <summary>Reducción de ganancia del compresor (dB ≥ 0) para el GR.</summary>
    public Func<float>? GainReductionProvider;

    private Cue? _cue;
    private bool _loading;

    private readonly EqCurveCanvas _canvas = new();
    private readonly CompCurveCanvas _compCanvas = new();
    private readonly Slider _gain;
    private readonly TextBlock _gainLabel;
    private readonly CheckBox _eqOn, _hpfOn, _lpfOn;
    private readonly TextBox _hpfFreq, _lpfFreq;
    private CheckBox _compOn = null!;
    private TextBox _compThr = null!, _compRatio = null!, _compAtt = null!,
        _compRel = null!, _compMakeup = null!;
    private readonly TextBox[] _bFreq = new TextBox[4];
    private readonly TextBox[] _bGain = new TextBox[4];
    private readonly TextBox[] _bQ = new TextBox[4];
    private readonly TextBlock _readout;
    private readonly TextBlock _noAudio;
    private readonly Grid _root;
    private readonly DispatcherTimer _rta;

    private static readonly Color[] BandUiColors =
    {
        Color.FromRgb(0xc0, 0x39, 0x2b),
        Color.FromRgb(0xe8, 0xa2, 0x00),
        Color.FromRgb(0x2e, 0xcc, 0x71),
        Color.FromRgb(0x2a, 0x6f, 0xb0),
    };

    public EqEditorControl()
    {
        // Distribución: las DOS herramientas lado a lado, cada una con SUS
        // parámetros en columna a la IZQUIERDA de su gráfica — así las
        // gráficas quedan a altura completa. Fader de clip al principio.
        //   [GANANCIA] [params EQ][gráfica EQ] [params COMP][gráfica COMP]
        _root = new Grid { Margin = new Thickness(10, 6, 10, 8) };
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 220 });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 180 });
        _root.RowDefinitions.Add(new RowDefinition());

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

        _canvas.Commit = (k, v) =>
        {
            if (_loading) return;
            Commit?.Invoke(k, v);
            RefreshParams();
        };
        _compCanvas.Commit = (k, v) =>
        {
            if (_loading) return;
            Commit?.Invoke(k, v);
            RefreshParams();
        };

        // ── HERRAMIENTA 1: ECUALIZADOR — parámetros en columna + gráfica ─
        _eqOn = MakeCheck("EQ activa", "eq_enabled");
        _hpfOn = MakeCheck("HPF", "hpf_on");
        _hpfFreq = MakeFreqBox("hpf_freq");
        _lpfOn = MakeCheck("LPF", "lpf_on");
        _lpfFreq = MakeFreqBox("lpf_freq");

        var eqPars = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        eqPars.Children.Add(_eqOn);
        var hpfRow = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        hpfRow.Children.Add(_hpfOn);
        hpfRow.Children.Add(_hpfFreq);
        hpfRow.Children.Add(Hz());
        eqPars.Children.Add(hpfRow);
        var lpfRow = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        lpfRow.Children.Add(_lpfOn);
        lpfRow.Children.Add(_lpfFreq);
        lpfRow.Children.Add(Hz());
        eqPars.Children.Add(lpfRow);

        // Las 4 bandas, una fila por banda (punto de color + F/G/Q)
        for (int i = 0; i < 4; i++)
        {
            var row = new StackPanel
            { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            row.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 10, Height = 10,
                Fill = new SolidColorBrush(BandUiColors[i]),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            });
            _bFreq[i] = MakeNumBox($"eq{i + 1}_freq", 20, 20000);
            _bFreq[i].Width = 50;
            row.Children.Add(_bFreq[i]);
            row.Children.Add(FieldLabel("Hz"));
            _bGain[i] = MakeNumBox($"eq{i + 1}_gain", -18, 18);
            _bGain[i].Width = 40;
            row.Children.Add(_bGain[i]);
            row.Children.Add(FieldLabel("dB"));
            _bQ[i] = MakeNumBox($"eq{i + 1}_q", 0.1, 12);
            _bQ[i].Width = 36;
            row.Children.Add(_bQ[i]);
            row.Children.Add(FieldLabel("Q"));
            eqPars.Children.Add(row);
        }

        // Ganancia de VISUALIZACIÓN del RTA (no toca el audio)
        var rtaRow = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        rtaRow.Children.Add(new TextBlock
        {
            Text = "RTA +",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 3, 0),
        });
        var rtaGain = new Slider
        {
            Minimum = 0,
            Maximum = 48,
            Value = EqCurveCanvas.RtaGainDb,
            Width = 80,
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
        rtaRow.Children.Add(rtaGain);
        rtaRow.Children.Add(rtaLabel);
        eqPars.Children.Add(rtaRow);

        _readout = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 190,
            Text = "arrastrar punto = frec/gan · rueda = Q",
        };
        eqPars.Children.Add(_readout);

        var eqParsScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = eqPars,
        };
        Grid.SetColumn(eqParsScroll, 1);
        _root.Children.Add(eqParsScroll);

        Grid.SetColumn(_canvas, 2);
        _root.Children.Add(_canvas);
        _canvas.ReadoutChanged += text => _readout!.Text = text;

        // ── HERRAMIENTA 2: COMPRESOR — parámetros en columna + gráfica ──
        var compPars = new StackPanel { Margin = new Thickness(14, 0, 8, 0) };
        _compOn = MakeCheck("Compresor", "comp_enabled");
        compPars.Children.Add(_compOn);
        _compThr = MakeNumBox("comp_threshold", -60, 0);
        compPars.Children.Add(CompRow("Umbral", _compThr, "dB"));
        _compRatio = MakeNumBox("comp_ratio", 1, 20);
        compPars.Children.Add(CompRow("Ratio", _compRatio, ":1"));
        _compAtt = MakeNumBox("comp_attack", 0.1, 200);
        compPars.Children.Add(CompRow("Ataque", _compAtt, "ms"));
        _compRel = MakeNumBox("comp_release", 5, 2000);
        compPars.Children.Add(CompRow("Release", _compRel, "ms"));
        _compMakeup = MakeNumBox("comp_makeup", 0, 24);
        compPars.Children.Add(CompRow("Makeup", _compMakeup, "dB"));
        compPars.Children.Add(new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 130,
            Text = "arrastrar = umbral · rueda = ratio · GR = reducción en vivo",
        });
        var compParsScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = compPars,
        };
        Grid.SetColumn(compParsScroll, 3);
        _root.Children.Add(compParsScroll);

        Grid.SetColumn(_compCanvas, 4);
        _root.Children.Add(_compCanvas);

        _noAudio = new TextBlock
        {
            Text = "La EQ paramétrica es para cues de AUDIO.",
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Content = _root;

        // RTA + medidor GR ~12 Hz mientras la pestaña está visible
        _rta = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _rta.Tick += (_, _) =>
        {
            if (IsVisible && _cue is { Type: CueType.Audio })
            {
                _canvas.SetSpectrum(SpectrumProvider?.Invoke());
                _compCanvas.SetGainReduction(GainReductionProvider?.Invoke() ?? 0f);
            }
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
        cb.Checked += (_, _) =>
        {
            if (!_loading) Commit?.Invoke(key, true);
            _canvas.InvalidateVisual();
            _compCanvas.InvalidateVisual();
        };
        cb.Unchecked += (_, _) =>
        {
            if (!_loading) Commit?.Invoke(key, false);
            _canvas.InvalidateVisual();
            _compCanvas.InvalidateVisual();
        };
        return cb;
    }

    private static StackPanel CompRow(string label, TextBox box, string unit)
    {
        var row = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        var lbl = FieldLabel(label);
        lbl.Width = 50;
        row.Children.Add(lbl);
        row.Children.Add(box);
        row.Children.Add(FieldLabel(unit));
        return row;
    }

    private static TextBlock FieldLabel(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(4, 0, 3, 0),
        FontSize = 11,
    };

    private TextBox MakeNumBox(string key, double min, double max)
    {
        var box = new TextBox
        {
            Width = 44,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xdd, 0xdd, 0xdd)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
        };
        void CommitBox()
        {
            if (_loading || !double.TryParse(box.Text.Replace(',', '.'),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return;
            Commit?.Invoke(key, Math.Clamp(v, min, max));
            _canvas.InvalidateVisual();
            _compCanvas.InvalidateVisual();
        }
        box.LostFocus += (_, _) => CommitBox();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitBox(); };
        return box;
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
        _compCanvas.SetCue(cue);
        if (cue is not { Type: CueType.Audio })
        {
            Content = _noAudio;
            return;
        }
        Content = _root;
        RefreshParams();
    }

    /// <summary>Vuelca los valores del cue en TODAS las cajas numéricas (al
    /// seleccionar y tras cada arrastre en las gráficas, que mutan el cue).</summary>
    private void RefreshParams()
    {
        if (_cue is not { Type: CueType.Audio } cue)
            return;
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
            for (int i = 0; i < 4; i++)
            {
                var (f, g, q) = (i switch
                {
                    0 => (cue.Eq1Freq, cue.Eq1Gain, cue.Eq1Q),
                    1 => (cue.Eq2Freq, cue.Eq2Gain, cue.Eq2Q),
                    2 => (cue.Eq3Freq, cue.Eq3Gain, cue.Eq3Q),
                    _ => (cue.Eq4Freq, cue.Eq4Gain, cue.Eq4Q),
                });
                _bFreq[i].Text = f.ToString("0", CultureInfo.InvariantCulture);
                _bGain[i].Text = g.ToString("0.#", CultureInfo.InvariantCulture);
                _bQ[i].Text = q.ToString("0.##", CultureInfo.InvariantCulture);
            }
            _compOn.IsChecked = cue.CompEnabled;
            _compThr.Text = cue.CompThreshold.ToString("0.#", CultureInfo.InvariantCulture);
            _compRatio.Text = cue.CompRatio.ToString("0.#", CultureInfo.InvariantCulture);
            _compAtt.Text = cue.CompAttack.ToString("0.#", CultureInfo.InvariantCulture);
            _compRel.Text = cue.CompRelease.ToString("0", CultureInfo.InvariantCulture);
            _compMakeup.Text = cue.CompMakeup.ToString("0.#", CultureInfo.InvariantCulture);
        }
        finally
        {
            _loading = false;
        }
        _compCanvas.InvalidateVisual();
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

/// <summary>Gráfica del COMPRESOR: curva de transferencia entrada/salida en
/// dB (−60..0) con el umbral arrastrable (rueda = ratio) y, a la derecha, el
/// medidor GR en vivo (cuánta reducción está aplicando ahora mismo).</summary>
public sealed class CompCurveCanvas : FrameworkElement
{
    private const double DbMin = -60.0;
    private const double GrScaleDb = 24.0;   // fondo de escala del medidor
    private const double MeterW = 16;

    private static readonly Brush BgBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x16)));
    private static readonly Pen GridPen = FrozenPen(Color.FromRgb(0x2c, 0x2c, 0x30), 1);
    private static readonly Pen UnityPen = MakeUnityPen();
    private static readonly Pen CurvePen = FrozenPen(Color.FromRgb(0xe8, 0xa2, 0x00), 2);
    private static readonly Pen CurveOffPen = FrozenPen(Color.FromRgb(0x6e, 0x58, 0x28), 1.5);
    private static readonly Brush GridText = Frozen(new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)));
    private static readonly Brush ThrBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xdd, 0xdd, 0xdd)));
    private static readonly Brush GrBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xe8, 0x7a, 0x00)));
    private static readonly Brush GrBg = Frozen(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x24)));

    private static Pen MakeUnityPen()
    {
        var p = new Pen(new SolidColorBrush(Color.FromRgb(0x4a, 0x4a, 0x50)), 1)
        { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
        p.Freeze();
        return p;
    }

    public Action<string, object?>? Commit;

    private Cue? _cue;
    private float _gr;
    private bool _drag;

    public CompCurveCanvas()
    {
        ClipToBounds = true;
        MinHeight = 90;
    }

    public void SetCue(Cue? cue)
    {
        _cue = cue;
        _gr = 0f;
        InvalidateVisual();
    }

    /// <summary>Reducción de ganancia actual (dB ≥ 0) — solo repinta si cambia.</summary>
    public void SetGainReduction(float grDb)
    {
        if (Math.Abs(grDb - _gr) < 0.15f)
            return;
        _gr = Math.Max(0f, grDb);
        InvalidateVisual();
    }

    private double PlotW => Math.Max(1, ActualWidth - MeterW - 4);
    private double XFromDb(double db) => (db - DbMin) / -DbMin * PlotW;
    private double DbFromX(double x) => DbMin + Math.Clamp(x / PlotW, 0, 1) * -DbMin;
    private double YFromDb(double db) => (db / DbMin) * ActualHeight;

    /// <summary>Transferencia del compresor (entrada dB → salida dB).</summary>
    private double OutDb(double inDb)
    {
        if (_cue is null)
            return inDb;
        double outDb = inDb <= _cue.CompThreshold
            ? inDb
            : _cue.CompThreshold + (inDb - _cue.CompThreshold) / Math.Max(1.0, _cue.CompRatio);
        return outDb + _cue.CompMakeup;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(BgBrush, null, new Rect(0, 0, w, h));
        if (_cue is null || w < 60)
            return;
        double pw = PlotW;

        // Rejilla cada 12 dB + diagonal 1:1 de referencia
        foreach (double db in new[] { -48.0, -36, -24, -12 })
        {
            double x = XFromDb(db), y = YFromDb(db);
            dc.DrawLine(GridPen, new Point(x, 0), new Point(x, h));
            dc.DrawLine(GridPen, new Point(0, y), new Point(pw, y));
        }
        dc.DrawLine(UnityPen, new Point(XFromDb(DbMin), YFromDb(DbMin)),
            new Point(XFromDb(0), YFromDb(0)));
        dc.DrawText(Text("COMP  in→out dB", GridText, 8), new Point(4, 2));

        // Curva de transferencia
        var pen = _cue.CompEnabled ? CurvePen : CurveOffPen;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            bool started = false;
            for (int px = 0; px <= (int)pw; px += 2)
            {
                double outDb = Math.Clamp(OutDb(DbFromX(px)), DbMin, 6.0);
                var pt = new Point(px, Math.Clamp(YFromDb(outDb), -10, h + 10));
                if (!started) { ctx.BeginFigure(pt, false, false); started = true; }
                else ctx.LineTo(pt, true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);

        // Manejador del umbral (sobre la curva, arrastrable en X)
        double tx = XFromDb(_cue.CompThreshold);
        double ty = YFromDb(Math.Clamp(OutDb(_cue.CompThreshold), DbMin, 0));
        dc.DrawEllipse(ThrBrush, null, new Point(tx, ty), _drag ? 6 : 5, _drag ? 6 : 5);
        dc.DrawText(Text($"{_cue.CompThreshold:0} dB · {_cue.CompRatio:0.#}:1",
            GridText, 8), new Point(Math.Min(tx + 6, pw - 70), Math.Max(2, ty - 14)));

        // ── Medidor GR (derecha): cuánta reducción aplica AHORA ─────────
        var meterRect = new Rect(pw + 4, 0, MeterW, h);
        dc.DrawRectangle(GrBg, null, meterRect);
        if (_cue.CompEnabled && _gr > 0.05f)
        {
            double frac = Math.Clamp(_gr / GrScaleDb, 0, 1);
            dc.DrawRectangle(GrBrush, null,
                new Rect(pw + 4, 0, MeterW, frac * h));   // crece de ARRIBA abajo
        }
        foreach (double mark in new[] { 6.0, 12, 18 })
        {
            double y = mark / GrScaleDb * h;
            dc.DrawLine(GridPen, new Point(pw + 4, y), new Point(pw + 4 + MeterW, y));
        }
        dc.DrawText(Text("GR", GridText, 8), new Point(pw + 6, h - 12));
        if (_cue.CompEnabled && _gr > 0.05f)
            dc.DrawText(Text($"-{_gr:0.0}", Frozen(new SolidColorBrush(Color.FromRgb(0xe8, 0xa2, 0x00))), 8),
                new Point(pw + 2, 2));
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _cue is null)
            return;
        var p = e.GetPosition(this);
        if (p.X > PlotW)
            return;   // el medidor no se arrastra
        _drag = true;
        CaptureMouse();
        Commit?.Invoke("comp_threshold",
            Math.Round(Math.Clamp(DbFromX(p.X), -60.0, 0.0), 1));
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_drag || _cue is null)
            return;
        Commit?.Invoke("comp_threshold",
            Math.Round(Math.Clamp(DbFromX(e.GetPosition(this).X), -60.0, 0.0), 1));
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_drag)
        {
            _drag = false;
            ReleaseMouseCapture();
            InvalidateVisual();
        }
    }

    /// <summary>Rueda sobre la gráfica: ajustar el RATIO.</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_cue is null)
            return;
        double newRatio = Math.Round(Math.Clamp(
            _cue.CompRatio * (e.Delta > 0 ? 1.15 : 1 / 1.15), 1.0, 20.0), 2);
        Commit?.Invoke("comp_ratio", newRatio);
        InvalidateVisual();
        e.Handled = true;
    }

    private static FormattedText Text(string t, Brush b, double size) => new(
        t, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI"), size, b, 1.0);

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    private static Pen FrozenPen(Color c, double width)
    {
        var p = new Pen(new SolidColorBrush(c), width);
        p.Freeze();
        return p;
    }
}
