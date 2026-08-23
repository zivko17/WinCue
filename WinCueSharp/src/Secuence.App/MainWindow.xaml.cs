// =============================================================================
// MainWindow.xaml.cs — tabla de cues + transport, conectada a MainController.
// =============================================================================
// Hito 0/1: solo pintaba la tabla, sin controlador detrás. Esta tarea añade el
// transport (GO/PAUSE/STOP ALL/FADE ALL/PANIC), el playhead en la tabla, el
// panel de cues activos y los atajos de Espacio/Esc — e implementa IShowView
// para que MainController (WinCue.Controller, el MISMO motor que WinCue) hable
// con esta ventana exactamente como habla con la de WPF.
//
// Lo que esta tarea NO trae (a propósito, ver el encargo): inspector, timeline,
// mezclador y salida de vídeo real. Los miembros de IShowView que son solo de
// vídeo quedan en stubs mínimos y honestos, comentados uno a uno más abajo.
// =============================================================================

using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Storage.Pickers;
using WinCue.Controller;
using WinCue.Core;
using WinCue.Presentation;
using WinRT.Interop;

namespace Secuence.App;

public sealed partial class MainWindow : Window, IShowView
{
    private readonly CueModel _model;
    private readonly ObservableCollection<CueRow> _rows = new();
    private readonly SettingsManager _settings = SettingsManager.Instance;
    private MainController? _controller;
    private int _playheadIndex;

    // ── Abrir / Guardar show ─────────────────────────────────────────────
    // hwnd propio: FileOpenPicker/FileSavePicker LANZAN COMException en una
    // app sin identidad de paquete (WindowsPackageType=None, lo que es
    // Secuence) si no se les asocia la ventana con InitializeWithWindow —
    // ver el porqué exacto en OnOpenShowAsync/OnSaveShowAsAsync. Mismo hwnd
    // que VideoOutputWindow.xaml.cs ya obtiene para su AppWindow.
    private readonly nint _hwndPropio;
    private readonly AppWindow _appWindow;
    private string? _currentShowPath;
    private bool _closeConfirmed;   // true tras confirmar cierre con cambios sin guardar

    // ── Salida de vídeo (ver el bloque IShowView de más abajo) ──────────
    private VideoOutputWindow? _videoOutput;
    private bool _blackout;

    // ── VU de programa (piloto Win2D, ver IShowView.UpdateVuMeter) ──────
    // Mismo ancho (9) y misma orientación vertical que _pgmVuL/_pgmVuR en
    // src/WinCue.App/MainWindow.xaml.cs: es el mismo medidor, otro pincel.
    private readonly VuMeterCanvas _pgmVuL = new(vertical: true) { Width = 9 };
    private readonly VuMeterCanvas _pgmVuR = new(vertical: true) { Width = 9 };

    // ── Timeline del show (hitos-0-1, DE SOLO LECTURA) ──────────────────
    // TimelineCanvas.cs, en Win2D: mismo cálculo compartido que
    // src/WinCue.App/TimelinePanel.cs (WinCue.Presentation.TimelineLayout) —
    // ver la cabecera de ese archivo para el alcance exacto de esta pasada.
    private readonly TimelineCanvas _timeline = new();

    // ── Inspector de cues (primera pasada, hitos-0-1) ───────────────────
    // InspectorPanel.cs: identidad/temporización/fundidos/reproducción del
    // cue seleccionado en la tabla — ver la cabecera de ese archivo para el
    // alcance exacto.
    private readonly InspectorPanel _inspector = new();

    // ── Mixer (hitos-0-1) ────────────────────────────────────────────────
    // MixerPanel.cs, en WinUI 3 + Win2D: strip MASTER, strip CUE (audición) y
    // un strip por cada cue de audio/vídeo — ver la cabecera de ese archivo
    // para el cálculo compartido con WinCue.App.AudioMixerPanel.
    private readonly MixerPanel _mixer = new();

    /// <summary>Últimos cues activos recibidos — la mesa se sincroniza
    /// también fuera de UpdateActiveCues (Attach, ReloadTable), y para eso
    /// necesita saber qué suena. Mismo reparto que WinCue.App.MainWindow.
    /// xaml.cs._lastActive / SyncMixer.</summary>
    private IReadOnlyList<ActiveCueInfo> _lastActive = Array.Empty<ActiveCueInfo>();

