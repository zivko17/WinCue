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
//
// VÍDEO TAMBIÉN: el rack (ganancia + EQ + compresor) vale para cues de VÍDEO
//   · con AUDIO ESPEJO (motor HAP propio o ruta híbrida) la cadena es la
//     NATIVA, igual que un cue de audio → hay RTA y GR reales;
//   · sin espejo el audio lo saca mpv y la EQ se traduce a su cadena af: los
//     controles funcionan, pero NO hay analizador → se muestra la curva (que
//     es matemática de los parámetros) y una nota honesta en vez de un RTA
//     muerto que parezca roto.
// =============================================================================

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinCue.Audio;
using WinCue.Core;
using WinCue.Presentation;

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
    /// <summary>El audio de este cue pasa por el rack nativo → hay RTA y GR
    /// reales. false = vídeo cuyo audio sale por mpv: curva sí, medidores no.</summary>
    private bool _analysis = true;

    private readonly EqCurveCanvas _canvas = new();
    private readonly CompCurveCanvas _compCanvas = new();
    private readonly Slider _gain;
    private readonly TextBlock _gainLabel;
    private readonly Border _gainFrame;   // marco de la lectura, encima del fader
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
    private readonly TextBlock _noAnalyzer;
    private readonly Grid _root;
    private readonly DispatcherTimer _rta;

    private static readonly Color[] BandUiColors =
    {
        Theme.Pgm,
        Theme.Warn,
        Theme.Go,
        Theme.Blue,
    };

    public EqEditorControl()
    {
        // Distribución: las DOS herramientas lado a lado, cada una con SUS
        // parámetros en columna a la IZQUIERDA de su gráfica — así las
        // gráficas quedan a altura completa. Fader de clip al principio.
        //   [GANANCIA] [params EQ][gráfica EQ] [params COMP][gráfica COMP]
        _root = new Grid { Margin = new Thickness(10, 6, 10, 8) };
        // 96 y no 74: la lectura en dB va enmarcada y con ancho fijo
        // (DbReadoutWidth = 74) más su marco y su margen. En 74 se salía de la
        // columna y quedaba recortada contra la gráfica de la EQ.
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 220 });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 180 });
        // Auto y no Star: con la fila en Star, la curva de la EQ y la del
        // compresor se estiraban con el panel. Al subir el separador del
        // inspector las gráficas crecían sin parar y la pestaña dejaba de tener
        // el mismo aspecto de una vez a otra. Miden lo que miden y el alto que
        // sobre se queda en el panel.
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.VerticalAlignment = VerticalAlignment.Top;

        // ── Fader de ganancia del clip (dB) ────────────────────────────
        // Columna con el MISMO reparto que la de VOL del inspector, que está
        // justo al lado: rótulo, fader, lectura DEBAJO. Antes era un DockPanel
        // con la lectura anclada abajo y ocupando las dos filas del grid, así
        // que la cifra se iba al fondo del panel —bien por debajo del final del
        // fader— y no cuadraba con la de VOL, a dos dedos de distancia.
        var gainPanel = new StackPanel
        {
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        var gainTitle = new TextBlock
        {
            Text = Loc.T("GANANCIA", "GAIN"),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Theme.TextDim),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        gainPanel.Children.Add(gainTitle);
        // La lectura va ARRIBA, bajo el rótulo, como en los strips de la mesa y
        // en el VOL de al lado: el mismo dato en el mismo sitio en los tres.
        _gainFrame = new Border
        {
            Background = Theme.Solid(Theme.SunkenBg),
            BorderBrush = Theme.Solid(Theme.Divider),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(0, 4, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        gainPanel.Children.Add(_gainFrame);
        _gainLabel = new TextBlock
        {
            Text = "0.0 dB",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Theme.Standby),
            // Ancho FIJO y centrado, como todas las lecturas de la consola: la
            // cifra cambia de ancho al mover el fader y sin reservarlo arrastra
            // la columna.
            Width = UiMetrics.DbReadoutWidth,
            TextAlignment = TextAlignment.Center,
        };
        _gain = new Slider
        {
            Orientation = Orientation.Vertical,
            Minimum = -24,
            Maximum = 12,
            TickFrequency = 3,
            IsSnapToTickEnabled = false,
            // Medidas compartidas: antes se estiraba a lo que quedase y salía de
            // otro tamaño que el VOL de al lado, en la misma pestaña.
            Width = UiMetrics.FaderWidth,
            Height = UiMetrics.FaderHeight,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
            ToolTip = Loc.T("Doble clic: volver a 0 dB",
                            "Double-click: back to 0 dB"),
        };
        _gain.ValueChanged += (_, _) =>
        {
            _gainLabel.Text = $"{_gain.Value:+0.0;-0.0;0.0} dB";
            if (!_loading)
                Commit?.Invoke("gain_db", Math.Round(_gain.Value, 1));
            _canvas.InvalidateVisual();
        };
        _gain.MouseDoubleClick += (_, e) =>
        {
            _gain.Value = 0.0;   // reset rápido, como los faders de volumen
            e.Handled = true;
        };
        gainPanel.Children.Add(_gain);
        _gainFrame.Child = _gainLabel;   // el marco ya está colocado arriba
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
        _eqOn = MakeCheck(Loc.T("EQ activa", "EQ active"), "eq_enabled");
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
            Foreground = new SolidColorBrush(Theme.TextDim),
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
            ToolTip = Loc.T("Ganancia de visualización del analizador (solo pantalla)", "Analyzer display gain (screen only)"),
        };
        var rtaLabel = new TextBlock
        {
            Text = $"{EqCurveCanvas.RtaGainDb:0} dB",
            Foreground = new SolidColorBrush(Theme.TextDim),
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
            Foreground = new SolidColorBrush(Theme.TextDim),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 190,
            Text = Loc.T("arrastrar punto = frec/gan · rueda = Q", "drag point = freq/gain · wheel = Q"),
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

        // Alto FIJO, el mismo que el compresor de al lado y del orden del strip
        // de volumen: las tres cosas de la pestaña miden lo mismo y la fila se
        // lee de un vistazo.
        _canvas.Height = UiMetrics.EqCanvasHeight;
        Grid.SetColumn(_canvas, 2);
        _root.Children.Add(_canvas);
        _canvas.ReadoutChanged += text => _readout!.Text = text;

        // Nota honesta EN EL SITIO DEL RTA (vídeo sin espejo de audio): la EQ
        // se aplica por la cadena af de mpv, pero no hay nada que medir —
        // mejor decirlo donde el operador espera ver el analizador que dejar
        // un fondo vacío que parece roto.
        _noAnalyzer = new TextBlock
        {
            Text = Loc.T(
                "Sin analizador (RTA/GR): el audio de este vídeo lo saca mpv. La ganancia, la EQ y el compresor SÍ actúan —y la curva es real—, pero solo hay medidores cuando el audio va por el rack (AUDIO ESPEJO).",
                "No analyzer (RTA/GR): this video's audio is played by mpv. Gain, EQ and compressor DO work —and the curve is real— but there are meters only when audio goes through the rack (MIRRORED AUDIO)."),
            Foreground = new SolidColorBrush(Theme.GoldSoft),
            Background = new SolidColorBrush(Theme.Alpha(Theme.EqBg, 0xcc)),
            FontSize = 10,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(8, 6, 8, 0),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
        };
        Grid.SetColumn(_noAnalyzer, 2);
        _root.Children.Add(_noAnalyzer);

        // ── HERRAMIENTA 2: COMPRESOR — parámetros en columna + gráfica ──
        var compPars = new StackPanel { Margin = new Thickness(14, 0, 8, 0) };
        _compOn = MakeCheck(Loc.T("Compresor", "Compressor"), "comp_enabled");
        compPars.Children.Add(_compOn);
        _compThr = MakeNumBox("comp_threshold", -60, 0);
        compPars.Children.Add(CompRow(Loc.T("Umbral", "Threshold"), _compThr, "dB"));
        _compRatio = MakeNumBox("comp_ratio", 1, 20);
        compPars.Children.Add(CompRow("Ratio", _compRatio, ":1"));
        _compAtt = MakeNumBox("comp_attack", 0.1, 200);
        compPars.Children.Add(CompRow(Loc.T("Ataque", "Attack"), _compAtt, "ms"));
        _compRel = MakeNumBox("comp_release", 5, 2000);
        compPars.Children.Add(CompRow("Release", _compRel, "ms"));
        _compMakeup = MakeNumBox("comp_makeup", 0, 24);
        compPars.Children.Add(CompRow("Makeup", _compMakeup, "dB"));
        compPars.Children.Add(new TextBlock
        {
            Foreground = new SolidColorBrush(Theme.TextDim),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 130,
            Text = Loc.T("arrastrar = umbral · rueda = ratio · GR = reducción en vivo", "drag = threshold · wheel = ratio · GR = live reduction"),
        });
        var compParsScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = compPars,
        };
        Grid.SetColumn(compParsScroll, 3);
        _root.Children.Add(compParsScroll);

        _compCanvas.Height = UiMetrics.EqCanvasHeight;
        Grid.SetColumn(_compCanvas, 4);
        _root.Children.Add(_compCanvas);

        _noAudio = new TextBlock
        {
            Text = Loc.T("Este tipo de cue no tiene audio: la ganancia, la EQ y el compresor son para cues de AUDIO y de VÍDEO.",
                         "This cue type has no audio: gain, EQ and compressor are for AUDIO and VIDEO cues."),
            Foreground = new SolidColorBrush(Theme.TextFaint),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Content = _root;

        // RTA + medidor GR ~12 Hz mientras la pestaña está visible
        _rta = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _rta.Tick += (_, _) =>
        {
            // Sin analizador (vídeo por mpv) no se pide nada: la curva de la
            // EQ sigue viva porque es matemática de los parámetros
            if (IsVisible && _analysis && _cue is { Type: CueType.Audio or CueType.Video })
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
            Foreground = new SolidColorBrush(Theme.TextPrimary),
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
        Foreground = new SolidColorBrush(Theme.TextDim),
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
            Background = new SolidColorBrush(Theme.SunkenBg),
            Foreground = new SolidColorBrush(Theme.TextPrimary),
            BorderBrush = new SolidColorBrush(Theme.Border),
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
            Background = new SolidColorBrush(Theme.SunkenBg),
            Foreground = new SolidColorBrush(Theme.TextPrimary),
            BorderBrush = new SolidColorBrush(Theme.Border),
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
        Foreground = new SolidColorBrush(Theme.TextDim),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(2, 0, 10, 0),
    };

    /// <summary>Repuebla el editor con el cue dado (solo al cambiar selección).
    /// hasAnalysis = su audio pasa por el rack nativo (RTA y GR reales).</summary>
    public void SetCue(Cue? cue, bool hasAnalysis = true)
    {
        _cue = cue;
        _analysis = hasAnalysis;
        _canvas.SetCue(cue);
        _compCanvas.SetCue(cue);
        // El rack vale para AUDIO y para VÍDEO (espejo nativo o cadena af de
        // mpv); el resto de tipos no tienen audio que tratar
        if (cue is not { Type: CueType.Audio or CueType.Video })
        {
            Content = _noAudio;
            return;
        }
        _noAnalyzer.Visibility = hasAnalysis ? Visibility.Collapsed : Visibility.Visible;
        Content = _root;
        RefreshParams();
    }

    /// <summary>Vuelca los valores del cue en TODAS las cajas numéricas (al
    /// seleccionar y tras cada arrastre en las gráficas, que mutan el cue).</summary>
    private void RefreshParams()
    {
        if (_cue is not { Type: CueType.Audio or CueType.Video } cue)
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

    private static readonly Brush BgBrush = Frozen(new SolidColorBrush(Theme.EqBg));
    private static readonly Pen GridPen = FrozenPen(Theme.EqGrid, 1);
    private static readonly Pen ZeroPen = FrozenPen(Theme.EqZero, 1);
    private static readonly Brush GridText = Frozen(new SolidColorBrush(Theme.TextDimmer));
    // RTA estilo analizador pro: área con degradado + contorno + picos
    private static readonly Brush RtaFill = FrozenGradient(
        Theme.Alpha(Theme.Go, 0x86), Theme.Alpha(Theme.Go, 0x10));
    private static readonly Pen RtaLine = FrozenPen(Theme.Alpha(Theme.RtaLine, 0xdd), 1.3);
    private static readonly Pen RtaPeakPen = FrozenPen(Theme.Alpha(Theme.RtaPeak, 0xb4), 1);

    private static Brush FrozenGradient(Color top, Color bottom)
    {
        var b = new LinearGradientBrush(top, bottom, 90.0);
        b.Freeze();
        return b;
    }

    /// <summary>Ganancia de VISUALIZACIÓN del RTA en dB (compartida por
    /// sesión; solo afecta a la pantalla, nunca al audio). Solo se toca
    /// desde el hilo de UI.
    ///
    /// Arranca en 0: la escala ya viene referenciada (RtaRefDb) para que la
    /// traza caiga donde tiene que caer sin ayuda. Los 18 dB que traía de
    /// fábrica eran una tirita para compensar una escala mal centrada — y
    /// además dejaban medio espectro pegado al techo.</summary>
    public static double RtaGainDb { get; set; }

    // ── Escala del analizador ──────────────────────────────────────────
    //
    // La traza salía plana y pegada al fondo, y NO era cosa de la escala: el
    // analizador estaba descalibrado 66 dB (dividía por N dos veces; ver
    // CueSampleProvider y SpectrumCalibrationTests). Con el medidor ya diciendo
    // la verdad en dBFS, aquí no hace falta compensar NADA — hubo una versión
    // de esto con un offset de referencia de +24 dB y era exactamente el mismo
    // error que los 18 dB de ganancia de fábrica: tapar un fallo con otro.
    //
    // Queda una escala honesta: dBFS de verdad, con un dedo de aire por encima
    // de 0 para que subir la ganancia de pantalla suba la traza en vez de
    // aplastarla contra el borde.

    /// <summary>Suelo y techo de la escala, en dBFS reales.</summary>
    private const double RtaFloorDb = -72.0;
    private const double RtaTopDb = 6.0;
    private static readonly Pen CurvePen = FrozenPen(Theme.Standby, 2.2);
    private static readonly Pen CurveOffPen = FrozenPen(Theme.EqCurveOff, 1.5);
    private static readonly Pen CurveGlowPen = FrozenPen(Theme.Alpha(Theme.Standby, 0x46), 5.5);
    private static readonly Brush CurveFill = Frozen(new SolidColorBrush(Theme.Alpha(Theme.Standby, 0x22)));

    private static readonly Color[] BandColors =
    {
        Theme.Pgm,    // 1 grave — rojo
        Theme.Warn,   // 2 medio-grave — ámbar
        Theme.Go,     // 3 medio-agudo — verde
        Theme.Blue,   // 4 agudo — azul
    };

    public Action<string, object?>? Commit;
    public event Action<string>? ReadoutChanged;

    private Cue? _cue;
    private float[]? _smooth;     // RTA con balística (ataque directo, release suave)
    private float[]? _peakHold;   // picos con caída lenta (peak-hold)
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
        _smooth = null;
        _peakHold = null;
        InvalidateVisual();
    }

    /// <summary>Espectro nuevo (~12 Hz). Aquí se aplica la balística de un
    /// analizador de verdad: ataque instantáneo, release ~30 dB/s y picos con
    /// caída lenta — sin esto las barras parpadean y se ve amateur.</summary>
    public void SetSpectrum(float[]? spectrum)
    {
        if (spectrum is null)
        {
            _smooth = null;
            _peakHold = null;
        }
        else if (_smooth is null || _smooth.Length != spectrum.Length)
        {
            _smooth = (float[])spectrum.Clone();
            _peakHold = (float[])spectrum.Clone();
        }
        else
        {
            for (int i = 0; i < spectrum.Length; i++)
            {
                float v = spectrum[i];
                _smooth[i] = v >= _smooth[i]
                    ? v                                     // ataque directo
                    : Math.Max(v, _smooth[i] - 2.4f);       // release ~30 dB/s
                _peakHold![i] = Math.Max(v, _peakHold[i] - 0.65f);  // ~8 dB/s
            }
        }
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

        // RTA detrás: ÁREA suavizada con degradado + contorno + peak-hold
        // (estilo analizador pro; dBFS -90..0 + ganancia de pantalla)
        if (_smooth is { Length: > 1 } spec)
        {
            double YFromRta(double db) => h * (1.0 - Math.Clamp(
                (db - RtaFloorDb) / (RtaTopDb - RtaFloorDb), 0.0, 1.0));
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(0, h + 1), true, true);
                ctx.LineTo(new Point(0, YFromRta(spec[0] + RtaGainDb)), false, false);
                for (int i = 0; i < spec.Length; i++)
                    ctx.LineTo(new Point((i + 0.5) / spec.Length * w,
                        YFromRta(spec[i] + RtaGainDb)), true, true);
                ctx.LineTo(new Point(w, YFromRta(spec[^1] + RtaGainDb)), true, true);
                ctx.LineTo(new Point(w, h + 1), false, false);
            }
            area.Freeze();
            dc.DrawGeometry(RtaFill, RtaLine, area);

            // Picos: segmento fino y brillante por banda, cayendo despacio
            if (_peakHold is { } pk)
                for (int i = 0; i < pk.Length; i++)
                {
                    double y = YFromRta(pk[i] + RtaGainDb);
                    if (y >= h - 1)
                        continue;
                    double x0 = (double)i / pk.Length * w + 1;
                    double x1 = (double)(i + 1) / pk.Length * w - 1;
                    dc.DrawLine(RtaPeakPen, new Point(x0, y), new Point(x1, y));
                }
        }

        // Curva de respuesta de los filtros: relleno hasta 0 dB + glow + trazo
        var eq = ParamsFrom(_cue);
        bool eqOn = _cue.EqEnabled || _cue.HpfOn || _cue.LpfOn;
        var pen = eqOn ? CurvePen : CurveOffPen;
        double y0 = YFromDb(0);
        var curve = new StreamGeometry();
        var curveArea = new StreamGeometry();
        using (var ctx = curve.Open())
        using (var actx = curveArea.Open())
        {
            bool started = false;
            actx.BeginFigure(new Point(0, y0), true, true);
            for (int px = 0; px <= (int)w; px += 3)
            {
                double db = EqChain.ResponseDb(eq, FreqFromX(px));
                var pt = new Point(px, Math.Clamp(YFromDb(db), -20, h + 20));
                if (!started) { ctx.BeginFigure(pt, false, false); started = true; }
                else ctx.LineTo(pt, true, false);
                actx.LineTo(pt, true, true);
            }
            actx.LineTo(new Point(w, y0), true, true);
        }
        curve.Freeze();
        curveArea.Freeze();
        if (eqOn)
        {
            dc.DrawGeometry(CurveFill, null, curveArea);   // sombra ámbar de la curva
            dc.DrawGeometry(null, CurveGlowPen, curve);    // halo
        }
        dc.DrawGeometry(null, pen, curve);

        // Manejadores HPF/LPF (cuadrados en la línea de 0 dB)
        DrawFilterHandle(dc, -2, _cue.HpfFreq, _cue.HpfOn, "HPF");
        DrawFilterHandle(dc, -3, _cue.LpfFreq, _cue.LpfOn, "LPF");

        // Manejadores de banda: anillo de color con centro oscuro (estilo
        // plugin pro) y halo al pasar el ratón o arrastrar
        for (int i = 0; i < 4; i++)
        {
            var (f, g, _) = Band(i);
            var center = new Point(XFromFreq(f), YFromDb(g));
            byte alpha = _cue.EqEnabled ? (byte)235 : (byte)90;
            var c = BandColors[i];
            bool hot = i == _drag || i == _hover;
            double r = hot ? 8.5 : 7;
            if (hot)
            {
                var halo = new SolidColorBrush(Theme.Alpha(c, 0x38));
                halo.Freeze();
                dc.DrawEllipse(halo, null, center, r + 5, r + 5);
            }
            var ring = new Pen(Frozen(new SolidColorBrush(Theme.Alpha(c, alpha))), 2.4);
            ring.Freeze();
            var inner = new SolidColorBrush(Theme.Alpha(
                Theme.EqHandleCore, _cue.EqEnabled ? (byte)0xdd : (byte)0x66));
            inner.Freeze();
            dc.DrawEllipse(inner, ring, center, r, r);
            var num = Text($"{i + 1}",
                Frozen(new SolidColorBrush(Theme.Alpha(Theme.White, alpha))), 9.5);
            dc.DrawText(num, new Point(center.X - num.Width / 2, center.Y - num.Height / 2));
        }
    }

    private void DrawFilterHandle(DrawingContext dc, int id, double freq, bool on, string label)
    {
        double x = XFromFreq(freq), y = YFromDb(0);
        var brush = new SolidColorBrush(
            Theme.Alpha(Theme.TextTertiary, on ? (byte)220 : (byte)80));
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

    private static readonly Brush BgBrush = Frozen(new SolidColorBrush(Theme.EqBg));
    private static readonly Pen GridPen = FrozenPen(Theme.EqGrid, 1);
    private static readonly Pen UnityPen = MakeUnityPen();
    private static readonly Pen CurvePen = FrozenPen(Theme.Warn, 2);
    private static readonly Pen CurveOffPen = FrozenPen(Theme.CompCurveOff, 1.5);
    private static readonly Brush GridText = Frozen(new SolidColorBrush(Theme.TextDimmer));
    private static readonly Brush ThrBrush = Frozen(new SolidColorBrush(Theme.TextPrimary));
    private static readonly Brush GrBrush = Frozen(new SolidColorBrush(Theme.WarnDeep));
    private static readonly Brush GrBg = Frozen(new SolidColorBrush(Theme.EqGrBg));

    private static Pen MakeUnityPen()
    {
        var p = new Pen(new SolidColorBrush(Theme.EqZero), 1)
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
            dc.DrawText(Text($"-{_gr:0.0}", Frozen(new SolidColorBrush(Theme.Warn)), 8),
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
