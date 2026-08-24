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
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Storage.Pickers;
using WinCue.Controller;
using WinCue.Core;
using WinCue.Presentation;
using WinRT.Interop;

namespace Secuence.App;

// CA1001: la ventana vive tanto como la app; _pgm/_pvw (LiveMonitorControl,
// cada uno con su mpv embebido + HwndHijo) se liberan deterministamente vía
// ShutdownMonitors() (llamado por App.xaml.cs en window.Closed) — mismo
// criterio y mismo comentario que WinCue.App.LiveMonitorControl (WPF) sobre
// su propio CA1001.
#pragma warning disable CA1001
public sealed partial class MainWindow : Window, IShowView
{
#pragma warning restore CA1001
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
    // VU de la AUDICIÓN local, en el PVW — mismos que _pvwVuL/_pvwVuR de
    // WinCue.App. Sin ellos la caja del PVW quedaba más ancha que la del PGM.
    private readonly VuMeterCanvas _pvwVuL = new(vertical: true) { Width = 9 };
    private readonly VuMeterCanvas _pvwVuR = new(vertical: true) { Width = 9 };

    // ── Previos PGM/PVW en vivo (mpv embebido, ver LiveMonitorControl.cs) ──
    // PGM se resincroniza por tic (_pgmTimer, igual que WinCue.App); PVW se
    // recarga cuando cambia el playhead (SetPlayhead → UpdatePreviewMonitor)
    // y lleva su propia tira de transporte (_pvwTransportTimer).
    private readonly LiveMonitorControl _pgm;
    private readonly LiveMonitorControl _pvw;
    private DispatcherQueueTimer? _pgmTimer;
    private DispatcherQueueTimer? _pvwTransportTimer;
    private bool _pvwSeeking;   // el operador tiene la barra agarrada (no la recoloca el tic)

    // ── Timeline del show (hitos-0-1, DE SOLO LECTURA) ──────────────────
    // TimelineCanvas.cs, en Win2D: mismo cálculo compartido que
    // src/WinCue.App/TimelinePanel.cs (WinCue.Presentation.TimelineLayout) —
    // ver la cabecera de ese archivo para el alcance exacto de esta pasada.
    private readonly TimelinePanel _timeline = new();

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
        VerifyBevelResources();

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
        WireSplitters();

        // handledEventsToo: true — si no, la tabla se come las letras con su
        // type-ahead y los hotkeys por cue no llegarían nunca.
        RootGrid.AddHandler(UIElement.KeyDownEvent,
            new KeyEventHandler(OnKeyDownGlobal), handledEventsToo: true);