    /// <summary>CueId del cue seleccionado en la tabla, o -1. Se guarda
    /// aparte de CueList.SelectedItem porque RefreshRow REEMPLAZA la
    /// instancia CueRow de la fila editada en cada commit del inspector (ver
    /// RefreshRow) — sin esto, cada campo comprometido perdería la selección
    /// visual y el inspector se repoblaría solo de leer su propio commit.</summary>
    private int _selectedCueId = -1;

    /// <summary>true mientras RefreshRow reasigna CueList.SelectedItem para
    /// conservar el resaltado tras reemplazar la fila: evita que ese
    /// reasignado dispare OnCueSelectionChanged y repueble el inspector en
    /// mitad de una edición que él mismo acaba de comprometer.</summary>
    private bool _suppressSelectionSync;

    // Reloj del show (fila 0, centro): igual reparto que WinCue.App —lee
    // MainController.Clock, no lo mide por su cuenta— pero con su PROPIO
    // timer, porque IShowView.UpdateCountdown no lo alimenta a él (ver más
    // abajo): el countdown POR CUE ya va en el panel de activos, y mezclar los
    // dos relojes en el mismo texto confundiría más de lo que ayudaría.
    private DispatcherQueueTimer? _clockTimer;
    private double? _showStartTime;

    /// <summary>Ventana vacía: el show lo puebla controller.LoadShow(ruta) vía
    /// ReloadTable/FlashStatus una vez Attach conecta el controlador (ver
    /// App.xaml.cs.OnLaunched) — ya NO se parsea el JSON aquí a mano (ver la
    /// cabecera del archivo para el porqué: rutas relativas sin resolver).</summary>
    public MainWindow(CueModel model)
    {
        InitializeComponent();

        _model = model;

        _hwndPropio = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwndPropio);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        // Cerrar con cambios sin guardar pide confirmación (un Alt+F4
        // accidental en mitad de un show sería fatal — mismo motivo que
        // WinCue.App.OnClosing). WinUI 3 no tiene Window.OnClosing cancelable
        // como WPF: el punto de enganche es AppWindow.Closing, el MISMO
        // mecanismo que VideoOutputWindow.xaml.cs usa para ocultar en vez de
        // destruir — aquí, en cambio, si el operador confirma, se deja pasar
        // el cierre real (_closeConfirmed evita volver a preguntar cuando
        // Close() se llama una segunda vez desde dentro del propio handler).
        _appWindow.Closing += async (_, args) =>
        {
            if (_closeConfirmed || _controller?.HasUnsavedChanges != true)
                return;
            args.Cancel = true;
            if (!await ConfirmDiscardChangesAsync("Cerrar Secuence"))
                return;
            _closeConfirmed = true;
            Close();
        };

        GoIcon.Text = Icons.Play;
        PauseIcon.Text = Icons.Pause;
        StopAllIcon.Text = Icons.Stop;
        FadeAllIcon.Text = Icons.Fade;
        PanicIcon.Text = Icons.Warning;
        BlackoutIcon.Text = Icons.BlackFrame;
        OpenIcon.Text = Icons.Open;
        SaveIcon.Text = Icons.Save;
        // Botones deshabilitados de la reorganización del hueco (ver
        // secuence-shell-report.md): mismo glifo que WinCue para el mismo
        // botón, aunque aquí no hagan nada todavía.
        VideoOutIcon.Text = Icons.VideoOutput;
        AddIcon.Text = Icons.Add;
        ImportIcon.Text = Icons.Import;
        DeleteIcon.Text = Icons.Delete;
        PreviewPlayIcon.Text = Icons.Play;
        TimelineTabIcon.Text = Icons.Timeline;
        InspectorTabIcon.Text = Icons.Inspector;
        MixerTabIcon.Text = Icons.Mixer;

        // Ctrl+O / Ctrl+S / Ctrl+Shift+S — en código, no en XAML: así no hace
        // falta adivinar la sintaxis exacta del atributo Modifiers en el
        // compilador de XAML de WinUI 3, y queda igual de fácil de leer.
        AddAccelerator(Windows.System.VirtualKey.O, Windows.System.VirtualKeyModifiers.Control, OnOpenAccelerator);
        AddAccelerator(Windows.System.VirtualKey.S, Windows.System.VirtualKeyModifiers.Control, OnSaveAccelerator);
        AddAccelerator(Windows.System.VirtualKey.S,
            Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift, OnSaveAsAccelerator);

        CueList.ItemsSource = _rows;
        ActiveCuesList.ItemsSource = Array.Empty<ActiveCueRow>();
        EstadoTexto.Text = "Cargando show…";
        UpdateTitle();

