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

    // Mini-monitores PVW/PGM (fase 5c)
    private readonly MonitorControl _pvw = new("PVW", Color.FromRgb(0x27, 0x86, 0x5a));
    private readonly MonitorControl _pgm = new("PGM", Color.FromRgb(0xc0, 0x39, 0x2b));
    private DispatcherTimer? _pgmTimer;

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

        // Mini-monitores PVW (siguiente) y PGM (programa, ~1.2 s como en Python)
        MonitorHost.Children.Add(_pvw);
        _pvw.Margin = new Thickness(0, 0, 0, 6);
        MonitorHost.Children.Add(_pgm);
        _pvw.Clear("—");
        _pgm.Clear("NEGRO");
        _pgmTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _pgmTimer.Tick += (_, _) => UpdateProgramMonitor();
        _pgmTimer.Start();

        ApplyViewSettings();
        ReloadTable();
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
    }

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
            if (_pgm.LastPath.Length > 0)
                _pgm.Clear("NEGRO");
            return;
        }
        _pgm.ShowFrame(info.Value.FilePath, info.Value.Elapsed);
    }

    /// <summary>PVW: primer frame del cue en standby (si es media con archivo).</summary>
    private void UpdatePreviewMonitor()
    {
        var cue = _model.GetCue(_playheadIndex);
        if (cue is null || !cue.IsMedia() || cue.FilePath.Length == 0)
        {
            if (_pvw.LastPath.Length > 0)
                _pvw.Clear("—");
            return;
        }
        if (cue.FilePath != _pvw.LastPath)
            _pvw.ShowFrame(cue.FilePath, cue.TrimStart, force: true);
    }

    private void UpdateInspector()
    {
        int row = CueGrid.SelectedIndex;
        Inspector.SetCue(row >= 0 ? _model.GetCue(row) : null, () => _model.Cues);
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
    }

    public void ReloadTable()
    {
        _rows.Clear();
        foreach (var cue in _model.Cues)
            _rows.Add(new CueRowViewModel(cue, _model));
        SetPlayhead(_playheadIndex);
        UpdateInspector();   // la lista cambió: reapuntar al cue seleccionado
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

    public void UpdateCountdown(double? secondsRemaining) =>
        CountdownLabel.Text = secondsRemaining is double s
            ? TimeSpan.FromSeconds(s).ToString(s >= 3600 ? @"h\:mm\:ss" : @"m\:ss\.f")
            : "";

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
    }

    public void UpdateVuMeter(float rms) => VuBar.SetLevel(rms);

    public nint GetVideoOutputHandle()
    {
        bool firstOpen = _videoOutput is null || !_videoOutput.IsVisible;
        _videoOutput ??= new VideoOutputWindow { Owner = this };
        nint handle = _videoOutput.EnsureVisibleHandle();
        if (firstOpen)
            PlaceVideoOutput();
        return handle;
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

    private void OnMasterChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_controller is null) return;   // durante InitializeComponent
        MasterLabel.Text = ((int)e.NewValue).ToString();
        _controller.OnMasterVolume((int)e.NewValue);
    }

    private void OnShowMode(object sender, RoutedEventArgs e)
    {
        ModeToggle.Content = "Modo: SHOW";
        SetEditingEnabled(false);
    }

    private void OnEditMode(object sender, RoutedEventArgs e)
    {
        ModeToggle.Content = "Modo: EDIT";
        SetEditingEnabled(true);
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
        _videoOutput ??= new VideoOutputWindow { Owner = this };
        if (_videoOutput.IsVisible)
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
            MasterSlider.Value = _settings.Get("master_volume", 100);
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
            _controller.LoadShow(dlg.FileName);
    }

    private void OnSaveShow(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "WinCue Show (*.json)|*.json" };
        if (dlg.ShowDialog(this) == true)
            _controller.SaveShow(dlg.FileName);
    }

    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_controller is null) return;
        UpdateInspector();   // el inspector SOLO se repuebla al cambiar selección
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
        }
    }

    // ==================================================================
    // Cierre
    // ==================================================================

    protected override void OnClosed(EventArgs e)
    {
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

        public string TimeText => _info.Remaining is double r
            ? $"-{TimeSpan.FromSeconds(r):m\\:ss\\.f}"
            : $"{TimeSpan.FromSeconds(_info.Elapsed):m\\:ss\\.f}";

        public double Fraction => _info.Fraction ?? 0.0;

        public void Update(ActiveCueInfo info)
        {
            _info = info;
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
        }
    }
}
