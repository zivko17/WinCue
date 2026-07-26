// =============================================================================
// MainWindow.xaml.cs — Ventana principal (implementa IShowView)
// =============================================================================
// Fase 5a: tabla de cues + transport + master + panel de activos + salida de
// vídeo básica. El inspector, las tarjetas y el monitor PGM/PVW llegan en 5b/5c.
// =============================================================================

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WinCue.Controller;
using WinCue.Core;

namespace WinCue.App;

public partial class MainWindow : Window, IShowView
{
    private CueModel _model = null!;
    private MainController _controller = null!;
    private SettingsManager _settings = null!;
    private Action? _applyEngineSettings;   // lo inyecta App (reconfigurar audio)
    // Hasta 4 salidas de vídeo independientes; la 0 es la principal (legado).
    // Se crean bajo demanda: solo existen las ventanas que algún cue usa.
    private readonly VideoOutputWindow?[] _videoOutputs = new VideoOutputWindow?[4];
    private readonly DispatcherTimer _statusTimer;

    // Mini-monitores PVW/PGM en vivo (mpv embebido y silenciado)
    private readonly LiveMonitorControl _pvw = new("PVW", Theme.GoDeep);
    private MonitorTransportBar _pvwBar = null!;   // reproducir/pausar el PVW
    private readonly LiveMonitorControl _pgm = new("PGM", Theme.Pgm);
    private DispatcherTimer? _pgmTimer;

    // VU estéreo junto a cada monitor: PGM = programa, PVW = audición local
    private readonly VuMeterControl _pgmVuL = new(vertical: true) { Width = 9 };
    private readonly VuMeterControl _pgmVuR = new(vertical: true) { Width = 9 };
    private readonly VuMeterControl _pvwVuL = new(vertical: true) { Width = 9 };
    private readonly VuMeterControl _pvwVuR = new(vertical: true) { Width = 9 };

    // Atajos configurables (Interfaz → Atajos de teclado)
    private Key _keyGo = Key.Space;
    private Key _keyStop = Key.S;
    private Key _keyPrev = Key.Up;

    // Hotkeys por cue (Triggers → Hotkey)
    private readonly Dictionary<Key, int> _cueHotkeys = new();

    // Drag & drop de filas (reordenar)
    private Point _dragStart;
    private int _dragRow = -1;

    private readonly ObservableCollection<CueRowViewModel> _rows = new();
    private readonly ObservableCollection<ActiveRowVM> _activeRows = new();

    private int _playheadIndex;
    private bool _suppressSelection;   // evita bucles selección↔playhead
    private string? _currentShowPath;  // ruta del show abierto (Save guarda ahí sin preguntar)

    public MainWindow()
    {
        InitializeComponent();
        CueGrid.ItemsSource = _rows;
        ActiveList.ItemsSource = _activeRows;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _statusTimer.Tick += (_, _) => { StatusLabel.Text = ""; _statusTimer.Stop(); };

        PreviewKeyDown += OnKeyDownGlobal;
    }

    /// <summary>Conecta modelo y controlador (llamado por App tras construir todo).</summary>
    public void Attach(CueModel model, MainController controller,
                       SettingsManager settings, Action? applyEngineSettings = null)
    {
        _model = model;
        _controller = controller;
        _settings = settings;
        _applyEngineSettings = applyEngineSettings;
        LoadKeyBindings();

        // Inspector → controlador (edición en vivo campo a campo)
        Inspector.CommitField = (cueId, key, value) =>
        {
            int row = _model.IndexOfId(cueId);
            if (row >= 0)
                _controller.OnCueEditCommitted(row,
                    new Dictionary<string, object?> { [key] = value });
        };
        Inspector.SpectrumProvider = cueId => _controller.GetAudioSpectrum(cueId);
        Inspector.GainReductionProvider = cueId => _controller.GetAudioGainReduction(cueId);
        Inspector.AudioAnalysisProvider = cueId => _controller.CueHasAudioAnalysis(cueId);
        // Aviso de fundido incoherente: la proyección del timeline es quien
        // sabe cuánto le queda al saliente de la misma pista al entrar el cue
        Inspector.CrossfadeHeadroomProvider = cueId => Timeline.OutgoingRemaining(cueId);

        // Panel AUDIO (mesa): faders master/audición/cues + VU + GR limitador
        Mixer.MasterGainReductionProvider = () => _controller.GetMasterGainReduction();
        Mixer.MasterVolumeChanged = (v, final) =>
        {
            _controller.OnMasterVolume(v);          // en vivo a cada cambio
            if (final)
                _settings.Set("master_volume", v);  // persistir con debounce
        };
        Mixer.AuditionVolumeChanged = (v, final) =>
            _controller.OnAuditionVolume(v, persist: final);
        Mixer.CueVolumeCommitted = (cueId, vol) =>
        {
            int row = _model.IndexOfId(cueId);
            if (row >= 0)
                _controller.OnCueEditCommitted(row,
                    new Dictionary<string, object?> { ["volume"] = vol });
        };
        Mixer.CueStripActivated = cueId =>
        {
            int row = _model.IndexOfId(cueId);
            if (row < 0)
                return;
            CueGrid.SelectedIndex = row;      // puebla el inspector (selección)
            BottomTabs.SelectedIndex = 1;     // pestaña del inspector
            Inspector.ShowAudioTab();
        };
        // MISMO estado de silencio/solo en la mesa y en el inspector: los
        // botones de las dos pestañas son los mismos botones. Cada lado avisa
        // al otro para repintar, porque un solo cambia el aspecto de strips que
        // no son el pulsado.
        // Alto del panel de pestañas tal como se dejó la última vez. El tope de
        // abajo es el MinHeight de la fila; el de arriba evita que un valor
        // raro guardado en otra pantalla deje la lista de cues sin sitio.
        int panelH = _settings.Get("bottom_pane_height", 330);
        BottomPaneRow.Height =
            new GridLength(Math.Clamp(panelH, 140, 900), GridUnitType.Pixel);

        Inspector.MuteSolo = Mixer.Board;
        Inspector.MuteSoloChanged = () => Mixer.RefreshStripButtons();
        Mixer.StripsRepainted = () => Inspector.RefreshMuteSoloButtons();

        // CUE = PFL: pinchar la señal VIVA de ese cue en los cascos. No es la
        // audición clásica, que relanza el archivo desde el principio — eso es
        // un preview y no dice nada de lo que está pasando en la sala ahora.
        void PedirCue(int cueId) => _controller.PflToggle(cueId);
        Mixer.CueRequested = PedirCue;
        Inspector.CueRequested = PedirCue;
        Mixer.AuditionSourceId = () => _controller.AuditionSourceCueId;
        Inspector.AuditionSourceId = () => _controller.AuditionSourceCueId;
        // Sin dispositivo de cascos NO hay preescucha posible: el botón CUE se
        // deshabilita en vez de encenderse y no sonar.
        Mixer.CueAvailable = () => _settings.Get("audition_device_name", "").Length > 0;
        Inspector.CueAvailable = Mixer.CueAvailable;

        Mixer.SetMasterFader(_settings.Get("master_volume", 100));
        Mixer.SetAuditionFader(_settings.Get("audition_volume", 100));
        UpdateMixerDeviceInfo();

        // Timeline del show, estilo Watchout (siempre presente, plegable)
        Timeline.SetModel(_model);
        Timeline.CommitField = (cueId, key, value) =>
        {
            int row = _model.IndexOfId(cueId);
            if (row >= 0)
                _controller.OnCueEditCommitted(row,
                    new Dictionary<string, object?> { [key] = value });
        };
        Timeline.MoveCueRequested = (from, to) => _controller.OnMoveCue(from, to);
        Timeline.CueSelected += cueId =>
        {
            int row = _model.IndexOfId(cueId);
            if (row >= 0 && CueGrid.SelectedIndex != row)
                CueGrid.SelectedIndex = row;
        };
        Timeline.CueActivated += cueId =>
        {
            int row = _model.IndexOfId(cueId);
            if (row < 0) return;
            CueGrid.SelectedIndex = row;
            BottomTabs.SelectedIndex = 1;   // pestaña del inspector
        };
        // Cursor de edición del timeline → scrub en el monitor PVW
        Timeline.EditScrubbed += (videoCue, mediaSeconds) =>
        {
            if (videoCue is { FilePath.Length: > 0 })
                _pvw.ShowPaused(videoCue.FilePath, mediaSeconds);
        };
        Timeline.TriggerRequested = cueId => _controller.TriggerCueId(cueId);
        Timeline.PlayFromRequested = items => _controller.PlayFromTimeline(items);
        Timeline.SetAutoCrossfade(_settings.Get("timeline_auto_crossfade", true));
        Timeline.AutoCrossfadeToggled = on =>
        {
            _settings.Set("timeline_auto_crossfade", on);
            FlashStatus(on
                ? Loc.T("Crossfade automático al solapar: ACTIVADO.", "Automatic crossfade on overlap: ON.")
                : Loc.T("Crossfade automático: DESACTIVADO — el timeline no tocará los fades.", "Automatic crossfade: OFF — the timeline won't touch fades."));
        };
        Timeline.StopRequested = cueId =>
        {
            // Un grupo no "suena" él mismo: detener sus descendientes activos
            foreach (int id in _controller.RunningCueIds.ToList())
                if (id == cueId ||
                    (_model.GetById(id) is Cue c && _model.IsDescendant(c, cueId)))
                    _controller.StopCueById(id);
        };
        Timeline.IsEditAllowed = () => ModeToggle.IsChecked != true;

        // Monitores en vivo: PGM arriba (con VU estéreo del PROGRAMA) y
        // PVW abajo (con VU estéreo de la AUDICIÓN local)
        MonitorHost.Children.Add(MonitorRow(_pgm, _pgmVuL, _pgmVuR,
            Loc.T("Nivel L/R del PROGRAMA. Por loopback mide TODO lo que sale del dispositivo (cues, audio de los vídeos —incluidos los que van con AUDIO ESPEJO por el rack—, sistema); con ASIO o si el loopback no está disponible, mide la mezcla interna de cues. El strip MASTER del panel Audio dice cuál de las dos.",
                  "L/R level of PROGRAM. Via loopback it measures EVERYTHING leaving the device (cues, video audio —including videos whose MIRRORED AUDIO goes through the rack—, system); with ASIO or if loopback isn't available, it measures the internal cue mix. The MASTER strip in the Audio panel says which one.")));
        // El PVW lleva transporte: es el monitor de PREPARACIÓN, y prepararse
        // es poder recorrer el cue antes de lanzarlo. El PGM no, porque
        // refleja lo que va a la sala.
        _pvwBar = new MonitorTransportBar(_pvw);
        MonitorHost.Children.Add(MonitorRow(_pvw, _pvwVuL, _pvwVuR,
            Loc.T($"Nivel L/R de la AUDICIÓN local ({Icons.Headphones})",
                  $"L/R level of the local AUDITION ({Icons.Headphones})"),
            _pvwBar));
        _pvwBar.Start();
        _pvw.Clear("—");
        _pgm.Clear(Loc.T("NEGRO", "BLACK"));
        _pgmTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _pgmTimer.Tick += (_, _) => UpdateProgramMonitor();
        _pgmTimer.Start();

        ApplyViewSettings();
        ReloadTable();

        // Texto inicial localizado de los botones de cabecera (la XAML trae
        // valores estáticos; el resto de su estado ya se refresca por evento)
        ModeToggle.Content = Loc.T("Modo: EDIT", "Mode: EDIT");
        LockModeToggleWidth();
        LockPauseButtonWidth();
        UpdateVideoOutButton();

        // Volumen master desde settings (el fader vive en ⚙ → Audio)
        _controller.OnMasterVolume(_settings.Get("master_volume", 100));

        // Reloj del show (medio segundo de resolución visual)
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clockTimer.Tick += (_, _) => UpdateShowClock();
        _clockTimer.Start();
        UpdateShowClock();
    }