        _appWindow.Closing += async (_, args) =>
        {
            // El alto del panel de pestañas se GUARDA, y se guarda ANTES de
            // mirar si hay cambios sin guardar: si no, cerrar un show limpio
            // —que es lo normal— se llevaba por delante el tamaño que acababa
            // de dejar el operador. Aquí la ventana sigue montada; después del
            // cierre real ActualHeight ya no vale nada (mismo motivo que en
            // WinCue.App.OnClosing).
            if (BottomPaneRow.ActualHeight > 0)
                _settings.Set("bottom_pane_height",
                    (int)Math.Round(BottomPaneRow.ActualHeight));

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
        // (los separadores se cablean en WireSplitters, llamado más arriba)
        StopAllIcon.Text = Icons.Stop;
        FadeAllIcon.Text = Icons.Fade;
        PanicIcon.Text = Icons.Warning;
        BlackoutIcon.Text = Icons.BlackFrame;
        OpenIcon.Text = Icons.Open;
        SaveIcon.Text = Icons.Save;
        // Añadir / Importar / Eliminar / Salida vídeo / Modo: mismo glifo que
        // WinCue para el mismo botón — ver BuildAddMenu para el desplegable
        // de tipos de cue de AddButton.
        VideoOutIcon.Text = Icons.VideoOutput;
        SettingsIcon.Text = Icons.Settings;
        // Los dos rótulos de las últimas columnas de la tabla son glifos, y se
        // ponen desde aquí como el resto (el XAML no conoce Icons).
        FlagColumnIcon.Text = Icons.Flag;
        ArmedColumnIcon.Text = Icons.Cross;
        AddIcon.Text = Icons.Add;
        AddExpandedIcon.Text = Icons.Expanded;
        ImportIcon.Text = Icons.Import;
        DeleteIcon.Text = Icons.Delete;
        PreviewPlayIcon.Text = Icons.Play;
        TimelineTabIcon.Text = Icons.Timeline;
        InspectorTabIcon.Text = Icons.Inspector;
        MixerTabIcon.Text = Icons.Mixer;
        BuildAddMenu();

        // Ctrl+O / Ctrl+S / Ctrl+Shift+S — en código, no en XAML: así no hace
        // falta adivinar la sintaxis exacta del atributo Modifiers en el
        // compilador de XAML de WinUI 3, y queda igual de fácil de leer.
        AddAccelerator(Windows.System.VirtualKey.O, Windows.System.VirtualKeyModifiers.Control, OnOpenAccelerator);
        AddAccelerator(Windows.System.VirtualKey.S, Windows.System.VirtualKeyModifiers.Control, OnSaveAccelerator);
        AddAccelerator(Windows.System.VirtualKey.S,
            Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift, OnSaveAsAccelerator);
        AddAccelerator(Windows.System.VirtualKey.G,
            Windows.System.VirtualKeyModifiers.Control, OnGroupAccelerator);

        CueList.ItemsSource = _rows;
        ActiveCuesList.ItemsSource = Array.Empty<ActiveCueRow>();
        EstadoTexto.Text = "Cargando show…";
        UpdateTitle();

        PgmVuHost.Children.Add(_pgmVuL);
        PgmVuHost.Children.Add(_pgmVuR);
        PvwVuHost.Children.Add(_pvwVuL);
        PvwVuHost.Children.Add(_pvwVuR);

        // Monitores ELÁSTICOS en 16:9 — mismo reparto que
        // WinCue.App.LiveMonitorControl: el alto sale del ancho para que el
        // HUECO DEL VÍDEO (descontando borde y barra del distintivo) quede en
        // proporción. Antes las dos cajas llevaban un Height fijo en el XAML y
        // la imagen no guardaba formato ninguno. El cálculo es el compartido
        // (WinCue.Presentation.MonitorLayout).
        KeepAspect(PgmBox, PgmHeader);
        KeepAspect(PvwBox, PvwHeader);

        // Previos PGM/PVW en vivo — ver LiveMonitorControl.cs para el HwndHijo
        // que hospeda a mpv y el porqué de no espejar el compositor.
        _pgm = new LiveMonitorControl(_hwndPropio, PgmInner, PgmHeader, PgmMessage, PgmStatus);
        _pvw = new LiveMonitorControl(_hwndPropio, PvwInner, PvwHeader, PvwMessage, PvwStatus);
        PreviewPlayIcon.Text = Icons.Play;
        PreviewPlayButton.Click += (_, _) =>
        {
            if (_pvw.PreviewIsPaused)
                _pvw.PreviewPlay();
            else
                _pvw.PreviewPause();
            RefreshPvwTransport();
        };
        PreviewSeekSlider.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => _pvwSeeking = true), true);
        PreviewSeekSlider.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler((_, _) => { _pvwSeeking = false; SeekPvwToSlider(); }), true);
        PreviewSeekSlider.ValueChanged += (_, _) => { if (_pvwSeeking) SeekPvwToSlider(); };
        RefreshPvwTransport();

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

    /// <summary>Los dos separadores arrastrables, con los MISMOS topes que los
    /// dos GridSplitter de WinCue (MainWindow.xaml). WinUI 3 no trae
    /// GridSplitter: el control es PaneSplitter y el cálculo de hasta dónde
    /// llega el arrastre es WinCue.Presentation.SplitterDrag, con pruebas.
    ///
    /// Diferencia deliberada entre los dos, copiada de WinCue: el ancho de la
    /// columna izquierda NO se guarda (cada arranque vuelve a 455), el alto
    /// del panel de pestañas SÍ.</summary>
    private void WireSplitters()
    {
        // Columna izquierda: crece al arrastrar hacia la DERECHA.
        LeftSplitter.Rows = false;
        LeftSplitter.GrowsAgainstDrag = false;
        LeftSplitter.MinSize = 200;
        LeftSplitter.MaxSize = 760;
        LeftSplitter.NeighbourMin = 400;
        LeftSplitter.Current = () => LeftPaneColumn.ActualWidth;
        LeftSplitter.Available = () => RootGrid.ActualWidth;
        LeftSplitter.Apply = w => LeftPaneColumn.Width = new GridLength(w, GridUnitType.Pixel);

        // Panel de pestañas: crece al arrastrar hacia ARRIBA, que es lo que
        // hace falta para que quepa el rack de audio entero.
        BottomSplitter.Rows = true;
        BottomSplitter.GrowsAgainstDrag = true;
        BottomSplitter.MinSize = 140;
        BottomSplitter.MaxSize = 900;
        BottomSplitter.NeighbourMin = 120;
        BottomSplitter.Current = () => BottomPaneRow.ActualHeight;
        // El hueco que se reparten son SUS DOS filas más el separador, no el
        // alto de la ventana: por encima están la cabecera de columnas y la
        // fila de pestañas, que no entran en el reparto.
        BottomSplitter.Available = () =>
            CueListRow.ActualHeight + BottomSplitter.ActualHeight + BottomPaneRow.ActualHeight;
        BottomSplitter.Apply = h => BottomPaneRow.Height = new GridLength(h, GridUnitType.Pixel);

        // Alto tal como se dejó la última vez. El tope de abajo es el MinHeight
        // de la fila; el de arriba evita que un valor raro guardado en otra
        // pantalla deje la lista de cues sin sitio (los dos, de WinCue).
        int panelH = _settings.Get("bottom_pane_height", 330);
        BottomPaneRow.Height =
            new GridLength(Math.Clamp(panelH, 140, 900), GridUnitType.Pixel);
    }

    /// <summary>Ctrl+G — «Agrupar selección», el mismo atajo que WinCue. La
    /// lógica es COMPARTIDA (MainController.OnGroupSelected → CueListEditor),
    /// aquí solo se le pasa la fila.
    ///
    /// Hasta ahora Secuence podía AÑADIR un cue de tipo GROUP pero no meter
    /// nada dentro: agrupar vive en el menú contextual de WinCue, que aquí
    /// todavía no existe. Los recuadros de grupo del timeline, por tanto, no
    /// podían llegar a verse nunca. Con esto ya se pueden crear.
    ///
    /// La tabla de Secuence es de selección ÚNICA, así que se agrupa un cue
    /// cada vez; WinCue agrupa el bloque seleccionado entero. Cuando la tabla
    /// admita selección múltiple, esto ya le pasa la lista.</summary>
    private void OnGroupAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_controller is null || ModeToggle.IsChecked == true)
            return;
        int row = CueList.SelectedIndex;
        if (row < 0)
            return;
        _controller.OnGroupSelected(new[] { row });
    }

    private void OnTimelineTabClick(object sender, RoutedEventArgs e) => SelectTab(0);
    private void OnInspectorTabClick(object sender, RoutedEventArgs e) => SelectTab(1);
    private void OnMixerTabClick(object sender, RoutedEventArgs e) => SelectTab(2);

    private void SelectTab(int index)
    {
        Button[] buttons = { TimelineTabButton, InspectorTabButton, MixerTabButton };
        TextBlock[] icons = { TimelineTabIcon, InspectorTabIcon, MixerTabIcon };
        TextBlock[] labels = { TimelineTabLabel, InspectorTabLabel, MixerTabLabel };
        UIElement[] hosts = { TimelineHost, InspectorHost, MixerHost };

        // El Style trae el RELIEVE (o su ausencia): mismo intercambio que el
        // Trigger IsSelected de WinCue.App.App.xaml (Style TargetType="TabItem")
        // — la activa se queda SIN velo (TabButtonFlat, «plano = lo lees»), la
        // inactiva lo conserva (TabButtonRelief, «relieve = lo tocas»).
        // RootGrid.Resources, no Resources: Microsoft.UI.Xaml.Window no tiene
        // Resources propio (a diferencia de WPF) — ver el comentario de
        // Grid.Resources en MainWindow.xaml para el porqué se declaró en el
        // Grid raíz y no en la ventana.
        var relief = (Style)RootGrid.Resources["TabButtonRelief"];
        var flat = (Style)RootGrid.Resources["TabButtonFlat"];

        for (int i = 0; i < buttons.Length; i++)
        {
            bool selected = i == index;
            buttons[i].Style = selected ? flat : relief;
            buttons[i].Background = selected ? Theme.WindowBgBrush : Theme.DeeperBgBrush;
            var fg = selected ? Theme.WarnBrush : Theme.TextDimBrush;
            icons[i].Foreground = fg;
            labels[i].Foreground = fg;
            hosts[i].Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Red de seguridad: MainWindow.xaml declara los cuatro velos de
    /// relieve (BevelFace/BevelFacePressed/BevelEdge/BevelHover) como recursos
    /// LITERALES en Grid.Resources, porque el compilador de XAML de WinUI 3
    /// resuelve StaticResource en tiempo de compilación y no puede leerlos de
    /// WinCue.Presentation.BevelPalette en caliente (ver el comentario de
    /// Grid.Resources en el XAML). Aquí se comparan, byte a byte, contra
    /// Theme.BevelFaceBrush/BevelFacePressedBrush/BevelEdgeBrush/
    /// BevelHoverBrush — el cálculo REAL, en C# — para que un cambio futuro en
    /// BevelPalette que no se copie a mano al XAML reviente aquí, con un
    /// mensaje claro, en vez de dejar a los dos productos calladamente
    /// descoordinados (mismo espíritu que el guardián de A==0 en
    /// WinCue.App.Theme.Install).</summary>
    private void VerifyBevelResources()
    {
        void CheckGradient(string key, LinearGradientBrush expected)
        {
            var actual = (LinearGradientBrush)RootGrid.Resources[key];
            if (actual.GradientStops.Count != expected.GradientStops.Count)
                throw new InvalidOperationException(
                    $"MainWindow.xaml Grid.Resources['{key}'] tiene {actual.GradientStops.Count} " +
                    $"paradas y Theme.{key}Brush tiene {expected.GradientStops.Count} — copia a mano " +
                    "descoordinada de WinCue.Presentation.BevelPalette.");
            for (int i = 0; i < actual.GradientStops.Count; i++)
            {
                var a = actual.GradientStops[i];
                var e = expected.GradientStops[i];
                if (a.Offset != e.Offset || a.Color != e.Color)
                    throw new InvalidOperationException(
                        $"MainWindow.xaml Grid.Resources['{key}'], parada {i}: " +
                        $"{a.Offset}/{a.Color} en el XAML frente a {e.Offset}/{e.Color} en " +
                        $"Theme.{key}Brush — copia a mano descoordinada de " +
                        "WinCue.Presentation.BevelPalette.");
            }
        }

        CheckGradient("BevelFace", Theme.BevelFaceBrush);
        CheckGradient("BevelFacePressed", Theme.BevelFacePressedBrush);
        CheckGradient("BevelEdge", Theme.BevelEdgeBrush);

        var hover = (SolidColorBrush)RootGrid.Resources["BevelHover"];
        if (hover.Color != Theme.BevelHoverBrush.Color)
            throw new InvalidOperationException(
                $"MainWindow.xaml Grid.Resources['BevelHover'] = {hover.Color} frente a " +
                $"Theme.BevelHoverBrush = {Theme.BevelHoverBrush.Color} — copia a mano " +
                "descoordinada de WinCue.Presentation.BevelPalette.");
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
            _timeline.SetSelected(row.CueId);   // resalta (y trae a la vista)
        }
        else
        {
            _selectedCueId = -1;
            _inspector.SetCue(null);
            _timeline.SetSelected(-1);
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

        // Timeline interactivo (hitos-0-1, segunda pasada): MISMO cableado
        // que WinCue.App.MainWindow.xaml.cs.Attach — los commits van por
        // OnCueEditCommitted (edición en vivo + asterisco de modificado),
        // reordenar por OnMoveCue (la tabla se refresca por el callback de
        // vista del controlador), selección bidireccional con la tabla, y en
        // modo SHOW se selecciona pero no se edita. El interruptor 🔀 lee la
        // MISMA clave de settings que WinCue; su casilla en la interfaz de
        // Secuence queda para la pasada del menú contextual (PENDIENTE).
        _timeline.CommitField = (cueId, key, value) =>
        {
            int row = _model.IndexOfId(cueId);
            if (row >= 0)
                controller.OnCueEditCommitted(row,
                    new Dictionary<string, object?> { [key] = value });
        };
        _timeline.MoveCueRequested = (from, to) => controller.OnMoveCue(from, to);
        _timeline.IsEditAllowed = () => ModeToggle.IsChecked != true;
        // La casilla de la cabecera arranca con lo que diga settings y, cuando
        // la toca el operador, lo persiste — igual que WinCue. SetAutoCrossfade
        // no cuenta como tocarla: si contara, cargar el ajuste lo reescribiría.
        _timeline.SetAutoCrossfade(_settings.Get("timeline_auto_crossfade", true));
        _timeline.AutoCrossfadeToggled = enabled =>
            _settings.Set("timeline_auto_crossfade", enabled);
        _timeline.CueSelected += cueId =>
        {
            var row = _rows.FirstOrDefault(r => r.CueId == cueId);
            if (row is not null)
                CueList.SelectedItem = row;   // → OnCueSelectionChanged → inspector
        };

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

        // Cue objetivo de un START/STOP/FADE: el inspector no conoce el
        // CueModel, se lo damos aquí (igual que WinCue.App le pasa allCues a
        // SetCue). Se lee al repoblar, así que siempre ve la lista de ahora.
        _inspector.AllCues = () => _model.Cues;

        // Aviso de fundido incoherente: los segundos que le quedan al vídeo
        // SALIENTE de la misma pista salen de la proyección del timeline —
        // mismo cableado que WinCue.App (Timeline.OutgoingRemaining).
        _inspector.CrossfadeHeadroomProvider = cueId => _timeline.OutgoingRemaining(cueId);

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
            // Selecciona la fila (dispara OnCueSelectionChanged, que puebla el
            // inspector), salta a la pestaña del inspector y abre dentro la de
            // Audio — los TRES pasos que da WinCue (CueGrid.SelectedIndex +
            // BottomTabs.SelectedIndex=1 + Inspector.ShowAudioTab). Pinchar el
            // canal de un cue en la mesa es pedir SU rack de audio; dejarlo en
            // la pestaña que estuviera abierta obligaba a buscarlo a mano.
            CueList.SelectedIndex = row;
            SelectTab(1);
            _inspector.ShowAudioTab();
        };
        // CUE = PFL: pinchar la señal VIVA de ese cue en los cascos.
        void PedirCue(int cueId) => controller.PflToggle(cueId);
        _mixer.CueRequested = PedirCue;
        _mixer.AuditionSourceId = () => controller.AuditionSourceCueId;
        // Sin dispositivo de cascos NO hay preescucha posible: el botón CUE
        // se deshabilita en vez de encenderse y no sonar.
        _mixer.CueAvailable = () => _settings.Get("audition_device_name", "").Length > 0;
        _mixer.CueBusSourceNow = () => controller.CueBusNow;

        // MUTE/CUE del inspector y de la mesa son EL MISMO estado (un solo
        // MuteSoloBoard) visto desde dos sitios, así que cada uno repinta al
        // otro — mismo cableado que WinCue.App.MainWindow (Inspector.MuteSolo
        // = Mixer.Board + los dos avisos cruzados).
        _inspector.MuteSolo = _mixer.Board;
        _inspector.MuteSoloChanged = () => _mixer.RefreshStripButtons();
        _mixer.StripsRepainted = () => _inspector.RefreshMuteSoloButtons();
        _inspector.CueRequested = PedirCue;
        _inspector.AuditionSourceId = () => controller.AuditionSourceCueId;
        _inspector.CueAvailable = _mixer.CueAvailable;

        _mixer.SetMasterFader(_settings.Get("master_volume", 100));
        _mixer.SetAuditionFader(_settings.Get("audition_volume", 100));

        _clockTimer = DispatcherQueue.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromMilliseconds(250);
        _clockTimer.Tick += (_, _) => RefreshShowClock();
        _clockTimer.Start();

        // PGM: se resincroniza por tic — mismo intervalo (500 ms) que
        // WinCue.App._pgmTimer.
        _pgmTimer = DispatcherQueue.CreateTimer();
        _pgmTimer.Interval = TimeSpan.FromMilliseconds(500);
        _pgmTimer.Tick += (_, _) => UpdateProgramMonitor();
        _pgmTimer.Start();

        // Tira de transporte del PVW: solo pregunta posición/duración a mpv
        // mientras hay algo cargado — mismo intervalo (120 ms) y motivo que
        // WinCue.App.MonitorTransportBar.
        _pvwTransportTimer = DispatcherQueue.CreateTimer();
        _pvwTransportTimer.Interval = TimeSpan.FromMilliseconds(120);
        _pvwTransportTimer.Tick += (_, _) => RefreshPvwTransport();
        _pvwTransportTimer.Start();

        SyncMixer();   // los canales del show cargado, ya visibles al arrancar
    }

    /// <summary>PGM = lo que SALE. Secuence no tiene compositor GPU todavía
    /// (ver la cabecera de LiveMonitorControl.cs), así que siempre decodifica
    /// su propia copia, muda — la misma rama que toma WinCue.App cuando no
    /// hay superficie que espejar.</summary>
    private void UpdateProgramMonitor()
    {
        var info = _controller?.GetProgramVideo();
        if (info is null)
        {
            if (_pgm.CurrentPath.Length > 0)
                _pgm.Clear("NEGRO");
            return;
        }
        // Mismo archivo → solo re-sincroniza; archivo nuevo → carga y arranca ahí.
        _pgm.ShowVideo(info.Value.FilePath, info.Value.Elapsed, loop: false);
    }

    /// <summary>PVW: el cue en standby reproduciéndose en bucle (mudo).</summary>
    private void UpdatePreviewMonitor()
    {
        var cue = _model.GetCue(_playheadIndex);
        if (cue is null || cue.FilePath.Length == 0 || cue.Type != CueType.Video)
        {
            if (_pvw.CurrentPath.Length > 0 || cue is null)
                _pvw.Clear("—");
            return;
        }
        if (cue.FilePath != _pvw.CurrentPath)
            _pvw.ShowVideo(cue.FilePath, cue.TrimStart, loop: true);
    }

    private void SeekPvwToSlider()
    {
        double dur = _pvw.PreviewDuration;
        if (dur > 0.05)
            _pvw.PreviewSeek(PreviewSeekSlider.Value / PreviewSeekSlider.Maximum * dur);
    }

    /// <summary>Pone al día botón, barra y reloj de la tira de transporte del
    /// PVW — mismo cálculo (WinCue.Presentation.MonitorClockFormat) y mismo
    /// reparto que WinCue.App.MonitorTransportBar.Refresh.</summary>
    private void RefreshPvwTransport()
    {
        PreviewPlayIcon.Text = _pvw.PreviewIsPaused ? Icons.Play : Icons.Pause;

        double dur = _pvw.PreviewDuration;
        double pos = _pvw.PreviewPosition;
        PreviewTimeText.Text = MonitorClockFormat.Format(pos, dur);
        if (_pvwSeeking || dur <= 0.05)
            return;
        double v = Math.Clamp(pos / dur, 0.0, 1.0) * PreviewSeekSlider.Maximum;
        if (Math.Abs(PreviewSeekSlider.Value - v) <= 1.0)
            return;
        _pvwSeeking = true;   // no re-entrar por ValueChanged
        PreviewSeekSlider.Value = v;
        _pvwSeeking = false;
    }

    /// <summary>Libera los dos previos (mpv + HWND hijo) — llamado por
    /// App.xaml.cs, window.Closed, ANTES de CloseVideoOutput()/video.Dispose():
    /// menos instancias de mpv vivas a la vez durante el cierre.</summary>
    public void ShutdownMonitors()
    {
        _pgmTimer?.Stop();
        _pvwTransportTimer?.Stop();
        _pgm.Dispose();
        _pvw.Dispose();
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
    // Lista — Añadir / Importar / Eliminar: WinCue.Controller.CueListEditor
    // (vía MainController), el MISMO motor y la MISMA validación que
    // WinCue.App — ni una línea de lógica de lista propia aquí. Modo EDIT/
    // SHOW: bloquea estas tres acciones + Abrir/Guardar + la edición del
    // inspector (ver SetEditingEnabled). Salida de vídeo: mostrar/ocultar la
    // ventana que ya coloca VideoOutputWindow.PlaceOnScreen — sin el menú de
    // patrones de prueba de WinCue (fuera de esta pasada).
    // ==================================================================

    /// <summary>Desplegable de AddButton: un MenuFlyoutItem por tipo de cue,
    /// mismos nueve tipos y mismo orden que el ContextMenu de
    /// WinCue.App.MainWindow.xaml (AddButton). Se construye en código, no en
    /// XAML — mismo motivo que los aceleradores de teclado del constructor:
    /// más fácil de leer que adivinar la sintaxis de MenuFlyoutItem en el
    /// compilador de XAML de WinUI 3. Button.Flyout se abre solo con Click
    /// en WinUI 3, así que no hace falta el equivalente de
    /// WinCue.App.OnAddMenuClick (que sí tiene que abrir su ContextMenu a
    /// mano).</summary>
    private void BuildAddMenu()
    {
        var flyout = new MenuFlyout();
        void Item(string cueTypeName, string label)
        {
            var mi = new MenuFlyoutItem { Text = label, Tag = cueTypeName };
            mi.Click += OnAddCue;
            flyout.Items.Add(mi);
        }
        Item(nameof(CueType.Audio), Loc.T("Audio", "Audio"));
        Item(nameof(CueType.Video), Loc.T("Vídeo", "Video"));
        Item(nameof(CueType.Wait), Loc.T("Espera", "Wait"));
        Item(nameof(CueType.Fade), Loc.T("Fundido", "Fade"));
        Item(nameof(CueType.Start), Loc.T("Iniciar", "Start"));
        Item(nameof(CueType.Stop), Loc.T("Parar", "Stop"));
        Item(nameof(CueType.Script), "Script");
        Item(nameof(CueType.Memo), Loc.T("Nota", "Memo"));
        Item(nameof(CueType.Group), Loc.T("Grupo", "Group"));
        AddButton.Flyout = flyout;
    }

    /// <summary>Inserta el tipo de cue elegido bajo la fila seleccionada —
    /// mismo contrato que WinCue.App.MainWindow.xaml.cs.OnAddCue: pasa por
    /// OnInsertCue, no AddNewCue (WinCue.App tampoco llama a AddNewCue desde
    /// su UI, solo desde tests).</summary>
    private void OnAddCue(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string tag } && Enum.TryParse<CueType>(tag, out var type))
            _controller?.OnInsertCue(type, CueList.SelectedIndex);
    }

    /// <summary>Importar archivos: selector múltiple + un cue por archivo —
    /// mismo contrato que WinCue.App.OnImportFiles (CueListEditor.
    /// AddCuesFromFiles ya valida con MediaImporter y detecta audio/vídeo
    /// por extensión, nada de eso se repite aquí). Mismo hwnd propio que
    /// Abrir/Guardar — ver InitializeWithWindow en la cabecera del archivo.</summary>
    private async void OnImportFilesClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, _hwndPropio);
        foreach (string ext in new[]
        {
            ".wav", ".flac", ".aiff", ".aif", ".ogg", ".mp3", ".aac", ".m4a", ".opus",
            ".mp4", ".mov", ".avi", ".mkv", ".webm",
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp",
        })
            picker.FileTypeFilter.Add(ext);
        picker.FileTypeFilter.Add("*");   // "Todos" — mismo criterio que WPF
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0)
            return;
        _controller?.AddCuesFromFiles(CueList.SelectedIndex, files.Select(f => f.Path).ToList());
    }

    /// <summary>Elimina el cue seleccionado — mismo contrato que
    /// WinCue.App.OnDeleteCue (CueListEditor.OnDeleteCue ya para el cue si
    /// estaba sonando antes de borrarlo).</summary>
    private void OnDeleteCueClick(object sender, RoutedEventArgs e)
    {
        int row = CueList.SelectedIndex;
        if (row >= 0)
            _controller?.OnDeleteCue(row);
    }

    private bool _modeReverting;   // evita re-preguntar cuando ModeToggle.IsChecked se reasigna solo

    private void OnShowMode(object sender, RoutedEventArgs e)
    {
        ModeToggle.Content = "MODO: SHOW";
        ModeToggle.Background = Theme.GoButtonBrush;
        ModeToggle.Foreground = new SolidColorBrush(Colors.White);
        SetEditingEnabled(false);
    }

    /// <summary>Con cues sonando, salir de SHOW pide confirmación — mismo
    /// motivo y mismo texto que WinCue.App.OnEditMode (un misclick en
    /// directo no debería reabrir la edición en mitad de una función). WinUI
    /// 3 no tiene MessageBox: mismo ContentDialog que
    /// ConfirmDiscardChangesAsync usa para Abrir/cerrar con cambios sin
    /// guardar.</summary>
    private async void OnEditMode(object sender, RoutedEventArgs e)
    {
        if (!_modeReverting && _controller is not null && _controller.RunningCueIds.Count > 0)
        {
            var dialog = new ContentDialog
            {
                Title = "Modo SHOW",
                Content = "Hay cues EN REPRODUCCIÓN.\n\n¿Salir del modo SHOW y volver a EDIT?",
                PrimaryButtonText = "Sí",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                _modeReverting = true;
                ModeToggle.IsChecked = true;   // re-dispara OnShowMode (idempotente)
                _modeReverting = false;
                return;
            }
        }
        ModeToggle.Content = "MODO: EDIT";
        ModeToggle.Background = Theme.GoldBrush;
        ModeToggle.Foreground = new SolidColorBrush(Colors.Black);
        SetEditingEnabled(true);
    }

    /// <summary>Bloquea/desbloquea todo lo que puede tocar la lista por
    /// accidente durante una función: el MISMO conjunto que WinCue.App.
    /// SetEditingEnabled/ToolbarSetEnabled bloquea (Abrir, Guardar, Añadir,
    /// Importar y Eliminar viven en la MISMA barra en los dos productos —
    /// ver el comentario del XAML). GO/transport/mezclador siguen vivos:
    /// SHOW bloquea la LISTA, no el audio.
    ///
    /// EL INSPECTOR NO SE BLOQUEA. Se bloqueaba, y era una diferencia con
    /// WinCue de las que se pagan caras: allí SetEditingEnabled no toca el
    /// inspector, y el candado del controlador (CueListEditor.RejectIfLocked)
    /// solo rechaza AÑADIR / INSERTAR / BORRAR / IMPORTAR — la LISTA. Editar
    /// los campos de un cue sigue permitido en modo SHOW, a propósito: es lo
    /// que se hace en un ensayo general, corregir un tiempo o un fundido con
    /// la función montada. En Secuence el inspector entero se quedaba gris y
    /// no respondía a nada.</summary>
    private void SetEditingEnabled(bool enabled)
    {
        _controller?.SetEditingLocked(!enabled);
        OpenButton.IsEnabled = enabled;
        SaveButton.IsEnabled = enabled;
        AddButton.IsEnabled = enabled;
        ImportButton.IsEnabled = enabled;
        DeleteButton.IsEnabled = enabled;
    }

    /// <summary>Muestra/oculta la salida de vídeo — a diferencia de
    /// WinCue.App (menú con mostrar/ocultar POR PANTALLA, patrones de
    /// prueba, blackout e «identificar»), Secuence solo tiene una salida:
    /// este botón es el equivalente reducido a lo que ya existe —
    /// GetVideoOutputHandle crea/coloca/muestra por el MISMO camino que un
    /// cue de vídeo, así que no hay lógica de colocación nueva aquí.</summary>
    private void OnVideoOutButtonClick(object sender, RoutedEventArgs e)
    {
        if (_videoOutput is { IsVisible: true })
            _videoOutput.Hide();
        else
            GetVideoOutputHandle();
        UpdateVideoOutButton();
    }

    /// <summary>Verde con la salida visible, rojo sin ella — mismo criterio
    /// de color que WinCue.App.UpdateVideoOutButton (sin sus estados de
    /// patrón de prueba/blackout: aquí Blackout es un botón propio,
    /// BlackoutButton). Se re-llama en cada tick del reloj del show
    /// (RefreshShowClock) por si la salida cambia de visibilidad por otra
    /// vía (Alt+F4 en la ventana de salida) — mismo idioma que el asterisco
    /// del título.</summary>
    private void UpdateVideoOutButton()
    {
        bool active = _videoOutput is { IsVisible: true };
        VideoOutButton.Background = active ? Theme.GoButtonBrush : Theme.PanicButtonBrush;
    }

    // ==================================================================
    // Ajustes (SettingsPanel.cs) — puerta de la tarea: hasta esta pasada
    // cambiar el monitor de salida de vídeo obligaba a editar settings.json
    // a mano (ver el encargo). WinUI 3 no tiene una Window modal ligera como
    // WPF (Window+ShowDialog): el formulario (SettingsPanel, un
    // ContentControl corriente) se aloja en un ContentDialog — mismo patrón
    // que ConfirmDiscardChangesAsync/OnEditMode más arriba.
    // ==================================================================

    /// <summary>Motor de audio a reconfigurar en caliente tras guardar
    /// Ajustes — mismo reparto que WinCue.App.MainWindow.OnSettingsClick
    /// (dlg.Saved → _applyEngineSettings). App.xaml.cs la fija justo después
    /// de construir el AudioEngine; Attach() no la toca (no hace falta la
    /// ventana entera, solo esta acción).</summary>
    public Action? ApplyEngineSettings { get; set; }

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var panel = new SettingsPanel(_settings) { BrowseFile = BrowseFileAsync };
        var dialog = new ContentDialog
        {
            Title = $"Secuence — {Loc.T("Configuración", "Settings")}",
            // Tamaño FIJO del contenido — no MaxHeight. Un ContentDialog de
            // WinUI 3 se ajusta a su contenido, así que con el alto libre cada
            // pestaña redimensionaba el cuadro al cambiar de una a otra: el
            // marco saltaba y los botones Guardar/Cancelar se movían bajo el
            // ratón. Con un alto fijo el marco no se mueve nunca y es el
            // ScrollViewer de cada pestaña (SettingsPanel.MakeTab) el que
            // desplaza lo que no quepa. El número sale de la ventana en vez de
            // estar clavado, para que el cuadro entero quepa también en el
            // monitor de 1024x600 — ver SettingsDialogMetrics, que es donde esa
            // cuenta tiene pruebas.
            Content = new Border
            {
                Width = SettingsDialogMetrics.ContentWidth,
                Height = SettingsDialogMetrics.ContentHeight(
                    Content.XamlRoot?.Size.Height ?? 0),
                Child = panel,
            },
            PrimaryButtonText = Loc.T("Guardar", "Save"),
            SecondaryButtonText = Loc.T("Restaurar valores de fábrica", "Restore factory defaults"),
            CloseButtonText = Loc.T("Cancelar", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            // Fuerza el tema oscuro EN ESTE diálogo (Microsoft.UI.Xaml.
            // Controls.ComboBox/CheckBox/NumberBox/TabView no tienen aquí una
            // plantilla propia como los Button de MainWindow.xaml — Grid.
            // Resources — así que sin esto seguirían el tema del SISTEMA, no
            // el de la app): mismo criterio "misma familia" del encargo, con
            // el mecanismo que WinUI 3 ofrece para un ContentDialog suelto en
            // vez de retemplar cada control uno a uno.
            RequestedTheme = ElementTheme.Dark,
            Background = Theme.WindowBgBrush,
            Foreground = Theme.TextPrimaryBrush,
            XamlRoot = Content.XamlRoot,
        };
        // Restaurar valores de fábrica NO cierra el diálogo ni guarda de
        // inmediato (a diferencia de WPF, que sí persiste ahí mismo tras un
        // MessageBox de confirmación): un ContentDialog de WinUI 3 no puede
        // abrir un segundo ContentDialog de confirmación sin cerrarse él
        // primero. Aquí "Restaurar" solo repuebla los campos EN PANTALLA —
        // sigue haciendo falta pulsar Guardar para que se escriba a disco,
        // lo que de paso da un "cancelar" gratis si fue un despiste.
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            panel.LoadDefaults();
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        int previousSampleRate = panel.InitialSampleRate;
        var updates = panel.CollectValues();
        _settings.SetMany(updates);

        if (Convert.ToInt32(updates["audio_sample_rate"]) != previousSampleRate)
        {
            var info = new ContentDialog
            {
                Title = Loc.T("Configuración", "Settings"),
                Content = Loc.T(
                    "La nueva frecuencia de muestreo se aplicará al reiniciar Secuence.",
                    "The new sample rate will apply after restarting Secuence."),
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot,
            };
            await info.ShowAsync();
        }

        // Reconfigurar EN CALIENTE — mismo reparto que
        // WinCue.App.MainWindow.OnSettingsClick (dlg.Saved): dispositivo de
        // audio (ApplyEngineSettings, fijado por App.xaml.cs), volumen
        // master, y la salida de vídeo si ya está abierta (recolocarla en el
        // monitor recién elegido, sin esperar al siguiente GO).
        ApplyEngineSettings?.Invoke();
        _controller?.OnMasterVolume(_settings.Get("master_volume", 100));
        _mixer.SetMasterFader(_settings.Get("master_volume", 100));
        if (_videoOutput is { IsVisible: true })
            _videoOutput.PlaceOnScreen(_settings.Get("video_output_screen", 0));
        FlashStatus(Loc.T("Configuración guardada y aplicada.", "Settings saved and applied."));
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

    /// <summary>true en modo SHOW: los aceleradores de teclado NO pasan por
    /// Button.IsEnabled (van directos a RootGrid.KeyboardAccelerators), así
    /// que Ctrl+O/Ctrl+S/Ctrl+Shift+S necesitan su propio candado — si no,
    /// un despiste en directo abriría o pisaría el show sonando pese a que
    /// OpenButton/SaveButton estén en gris. Mismo candado que OpenButton/
    /// SaveButton (MainController.EditingLocked), no uno nuevo.</summary>
    private bool EditingLocked => _controller?.EditingLocked == true;

    private void OnOpenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (!EditingLocked)
            _ = OnOpenShowAsync();
    }

    private void OnSaveAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (!EditingLocked)
            _ = OnSaveShowAsync();
    }

    private void OnSaveAsAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (!EditingLocked)
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
        // título lo refleja igual, sin plumbing nuevo. Mismo idioma para el
        // color del botón de salida de vídeo (ver UpdateVideoOutButton).
        UpdateTitle();
        UpdateVideoOutButton();

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
        UpdatePreviewMonitor();
        UpdateNextCueLabel();
    }

    /// <summary>SIGUIENTE: el cue en el playhead — mismo dato que WinCue.App.
    /// UpdateNextCueLabel, leído de MainController.Playhead (la MISMA fuente
    /// que el índice que este método acaba de recibir) para no inventar un
    /// segundo sitio donde se decide "el siguiente".</summary>
    private void UpdateNextCueLabel()
    {
        if (_controller is null)
            return;
        var cue = _model.GetCue(_controller.Playhead);
        NextCueLabel.Text = cue is null
            ? Loc.T("— fin de la lista —", "— end of list —")
            : $"{cue.DisplayNumber} · {cue.Name}";
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

        // El commit del inspector llega justo por aquí (MainController.
        // OnCueEditCommitted → _view.RefreshRow): la fila reemplazada deja de
        // ser el objeto que la ListView tenía seleccionado. Reasignar la
        // selección al reemplazo conserva el resaltado SIN pasar por
        // OnCueSelectionChanged, que si no repoblaría el inspector con sus
        // propios valores recién guardados en mitad de la edición.
        //
        // OJO CON EL ORDEN, que es donde estaba el fallo: hay que apuntar a
        // quién estaba seleccionado ANTES de tocar la colección, y el
        // guardián tiene que cubrir el REEMPLAZO, no solo la reasignación.
        // Quitar de la colección el objeto seleccionado hace que la ListView
        // ponga SelectedItem en null y dispare OnCueSelectionChanged AHÍ
        // MISMO; con el guardián puesto después, ese disparo entraba y dejaba
        // _selectedCueId en -1, así que la comparación de abajo ya no casaba
        // nunca y la selección se perdía en CADA edición de CADA campo.
        int selectedBefore = _selectedCueId;
        bool wasSelected = _rows[index].CueId == selectedBefore;

        _suppressSelectionSync = true;
        try
        {
            _rows[index] = refreshed;
            if (wasSelected)
            {
                CueList.SelectedItem = refreshed;
                _selectedCueId = selectedBefore;
            }
        }
        finally { _suppressSelectionSync = false; }

        // El timeline proyecta el MISMO modelo: un campo comprometido (desde
        // el inspector o desde el propio timeline) lo repinta — igual que
        // WinCue.App.MainWindow.RefreshRow.
        _timeline.RefreshFromModel();
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
    /// <summary>Mapa tecla → cue de «Disparadores → Hotkey». Lo mismo que
    /// WinCue.App.MainWindow._cueHotkeys, con VirtualKey en vez de Key.</summary>
    private readonly Dictionary<Windows.System.VirtualKey, int> _cueHotkeys = new();

    /// <summary>Reconstruye el mapa. Lo llama el controlador cada vez que la
    /// lista cambia (es parte de IShowView).</summary>
    public void RebuildHotkeys()
    {
        _cueHotkeys.Clear();
        foreach (var cue in _model.Cues)
        {
            if (cue.Hotkey.Length == 0)
                continue;
            // El alias («Esc», «Return») lo traduce el cálculo COMPARTIDO;
            // pasar de nombre a tecla es de framework y va aquí, contra
            // VirtualKey, porque WPF y WinUI 3 no usan la misma enumeración.
            if (Enum.TryParse<Windows.System.VirtualKey>(
                    HotkeyName.Normalize(cue.Hotkey), ignoreCase: true, out var key))
                _cueHotkeys[key] = cue.Id;
        }
    }

    /// <summary>Los hotkeys por cue. Va por AddHandler con
    /// handledEventsToo, y NO como KeyboardAccelerator, por dos motivos:
    /// hay que ver la tecla aunque la haya consumido quien tenga el foco (la
    /// tabla se come las letras haciendo type-ahead), y hace falta poder
    /// CALLARSE mientras se escribe.
    ///
    /// Ese silencio es la parte importante: sin él, teclear el nombre de un
    /// cue en el inspector dispararía los cues cuyas teclas coincidan con las
    /// letras que escribes. WinCue se planta igual (OnKeyDownGlobal sale
    /// entero si el foco está en un TextBox o un ComboBox).</summary>
    private void OnKeyDownGlobal(object sender, KeyRoutedEventArgs e)
    {
        if (_controller is null || e.Handled)
            return;

        var focused = FocusManager.GetFocusedElement(Content.XamlRoot);
        if (focused is TextBox or ComboBox or ComboBoxItem)
            return;

        // Sin modificadores: un Ctrl+S no puede disparar el cue de la «S».
        if (IsDown(Windows.System.VirtualKey.Control)
            || IsDown(Windows.System.VirtualKey.Menu)
            || IsDown(Windows.System.VirtualKey.Shift))
            return;

        if (_cueHotkeys.TryGetValue(e.Key, out int cueId))
        {
            // Dispara SIN mover el playhead, igual que en WinCue.
            _controller.TriggerCueId(cueId);
            e.Handled = true;
        }
    }

    private static bool IsDown(Windows.System.VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

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

        // El fader del inspector y el del strip son EL MISMO fader: mover uno
        // tiene que mover el otro EN VIVO. El strip ya se refrescaba del
        // modelo en cada vuelta; sin esto el inspector solo se enteraba al
        // reseleccionar el cue y el mismo volumen se veía con dos valores.
        if (_selectedCueId >= 0)
            _inspector.SyncVolumeFromModel(_model.GetById(_selectedCueId));
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
    /// al strip de su propio cue, y también al strip de niveles del
    /// INSPECTOR, que mide la misma señal (filtra él por cue).</summary>
    public void UpdateCueVuMeter(int cueId, float left, float right)
    {
        if (cueId == MainController.AuditionCueId)
        {
            // La audición local es el "preview" de audio → VU del PVW, igual
            // que en WinCue.App.UpdateCueVuMeter.
            _pvwVuL.SetLevel(left);
            _pvwVuR.SetLevel(right);
            _mixer.SetAuditionLevel(left, right);
            return;
        }
        _mixer.UpdateCueLevel(cueId, left, right);
        // Y al strip del INSPECTOR, que mide la misma señal: el mismo cue no
        // puede leerse distinto según la pestaña. Filtra él por cue.
        _inspector.UpdateCueLevel(cueId, left, right);
    }

    /// <summary>Mantiene la caja de un monitor en 16:9 al cambiar de ancho.
    ///
    /// El guardián de re-entrada es imprescindible: asignar Height vuelve a
    /// disparar SizeChanged, y sin comprobar que el ANCHO es el que cambió se
    /// entraría en bucle. WPF trae e.WidthChanged para esto; el
    /// SizeChangedEventArgs de WinUI 3 solo trae NewSize/PreviousSize, así que
    /// la comparación se hace a mano — misma intención, distinto framework.</summary>
    private static void KeepAspect(FrameworkElement box, FrameworkElement badgeBar)
    {
        box.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width <= 0 || e.NewSize.Width == e.PreviousSize.Width)
                return;
            double barH = badgeBar.ActualHeight > 0
                ? badgeBar.ActualHeight : MonitorLayout.DefaultBarHeight;
            double border = box is Border b ? b.BorderThickness.Left + b.BorderThickness.Right : 0;
            double vertical = box is Border vb ? vb.BorderThickness.Top + vb.BorderThickness.Bottom : 0;
            box.Height = MonitorLayout.HeightForWidth(
                e.NewSize.Width, border, vertical + barH);
        };
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

    /// <summary>true si la salida ya está mostrada — mismo contrato que
    /// WinCue.App.IsVideoOutputVisible(screen), pero Secuence solo tiene UNA
    /// salida (sin el array _videoOutputs por pantalla de WinCue): el
    /// parámetro screen se ignora a propósito, mismo criterio que
    /// SurfaceDimensions. MainController.PrepareVideoStandby ya lo consulta
    /// para decidir si prepara el standby GPU o solo precarga clásico — antes
    /// de esto siempre devolvía false (la implementación por defecto de
    /// IShowView), así que la ruta híbrida de vídeo nunca llegaba a
    /// standby en Secuence.</summary>
    public bool IsVideoOutputVisible(int screen) => _videoOutput is { IsVisible: true };

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
