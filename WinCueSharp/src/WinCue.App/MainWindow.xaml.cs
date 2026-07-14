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
    private VideoOutputWindow? _videoOutput;
    private readonly DispatcherTimer _statusTimer;

    // Mini-monitores PVW/PGM en vivo (mpv embebido y silenciado)
    private readonly LiveMonitorControl _pvw = new("PVW", Color.FromRgb(0x27, 0x86, 0x5a));
    private readonly LiveMonitorControl _pgm = new("PGM", Color.FromRgb(0xc0, 0x39, 0x2b));
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
        Inspector.AuditionRequested += () =>
            _controller.AuditionToggle(CueGrid.SelectedIndex);
        Inspector.SpectrumProvider = cueId => _controller.GetAudioSpectrum(cueId);

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
        Timeline.TriggerRequested = cueId => _controller.TriggerCueId(cueId);
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
            "Nivel L/R del PROGRAMA (cues de AUDIO del mixer; el sonido de los " +
            "vídeos sale por mpv y no es medible aquí)"));
        MonitorHost.Children.Add(MonitorRow(_pvw, _pvwVuL, _pvwVuR,
            "Nivel L/R de la AUDICIÓN local (🎧)"));
        _pvw.Clear("—");
        _pgm.Clear("NEGRO");
        _pgmTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _pgmTimer.Tick += (_, _) => UpdateProgramMonitor();
        _pgmTimer.Start();

        ApplyViewSettings();
        ReloadTable();

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
    }

    private void UpdateNextCueLabel()
    {
        var cue = _model.GetCue(_playheadIndex);
        NextCueLabel.Text = cue is null
            ? "— fin de la lista —"
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

    private void OnFadeAllClick(object sender, RoutedEventArgs e) => _controller.OnFadeAll();

    /// <summary>GO con confirmación opcional al disparar el ÚLTIMO cue.</summary>
    private void DoGo()
    {
        if (_settings.Get("confirm_last_cue", true) &&
            _model.Count > 0 && _controller.Playhead == _model.Count - 1)
        {
            var cue = _model.GetCue(_controller.Playhead);
            if (MessageBox.Show(this,
                    $"Es el ÚLTIMO cue de la lista:\n\n{cue?.DisplayNumber} · {cue?.Name}\n\n¿Disparar?",
                    "GO — último cue", MessageBoxButton.YesNo, MessageBoxImage.Question)
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
            if (_pgm.CurrentPath.Length > 0)
                _pgm.Clear("NEGRO");
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
                _pvw.Clear("♪ AUDIO (onda en el inspector)");
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

    public void FlashStatus(string message)
    {
        StatusLabel.Text = message;
        _statusTimer.Stop();
        _statusTimer.Start();
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

    public void UpdateTransport(bool paused) =>
        PauseButton.Content = paused ? "▶ REANUDAR" : "⏸ PAUSA";

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

        // Progreso embebido en las filas de la tabla (estilo QLab)
        var byId = new Dictionary<int, double>();
        foreach (var r in rows)
            if (r.Fraction is double f)
                byId[r.Id] = f;
        foreach (var vm in _rows)
            vm.SetProgress(byId.GetValueOrDefault(vm.CueId));

        // Cursor y progreso del timeline (posición real de los motores)
        Timeline.SetActive(rows);
    }

    /// <summary>Monitor + VU estéreo pegado a su derecha (misma altura).</summary>
    private static DockPanel MonitorRow(LiveMonitorControl monitor,
        VuMeterControl left, VuMeterControl right, string tooltip)
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
        row.Children.Add(monitor);
        return row;
    }

    public void UpdateVuMeter(float left, float right)
    {
        _pgmVuL.SetLevel(left);
        _pgmVuR.SetLevel(right);
    }

    public void UpdateCueVuMeter(int cueId, float left, float right)
    {
        if (cueId == MainController.AuditionCueId)
        {
            // La audición local es el "preview" de audio → VU del PVW
            _pvwVuL.SetLevel(left);
            _pvwVuR.SetLevel(right);
            return;
        }
        Inspector.UpdateCueLevel(cueId, Math.Max(left, right));
    }

    public nint GetVideoOutputHandle()
    {
        bool firstOpen = _videoOutput is null || !_videoOutput.IsVisible;
        EnsureVideoOutputWindow();
        nint handle = _videoOutput!.EnsureVisibleHandle();
        if (firstOpen)
            PlaceVideoOutput();
        return handle;
    }

    public nint GetVideoOutputLayerHandle(int layer)
    {
        bool firstOpen = _videoOutput is null || !_videoOutput.IsVisible;
        EnsureVideoOutputWindow();
        nint handle = _videoOutput!.EnsureLayerHandle(layer);
        if (firstOpen)
            PlaceVideoOutput();
        return handle;
    }

    public void SetActiveVideoLayers(IReadOnlyCollection<int> activeLayers) =>
        _videoOutput?.SetActiveLayers(activeLayers);

    private void EnsureVideoOutputWindow()
    {
        if (_videoOutput is not null)
            return;
        _videoOutput = new VideoOutputWindow { Owner = this };
        // El botón refleja si la salida está activa (verde) o no (rojo)
        _videoOutput.IsVisibleChanged += (_, _) => UpdateVideoOutButton();
    }

    /// <summary>Coloca la salida en la pantalla configurada (Settings → Vídeo).</summary>
    private void PlaceVideoOutput()
    {
        _videoOutput?.PlaceOnScreen(
            _settings.Get("video_output_screen", 0),
            _settings.Get("video_fullscreen_output", false));
    }

    public (double Width, double Height) SurfaceDimensions()
    {
        if (_videoOutput is { IsVisible: true })
            return (_videoOutput.ActualWidth, _videoOutput.ActualHeight);
        return (1920.0, 1080.0);
    }

    public void SetOutputContent(string? videoName) =>
        TallyLabel.Text = videoName is null ? "SALIDA: NEGRO" : $"SALIDA: ▣ {videoName}";

    // ==================================================================
    // Transport / toolbar
    // ==================================================================

    private void OnGoClick(object sender, RoutedEventArgs e) => DoGo();

    private void OnActiveStopClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int cueId })
            _controller.StopCueById(cueId);
    }
    private void OnStopAllClick(object sender, RoutedEventArgs e) => _controller.OnStopPressed();
    private void OnPanicClick(object sender, RoutedEventArgs e) => _controller.OnPanic();
    private void OnPauseClick(object sender, RoutedEventArgs e) => _controller.OnPauseToggle();

    // ── Reloj del show (restante ↔ transcurrido, clic para alternar) ────
    private bool _clockCountdown = true;
    private long? _showStartTicks;
    private DispatcherTimer? _clockTimer;

    private void OnShowClockClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _showStartTicks = null;   // reiniciar el cronómetro del show
            FlashStatus("Reloj del show reiniciado.");
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
            ShowClockMode.Text = "SHOW · RESTANTE ▾";
            ShowClockText.Text = FormatClock(remaining);
            ShowClockText.Foreground = remaining <= 0.0 && _showStartTicks is not null
                ? new SolidColorBrush(Color.FromRgb(0xc0, 0x39, 0x2b))
                : new SolidColorBrush(Color.FromRgb(0xe8, 0xc0, 0x00));
        }
        else
        {
            ShowClockMode.Text = "SHOW · TRANSCURRIDO ▾";
            ShowClockText.Text = FormatClock(elapsed);
            ShowClockText.Foreground = new SolidColorBrush(Color.FromRgb(0x2e, 0xcc, 0x71));
        }
    }

    private static string FormatClock(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss");

    private void OnShowMode(object sender, RoutedEventArgs e)
    {
        ModeToggle.Content = "Modo: SHOW";
        ModeToggle.Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x7e, 0x34));
        ModeToggle.Foreground = Brushes.White;
        SetEditingEnabled(false);
        BottomTabs.SelectedIndex = 0;   // en SHOW, abajo SIEMPRE el timeline
    }

    private bool _modeReverting;

    private void OnEditMode(object sender, RoutedEventArgs e)
    {
        // Con cues sonando, salir de SHOW merece confirmación (un misclick
        // en directo reabriría la edición)
        if (!_modeReverting && _controller is not null &&
            _controller.RunningCueIds.Count > 0 &&
            MessageBox.Show(this,
                "Hay cues EN REPRODUCCIÓN.\n\n¿Salir del modo SHOW y volver a EDIT?",
                "WinCue — Modo SHOW", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            _modeReverting = true;
            ModeToggle.IsChecked = true;   // re-dispara OnShowMode (idempotente)
            _modeReverting = false;
            return;
        }
        ModeToggle.Content = "Modo: EDIT";
        ModeToggle.Background = new SolidColorBrush(Color.FromRgb(0xc9, 0xa2, 0x27));
        ModeToggle.Foreground = Brushes.Black;
        SetEditingEnabled(true);
    }

    /// <summary>Botón de salida: rojo sin salida activa, verde con ella.</summary>
    private void UpdateVideoOutButton()
    {
        bool active = _videoOutput is { IsVisible: true };
        VideoOutButton.Background = new SolidColorBrush(active
            ? Color.FromRgb(0x1e, 0x7e, 0x34)
            : Color.FromRgb(0x7e, 0x1e, 0x1e));
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

    private void OnVideoOutputClick(object sender, RoutedEventArgs e)
    {
        EnsureVideoOutputWindow();
        if (_videoOutput!.IsVisible)
        {
            _videoOutput.Hide();
        }
        else
        {
            _videoOutput.Show();
            PlaceVideoOutput();
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings) { Owner = this };
        dlg.Saved += () =>
        {
            LoadKeyBindings();
            ApplyViewSettings();
            _controller.OnMasterVolume(_settings.Get("master_volume", 100));
            _applyEngineSettings?.Invoke();
            if (_videoOutput is { IsVisible: true })
                PlaceVideoOutput();   // recolocar en la pantalla elegida
            FlashStatus("Configuración guardada y aplicada.");
        };
        dlg.ShowDialog();
    }

    // ==================================================================
    // Lista
    // ==================================================================

    private void OnAddCue(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } &&
            Enum.TryParse<CueType>(Pascal(tag), out var type))
        {
            _controller.OnInsertCue(type, CueGrid.SelectedIndex);
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

    private void OnOpenShow(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "WinCue Show (*.json)|*.json|Todos|*.*" };
        if (dlg.ShowDialog(this) == true)
        {
            _controller.LoadShow(dlg.FileName);
            AddRecentShow(dlg.FileName);
        }
    }

    private void OnSaveShow(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "WinCue Show (*.json)|*.json" };
        if (dlg.ShowDialog(this) == true)
        {
            _controller.SaveShow(dlg.FileName);
            AddRecentShow(dlg.FileName);
        }
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
            FlashStatus($"🔍 {matches} cue(s) coinciden con «{q}»");
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
            menu.Items.Add(Mi("▶ Disparar (sin mover playhead)", "",
                () => _controller.TriggerCueId(cue.Id)));
            if (cue.Type == CueType.Audio && cue.FilePath.Length > 0)
                menu.Items.Add(Mi("🎧 Audición (cascos)", "",
                    () => _controller.AuditionToggle(row)));
            menu.Items.Add(Mi("⏸ Pausar / reanudar", "Ctrl+P",
                () => _controller.OnPauseCueToggle(row), running && cue.IsMedia()));
            menu.Items.Add(Mi("■ Detener", "S",
                () => _controller.OnStopSelected(row), running));
            menu.Items.Add(new Separator());

            menu.Items.Add(Mi("Copiar", "Ctrl+C", () => _controller.CopyCues(selRows)));
            menu.Items.Add(Mi("Pegar debajo", "Ctrl+V",
                () => _controller.PasteCues(row), edit));
            menu.Items.Add(Mi("Duplicar", "Ctrl+D",
                () => _controller.DuplicateCues(selRows), edit));
            menu.Items.Add(Mi("🗑 Eliminar", "Supr", () =>
            {
                foreach (int r in selRows.OrderByDescending(i => i))
                    _controller.OnDeleteCue(r);
            }, edit));
            menu.Items.Add(new Separator());

            menu.Items.Add(Mi("▸ Agrupar selección", "Ctrl+G",
                () => _controller.OnGroupSelected(selRows), edit));
            if (cue.Type == CueType.Group)
            {
                menu.Items.Add(Mi("Desagrupar", "",
                    () => _controller.OnUngroup(row), edit));
                menu.Items.Add(Mi(cue.Collapsed ? "Expandir" : "Colapsar", "Doble clic",
                    () => _controller.OnToggleCollapse(row)));
            }
            menu.Items.Add(new Separator());

            menu.Items.Add(Mi("✎ Renombrar", "F2", () =>
            {
                BottomTabs.SelectedIndex = 1;   // pestaña del inspector
                Inspector.FocusName();
            }, edit));
            menu.Items.Add(ColorSubmenu(
                color => CommitRowField(row, "color", color), edit));
            menu.Items.Add(Mi(cue.Armed ? "✕ Desarmar" : "✓ Armar", "",
                () => CommitRowField(row, "armed", !cue.Armed), edit));
            menu.Items.Add(Mi(cue.Flagged ? "⚐ Quitar flag" : "⚑ Poner flag", "",
                () => CommitRowField(row, "flagged", !cue.Flagged), edit));
            menu.Items.Add(Mi("№ Renumerar todos (1..n)", "",
                () => _controller.RenumberCues(), edit));
            menu.Items.Add(new Separator());
        }
        else
        {
            menu.Items.Add(Mi("Pegar al final", "Ctrl+V",
                () => _controller.PasteCues(_model.Count - 1), edit));
        }

        menu.Items.Add(InsertSubmenu(row, edit));
        menu.Items.Add(Mi("＋ Importar archivos…", "",
            () => OnImportFiles(this, new RoutedEventArgs()), edit));
        return menu;
    }

    private MenuItem InsertSubmenu(int row, bool enabled)
    {
        var sub = new MenuItem { Header = "Insertar cue", IsEnabled = enabled };
        foreach (var (label, type) in new (string, CueType)[]
        {
            ("♪ Audio", CueType.Audio), ("▣ Vídeo", CueType.Video),
            ("⏱ Wait", CueType.Wait), ("◢ Fade", CueType.Fade),
            ("▶ Start", CueType.Start), ("■ Stop", CueType.Stop),
            ("⚙ Script", CueType.Script), ("✎ Memo", CueType.Memo),
            ("▸ Grupo", CueType.Group),
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
            FlashStatus($"Ir a cue: {_gotoBuffer}▌  (Enter salta · Retroceso borra)");
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
                    ? $"Ir a cue: {_gotoBuffer}▌" : "");
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
                warning = "Hay cues EN REPRODUCCIÓN.";
            else if (_controller.HasUnsavedChanges)
                warning = "Hay cambios sin guardar (el autoguardado se descarta al cerrar).";
            if (warning is not null && MessageBox.Show(this,
                    $"{warning}\n\n¿Cerrar WinCue igualmente?",
                    "WinCue — Confirmar cierre", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _pvw.Shutdown();
        _pgm.Shutdown();
        Inspector.ShutdownPreview();
        _videoOutput?.CloseForShutdown();
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
            ? $"⏳ espera −{p:0.0}s"
            : _info.Remaining is double r
                ? $"-{TimeSpan.FromSeconds(r):m\\:ss\\.f}"
                : $"{TimeSpan.FromSeconds(_info.Elapsed):m\\:ss\\.f}";

        /// <summary>Ámbar durante el pre_wait, verde reproduciendo.</summary>
        public Brush TimeBrush => _info.PreWaitRemaining is not null
            ? new SolidColorBrush(Color.FromRgb(0xe8, 0xc0, 0x00))
            : new SolidColorBrush(Color.FromRgb(0x2e, 0xcc, 0x71));

        public double Fraction => _info.Fraction ?? 0.0;

        public void Update(ActiveCueInfo info)
        {
            _info = info;
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
        }
    }
}