    /// <summary>Aplica los settings de vista (columna de latencia, etiqueta
    /// del siguiente cue). Se llama al arrancar y al guardar settings.</summary>
    private void ApplyViewSettings()
    {
        LatColumn.Visibility = _settings.Get("show_latency_column", true)
            ? Visibility.Visible : Visibility.Collapsed;
        NextCueLabel.Visibility = _settings.Get("show_next_cue_label", true)
            ? Visibility.Visible : Visibility.Collapsed;

        // Escala de fuente para FOH a oscuras (Settings → Interfaz)
        double scale = Math.Clamp(_settings.Get("ui_font_scale", 100), 80, 200) / 100.0;
        CueGrid.FontSize = 13.0 * scale;
        CueGrid.RowHeight = 30.0 * scale;
        ActiveList.FontSize = 12.0 * scale;
        NextCueLabel.FontSize = 14.0 * scale;
    }

    private void UpdateNextCueLabel()
    {
        var cue = _model.GetCue(_playheadIndex);
        NextCueLabel.Text = cue is null
            ? Loc.T("— fin de la lista —", "— end of list —")
            : $"{cue.DisplayNumber} · {cue.Name}";

        // Notas del cue en standby (el operador las lee sin abrir el inspector)
        if (cue is { Notes.Length: > 0 })
        {
            NotesText.Text = cue.Notes;
            NotesPanel.Visibility = Visibility.Visible;
        }
        else
        {
            NotesPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void OnFadeAllClick(object sender, RoutedEventArgs e)
    {
        // El mismo tiempo que va a usar el controlador: el parpadeo dura lo que
        // dura el fundido, ni un tic más.
        _fadeAllSeconds = Math.Max(0.1, _settings.Get("fade_all_seconds", 5.0));
        _fadeAllT0Ticks = System.Diagnostics.Stopwatch.GetTimestamp();
        _controller.OnFadeAll();
        RefreshTransportLights();
    }

    /// <summary>GO con confirmación opcional al disparar el ÚLTIMO cue.</summary>
    private void DoGo()
    {
        if (_settings.Get("confirm_last_cue", true) &&
            _model.Count > 0 && _controller.Playhead == _model.Count - 1)
        {
            var cue = _model.GetCue(_controller.Playhead);
            if (MessageBox.Show(this,
                    Loc.T($"Es el ÚLTIMO cue de la lista:\n\n{cue?.DisplayNumber} · {cue?.Name}\n\n¿Disparar?",
                          $"This is the LAST cue in the list:\n\n{cue?.DisplayNumber} · {cue?.Name}\n\nTrigger it?"),
                    Loc.T("GO — último cue", "GO — last cue"), MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes)
                return;
        }
        _controller.OnGoPressed();
    }

    private void UpdateProgramMonitor()
    {
        var info = _controller.GetProgramVideo();
        if (info is null)
        {
            if (_pgm.CurrentPath.Length > 0 || _pgm.IsMirroring)
                _pgm.Clear(Loc.T("NEGRO", "BLACK"));
            return;
        }
        // PGM = lo que SALE. Con el compositor GPU la salida ya está en una
        // superficie D3D del árbol WPF: el monitor la ESPEJA (pila entera de
        // pistas, opacidades y blackout incluidos) y no decodifica nada. Sin
        // compositor (ruta clásica HWND) WPF no puede ver el vídeo (airspace)
        // → el monitor reproduce el archivo por su cuenta, como siempre.
        int screen = Math.Clamp(info.Value.Screen, 0, _videoOutputs.Length - 1);
        var win = _videoOutputs[screen];
        if (win is { IsVisible: true } && win.HasCompositorSurface(info.Value.CueId))
        {
            _pgm.ShowMirror(win.MirrorVisual,
                System.IO.Path.GetFileName(info.Value.FilePath));
            return;
        }
        // Mismo archivo → solo re-sincroniza; archivo nuevo → carga y arranca ahí
        _pgm.ShowVideo(info.Value.FilePath, info.Value.Elapsed, loop: false);
    }

    /// <summary>PVW: el cue en standby reproduciéndose en bucle (mudo).</summary>
    private void UpdatePreviewMonitor()
    {
        var cue = _model.GetCue(_playheadIndex);
        if (cue is null || cue.FilePath.Length == 0 || cue.Type != CueType.Video)
        {
            if (cue is { Type: CueType.Audio, FilePath.Length: > 0 })
                _pvw.Clear($"{Icons.Audio} {Loc.T("AUDIO (onda en el inspector)", "AUDIO (waveform in the inspector)")}");
            else if (_pvw.CurrentPath.Length > 0 || cue is null)
                _pvw.Clear("—");
            return;
        }
        if (cue.FilePath != _pvw.CurrentPath)
            _pvw.ShowVideo(cue.FilePath, cue.TrimStart, loop: true);
    }

    private void UpdateInspector()
    {
        int row = CueGrid.SelectedIndex;
        Inspector.SetCue(row >= 0 ? _model.GetCue(row) : null, () => _model.Cues);
    }

    /// <summary>Correlación tabla → timeline: resaltar el cue seleccionado.</summary>
    private void UpdateTimelinePanel()
    {
        if (_model is null) return;
        var cue = CueGrid.SelectedIndex >= 0 ? _model.GetCue(CueGrid.SelectedIndex) : null;
        Timeline.SetSelected(cue?.Id ?? -1);
    }

    private void LoadKeyBindings()
    {
        _keyGo = ParseKey(_settings.Get("keyboard_go", "Space"), Key.Space);
        _keyStop = ParseKey(_settings.Get("keyboard_stop", "S"), Key.S);
        _keyPrev = ParseKey(_settings.Get("keyboard_prev", "Left"), Key.Left);
    }

    private static Key ParseKey(string name, Key fallback)
    {
        // Aceptar nombres Qt heredados del settings.json de Python
        name = name switch
        {
            "Esc" => "Escape",
            "Return" => "Enter",
            _ => name,
        };
        return Enum.TryParse<Key>(name, ignoreCase: true, out var k) ? k : fallback;
    }

    // ==================================================================
    // IShowView
    // ==================================================================

    // Historial de mensajes de estado (panel de avisos del botón 🕮).
    // El estado "hay avisos" lo lleva el color del propio LogButton.
    private readonly List<string> _statusHistory = new();

    public void FlashStatus(string message)
    {
        StatusLabel.Text = message;
        _statusTimer.Stop();
        _statusTimer.Start();

        // Los avisos pasan fugaces por la barra: quedan en el historial
        _statusHistory.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        if (_statusHistory.Count > 300)
            _statusHistory.RemoveAt(0);
        if (message.Contains("[AVISO]") || message.Contains(Icons.Warning)
            || message.Contains("Error", StringComparison.OrdinalIgnoreCase)
            || message.Contains("falló", StringComparison.OrdinalIgnoreCase))
        {
            LogButton.Foreground = Theme.StandbyBrush;
        }
    }

    private void OnShowStatusLog(object sender, RoutedEventArgs e)
    {
        LogButton.Foreground = Theme.Solid(Theme.TextMutedAlt);

        var text = new TextBox
        {
            Text = _statusHistory.Count > 0
                ? string.Join(Environment.NewLine, Enumerable.Reverse(_statusHistory))
                : Loc.T("(sin mensajes todavía)", "(no messages yet)"),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Theme.WindowBgBrush,
            Foreground = Theme.TextPrimaryBrush,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = LogButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Width = 560, Height = 320,
                Background = Theme.WindowBgBrush,
                BorderBrush = Theme.BorderBrush,
                BorderThickness = new Thickness(1),
                Child = text,
            },
        };
        popup.IsOpen = true;
    }

    public void ShowError(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void SetPlayhead(int index)
    {
        _playheadIndex = index;
        for (int i = 0; i < _rows.Count; i++)
            _rows[i].IsPlayhead = i == index;
        if (index >= 0 && index < _rows.Count)
        {
            _suppressSelection = true;
            CueGrid.SelectedIndex = index;
            CueGrid.ScrollIntoView(_rows[index]);
            _suppressSelection = false;
        }
        if (_controller is not null)
        {
            UpdatePreviewMonitor();
            UpdateNextCueLabel();
        }
    }

    public void RefreshRow(int index)
    {
        if (index >= 0 && index < _rows.Count)
            _rows[index].RefreshAll();
        // La edición pudo cambiar tiempos/capas/modos → reproyectar el timeline
        Timeline.RefreshFromModel();
    }

    public void ReloadTable()
    {
        _rows.Clear();
        foreach (var cue in _model.Cues)
            _rows.Add(new CueRowViewModel(cue, _model));
        SetPlayhead(_playheadIndex);
        UpdateInspector();   // la lista cambió: reapuntar al cue seleccionado
        Timeline.RefreshFromModel();
        UpdateTimelinePanel();
        RebuildHotkeys();
        UpdateRelocateButton();
    }

    /// <summary>El botón "Relocalizar" solo aparece con medios faltantes.</summary>
    private void UpdateRelocateButton()
    {
        int missing = _controller?.MissingMediaCount() ?? 0;
        RelocateButton.Visibility = missing > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (missing > 0)
            RelocateButton.Content = $"{Icons.Relocate} Relocalizar ({missing})";
    }

    private void OnRelocateMedia(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Carpeta donde buscar los medios del show",
        };
        if (dlg.ShowDialog(this) != true)
            return;
        _controller.RelocateMedia(dlg.FolderName);
        UpdateRelocateButton();
    }

    public void AppendCueRow(Cue cue) => _rows.Add(new CueRowViewModel(cue, _model));

    public void ApplyGroupVisibility()
    {
        foreach (var r in _rows)
            r.RefreshAll();
    }

    /// <summary>Reconstruye el mapa tecla → cue (Triggers → Hotkey).</summary>
    public void RebuildHotkeys()
    {
        _cueHotkeys.Clear();
        foreach (var cue in _model.Cues)
        {
            if (cue.Hotkey.Length == 0)
                continue;
            var key = ParseKey(cue.Hotkey, Key.None);
            if (key != Key.None)
                _cueHotkeys[key] = cue.Id;
        }
    }

    public void UpdateTransport(bool paused)
    {
        // Estaba escrito a mano en español —"REANUDAR"/"PAUSA"— y se colaba en
        // la interfaz en inglés. Va por Loc como todo lo demás.
        PauseButton.Content = paused
            ? $"{Icons.Play} {Loc.T("REANUDAR", "RESUME")}"
            : $"{Icons.Pause} {Loc.T("PAUSA", "PAUSE")}";
        RefreshTransportLights();
    }

    /// <summary>Reserva en PAUSA el ancho del más largo de sus dos textos, por
    /// el mismo motivo que en el botón de modo: al cambiar a REANUDAR el botón
    /// crecía y empujaba a los de al lado. Una barra de transporte cuyos botones
    /// se mueven obliga a mirar dónde está cada uno antes de pulsar.</summary>
    private void LockPauseButtonWidth()
    {
        var typeface = new Typeface(PauseButton.FontFamily, PauseButton.FontStyle,
                                    PauseButton.FontWeight, PauseButton.FontStretch);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        double Measure(string s) => new FormattedText(
            s, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, PauseButton.FontSize,
            Brushes.Black, pixelsPerDip).Width;

        double widest = Math.Max(
            Measure($"{Icons.Pause} {Loc.T("PAUSA", "PAUSE")}"),
            Measure($"{Icons.Play} {Loc.T("REANUDAR", "RESUME")}"));

        PauseButton.MinWidth = Math.Ceiling(widest)
                             + PauseButton.Padding.Left + PauseButton.Padding.Right + 2;
    }

    /// <summary>Enciende en COLOR VIVO el botón cuya función está en efecto
    /// ahora mismo, para leer de un vistazo qué está actuando.
    ///
    /// Solo se encienden los tres que tienen ESTADO. STOP ALL y FADE ALL son
    /// acciones instantáneas: no hay nada que siga puesto después de pulsarlas,
    /// y encenderlas un instante sería un parpadeo — justo el tipo de
    /// realimentación animada que estorba en directo.
    ///
    /// Los colores vivos NO son nuevos: son los mismos tokens que ya usa el
    /// resto de la consola para «sonando» (Go), «atención» (Standby) y «en
    /// aire» (Pgm), así que el vocabulario de color sigue siendo uno.</summary>
    private bool _stopAllLatched;    // STOP ALL pulsado y aún sin reanudar
    private bool _panicLatched;      // PANIC pulsado y aún sin reanudar
    private bool _blinkOn;           // fase del parpadeo
    private DispatcherTimer? _blinkTimer;

    // FADE ALL en curso, por RELOJ. El primer intento lo daba por terminado
    // «cuando ya no quedara nada sonando», y eso nunca llegaba a verse: el
    // controlador retira los cues de la lista de activos EN EL ACTO y deja que
    // el fundido lo terminen los motores, así que el parpadeo se apagaba en la
    // misma pulsación que lo encendía. Lo que dura el fundido lo dice el ajuste.
    private long _fadeAllT0Ticks;    // 0 = no hay fundido en curso
    private double _fadeAllSeconds;

    private bool FadeAllRunning =>
        _fadeAllT0Ticks != 0 &&
        System.Diagnostics.Stopwatch.GetElapsedTime(_fadeAllT0Ticks).TotalSeconds
            < _fadeAllSeconds;

    private void RefreshTransportLights()
    {
        bool running = _controller is not null && _controller.RunningCueIds.Count > 0;
        bool paused = _controller is not null && _controller.IsPaused;
        bool fading = FadeAllRunning;

        // Que algo vuelva a sonar significa que el show ha seguido: es la señal
        // para apagar los enclavados. Sin esto, STOP ALL y PANIC se quedarían
        // encendidos para siempre y dejarían de querer decir nada.
        if (running)
        {
            _stopAllLatched = false;
            _panicLatched = false;
        }

        // GO: verde claro mientras hay algo sonando; verde apagado en reposo.
        GoButton.Background = Theme.Solid(running ? Theme.Go : Theme.GoButton);

        // PAUSA: PARPADEA en ámbar mientras la reproducción está detenida. Texto
        // en negro en la fase encendida, porque sobre el ámbar el claro no se lee.
        bool pauseLit = paused && _blinkOn;
        PauseButton.Background = pauseLit ? Theme.Solid(Theme.Standby) : Theme.ButtonBgBrush;
        PauseButton.Foreground = pauseLit ? Brushes.Black : Theme.TextPrimaryBrush;

        // STOP ALL: se queda MARCADO en rojo tras ejecutarse.
        StopAllButton.Background = _stopAllLatched
            ? Theme.Solid(Theme.Pgm) : Theme.ButtonBgBrush;

        // FADE ALL: PARPADEA mientras el fundido se va yendo.
        bool fadeLit = fading && _blinkOn;
        FadeAllButton.Background = fadeLit ? Theme.Solid(Theme.Warn) : Theme.ButtonBgBrush;

        // PANIC: se queda ENCENDIDO. También con el blackout puesto, que es el
        // caso que más importa: la sala está a negro y desde la mesa no se ve.
        PanicButton.Background = Theme.Solid(_panicLatched || _blackout
            ? Theme.Pgm : Theme.PanicButton);

        // El temporizador del parpadeo solo corre cuando hay algo que parpadee:
        // en reposo no tiene sentido repintar la barra dos veces por segundo.
        // Al vencer el plazo del fundido, es el propio tic del parpadeo quien
        // se encuentra con FadeAllRunning en false, repinta y se para solo.
        bool needsBlink = paused || fading;
        if (needsBlink && _blinkTimer is null)
        {
            _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _blinkTimer.Tick += (_, _) =>
            {
                _blinkOn = !_blinkOn;
                RefreshTransportLights();
            };
            _blinkTimer.Start();
        }
        else if (!needsBlink && _blinkTimer is not null)
        {
            _blinkTimer.Stop();
            _blinkTimer = null;
            _blinkOn = false;
        }
    }

    public void UpdateCountdown(double? secondsRemaining)
    {
        // El countdown por cue se muestra en las tarjetas de EN REPRODUCCIÓN
        // y en el progreso de la fila; el centro es solo del reloj del show.
    }

    public void UpdateActiveCues(IReadOnlyList<ActiveCueInfo> rows)
    {
        // Reconciliar sin recrear (evita parpadeo del panel)
        while (_activeRows.Count > rows.Count)
            _activeRows.RemoveAt(_activeRows.Count - 1);
        for (int i = 0; i < rows.Count; i++)
        {
            if (i < _activeRows.Count)
                _activeRows[i].Update(rows[i]);
            else
                _activeRows.Add(new ActiveRowVM(rows[i]));
        }

        // Progreso + cuenta atrás embebidos en las filas de la tabla (estilo QLab)
        var live = new Dictionary<int, ActiveCueInfo>();
        foreach (var r in rows)
            live[r.Id] = r;
        foreach (var vm in _rows)
        {
            if (live.TryGetValue(vm.CueId, out var info))
                vm.SetLive(info.Remaining, info.PreWaitRemaining);
            else
                vm.SetLive(null, null);
        }

        // Cursor y progreso del timeline (posición real de los motores)
        Timeline.SetActive(rows);

        // Strips del panel Audio (misma cadencia; reconcilia sin recrear)
        // Se le pasa la LISTA ENTERA: la mesa enseña todos los cues que pueden
        // sonar, plegados, y abre el que suena.
        Mixer.SyncActive(rows, cueId =>
        {
            int r = _model.IndexOfId(cueId);
            return r >= 0 ? _model.GetCue(r) : null;
        }, _model.Cues);

        // El fader del inspector y el del strip son EL MISMO fader: mover uno
        // tiene que mover el otro EN VIVO. El strip ya se refrescaba del modelo
        // en cada vuelta; el inspector solo se enteraba al reseleccionar el cue,
        // así que el mismo volumen se veía en dos sitios con dos valores.
        if (CueGrid.SelectedIndex >= 0)
            Inspector.SyncVolumeFromModel(_model.GetCue(CueGrid.SelectedIndex));
    }

    /// <summary>Monitor + VU estéreo pegado a su derecha (misma altura).</summary>
    /// <param name="transport">Barra de reproducir/pausar y salto, DEBAJO del
    /// monitor. Solo la lleva el PVW: el PGM refleja lo que va a la sala y ahí
    /// un botón de pausa sería un botón para parar la función desde un sitio
    /// que no es el transporte.</param>
    private static DockPanel MonitorRow(LiveMonitorControl monitor,
        VuMeterControl left, VuMeterControl right, string tooltip,
        MonitorTransportBar? transport = null)
    {
        var vu = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(3, 0, 0, 0),
            ToolTip = tooltip,
            Background = System.Windows.Media.Brushes.Transparent,   // hover activo
        };
        left.Height = double.NaN;    // estirar a la altura del monitor
        right.Height = double.NaN;
        right.Margin = new Thickness(1, 0, 0, 0);
        vu.Children.Add(left);
        vu.Children.Add(right);

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(vu, Dock.Right);
        row.Children.Add(vu);
        if (transport is not null)
        {
            // La barra va abajo del TODO, por debajo del VU: si se metiera
            // dentro del bloque monitor+VU, el VU se estiraría también sobre
            // ella y dejaría de medir el alto del vídeo.
            DockPanel.SetDock(transport, Dock.Bottom);
            var stack = new DockPanel();
            stack.Children.Add(row);
            DockPanel.SetDock(row, Dock.Top);
            stack.Children.Add(transport);
            var outer = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            outer.Children.Add(stack);
            row.Margin = new Thickness(0);
            return outer;
        }
        return row;
    }

    public void UpdateVuMeter(float left, float right)
    {
        _pgmVuL.SetLevel(left);
        _pgmVuR.SetLevel(right);
        Mixer.SetMasterLevel(left, right);
    }

    public void UpdateCueVuMeter(int cueId, float left, float right)
    {
        if (cueId == MainController.AuditionCueId)
        {
            // La audición local es el "preview" de audio → VU del PVW.
            // Con el loopback de audición activo, ese VU lo alimenta el
            // DISPOSITIVO (UpdateAuditionVu): ignorar el VU de stream.
            if (_auditionVuFromLoopback)
                return;
            _pvwVuL.SetLevel(left);
            _pvwVuR.SetLevel(right);
            Mixer.SetAuditionLevel(left, right);
            return;
        }
        Inspector.UpdateCueLevel(cueId, left, right);   // el inspector ya es estéreo
        Mixer.UpdateCueLevel(cueId, left, right);
    }

    // ── VU de audición por loopback (dispositivo de audición propio) ───

    private bool _auditionVuFromLoopback;

    /// <summary>true → el VU del PVW/panel lo alimenta el loopback del
    /// dispositivo de audición (UpdateAuditionVu) y no el stream.</summary>
    public void SetAuditionVuLoopback(bool active) => _auditionVuFromLoopback = active;

    /// <summary>RMS del dispositivo de AUDICIÓN (loopback). Lo cablea App.</summary>
    public void UpdateAuditionVu(float left, float right)
    {
        _pvwVuL.SetLevel(left);
        _pvwVuR.SetLevel(right);
        Mixer.SetAuditionLevel(left, right);
    }

    // Origen del VU de PGM y salida realmente abierta (los empuja App desde
    // los eventos del motor: los medidores se abren fuera del hilo de UI).
    private string _vuSourceInfo = "";
    private string _realOutputInfo = "";

    /// <summary>De dónde sale el VU de PROGRAMA y qué salida se abrió de
    /// verdad. Lo cablea App a los eventos del AudioEngine.</summary>
    public void SetAudioSourceInfo(string vuSource, string realOutput)
    {
        _vuSourceInfo = vuSource;
        _realOutputInfo = realOutput;
        UpdateMixerDeviceInfo();
    }

    /// <summary>Línea de dispositivo/driver del strip MASTER del panel Audio.</summary>
    private void UpdateMixerDeviceInfo()
    {
        string driver = _settings.Get("audio_driver", "wasapi").ToUpperInvariant();
        string dev = _settings.Get("audio_device_name", "");
        if (dev.Length == 0)
            dev = Loc.T("Dispositivo por defecto", "Default device");
        int sr = _settings.Get("audio_sample_rate", 48000);
        string line = $"{driver} · {dev} · {sr / 1000.0:0.#} kHz";
        if (_realOutputInfo.Length > 0)
            line += $"\n→ {_realOutputInfo}";
        if (_vuSourceInfo.Length > 0)
            line += $"\n{_vuSourceInfo}";
        Mixer.SetDeviceInfo(line);
    }

    public nint GetVideoOutputHandle()
    {
        bool firstOpen = _videoOutputs[0] is null || !_videoOutputs[0]!.IsVisible;
        EnsureVideoOutputWindow(0);
        nint handle = _videoOutputs[0]!.EnsureVisibleHandle();
        if (firstOpen)
            PlaceVideoOutput(0);
        return handle;
    }

    public nint GetVideoOutputLayerHandle(int screen, int layer)
    {
        screen = Math.Clamp(screen, 0, _videoOutputs.Length - 1);
        bool firstOpen = _videoOutputs[screen] is null || !_videoOutputs[screen]!.IsVisible;
        EnsureVideoOutputWindow(screen);
        nint handle = _videoOutputs[screen]!.EnsureLayerHandle(layer);
        if (firstOpen)
            PlaceVideoOutput(screen);
        return handle;
    }

    public void SetActiveVideoLayers(int screen, IReadOnlyCollection<int> activeLayers)
    {
        // Solo sobre ventanas YA creadas: el tally barre las 4 salidas y no
        // debe materializar ventanas que ningún cue usa.
        if (screen >= 0 && screen < _videoOutputs.Length)
            _videoOutputs[screen]?.SetActiveLayers(activeLayers);
    }

    public void SetVideoCueOpacity(int cueId, double opacity, double fadeSeconds = 0.0)
    {
        // El cue puede verse en varias salidas (modo canvas): cada ventana
        // funde su superficie si la tiene; sin ella, no-op.
        foreach (var w in _videoOutputs)
            w?.SetCueOpacity(cueId, opacity, fadeSeconds);
    }

    public void RaiseVideoCueToFront(int cueId)
    {
        foreach (var w in _videoOutputs)
            w?.RaiseCueToFront(cueId);
    }

    /// <summary>Superficie PERSISTENTE del compositor para un slot del pool
    /// (la usa App para el provider del motor; auditoría 05).</summary>
    public CompositorLayerControl GetCompositorSurface(int screen, int layer, int slotSeq)
    {
        screen = Math.Clamp(screen, 0, _videoOutputs.Length - 1);
        bool firstOpen = _videoOutputs[screen] is null || !_videoOutputs[screen]!.IsVisible;
        EnsureVideoOutputWindow(screen);
        var surface = _videoOutputs[screen]!.GetCompositorSurface(layer, slotSeq);
        if (firstOpen)
            PlaceVideoOutput(screen);
        return surface;
    }

    private void EnsureVideoOutputWindow(int screen)
    {
        if (_videoOutputs[screen] is not null)
            return;
        var w = new VideoOutputWindow { Owner = this };
        w.Title = Loc.T($"WinCue — Salida de vídeo {screen + 1}", $"WinCue — Video output {screen + 1}");
        // El botón refleja si alguna salida está activa (verde) o no (rojo);
        // al ocultarse se recuerda su geometría para la próxima sesión.
        w.IsVisibleChanged += (_, _) =>
        {
            UpdateVideoOutButton();
            if (!w.IsVisible)
                SaveOutputGeometry(screen);
        };
        _videoOutputs[screen] = w;
    }

    /// <summary>Clave de settings del monitor de una salida (la 1 conserva la
    /// clave histórica para no romper settings existentes).</summary>
    private static string ScreenSettingKey(int screen) =>
        screen == 0 ? "video_output_screen" : $"video_output_screen{screen + 1}";

    /// <summary>Coloca una salida en su pantalla configurada (Settings → Vídeo),
    /// restaurando la geometría recordada en modo ventana.</summary>
    private void PlaceVideoOutput(int screen)
    {
        // Monitor configurado; −1 (deshabilitada) o índice fuera de rango →
        // principal, para no reventar si un cue apunta a una salida sin monitor.
        int mon = _settings.Get(ScreenSettingKey(screen), 0);
        if (mon < 0 || mon >= System.Windows.Forms.Screen.AllScreens.Length)
            mon = 0;
        _videoOutputs[screen]?.PlaceOnScreen(
            mon,
            _settings.Get("video_fullscreen_output", false),
            _settings.Get($"video_output_geom{screen + 1}", ""));
        // Una salida que aparece durante un blackout nace fundida a negro
        if (_blackout)
            _videoOutputs[screen]?.SetBlackout(true, 0.0);
    }

    private void SaveOutputGeometry(int screen)
    {
        if (_videoOutputs[screen]?.CurrentGeometry() is string geom)
            _settings.Set($"video_output_geom{screen + 1}", geom);
    }

    public bool IsVideoOutputVisible(int screen) =>
        screen >= 0 && screen < _videoOutputs.Length
        && _videoOutputs[screen] is { IsVisible: true };

    /// <summary>Abre (o trae) una salida de vídeo desde código — lo usa el
    /// modo estrés desatendido, que necesita la salida activa sin operador.</summary>
    public void ShowVideoOutput(int screen)
    {
        screen = Math.Clamp(screen, 0, _videoOutputs.Length - 1);
        EnsureVideoOutputWindow(screen);
        _videoOutputs[screen]!.Show();
        PlaceVideoOutput(screen);
    }

    public (double Width, double Height) SurfaceDimensions() => SurfaceDimensions(0);

    public (double Width, double Height) SurfaceDimensions(int screen)
    {
        if (screen >= 0 && screen < _videoOutputs.Length
            && _videoOutputs[screen] is { IsVisible: true } w)
            return (w.ActualWidth, w.ActualHeight);
        return (1920.0, 1080.0);
    }

    public void SetOutputContent(string? videoName) =>
        TallyLabel.Text = videoName is null
            ? Loc.T("SALIDA: NEGRO", "OUTPUT: BLACK")
            : Loc.T($"SALIDA: {Icons.Video} {videoName}", $"OUTPUT: {Icons.Video} {videoName}");

    public void SetOutputDetails(string details)
    {
        TallyLabel.ToolTip = details.Length > 0 ? details : null;
        VideoOutButton.ToolTip = details.Length > 0 ? details : null;
    }

    // ==================================================================
    // Transport / toolbar
    // ==================================================================

    private void OnGoClick(object sender, RoutedEventArgs e) => DoGo();

    private void OnActiveStopClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int cueId })
            _controller.StopCueById(cueId);
    }
    private void OnStopAllClick(object sender, RoutedEventArgs e)
    {
        _stopAllLatched = true;
        _fadeAllT0Ticks = 0;   // un STOP corta el fundido en curso
        _controller.OnStopPressed();
        RefreshTransportLights();
    }

    private void OnPanicClick(object sender, RoutedEventArgs e)
    {
        _panicLatched = true;
        _controller.OnPanic();
        RefreshTransportLights();
    }
    private void OnPauseClick(object sender, RoutedEventArgs e) => _controller.OnPauseToggle();

    // ── Imagen de respaldo del PANIC (IShowView) ───────────────────────

    /// <summary>Muestra la cartela de respaldo en las salidas VISIBLES (la
    /// llama el controlador en el PANIC con panic_action = "image").</summary>
    public void ShowBackupSlate(double fadeSeconds)
    {
        string path = _settings.Get("panic_image_path", "");
        if (path.Length == 0 || !System.IO.File.Exists(path))
        {
            FlashStatus(Loc.T(
                "PANIC: no hay imagen de respaldo válida (clic derecho en PANIC → Elegir imagen).",
                "PANIC: no valid backup image (right-click PANIC → Choose image)."));
            return;
        }
        foreach (var w in _videoOutputs)
            if (w is { IsVisible: true })
                w.ShowBackupSlate(path, fadeSeconds);
    }

    /// <summary>Quita la cartela de todas las salidas.</summary>
    public void HideBackupSlate()
    {
        foreach (var w in _videoOutputs)
            w?.HideBackupSlate();
    }

    /// <summary>Clic derecho en PANIC: opciones de la imagen de respaldo (la
    /// configuración completa vive en Settings → GO).</summary>
    private void OnPanicRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        bool imageOn = _settings.Get("panic_action", "black") == "image";
        var menu = new ContextMenu();
        var toggle = new MenuItem
        {
            Header = Loc.T("Imagen de respaldo al hacer PANIC", "Backup image on PANIC"),
            IsCheckable = true,
            IsChecked = imageOn,
        };
        toggle.Click += (_, _) =>
        {
            _settings.Set("panic_action", imageOn ? "black" : "image");
            FlashStatus(imageOn
                ? Loc.T("PANIC → negro.", "PANIC → black.")
                : Loc.T("PANIC → imagen de respaldo.", "PANIC → backup image."));
        };
        menu.Items.Add(toggle);
        menu.Items.Add(Mi(Loc.T("Elegir imagen…", "Choose image…"), "", () =>
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Imagen|*.jpg;*.jpeg;*.png;*.bmp;*.webp|Todos|*.*",
            };
            if (dlg.ShowDialog(this) == true)
            {
                _settings.SetMany(new Dictionary<string, object?>
                {
                    ["panic_image_path"] = dlg.FileName,
                    ["panic_action"] = "image",
                });
                FlashStatus(Loc.T(
                    $"Imagen de respaldo: {System.IO.Path.GetFileName(dlg.FileName)} (activada).",
                    $"Backup image: {System.IO.Path.GetFileName(dlg.FileName)} (enabled)."));
            }
        }));
        menu.Items.Add(Mi(Loc.T("Quitar imagen de las salidas", "Remove image from outputs"), "",
            HideBackupSlate));
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Clic derecho en FADE ALL: presets de duración del fundido.</summary>
    private void OnFadeAllRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        double current = _settings.Get("fade_all_seconds", 5.0);
        var menu = new ContextMenu();
        foreach (double s in new[] { 3.0, 5.0, 10.0, 20.0 })
        {
            var item = new MenuItem
            {
                Header = Loc.T($"Fundido de {s:0.#} s", $"{s:0.#} s fade"),
                IsCheckable = true,
                IsChecked = Math.Abs(current - s) < 0.01,
            };
            double v = s;
            item.Click += (_, _) =>
            {
                _settings.Set("fade_all_seconds", v);
                FlashStatus(Loc.T($"FADE ALL: {v:0.#} s.", $"FADE ALL: {v:0.#} s."));
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ── Reloj del show (restante ↔ transcurrido, clic para alternar) ────
    private bool _clockCountdown = true;
    private long? _showStartTicks;
    private DispatcherTimer? _clockTimer;

    private void OnShowClockClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _showStartTicks = null;   // reiniciar el cronómetro del show
            FlashStatus(Loc.T("Reloj del show reiniciado.", "Show clock reset."));
        }
        else
        {
            _clockCountdown = !_clockCountdown;
        }
        UpdateShowClock();
    }

    private void UpdateShowClock()
    {
        if (_controller is null) return;

        // Aquí, y no solo al pausar: «hay algo sonando» cambia sin avisar
        // (un cue que termina solo), y el GO tiene que apagarse con él.
        RefreshTransportLights();

        // El show "empieza" cuando arranca el primer cue
        if (_showStartTicks is null && _controller.RunningCueIds.Count > 0)
            _showStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        double elapsed = _showStartTicks is long t
            ? System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalSeconds
            : 0.0;

        if (_clockCountdown)
        {
            // Duración estimada del show: suma de pre-wait + duración (armados)
            double total = 0.0;
            foreach (var c in _model.Cues)
                if (c.Armed)
                    total += c.PreWait + Math.Max(0.0, c.Duration);
            double remaining = Math.Max(0.0, total - elapsed);
            ShowClockMode.Text = $"{Loc.T("SHOW · RESTANTE", "SHOW · REMAINING")} {Icons.Expanded}";
            ShowClockText.Text = FormatClock(remaining);
            // En modo SHOW el reloj va en el verde "armado" de la salida
            // (mismo #1e7e34 aclarado para leerse sobre fondo oscuro);
            // agotado sigue en rojo, y en EDIT queda el ámbar de trabajo.
            ShowClockText.Foreground = remaining <= 0.0 && _showStartTicks is not null
                ? Theme.PgmBrush
                : ModeToggle.IsChecked == true
                    ? Theme.GoBrush
                    : Theme.StandbyBrush;
        }
        else
        {
            ShowClockMode.Text = $"{Loc.T("SHOW · TRANSCURRIDO", "SHOW · ELAPSED")} {Icons.Expanded}";
            ShowClockText.Text = FormatClock(elapsed);
            ShowClockText.Foreground = Theme.GoBrush;
        }
    }

    private static string FormatClock(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss");

    /// <summary>Reserva en el botón de modo el ancho del MÁS LARGO de sus dos
    /// textos. Sin esto el botón se encoge al pasar de «SHOW» a «EDIT» (en
    /// fuente proporcional la W es más ancha que la I), y al encogerse arrastra
    /// consigo el reloj del show, que está a su lado. En directo el reloj no se
    /// puede mover de sitio: se busca de reojo, siempre en el mismo punto.
    ///
    /// Se MIDE en vez de fijar un ancho a mano porque el texto es traducible y
    /// la tipografía puede cambiar: un número escrito a pelo aquí volvería a
    /// romperse en cuanto una de las dos cosas se moviera, y sin avisar.</summary>
    private void LockModeToggleWidth()
    {
        var typeface = new Typeface(ModeToggle.FontFamily, ModeToggle.FontStyle,
                                    ModeToggle.FontWeight, ModeToggle.FontStretch);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        double Measure(string s) => new FormattedText(
            s, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, ModeToggle.FontSize,
            Brushes.Black, pixelsPerDip).Width;

        double widest = Math.Max(Measure(Loc.T("Modo: EDIT", "Mode: EDIT")),
                                 Measure(Loc.T("Modo: SHOW", "Mode: SHOW")));

        // + relleno del botón + el borde de 1 px de cada lado
        ModeToggle.MinWidth = Math.Ceiling(widest)
                            + ModeToggle.Padding.Left + ModeToggle.Padding.Right + 2;
    }

    private void OnShowMode(object sender, RoutedEventArgs e)
    {
        ModeToggle.Content = Loc.T("Modo: SHOW", "Mode: SHOW");
        ModeToggle.Background = Theme.Solid(Theme.GoButton);
        ModeToggle.Foreground = Brushes.White;
        SetEditingEnabled(false);
        BottomTabs.SelectedIndex = 0;   // en SHOW, abajo SIEMPRE el timeline
        UpdateShowClock();   // el reloj pasa a verde "armado"
    }

    private bool _modeReverting;

    private void OnEditMode(object sender, RoutedEventArgs e)
    {
        // Con cues sonando, salir de SHOW merece confirmación (un misclick
        // en directo reabriría la edición)
        if (!_modeReverting && _controller is not null &&
            _controller.RunningCueIds.Count > 0 &&
            MessageBox.Show(this,
                Loc.T("Hay cues EN REPRODUCCIÓN.\n\n¿Salir del modo SHOW y volver a EDIT?",
                      "There are cues PLAYING.\n\nExit SHOW mode and return to EDIT?"),
                "WinCue — " + Loc.T("Modo SHOW", "SHOW mode"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            _modeReverting = true;
            ModeToggle.IsChecked = true;   // re-dispara OnShowMode (idempotente)
            _modeReverting = false;
            return;
        }
        ModeToggle.Content = Loc.T("Modo: EDIT", "Mode: EDIT");
        ModeToggle.Background = Theme.GoldBrush;
        ModeToggle.Foreground = Brushes.Black;
        SetEditingEnabled(true);
        UpdateShowClock();   // el reloj vuelve al ámbar de edición
    }

    /// <summary>Botón de salida: rojo sin salida activa, verde con alguna,
    /// negro parpadeante-mental en blackout (que se note desde lejos).</summary>
    private void UpdateVideoOutButton()
    {
        bool active = _videoOutputs.Any(w => w is { IsVisible: true });
        if (_blackout)
        {
            VideoOutButton.Background = Brushes.Black;
            VideoOutButton.Foreground = Brushes.Red;
            VideoOutButton.Content = $"{Icons.BlackFrame} BLACKOUT {Icons.Expanded}";
            return;
        }
        VideoOutButton.Foreground = Brushes.White;
        VideoOutButton.Content =
            $"{Icons.VideoOutput} {Loc.T("Salida vídeo", "Video output")} {Icons.Expanded}";
        VideoOutButton.Background = Theme.Solid(active
            ? Theme.GoButton
            : Theme.PanicButton);
    }

    private void SetEditingEnabled(bool enabled)
    {
        DeleteButton.IsEnabled = enabled;
        // En SHOW el borrado y añadido quedan bloqueados; GO/transport siguen vivos
        foreach (var child in LogicalTreeHelper.GetChildren(this))
            if (child is Border) { /* la toolbar completa se gestiona abajo */ }
        ToolbarSetEnabled(enabled);
    }

    private void ToolbarSetEnabled(bool enabled)
    {
        if (DeleteButton.Parent is StackPanel panel)
            foreach (var child in panel.Children)
                if (child is Button b && !ReferenceEquals(b, GoButton))
                    b.IsEnabled = enabled;
    }

    private bool _blackout;

    /// <summary>Menú de control de salidas (mostrar/ocultar, identificar,
    /// patrones de test y blackout) — control fino sin pasar por Settings.</summary>
    private void OnVideoOutputClick(object sender, RoutedEventArgs e)
    {
        // UX: el menú SE QUEDA ABIERTO al pulsar (StaysOpenOnClick) para
        // encadenar acciones — mostrar, identificar, probar patrones — sin
        // reabrirlo cada vez. Los estados (Mostrar/Ocultar, checks de patrón,
        // blackout) se refrescan EN VIVO. Se cierra con Esc, clic fuera o con
        // «Ocultar todas» (acción terminal).
        var menu = new ContextMenu();

        // Checks de patrón de TODAS las salidas (para que «Quitar todos los
        // patrones» los desmarque en vivo, sin cerrar el menú)
        var patternItems = new List<(int Screen, TestPattern P, MenuItem Item)>();
        void RefreshPatternChecks(int screen)
        {
            foreach (var (sc, p, item) in patternItems)
                if (sc == screen)
                    item.IsChecked = (_videoOutputs[sc]?.Pattern ?? TestPattern.None) == p;
        }

        for (int s = 0; s < _videoOutputs.Length; s++)
        {
            int idx = s;
            // Salida deshabilitada en Settings (monitor −1): no se ofrece en el
            // menú, así no se lanza sin querer sobre el monitor principal.
            if (_settings.Get(ScreenSettingKey(s), s == 0 ? 0 : -1) < 0)
                continue;
            var w = _videoOutputs[s];
            var sub = new MenuItem
            {
                Header = Loc.T($"Salida {s + 1}", $"Output {s + 1}"),
                Foreground = new SolidColorBrush(CanvasEditorControl.OutColors[s]),
                FontWeight = FontWeights.Bold,
                StaysOpenOnClick = true,
            };

            var show = new MenuItem
            {
                Header = w is { IsVisible: true } ? Loc.T("Ocultar", "Hide") : Loc.T("Mostrar", "Show"),
                // Sin Foreground: lo pone el estilo de MenuItem. Fijarlo aquí
                // (era Brushes.Black, de cuando el menú era el claro de WPF)
                // dejaba el texto negro sobre el panel oscuro.
                FontWeight = FontWeights.Normal,
                StaysOpenOnClick = true,
            };
            show.Click += (_, _) =>
            {
                if (_videoOutputs[idx] is { IsVisible: true } vw)
                {
                    vw.Hide();
                }
                else
                {
                    EnsureVideoOutputWindow(idx);
                    _videoOutputs[idx]!.Show();
                    PlaceVideoOutput(idx);
                }
                show.Header = _videoOutputs[idx] is { IsVisible: true }
                    ? Loc.T("Ocultar", "Hide") : Loc.T("Mostrar", "Show");
            };
            sub.Items.Add(show);

            var ident = new MenuItem
            {
                Header = Loc.T("Identificar", "Identify"),
                FontWeight = FontWeights.Normal,   // el color lo pone el estilo
                StaysOpenOnClick = true,
            };
            ident.Click += (_, _) =>
            {
                IdentifyOutput(idx);
                show.Header = Loc.T("Ocultar", "Hide");   // identificar la muestra
            };
            sub.Items.Add(ident);

            sub.Items.Add(new Separator());
            foreach (var (pattern, label) in new[]
            {
                (TestPattern.None, Loc.T("Sin patrón", "No pattern")),
                (TestPattern.Grid, Loc.T("Patrón: rejilla", "Pattern: grid")),
                (TestPattern.Bars, Loc.T("Patrón: barras", "Pattern: bars")),
                (TestPattern.Border, Loc.T("Patrón: borde", "Pattern: border")),
            })
            {
                var item = new MenuItem
                {
                    Header = label,
                    IsCheckable = true,
                    IsChecked = (w?.Pattern ?? TestPattern.None) == pattern,
                    FontWeight = FontWeights.Normal,   // el color lo pone el estilo
                    StaysOpenOnClick = true,
                };
                var p = pattern;
                item.Click += (_, _) =>
                {
                    EnsureVideoOutputWindow(idx);
                    _videoOutputs[idx]!.SetTestPattern(p);
                    PlaceVideoOutput(idx);
                    RefreshPatternChecks(idx);            // radio en vivo
                    show.Header = Loc.T("Ocultar", "Hide");
                };
                patternItems.Add((idx, p, item));
                sub.Items.Add(item);
            }
            menu.Items.Add(sub);
        }

        menu.Items.Add(new Separator());

        var identAll = new MenuItem
        {
            Header = Loc.T("Identificar salidas visibles", "Identify visible outputs"),
            StaysOpenOnClick = true,
        };
        identAll.Click += (_, _) =>
        {
            for (int s = 0; s < _videoOutputs.Length; s++)
                if (_videoOutputs[s] is { IsVisible: true })
                    IdentifyOutput(s);
        };
        menu.Items.Add(identAll);

        var clearPatterns = new MenuItem
        {
            Header = Loc.T("Quitar todos los patrones", "Clear all patterns"),
            StaysOpenOnClick = true,
        };
        clearPatterns.Click += (_, _) =>
        {
            for (int s = 0; s < _videoOutputs.Length; s++)
            {
                _videoOutputs[s]?.SetTestPattern(TestPattern.None);
                RefreshPatternChecks(s);
            }
        };
        menu.Items.Add(clearPatterns);

        var blackout = new MenuItem
        {
            Header = "Blackout (B)",
            IsCheckable = true,
            IsChecked = _blackout,
            StaysOpenOnClick = true,
        };
        blackout.Click += (_, _) =>
        {
            ToggleBlackout();
            blackout.IsChecked = _blackout;
        };
        menu.Items.Add(blackout);

        menu.Items.Add(new Separator());
        var hideAll = new MenuItem { Header = Loc.T("Ocultar todas las salidas", "Hide all outputs") };
        hideAll.Click += (_, _) =>
        {
            foreach (var w in _videoOutputs)
                w?.Hide();
        };
        menu.Items.Add(hideAll);

        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void IdentifyOutput(int screen)
    {
        EnsureVideoOutputWindow(screen);
        _videoOutputs[screen]!.ShowIdentify(screen, CanvasEditorControl.OutColors[screen]);
        PlaceVideoOutput(screen);
    }

    /// <summary>Blackout de operador (tecla B): funde TODAS las salidas a
    /// negro sin parar los cues; otra pulsación lo levanta.</summary>
    public void ToggleBlackout()
    {
        _blackout = !_blackout;
        double fade = Math.Max(0.0, _settings.Get("blackout_fade", 1.0));
        foreach (var w in _videoOutputs)
            if (w is { IsVisible: true })
                w.SetBlackout(_blackout, fade);
        UpdateVideoOutButton();
        RefreshTransportLights();   // PANIC se enciende con el blackout
        FlashStatus(_blackout
            ? Loc.T("BLACKOUT — salidas fundidas a negro (B para levantar).", "BLACKOUT — outputs faded to black (B to release).")
            : Loc.T("Blackout levantado.", "Blackout released."));
    }

    // El "Acerca de" vive ahora en Configuración → ⓘ Acerca de (SettingsWindow)

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings) { Owner = this };
        dlg.Saved += () =>
        {
            LoadKeyBindings();
            ApplyViewSettings();
            _controller.OnMasterVolume(_settings.Get("master_volume", 100));
            _applyEngineSettings?.Invoke();
            // Reflejar en el panel Audio lo guardado en settings
            Mixer.SetMasterFader(_settings.Get("master_volume", 100));
            Mixer.SetAuditionFader(_settings.Get("audition_volume", 100));
            UpdateMixerDeviceInfo();
            for (int s = 0; s < _videoOutputs.Length; s++)
                if (_videoOutputs[s] is { IsVisible: true })
                    PlaceVideoOutput(s);   // recolocar en la pantalla elegida
            FlashStatus(Loc.T("Configuración guardada y aplicada.", "Settings saved and applied."));
        };
        dlg.ShowDialog();
    }

    // ==================================================================
    // Lista
    // ==================================================================

    private void OnAddCue(object sender, RoutedEventArgs e)
    {
        // Sirve desde botón o MenuItem (ambos FrameworkElement con Tag)
        if (sender is FrameworkElement { Tag: string tag } &&
            Enum.TryParse<CueType>(Pascal(tag), out var type))
        {
            _controller.OnInsertCue(type, CueGrid.SelectedIndex);
        }
    }

    /// <summary>Abre el menú desplegable del botón «Añadir» (tipos de cue).</summary>
    private void OnAddMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } b)
        {
            menu.PlacementTarget = b;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private static string Pascal(string screaming) =>
        char.ToUpperInvariant(screaming[0]) + screaming[1..].ToLowerInvariant();

    private void OnDeleteCue(object sender, RoutedEventArgs e)
    {
        int row = CueGrid.SelectedIndex;
        if (row >= 0)
            _controller.OnDeleteCue(row);
    }

    private void OnImportFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Media|*.wav;*.flac;*.aiff;*.aif;*.ogg;*.mp3;*.aac;*.m4a;*.opus;" +
                     "*.mp4;*.mov;*.avi;*.mkv;*.webm;*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|" +
                     "Todos|*.*",
        };
        if (dlg.ShowDialog(this) == true)
            _controller.AddCuesFromFiles(CueGrid.SelectedIndex, dlg.FileNames);
    }

    /// <summary>Fija el show actual (lo usa App al abrir un show por argumento
    /// de línea de comandos). El autosave recuperado NO lo fija: Save no debe
    /// sobrescribir autosave.json.</summary>
    public void SetCurrentShowPath(string path) => _currentShowPath = path;

    private void OnOpenShow(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "WinCue Show (*.json)|*.json|Todos|*.*" };
        if (dlg.ShowDialog(this) == true)
        {
            _controller.LoadShow(dlg.FileName);
            _currentShowPath = dlg.FileName;
            AddRecentShow(dlg.FileName);
        }
    }

    /// <summary>Botón Guardar: guarda sobre el show abierto SIN preguntar. Si no
    /// hay show todavía (nunca se abrió/guardó), cae en «Guardar como…».</summary>
    private void OnSaveShow(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentShowPath))
        {
            OnSaveShowAs(sender, e);
            return;
        }
        _controller.SaveShow(_currentShowPath);
    }

    /// <summary>Guardar como…: pregunta la ruta y pasa a ser el show actual.</summary>
    private void OnSaveShowAs(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "WinCue Show (*.json)|*.json" };
        if (_currentShowPath is not null)
        {
            dlg.FileName = System.IO.Path.GetFileName(_currentShowPath);
            string? d = System.IO.Path.GetDirectoryName(_currentShowPath);
            if (d is not null) dlg.InitialDirectory = d;
        }
        if (dlg.ShowDialog(this) != true)
            return;
        string path = dlg.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? dlg.FileName : dlg.FileName + ".json";
        _controller.SaveShow(path);
        _currentShowPath = path;
        AddRecentShow(path);
    }

    /// <summary>Desplegable junto a «Guardar»: opciones de guardado avanzadas
    /// (exportar portable, y sitio natural para futuras).</summary>
    private void OnSaveMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Mi(Loc.T("Guardar como…", "Save as…"), "",
            () => OnSaveShowAs(sender, e)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Mi($"{Icons.Package} {Loc.T("Exportar show portable…", "Export portable show…")}", "",
            () => OnExportPortable(sender, e)));
        menu.PlacementTarget = (Button)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Exporta un show portable: copia todos los medios a una carpeta
    /// «media» junto al .json elegido, para llevar el show a otro equipo. No
    /// cambia el show de trabajo ni su ruta actual.</summary>
    private void OnExportPortable(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "WinCue Show (*.json)|*.json",
            Title = Loc.T("Exportar show portable (crea carpeta «media»)",
                          "Export portable show (creates «media» folder)"),
        };
        if (dlg.ShowDialog(this) != true)
            return;
        // ¿Convertir el audio comprimido a WAV en la copia? (arranque
        // instantáneo y seek exacto en directo; los WAV/FLAC no se tocan)
        bool convertWav = _model.Cues.Any(c => c.Type == CueType.Audio &&
                MediaImporter.LossyAudioFormats.Contains(
                    System.IO.Path.GetExtension(c.FilePath))) &&
            MessageBox.Show(this,
                Loc.T("Hay cues de audio comprimido (mp3, aac, ogg…).\n\n¿Convertirlos a WAV en la copia exportada?\n(El original no se toca; si ffmpeg no está disponible se copia tal cual.)",
                      "There are compressed audio cues (mp3, aac, ogg…).\n\nConvert them to WAV in the exported copy?\n(The original is untouched; if ffmpeg isn't available they're copied as-is.)"),
                Loc.T("Exportar show portable", "Export portable show"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        // ¿Convertir los vídeos a HAP? (decode baratísimo en directo; ocupan
        // MÁS disco y sus fundidos de opacidad van a corte — ruta clásica)
        bool convertHap = _model.Cues.Any(c => c.Type == CueType.Video &&
                c.FilePath.Length > 0 &&
                !MediaImporter.ImageFormats.Contains(System.IO.Path.GetExtension(c.FilePath)) &&
                !MediaImporter.IsUnsupportedVideoCodec(c.FilePath)) &&
            MessageBox.Show(this,
                Loc.T("¿Convertir los VÍDEOS a HAP en la copia exportada?\n\nHAP descomprime con mínima CPU (arranque instantáneo, scrub exacto), ideal para directo. A cambio ocupa bastante más disco y sus fundidos de opacidad entre capas van a corte.\n(El original no se toca; si ffmpeg falla se copia tal cual. Puede tardar varios minutos.)",
                      "Convert VIDEOS to HAP in the exported copy?\n\nHAP decodes with minimal CPU (instant start, exact scrub), ideal for live use. In exchange it takes much more disk and its per-layer opacity fades become cuts.\n(The original is untouched; if ffmpeg fails it's copied as-is. May take several minutes.)"),
                Loc.T("Exportar show portable", "Export portable show"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        Mouse.OverrideCursor = Cursors.Wait;   // la conversión puede tardar
        (int copied, int missing) result;
        try
        {
            result = _controller.ExportPortableShow(dlg.FileName, convertWav, convertHap);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        var (copied, missing) = result;
        if (missing > 0)
            MessageBox.Show(this,
                Loc.T($"Se copiaron {copied} archivo(s) a «media», pero {missing} no se encontraron en disco (cues con media faltante). Reubica esos medios y vuelve a exportar.",
                      $"Copied {copied} file(s) to «media», but {missing} were not found on disk (cues with missing media). Relocate those media and export again."),
                Loc.T("Exportar show portable", "Export portable show"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ── Shows recientes (botón ▾ junto a Abrir) ────────────────────────

    private void AddRecentShow(string path)
    {
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            path += ".json";
        var recent = _settings.Get("recent_shows", new List<string>());
        recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, path);
        if (recent.Count > 5)
            recent.RemoveRange(5, recent.Count - 5);
        _settings.Set("recent_shows", recent);
    }

    private void OnRecentShows(object sender, RoutedEventArgs e)
    {
        var recent = _settings.Get("recent_shows", new List<string>())
            .Where(System.IO.File.Exists).ToList();
        var menu = new ContextMenu();
        if (recent.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(sin shows recientes)", IsEnabled = false });
        }
        else
        {
            foreach (string path in recent)
            {
                string p = path;
                menu.Items.Add(Mi(System.IO.Path.GetFileName(p), "", () =>
                {
                    _controller.LoadShow(p);
                    _currentShowPath = p;
                    AddRecentShow(p);
                }));
                ((MenuItem)menu.Items[^1]).ToolTip = p;
            }
        }
        menu.PlacementTarget = (Button)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ── Búsqueda / filtro (Ctrl+F) ─────────────────────────────────────

    private void ToggleSearchBox()
    {
        bool show = SearchBox.Visibility != Visibility.Visible;
        SearchBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SearchLabel.Visibility = SearchBox.Visibility;
        if (show)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
        else
        {
            SearchBox.Text = "";
            CueGrid.Focus();
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        string q = SearchBox.Text.Trim();
        int matches = 0;
        foreach (var vm in _rows)
        {
            bool hit = q.Length == 0
                || vm.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || vm.Number.Contains(q, StringComparison.OrdinalIgnoreCase);
            vm.FilterHidden = !hit;
            if (hit && q.Length > 0)
                matches++;
        }
        if (q.Length > 0)
            FlashStatus($"{Icons.Search} {Loc.T($"{matches} cue(s) coinciden con «{q}»", $"{matches} cue(s) match «{q}»")}");
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ToggleSearchBox();   // cierra y limpia el filtro
            e.Handled = true;
        }
    }

    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_controller is null) return;
        UpdateInspector();   // el inspector SOLO se repuebla al cambiar selección
        UpdateTimelinePanel();
        if (_suppressSelection) return;
        if (CueGrid.SelectedIndex >= 0)
            _controller.OnSetPlayhead(CueGrid.SelectedIndex);
    }

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Doble clic en un GROUP: colapsar/expandir
        int row = CueGrid.SelectedIndex;
        var cue = row >= 0 ? _model.GetCue(row) : null;
        if (cue?.Type == CueType.Group)
            _controller.OnToggleCollapse(row);
    }

    private ScrollViewer? _gridScroll;

    /// <summary>Auto-scroll durante un arrastre: acercarse al borde superior o
    /// inferior de la tabla desplaza la lista (shows largos). WPF dispara
    /// DragOver de forma continua durante el drag, no solo al mover.</summary>
    private void OnGridDragOver(object sender, DragEventArgs e)
    {
        _gridScroll ??= FindDescendant<ScrollViewer>(CueGrid);
        if (_gridScroll is null)
            return;
        const double zone = 28;
        double y = e.GetPosition(CueGrid).Y;
        if (y < zone)
            _gridScroll.ScrollToVerticalOffset(_gridScroll.VerticalOffset - 0.35);
        else if (y > CueGrid.ActualHeight - zone)
            _gridScroll.ScrollToVerticalOffset(_gridScroll.VerticalOffset + 0.35);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T hit)
                return hit;
            if (FindDescendant<T>(child) is T deep)
                return deep;
        }
        return null;
    }

    private void OnGridDrop(object sender, DragEventArgs e)
    {
        // Reordenación interna de filas
        if (e.Data.GetDataPresent("WinCueRow"))
        {
            int from = (int)e.Data.GetData("WinCueRow")!;
            int to = RowIndexAt(e.GetPosition(CueGrid));
            if (to >= 0 && from != to)
                _controller.OnMoveCue(from, to);
            return;
        }
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            _controller.AddCuesFromFiles(CueGrid.SelectedIndex, files);
    }

    // ── Menú contextual de la tabla (botón derecho) ────────────────────

    private void OnGridRightClick(object sender, MouseButtonEventArgs e)
    {
        int row = RowIndexAt(e.GetPosition(CueGrid));
        // Si la fila no está en la selección, seleccionarla (sin romper una
        // selección múltiple que ya la incluya)
        if (row >= 0 && !CueGrid.SelectedItems.Cast<CueRowViewModel>()
                .Any(vm => _model.IndexOfId(vm.CueId) == row))
            CueGrid.SelectedIndex = row;
        CueGrid.ContextMenu = BuildGridMenu(row);
    }

    private ContextMenu BuildGridMenu(int row)
    {
        var menu = new ContextMenu();
        bool edit = ModeToggle.IsChecked != true;
        var cue = row >= 0 ? _model.GetCue(row) : null;
        var selRows = CueGrid.SelectedItems.Cast<CueRowViewModel>()
            .Select(vm => _model.IndexOfId(vm.CueId)).Where(i => i >= 0)
            .OrderBy(i => i).ToList();

        if (cue is not null)
        {
            bool running = _controller.RunningCueIds.Contains(cue.Id);
            menu.Items.Add(Mi($"{Icons.Play} Disparar (sin mover playhead)", "",
                () => _controller.TriggerCueId(cue.Id)));
            if (cue.Type == CueType.Audio && cue.FilePath.Length > 0)
            {
                // PREVIEW, no «audición»: es la fuente del bus CUE que
                // reproduce un cue PARADO desde el principio. La otra fuente es
                // el pinchazo en vivo, y esa se llama CUE. Un nombre por
                // concepto — «audición» era un tercer nombre para esto mismo.
                menu.Items.Add(Mi($"{Icons.Headphones} {Loc.T("Preview (cascos)", "Preview (headphones)")}", "",
                    () => _controller.AuditionToggle(row)));
                menu.Items.Add(Mi($"{Icons.Clef} Normalizar sonoridad (−16 LUFS)", "",
                    () => NormalizeLoudness(selRows), edit));
            }
            // Solo vídeos aún no-HAP; en modo SHOW queda deshabilitado (la
            // conversión es tarea de preparación, no de directo)
            if (MainController.IsHapConvertible(cue))
                menu.Items.Add(Mi($"{Icons.Film} {Loc.T("Convertir a HAP (motor propio)", "Convert to HAP (native engine)")}", "",
                    () => _controller.ConvertCueToHap(row), edit));
            menu.Items.Add(Mi($"{Icons.Pause} Pausar / reanudar", "Ctrl+P",
                () => _controller.OnPauseCueToggle(row), running && cue.IsMedia()));
            menu.Items.Add(Mi($"{Icons.Stop} Detener", "S",
                () => _controller.OnStopSelected(row), running));
            menu.Items.Add(new Separator());

            menu.Items.Add(Mi("Copiar", "Ctrl+C", () => _controller.CopyCues(selRows)));
            menu.Items.Add(Mi("Pegar debajo", "Ctrl+V",
                () => _controller.PasteCues(row), edit));
            menu.Items.Add(Mi("Duplicar", "Ctrl+D",
                () => _controller.DuplicateCues(selRows), edit));
            menu.Items.Add(Mi($"{Icons.Delete} {Loc.T("Eliminar", "Delete")}", "Supr", () =>
            {
                foreach (int r in selRows.OrderByDescending(i => i))
                    _controller.OnDeleteCue(r);
            }, edit));
            menu.Items.Add(new Separator());

            menu.Items.Add(Mi($"{Icons.Group} {Loc.T("Agrupar selección", "Group selection")}", "Ctrl+G",
                () => _controller.OnGroupSelected(selRows), edit));
            if (cue.Type == CueType.Group)
            {
                menu.Items.Add(Mi("Desagrupar", "",
                    () => _controller.OnUngroup(row), edit));
                menu.Items.Add(Mi(cue.Collapsed ? "Expandir" : "Colapsar", "Doble clic",
                    () => _controller.OnToggleCollapse(row)));
            }
            menu.Items.Add(new Separator());

            menu.Items.Add(Mi($"{Icons.Edit} Renombrar", "F2", () =>
            {
                BottomTabs.SelectedIndex = 1;   // pestaña del inspector
                Inspector.FocusName();
            }, edit));
            menu.Items.Add(ColorSubmenu(
                color => CommitRowField(row, "color", color), edit));
            menu.Items.Add(Mi(cue.Armed
                    ? $"{Icons.Cross} Desarmar"
                    : $"{Icons.Check} {Loc.T("Armar", "Arm")}", "",
                () => CommitRowField(row, "armed", !cue.Armed), edit));
            menu.Items.Add(Mi(cue.Flagged
                    ? $"{Icons.FlagOff} {Loc.T("Quitar flag", "Clear flag")}"
                    : $"{Icons.Flag} {Loc.T("Poner flag", "Set flag")}", "",
                () => CommitRowField(row, "flagged", !cue.Flagged), edit));
            menu.Items.Add(Mi($"{Icons.Number} Renumerar todos (1..n)", "",
                () => _controller.RenumberCues(), edit));
            menu.Items.Add(new Separator());
        }
        else
        {
            menu.Items.Add(Mi("Pegar al final", "Ctrl+V",
                () => _controller.PasteCues(_model.Count - 1), edit));
        }

        menu.Items.Add(InsertSubmenu(row, edit));
        menu.Items.Add(Mi($"{Icons.Import} Importar archivos…", "",
            () => OnImportFiles(this, new RoutedEventArgs()), edit));
        // Acción de show: preparar TODOS los vídeos para el motor propio
        if (_model.Cues.Any(MainController.IsHapConvertible))
            menu.Items.Add(Mi($"{Icons.Film} {Loc.T("Convertir todos los vídeos a HAP", "Convert all videos to HAP")}", "",
                () => _controller.ConvertAllVideosToHap(), edit));
        return menu;
    }

    private MenuItem InsertSubmenu(int row, bool enabled)
    {
        var sub = new MenuItem { Header = "Insertar cue", IsEnabled = enabled };
        foreach (var (label, type) in new (string, CueType)[]
        {
            ($"{Icons.Audio} Audio", CueType.Audio), ($"{Icons.Video} Vídeo", CueType.Video),
            ($"{Icons.Wait} Wait", CueType.Wait), ($"{Icons.Fade} Fade", CueType.Fade),
            ($"{Icons.Start} Start", CueType.Start), ($"{Icons.Stop} Stop", CueType.Stop),
            ($"{Icons.Script} Script", CueType.Script), ($"{Icons.Memo} Memo", CueType.Memo),
            ($"{Icons.Group} Grupo", CueType.Group),
        })
        {
            var t = type;
            sub.Items.Add(Mi(label, "", () => _controller.OnInsertCue(t, row)));
        }
        return sub;
    }

    /// <summary>Submenú de color de etiqueta (paleta compartida con Python).</summary>
    internal static MenuItem ColorSubmenu(Action<string> apply, bool enabled)
    {
        var sub = new MenuItem { Header = "Color", IsEnabled = enabled };
        foreach (var (key, label) in new[]
        {
            ("", "Sin color"), ("red", "Rojo"), ("orange", "Naranja"),
            ("yellow", "Amarillo"), ("green", "Verde"), ("blue", "Azul"),
            ("purple", "Morado"),
        })
        {
            string k = key;
            var item = Mi(label, "", () => apply(k));
            string hex = CueConstants.CueColors[key];
            item.Icon = new Border
            {
                Width = 12, Height = 12, CornerRadius = new CornerRadius(2),
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                Background = hex.Length > 0
                    ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex))
                    : Brushes.Transparent,
            };
            sub.Items.Add(item);
        }
        return sub;
    }

    internal static MenuItem Mi(string header, string gesture, Action onClick,
        bool enabled = true)
    {
        var item = new MenuItem
        {
            Header = header,
            InputGestureText = gesture,
            IsEnabled = enabled,
        };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>Analiza la sonoridad (LUFS) de los cues de audio seleccionados
    /// en segundo plano y fija su gain_db para dejarlos a −16 LUFS.</summary>
    private async void NormalizeLoudness(IReadOnlyList<int> rows)
    {
        const double target = -16.0;
        var cues = rows.Select(r => _model.GetCue(r))
            .Where(c => c is { Type: CueType.Audio, FilePath.Length: > 0 })
            .Cast<Cue>().ToList();
        if (cues.Count == 0)
            return;
        int done = 0;
        foreach (var cue in cues)
        {
            FlashStatus($"{Icons.Clef} Midiendo sonoridad de «{cue.Name}»… ({++done}/{cues.Count})");
            double? lufs = await System.Threading.Tasks.Task.Run(
                () => MediaImporter.MeasureLufs(cue.FilePath));
            if (lufs is null)
            {
                FlashStatus($"{Icons.Clef} «{cue.Name}»: no se pudo medir (¿ffmpeg en PATH?)");
                continue;
            }
            double gain = Math.Clamp(Math.Round(target - lufs.Value, 1), -24.0, 12.0);
            int row = _model.IndexOfId(cue.Id);
            if (row >= 0)
                CommitRowField(row, "gain_db", gain);
            FlashStatus($"{Icons.Clef} «{cue.Name}»: {lufs:0.1} LUFS → ganancia {gain:+0.0;-0.0} dB");
        }
    }

    private void CommitRowField(int row, string key, object? value)
    {
        _controller.OnCueEditCommitted(row,
            new Dictionary<string, object?> { [key] = value });
        UpdateInspector();   // el commit vino de fuera del inspector: reapuntar
    }

    // ── Menú contextual de las tarjetas EN REPRODUCCIÓN ────────────────

    private void OnActivePauseMenu(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ActiveRowVM vm)
        {
            int row = _model.IndexOfId(vm.Id);
            if (row >= 0)
                _controller.OnPauseCueToggle(row);
        }
    }

    private void OnActiveStopMenu(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ActiveRowVM vm)
            _controller.StopCueById(vm.Id);
    }

    // ── Drag & drop de filas para reordenar ────────────────────────────

    private void OnGridMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(CueGrid);
        _dragRow = RowIndexAt(_dragStart);
    }

    private void OnGridMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragRow < 0)
            return;
        if (ModeToggle.IsChecked == true)
            return;   // en modo SHOW no se reordena
        var pos = e.GetPosition(CueGrid);
        if (Math.Abs(pos.X - _dragStart.X) < 6 && Math.Abs(pos.Y - _dragStart.Y) < 6)
            return;   // umbral: no confundir clic con arrastre
        int from = _dragRow;
        _dragRow = -1;
        DragDrop.DoDragDrop(CueGrid, new DataObject("WinCueRow", from),
            DragDropEffects.Move);
    }

    /// <summary>Índice de fila bajo un punto del DataGrid (-1 si ninguna).</summary>
    private int RowIndexAt(Point point)
    {
        var hit = CueGrid.InputHitTest(point) as DependencyObject;
        while (hit is not null and not DataGridRow)
            hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
        return hit is DataGridRow row ? row.GetIndex() : -1;
    }

    // ==================================================================
    // Teclado global
    // ==================================================================

    private void OnKeyDownGlobal(object sender, KeyEventArgs e)
    {
        if (_controller is null) return;

        // NO interceptar mientras se escribe en el inspector u otro campo:
        // Espacio en un TextBox dispararía un GO.
        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem)
        {
            if (e.Key == Key.Escape)
                CueGrid.Focus();   // Esc devuelve el foco a la tabla
            return;
        }

        // Atajos configurables (Settings → Interfaz)
        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            if (e.Key == _keyGo)
            {
                DoGo();
                e.Handled = true;
                return;
            }
            if (e.Key == _keyStop)
            {
                _controller.OnStopSelected(CueGrid.SelectedIndex);
                e.Handled = true;
                return;
            }
            if (e.Key == _keyPrev)
            {
                _controller.OnPrevPressed();
                e.Handled = true;
                return;
            }
            // Blackout de vídeo (control de seguridad: gana a los hotkeys)
            if (e.Key == Key.B)
            {
                ToggleBlackout();
                e.Handled = true;
                return;
            }
            // Hotkey por cue (dispara sin mover el playhead)
            if (_cueHotkeys.TryGetValue(e.Key, out int hotCueId))
            {
                _controller.TriggerCueId(hotCueId);
                e.Handled = true;
                return;
            }

            // "Ir a cue": teclear el número y Enter (estilo QLab)
            if (HandleGotoBuffer(e))
                return;
        }

        // Edición (Ctrl+…)
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            var selRows = CueGrid.SelectedItems.Cast<CueRowViewModel>()
                .Select(vm => _model.IndexOfId(vm.CueId)).Where(i => i >= 0).ToList();
            switch (e.Key)
            {
                case Key.Z: _controller.Undo(); e.Handled = true; return;
                case Key.Y: _controller.Redo(); e.Handled = true; return;
                case Key.C: _controller.CopyCues(selRows); e.Handled = true; return;
                case Key.V: _controller.PasteCues(CueGrid.SelectedIndex); e.Handled = true; return;
                case Key.D:
                    if (selRows.Count > 0) _controller.DuplicateCues(selRows);
                    e.Handled = true; return;
                case Key.P:
                    _controller.OnPauseCueToggle(CueGrid.SelectedIndex);
                    e.Handled = true; return;
                case Key.F:
                    ToggleSearchBox();
                    e.Handled = true; return;
            }
        }

        switch (e.Key)
        {
            case Key.Escape:
                _controller.OnStopPressed();
                e.Handled = true;
                break;
            case Key.P when Keyboard.Modifiers == ModifierKeys.None:
                _controller.OnPauseToggle();
                e.Handled = true;
                break;
            case Key.Up:
                _controller.OnPrevPressed();
                e.Handled = true;
                break;
            case Key.Down:
                _controller.OnNextPressed();
                e.Handled = true;
                break;
            case Key.G when Keyboard.Modifiers == ModifierKeys.Control:
                var rows = CueGrid.SelectedItems.Cast<CueRowViewModel>()
                    .Select(vm => _model.IndexOfId(vm.CueId)).Where(i => i >= 0).ToList();
                _controller.OnGroupSelected(rows);
                e.Handled = true;
                break;
            case Key.Delete:
                if (CueGrid.SelectedIndex >= 0 && DeleteButton.IsEnabled)
                {
                    _controller.OnDeleteCue(CueGrid.SelectedIndex);
                    e.Handled = true;
                }
                break;
            case Key.F2:
                Inspector.FocusName();   // renombrado rápido
                e.Handled = true;
                break;
        }
    }

    // ── "Ir a cue" por número: teclear dígitos + Enter ─────────────────

    private string _gotoBuffer = "";
    private long _gotoBufferTicks;

    private bool HandleGotoBuffer(KeyEventArgs e)
    {
        // El buffer caduca a los 4 s sin teclear
        if (_gotoBuffer.Length > 0 && System.Diagnostics.Stopwatch
                .GetElapsedTime(_gotoBufferTicks).TotalSeconds > 4)
            _gotoBuffer = "";

        char? ch = e.Key switch
        {
            >= Key.D0 and <= Key.D9 => (char)('0' + (e.Key - Key.D0)),
            >= Key.NumPad0 and <= Key.NumPad9 => (char)('0' + (e.Key - Key.NumPad0)),
            Key.OemPeriod or Key.Decimal => '.',
            _ => null,
        };
        if (ch is char c)
        {
            _gotoBuffer += c;
            _gotoBufferTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            FlashStatus($"Ir a cue: {_gotoBuffer}{Icons.Bar}  (Enter salta · Retroceso borra)");
            e.Handled = true;
            return true;
        }
        if (_gotoBuffer.Length == 0)
            return false;

        switch (e.Key)
        {
            case Key.Back:
                _gotoBuffer = _gotoBuffer[..^1];
                FlashStatus(_gotoBuffer.Length > 0
                    ? $"Ir a cue: {_gotoBuffer}{Icons.Bar}" : "");
                e.Handled = true;
                return true;
            case Key.Enter:
                string target = _gotoBuffer;
                _gotoBuffer = "";
                int row = -1;
                for (int i = 0; i < _model.Count; i++)
                    if (_model.Cues[i].DisplayNumber == target)
                    {
                        row = i;
                        break;
                    }
                if (row >= 0)
                {
                    _controller.OnSetPlayhead(row);
                    FlashStatus($"Playhead → cue {target}");
                }
                else
                {
                    FlashStatus($"No hay ningún cue con número «{target}»");
                }
                e.Handled = true;
                return true;
        }
        return false;
    }

    // ==================================================================
    // Cierre
    // ==================================================================

    /// <summary>Cerrar con cues sonando o cambios sin guardar pide confirmación
    /// (un Alt+F4 accidental en mitad del show sería fatal).</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_controller is not null && !App.SmokeMode)
        {
            string? warning = null;
            if (_controller.RunningCueIds.Count > 0)
                warning = Loc.T("Hay cues EN REPRODUCCIÓN.", "There are cues PLAYING.");
            else if (_controller.HasUnsavedChanges)
                warning = Loc.T("Hay cambios sin guardar (el autoguardado se descarta al cerrar).",
                                "There are unsaved changes (autosave is discarded on close).");
            if (warning is not null && MessageBox.Show(this,
                    Loc.T($"{warning}\n\n¿Cerrar WinCue igualmente?", $"{warning}\n\nClose WinCue anyway?"),
                    "WinCue — " + Loc.T("Confirmar cierre", "Confirm close"), MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        // El alto del panel de pestañas se GUARDA: «por defecto» tiene que ser
        // el que dejó el operador la última vez, no un número que alguien
        // eligió una vez. Se escribe aquí, con la ventana aún montada — en
        // OnClosed el ActualHeight ya no vale nada.
        if (!App.SmokeMode && BottomPaneRow.ActualHeight > 0)
            _settings.Set("bottom_pane_height",
                (int)Math.Round(BottomPaneRow.ActualHeight));
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _pvw.Shutdown();
        _pgm.Shutdown();
        Inspector.ShutdownPreview();
        for (int s = 0; s < _videoOutputs.Length; s++)
        {
            if (_videoOutputs[s] is { IsVisible: true })
                SaveOutputGeometry(s);
            _videoOutputs[s]?.CloseForShutdown();
        }
        base.OnClosed(e);
    }

    /// <summary>ViewModel de una tarjeta del panel "EN REPRODUCCIÓN".</summary>
    public sealed class ActiveRowVM : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        private ActiveCueInfo _info;

        public ActiveRowVM(ActiveCueInfo info) => _info = info;

        public int Id => _info.Id;

        public string Title => $"{_info.Number} · {_info.Name}";

        public string TimeText => _info.PreWaitRemaining is double p
            ? $"{Icons.Hourglass} espera −{p:0.0}s"
            : _info.Remaining is double r
                ? $"-{TimeSpan.FromSeconds(r):m\\:ss\\.f}"
                : $"{TimeSpan.FromSeconds(_info.Elapsed):m\\:ss\\.f}";

        /// <summary>Ámbar durante el pre_wait, verde reproduciendo.</summary>
        public Brush TimeBrush => _info.PreWaitRemaining is not null
            ? Theme.StandbyBrush
            : Theme.GoBrush;

        public double Fraction => _info.Fraction ?? 0.0;

        public void Update(ActiveCueInfo info)
        {
            _info = info;
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
        }
    }
}
