// =============================================================================
// MixerPanel.cs — Mesa de mezclas del show, en WinUI 3 + Win2D.
// =============================================================================
// Equivalente de src/WinCue.App/AudioMixerPanel.cs (WPF) para esta interfaz.
// MISMO reparto que el resto del traslado (ver la cabecera de VuMeterCanvas.cs,
// el piloto): el CÁLCULO —la ley del fader en píxeles, qué strips muestra la
// mesa y en qué orden, cuándo pliega uno— vive en WinCue.Presentation
// (FaderScaleLayout, MixerLayout), sin un solo tipo de framework de interfaz.
// Este archivo compone el strip con controles CORRIENTES de WinUI 3 (Border,
// Grid, Slider, TextBox, Button) — como ya hace InspectorPanel.cs— y solo
// pinta a mano en Win2D las dos piezas que en WPF eran DrawingContext:
// FaderScaleCanvas.cs (la escala) y GrBarCanvas.cs (el medidor de GR).
//
// EL FADER SIGUE SIENDO UN Slider NATIVO, no pintado a mano: el Slider de
// WinUI 3 (Microsoft.UI.Xaml.Controls.Slider) YA soporta Orientation=Vertical
// y clic-para-posicionar de fábrica —a diferencia de WPF, que necesitaba
// IsMoveToPointEnabled=true—, así que arrastre, teclado y accesibilidad
// vienen gratis. Repintarlo a mano en Win2D solo para "que sea Win2D" habría
// sido reinventar una rueda que el framework ya da, y el encargo pide portar
// el CONTROL, no el framework de dibujo por sí mismo.
//
// COLAPSADO CON EL NOMBRE GIRADO: WinUI 3 no tiene FrameworkElement.
// LayoutTransform (a diferencia de WPF) — mismo hueco de plataforma que
// TimelineCanvas.cs documenta para los glifos de icono. Aquí la tira plegada
// se pinta a mano en Win2D (CollapsedLabelCanvas, más abajo): mismo criterio
// que el resto del traslado, "pinta lo que DrawingContext ya no puede".
//
// SIMPLIFICACIÓN DELIBERADA DE ESTA PASADA: el strip de un cue detecta que el
// operador tiene el fader "agarrado" mirando el foco de teclado y la captura
// de puntero (Fader.PointerCaptures) — WPF usa IsMouseCaptureWithin /
// IsKeyboardFocusWithin, que WinUI no expone igual de directo por elemento.
// Ver el comentario de CueStrip.RefreshFromModel más abajo.
// =============================================================================

using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;
using WinCue.Controller;
using WinCue.Core;
using WinCue.Presentation;

namespace Secuence.App;

/// <summary>Mesa de mezclas del show: strip MASTER, strip CUE (audición) y un
/// strip por cada cue de audio/vídeo. Equivalente de
/// <c>WinCue.App.AudioMixerPanel</c> (WPF).</summary>
public sealed class MixerPanel : ContentControl
{
    // ── Callbacks (los cablea MainWindow.Attach) — mismo contrato que WPF ──
    public Action<int, bool>? MasterVolumeChanged;
    public Action<int, bool>? AuditionVolumeChanged;
    public Action<int, double>? CueVolumeCommitted;
    public Action<int>? CueStripActivated;
    public Action<int>? CueRequested;
    public Func<int>? AuditionSourceId;
    public Func<bool>? CueAvailable;
    public Func<CueBusSource>? CueBusSourceNow;
    public Func<float>? MasterGainReductionProvider;

    /// <summary>El estado de silencio/solo cambió desde la mesa. Lo escucha
    /// el inspector, que tiene los mismos botones en su pestaña de audio.</summary>
    public Action? StripsRepainted;

    private readonly Strip _master;
    private readonly Strip _audition;
    private readonly TextBlock _grText;
    private readonly GrBarCanvas _grBar = new();
    private readonly TextBlock _cueSourceText;
    private readonly StackPanel _cueHost = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _emptyHint;
    private readonly Dictionary<int, CueStrip> _cueStrips = new();

    /// <summary>Decisión EXPLÍCITA del operador sobre un canal (true = plegado).
    /// Manda sobre el automatismo — mismo motivo que WPF.</summary>
    private readonly Dictionary<int, bool> _plegadoAMano = new();

