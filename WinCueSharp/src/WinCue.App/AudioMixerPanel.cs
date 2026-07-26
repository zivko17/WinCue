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

    /// <summary>Botón CUE de un strip: preescuchar ese cue por los cascos.</summary>
    public Action<int>? CueRequested;

    /// <summary>Qué cue suena por los cascos ahora mismo (−1 = ninguno).</summary>
    public Func<int>? AuditionSourceId;

    /// <summary>Hay dispositivo de audición configurado (si no, no hay CUE).</summary>
    public Func<bool>? CueAvailable;

    /// <summary>Qué fuente ocupa el bus CUE ahora mismo.</summary>
    public Func<CueBusSource>? CueBusSourceNow;
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
    private readonly TextBlock _cueSourceText;   // qué fuente ocupa el bus CUE
    private readonly StackPanel _cueHost = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _emptyHint;
    private readonly Dictionary<int, CueStrip> _cueStrips = new();

    /// <summary>Canales que el operador abrió A MANO estando parados. Sin
    /// recordarlo, la siguiente vuelta de sincronía volvería a plegar el que
    /// acaba de abrir.</summary>
    private readonly HashSet<int> _abiertosAMano = new();

    /// <summary>Claves reservadas del board para los dos strips fijos. Van en
    /// negativo: los ids de cue reales nunca lo son.</summary>
    private const int MasterKey = -1;
    private const int AuditionKey = -2;

    /// <summary>Silencio y solo, FUERA de la interfaz para poder probarlos
    /// (WinCue.Controller.MuteSoloBoard). El panel solo pulsa y pinta.</summary>
    private readonly MuteSoloBoard _board = new();

    /// <summary>El MISMO estado de silencio/solo que usa el inspector: los
    /// botones de las dos pestañas son los mismos botones, no dos copias que
    /// haya que mantener de acuerdo.</summary>
    public MuteSoloBoard Board => _board;

    /// <summary>Repinta la mesa entera (lo llama también el inspector tras
    /// tocar un botón desde su lado).</summary>
    public void RefreshStripButtons() => RepaintAllStrips();

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

        // Se llama CUE porque es el MASTER del bus de preescucha: el mismo al
        // que mandan los botones CUE de los strips. Llamarlo «audición» y a los
        // botones «cue» sería ponerle dos nombres a la misma cosa.
        _audition = Strip.Build($"{Icons.Headphones} CUE", AccentPvw, withVu: true);
        _audition.VuTooltip(Loc.T("VU de la preescucha (PVW).", "Audition (PVW) VU."));
        _audition.Root.ToolTip = Loc.T("Trim × volumen del cue", "Trim × cue volume");

        // QUÉ estás oyendo por los cascos. Las dos fuentes del bus se relevan,
        // así que lo único que falta para no perderse es saber cuál manda ahora.
        // Es INFORMACIÓN, no un modo: no puede contradecir a ningún botón, que
        // es justo lo que haría un selector de fuente.
        _cueSourceText = new TextBlock
        {
            FontSize = 10, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = AccentPvw,
            Text = "",
        };
        _audition.Extras.Children.Add(_cueSourceText);

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

    /// <summary>Coloca el fader MASTER desde los ajustes guardados. El board se
    /// entera también: si la mesa está silenciada, ese es el volumen al que
    /// tiene que volver al quitar el silencio (y el fader se queda abajo).</summary>
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

    /// <summary>Reconcilia los strips con el show. La mesa enseña TODOS los cues
    /// que pueden sonar —audio y vídeo con archivo—, no solo los que suenan
    /// ahora: así se ve la mesa entera y se puede dejar preparado un silencio
    /// antes de disparar, en vez de que los canales aparezcan y desaparezcan.
    ///
    /// Los que NO suenan salen PLEGADOS a una tira estrecha (un cue parado no
    /// tiene nivel ni VU que enseñar), y el que suena se abre solo. A mano se
    /// puede abrir cualquiera con un clic, y esa decisión se respeta hasta que
    /// el operador la deshaga.
    ///
    /// No recrea strips existentes: sin parpadeo y sin soltar faders.</summary>
    public void SyncActive(IReadOnlyList<ActiveCueInfo> rows, Func<int, Cue?> resolveCue,
                           IReadOnlyList<Cue>? allCues = null)
    {
        var sonando = rows.Where(r => r.Type is CueType.Audio or CueType.Video)
                          .ToDictionary(r => r.Id);

        // El orden de la mesa es el de la LISTA, no el del disparo: una mesa
        // cuyos canales cambian de sitio no se puede tocar de memoria.
        var deseados = new List<ActiveCueInfo>();
        if (allCues is not null)
        {
            foreach (var c in allCues)
            {
                if (c.Type is not (CueType.Audio or CueType.Video) || c.FilePath.Length == 0)
                    continue;
                deseados.Add(sonando.TryGetValue(c.Id, out var viva)
                    ? viva
                    : new ActiveCueInfo(c.Id, c.DisplayNumber, c.Name, c.Type, c.Color,
                                        0.0, null, null));
            }
        }
        else
        {
            deseados.AddRange(sonando.Values);
        }

        var seen = new HashSet<int>();
        foreach (var row in deseados)
        {
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
            // MakeStrip lo da de alta en el board, y el alta ya reparte el solo:
            // si hay uno puesto, el strip nuevo nace mudo.
            _cueHost.Children.Add(MakeStrip(row, resolveCue).Root);
        }
        foreach (int id in _cueStrips.Keys.ToList())
        {
            if (seen.Contains(id))
                continue;
            _cueHost.Children.Remove(_cueStrips[id].Root);
            _cueStrips.Remove(id);
            // BAJA EN EL BOARD, y no es un detalle: si el cue que se va era el
            // único en solo, sin esto los demás se quedan mudos para siempre
            // por un solo que ya no existe.
            _board.Remove(id);
            _abiertosAMano.Remove(id);
        }

        // PLEGADO: abierto si suena, o si el operador lo abrió a mano.
        foreach (var (id, strip) in _cueStrips)
            strip.SetCollapsed(!sonando.ContainsKey(id) && !_abiertosAMano.Contains(id));

        _emptyHint.Visibility = _cueStrips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Entrar o salir de la lista de activos habilita o deshabilita los
        // botones del inspector: sin strip vivo no hay nada que silenciar.
        StripsRepainted?.Invoke();
    }

    // ── Interno ────────────────────────────────────────────────────────

    private CueStrip MakeStrip(ActiveCueInfo row, Func<int, Cue?> resolveCue)
    {
        var s = new CueStrip(row, resolveCue(row.Id),
            (id, vol) => CueVolumeCommitted?.Invoke(id, vol),
            id => CueStripActivated?.Invoke(id));
        s.Key = row.Id;
        s.Board = _board;
        s.RepaintAll = RepaintAllStrips;
        s.AuditionSource = () => AuditionSourceId?.Invoke() ?? -1;
        s.CueAvailable = () => CueAvailable?.Invoke() ?? true;
        s.CueRequested = id => CueRequested?.Invoke(id);
        // El solo solo existe entre cues: aquí sí hay algo contra lo que solear.
        s.EnableSolo();
        // Plegable: el nombre de pie en la tira estrecha.
        s.EnableCollapse($"{row.Number} · {row.Name}", Theme.Solid(Theme.TextSecondary));
        s.CollapseToggled = st =>
        {
            // Abrir a mano es una decisión del operador y se RESPETA hasta que
            // la deshaga: sin recordarla, la siguiente vuelta de sincronía
            // volvería a plegar el canal que acaba de abrir.
            if (st.IsCollapsed)
                _abiertosAMano.Add(st.Key);
            else
                _abiertosAMano.Remove(st.Key);
            st.SetCollapsed(!st.IsCollapsed);
        };
        // El alta reparte el solo sola: si hay uno puesto, este nace mudo.
        _board.Add(row.Id, (int)Math.Round((resolveCue(row.Id)?.Volume ?? 1.0) * 100.0));
        s.PaintButtons();
        _cueStrips[row.Id] = s;
        return s;
    }

    /// <summary>Pone al día el aspecto de TODOS los strips con lo que dice el
    /// board. Barato (encender cuatro brochas) y quita de en medio toda una
    /// familia de fallos: la de «el botón se quedó encendido porque el repintado
    /// colgaba de un evento que esta vez no saltó».</summary>
    private void RepaintAllStrips()
    {
        _master.PaintButtons();
        _audition.PaintButtons();

        // El rótulo de la fuente del bus CUE se repinta con la mesa: es el
        // mismo tic que ya recorre los strips, no hace falta uno propio.
        var src = CueBusSourceNow?.Invoke() ?? CueBusSource.None;
        _cueSourceText.Text = src switch
        {
            CueBusSource.Pfl => Loc.T("● EN VIVO", "● LIVE"),
            CueBusSource.Preview => Loc.T("▶ PREVIEW", "▶ PREVIEW"),
            _ => "",
        };
        _cueSourceText.Foreground = src == CueBusSource.Pfl
            ? Theme.Solid(Theme.Pgm)      // pinchando señal que SÍ está al aire
            : Theme.Solid(Theme.Standby); // preview: algo que aún no ha salido

        foreach (var s in _cueStrips.Values)
            s.PaintButtons();
        // El inspector enseña los MISMOS botones para el cue seleccionado: si
        // no se entera, se queda enseñando el estado anterior.
        StripsRepainted?.Invoke();
    }

    /// <summary>El estado de silencio/solo cambió desde la mesa. Lo escucha el
    /// inspector, que tiene los mismos botones en su pestaña de audio.</summary>
    public Action? StripsRepainted;

    /// <summary>El board dice qué debe sonar en un strip. Aquí solo se reparte:
    /// el fader baja a la vista y el valor sale por la ruta que corresponda.
    ///
    /// MASTER y AUDICIÓN van EN VIVO Y SIN PERSISTIR (final:false). Es
    /// deliberado: el silencio es una maniobra de directo, no un ajuste — si se
    /// guardara, mañana la app arrancaría muda.</summary>
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
        // El silencio NO pasa por aquí: lo emite OnBoardGain con final:false,
        // en vivo y sin persistir. El ajuste guardado sigue siendo el volumen
        // real, para que quitar el mute lo devuelva y la app no arranque muda.
    }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    /// <summary>Strip genérico: título, fader vertical + VU estéreo, etiqueta
    /// dB y zona de extras. Base de master/audición y de los strips por cue.</summary>
    private class Strip
    {
        /// <summary>Pasos del recorrido del fader. No son «por ciento de
        /// volumen»: son posición, y la ganancia sale de AudioLevels.</summary>
        public const double PosSteps = 1000.0;

        public Border Root = null!;
        public Slider Fader = null!;
        /// <summary>Lectura en dB, EDITABLE: se puede teclear la cifra.</summary>
        public TextBox Db = null!;
        public StackPanel Extras = null!;
        public Button MuteButton = null!;
        public Button SoloButton = null!;

        /// <summary>Cambio del fader en GANANCIA POR CIENTO (100 = 0 dB), que
        /// es lo que guardan el modelo y los ajustes desde siempre.</summary>
        public Action<int>? FaderChanged;

        /// <summary>Clave de este strip en el MuteSoloBoard (id de cue, o las
        /// reservadas de MASTER/AUDICIÓN).</summary>
        public int Key = int.MinValue;

        /// <summary>La máquina de silencio/solo, que vive FUERA de la interfaz
        /// (WinCue.Controller.MuteSoloBoard) precisamente para poder probarla.
        /// Aquí dentro solo queda pulsar botones y pintar.</summary>
        public MuteSoloBoard? Board;

        /// <summary>Repinta la mesa ENTERA. Lo pone el panel: una pulsación en
        /// un strip puede cambiar el aspecto de los demás.</summary>
        public Action? RepaintAll;

        /// <summary>Pedir CUE (preescucha) de este strip. Es un toggle: pulsar
        /// el que ya suena por los cascos lo apaga.</summary>
        public Action<int>? CueRequested;

        protected VuMeterControl? VuL, VuR;
        protected StackPanel MeterZone = null!;
        protected TextBlock TitleText = null!;
        protected bool Syncing;

        // ── Plegado (solo strips de cue) ───────────────────────────────
        protected Grid ContentHost = null!;      // aloja completo y plegado
        protected FrameworkElement FullContent = null!;
        private Border? _collapsed;

        /// <summary>Está plegado a una tira estrecha.</summary>
        public bool IsCollapsed { get; private set; }

        /// <summary>El operador pidió plegar o desplegar este strip.</summary>
        public Action<Strip>? CollapseToggled;

        /// <summary>Da de alta la vista PLEGADA: una tira con el nombre de pie.
        /// Un cue parado no tiene nivel ni VU que enseñar, así que plegado no se
        /// pierde información — se gana sitio para los que sí suenan.</summary>
        public void EnableCollapse(string label, Brush accent)
        {
            var text = new TextBlock
            {
                Text = label,
                Foreground = accent,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                // DE PIE: en una tira de 34 px no cabe una línea de texto, y
                // rotarla deja leer el nombre entero sin ensanchar nada.
                LayoutTransform = new RotateTransform(-90),
            };
            _collapsed = new Border
            {
                Background = System.Windows.Media.Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = Loc.T("Clic para desplegar este canal",
                                "Click to expand this channel"),
                Child = text,
                Visibility = Visibility.Collapsed,
            };
            _collapsed.MouseLeftButtonUp += (_, _) => CollapseToggled?.Invoke(this);
            ContentHost.Children.Add(_collapsed);
        }

        /// <summary>Pliega o despliega. Sin EnableCollapse no hace nada: el
        /// MASTER y el CUE no se pliegan, siempre tienen algo que enseñar.</summary>
        public void SetCollapsed(bool collapsed)
        {
            if (_collapsed is null || collapsed == IsCollapsed)
                return;
            IsCollapsed = collapsed;
            FullContent.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            _collapsed.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
            Root.Width = collapsed ? UiMetrics.StripCollapsedWidth : UiMetrics.StripWidth;
            Root.Padding = collapsed
                ? new Thickness(2, 6, 2, 6) : new Thickness(8, 6, 8, 6);
        }

        /// <summary>El mute va en su forma ancha (MASTER/AUDICIÓN) o compacta
        /// (strips de cue, que comparten fila con el solo).</summary>
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
            // 0 rótulo · 1 lectura dB · 2 escala+fader+VU · 3 botones · 4 extras
            //
            // TODAS Auto, ninguna Star. Con la fila del fader en Star, el strip
            // se estiraba con el panel: al subir el separador, el fader se
            // quedaba arriba —está anclado ahí— y los botones se iban al fondo,
            // con un palmo de vacío en medio. Un strip mide lo que miden sus
            // piezas, que ya son todas de tamaño fijo, y el alto que sobre se
            // queda debajo, en el panel, que es a quien pertenece.
            var grid = new Grid();
            for (int i = 0; i < 5; i++)
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
            // EDITABLE: se teclean los dB. Es un TextBox sin fondo ni borde
            // propios —el marco lo pone el Border de fuera— así que se ve igual
            // que cuando era una etiqueta, pero se puede escribir dentro.
            Db = new TextBox
            {
                FontFamily = new FontFamily("Consolas"), FontSize = 12,
                Foreground = DbGreen,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CaretBrush = DbGreen,
                Padding = new Thickness(0),
                ToolTip = Loc.T("Escribe los dB y pulsa Intro",
                                "Type the dB value and press Enter"),
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

            // INTRO confirma, ESCAPE cancela y devuelve lo que había. Perder el
            // foco confirma también: quien escribe una cifra y se va a mover el
            // ratón espera que valga, no que se pierda.
            Db.KeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    CommitTypedDb();
                    System.Windows.Input.Keyboard.ClearFocus();
                    e.Handled = true;
                }
                else if (e.Key == System.Windows.Input.Key.Escape)
                {
                    ShowCurrentDb();
                    System.Windows.Input.Keyboard.ClearFocus();
                    e.Handled = true;
                }
            };
            Db.LostFocus += (_, _) => CommitTypedDb();
            // Al entrar, todo seleccionado: se teclea encima sin borrar antes.
            Db.GotKeyboardFocus += (_, _) => Db.SelectAll();

            MeterZone = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            // ESCALA en dB a la izquierda del fader, con las marcas justo donde
            // la ley del fader coloca cada valor (AudioLevels).
            MeterZone.Children.Add(new FaderScaleControl
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

            // BOTONERA. Arranca en la forma ANCHA —un solo botón que pone
            // MUTE— porque es la de MASTER y AUDICIÓN, que no tienen solo y por
            // tanto tienen el ancho del strip para ellos. Los strips de cue la
            // encogen a «M» al llamar a EnableSolo(), que es cuando aparece un
            // segundo botón con el que repartirse el sitio.
            MuteButton = MuteButtonWide();
            // CUE (preescucha), NO solo. El solo-in-place enmudece la SALA para
            // dejar oír uno: en un teatro es una trampa —un toque y el público
            // se queda sin media función—. CUE manda ese cue a los CASCOS y no
            // toca la sala, que es lo que de verdad se necesita en directo. El
            // bus ya existía: es el de audición.
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

            // Se repinta TODA la mesa tras cada pulsación, no solo este strip y
            // no solo si cambió la ganancia. El pintado colgaba de GainChanged,
            // que NO salta cuando lo que debe sonar no cambia — y entonces el
            // botón se quedaba encendido aunque el estado ya fuera otro. El
            // aspecto tiene que seguir al estado, no a los efectos secundarios.
            MuteButton.Click += (_, _) =>
            {
                Board?.ToggleMute(Key);
                RepaintAll?.Invoke();
            };

            // Alto RESERVADO e IGUAL en todos, escriban algo ahí o no: un
            // TextBlock vacío de WPF sigue midiendo una línea, así que el hueco
            // ya existía solo en el MASTER y le descuadraba el strip.
            Extras = new StackPanel
            { Margin = new Thickness(0, 2, 0, 0), Height = UiMetrics.StripExtrasHeight };
            Grid.SetRow(Extras, 4);
            grid.Children.Add(Extras);

            // El contenido completo y el colapsado conviven en el mismo hueco y
            // se turnan: así el strip conserva su estado —fader, silencio, VU—
            // al plegarlo y desplegarlo, en vez de reconstruirse y perderlo.
            FullContent = grid;
            ContentHost = new Grid();
            ContentHost.Children.Add(grid);

            Root = new Border
            {
                Width = UiMetrics.StripWidth, Margin = new Thickness(5, 8, 5, 8),
                Padding = new Thickness(8, 6, 8, 6),
                // Anclado ARRIBA: sin esto el Border se estira al alto del panel
                // aunque sus filas ya no lo hagan, y vuelve el vacío de antes.
                VerticalAlignment = VerticalAlignment.Top,
                Background = Frozen(new SolidColorBrush(Theme.ToolbarBg)),
                BorderBrush = Frozen(new SolidColorBrush(Theme.ButtonBgAlt)),
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

            // DOBLE CLIC → 0 dB, como en cualquier mesa. Va DESPUÉS del clic
            // simple, que con IsMoveToPointEnabled ya ha movido el fader a donde
            // cayó el ratón: este lo corrige a la unidad y esa es la posición
            // que queda. Handled corta el evento para que no siga subiendo.
            Fader.MouseDoubleClick += (_, e) =>
            {
                GoToUnity();
                e.Handled = true;
            };
            Fader.ToolTip = Loc.T("Doble clic: volver a 0 dB",
                                  "Double-click: back to 0 dB");

            PaintButtons();
        }

        /// <summary>Maniobra del operador sobre el fader: la ganancia va al
        /// board y de ahí al motor.
        ///
        /// Lo que se manda es lo que dice el BOARD, no lo que marca el control:
        /// con un solo ajeno puesto este strip sigue callado por mucho que se
        /// le suba el fader, y emitir el valor del control desharía el solo de
        /// otro sin querer.</summary>
        private void EmitOperatorGain(double gain)
        {
            int pedido = GainPercent(gain);
            int suena = Board?.FaderMoved(Key, pedido) ?? pedido;
            PaintButtons();
            FaderChanged?.Invoke(suena);
        }

        /// <summary>Aplica los dB ESCRITOS en la caja. Si no hay manera de
        /// leerlos se repone lo que había: un texto que no se entiende no puede
        /// convertirse en silencio, que es el peor sitio al que caer por error.</summary>
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

        /// <summary>Repone en la caja la cifra que corresponde al fader.</summary>
        private void ShowCurrentDb() =>
            Db.Text = AudioLevels.DbLabel(
                AudioLevels.PositionToGain(Fader.Value / PosSteps));

        /// <summary>Devuelve el fader a la UNIDAD (0 dB). Emite a mano en vez
        /// de fiarlo a ValueChanged: si ya estaba justo en 0 dB el evento no
        /// salta, y el doble clic tiene que levantar el silencio igualmente.</summary>
        public void GoToUnity()
        {
            Syncing = true;
            Fader.Value = AudioLevels.GainToPosition(1.0) * PosSteps;
            Syncing = false;
            Db.Text = AudioLevels.DbLabel(1.0);
            EmitOperatorGain(1.0);
        }

        private static int GainPercent(double gain) => (int)Math.Round(gain * 100.0);

        /// <summary>Botón de la botonera de un strip de CUE, que mete DOS en el
        /// ancho del strip. Lleva la palabra entera, no la inicial: una «M» de
        /// 11 px en un botón de 26 no se lee, y un botón que hay que adivinar no
        /// se pulsa en directo. Con 54 px cabe holgada.</summary>
        private static Button SmallButton(string text, string tip) => new()
        {
            Content = text,
            Width = UiMetrics.StripButtonWidth, Height = UiMetrics.StripButtonHeight,
            ToolTip = tip,
            FontSize = 11, FontWeight = FontWeights.Bold,
            Margin = new Thickness(2, 0, 2, 0),
            Background = Theme.ButtonBgBrush,
            Foreground = Theme.TextPrimaryBrush,
        };

        /// <summary>Botón ANCHO con la palabra entera, para los strips que no
        /// llevan solo. Se lee de un vistazo y es más difícil de fallar con la
        /// mano — que es lo que se le pide a un mute en directo.</summary>
        private static Button MuteButtonWide() => new()
        {
            Content = "MUTE",
            Width = UiMetrics.WideMuteWidth, Height = UiMetrics.WideMuteHeight,
            ToolTip = Loc.T("Silenciar este strip", "Mute this strip"),
            FontSize = 12, FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 0),
            Background = Theme.ButtonBgBrush,
            Foreground = Theme.Solid(Theme.Pgm),
        };

        /// <summary>Qué cue suena por los cascos (−1 = ninguno). Enciende el
        /// botón CUE del strip que corresponda.</summary>
        public Func<int>? AuditionSource;

        /// <summary>Hay dispositivo de cascos configurado. Sin él la preescucha
        /// no tiene a dónde ir y el botón CUE se deshabilita.</summary>
        public Func<bool>? CueAvailable;

        /// <summary>Da de alta el botón CUE. Solo lo llaman los strips de cue:
        /// el MASTER no se preescucha, y el strip CUE ES el bus de preescucha.</summary>
        public void EnableSolo()
        {
            // Entra un segundo botón: el mute deja el ancho entero y vuelve a
            // la «M» compacta para repartirse el sitio con él.
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

        /// <summary>Refleja el estado que dicta el board. Es TODO lo que le
        /// queda al strip del asunto: encender los botones y bajar el fader a
        /// cero, para que lo que se ve sea lo que suena.</summary>
        public void PaintButtons()
        {
            bool muted = Board?.IsMuted(Key) ?? false;
            // CUE encendido = ESTE cue es el que suena por los cascos.
            bool soloed = AuditionSource?.Invoke() == Key;

            // Sin dispositivo de cascos la preescucha NO TIENE A DÓNDE IR, así
            // que el botón se deshabilita en vez de dejarse pulsar y no sonar.
            // Un botón que se enciende sin efecto es peor que uno apagado: te
            // hace buscar el fallo en el sitio equivocado.
            bool hayCascos = CueAvailable?.Invoke() ?? true;
            SoloButton.IsEnabled = hayCascos;
            SoloButton.ToolTip = hayCascos
                ? Loc.T("Escuchar este cue por los cascos (no toca la sala)",
                        "Listen to this cue on headphones (does not touch the room)")
                : Loc.T("Sin dispositivo de audición: elígelo en Ajustes → Audio",
                        "No audition device: pick one in Settings → Audio");
            MuteButton.Background = muted
                ? Theme.Solid(Theme.Pgm) : Theme.ButtonBgBrush;
            // En reposo, el botón ANCHO lleva la palabra en rojo: dice a qué
            // botón estás mirando sin tener que estar encendido. La «M»
            // compacta no, que en 26 px el rojo sobre gris se lee como avería.
            MuteButton.Foreground = muted
                ? System.Windows.Media.Brushes.Black
                : _wideMute ? Theme.Solid(Theme.Pgm) : Theme.TextPrimaryBrush;
            SoloButton.Background = soloed
                ? Theme.Solid(Theme.Standby) : Theme.ButtonBgBrush;
            SoloButton.Foreground = soloed
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
            // SILENCIADO: no tocar NADA. El 0 que trae el modelo lo escribió el
            // propio silencio, así que devolvérselo al board machacaba la
            // ganancia guardada para volver: el strip se quedaba con 0 como
            // «valor al que regresar», quitar el silencio no cambiaba nada, y
            // al no cambiar nada tampoco se reemitía ni se repintaba — el botón
            // se quedaba encendido para siempre y el cue mudo.
            if (Board?.IsMuted(Key) == true)
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