        PgmVuHost.Children.Add(_pgmVuL);
        PgmVuHost.Children.Add(_pgmVuR);

        TimelineHost.Content = _timeline;
        _timeline.SetModel(_model);

        InspectorHost.Content = _inspector;
        // El picker necesita el hwnd propio (misma trampa que OnOpenShowAsync/
        // OnSaveShowAsAsync): el inspector no tiene por qué saber de eso, así
        // que MainWindow resuelve el archivo y el inspector solo recibe la ruta.
        _inspector.BrowseFile = BrowseFileAsync;

        MixerHost.Content = _mixer;

        // Pestaña por defecto: Timeline — mismo criterio que WinCue.App
        // (WPF TabControl sin SelectedIndex explícito = la primera, que allí
        // también es Timeline).
        SelectTab(0);
    }

    // ==================================================================
    // Pestañas Timeline / Inspector / Mixer (reorganización del hueco, ver
    // secuence-shell-report.md). WinUI 3 no tiene un TabControl retemplado
    // con el mismo aspecto de "tecla" que WinCue.App.App.xaml (Style
    // TargetType="TabItem"): tres botones seleccionan cuál de los tres
    // ContentControl (TimelineHost/InspectorHost/MixerHost, superpuestos en
    // la misma celda de MainWindow.xaml) se enseña, con el mismo par de
    // colores que usa WinCue para pestaña activa/inactiva (Theme.WindowBg +
    // Theme.Warn vs. Theme.DeeperBg + Theme.TextDim).
    // ==================================================================

    private void OnTimelineTabClick(object sender, RoutedEventArgs e) => SelectTab(0);
    private void OnInspectorTabClick(object sender, RoutedEventArgs e) => SelectTab(1);
    private void OnMixerTabClick(object sender, RoutedEventArgs e) => SelectTab(2);

    private void SelectTab(int index)
    {
        Button[] buttons = { TimelineTabButton, InspectorTabButton, MixerTabButton };
        TextBlock[] icons = { TimelineTabIcon, InspectorTabIcon, MixerTabIcon };
        TextBlock[] labels = { TimelineTabLabel, InspectorTabLabel, MixerTabLabel };
        UIElement[] hosts = { TimelineHost, InspectorHost, MixerHost };

        for (int i = 0; i < buttons.Length; i++)
        {
            bool selected = i == index;
            buttons[i].Background = selected ? Theme.WindowBgBrush : Theme.DeeperBgBrush;
            var fg = selected ? Theme.WarnBrush : Theme.TextDimBrush;
            icons[i].Foreground = fg;
            labels[i].Foreground = fg;
            hosts[i].Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Selector de archivo para el campo Archivo del inspector — sin
    /// filtro de extensión, igual que el OpenFileDialog de WPF (el inspector
    /// acepta cualquier fichero y deja que MediaImporter detecte el tipo).</summary>
    private async Task<string?> BrowseFileAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, _hwndPropio);
        picker.FileTypeFilter.Add("*");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>Selección de fila en la tabla: repuebla el inspector con el
    /// cue elegido (o lo deshabilita si no hay selección). Se suprime
    /// mientras RefreshRow reasigna la selección tras un commit propio del
    /// inspector — ver _suppressSelectionSync.</summary>
    private void OnCueSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionSync)
            return;
        if (CueList.SelectedItem is CueRow row)
        {
            _selectedCueId = row.CueId;
            _inspector.SetCue(_model.GetById(row.CueId));
        }
        else
        {
            _selectedCueId = -1;
            _inspector.SetCue(null);
        }
    }

    private void AddAccelerator(Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers modifiers,
        TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> invoked)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += invoked;
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    /// <summary>Conecta el controlador (llamado por App tras construir motores
    /// y controlador — mismo orden que WinCue.App.App.xaml.cs.Attach).</summary>
    public void Attach(MainController controller)
    {
        _controller = controller;
        RebuildHotkeys();
        UpdateTransport(controller.IsPaused);
        _timeline.Clock = controller.Clock;

        // Inspector → controlador (edición en vivo campo a campo) — mismo
        // camino que WinCue.App.MainWindow.xaml.cs.Attach: cueId → row por
        // CueModel.IndexOfId, y de ahí a MainController.OnCueEditCommitted,
        // que aplica el cambio EN VIVO si el cue está sonando y marca el show
        // como modificado (HasUnsavedChanges, ver PushUndo).
        _inspector.CommitField = (cueId, key, value) =>
        {
            int row = _model.IndexOfId(cueId);
            if (row >= 0)
                controller.OnCueEditCommitted(row,
                    new Dictionary<string, object?> { [key] = value });
        };

        // Rack de audio del inspector (EQ + compresor): RTA, medidor GR y el
        // aviso de "sin analizador" por cue — MISMO cableado que
        // WinCue.App.MainWindow.xaml.cs.Attach (GetAudioSpectrum/
        // GetAudioGainReduction/CueHasAudioAnalysis ya son públicos y no
        // piden nada nuevo del motor).
        _inspector.SpectrumProvider = cueId => controller.GetAudioSpectrum(cueId);
        _inspector.GainReductionProvider = cueId => controller.GetAudioGainReduction(cueId);
        _inspector.AudioAnalysisProvider = cueId => controller.CueHasAudioAnalysis(cueId);

        // Mesa AUDIO (mixer): faders master/audición/cues + VU + GR limitador
        // — mismo cableado que WinCue.App.MainWindow.xaml.cs.Attach.
        _mixer.MasterGainReductionProvider = () => controller.GetMasterGainReduction();
        _mixer.MasterVolumeChanged = (v, final) =>
        {
            controller.OnMasterVolume(v);            // en vivo a cada cambio
            if (final)
                _settings.Set("master_volume", v);    // persistir con debounce
        };
        _mixer.AuditionVolumeChanged = (v, final) => controller.OnAuditionVolume(v, persist: final);
        _mixer.CueVolumeCommitted = (cueId, vol) =>
        {
            int row = _model.IndexOfId(cueId);
            if (row >= 0)
                controller.OnCueEditCommitted(row,
                    new Dictionary<string, object?> { ["volume"] = vol });
        };
        _mixer.CueStripActivated = cueId =>
        {
            int row = _model.IndexOfId(cueId);
            if (row < 0)
                return;
            // Selecciona la fila: dispara OnCueSelectionChanged, que puebla
            // el inspector — mismo efecto que WPF (CueGrid.SelectedIndex),
            // sin pestaña de inspector que cambiar (Secuence lo enseña
            // siempre, en la columna de la derecha).
            CueList.SelectedIndex = row;
        };
        // CUE = PFL: pinchar la señal VIVA de ese cue en los cascos.
        void PedirCue(int cueId) => controller.PflToggle(cueId);
        _mixer.CueRequested = PedirCue;
        _mixer.AuditionSourceId = () => controller.AuditionSourceCueId;
        // Sin dispositivo de cascos NO hay preescucha posible: el botón CUE
        // se deshabilita en vez de encenderse y no sonar.
        _mixer.CueAvailable = () => _settings.Get("audition_device_name", "").Length > 0;
        _mixer.CueBusSourceNow = () => controller.CueBusNow;
        // StripsRepainted queda sin conectar a propósito: el inspector de
        // Secuence todavía no tiene botones de mute/solo que sincronizar
        // (ver InspectorPanel.cs, "FUERA de esta pasada: el rack de audio").

        _mixer.SetMasterFader(_settings.Get("master_volume", 100));
        _mixer.SetAuditionFader(_settings.Get("audition_volume", 100));

        _clockTimer = DispatcherQueue.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromMilliseconds(250);
        _clockTimer.Tick += (_, _) => RefreshShowClock();
        _clockTimer.Start();

        SyncMixer();   // los canales del show cargado, ya visibles al arrancar
    }

    /// <summary>Pone al día la mesa con TODO el show — mismo reparto que
    /// WinCue.App.MainWindow.xaml.cs.SyncMixer: MixerPanel.SyncActive hace la
    /// reconciliación (crear/actualizar/quitar strips) con el cálculo
    /// compartido WinCue.Presentation.MixerLayout.</summary>
    private void SyncMixer() =>
        _mixer.SyncActive(_lastActive, cueId =>
        {
            int r = _model.IndexOfId(cueId);
            return r >= 0 ? _model.GetCue(r) : null;
        }, _model.Cues);

    // ==================================================================
    // Transport — clics y atajos (Espacio = GO, Esc = STOP ALL)
    // ==================================================================

    private void OnGoClick(object sender, RoutedEventArgs e) => _controller?.OnGoPressed();
    private void OnPauseClick(object sender, RoutedEventArgs e) => _controller?.OnPauseToggle();
    private void OnStopAllClick(object sender, RoutedEventArgs e) => _controller?.OnStopPressed();
    private void OnFadeAllClick(object sender, RoutedEventArgs e) => _controller?.OnFadeAll();
    private void OnPanicClick(object sender, RoutedEventArgs e) => _controller?.OnPanic();
    private void OnBlackoutClick(object sender, RoutedEventArgs e) => ToggleBlackout();

    /// <summary>Blackout de operador: oculta/muestra el HWND hijo de la
    /// salida (si existe todavía — un show sin vídeo abierto no tiene nada
    /// que ocultar). Corte instantáneo, no un fundido — ver el porqué
    /// (airspace, modo clásico sin compositor) en HwndHijo.Ocultar.</summary>
    private void ToggleBlackout()
    {
        _blackout = !_blackout;
        _videoOutput?.SetBlackout(_blackout);
        BlackoutButton.Background = _blackout ? Theme.WarnBrush : Theme.ControlBgBrush;
        FlashStatus(_blackout
            ? $"{Icons.BlackFrame} Blackout activado."
            : "Blackout levantado.");
    }

    private void OnGoAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        _controller?.OnGoPressed();
        args.Handled = true;
    }

    private void OnStopAllAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        _controller?.OnStopPressed();
        args.Handled = true;
    }

    // ==================================================================
    // Abrir / Guardar show — botones de la barra, Ctrl+O, Ctrl+S,
    // Ctrl+Shift+S. El JSON es sagrado: CueModel.ToJson()/FromJson() de
    // WinCue.Core tal cual (dentro de MainController.SaveShow/LoadShow, vía
    // ShowFileService) — un show de Secuence tiene que abrir igual en WinCue
    // y viceversa. Esta ventana solo pone los diálogos, ni una línea de
    // (de)serialización propia.
    // ==================================================================

    /// <summary>Fija el show actual (lo usa App al abrir un show por
    /// argumento de línea de comandos) — mismo contrato que
    /// WinCue.App.SetCurrentShowPath.</summary>
    public void SetCurrentShowPath(string path)
    {
        _currentShowPath = path;
        UpdateTitle();
    }

    /// <summary>Nombre del show actual + asterisco si hay cambios sin
    /// guardar (MainController.HasUnsavedChanges, compartido con WinCue).
    /// Se llama tras Abrir/Guardar/Guardar como y en cada tick del reloj del
    /// show (RefreshShowClock) — así el título no se queda desactualizado si
    /// el show se ensucia por una vía que no pasa por estos botones.</summary>
    private void UpdateTitle()
    {
        string name = _currentShowPath is null ? "(sin guardar)" : Path.GetFileName(_currentShowPath);
        bool dirty = _controller?.HasUnsavedChanges == true;
        Title = $"Secuence — {name}{(dirty ? " *" : "")}";
    }

    private void OnOpenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = OnOpenShowAsync();
    }

    private void OnSaveAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = OnSaveShowAsync();
    }

    private void OnSaveAsAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = OnSaveShowAsAsync();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => _ = OnOpenShowAsync();
    private void OnSaveClick(object sender, RoutedEventArgs e) => _ = OnSaveShowAsync();

    /// <summary>Botón/atajo Abrir: FileOpenPicker con la ventana asociada de
    /// VERDAD (WinRT.Interop.InitializeWithWindow.Initialize, con el hwnd de
    /// WindowNative.GetWindowHandle) — SIN eso, el picker lanza COMException
    /// en una app sin identidad de paquete (WindowsPackageType=None, lo que
    /// es Secuence). Misma forma de trampa, mismo remedio, que ms-appx:///
    /// con IconFont (ver la cabecera de ese archivo) — la app arranca bien y
    /// solo revienta al pulsar el botón si esto falta, así que aquí no hay
    /// try/catch que la esconda: si InitializeWithWindow no basta, que reviente.</summary>
    private async Task OnOpenShowAsync()
    {
        if (!await ConfirmDiscardChangesAsync("Abrir otro show"))
            return;

        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, _hwndPropio);
        picker.FileTypeFilter.Add(".json");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        // LoadShow (MainController → ShowFileService) ya resuelve rutas
        // relativas, repuebla la tabla (ReloadTable) y mueve el playhead al
        // principio — no hay nada más que hacer aquí (ver ShowFileService.
        // LoadShow, WinCue.Controller).
        _controller?.LoadShow(file.Path);
        _playheadIndex = 0;
        _currentShowPath = file.Path;
        UpdateTitle();
    }

    /// <summary>Botón/atajo Guardar: guarda sobre el show abierto SIN
    /// preguntar. Si no hay show todavía (nunca se abrió/guardó), cae en
    /// «Guardar como…» — mismo contrato que WinCue.App.OnSaveShow.</summary>
    private async Task OnSaveShowAsync()
    {
        if (_currentShowPath is null)
        {
            await OnSaveShowAsAsync();
            return;
        }
        _controller?.SaveShow(_currentShowPath);
        UpdateTitle();
    }

    /// <summary>Guardar como…: FileSavePicker (misma trampa de identidad de
    /// paquete que el de abrir) y pasa a ser el show actual.</summary>
    private async Task OnSaveShowAsAsync()
    {
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, _hwndPropio);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("WinCue Show", new List<string> { ".json" });
        picker.SuggestedFileName = _currentShowPath is not null
            ? Path.GetFileNameWithoutExtension(_currentShowPath)
            : "show";

        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return;

        // SaveShow (MainController → ShowFileService) ya relativiza las
        // rutas de media que viven junto al .json y escribe con
        // CueModel.ToJson() tal cual — nada que tocar aquí.
        _controller?.SaveShow(file.Path);
        _currentShowPath = file.Path;
        UpdateTitle();
    }

    /// <summary>Pregunta antes de perder cambios sin guardar (abrir otro show
    /// descarta el actual sin red, y cerrar la ventana con el autoguardado
    /// descartado sería peor todavía). true = seguir adelante (no había
    /// cambios, o el operador confirmó); false = cancelar la acción que lo
    /// preguntó.</summary>
    private async Task<bool> ConfirmDiscardChangesAsync(string action)
    {
        if (_controller?.HasUnsavedChanges != true)
            return true;

        var dialog = new ContentDialog
        {
            Title = "Cambios sin guardar",
            Content = $"Hay cambios sin guardar. ¿{action} de todos modos?",
            PrimaryButtonText = "Sí",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Reloj del show, fila 0: cuenta atrás de la duración total
    /// ARMADA (pre_wait + duración de cada cue armado) menos lo que ha pasado
    /// desde que sonó el primer cue. Reimplementación simplificada de
    /// MainWindow.xaml.cs.UpdateShowClock de WPF (sin el toggle
    /// restante/transcurrido — esta tarea solo pide el countdown) sobre el
    /// MISMO reloj del controlador (_controller.Clock), así que el número que
    /// ve el operador es el que de verdad manda la secuencia.</summary>
    private void RefreshShowClock()
    {
        if (_controller is null)
            return;

        // El asterisco del título se refresca aquí también (no solo tras
        // Abrir/Guardar): si algún día una edición en vivo marca el show
        // como modificado por una vía que no pasa por esta ventana, el
        // título lo refleja igual, sin plumbing nuevo.
        UpdateTitle();

        // Refrescar el color de GO con lo que hay sonando AHORA MISMO: un cue
        // puede terminar solo (sin que el operador toque nada) y el botón
        // tiene que apagarse igual que si hubiera sido STOP ALL.
        bool running = _controller.RunningCueIds.Count > 0;
        GoButton.Background = running ? Theme.GoBrush : Theme.GoButtonBrush;

        if (_showStartTime is null && running)
            _showStartTime = _controller.Clock.Now;

        double elapsed = _showStartTime is double t
            ? Math.Max(0.0, _controller.Clock.Now - t)
            : 0.0;

        double total = 0.0;
        foreach (var cue in _model.Cues)
            if (cue.Armed)
                total += cue.PreWait + Math.Max(0.0, cue.Duration);
        double remaining = Math.Max(0.0, total - elapsed);

        ShowClockText.Text = TimeSpan.FromSeconds(remaining).ToString(@"h\:mm\:ss");
        ShowClockText.Foreground = remaining <= 0.0 && _showStartTime is not null
            ? Theme.PgmBrush
            : Theme.StandbyBrush;
    }

    private void ReloadRowsFromModel()
    {
        _rows.Clear();
        foreach (var cue in _model.Cues)
            _rows.Add(new CueRow(cue, _model));
    }

    // ==================================================================
    // IShowView — Feedback de estado
    // ==================================================================

    public void FlashStatus(string message) => EstadoTexto.Text = message;

    /// <summary>Sin ContentDialog modal a propósito: sacar el error por la
    /// MISMA barra de estado que FlashStatus mantiene la ventana observable
    /// de un tirón por UI Automation (un diálogo modal exige cerrarlo para
    /// seguir leyendo la pantalla, y esta tarea se verifica sin ojos detrás
    /// del teclado — ver el informe). El operador real de Secuence querrá
    /// probablemente un diálogo de verdad más adelante.</summary>
    public void ShowError(string title, string message) =>
        EstadoTexto.Text = $"{Icons.Warning} {title}: {message}";

    // ==================================================================
    // IShowView — Tabla / playhead
    // ==================================================================

    public void SetPlayhead(int index)
    {
        _playheadIndex = index;
        for (int i = 0; i < _rows.Count; i++)
            _rows[i].IsPlayhead = i == index;
        if (index >= 0 && index < _rows.Count)
            CueList.ScrollIntoView(_rows[index]);
    }

    public void RefreshRow(int index)
    {
        if (index < 0 || index >= _rows.Count)
            return;
        var cue = _model.GetCue(index);
        if (cue is null)
            return;
        bool wasPlayhead = _rows[index].IsPlayhead;
        // CueRow es inmutable salvo IsPlayhead (ver CueRow.cs): "refrescar"
        // una fila es reconstruirla desde el modelo y preservar esa única
        // marca — reemplazarla EN SITIO en la ObservableCollection para que
        // la ListView repinte solo esa fila, no la tabla entera.
        var refreshed = new CueRow(cue, _model) { IsPlayhead = wasPlayhead };
        _rows[index] = refreshed;

        // El commit del inspector llega justo por aquí (MainController.
        // OnCueEditCommitted → _view.RefreshRow): la fila reemplazada deja
        // de ser el objeto que la ListView tenía seleccionado. Reasignar la
        // selección al reemplazo conserva el resaltado SIN pasar por
        // OnCueSelectionChanged (_suppressSelectionSync), que si no
        // repoblaría el inspector con sus propios valores recién guardados
        // en mitad de la edición.
        if (refreshed.CueId == _selectedCueId)
        {
            _suppressSelectionSync = true;
            try { CueList.SelectedItem = refreshed; }
            finally { _suppressSelectionSync = false; }
        }
    }

    public void ReloadTable()
    {
        ReloadRowsFromModel();
        SetPlayhead(_playheadIndex);
        RebuildHotkeys();
        _timeline.RefreshFromModel();

        // Show nuevo (o recargado): igual que WPF (UpdateInspector tras
        // ReloadTable), el inspector se deselecciona en vez de intentar
        // adivinar si el mismo CueId de antes sigue significando lo mismo.
        _selectedCueId = -1;
        _inspector.SetCue(null);

        // Show nuevo: los canales de la mesa cambian con él (mismo motivo
        // que WPF.SyncMixer tras ReloadTable).
        SyncMixer();
    }

    public void AppendCueRow(Cue cue)
    {
        _rows.Add(new CueRow(cue, _model));
        _timeline.RefreshFromModel();
        SyncMixer();   // importar medios ya deja su canal en la mesa
    }

    /// <summary>WPF filtra visibilidad de fila por grupo colapsado
    /// (CueRowViewModel.RefreshAll → Visibility). Secuence todavía no tiene
    /// esa UI de colapso (fuera del alcance de esta tarea): reconstruir la
    /// tabla entera desde el modelo es, por ahora, un ApplyGroupVisibility
    /// correcto aunque más caro — no hay estado de colapso que preservar
    /// aparte del que ya vive en Cue.Collapsed.</summary>
    public void ApplyGroupVisibility() => ReloadTable();

    /// <summary>Los hotkeys por cue (Triggers → Hotkey, más allá de Espacio/
    /// Esc) no están construidos todavía en Secuence — quedan fuera de esta
    /// tarea ("los demás luego", dice el encargo). Cuando existan, este
    /// método reconstruirá aquí el mapa tecla→cue recorriendo _model.Cues,
    /// igual que RebuildHotkeys en WPF.</summary>
    public void RebuildHotkeys() { }

    // ==================================================================
    // IShowView — Transport / paneles en vivo
    // ==================================================================

    public void UpdateTransport(bool paused)
    {
        PauseLabel.Text = paused ? "RESUME" : "PAUSE";
        PauseIcon.Text = paused ? Icons.Play : Icons.Pause;
        PauseButton.Background = paused ? Theme.StandbyBrush : Theme.ControlBgBrush;
    }

    /// <summary>No-op a propósito: el reloj GRANDE del show (fila 0) lee
    /// _controller.Clock por su propio timer (RefreshShowClock) en vez de
    /// esperar a que el controlador se lo empuje aquí — mismo reparto que
    /// UpdateShowClock en WPF. El countdown POR CUE (pre_wait/remaining) ya
    /// llega por UpdateActiveCues, así que este método no tiene nada propio
    /// que hacer.</summary>
    public void UpdateCountdown(double? secondsRemaining) { }

    public void UpdateActiveCues(IReadOnlyList<ActiveCueInfo> rows)
    {
        var vm = new ActiveCueRow[rows.Count];
        for (int i = 0; i < rows.Count; i++)
            vm[i] = new ActiveCueRow(rows[i]);
        ActiveCuesList.ItemsSource = vm;
        _timeline.SetActive(rows);

        // Strips del panel de mezclador (misma cadencia; reconcilia sin
        // recrear) — mismo reparto que WinCue.App.MainWindow.xaml.cs.
        _lastActive = rows;
        SyncMixer();
    }

    /// <summary>VU de programa, en Win2D (VuMeterCanvas — piloto del traslado
    /// de los siete controles pintados a mano de WinCue, ver el encargo
    /// hitos-0-1). El strip de mezclador POR CUE sigue fuera de esta tarea:
    /// UpdateCueVuMeter, justo debajo, sigue siendo un stub a propósito.</summary>
    public void UpdateVuMeter(float left, float right)
    {
        _pgmVuL.SetLevel(left);
        _pgmVuR.SetLevel(right);
        _mixer.SetMasterLevel(left, right);
    }

    /// <summary>VU por cue, al strip de mezclador correspondiente (mismo
    /// reparto que WinCue.App.MainWindow.xaml.cs.UpdateCueVuMeter): la
    /// audición local es el "preview" de audio → VU del strip CUE; el resto,
    /// al strip de su propio cue. El inspector sigue sin rack de audio en
    /// esta pasada (ver InspectorPanel.cs), así que aquí no hay nada más que
    /// alimentar.</summary>
    public void UpdateCueVuMeter(int cueId, float left, float right)
    {
        if (cueId == MainController.AuditionCueId)
        {
            _mixer.SetAuditionLevel(left, right);
            return;
        }
        _mixer.UpdateCueLevel(cueId, left, right);
    }

    // ==================================================================
    // IShowView — Salida de vídeo
    // ==================================================================
    // WinCue.Video.VideoEngine (real, con mpv) sustituye a NullVideoEngine
    // desde esta tarea (ver App.xaml.cs): un cue de vídeo ya tiene un HWND de
    // verdad donde pintar. La ventana de salida (VideoOutputWindow.xaml.cs)
    // nace la PRIMERA vez que se pide su HWND —igual que WinCue.App— y se
    // coloca en el monitor de Settings (video_output_screen) a pantalla
    // completa esa primera vez, no en cada Play (ver PlaceOnScreen).

    /// <summary>HWND de la ventana hija donde mpv pinta. La crea y la coloca
    /// en su monitor la PRIMERA vez que se pide (los Play siguientes solo
    /// reciben el mismo handle, ya visible).</summary>
    public nint GetVideoOutputHandle()
    {
        bool firstOpen = _videoOutput is null || !_videoOutput.IsVisible;
        _videoOutput ??= new VideoOutputWindow();
        nint handle = _videoOutput.EnsureVisibleHandle();
        if (firstOpen)
        {
            _videoOutput.PlaceOnScreen(_settings.Get("video_output_screen", 0));
            _videoOutput.SetBlackout(_blackout);   // una salida que nace durante un blackout, nace en negro
        }
        return handle;
    }

    /// <summary>Dimensiones reales de la salida ya abierta; 1920×1080 (mismo
    /// valor por defecto que FakeView en los tests del controlador) mientras
    /// no exista ventana — la geometría CUSTOM de un cue no depende de esto
    /// hasta que Secuence tenga esa UI.</summary>
    public (double Width, double Height) SurfaceDimensions() =>
        _videoOutput is { IsVisible: true } w ? w.ClientSize : (1920.0, 1080.0);

    /// <summary>Testigo: nombre del vídeo en pantalla, o null = NEGRO. Vive
    /// en la barra de estado de ESTA ventana (el operador), no en la salida
    /// — mismo reparto que TallyLabel en WinCue.App.</summary>
    public void SetOutputContent(string? videoName) =>
        TallyLabel.Text = videoName is null
            ? $"{Icons.BlackFrame} SALIDA: NEGRO"
            : $"{Icons.Video} SALIDA: {videoName}";

    /// <summary>Cierre real de la ventana de salida (llamado por
    /// App.xaml.cs.OnLaunched, window.Closed — mismo momento que
    /// audio.Dispose()). Antes de esto, VideoOutputWindow.Closing la oculta
    /// en vez de destruirla: mpv puede seguir apuntando a su HWND hijo.</summary>
    public void CloseVideoOutput() => _videoOutput?.CloseForShutdown();
}