    private const int MasterKey = -1;
    private const int AuditionKey = -2;

    /// <summary>Silencio y solo, FUERA de la interfaz (WinCue.Controller.
    /// MuteSoloBoard) — el MISMO tipo que usa WinCue, así que el inspector de
    /// Secuence podrá compartir botones con la mesa el día que los tenga.</summary>
    private readonly MuteSoloBoard _board = new();
    public MuteSoloBoard Board => _board;

    public void RefreshStripButtons() => RepaintAllStrips();

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _grTimer;

    public MixerPanel()
    {
        _master = Strip.Build("MASTER", Theme.PgmBrush, withVu: true, extraMeter: _grBar);
        ToolTipService.SetToolTip(_grBar, Loc.T(
            "Limitador del bus (techo −1 dBFS): reducción de ganancia.\nLa suma de cues no puede saturar el dispositivo.",
            "Bus limiter (−1 dBFS ceiling): gain reduction.\nThe sum of cues cannot clip the device."));
        _master.VuTooltip(Loc.T(
            "VU de PROGRAMA. Con loopback: lo que sale del dispositivo\n(audio + vídeo + todo). Con ASIO o sin loopback disponible:\nla mezcla interna de cues. La línea de abajo dice cuál.",
            "PROGRAM VU. With loopback: what leaves the device\n(audio + video + everything). With ASIO or no loopback available:\nthe internal cue mix. The line below says which one."));

        _grText = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"), FontSize = 11,
            Foreground = Theme.WarnBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Text = "",
        };
        _master.Extras.Children.Add(_grText);

        // Se llama CUE porque es el MASTER del bus de preescucha — mismo
        // motivo que WPF (ver AudioMixerPanel.cs).
        _audition = Strip.Build($"{Icons.Headphones} CUE", Theme.GoBrush, withVu: true);
        _audition.VuTooltip(Loc.T("VU de la preescucha (PVW).", "Audition (PVW) VU."));
        ToolTipService.SetToolTip(_audition.Root, Loc.T("Trim × volumen del cue", "Trim × cue volume"));

        _cueSourceText = new TextBlock
        {
            FontSize = 10, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Theme.GoBrush,
            Text = "",
        };
        _audition.Extras.Children.Add(_cueSourceText);

