// =============================================================================
// AudioMixerPanel.cs — Pestaña AUDIO: la mesa de mezclas del show
// =============================================================================
//
// ORGANIZACIÓN (de izquierda a derecha, como una mesa real)
//   · MASTER    — fader master + VU de PROGRAMA (loopback: lo que sale del
//                 dispositivo) + medidor GR del limitador de bus + info de
//                 dispositivo/driver.
//   · AUDICIÓN  — trim de preescucha (se aplica SOBRE el volumen del cue
//                 auditado) + VU del PVW.
//   · CUES      — un strip por cue ACTIVO: fader en vivo (misma ruta de commit
//                 que el inspector → modelo + motor + undo) y VU real del
//                 stream para cues de AUDIO y para cues de VÍDEO con AUDIO
//                 ESPEJO (motor HAP propio o ruta híbrida: su audio pasa por
//                 el AudioEngine y el controlador remapea su VU al cue). Solo
//                 el vídeo SIN espejo (mpv → dispositivo directo, el motor no
//                 ve las muestras) lleva el honesto "VU n/d" (nivel en MASTER).
//
// SINCRONÍA DE FADERS
//   Los faders fijos (master/audición) emiten (valor, final): en vivo a cada
//   cambio y un commit "final" con debounce de 400 ms para no clavar el disco
//   persistiendo settings a cada pixel de arrastre. Los faders de cue commitean
//   como el inspector (cada cambio: el modelo ya es en-memoria).
// =============================================================================

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WinCue.Controller;
using WinCue.Core;

namespace WinCue.App;

public sealed class AudioMixerPanel : ContentControl
{
    // ── Callbacks (los cablea MainWindow.Attach) ───────────────────────
    /// <summary>Fader master (0-100, final=commit para persistir).</summary>
    public Action<int, bool>? MasterVolumeChanged;
    /// <summary>Fader de audición (0-100, final=commit para persistir).</summary>
    public Action<int, bool>? AuditionVolumeChanged;
    /// <summary>Fader de un cue activo (cueId, volumen 0..1).</summary>
    public Action<int, double>? CueVolumeCommitted;
    /// <summary>Clic en el título de un strip de cue → abrir su edición
    /// (la ventana selecciona el cue y muestra Inspector → 🔊 Audio).</summary>
    public Action<int>? CueStripActivated;
    /// <summary>GR del limitador del bus master (dB ≥ 0), para el medidor.</summary>
    public Func<float>? MasterGainReductionProvider;

    private static readonly Brush FgDim = Frozen(new SolidColorBrush(Theme.TextDimmer));
    private static readonly Brush AccentPgm = Frozen(new SolidColorBrush(Theme.Pgm));
    private static readonly Brush AccentPvw = Frozen(new SolidColorBrush(Theme.Go));
    private static readonly Brush DbGreen = Frozen(new SolidColorBrush(Theme.Go));

    private readonly Strip _master;
    private readonly Strip _audition;
    private readonly TextBlock _grText;
    private readonly GrBar _grBar = new();
    private readonly TextBlock _deviceInfo;
    private readonly StackPanel _cueHost = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _emptyHint;
    private readonly Dictionary<int, CueStrip> _cueStrips = new();

    public AudioMixerPanel()
    {
        _master = Strip.Build("MASTER", AccentPgm, withVu: true, extraMeter: _grBar);
        _grBar.ToolTip = Loc.T(
            "Limitador del bus (techo −1 dBFS): reducción de ganancia.\nLa suma de cues no puede saturar el dispositivo.",
            "Bus limiter (−1 dBFS ceiling): gain reduction.\nThe sum of cues cannot clip the device.");
        _master.VuTooltip(Loc.T(
            "VU de PROGRAMA. Con loopback: lo que sale del dispositivo\n(audio + vídeo + todo). Con ASIO o sin loopback disponible:\nla mezcla interna de cues. La línea de abajo dice cuál.",
            "PROGRAM VU. With loopback: what leaves the device\n(audio + video + everything). With ASIO or no loopback available:\nthe internal cue mix. The line below says which one."));

        _grText = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"), FontSize = 11,
            Foreground = Theme.WarnBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            // Va dentro de la zona de extras, que ya tiene el alto reservado en
            // todos los strips: aparezca o no la línea de GR, nada se mueve.
            Text = "",
        };
        _master.Extras.Children.Add(_grText);
        _deviceInfo = new TextBlock
        {
            FontSize = 10.5, Foreground = FgDim, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 0),
        };
        // El detalle del dispositivo pasa a TOOLTIP en vez de ocupar tres líneas
        // bajo el fader: es un dato de configuración, no de operación, y ahí
        // abajo solo servía para descuadrar los strips entre sí.
        _master.Root.ToolTip = "";

        _audition = Strip.Build($"{Icons.Headphones} {Loc.T("AUDICIÓN", "AUDITION")}", AccentPvw, withVu: true);
        _audition.VuTooltip(Loc.T("VU de la preescucha (PVW).", "Audition (PVW) VU."));
        _audition.Root.ToolTip = Loc.T("Trim × volumen del cue", "Trim × cue volume");

        _emptyHint = new TextBlock
        {
            Text = Loc.T("— sin cues sonando —", "— no cues playing —"),
            Foreground = FgDim, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24, 0, 24, 0),
        };

        var strips = new StackPanel { Orientation = Orientation.Horizontal };
        strips.Children.Add(_master.Root);
        strips.Children.Add(_audition.Root);
        strips.Children.Add(new Border
        {
            // Gris #3a3a3a: sin token de SEPARADOR con ese valor; se usa el del
            // botón alternativo, que es exactamente el mismo gris
            Width = 1, Background = Frozen(new SolidColorBrush(Theme.ButtonBgAlt)),
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

        // Faders fijos: en vivo a cada cambio + commit final con debounce
        WireDebouncedFader(_master, (v, final) => MasterVolumeChanged?.Invoke(v, final));
        WireDebouncedFader(_audition, (v, final) => AuditionVolumeChanged?.Invoke(v, final));

        // Medidor GR: sondeo ligero solo con la pestaña visible
        var grTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        grTimer.Tick += (_, _) =>
        {
            if (!IsVisible || MasterGainReductionProvider is null)
                return;
            float gr = MasterGainReductionProvider();
            _grBar.SetDb(gr);
            _grText.Text = gr >= 0.1f ? $"GR −{gr:0.0} dB" : "";
        };
        grTimer.Start();
    }

    // ── API que alimenta MainWindow ────────────────────────────────────

    public void SetMasterLevel(float l, float r) => _master.SetLevels(l, r);

    public void SetAuditionLevel(float l, float r) => _audition.SetLevels(l, r);

    public void SetMasterFader(int value) => _master.SetFaderSilent(value);

    public void SetAuditionFader(int value) => _audition.SetFaderSilent(value);

    /// <summary>Detalle del dispositivo. Va al TOOLTIP del strip MASTER, no
    /// debajo del fader: es información de configuración, se consulta de uvas a
    /// peras, y ocupando tres líneas descuadraba los strips entre sí.</summary>
    public void SetDeviceInfo(string text)
    {
        _deviceInfo.Text = text;
        _master.Root.ToolTip = text;
    }

    public void UpdateCueLevel(int cueId, float l, float r)
    {
        if (_cueStrips.TryGetValue(cueId, out var s))
            s.SetLevels(l, r);
    }

    /// <summary>Reconcilia los strips con los cues activos (misma cadencia que
    /// el panel PLAYING). Solo Audio y Video tienen strip (los demás tipos no
    /// suenan). No recrea strips existentes (sin parpadeo, sin soltar faders).</summary>
    public void SyncActive(IReadOnlyList<ActiveCueInfo> rows, Func<int, Cue?> resolveCue)
    {
        var seen = new HashSet<int>();
        foreach (var row in rows)
        {
            if (row.Type != CueType.Audio && row.Type != CueType.Video)
                continue;
            seen.Add(row.Id);
            if (_cueStrips.TryGetValue(row.Id, out var strip))
            {
                // MirrorAudio no cambia en la vida del cue SALVO el fallback
                // del espejo (audio recuperado por mpv): recrear el strip
                // para que el VU y la nota reflejen la ruta real.
                if (strip.MirrorAudio == row.MirrorAudio)
                {
                    strip.RefreshFromModel(resolveCue(row.Id));
                    continue;
                }
                int at = _cueHost.Children.IndexOf(strip.Root);
                _cueHost.Children.Remove(strip.Root);
                _cueStrips.Remove(row.Id);
                var recreated = MakeStrip(row, resolveCue);
                _cueHost.Children.Insert(at, recreated.Root);
                continue;
            }
            _cueHost.Children.Add(MakeStrip(row, resolveCue).Root);
            ApplySolo();   // nace enmudecido si hay un solo puesto
        }
        foreach (int id in _cueStrips.Keys.ToList())
        {
            if (seen.Contains(id))
                continue;
            _cueHost.Children.Remove(_cueStrips[id].Root);
            _cueStrips.Remove(id);
        }
        _emptyHint.Visibility = _cueStrips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Interno ────────────────────────────────────────────────────────

    private CueStrip MakeStrip(ActiveCueInfo row, Func<int, Cue?> resolveCue)
    {
        var s = new CueStrip(row, resolveCue(row.Id),
            (id, vol) => CueVolumeCommitted?.Invoke(id, vol),
            id => CueStripActivated?.Invoke(id));
        // El solo solo existe entre cues: aquí sí hay algo contra lo que solear.
        s.EnableSolo();
        s.SoloToggled = _ => ApplySolo();
        _cueStrips[row.Id] = s;
        return s;
    }

    /// <summary>Reparte el silencio del SOLO. Con uno o más cues en solo, los
    /// demás enmudecen; al soltar el último, vuelven — pero solo los que enmudeció
    /// el solo: si el operador silenció uno a mano, sigue mudo. Un cue que entra
    /// a sonar con un solo puesto nace enmudecido, o el solo duraría hasta el
    /// siguiente GO y no hasta que se suelte.</summary>
    private void ApplySolo()
    {
        bool anySolo = _cueStrips.Values.Any(s => s.Soloed);
        foreach (var s in _cueStrips.Values)
            s.SetMutedBySolo(anySolo && !s.Soloed);
    }

    private static void WireDebouncedFader(Strip strip, Action<int, bool> emit)
    {
        int pending = -1;
        var final = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        final.Tick += (_, _) =>
        {
            final.Stop();
            if (pending >= 0)
                emit(pending, true);
            pending = -1;
        };
        strip.FaderChanged = v =>
        {
            emit(v, false);
            pending = v;
            final.Stop();
            final.Start();
        };
        // El silencio se aplica EN VIVO y no se guarda: el ajuste persistido
        // sigue siendo el volumen real, para que quitar el mute lo devuelva y
        // para que la app no arranque muda mañana.
        strip.MuteChanged = v =>
        {
            emit(v, false);
            final.Stop();
            pending = -1;
        };
    }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    /// <summary>Escala en dB al costado del fader. Las marcas se colocan con la
    /// MISMA ley que mueve el fader (AudioLevels), así que el rótulo «0» cae
    /// exactamente donde el fader da 0 dB — si se cambia la ley, la escala se
    /// entera sola.
    ///
    /// El recorrido útil de un Slider vertical de WPF no es todo su alto: el
    /// pomo se centra en el valor, así que arriba y abajo sobra medio pomo que
    /// hay que descontar o la escala queda corrida contra el fader.</summary>
    private sealed class FaderScale : FrameworkElement
    {
        /// <summary>Alto del pomo del fader vertical (App.xaml, FaderThumb).</summary>
        private const double ThumbH = 13.0;

        private static readonly Brush TickBrush = Frozen(new SolidColorBrush(Theme.TextDimmer));
        private static readonly Pen TickPen = FrozenPen(new Pen(TickBrush, 1.0));
        private static readonly Pen UnityPen =
            FrozenPen(new Pen(Frozen(new SolidColorBrush(Theme.TextSecondary)), 1.0));

        private static Pen FrozenPen(Pen p) { p.Freeze(); return p; }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0)
                return;
            double top = ThumbH / 2.0, travel = h - ThumbH;
            if (travel <= 0)
                return;

            foreach (double db in AudioLevels.ScaleTicks)
            {
                double y = top + (1.0 - AudioLevels.DbToPosition(db)) * travel;
                // La unidad se marca más clara: es la referencia que se busca.
                bool unity = Math.Abs(db) < 0.001;
                dc.DrawLine(unity ? UnityPen : TickPen,
                            new Point(w - 5, y), new Point(w, y));
                var label = Label(db > 0 ? $"+{db:0}" : $"{db:0}",
                                  unity ? UnityPen.Brush : TickBrush);
                dc.DrawText(label, new Point(w - 7 - label.Width, y - label.Height / 2.0));
            }

            // El fondo del recorrido es SILENCIO, no −60 dB: se rotula aparte
            // porque no es un punto de la ley, es el final del fader.
            var inf = Label("dB", TickBrush);
            dc.DrawText(inf, new Point(w - 7 - inf.Width, h - inf.Height));
        }

        private static FormattedText Label(string text, Brush brush) => new(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Consolas"), 9, brush, 1.0);
    }

    /// <summary>Strip genérico: título, fader vertical + VU estéreo, etiqueta
    /// dB y zona de extras. Base de master/audición y de los strips por cue.</summary>
    private class Strip
    {
        /// <summary>Pasos del recorrido del fader. No son «por ciento de
        /// volumen»: son posición, y la ganancia sale de AudioLevels.</summary>
        public const double PosSteps = 1000.0;

        public Border Root = null!;
        public Slider Fader = null!;
        public TextBlock Db = null!;
        public StackPanel Extras = null!;
        public Button MuteButton = null!;
        public Button SoloButton = null!;

        /// <summary>Cambio del fader en GANANCIA POR CIENTO (100 = 0 dB), que
        /// es lo que guardan el modelo y los ajustes desde siempre.</summary>
        public Action<int>? FaderChanged;

        /// <summary>Cambio que NO debe PERSISTIR: el silencio es una maniobra de
        /// directo, no un ajuste. Sin esta ruta aparte, silenciar el MASTER
        /// guardaba master_volume=0 y al reabrir la app la mesa arrancaba muda.
        /// Si nadie la cablea (strips de cue) se cae a FaderChanged.</summary>
        public Action<int>? MuteChanged;

        /// <summary>Al entrar o salir del solo (solo strips de cue).</summary>
        public Action<Strip>? SoloToggled;

        protected VuMeterControl? VuL, VuR;
        protected StackPanel MeterZone = null!;
        protected TextBlock TitleText = null!;
        protected bool Syncing;

        /// <summary>Silenciado a mano (botón M) o por el solo de otro strip.</summary>
        public bool Muted { get; private set; }
        public bool Soloed { get; private set; }

        /// <summary>Ganancia por ciento anterior al silencio, para devolverla
        /// intacta al quitarlo: el mute NO es «bajar el fader a cero», es
        /// apartarlo un momento y poder volver exactamente a donde estaba.</summary>
        private int _preMuteGain = 100;
        private bool _mutedBySolo;

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
            // 0 rótulo · 1 lectura dB · 2 escala+fader+VU · 3 botones · 4 extras
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TitleText = new TextBlock
            {
                Text = title, FontWeight = FontWeights.Bold, FontSize = 12,
                Foreground = accent, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis,
                // El rótulo mide lo MISMO lleve icono o no: alto explícito, más
                // BlockLineHeight para que la caja de línea sea exactamente
                // LineHeight y deje de depender del alto del glifo. Sin esto
                // «🎧 AUDICIÓN» ocupaba más que «MASTER» y bajaba con él todo
                // lo que tiene debajo.
                Height = UiMetrics.StripTitleLineHeight,
                LineHeight = UiMetrics.StripTitleLineHeight,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            };
            grid.Children.Add(TitleText);

            // LECTURA EN dB ARRIBA, justo bajo el rótulo: es el dato que se
            // consulta de un vistazo antes de tocar el fader, y abajo competía
            // con los botones.
            Db = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"), FontSize = 12,
                Foreground = DbGreen,
                // Ancho FIJO y texto centrado dentro: antes la cifra cambiaba de
                // ancho al mover el fader y arrastraba la columna entera.
                Width = UiMetrics.DbReadoutWidth,
                TextAlignment = TextAlignment.Center,
                Text = AudioLevels.DbLabel(1.0),
            };
            var dbFrame = new Border
            {
                Background = Theme.Solid(Theme.SunkenBg),
                BorderBrush = Theme.Solid(Theme.Divider),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 2, 4, 2),
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = Db,
            };
            Grid.SetRow(dbFrame, 1);
            grid.Children.Add(dbFrame);

            MeterZone = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            // ESCALA en dB a la izquierda del fader, con las marcas justo donde
            // la ley del fader coloca cada valor (AudioLevels).
            MeterZone.Children.Add(new FaderScale
            { Width = UiMetrics.FaderScaleWidth, Height = UiMetrics.FaderHeight });
            Fader = new Slider
            {
                // El fader recorre POSICIÓN (0..1000), no ganancia: la ganancia
                // sale de AudioLevels.PositionToGain. Con recorrido lineal en
                // ganancia, −20 dB caía al 10 % del fader y −45 dB al 0,5 %, con
                // todo el rango útil en el último milímetro. Los 1000 pasos dan
                // resolución de sobra para que el arrastre se sienta continuo.
                Orientation = Orientation.Vertical, Minimum = 0, Maximum = PosSteps,
                Value = PosSteps,
                // Alto FIJO y no MinHeight: con MinHeight cada strip se estiraba
                // a lo que le tocase y no había dos faders del mismo tamaño.
                // TODOS miden lo mismo, MASTER incluido: en una fila de strips
                // idénticos, uno más alto se lee como un descuadre, no como una
                // jerarquía. El MASTER ya se distingue por el rótulo en rojo y
                // por llevar la barra del limitador que no tiene ningún otro.
                Width = UiMetrics.FaderWidth, Height = UiMetrics.FaderHeight,
                IsMoveToPointEnabled = true,
            };
            MeterZone.Children.Add(Fader);
            // Los VU llevan el MISMO alto que el fader, no el que les toque al
            // estirarse: al fijar el alto del fader se destapó que crecían por
            // su cuenta y el strip quedaba con la escala del fader y la del
            // medidor descuadradas, que en una mesa se lee como una avería.
            if (withVu)
            {
                VuL = new VuMeterControl(vertical: true)
                { Width = 7, Height = UiMetrics.FaderHeight, Margin = new Thickness(6, 0, 0, 0) };
                VuR = new VuMeterControl(vertical: true)
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

            // BOTONERA: M siempre; S solo donde significa algo (los strips de
            // cue lo activan con EnableSolo). En MASTER y AUDICIÓN no hay nada
            // contra lo que solear, así que su hueco queda vacío en vez de
            // ofrecer un botón que no haría nada.
            MuteButton = SmallButton("M", Loc.T("Silenciar este strip", "Mute this strip"));
            SoloButton = SmallButton("S", Loc.T("Escuchar solo este cue", "Solo this cue"));
            SoloButton.Visibility = Visibility.Hidden;
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

            MuteButton.Click += (_, _) => SetMuted(!Muted);

            // Alto RESERVADO e IGUAL en todos, escriban algo ahí o no: un
            // TextBlock vacío de WPF sigue midiendo una línea, así que el hueco
            // ya existía solo en el MASTER y le descuadraba el strip.
            Extras = new StackPanel
            { Margin = new Thickness(0, 2, 0, 0), Height = UiMetrics.StripExtrasHeight };
            Grid.SetRow(Extras, 4);
            grid.Children.Add(Extras);

            Root = new Border
            {
                Width = UiMetrics.StripWidth, Margin = new Thickness(5, 8, 5, 8),
                Padding = new Thickness(8, 6, 8, 6),
                Background = Frozen(new SolidColorBrush(Theme.ToolbarBg)),
                BorderBrush = Frozen(new SolidColorBrush(Theme.ButtonBgAlt)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Child = grid,
            };

            Fader.ValueChanged += (_, e) =>
            {
                double gain = AudioLevels.PositionToGain(e.NewValue / PosSteps);
                Db.Text = AudioLevels.DbLabel(gain);
                if (Syncing)
                    return;
                // Mover el fader a mano deshace el silencio: si no, el operador
                // subiría un fader mudo sin entender por qué no suena.
                if (Muted)
                    ClearMuteFlag();
                FaderChanged?.Invoke(GainPercent(gain));
            };

            PaintButtons();
        }

        private static int GainPercent(double gain) => (int)Math.Round(gain * 100.0);

        /// <summary>Botón cuadrado de la botonera (M / S).</summary>
        private static Button SmallButton(string text, string tip) => new()
        {
            Content = text, Width = 26, Height = 20, ToolTip = tip,
            FontSize = 11, FontWeight = FontWeights.Bold,
            Margin = new Thickness(3, 0, 3, 0),
            Background = Theme.ButtonBgBrush,
            Foreground = Theme.TextPrimaryBrush,
        };

        /// <summary>Da de alta el botón S. Solo lo llaman los strips de cue:
        /// en MASTER y AUDICIÓN no hay nada contra lo que solear.</summary>
        public void EnableSolo()
        {
            SoloButton.Visibility = Visibility.Visible;
            SoloButton.Click += (_, _) =>
            {
                Soloed = !Soloed;
                PaintButtons();
                SoloToggled?.Invoke(this);
            };
        }

        /// <summary>Silencio a mano (botón M).</summary>
        public void SetMuted(bool muted)
        {
            if (muted == Muted)
                return;
            _mutedBySolo = false;
            ApplyMute(muted);
        }

        /// <summary>Silencio IMPUESTO por el solo de otro strip. Se distingue
        /// del manual para no pisarlo: un strip silenciado a mano sigue mudo
        /// cuando se acaba el solo.</summary>
        public void SetMutedBySolo(bool muted)
        {
            if (muted == Muted)
                return;
            if (!muted && !_mutedBySolo)
                return;   // lo silenció el operador: no lo devuelve el solo
            _mutedBySolo = muted;
            ApplyMute(muted);
        }

        private void ApplyMute(bool muted)
        {
            if (muted)
                _preMuteGain = GainPercent(
                    AudioLevels.PositionToGain(Fader.Value / PosSteps));
            Muted = muted;
            PaintButtons();
            // El fader BAJA a cero a la vista y el valor viaja por la ruta de
            // siempre: lo que se ve es lo que suena, sin un silencio invisible
            // por debajo que no cuadre con la posición del fader.
            SetFaderSilent(muted ? 0 : _preMuteGain);
            (MuteChanged ?? FaderChanged)?.Invoke(muted ? 0 : _preMuteGain);
        }

        /// <summary>El operador movió el fader a mano estando en silencio: el
        /// silencio se levanta sin tocar el valor que acaba de poner.</summary>
        private void ClearMuteFlag()
        {
            Muted = false;
            _mutedBySolo = false;
            PaintButtons();
        }

        protected void PaintButtons()
        {
            MuteButton.Background = Muted
                ? Theme.Solid(Theme.Pgm) : Theme.ButtonBgBrush;
            MuteButton.Foreground = Muted
                ? System.Windows.Media.Brushes.Black : Theme.TextPrimaryBrush;
            SoloButton.Background = Soloed
                ? Theme.Solid(Theme.Standby) : Theme.ButtonBgBrush;
            SoloButton.Foreground = Soloed
                ? System.Windows.Media.Brushes.Black : Theme.TextPrimaryBrush;
        }

        public void SetLevels(float l, float r)
        {
            VuL?.SetLevel(l);
            VuR?.SetLevel(r);
        }

        /// <summary>Coloca el fader a una GANANCIA POR CIENTO (100 = 0 dB) sin
        /// emitir cambio. La posición sale de la ley del fader, no del valor:
        /// son unidades distintas desde que el recorrido se reparte en dB.</summary>
        public void SetFaderSilent(int value)
        {
            double gain = Math.Clamp(value, 0, MaxGainPercent) / 100.0;
            Syncing = true;
            Fader.Value = AudioLevels.GainToPosition(gain) * PosSteps;
            // ValueChanged no salta si la posición no cambia (p. ej. dos
            // ganancias por debajo de −60 dB): la lectura se repinta a mano.
            Db.Text = AudioLevels.DbLabel(gain);
            Syncing = false;
        }

        /// <summary>Techo en las mismas unidades que viajan por FaderChanged.</summary>
        public static int MaxGainPercent => (int)Math.Round(AudioLevels.MaxGain * 100.0);

        public void VuTooltip(string tip)
        {
            if (VuL is null) return;
            VuL.ToolTip = tip;
            VuR!.ToolTip = tip;
        }
    }

    /// <summary>Strip de un cue activo. Audio → VU real del stream; Video con
    /// AUDIO ESPEJO (motor HAP o híbrido: su audio pasa por el AudioEngine)
    /// → también VU real (el controlador remapea el nivel del espejo al cue);
    /// Video sin espejo → sin VU por-cue (mpv directo al dispositivo): aviso
    /// honesto.</summary>
    private sealed class CueStrip : Strip
    {
        public readonly int CueId;

        /// <summary>Con qué MirrorAudio se construyó el strip: si cambia en
        /// vivo (fallback del espejo → audio por mpv), hay que recrearlo.</summary>
        public readonly bool MirrorAudio;

        public CueStrip(ActiveCueInfo info, Cue? cue,
                        Action<int, double> commit, Action<int> activate)
        {
            CueId = info.Id;
            MirrorAudio = info.MirrorAudio;
            bool isAudio = info.Type == CueType.Audio;
            bool withVu = isAudio || info.MirrorAudio;
            string glyph = isAudio ? Icons.Audio : Icons.Clapper;
            Init($"{glyph} {info.Number} · {info.Name}",
                 isAudio ? Frozen(new SolidColorBrush(Theme.InfoAlt))
                         : Frozen(new SolidColorBrush(Theme.MagentaAlt)),
                 withVu: withVu);
            Root.ToolTip = $"{info.Number} · {info.Name}\n" + Loc.T(
                $"Clic en el título: editar en Inspector → {Icons.Volume} Audio",
                $"Click the title: edit in Inspector → {Icons.Volume} Audio");
            TitleText.Cursor = System.Windows.Input.Cursors.Hand;
            TitleText.MouseLeftButtonUp += (_, _) => activate(CueId);

            // La explicación de por qué este cue no tiene VU va al tooltip, no
            // bajo el fader: dos líneas de texto ahí dejaban este strip más alto
            // que sus vecinos y la fila de mixer quedaba en escalera.
            if (!withVu)
            {
                Root.ToolTip += "\n" + Loc.T(
                    "VU n/d (mpv → dispositivo); su nivel se ve en MASTER",
                    "VU n/a (mpv → device); its level shows on MASTER");
            }

            // SetFaderSilent ya deja la lectura en dB puesta.
            SetFaderSilent((int)Math.Round((cue?.Volume ?? 1.0) * 100.0));
            FaderChanged = v => commit(CueId, v / 100.0);
            PaintButtons();
        }

        /// <summary>El volumen del modelo cambió por otra vía (inspector) →
        /// reflejarlo, salvo que el operador esté arrastrando ESTE fader.</summary>
        public void RefreshFromModel(Cue? cue)
        {
            if (cue is null || Fader.IsMouseCaptureWithin || Fader.IsKeyboardFocusWithin)
                return;
            // Silenciado: el modelo dice 0 porque lo puso el propio silencio.
            // Recolocar el fader aquí borraría el valor guardado para volver.
            if (Muted)
                return;
            // La comparación va en POSICIÓN, no en ganancia: son unidades
            // distintas desde que el recorrido se reparte en dB, y comparar una
            // con otra recolocaba el fader en cada vuelta de sincronía.
            double target = AudioLevels.GainToPosition(cue.Volume) * PosSteps;
            if (Math.Abs(Fader.Value - target) >= 1.0)
                SetFaderSilent((int)Math.Round(cue.Volume * 100.0));
        }
    }

    /// <summary>Medidor de reducción de ganancia del limitador: barra naranja
    /// que crece de ARRIBA hacia abajo (como en una mesa), escala 0-12 dB.</summary>
    private sealed class GrBar : FrameworkElement
    {
        private static readonly Brush Bg = Frozen(new SolidColorBrush(Theme.MeterBg));
        private static readonly Pen Frame = MakePen(Theme.BorderMeter);
        private static readonly Brush Fill = Frozen(new SolidColorBrush(Theme.Warn));

        private double _db;

        public GrBar() => Width = 7;

        public void SetDb(float db)
        {
            double clamped = Math.Clamp(db, 0.0, 12.0);
            if (Math.Abs(clamped - _db) < 0.05)
                return;
            _db = clamped;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(Bg, Frame, new Rect(0.5, 0.5, w - 1, h - 1));
            if (_db > 0.05)
                dc.DrawRectangle(Fill, null, new Rect(1, 1, w - 2, (h - 2) * (_db / 12.0)));
        }

        private static Pen MakePen(Color c)
        {
            var p = new Pen(new SolidColorBrush(c), 1);
            p.Freeze();
            return p;
        }
    }
}