        _emptyHint = new TextBlock
        {
            Text = Loc.T("— sin cues sonando —", "— no cues playing —"),
            Foreground = Theme.TextDimmerBrush, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24, 0, 24, 0),
        };

        var strips = new StackPanel { Orientation = Orientation.Horizontal };
        strips.Children.Add(_master.Root);
        strips.Children.Add(_audition.Root);
        strips.Children.Add(new Border
        {
            Width = 1, Background = Theme.ButtonBgBrush,
            Margin = new Thickness(6, 12, 6, 12),
        });
        strips.Children.Add(_cueHost);
        strips.Children.Add(_emptyHint);

        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = strips,
        };

        // Alta en el board. Sin solo: no hay nada contra lo que solearlos.
        _master.Key = MasterKey;
        _master.Board = _board;
        _master.RepaintAll = RepaintAllStrips;
        _audition.Key = AuditionKey;
        _audition.Board = _board;
        _audition.RepaintAll = RepaintAllStrips;
        _board.Add(MasterKey, 100, canSolo: false);
        _board.Add(AuditionKey, 100, canSolo: false);
        _board.GainChanged = OnBoardGain;

        WireDebouncedFader(_master, (v, final) => MasterVolumeChanged?.Invoke(v, final));
        WireDebouncedFader(_audition, (v, final) => AuditionVolumeChanged?.Invoke(v, final));

        // El timer de GR necesita un DispatcherQueue ya asociado a una
        // ventana; en el constructor puede no estar listo (mismo motivo que
        // VuMeterCanvas.StartDecayTimer), así que se arma al cargar.
        Loaded += (_, _) => StartGrTimer();
        Unloaded += (_, _) => _grTimer?.Stop();
    }

    private void StartGrTimer()
    {
        if (_grTimer is not null)
            return;
        _grTimer = DispatcherQueue.CreateTimer();
        _grTimer.Interval = TimeSpan.FromMilliseconds(150);
        _grTimer.Tick += (_, _) =>
        {
            if (MasterGainReductionProvider is null)
                return;
            float gr = MasterGainReductionProvider();
            _grBar.SetDb(gr);
            _grText.Text = gr >= 0.1f ? $"GR −{gr:0.0} dB" : "";
        };
        _grTimer.Start();
    }

    // ── API que alimenta MainWindow — mismo contrato que WPF ────────────

    public void SetMasterLevel(float l, float r) => _master.SetLevels(l, r);

    public void SetAuditionLevel(float l, float r) => _audition.SetLevels(l, r);

    public void SetMasterFader(int value)
    {
        _board.SetGainSilently(MasterKey, value);
        if (!_board.IsMuted(MasterKey))
            _master.SetFaderSilent(value);
    }

    public void SetAuditionFader(int value)
    {
        _board.SetGainSilently(AuditionKey, value);
        if (!_board.IsMuted(AuditionKey))
            _audition.SetFaderSilent(value);
    }

    /// <summary>Detalle del dispositivo, al TOOLTIP del strip MASTER —mismo
    /// motivo que WPF: es información de configuración, no de operación.</summary>
    public void SetDeviceInfo(string text) => ToolTipService.SetToolTip(_master.Root, text);

    public void UpdateCueLevel(int cueId, float l, float r)
    {
        if (_cueStrips.TryGetValue(cueId, out var s))
            s.SetLevels(l, r);
    }

    /// <summary>Reconcilia los strips con el show — ver la cabecera de
    /// WinCue.Presentation.MixerLayout.PlanCueStrips para el reparto
    /// COMPARTIDO con WinCue.App. Aquí solo queda crear/actualizar/quitar los
    /// controles de WinUI 3, sin recrear strips existentes.</summary>
    public void SyncActive(IReadOnlyList<ActiveCueInfo> rows, Func<int, Cue?> resolveCue,
                           IReadOnlyList<Cue>? allCues = null)
    {
        var deseados = MixerLayout.PlanCueStrips(rows, allCues);
        var porId = deseados.ToDictionary(s => s.Id);

        var seen = new HashSet<int>();
        foreach (var spec in deseados)
        {
            seen.Add(spec.Id);
            if (_cueStrips.TryGetValue(spec.Id, out var strip))
            {
                // MirrorAudio no cambia en la vida del cue SALVO el fallback
                // del espejo: recrear el strip para que el VU y la nota
                // reflejen la ruta real — mismo motivo que WPF.
                if (strip.MirrorAudio == spec.MirrorAudio)
                {
                    strip.RefreshFromModel(resolveCue(spec.Id));
                    continue;
                }
                int at = _cueHost.Children.IndexOf(strip.Root);
                _cueHost.Children.Remove(strip.Root);
                _cueStrips.Remove(spec.Id);
                var recreated = MakeStrip(spec, resolveCue);
                _cueHost.Children.Insert(at, recreated.Root);
                continue;
            }
            _cueHost.Children.Add(MakeStrip(spec, resolveCue).Root);
        }
        foreach (int id in _cueStrips.Keys.ToList())
        {
            if (seen.Contains(id))
                continue;
            _cueHost.Children.Remove(_cueStrips[id].Root);
            _cueStrips.Remove(id);
            _board.Remove(id);
            _plegadoAMano.Remove(id);
        }

        // PLEGADO: MixerLayout.ShouldCollapse — la decisión manual del
        // operador manda; sin ella, abierto el que suena y plegado el resto.
        foreach (var (id, strip) in _cueStrips)
            strip.SetCollapsed(MixerLayout.ShouldCollapse(porId[id].IsSounding,
                _plegadoAMano.TryGetValue(id, out bool quiere) ? quiere : null));

        _emptyHint.Visibility = _cueStrips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StripsRepainted?.Invoke();
    }

    // ── Interno ────────────────────────────────────────────────────────

    private CueStrip MakeStrip(MixerStripSpec spec, Func<int, Cue?> resolveCue)
    {
        var s = new CueStrip(spec, resolveCue(spec.Id),
            (id, vol) => CueVolumeCommitted?.Invoke(id, vol),
            id => CueStripActivated?.Invoke(id));
        s.Key = spec.Id;
        s.Board = _board;
        s.RepaintAll = RepaintAllStrips;
        s.AuditionSource = () => AuditionSourceId?.Invoke() ?? -1;
        s.CueAvailable = () => CueAvailable?.Invoke() ?? true;
        s.CueRequested = id => CueRequested?.Invoke(id);
        s.EnableSolo();
        s.EnableCollapse($"{spec.Number} · {spec.Name}");
        s.CollapseToggled = st =>
        {
            bool quiere = !st.IsCollapsed;
            _plegadoAMano[st.Key] = quiere;
            st.SetCollapsed(quiere);
        };
        _board.Add(spec.Id, (int)Math.Round((resolveCue(spec.Id)?.Volume ?? 1.0) * 100.0));
        s.PaintButtons();
        _cueStrips[spec.Id] = s;
        return s;
    }

    private void RepaintAllStrips()
    {
        _master.PaintButtons();
        _audition.PaintButtons();

        var src = CueBusSourceNow?.Invoke() ?? CueBusSource.None;
        _cueSourceText.Text = src switch
        {
            CueBusSource.Pfl => Loc.T("● EN VIVO", "● LIVE"),
            CueBusSource.Preview => Loc.T("▶ PREVIEW", "▶ PREVIEW"),
            _ => "",
        };
        _cueSourceText.Foreground = src == CueBusSource.Pfl ? Theme.PgmBrush : Theme.StandbyBrush;

        foreach (var s in _cueStrips.Values)
            s.PaintButtons();
        StripsRepainted?.Invoke();
    }

    private void OnBoardGain(int key, int gain)
    {
        var strip = key == MasterKey ? _master
                  : key == AuditionKey ? _audition
                  : _cueStrips.GetValueOrDefault(key);
        if (strip is null)
            return;
        strip.SetFaderSilent(gain);
        strip.PaintButtons();

        if (key == MasterKey)
            MasterVolumeChanged?.Invoke(gain, false);
        else if (key == AuditionKey)
            AuditionVolumeChanged?.Invoke(gain, false);
        else
            CueVolumeCommitted?.Invoke(key, gain / 100.0);
    }

    /// <summary>Debounce de 400 ms del commit "final" de master/audición —
    /// mismo contrato que WPF.WireDebouncedFader. El timer se crea al primer
    /// movimiento del fader (mismo motivo que StartGrTimer: el DispatcherQueue
    /// no está garantizado en el constructor).</summary>
    private void WireDebouncedFader(Strip strip, Action<int, bool> emit)
    {
        int pending = -1;
        Microsoft.UI.Dispatching.DispatcherQueueTimer? final = null;
        strip.FaderChanged = v =>
        {
            emit(v, false);
            pending = v;
            if (final is null)
            {
                final = DispatcherQueue.CreateTimer();
                final.Interval = TimeSpan.FromMilliseconds(400);
                final.Tick += (_, _) =>
                {
                    final!.Stop();
                    if (pending >= 0)
                        emit(pending, true);
                    pending = -1;
                };
            }
            final.Stop();
            final.Start();
        };
    }

    /// <summary>Strip genérico: título, fader vertical + VU estéreo, etiqueta
    /// dB y zona de extras. Base de master/audición y de los strips por cue.</summary>
    private class Strip
    {
        public const double PosSteps = 1000.0;

        public Border Root = null!;
        public Slider Fader = null!;
        public TextBox Db = null!;
        public StackPanel Extras = null!;
        public Button MuteButton = null!;
        public Button SoloButton = null!;

        public Action<int>? FaderChanged;
        public int Key = int.MinValue;
        public MuteSoloBoard? Board;
        public Action? RepaintAll;
        public Action<int>? CueRequested;

        protected VuMeterCanvas? VuL, VuR;
        protected StackPanel MeterZone = null!;
        protected TextBlock TitleText = null!;
        protected bool Syncing;

        protected Grid ContentHost = null!;
        protected FrameworkElement FullContent = null!;
        private CollapsedLabelCanvas? _collapsed;

        public bool IsCollapsed { get; private set; }
        public Action<Strip>? CollapseToggled;
        public Button CollapseBtn = null!;

        /// <summary>Da de alta la vista PLEGADA — mismo motivo que WPF: un cue
        /// parado no tiene nivel ni VU que enseñar.</summary>
        public void EnableCollapse(string label)
        {
            _collapsed = new CollapsedLabelCanvas();
            _collapsed.SetText(label, Palette.TextSecondary);
            _collapsed.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(_collapsed,
                Loc.T("Clic para desplegar este canal", "Click to expand this channel"));
            _collapsed.Tapped += (_, _) => CollapseToggled?.Invoke(this);
            ContentHost.Children.Add(_collapsed);

            CollapseBtn.Visibility = Visibility.Visible;
            CollapseBtn.Click += (_, _) => CollapseToggled?.Invoke(this);
        }

        public void SetCollapsed(bool collapsed)
        {
            if (_collapsed is null || collapsed == IsCollapsed)
                return;
            IsCollapsed = collapsed;
            CollapseBtn.Content = collapsed ? "▶" : "◀";
            ToolTipService.SetToolTip(CollapseBtn, collapsed
                ? Loc.T("Desplegar este canal", "Expand this channel")
                : Loc.T("Plegar este canal", "Collapse this channel"));
            FullContent.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            _collapsed.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
            Root.Width = collapsed ? UiMetrics.StripCollapsedWidth : UiMetrics.StripWidth;
            Root.Padding = collapsed ? new Thickness(2, 6, 2, 6) : new Thickness(8, 6, 8, 6);
        }

        private bool _wideMute = true;

        public static Strip Build(string title, Brush accent, bool withVu,
                                  FrameworkElement? extraMeter = null)
        {
            var s = new Strip();
            s.Init(title, accent, withVu, extraMeter);
            return s;
        }

        protected void Init(string title, Brush accent, bool withVu,
                            FrameworkElement? extraMeter = null)
        {
            var grid = new Grid();
            for (int i = 0; i < 5; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TitleText = new TextBlock
            {
                Text = title, FontWeight = FontWeights.Bold, FontSize = 12,
                Foreground = accent, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis,
                Height = UiMetrics.StripTitleLineHeight,
                LineHeight = UiMetrics.StripTitleLineHeight,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            };
            var titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleRow.Children.Add(TitleText);
            CollapseBtn = new Button
            {
                Width = 16, Height = 16, Padding = new Thickness(0),
                FontSize = 9, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Background = Theme.ButtonBgBrush,
                Foreground = Theme.TextDimmerBrush,
                BorderThickness = new Thickness(0),
                Visibility = Visibility.Collapsed,
                Content = "◀",
            };
            ToolTipService.SetToolTip(CollapseBtn, Loc.T("Plegar este canal", "Collapse this channel"));
            Grid.SetColumn(CollapseBtn, 1);
            titleRow.Children.Add(CollapseBtn);
            grid.Children.Add(titleRow);

            // LECTURA EN dB, EDITABLE — mismo motivo y mismo ancho fijo que
            // WPF (UiMetrics.DbReadoutWidth: el texto cambia de ancho al
            // usarlo y sin ancho reservado se menea media columna).
            Db = new TextBox
            {
                FontFamily = new FontFamily("Consolas"), FontSize = 12,
                Foreground = Theme.GoBrush,
                Background = Theme.TransparentBrush,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Width = UiMetrics.DbReadoutWidth,
                TextAlignment = TextAlignment.Center,
                Text = AudioLevels.DbLabel(1.0),
            };
            ToolTipService.SetToolTip(Db,
                Loc.T("Escribe los dB y pulsa Intro", "Type the dB value and press Enter"));
            var dbFrame = new Border
            {
                Background = Theme.SunkenBgBrush,
                BorderBrush = Theme.DividerBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 2, 4, 2),
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = Db,
            };
            Grid.SetRow(dbFrame, 1);
            grid.Children.Add(dbFrame);

            // INTRO confirma, ESCAPE cancela; perder el foco confirma también.
            Db.KeyDown += (_, e) =>
            {
                if (e.Key == VirtualKey.Enter)
                {
                    CommitTypedDb();
                    e.Handled = true;
                }
                else if (e.Key == VirtualKey.Escape)
                {
                    ShowCurrentDb();
                    e.Handled = true;
                }
            };
            Db.LostFocus += (_, _) => CommitTypedDb();
            Db.GotFocus += (_, _) => Db.SelectAll();

            MeterZone = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            // ESCALA en dB a la izquierda del fader — Win2D, cálculo
            // compartido (FaderScaleLayout/AudioLevels).
            MeterZone.Children.Add(new FaderScaleCanvas
            { Width = UiMetrics.FaderScaleWidth, Height = UiMetrics.FaderHeight });
            Fader = new Slider
            {
                // El fader recorre POSICIÓN (0..1000), no ganancia — mismo
                // motivo que WPF (AudioLevels.PositionToGain reparte el
                // recorrido por dB, no linealmente en ganancia).
                Orientation = Orientation.Vertical, Minimum = 0, Maximum = PosSteps,
                Value = PosSteps,
                Width = UiMetrics.FaderWidth, Height = UiMetrics.FaderHeight,
            };
            ToolTipService.SetToolTip(Fader,
                Loc.T("Doble clic: volver a 0 dB", "Double-click: back to 0 dB"));
            MeterZone.Children.Add(Fader);
            if (withVu)
            {
                VuL = new VuMeterCanvas(vertical: true)
                { Width = 7, Height = UiMetrics.FaderHeight, Margin = new Thickness(6, 0, 0, 0) };
                VuR = new VuMeterCanvas(vertical: true)
                { Width = 7, Height = UiMetrics.FaderHeight, Margin = new Thickness(1, 0, 0, 0) };
                MeterZone.Children.Add(VuL);
                MeterZone.Children.Add(VuR);
            }
            if (extraMeter is not null)
            {
                extraMeter.Height = UiMetrics.FaderHeight;
                extraMeter.Margin = new Thickness(4, 0, 0, 0);
                MeterZone.Children.Add(extraMeter);
            }
            MeterZone.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetRow(MeterZone, 2);
            grid.Children.Add(MeterZone);

            MuteButton = MuteButtonWide();
            SoloButton = SmallButton("CUE", Loc.T(
                "Escuchar este cue por los cascos (no toca la sala)",
                "Listen to this cue on headphones (does not touch the room)"));
            SoloButton.Visibility = Visibility.Collapsed;
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
            };
            buttons.Children.Add(MuteButton);
            buttons.Children.Add(SoloButton);
            Grid.SetRow(buttons, 3);
            grid.Children.Add(buttons);

            MuteButton.Click += (_, _) =>
            {
                Board?.ToggleMute(Key);
                RepaintAll?.Invoke();
            };

            Extras = new StackPanel
            { Margin = new Thickness(0, 2, 0, 0), Height = UiMetrics.StripExtrasHeight };
            Grid.SetRow(Extras, 4);
            grid.Children.Add(Extras);

            FullContent = grid;
            ContentHost = new Grid();
            ContentHost.Children.Add(grid);

            Root = new Border
            {
                Width = UiMetrics.StripWidth,
                Height = UiMetrics.StripHeight,
                Margin = new Thickness(5, 8, 5, 8),
                Padding = new Thickness(8, 6, 8, 6),
                VerticalAlignment = VerticalAlignment.Top,
                Background = Theme.ToolbarBgBrush,
                BorderBrush = Theme.ButtonBgBrush,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Child = ContentHost,
            };

            Fader.ValueChanged += (_, e) =>
            {
                double gain = AudioLevels.PositionToGain(e.NewValue / PosSteps);
                Db.Text = AudioLevels.DbLabel(gain);
                if (Syncing)
                    return;
                EmitOperatorGain(gain);
            };

            // DOBLE CLIC → 0 dB, como en cualquier mesa.
            Fader.DoubleTapped += (_, e) =>
            {
                GoToUnity();
                e.Handled = true;
            };

            PaintButtons();
        }

        private void EmitOperatorGain(double gain)
        {
            int pedido = GainPercent(gain);
            int suena = Board?.FaderMoved(Key, pedido) ?? pedido;
            PaintButtons();
            FaderChanged?.Invoke(suena);
        }

        private void CommitTypedDb()
        {
            if (AudioLevels.ParseDb(Db.Text) is not double gain)
            {
                ShowCurrentDb();
                return;
            }
            Syncing = true;
            Fader.Value = AudioLevels.GainToPosition(gain) * PosSteps;
            Syncing = false;
            Db.Text = AudioLevels.DbLabel(gain);
            EmitOperatorGain(gain);
        }

        private void ShowCurrentDb() =>
            Db.Text = AudioLevels.DbLabel(AudioLevels.PositionToGain(Fader.Value / PosSteps));

        public void GoToUnity()
        {
            Syncing = true;
            Fader.Value = AudioLevels.GainToPosition(1.0) * PosSteps;
            Syncing = false;
            Db.Text = AudioLevels.DbLabel(1.0);
            EmitOperatorGain(1.0);
        }

        private static int GainPercent(double gain) => (int)Math.Round(gain * 100.0);

        private static Button SmallButton(string text, string tip)
        {
            var b = new Button
            {
                Content = text,
                Width = UiMetrics.StripButtonWidth, Height = UiMetrics.StripButtonHeight,
                FontSize = 11, FontWeight = FontWeights.Bold,
                Margin = new Thickness(2, 0, 2, 0),
                Background = Theme.ButtonBgBrush,
                Foreground = Theme.TextPrimaryBrush,
            };
            ToolTipService.SetToolTip(b, tip);
            return b;
        }

        private static Button MuteButtonWide()
        {
            var b = new Button
            {
                Content = "MUTE",
                Width = UiMetrics.WideMuteWidth, Height = UiMetrics.WideMuteHeight,
                FontSize = 12, FontWeight = FontWeights.Bold,
                Background = Theme.ButtonBgBrush,
                Foreground = Theme.PgmBrush,
            };
            ToolTipService.SetToolTip(b, Loc.T("Silenciar este strip", "Mute this strip"));
            return b;
        }

        public Func<int>? AuditionSource;
        public Func<bool>? CueAvailable;

        public void EnableSolo()
        {
            _wideMute = false;
            MuteButton.Content = "MUTE";
            MuteButton.Width = UiMetrics.StripButtonWidth;
            MuteButton.Height = UiMetrics.StripButtonHeight;
            MuteButton.FontSize = 11;
            MuteButton.Margin = new Thickness(2, 0, 2, 0);

            SoloButton.Visibility = Visibility.Visible;
            SoloButton.Click += (_, _) =>
            {
                CueRequested?.Invoke(Key);
                RepaintAll?.Invoke();
            };
            PaintButtons();
        }

        public void PaintButtons()
        {
            bool muted = Board?.IsMuted(Key) ?? false;
            bool soloed = AuditionSource?.Invoke() == Key;

            bool hayCascos = CueAvailable?.Invoke() ?? true;
            SoloButton.IsEnabled = hayCascos;
            ToolTipService.SetToolTip(SoloButton, hayCascos
                ? Loc.T("Escuchar este cue por los cascos (no toca la sala)",
                        "Listen to this cue on headphones (does not touch the room)")
                : Loc.T("Sin dispositivo de audición: elígelo en Ajustes → Audio",
                        "No audition device: pick one in Settings → Audio"));
            MuteButton.Background = muted ? Theme.PgmBrush : Theme.ButtonBgBrush;
            MuteButton.Foreground = muted
                ? new SolidColorBrush(Colors.Black)
                : _wideMute ? Theme.PgmBrush : Theme.TextPrimaryBrush;
            SoloButton.Background = soloed ? Theme.StandbyBrush : Theme.ButtonBgBrush;
            SoloButton.Foreground = soloed ? new SolidColorBrush(Colors.Black) : Theme.TextPrimaryBrush;
        }

        public void SetLevels(float l, float r)
        {
            VuL?.SetLevel(l);
            VuR?.SetLevel(r);
        }

        public void SetFaderSilent(int value)
        {
            double gain = Math.Clamp(value, 0, MaxGainPercent) / 100.0;
            Syncing = true;
            Fader.Value = AudioLevels.GainToPosition(gain) * PosSteps;
            Db.Text = AudioLevels.DbLabel(gain);
            Syncing = false;
        }

        public static int MaxGainPercent => (int)Math.Round(AudioLevels.MaxGain * 100.0);

        public void VuTooltip(string tip)
        {
            if (VuL is null) return;
            ToolTipService.SetToolTip(VuL, tip);
            ToolTipService.SetToolTip(VuR!, tip);
        }
    }

    /// <summary>Strip de un cue activo — mismo reparto que WPF.CueStrip.</summary>
    private sealed class CueStrip : Strip
    {
        public readonly int CueId;
        public readonly bool MirrorAudio;

        public CueStrip(MixerStripSpec spec, Cue? cue,
                        Action<int, double> commit, Action<int> activate)
        {
            CueId = spec.Id;
            MirrorAudio = spec.MirrorAudio;
            bool isAudio = spec.Type == CueType.Audio;
            bool withVu = isAudio || spec.MirrorAudio;
            string glyph = isAudio ? Icons.Audio : Icons.Clapper;
            Init($"{glyph} {spec.Number} · {spec.Name}",
                 isAudio ? Theme.InfoAltBrush : Theme.MagentaAltBrush,
                 withVu: withVu);
            string tip = $"{spec.Number} · {spec.Name}\n" + Loc.T(
                $"Clic en el título: editar en Inspector → {Icons.Volume} Audio",
                $"Click the title: edit in Inspector → {Icons.Volume} Audio");
            if (!withVu)
            {
                tip += "\n" + Loc.T(
                    "VU n/d (mpv → dispositivo); su nivel se ve en MASTER",
                    "VU n/a (mpv → device); its level shows on MASTER");
            }
            ToolTipService.SetToolTip(Root, tip);
            TitleText.Tapped += (_, _) => activate(CueId);

            SetFaderSilent((int)Math.Round((cue?.Volume ?? 1.0) * 100.0));
            FaderChanged = v => commit(CueId, v / 100.0);
            PaintButtons();
        }

        /// <summary>El volumen del modelo cambió por otra vía (inspector) →
        /// reflejarlo, salvo que el operador esté arrastrando ESTE fader.
        ///
        /// WPF comprueba Fader.IsMouseCaptureWithin / IsKeyboardFocusWithin.
        /// WinUI 3 no expone captura de puntero por elemento tan directo: se
        /// aproxima con Fader.PointerCaptures (arrastre con el ratón) y
        /// FocusState (foco de teclado, p. ej. tras Tab) — cubre los dos
        /// gestos reales con los que se "posee" el fader.</summary>
        public void RefreshFromModel(Cue? cue)
        {
            if (cue is null)
                return;
            bool dragging = Fader.PointerCaptures is { Count: > 0 };
            if (dragging || Fader.FocusState != FocusState.Unfocused)
                return;
            if (Board?.IsMuted(Key) == true)
                return;
            double target = AudioLevels.GainToPosition(cue.Volume) * PosSteps;
            if (Math.Abs(Fader.Value - target) >= 1.0)
                SetFaderSilent((int)Math.Round(cue.Volume * 100.0));
        }
    }

    /// <summary>Tira plegada de un strip de cue: el nombre girado, DE PIE.
    /// Pintado a mano en Win2D — ver la cabecera del archivo (WinUI 3 no
    /// tiene FrameworkElement.LayoutTransform).</summary>
    private sealed class CollapsedLabelCanvas : UserControl
    {
        private readonly CanvasControl _canvas = new();
        private string _text = "";
        private Color _color = Colors.White;

        public CollapsedLabelCanvas()
        {
            Content = _canvas;
            _canvas.Draw += OnDraw;
            _canvas.SizeChanged += (_, _) => _canvas.Invalidate();
        }

        public void SetText(string text, uint paletteColor)
        {
            _text = text;
            _color = Color.FromArgb(0xff, (byte)((paletteColor >> 16) & 0xff),
                (byte)((paletteColor >> 8) & 0xff), (byte)(paletteColor & 0xff));
            _canvas.Invalidate();
        }

        private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            var ds = args.DrawingSession;
            double w = sender.ActualWidth, h = sender.ActualHeight;
            if (w <= 0 || h <= 0)
                return;

            using var format = new CanvasTextFormat
            {
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                WordWrapping = CanvasWordWrapping.NoWrap,
                TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
                TrimmingSign = CanvasTrimmingSign.Ellipsis,
            };
            // Layout HORIZONTAL, del tamaño que tendría de pie (ancho = alto
            // del strip, alto = ancho del strip), girado 90° sobre el centro
            // del control — mismo efecto visual que RotateTransform(90) +
            // LayoutTransform en WPF, con el principio del texto (el número
            // del cue) cerca de la CABECERA de la tira.
            float layoutW = (float)h - 8f, layoutH = (float)w;
            using var layout = new CanvasTextLayout(ds, _text, format, layoutW, layoutH);

            float cx = (float)(w / 2.0), cy = (float)(h / 2.0);
            ds.Transform = Matrix3x2.CreateRotation((float)(Math.PI / 2), new Vector2(cx, cy));
            ds.DrawTextLayout(layout, cx - layoutW / 2f, cy - layoutH / 2f, _color);
            ds.Transform = Matrix3x2.Identity;
        }
    }
}
