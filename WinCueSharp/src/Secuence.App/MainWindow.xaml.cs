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
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
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
    private bool _blackout;

    /// <summary>Modo canvas, tamaño del lienzo y recortes por salida — el
    /// MISMO cálculo que usa el motor (WinCue.Controller.CanvasLayout), no una
    /// segunda lectura de settings a mano. MainController lleva el suyo en
    /// privado, así que esta ventana construye otro sobre los mismos dos
    /// argumentos (ajustes + vista): el espejo del PGM tiene que recomponer
    /// EXACTAMENTE el lienzo que ven las salidas, y para eso hay que leer los
    /// recortes con la misma regla.</summary>
    private readonly CanvasLayout _canvas;

    /// <summary>Selector de qué mira el espejo del PGM. Vive en la cabecera de
    /// color que el monitor ya tiene (PgmHeader): no se inventa disposición
    /// nueva para él. Lo puebla ConstruirSelectorPgm.</summary>
    private readonly ComboBox _pgmFuente = new();

    /// <summary>true mientras ConstruirSelectorPgm repuebla el desplegable:
    /// evita que el SelectedIndex que él mismo pone se tome por una elección
    /// del operador y reescriba el ajuste.</summary>
    private bool _repoblandoSelectorPgm;

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

    /// <summary>Buffer de «ir a cue» tecleando su número (estilo QLab).
    /// DECLARADO AQUÍ, en la tanda de la navegación por teclado, aunque quien
    /// lo RELLENA es la tarea siguiente (<c>TratarBufferIrACue</c>, aún sin
    /// escribir): la resolución de <c>Enter</c> en <see cref="OnKeyDownHotkeys"/>
    /// ya necesita comprobar si está vacío HOY, para que esa tarea no tenga
    /// que reordenar nada — ver el porqué completo ahí. Mientras no exista
    /// quien lo rellene, se queda vacío siempre y esa comprobación es un
    /// no-op deliberado, no un descuido.</summary>
    private string _bufferIrACue = "";

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

        // EL TECLADO, EN DOS ENGANCHES Y NO EN UNO, A PROPÓSITO:
        //
        //   · TRANSPORTE (Espacio, Escape) en el evento TÚNEL del Grid raíz.
        //     Tiene que ganarle al control con foco, y ése es todo el arreglo
        //     — ver OnPreviewKeyDownGlobal para el porqué medido.
        //     handledEventsToo: true por si algún día alguien marca la tecla
        //     más arriba; siendo el preview de la raíz, en la práctica siempre
        //     llega sin marcar.
        //
        //   · HOTKEYS POR CUE en el KeyDown que BURBUJEA y SIN
        //     handledEventsToo, para que una tecla que el control con foco ya
        //     ha consumido (las flechas de la tabla, las de un deslizador) no
        //     dispare nada. Ver OnKeyDownHotkeys: juntarlos en el preview mató
        //     la navegación de la tabla, y está medido.
        RootGrid.AddHandler(UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(OnPreviewKeyDownGlobal), handledEventsToo: true);
        RootGrid.KeyDown += OnKeyDownHotkeys;

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
        LogButtonIcon.Text = Icons.Log;
        ToolTipService.SetToolTip(LogButton, Loc.T(
            "Historial de avisos y mensajes de estado",
            "History of warnings and status messages"));
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
        // que hospeda a mpv y para las DOS ramas del PGM (espejo del
        // compositor / mpv propio).
        _pgm = new LiveMonitorControl(_hwndPropio, PgmInner, PgmHeader, PgmMessage, PgmStatus);
        _pvw = new LiveMonitorControl(_hwndPropio, PvwInner, PvwHeader, PvwMessage, PvwStatus);

        // El PGM espeja: le damos el plan (qué salidas y en qué hueco del
        // lienzo) y el selector de qué mira. Solo el PGM — el PVW enseña el
        // cue en STANDBY, que por definición aún NO sale.
        _canvas = new CanvasLayout(_settings, this);
        _pgm.PedirPlanEspejo = PlanEspejoPgm;
        // Un espejo que no arranca tiene que verse donde el operador mira, no
        // solo en logs/video-output.log — mismo reparto que el AvisoEnPantalla
        // de la ventana de salida.
        _pgm.AvisoEnPantalla = FlashStatus;
        ConstruirSelectorPgm();
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
        var rows = SelectedRows();
        if (rows.Count == 0)
            return;
        _controller.OnGroupSelected(rows);
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
    /// inspector — ver _suppressSelectionSync.
    ///
    /// DELIBERADAMENTE NO TOCA EL PLAYHEAD, y es la mitad del modelo de esta
    /// tanda: la selección es lo que el operador está MIRANDO (puebla el
    /// inspector), el playhead es lo que disparará el próximo GO, y un clic
    /// simple no puede confundir el uno con el otro — así se puede repasar el
    /// cue 47 en mitad del acto sin que el GO deje de disparar el 12. Quien
    /// mueve el playhead son los cinco gestos de <see cref="OnPreviewKeyDownGlobal"/>,
    /// <see cref="OnKeyDownHotkeys"/> y <see cref="OnCueListDoubleTapped"/> —
    /// nunca este manejador. Si alguna vez alguien añade aquí una llamada a
    /// <c>OnSetPlayhead</c>, es un bug, no una mejora.</summary>
    private void OnCueSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionSync)
            return;

        // Con selección múltiple, el inspector enseña la fila que se ACABA de
        // tocar, no la primera de la lista: al extender con Mayús hacia
        // arriba, SelectedItem sería la de más arriba y el inspector saltaría
        // a un cue que el operador no ha señalado.
        var focus = e.AddedItems.OfType<CueRow>().LastOrDefault()
            ?? CueList.SelectedItems.OfType<CueRow>().LastOrDefault();

        if (focus is not null)
        {
            _selectedCueId = focus.CueId;
            _inspector.SetCue(_model.GetById(focus.CueId));
            _timeline.SetSelected(focus.CueId);   // resalta (y trae a la vista)
        }
        else
        {
            _selectedCueId = -1;
            _inspector.SetCue(null);
            _timeline.SetSelected(-1);
        }
    }

    /// <summary>Las filas seleccionadas como ÍNDICES del modelo, ordenadas.
    /// Es lo que esperan las acciones del controlador que trabajan sobre
    /// varias (agrupar, copiar, duplicar, borrar) — mismo contrato que la
    /// proyección que hace WinCue sobre CueGrid.SelectedItems.</summary>
    private List<int> SelectedRows() =>
        CueList.SelectedItems.OfType<CueRow>()
            .Select(r => _model.IndexOfId(r.CueId))
            .Where(i => i >= 0)
            .Distinct()
            .OrderBy(i => i)
            .ToList();

    /// <summary>La fila «de referencia» para las acciones de una sola: la del
    /// cue que el inspector está enseñando, o la primera seleccionada.</summary>
    private int PrimaryRow()
    {
        int row = _selectedCueId >= 0 ? _model.IndexOfId(_selectedCueId) : -1;
        if (row >= 0)
            return row;
        var rows = SelectedRows();
        return rows.Count > 0 ? rows[0] : -1;
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
        // Doble clic en la regla del timeline: reproducir desde el cursor.
        // Qué arranca y con cuánto retraso lo decide el cálculo compartido
        // (TimelineCursor.PlayItems); aquí solo se entrega al controlador.
        _timeline.PlayFromRequested = items => controller.PlayFromTimeline(items);
        // Cursor de edición del timeline -> scrub en el monitor PVW, igual que
        // WinCue. El PVW es el de PREPARACIÓN: mirar un fotograma ahí no toca
        // la sala.
        _timeline.EditScrubbed += (videoCue, mediaSeconds) =>
        {
            if (videoCue is { FilePath.Length: > 0 })
                _pvw.ShowPaused(videoCue.FilePath, mediaSeconds);
        };
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

    /// <summary>PGM = lo que SALE. Con el compositor GPU encendido la salida ya
    /// está compuesta en el grafo de su SalidaGpu: el monitor la ESPEJA (pila
    /// entera de pistas, opacidades y cruces incluidos) y no decodifica nada.
    /// Sin compositor —ruta clásica HWND, y también HAP/ProRes por sus
    /// pipelines propios— no hay nada que espejar y el monitor reproduce el
    /// archivo por su cuenta, mudo, exactamente como hace WinCue en ese caso.
    ///
    /// Y un tercer caso, que es el que hace que el monitor no mienta: con el
    /// cue al aire EN el compositor pero en otra salida distinta de la que el
    /// PGM mira, el hueco dice «sin señal» en vez de caer a decodificar.</summary>
    private void UpdateProgramMonitor()
    {
        var info = _controller?.GetProgramVideo();
        if (info is null)
        {
            _cueEnPantalla = -1;
            if (_pgm.CurrentPath.Length > 0 || _pgm.Espejando)
                _pgm.Clear("NEGRO");
            _avisoPgm = "";
            return;
        }
        _cueEnPantalla = info.Value.CueId;
        _pgm.RefrescarEspejo(System.IO.Path.GetFileName(info.Value.FilePath));
        if (_pgm.Espejando)
            return;

        // SIN ESPEJO. Dos motivos DISTINTOS, y confundirlos es hacer mentir al
        // monitor:
        //
        //   · El cue al aire VA por el compositor, pero no por la fuente que el
        //     PGM está mirando. Por esa salida no está saliendo nada, y hay que
        //     DECIRLO. Caer aquí a decodificar el vídeo de programa sería
        //     enseñar en el hueco de la «Salida 3» algo que la Salida 3 no está
        //     dando — el mismo engaño que el blackout, con otra cara.
        //
        //   · El cue NO va por el compositor (compositor apagado, ruta clásica
        //     HWND, HAP o ProRes por sus pipelines propios). Entonces no hay
        //     nada que espejar de ninguna salida, y el monitor decodifica su
        //     copia muda exactamente como hace WinCue en ese caso. Esa rama es
        //     la de abajo y NO se toca.
        // AVERÍA del espejo (su HWND o su swapchain no nacieron): la sala está
        // bien, lo roto es la fontanería de NUESTRO monitor. Se cae a la rama
        // clásica sin más — negarle imagen al operador por esto sería peor, y
        // LiveMonitorControl ya le ha puesto el aviso en la barra de estado.
        // NO es el caso de «sin señal», que es otro: ahí la salida que mira
        // realmente no tiene compositor.
        if (!_pgm.EspejoAveriado && CueConCapaDeCompositor(_cueEnPantalla))
        {
            string aviso = TextoSinSenal();
            // Mismo criterio que la rama de NEGRO: no se re-limpia en cada tic
            // si el aviso ya está puesto — Clear para el mpv del monitor, y
            // pararlo cada medio segundo es una llamada al core para nada.
            if (_pgm.CurrentPath.Length > 0 || _pgm.Espejando || _avisoPgm != aviso)
            {
                _pgm.Clear(aviso);
                _avisoPgm = aviso;
            }
            return;
        }

        _avisoPgm = "";
        // Mismo archivo → solo re-sincroniza; archivo nuevo → carga y arranca ahí.
        _pgm.ShowVideo(info.Value.FilePath, info.Value.Elapsed, loop: false);
    }

    /// <summary>Aviso que hay puesto ahora mismo en el hueco del PGM ("" = no
    /// hay). Evita re-limpiar el monitor en cada tic mientras el aviso no
    /// cambia.</summary>
    private string _avisoPgm = "";

    /// <summary>Lo que dice el hueco del PGM cuando por la fuente elegida no
    /// está saliendo nada. Sin adornos: qué se está mirando y que no hay
    /// señal.</summary>
    private string TextoSinSenal() =>
        _pgm.FuenteEspejo < 0
            ? Loc.T("LIENZO — sin señal", "CANVAS — no signal")
            : Loc.T($"Salida {_pgm.FuenteEspejo + 1} — sin señal",
                    $"Output {_pgm.FuenteEspejo + 1} — no signal");

    /// <summary>Cue que el PGM está mirando ahora mismo. Lo apunta
    /// UpdateProgramMonitor para que PlanEspejoPgm —que corre justo después,
    /// dentro de RefrescarEspejo— sepa de qué cue tiene que buscar capa sin
    /// volver a preguntárselo al controlador.</summary>
    private int _cueEnPantalla = -1;

    // ── El plan del espejo del PGM ────────────────────────────────────
    // Esta ventana es la ÚNICA que conoce las cuatro salidas y los ajustes,
    // así que es la que traduce «qué mira el operador» a «qué salidas, en qué
    // hueco del lienzo». LiveMonitorControl solo ejecuta ese plan.

    /// <summary>Salida GPU YA creada de una salida, o null. Nunca la
    /// materializa: ver VideoOutputWindow.SalidaGpuActual.</summary>
    private SalidaGpu? SalidaGpuDe(int output) =>
        output >= 0 && output < MaxVideoOutputs
            ? _videoOutputs[output]?.SalidaGpuActual : null;

    /// <summary>true si el cue al aire tiene capa de compositor en ALGUNA
    /// salida. Es la condición de la regla 6 del encargo: cuando no la tiene
    /// (ruta clásica, HAP, ProRes) el PGM sigue decodificando su copia muda,
    /// como hace WinCue. Equivalente de
    /// WinCue.App.VideoOutputWindow.HasCompositorSurface, con las cuatro
    /// salidas de una vez porque en modo canvas un cue vive en todas.</summary>
    private bool CueConCapaDeCompositor(int cueId)
    {
        for (int s = 0; s < MaxVideoOutputs; s++)
        {
            if (SalidaGpuDe(s) is not { } salida)
                continue;
            var capas = salida.Capas;
            for (int i = 0; i < capas.Count; i++)
                if (capas[i].CueId == cueId)
                    return true;
        }
        return false;
    }

    private (int LienzoW, int LienzoH,
        IReadOnlyList<LiveMonitorControl.RegionEspejo> Regiones)? PlanEspejoPgm()
    {
        if (_cueEnPantalla < 0 || !CueConCapaDeCompositor(_cueEnPantalla))
            return null;

        // LIENZO: se recompone el canvas entero colocando cada vista en su
        // hueco. Las vistas llegan YA RECORTADAS desde MpvGpuRenderer (cada
        // salida renderiza su propio recorte en su propia superficie), así que
        // aquí no hay recorte que calcular — solo colocación.
        if (_pgm.FuenteEspejo < 0 && _canvas.CanvasMode)
        {
            var regiones = new List<LiveMonitorControl.RegionEspejo>();
            foreach (var v in _canvas.CanvasViews())
                if (SalidaGpuDe(v.Screen) is { Compositor: not null } salida)
                    // El blackout viaja POR SALIDA: apagar la 2 deja negro SU
                    // trozo del lienzo y no toca el de las demás, así que el
                    // operador ve exactamente qué pantallas están a oscuras.
                    regiones.Add(new LiveMonitorControl.RegionEspejo(
                        salida, v.X, v.Y, v.W, v.H, EnBlackout(v.Screen)));
            if (regiones.Count == 0)
                return null;
            var (cw, ch) = _canvas.CanvasDims();
            // La ANFITRIONA es la PRIMERA región, la que presta su compositor
            // para dibujar el espejo entero. CanvasViews() viene ordenado por
            // salida, así que NORMALMENTE es la Salida 1 — pero no está
            // garantizado: el filtro de arriba descarta las salidas cuya
            // SalidaGpu todavía no existe, y la 1 puede ser una de ellas (un
            // cue que solo toca la 2, por ejemplo). El código aguanta ese caso
            // —anfitriona es quien quede primera, y su grafo va en vivo
            // mientras los demás van por instantánea—, así que aquí no hay
            // nada que arreglar; lo que había que arreglar era este comentario,
            // que prometía una garantía que nadie da.
            return (cw, ch, regiones);
        }

        // UNA SOLA SALIDA: el espejo es esa salida entera, sin recorte.
        int output = Math.Clamp(_pgm.FuenteEspejo < 0 ? 0 : _pgm.FuenteEspejo,
            0, MaxVideoOutputs - 1);
        if (SalidaGpuDe(output) is not { Compositor: not null } sola)
            return null;
        return (sola.Ancho, sola.Alto,
            new[]
            {
                new LiveMonitorControl.RegionEspejo(
                    sola, 0, 0, sola.Ancho, sola.Alto, EnBlackout(output)),
            });
    }

    /// <summary>true si ESA salida está en blackout. La verdad la tiene la
    /// ventana de salida (nace en negro si se abre durante un blackout), no el
    /// interruptor global de esta ventana — por eso se pregunta ahí.</summary>
    private bool EnBlackout(int output) =>
        output >= 0 && output < MaxVideoOutputs &&
        _videoOutputs[output] is { Blackout: true };

    // ── El selector de la cabecera del PGM ────────────────────────────

    /// <summary>Puebla el desplegable de la cabecera del PGM con lo que HOY se
    /// puede mirar: las salidas HABILITADAS (monitor asignado en Ajustes) y,
    /// SOLO con el modo canvas puesto, LIENZO. Por defecto LIENZO en modo
    /// canvas y SALIDA 1 si no. Se vuelve a llamar tras guardar Ajustes: ahí
    /// pueden haber cambiado tanto las salidas habilitadas como el modo.</summary>
    private void ConstruirSelectorPgm()
    {
        // El desplegable se monta la PRIMERA vez dentro de la cabecera que el
        // monitor ya tiene (PgmHeader), sin inventar disposición nueva: una
        // columna más en su Grid, entre el distintivo y el nombre del archivo.
        if (_pgmFuente.Parent is null && PgmHeader.Child is Grid cabecera)
        {
            cabecera.ColumnDefinitions.Insert(1,
                new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(PgmStatus, 2);
            _pgmFuente.FontSize = 10;
            _pgmFuente.MinWidth = 0;
            _pgmFuente.MinHeight = 0;
            _pgmFuente.Height = 20;
            _pgmFuente.Padding = new Thickness(6, 0, 6, 0);
            _pgmFuente.Margin = new Thickness(8, 0, 8, 0);
            _pgmFuente.BorderThickness = new Thickness(0);
            // Pastilla OSCURA sobre la cabecera roja, y no texto negro sobre
            // rojo: el desplegable abierto se pinta con el tema oscuro de
            // WinUI, así que un Foreground pensado para el rojo dejaría la
            // lista ilegible. Tokens de la paleta, ni un color literal.
            _pgmFuente.Background = Theme.DeepBgBrush;
            _pgmFuente.Foreground = Theme.TextPrimaryBrush;
            _pgmFuente.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_pgmFuente, 1);
            cabecera.Children.Add(_pgmFuente);
            _pgmFuente.SelectionChanged += OnPgmFuenteChanged;
        }

        // DOS NOCIONES DISTINTAS DE «SALIDA HABILITADA», y es a propósito:
        //   · el SELECTOR usa video_output_screen* (¿tiene monitor asignado?),
        //     el mismo criterio que el botón de salidas — es lo que el operador
        //     entiende por «esta salida existe».
        //   · las REGIONES del LIENZO salen de video_output_enabled*, que es lo
        //     que marca el editor de canvas de Ajustes → Vídeo y lo que lee el
        //     motor (CanvasLayout.CanvasViews).
        // Consecuencia conocida: una salida con monitor asignado pero SIN
        // marcar en el editor de canvas se ofrece en el desplegable y no aporta
        // región al lienzo. Es coherente —se puede mirar esa salida suelta,
        // simplemente no forma parte del canvas—, pero queda escrito para que
        // nadie «unifique» las dos claves creyendo que es un descuido.
        bool canvas = _canvas.CanvasMode;
        _fuentesPgm.Clear();
        if (canvas)
            _fuentesPgm.Add(-1);
        for (int s = 0; s < MaxVideoOutputs; s++)
            if (ConfiguredScreen(s) >= 0)
                _fuentesPgm.Add(s);
        if (_fuentesPgm.Count == 0)
            _fuentesPgm.Add(0);   // la Salida 1 siempre se puede nombrar

        // Guardado, saneado: LIENZO solo vale con el modo canvas puesto, y una
        // salida que ya no está habilitada cae a la primera de la lista.
        int guardado = _settings.Get("pgm_monitor_source", -1);
        if (!_fuentesPgm.Contains(guardado))
            guardado = _fuentesPgm[0];
        _pgm.FuenteEspejo = guardado;

        _repoblandoSelectorPgm = true;
        _pgmFuente.Items.Clear();
        foreach (int f in _fuentesPgm)
            _pgmFuente.Items.Add(f < 0
                ? Loc.T("LIENZO", "CANVAS")
                : Loc.T($"SALIDA {f + 1}", $"OUTPUT {f + 1}"));
        _pgmFuente.SelectedIndex = _fuentesPgm.IndexOf(guardado);
        _repoblandoSelectorPgm = false;
    }

    /// <summary>Valor (-1 = LIENZO, 0..3 = salida) de cada entrada del
    /// desplegable, en su mismo orden.</summary>
    private readonly List<int> _fuentesPgm = new();

    private void OnPgmFuenteChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_repoblandoSelectorPgm)
            return;
        int i = _pgmFuente.SelectedIndex;
        if (i < 0 || i >= _fuentesPgm.Count)
            return;
        _pgm.FuenteEspejo = _fuentesPgm[i];
        _settings.Set("pgm_monitor_source", _fuentesPgm[i]);
        // Sin esperar al tic: RefrescarEspejo mueve el Mirror al compositor de
        // la salida elegida (quitándoselo al anterior) y reconstruye la raíz.
        UpdateProgramMonitor();
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
        foreach (var w in _videoOutputs)
            w?.SetBlackout(_blackout);
        // El espejo del PGM se entera AHORA, no en el próximo tic de 500 ms:
        // la tecla B es un corte, y un monitor que tarda medio segundo en
        // apagarse enseña vídeo que la sala ya no está viendo.
        UpdateProgramMonitor();
        BlackoutButton.Background = _blackout ? Theme.WarnBrush : Theme.ControlBgBrush;
        FlashStatus(_blackout
            ? $"{Icons.BlackFrame} Blackout activado."
            : "Blackout levantado.");
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

    // ── Soltar archivos sobre la tabla ─────────────────────────────────
    // Equivalente de WinCue.App.OnGridDrop en su rama de FileDrop. El alta la
    // hace el MISMO camino compartido que el botón IMPORTAR
    // (CueListEditor.AddCuesFromFiles, que ya valida con MediaImporter y
    // decide audio o vídeo por extensión): aquí solo se sacan las rutas.
    //
    // En modo SHOW no se acepta el arrastre — el candado de SHOW es sobre la
    // LISTA (añadir / insertar / borrar / importar), y esto es añadir. Se
    // rechaza ya en el DragOver para que el cursor lo diga antes de soltar,
    // en vez de tragarse el gesto y no hacer nada.

    /// <summary>Formato propio del arrastre INTERNO de filas. Equivale al
    /// "WinCueRow" de WinCue: distingue reordenar de soltar archivos, para que
    /// un arrastre venido de fuera no pueda acabar moviendo un cue.</summary>
    private const string RowDragFormat = "SecuenceRow";

    /// <summary>Arrastrar una fila = REORDENAR. La lista NO reordena sola
    /// (CanReorderItems=False) a propósito: mutaría la colección a espaldas
    /// del modelo, y quien mueve un cue —con su deshacer— es el controlador.
    /// Aquí solo se marca de dónde sale.</summary>
    private void OnCueListDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (_controller is null || ModeToggle.IsChecked == true
            || e.Items.FirstOrDefault() is not CueRow row)
        {
            e.Cancel = true;   // en modo SHOW no se reordena
            return;
        }
        int from = _model.IndexOfId(row.CueId);
        if (from < 0)
        {
            e.Cancel = true;
            return;
        }
        e.Data.SetData(RowDragFormat, from);
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void OnCueListDragOver(object sender, DragEventArgs e)
    {
        if (_controller is null || ModeToggle.IsChecked == true)
            return;

        if (e.DataView.Contains(RowDragFormat))
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            if (e.DragUIOverride is not null)
                e.DragUIOverride.Caption = Loc.T("Mover cue", "Move cue");
            e.Handled = true;
            return;
        }

        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        e.AcceptedOperation = DataPackageOperation.Copy;
        if (e.DragUIOverride is not null)
        {
            e.DragUIOverride.Caption = Loc.T("Añadir cues", "Add cues");
            e.DragUIOverride.IsGlyphVisible = true;
        }
        e.Handled = true;
    }

    /// <summary>Fila bajo un punto de la lista, o -1. Equivalente de
    /// WinCue.App.RowIndexAt: se recorren los contenedores REALIZADOS, que
    /// son los únicos que tienen sitio en pantalla.</summary>
    private int RowIndexAt(Point p)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (CueList.ContainerFromIndex(i) is not FrameworkElement c)
                continue;
            var top = c.TransformToVisual(CueList).TransformPoint(new Point(0, 0));
            if (p.Y >= top.Y && p.Y <= top.Y + c.ActualHeight)
                return i;
        }
        return -1;
    }

    private async void OnCueListDrop(object sender, DragEventArgs e)
    {
        if (_controller is null || ModeToggle.IsChecked == true)
            return;

        // Reordenación interna primero, como en WinCue.App.OnGridDrop.
        if (e.DataView.Contains(RowDragFormat))
        {
            e.Handled = true;
            object raw = await e.DataView.GetDataAsync(RowDragFormat);
            if (raw is not int from)
                return;
            int to = RowIndexAt(e.GetPosition(CueList));
            if (to >= 0 && from != to)
                _controller.OnMoveCue(from, to);
            return;
        }

        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        // Deferral: GetStorageItemsAsync es asíncrono y sin esto el sistema da
        // por terminado el arrastre —y suelta el DataView— antes de que
        // lleguen las rutas.
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            // Solo archivos: soltar una CARPETA no añade nada, igual que en
            // WinCue (allí el FileDrop de una carpeta lo descarta
            // MediaImporter por extensión).
            var paths = items.OfType<StorageFile>().Select(f => f.Path).ToList();
            if (paths.Count > 0)
                _controller.AddCuesFromFiles(PrimaryRow(), paths);
        }
        finally { deferral.Complete(); }
    }

    // ── Menú contextual de la tabla (botón derecho) ────────────────────
    // Equivalente de WinCue.App.MainWindow.BuildGridMenu, con MenuFlyout en
    // vez de ContextMenu. Todas las acciones son llamadas al MISMO controlador
    // compartido: aquí no hay lógica de edición, solo el menú.
    //
    // DIFERENCIAS CON EL DE WINCUE, ninguna por descuido:
    //   · La tabla de Secuence es de selección ÚNICA, así que donde WinCue
    //     opera sobre el bloque seleccionado, aquí se opera sobre la fila.
    //   · No está «Normalizar sonoridad»: en WinCue es un método privado de su
    //     ventana, no del controlador. Copiarlo sería exactamente lo que
    //     prohíbe la regla 2; subirlo a compartido es una tarea aparte.
    //   · Los colores salen de CueChoices.Colors (compartido, el mismo que
    //     usan las muestras del inspector). WinCue los tiene escritos a mano
    //     en su submenú — esa lista es la que sobra, no esta.

    private void OnCueListRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_controller is null)
            return;
        // Si la fila bajo el puntero NO está en la selección, seleccionarla.
        // Si SÍ lo está, no se toca: el menú actúa sobre la selección, y un
        // clic derecho sobre un bloque ya elegido no puede deshacerlo (mismo
        // criterio que WinCue.App.OnGridRightClick).
        if ((e.OriginalSource as FrameworkElement)?.DataContext is CueRow row
            && !CueList.SelectedItems.Contains(row))
            CueList.SelectedItem = row;

        BuildCueMenu(PrimaryRow())
            .ShowAt(CueList, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            { Position = e.GetPosition(CueList) });
        e.Handled = true;
    }

    /// <summary>Doble clic en una fila: uno de los cinco gestos que SÍ mueven
    /// el playhead (ver la nota de <see cref="OnCueSelectionChanged"/>). El
    /// primer clic del par ya disparó SelectionChanged y pobló el inspector,
    /// como cualquier clic simple — este manejador solo AÑADE el salto de
    /// playhead encima, sin decidir selección.
    ///
    /// Mismo patrón que <see cref="OnCueListRightTapped"/> para encontrar la
    /// fila bajo el gesto: <c>e.OriginalSource</c> como <c>FrameworkElement</c>
    /// y su <c>DataContext</c> como <c>CueRow</c>, en vez de fiarse de
    /// <c>CueList.SelectedItem</c> (que con selección extendida podría no ser
    /// la fila que el operador acaba de tocar).</summary>
    private void OnCueListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_controller is null)
            return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not CueRow row)
            return;
        int idx = _model.IndexOfId(row.CueId);
        if (idx >= 0)
            _controller.OnSetPlayhead(idx);
    }

    private MenuFlyout BuildCueMenu(int row)
    {
        var menu = new MenuFlyout();
        bool edit = ModeToggle.IsChecked != true;
        var cue = row >= 0 ? _model.GetCue(row) : null;

        void Item(string text, string accel, Action onClick, bool enabled = true)
        {
            var mi = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            if (accel.Length > 0)
                ToolTipService.SetToolTip(mi, accel);
            mi.Click += (_, _) => onClick();
            menu.Items.Add(mi);
        }
        void Sep() => menu.Items.Add(new MenuFlyoutSeparator());

        if (cue is not null)
        {
            bool running = _controller!.RunningCueIds.Contains(cue.Id);
            Item(Loc.T("Disparar (sin mover playhead)", "Trigger (without moving playhead)"), "",
                () => _controller.TriggerCueId(cue.Id));
            if (cue.Type == CueType.Audio && cue.FilePath.Length > 0)
            {
                Item(Loc.T("Preview (cascos)", "Preview (headphones)"), "",
                    () => _controller.AuditionToggle(row));
                Item(Loc.T($"Normalizar sonoridad ({MediaImporter.TargetLufs:0} LUFS)",
                           $"Normalise loudness ({MediaImporter.TargetLufs:0} LUFS)"), "",
                    () => NormalizeLoudness(SelectedRows()), edit);
            }
            if (MainController.IsHapConvertible(cue))
                Item(Loc.T("Convertir a HAP (motor propio)", "Convert to HAP (native engine)"), "",
                    () => _controller.ConvertCueToHap(row), edit);
            Item(Loc.T("Pausar / reanudar", "Pause / resume"), "Ctrl+P",
                () => _controller.OnPauseCueToggle(row), running && cue.IsMedia());
            Item(Loc.T("Detener", "Stop"), "S",
                () => _controller.OnStopSelected(row), running);
            Sep();

            // Copiar / duplicar / borrar / agrupar operan sobre TODA la
            // selección, como en WinCue. Se toma aquí y no dentro del lambda
            // porque el menú puede seguir abierto cuando se pulsa y la
            // selección ya no tiene por qué ser la misma.
            var selRows = SelectedRows();
            Item(Loc.T("Copiar", "Copy"), "Ctrl+C", () => _controller.CopyCues(selRows));
            Item(Loc.T("Pegar debajo", "Paste below"), "Ctrl+V",
                () => _controller.PasteCues(row), edit);
            Item(Loc.T("Duplicar", "Duplicate"), "Ctrl+D",
                () => _controller.DuplicateCues(selRows), edit);
            Item(Loc.T("Eliminar", "Delete"), "Supr", () =>
            {
                // De abajo arriba: borrar la de arriba primero correría los
                // índices de las de abajo y se borraría lo que no toca.
                foreach (int r in selRows.OrderByDescending(i => i))
                    _controller.OnDeleteCue(r);
            }, edit);
            Sep();

            Item(Loc.T("Agrupar selección", "Group selection"), "Ctrl+G",
                () => _controller.OnGroupSelected(selRows), edit);
            if (cue.Type == CueType.Group)
            {
                Item(Loc.T("Desagrupar", "Ungroup"), "", () => _controller.OnUngroup(row), edit);
                Item(cue.Collapsed ? Loc.T("Expandir", "Expand") : Loc.T("Colapsar", "Collapse"),
                    "", () => _controller.OnToggleCollapse(row));
            }
            Sep();

            Item(Loc.T("Renombrar", "Rename"), "F2", () =>
            {
                SelectTab(1);              // pestaña del inspector
                _inspector.FocusName();
            }, edit);
            menu.Items.Add(ColorSubmenu(color => CommitRowField(row, "color", color), edit));
            Item(cue.Armed ? Loc.T("Desarmar", "Disarm") : Loc.T("Armar", "Arm"), "",
                () => CommitRowField(row, "armed", !cue.Armed), edit);
            Item(cue.Flagged ? Loc.T("Quitar flag", "Clear flag") : Loc.T("Poner flag", "Set flag"),
                "", () => CommitRowField(row, "flagged", !cue.Flagged), edit);
            Item(Loc.T("Renumerar todos (1..n)", "Renumber all (1..n)"), "",
                () => _controller.RenumberCues(), edit);
            Sep();
        }
        else
        {
            Item(Loc.T("Pegar al final", "Paste at end"), "Ctrl+V",
                () => _controller!.PasteCues(_model.Count - 1), edit);
        }

        menu.Items.Add(InsertSubmenu(row, edit));
        Item(Loc.T("Importar archivos…", "Import files…"), "",
            () => OnImportFilesClick(this, new RoutedEventArgs()), edit);
        if (_model.Cues.Any(MainController.IsHapConvertible))
            Item(Loc.T("Convertir todos los vídeos a HAP", "Convert all videos to HAP"), "",
                () => _controller!.ConvertAllVideosToHap(), edit);
        return menu;
    }

    /// <summary>Normalizar sonoridad: mide cada cue de audio y le pone la
    /// ganancia que lo deja en el objetivo. Mismo contrato que
    /// WinCue.App.NormalizeLoudness.
    ///
    /// La MEDIDA y la ganancia que implica son compartidas
    /// (MediaImporter.MeasureLufs / GainForTarget). Lo que queda aquí es el
    /// bucle: medir es LENTO —decodifica el archivo entero— así que va en
    /// segundo plano y va contando por la barra de estado, o parecería que la
    /// aplicación se ha colgado.</summary>
    private async void NormalizeLoudness(IReadOnlyList<int> rows)
    {
        if (_controller is null)
            return;
        var cues = rows.Select(r => _model.GetCue(r))
            .Where(c => c is { Type: CueType.Audio, FilePath.Length: > 0 })
            .Cast<Cue>().ToList();
        if (cues.Count == 0)
            return;

        int done = 0;
        foreach (var cue in cues)
        {
            FlashStatus(Loc.T(
                $"Midiendo sonoridad de «{cue.Name}»… ({++done}/{cues.Count})",
                $"Measuring loudness of “{cue.Name}”… ({done}/{cues.Count})"));
            double? lufs = await Task.Run(() => MediaImporter.MeasureLufs(cue.FilePath));
            if (lufs is null)
            {
                FlashStatus(Loc.T(
                    $"«{cue.Name}»: no se pudo medir (¿ffmpeg en PATH?)",
                    $"“{cue.Name}”: could not measure (is ffmpeg on PATH?)"));
                continue;
            }
            double gain = MediaImporter.GainForTarget(lufs.Value);
            // El cue puede haberse movido mientras se medía: se vuelve a
            // buscar su fila por id en vez de fiarse de la de antes.
            int row = _model.IndexOfId(cue.Id);
            if (row >= 0)
                CommitRowField(row, "gain_db", gain);
            FlashStatus($"«{cue.Name}»: {lufs:0.0} LUFS → {gain:+0.0;-0.0} dB");
        }
    }

    /// <summary>Submenú de color. La paleta, su orden y sus etiquetas son
    /// cálculo COMPARTIDO (CueChoices.Colors) — el mismo que pinta las
    /// muestras del inspector.</summary>
    private static MenuFlyoutSubItem ColorSubmenu(Action<string> apply, bool enabled)
    {
        var sub = new MenuFlyoutSubItem { Text = Loc.T("Color", "Colour"), IsEnabled = enabled };
        foreach (var choice in CueChoices.Colors)
        {
            string key = choice.Key;
            var mi = new MenuFlyoutItem { Text = choice.Label };
            mi.Click += (_, _) => apply(key);
            sub.Items.Add(mi);
        }
        return sub;
    }

    private MenuFlyoutSubItem InsertSubmenu(int row, bool enabled)
    {
        var sub = new MenuFlyoutSubItem
        { Text = Loc.T("Insertar cue", "Insert cue"), IsEnabled = enabled };
        foreach (var (label, type) in new (string, CueType)[]
        {
            (Loc.T("Audio", "Audio"), CueType.Audio), (Loc.T("Vídeo", "Video"), CueType.Video),
            (Loc.T("Espera", "Wait"), CueType.Wait), (Loc.T("Fundido", "Fade"), CueType.Fade),
            (Loc.T("Iniciar", "Start"), CueType.Start), (Loc.T("Parar", "Stop"), CueType.Stop),
            ("Script", CueType.Script), (Loc.T("Nota", "Memo"), CueType.Memo),
            (Loc.T("Grupo", "Group"), CueType.Group),
        })
        {
            var t = type;
            var mi = new MenuFlyoutItem { Text = label };
            mi.Click += (_, _) => _controller?.OnInsertCue(t, row);
            sub.Items.Add(mi);
        }
        return sub;
    }

    /// <summary>Commit de un campo que NO viene del inspector (menú
    /// contextual). Reapunta el inspector después, porque el valor que el
    /// operador acaba de cambiar tiene que verse allí — igual que
    /// WinCue.App.CommitRowField.</summary>
    private void CommitRowField(int row, string key, object? value)
    {
        _controller?.OnCueEditCommitted(row,
            new Dictionary<string, object?> { [key] = value });
        _inspector.SetCue(_model.GetCue(row));
    }

    /// <summary>Inserta el tipo de cue elegido bajo la fila seleccionada —
    /// mismo contrato que WinCue.App.MainWindow.xaml.cs.OnAddCue: pasa por
    /// OnInsertCue, no AddNewCue (WinCue.App tampoco llama a AddNewCue desde
    /// su UI, solo desde tests).</summary>
    private void OnAddCue(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string tag } && Enum.TryParse<CueType>(tag, out var type))
            _controller?.OnInsertCue(type, PrimaryRow());
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
        _controller?.AddCuesFromFiles(PrimaryRow(), files.Select(f => f.Path).ToList());
    }

    /// <summary>Elimina el cue seleccionado — mismo contrato que
    /// WinCue.App.OnDeleteCue (CueListEditor.OnDeleteCue ya para el cue si
    /// estaba sonando antes de borrarlo).</summary>
    private void OnDeleteCueClick(object sender, RoutedEventArgs e)
    {
        // De abajo arriba, por lo mismo que en el menú contextual: borrar la
        // de arriba primero correría los índices de las de abajo.
        foreach (int row in SelectedRows().OrderByDescending(i => i))
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
            if (await MostrarDialogoAsync(dialog) != ContentDialogResult.Primary)
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
    /// <summary>Menú de salidas, equivalente del de WinCue.App.
    /// OnVideoOutputClick: un submenú por salida HABILITADA en Ajustes, con
    /// mostrar/ocultar e identificar, y debajo las acciones globales.
    ///
    /// Antes esto era un interruptor de una sola salida, y por eso «no se
    /// podía elegir la salida»: no había dónde.
    ///
    /// Una salida deshabilitada (monitor −1) NO aparece: así no se lanza sin
    /// querer sobre el monitor de control.</summary>
    private void OnVideoOutButtonClick(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();

        for (int s = 0; s < MaxVideoOutputs; s++)
        {
            if (ConfiguredScreen(s) < 0)
                continue;
            int idx = s;
            var sub = new MenuFlyoutSubItem
            {
                Text = Loc.T($"Salida {idx + 1}", $"Output {idx + 1}"),
            };

            bool visible = _videoOutputs[idx] is { IsVisible: true };
            var mostrar = new MenuFlyoutItem
            {
                Text = visible ? Loc.T("Ocultar", "Hide") : Loc.T("Mostrar", "Show"),
            };
            mostrar.Click += (_, _) =>
            {
                if (_videoOutputs[idx] is { IsVisible: true } abierta)
                {
                    abierta.Hide();
                }
                else
                {
                    var w = EnsureVideoOutputWindow(idx);
                    w.EnsureVisibleHandle();
                    PlaceVideoOutput(idx);
                }
                UpdateVideoOutButton();
            };
            sub.Items.Add(mostrar);

            var identificar = new MenuFlyoutItem
            {
                Text = Loc.T("Identificar", "Identify"),
            };
            identificar.Click += (_, _) => IdentifyOutput(idx);
            sub.Items.Add(identificar);

            menu.Items.Add(sub);
        }

        if (menu.Items.Count == 0)
        {
            // Todas deshabilitadas: decirlo, en vez de abrir un menú vacío.
            var aviso = new MenuFlyoutItem
            {
                Text = Loc.T("Sin salidas habilitadas — ver Ajustes → Vídeo",
                             "No outputs enabled — see Settings → Video"),
                IsEnabled = false,
            };
            menu.Items.Add(aviso);
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var identVisibles = new MenuFlyoutItem
        {
            Text = Loc.T("Identificar salidas visibles", "Identify visible outputs"),
        };
        identVisibles.Click += (_, _) =>
        {
            for (int s = 0; s < MaxVideoOutputs; s++)
                if (_videoOutputs[s] is { IsVisible: true })
                    IdentifyOutput(s);
        };
        menu.Items.Add(identVisibles);

        var black = new ToggleMenuFlyoutItem
        {
            Text = Loc.T("Blackout (B)", "Blackout (B)"),
            IsChecked = _blackout,
        };
        black.Click += (_, _) => ToggleBlackout();
        menu.Items.Add(black);

        menu.Items.Add(new MenuFlyoutSeparator());

        var ocultarTodas = new MenuFlyoutItem
        {
            Text = Loc.T("Ocultar todas las salidas", "Hide all outputs"),
        };
        ocultarTodas.Click += (_, _) =>
        {
            foreach (var w in _videoOutputs)
                w?.Hide();
            UpdateVideoOutButton();
        };
        menu.Items.Add(ocultarTodas);

        menu.ShowAt(VideoOutButton);
    }

    /// <summary>Enseña el rótulo con el número de la salida en su monitor —
    /// la respuesta a «¿cuál de mis pantallas es esta?».</summary>
    private void IdentifyOutput(int output)
    {
        var w = EnsureVideoOutputWindow(output);
        w.EnsureVisibleHandle();
        PlaceVideoOutput(output);
        w.ShowIdentify(OutColors[output]);
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
        bool active = _videoOutputs.Any(w => w is { IsVisible: true });
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

        if (await MostrarDialogoAsync(dialog) != ContentDialogResult.Primary)
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
            await MostrarDialogoAsync(info);
        }

        // Reconfigurar EN CALIENTE — mismo reparto que
        // WinCue.App.MainWindow.OnSettingsClick (dlg.Saved): dispositivo de
        // audio (ApplyEngineSettings, fijado por App.xaml.cs), volumen
        // master, y la salida de vídeo si ya está abierta (recolocarla en el
        // monitor recién elegido, sin esperar al siguiente GO).
        ApplyEngineSettings?.Invoke();
        _controller?.OnMasterVolume(_settings.Get("master_volume", 100));
        _mixer.SetMasterFader(_settings.Get("master_volume", 100));
        // Recolocar TODAS las salidas ya abiertas en el monitor recién
        // elegido, sin esperar al siguiente GO.
        for (int s = 0; s < MaxVideoOutputs; s++)
            if (_videoOutputs[s] is { IsVisible: true })
                PlaceVideoOutput(s);
        UpdateVideoOutButton();
        // El selector del PGM depende de lo que se acaba de guardar: qué
        // salidas están habilitadas y si el modo canvas sigue puesto (LIENZO
        // solo se ofrece con él). Y el espejo, de si el compositor sigue vivo:
        // ApplyEngineSettings ha podido soltar todos los compositores.
        ConstruirSelectorPgm();
        UpdateProgramMonitor();
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
        return await MostrarDialogoAsync(dialog) == ContentDialogResult.Primary;
    }

    // ==================================================================
    // Diálogos modales y los huecos de vídeo — la mitigación del airspace
    // ==================================================================

    /// <summary>Cuántos diálogos modales hay abiertos AHORA. Lo leen DOS cosas,
    /// y las dos importan en directo:
    ///   · la mitigación del airspace (MostrarDialogoAsync, más abajo): con un
    ///     diálogo delante los HWND de los monitores se apartan;
    ///   · el teclado (OnPreviewKeyDownGlobal / OnKeyDownHotkeys): con un
    ///     diálogo delante, ni el transporte ni los hotkeys tocan nada — el
    ///     ContentDialog vive en el MISMO XamlRoot y el preview del Grid raíz
    ///     le vería las teclas antes que él.
    ///
    /// Contador y no booleano por PRUDENCIA, no por un caso conocido: hoy
    /// ningún camino de Secuence llega a tener dos a la vez —el aviso de
    /// frecuencia de muestreo de Ajustes se abre DESPUÉS de que el primero se
    /// haya cerrado, así que ahí el contador nunca pasa de 1—, pero un
    /// booleano convierte cualquier anidamiento futuro en monitores destapados
    /// sobre un diálogo y en un Escape que para el show. Es dos líneas más y
    /// quita un modo de fallo entero.</summary>
    private int _dialogosAbiertos;

    /// <summary>Abre un ContentDialog CON LOS HUECOS DE VÍDEO APARTADOS.
    ///
    /// POR QUÉ: los previos PGM/PVW son ventanas hijas Win32 que se pintan por
    /// encima del XAML de esta ventana (ver HwndHijo). Está fotografiado que,
    /// donde el diálogo se solapa con ellos, el vídeo se pinta ENCIMA del
    /// diálogo — se come los rótulos y las casillas—, y que el velo de
    /// atenuación del ContentDialog no atenúa los huecos (luma 75,64 con el
    /// diálogo abierto, la misma que sin él). Con la ventana a 1440 px no se
    /// solapan; a 980 px, y en la pantalla de 1024×600 del autor, sí. Sin esto,
    /// Ajustes es inusable.
    ///
    /// Se ocultan los hijos —el mismo camino que ya usan Clear y el blackout— y
    /// se restauran al cerrar. Los popups de WinUI 3 (menús, flyouts, el
    /// desplegable del propio PGM) NO necesitan esto: van por su propia capa,
    /// por encima del puente, y está medido que se siguen viendo.</summary>
    private async Task<ContentDialogResult> MostrarDialogoAsync(ContentDialog dialog)
    {
        if (_dialogosAbiertos++ == 0)
        {
            _pgm.OcultarPorDialogo();
            _pvw.OcultarPorDialogo();
        }
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            if (--_dialogosAbiertos == 0)
            {
                _pgm.MostrarTrasDialogo();
                _pvw.MostrarTrasDialogo();
            }
        }
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

    /// <summary>Historial de la barra de estado. El tope, el sello de hora y
    /// qué cuenta como aviso son cálculo COMPARTIDO
    /// (WinCue.Presentation.StatusLog), el mismo que usa WinCue.</summary>
    private readonly StatusLog _statusHistory = new();
    private DispatcherQueueTimer? _statusTimer;

    /// <summary>Mensaje en la barra. Se borra solo a los 4 segundos —los
    /// mismos que WinCue— y queda apuntado en el historial: un aviso que salta
    /// mientras el operador mira otro sitio no puede perderse.</summary>
    public void FlashStatus(string message)
    {
        EstadoTexto.Text = message;

        _statusTimer ??= CreateStatusTimer();
        _statusTimer.Stop();
        _statusTimer.Start();

        _statusHistory.Add(message, DateTime.Now);
        if (StatusLog.IsWarning(message))
            LogButton.Foreground = Theme.StandbyBrush;
    }

    private DispatcherQueueTimer CreateStatusTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(4);
        timer.Tick += (_, _) => { EstadoTexto.Text = ""; timer.Stop(); };
        return timer;
    }

    /// <summary>Abre el historial. Va en un Flyout y no en un ContentDialog
    /// porque no es una decisión que haya que contestar: se mira y se cierra
    /// pinchando fuera. Al abrirlo se apaga el aviso del botón.</summary>
    private void OnShowStatusLog(object sender, RoutedEventArgs e)
    {
        LogButton.Foreground = Theme.TextDimBrush;

        var text = new TextBox
        {
            Text = _statusHistory.Render(
                Loc.T("(sin mensajes todavía)", "(no messages yet)")),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Theme.WindowBgBrush,
            Foreground = Theme.TextPrimaryBrush,
            BorderThickness = new Thickness(0),
            FontSize = 12,
        };
        var flyout = new Flyout
        {
            Content = new ScrollViewer
            {
                Width = 560,
                Height = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = text,
            },
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top,
        };
        flyout.ShowAt(LogButton);
    }

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
        // Se AÑADE el reemplazo a SelectedItems en vez de asignar
        // SelectedItem: con selección múltiple, asignar la colapsaría a una
        // sola fila y editar un campo desharía el bloque que el operador
        // acababa de marcar.
        var old = _rows[index];
        bool wasSelected = CueList.SelectedItems.Contains(old);

        _suppressSelectionSync = true;
        try
        {
            _rows[index] = refreshed;
            if (wasSelected && !CueList.SelectedItems.Contains(refreshed))
                CueList.SelectedItems.Add(refreshed);
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
    /// lista cambia (es parte de IShowView).
    ///
    /// REGRESIÓN CONOCIDA, DEJADA A PROPÓSITO — no la "arregles" aquí: un cue
    /// cuya hotkey sea una flecha (<c>"Up"</c>/<c>"Down"</c>/<c>"Left"</c>/
    /// <c>"Right"</c>, las cuatro se parsean bien y entran en el diccionario)
    /// YA NO DISPARA. Desde que ↑/←/↓/→ son transporte
    /// (<see cref="OnPreviewKeyDownGlobal"/>, caso 3), el túnel se come esas
    /// cuatro teclas —y las marca <c>Handled</c>— antes de que lleguen a
    /// <see cref="OnKeyDownHotkeys"/>, que es quien mira este diccionario. En
    /// WinCue sí disparan, porque allí los hotkeys se resuelven ANTES que las
    /// flechas de playhead, dentro del mismo despachador túnel.
    ///
    /// No se excluyen aquí las cuatro flechas de <c>_cueHotkeys</c> para
    /// compensar: hacerlo cambiaría en silencio lo que significa un show YA
    /// GUARDADO con esa hotkey, y la razón de fondo por la que el transporte
    /// va por encima de los hotkeys es precisamente que un archivo de show no
    /// pueda desactivarle al operador la navegación del playhead — permitir
    /// la excepción inversa (que un hotkey desactive el transporte) sería la
    /// misma trampa al revés. La mitigación es que esto no se descubra en
    /// mitad de una función: la Task 6 avisa al cargar un show cuyos hotkeys
    /// pisen teclas de transporte, y las cuatro flechas están en esa
    /// lista.</summary>
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

    /// <summary>EL TRANSPORTE (Espacio y Escape), en el evento TÚNEL.
    ///
    /// NO es el único enganche de teclado de esta ventana, y decir lo contrario
    /// —como decía este comentario— contradice al constructor treinta líneas
    /// más abajo: los HOTKEYS POR CUE van aparte, en el KeyDown que burbujea
    /// (<see cref="OnKeyDownHotkeys"/>), y el porqué medido de esa separación
    /// está allí. Aquí solo viven las dos teclas que tienen que ganarle al
    /// control con foco pase lo que pase.
    ///
    /// Equivalente WinUI 3 del <c>PreviewKeyDown += OnKeyDownGlobal</c> de
    /// WinCue (WinCue.App/MainWindow.xaml.cs:72), portado con su guardián de
    /// escritura por delante.
    ///
    /// POR QUÉ EN <c>PreviewKeyDown</c> Y NO EN <c>KeyDown</c> NI EN UN
    /// <c>KeyboardAccelerator</c> — está medido, no supuesto:
    ///
    /// En WinUI 3 los aceleradores de un ANCESTRO se procesan DESPUÉS del
    /// <c>OnKeyDown</c> interno del control con foco. <c>ButtonBase</c> atiende
    /// Espacio y Enter (es su forma nativa de pulsarse) y marca
    /// <c>Handled</c>; <c>ListView</c> atiende Espacio y las flechas. Con los
    /// dos atajos declarados como acelerador del Grid raíz, el GO no llegaba
    /// nunca mientras el foco estuviera en un botón o en la tabla: la barra
    /// pulsaba lo último que hubiera tocado el operador (BLACKOUT, PANIC, el
    /// interruptor de MODO…). Y por el mismo mecanismo al revés, el
    /// <c>TextBox</c> NO marca el Espacio como Handled, así que ahí el
    /// acelerador SÍ llegaba: escribir un espacio en el nombre de un cue
    /// disparaba el GO del show, y Escape dentro de un campo lo paraba entero.
    ///
    /// El evento túnel viaja de la RAÍZ hacia el elemento con foco, así que
    /// este método ve la tecla antes que nadie y, al marcar <c>Handled</c>, el
    /// control con foco ni se entera. Comprobado empíricamente antes de
    /// escribir esto, en <c>tools/winui3-lab/SpikeTeclado.cs</c>: con el foco
    /// en un Button y el preview marcando Handled, el <c>Click</c> del botón
    /// NO llega; con el preview callado, la tecla sigue su curso normal y el
    /// espacio se escribe en el TextBox.
    ///
    /// ENTER SE QUEDA FUERA A PROPÓSITO. Interceptar Espacio le quita al
    /// usuario de teclado la forma nativa de activar el botón enfocado — es el
    /// precio que WinCue ya paga hoy—, y dejar Enter fuera mantiene Tab+Enter
    /// como ruta de activación accesible.
    ///
    /// ALCANCE: la tanda que dio origen a este método solo arreglaba Espacio y
    /// Escape (y de paso los hotkeys por cue, que sufrían el mismo fallo, ver
    /// más abajo). Las flechas de playhead (↑/←, ↓/→) SÍ están aquí desde la
    /// tanda siguiente — ver el caso 3 del switch — con el mismo argumento de
    /// «tiene que ganarle al control con foco» que ya valía para Espacio y
    /// Escape. Lo que sigue sin estar es el resto de los 18 atajos de WinCue
    /// que faltan —S, B, P, Supr, F2, ir a cue por número, Ctrl+Z/Y/C/V/D/P/F—.
    ///
    /// Ctrl+O / Ctrl+S / Ctrl+Shift+S / Ctrl+G siguen siendo
    /// <c>KeyboardAccelerator</c> (ver el constructor): ningún control se come
    /// esas combinaciones, así que la fase tardía les llega igual de bien.</summary>
    private void OnPreviewKeyDownGlobal(object sender, KeyRoutedEventArgs e)
    {
        // ── 0 · CON UN DIÁLOGO MODAL DELANTE, EL TECLADO NO ES NUESTRO ──
        // MEDIDO, y es la trampa que este preview abrió: el ContentDialog de
        // Ajustes vive en el MISMO XamlRoot, así que el preview del Grid raíz ve
        // sus teclas ANTES que él. Con Ajustes abierto, Escape disparaba
        // STOP ALL Y ADEMÁS dejaba el diálogo abierto — o sea que abrir Ajustes
        // en mitad de una función y pulsar Escape para cerrarlo paraba el show.
        // Antes de esta tanda no pasaba: el KeyboardAccelerator llegaba tarde y
        // el ContentDialog se quedaba su Escape, que es lo correcto.
        //
        // El contador es el MISMO que usa la mitigación de los monitores
        // (MostrarDialogoAsync): mientras algo modal esté delante, el
        // transporte no se mete.
        if (_controller is null || _dialogosAbiertos > 0)
            return;

        object? foco = FocusManager.GetFocusedElement(Content.XamlRoot);

        // ── 1a · DESPLEGABLE ABIERTO ──
        // Mientras la lista de un ComboBox está desplegada, sus teclas son
        // suyas: Escape la cierra, Espacio elige. Se sale SIN tocar el foco —
        // a diferencia del caso de escritura de abajo, moverlo aquí cerraría
        // el desplegable de una forma que el operador no ha pedido.
        if (EsDesplegableAbierto(foco))
            return;

        // ── 1b · GUARDIÁN DE ESCRITURA. VA PRIMERO, Y NO ES NEGOCIABLE ──
        // Si el foco está donde se escribe, este despachador sale ENTERO: en un
        // preview que marca Handled, el carácter no llega a escribirse (medido
        // en el spike, caso C). Sin esto el problema se invierte y la app se
        // come la escritura.
        //
        // Escape es la única excepción, y hace lo MISMO que en WinCue: devolver
        // el foco a la tabla. NO para el show — que es justo lo que hacía
        // Secuence hasta ahora desde dentro de un TextBox. No se marca
        // Handled, igual que WinCue: quien tenga el foco puede seguir usando su
        // Escape (el inspector lo usa para revertir un campo).
        if (EsCampoDeEscritura(foco))
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
                CueList.Focus(FocusState.Programmatic);
            return;
        }

        // ── 2 · Sin modificadores ──
        // Un Ctrl+S no puede disparar el cue de la «S», y Ctrl+O/S/G tienen que
        // seguir llegando a sus aceleradores.
        if (IsDown(Windows.System.VirtualKey.Control)
            || IsDown(Windows.System.VirtualKey.Menu)
            || IsDown(Windows.System.VirtualKey.Shift))
            return;

        // ── 3 · Los atajos de directo y el playhead ──
        // ↑/← y ↓/→ son TRANSPORTE, igual que Espacio/Escape: tienen que
        // ganarle a la navegación nativa de la tabla (que hasta esta tanda
        // era la dueña de esas teclas), y por eso van AQUÍ y no en
        // OnKeyDownHotkeys — mismo argumento, ya medido más abajo, de por qué
        // Espacio/Escape viven en el túnel.
        //
        // Enter TAMBIÉN es un gesto de playhead (Enter con el foco en la
        // tabla) pero, a propósito, NO vive en este switch — ver el porqué
        // completo en OnKeyDownHotkeys, justo donde se resuelve.
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Space:
                _controller.OnGoPressed();
                e.Handled = true;
                return;
            case Windows.System.VirtualKey.Escape:
                _controller.OnStopPressed();
                e.Handled = true;
                return;
            case Windows.System.VirtualKey.Up:
            case Windows.System.VirtualKey.Left:
                _controller.OnPrevPressed();
                e.Handled = true;
                return;
            case Windows.System.VirtualKey.Down:
            case Windows.System.VirtualKey.Right:
                _controller.OnNextPressed();
                e.Handled = true;
                return;
        }

        // ── 4 · Los hotkeys por cue NO se despachan aquí: ver OnKeyDownHotkeys,
        //        y el porqué medido de que vayan en otro sitio.
    }

    /// <summary>Los hotkeys por cue (Disparadores → Hotkey), y NO en el preview
    /// de arriba. La diferencia es deliberada, y está medida.
    ///
    /// LOS ATAJOS DE DIRECTO Y LOS HOTKEYS NO SON LA MISMA CLASE DE TECLA.
    /// Espacio y Escape son TRANSPORTE: tienen que ganarle siempre al control
    /// con foco, porque una barra que hace algo distinto según dónde esté el
    /// foco es una barra de la que el operador no se puede fiar en una función.
    /// Por eso viven en el preview y marcan <c>Handled</c>. Un hotkey por cue,
    /// en cambio, es ADITIVO: es una comodidad sobre una tecla cualquiera, y no
    /// puede quitarle a quien tiene el foco una tecla que ese control necesita
    /// para funcionar.
    ///
    /// EL CONTRATO ES «LA TECLA QUE EL CONTROL CON FOCO YA HA CONSUMIDO NO
    /// DISPARA NADA», y por eso este manejador va en el <c>KeyDown</c> que
    /// BURBUJEA y SIN <c>handledEventsToo</c>: aquí solo llegan las teclas que
    /// nadie ha querido. Ése era el contrato que el código viejo PRETENDÍA
    /// tener y no tenía —se registraba con <c>handledEventsToo: true</c> y
    /// acto seguido hacía <c>if (e.Handled) return</c>, que anulaba justo eso—;
    /// la contradicción se resuelve quitando la BANDERA, no la comprobación.
    /// Meterlo todo en el preview la resolvía al revés y se llevaba por delante
    /// la navegación de la tabla y la del único <c>Slider</c> de la ventana
    /// (la barra de progreso del previo PVW/PGM — Secuence no tiene mezclador
    /// con deslizadores, así que no es ese el ejemplo que aplica aquí).
    ///
    /// Con un hotkey en la flecha ABAJO y la tabla enfocada, tres resultados
    /// en tres momentos distintos de este archivo — hay que mantener los tres
    /// escritos porque cada uno fue real en su momento, pero NO los tres son
    /// del mismo tipo de evidencia, y hay que decirlo con esas palabras:
    ///   · MEDIDO — con los hotkeys metidos en el preview (primer intento,
    ///     descartado): el cue SE DISPARA y la selección se queda clavada en
    ///     0 — la navegación, muerta;
    ///   · MEDIDO — con los hotkeys aquí (KeyDown sin handledEventsToo) y las
    ///     flechas TODAVÍA sin ser transporte: la flecha MUEVE la selección
    ///     (fila 0 → 1) y el cue NO se dispara — el ListView se la come y la
    ///     marca Handled;
    ///   · RAZONADO POR LECTURA DEL CÓDIGO, PENDIENTE DE COMPROBAR EN LA
    ///     PASADA MANUAL (NO MEDIDO con una tecla real) — HOY, con las
    ///     flechas ya convertidas en transporte en el preview de arriba
    ///     (<see cref="OnPreviewKeyDownGlobal"/>, caso 3): el preview marca
    ///     <c>Handled</c> sin excepción por foco, y este método se suscribe
    ///     sin <c>handledEventsToo</c>, así que el evento no debería llegar
    ///     aquí — ni se movería la selección ni se dispararía el cue. Es la
    ///     regresión de los hotkeys en tecla de flecha que se documenta en
    ///     <see cref="RebuildHotkeys"/>, y que se deja así a propósito; falta
    ///     confirmarla con teclado real (ver el informe de la tanda).
    ///
    /// El type-ahead de la tabla NO entra en esta decisión, y conviene que
    /// quede escrito porque parece que sí: está medido en los DOS binarios que
    /// **no funciona** —teclear una letra no mueve la selección ni antes ni
    /// después de la tanda—, porque todas las filas comparten la misma
    /// representación de texto (<c>CueRow</c> no sobrescribe <c>ToString()</c>).
    /// O sea que hoy no hay ninguna colisión «buscar vs. disparar» que
    /// resolver. El día que la tabla tenga búsqueda de verdad habrá que decidir
    /// de quién es la letra, y esa decisión será de producto, no de teclado.
    ///
    /// ══ ESTO ES UNA DIVERGENCIA CONSCIENTE CON WinCue, Y SIGUE SIÉNDOLO ══
    ///
    /// En WinCue los hotkeys por cue NO viven en un manejador aparte. Están
    /// dentro del MISMO despachador túnel (<c>OnKeyDownGlobal</c>,
    /// WinCue.App/MainWindow.xaml.cs:2126), o sea en la posición que aquí se
    /// descartó. WinCue puede permitírselo porque su tabla NUNCA VE LAS
    /// FLECHAS: ↑ y ↓ los reclama el propio despachador para mover el
    /// playhead (<c>OnPrevPressed</c>/<c>OnNextPressed</c>, <c>:2171-2178</c>)
    /// y los marca <c>Handled</c>, así que en WinCue no hay navegación de
    /// <c>DataGrid</c> que proteger: se la quitó a propósito.
    ///
    /// Secuence YA TIENE ↑/←/↓/→ de playhead (<see
    /// cref="OnPreviewKeyDownGlobal"/>, caso 3, misma tanda que este
    /// comentario) con exactamente ese criterio, y con ellas convertidas en
    /// transporte la tabla de Secuence TAMPOCO ve ya las flechas — igual que
    /// en WinCue. Esta es la decisión que el propio comentario, en su versión
    /// anterior, dejaba pendiente para "cuando esa tanda llegara": llegó, y
    /// la pregunta que responde es «si la tabla ya no necesita protegerse de
    /// las flechas, ¿por qué seguir separando los hotkeys del preview, en vez
    /// de fundirlos ahí como hace WinCue?».
    ///
    /// DECISIÓN, TOMADA EN ESTA MISMA TANDA: SE MANTIENEN SEPARADOS. El
    /// motivo ya no es la tabla — es que un hotkey puede caer en CUALQUIER
    /// tecla, no solo en las cuatro flechas, y el contrato «la tecla que el
    /// control con foco ya ha consumido no dispara nada» sigue vivo para
    /// TODAS las demás: una letra mientras se edita el nombre de un cue en el
    /// inspector, o una tecla que un control futuro necesite para su propio
    /// gesto. Fundir los hotkeys en el preview le devolvería a cualquier
    /// control con foco el mismo riesgo que esta separación existe para
    /// evitar —perder una tecla propia porque coincide con el hotkey de
    /// algún cue del show—, solo que ahora sobre las 34 teclas restantes en
    /// vez de sobre las cuatro flechas. Que las flechas concretas ya no
    /// necesiten esa protección no hace desaparecer la necesidad para el
    /// resto.</summary>
    private void OnKeyDownHotkeys(object sender, KeyRoutedEventArgs e)
    {
        // Con un diálogo modal delante, tampoco: teclear en Ajustes no puede
        // disparar cues. Mismo motivo y mismo contador que en el preview.
        if (_controller is null || _dialogosAbiertos > 0)
            return;

        // El MISMO guardián, y por el mismo motivo: sin él, teclear el nombre
        // de un cue dispararía los cues cuyas teclas coincidan con sus letras.
        object? foco = FocusManager.GetFocusedElement(Content.XamlRoot);
        if (EsDesplegableAbierto(foco) || EsCampoDeEscritura(foco))
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
            return;
        }

        // ── Enter: se resuelve en ESTE ÚNICO sitio, y a propósito no en el
        // túnel de arriba (OnPreviewKeyDownGlobal) ──
        //
        // El plan original de esta tanda ponía Enter junto a las flechas, en
        // el transporte. Se corrigió a mano porque choca de frente con la
        // tarea siguiente («ir a cue» tecleando su número): ese buffer
        // también resuelve Enter, y tiene que verlo ANTES que nadie cuando
        // hay dígitos escritos — si Enter se resolviera en el túnel (que
        // llega primero por diseño, ver arriba), teclear «5» «.» «1» y Enter
        // saltaría a la SELECCIÓN en vez de al cue 5.1: el túnel se habría
        // comido la tecla antes de que el buffer llegara a verla.
        //
        // La regla completa, en un solo sitio:
        //   · buffer de «ir a cue» con dígitos  → salta a ESE cue (lo hace
        //     TratarBufferIrACue, tarea siguiente — se enganchará AQUÍ MISMO,
        //     antes de esta comprobación, y devolverá true/Handled primero);
        //   · buffer vacío y el foco está en la tabla → playhead a la fila
        //     seleccionada (lo de abajo);
        //   · si no (buffer vacío y el foco NO está en la tabla) → Enter
        //     sigue siendo de quien tenga el foco, sin marcar Handled. Es lo
        //     que mantiene viva la ruta Tab+Enter sobre un botón: al vivir en
        //     el KeyDown que BURBUJEA (sin handledEventsToo, como los
        //     hotkeys de arriba), un botón enfocado ya ha consumido su Enter
        //     —e invocado su Click— ANTES de que este método llegue a verlo.
        //
        // _bufferIrACue vive declarado más arriba desde ESTA tanda aunque
        // nadie lo rellena todavía (ver su comentario): hoy la primera rama
        // de la regla nunca se alcanza, y eso es correcto, no un hueco.
        if (e.Key == Windows.System.VirtualKey.Enter
            && _bufferIrACue.Length == 0
            && EstaEnLaTabla(foco))
        {
            if (CueList.SelectedItem is CueRow fila)
            {
                int idx = _model.IndexOfId(fila.CueId);
                if (idx >= 0)
                    _controller.OnSetPlayhead(idx);
            }
            e.Handled = true;
        }
    }

    /// <summary>¿El elemento con foco es la tabla de cues, o algo dentro de
    /// ella (una fila, una celda)? NO basta con comparar por tipo contra
    /// <c>ListView</c>: en WinUI, navegar una lista con el teclado deja el
    /// foco real en el <c>ListViewItem</c> concreto, no en el <c>ListView</c>
    /// contenedor — y tras esta misma tanda (↑/↓ ya son transporte, ver
    /// OnPreviewKeyDownGlobal) el clic sigue siendo la única forma de
    /// enfocar una fila, así que es el caso que hay que cubrir. Por eso se
    /// sube por el árbol visual hasta encontrar <c>CueList</c> o quedarse sin
    /// padre.</summary>
    private bool EstaEnLaTabla(object? foco)
    {
        for (var actual = foco as DependencyObject; actual is not null;
             actual = VisualTreeHelper.GetParent(actual))
        {
            if (ReferenceEquals(actual, CueList))
                return true;
        }
        return false;
    }

    /// <summary>¿Hay una lista desplegada AHORA MISMO? Mientras la haya, sus
    /// teclas son suyas (Escape cierra, Espacio elige) y ni el transporte ni
    /// los hotkeys se meten. Con el desplegable abierto el foco puede estar en
    /// el <c>ComboBoxItem</c> o seguir en el propio <c>ComboBox</c>, así que se
    /// miran los dos.</summary>
    private static bool EsDesplegableAbierto(object? foco) =>
        foco is ComboBoxItem || foco is ComboBox { IsDropDownOpen: true };

    /// <summary>¿El foco está donde el despachador NO debe meterse? Se mira el
    /// TIPO del elemento con foco, no una lista de teclas — mismo criterio que
    /// WinCue. <c>NumberBox</c> y <c>AutoSuggestBox</c> no hacen falta por
    /// separado (envuelven un <c>TextBox</c>, y el que tiene el foco es el de
    /// dentro); van igualmente por si algún día el foco cae en el envoltorio, y
    /// con ellos <c>RichEditBox</c>/<c>PasswordBox</c>, que hoy no existen en
    /// Secuence, para que aparecer no cueste otra auditoría.
    ///
    /// LA REGLA ENTERA DE LOS <c>ComboBox</c>, QUE NO ES «COMBO SÍ / COMBO NO»
    /// SINO «TRANSPORTE SÍ / EDICIÓN NO»:
    ///
    /// La ventana principal tiene DIEZ desplegables —siete en el inspector, dos
    /// en el editor de lienzo y el selector de fuente del PGM— y no se parecen
    /// en nada más que en el tipo:
    ///
    ///   · <c>_pgmFuente</c> (CANVAS / OUTPUT 1..4) vive PEGADO a los monitores,
    ///     en la zona de transporte, y el operador lo toca DURANTE la función.
    ///     Tras elegir fuente con el ratón el foco se queda ahí, y con el
    ///     guardián puesto la barra abría el desplegable en vez de dar GO:
    ///     exactamente el patrón que esta tanda existe para matar, reaparecido
    ///     en la zona donde más duele. Ese, y solo ese, queda EXENTO.
    ///
    ///   · Los otros nueve son controles de EDICIÓN: se usan en modo EDIT,
    ///     preparando el show, con el operador tecleando parámetros de un cue.
    ///     Ahí Espacio es el gesto NATIVO para abrir un desplegable, y
    ///     exceptuarlos convertía ese gesto en un GO al aire. Eso es peor que el
    ///     problema que la excepción venía a resolver, así que para ellos el
    ///     guardián SIGUE EN PIE.
    ///
    /// Por eso la frontera es la FUNCIÓN del control y no su tipo, y por eso
    /// esto compara por referencia contra <c>_pgmFuente</c> en vez de preguntar
    /// «¿eres un ComboBox cerrado?». Si algún día hay más desplegables en la
    /// barra de transporte, se añaden AQUÍ, uno a uno y a sabiendas.
    ///
    /// (Un combo EDITABLE nunca queda exento: ahí sí se escribe. Y el caso del
    /// desplegable ABIERTO lo resuelve antes <see cref="EsDesplegableAbierto"/>,
    /// que vale para los diez.)</summary>
    private bool EsCampoDeEscritura(object? foco) =>
        foco is TextBox or AutoSuggestBox or RichEditBox or PasswordBox or NumberBox
             || (foco is ComboBox cb && !EsSelectorDeTransporte(cb));

    /// <summary>El único desplegable que NO es de edición: el selector de fuente
    /// del PGM. Ver la regla completa en <see cref="EsCampoDeEscritura"/>.</summary>
    private bool EsSelectorDeTransporte(ComboBox cb) =>
        ReferenceEquals(cb, _pgmFuente) && !cb.IsEditable;

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
    // ── SALIDAS DE VÍDEO ───────────────────────────────────────────────
    // CUATRO, como WinCue, y no una: cada cue de vídeo elige la suya en el
    // inspector (Geometría → Salida) y el campo ya viajaba en el JSON
    // (Cue.VideoOutput). Lo que faltaba era que alguien lo mirara: el
    // IShowView por defecto devuelve SIEMPRE la misma ventana, así que
    // elegir salida no hacía nada. Al implementar GetVideoOutputLayerHandle
    // por pantalla, ese campo pasa a significar algo.
    //
    // Las ventanas se crean PEREZOSAMENTE: una salida que ningún cue usa no
    // materializa nada, y una «Deshabilitada» en Ajustes (monitor −1) ni
    // siquiera aparece en el botón.
    private const int MaxVideoOutputs = 4;
    private readonly VideoOutputWindow?[] _videoOutputs = new VideoOutputWindow?[MaxVideoOutputs];

    /// <summary>Colores por salida, para identificar y para el rótulo.
    /// CanvasEditorLayout.OutputColors (WinCue.Presentation), no un array
    /// propio: antes había un segundo juego de colores por salida solo aquí,
    /// que no coincidía con el que pinta el editor de canvas de
    /// Ajustes → Vídeo (CanvasEditorPanel.cs) — mismos cuatro en los dos
    /// sitios ahora, por construcción.</summary>
    private static readonly Windows.UI.Color[] OutColors =
        CanvasEditorLayout.OutputColors.Select(Theme.Hex).ToArray();

    /// <summary>Clave de settings del monitor de cada salida. La 1 conserva
    /// el nombre histórico para no romper shows/ajustes ya guardados — mismo
    /// esquema que WinCue.App.ScreenSettingKey.</summary>
    private static string ScreenSettingKey(int screen) =>
        screen == 0 ? "video_output_screen" : $"video_output_screen{screen + 1}";

    /// <summary>Monitor configurado para una salida, o −1 si está
    /// deshabilitada. La 1 vale 0 por defecto; las demás nacen apagadas, para
    /// que nadie lance vídeo sobre el monitor de control sin pedirlo.</summary>
    private int ConfiguredScreen(int output) =>
        _settings.Get(ScreenSettingKey(output), output == 0 ? 0 : -1);

    private VideoOutputWindow EnsureVideoOutputWindow(int output)
    {
        if (_videoOutputs[output] is { } existente)
            return existente;
        var w = new VideoOutputWindow { OutputNumber = output + 1 };
        // Un fallo del compositor de esa salida (un swapchain que no se puede
        // recrear) tiene que verse en la barra de estado, como cualquier otro
        // aviso del motor — no solo en logs/video-output.log.
        w.AvisoEnPantalla = FlashStatus;
        _videoOutputs[output] = w;
        return w;
    }

    /// <summary>Coloca una salida en su monitor. Una salida deshabilitada
    /// (−1) cae a la principal en vez de reventar: si un cue apunta a ella,
    /// más vale verlo en algún sitio que no verlo — mismo criterio que
    /// WinCue.App.PlaceVideoOutput.</summary>
    private void PlaceVideoOutput(int output)
    {
        int mon = ConfiguredScreen(output);
        _videoOutputs[output]?.PlaceOnScreen(mon < 0 ? 0 : mon);
        if (_blackout)
            _videoOutputs[output]?.SetBlackout(true);
    }

    public nint GetVideoOutputHandle() => GetVideoOutputLayerHandle(0, 0);

    /// <summary>El HWND donde tiene que pintar un cue, SEGÚN SU SALIDA. Es la
    /// pieza que hacía falta: sin esto, el IShowView por defecto mandaba todo
    /// a la misma ventana y el campo «Salida» del inspector era decorativo.
    ///
    /// La capa se ignora — Secuence no tiene compositor GPU (fase 2b), así que
    /// cada salida es UNA superficie, la clásica de mpv.</summary>
    public nint GetVideoOutputLayerHandle(int screen, int layer)
    {
        int output = Math.Clamp(screen, 0, MaxVideoOutputs - 1);
        var w = EnsureVideoOutputWindow(output);
        bool firstOpen = !w.IsVisible;
        nint handle = w.EnsureVisibleHandle();
        if (firstOpen)
        {
            PlaceVideoOutput(output);
            w.SetBlackout(_blackout);   // la que nace durante un blackout, nace en negro
            UpdateVideoOutButton();
        }
        return handle;
    }

    /// <summary>Superficie del COMPOSITOR GPU para un slot del pool de
    /// VideoEngine (salida, pista, secuencia). Es el proveedor que App.xaml.cs
    /// enchufa con SetGpuLayerSurfaceProvider cuando Ajustes → Vídeo tiene el
    /// compositor encendido; con él, el cue ya no pinta en un HWND propio sino
    /// en un NODO del grafo de su salida, y por fin hay opacidad de verdad
    /// entre pistas.
    ///
    /// Misma resolución de salida que GetVideoOutputLayerHandle (el índice se
    /// acota a las cuatro) y mismo ritual de primera apertura: colocar en su
    /// monitor, heredar el blackout y refrescar el botón.
    ///
    /// LANZA si el puente no se puede montar, y es lo que hay que hacer: la
    /// única pieza que sabe caer a la ruta clásica es VideoEngine, y solo se
    /// entera por una excepción (marca _gpuFailed, avisa y reintenta el cue
    /// por el camino de siempre).</summary>
    public IVideoLayerSurface ObtenerSuperficieCompositor(int screen, int layer, int slotSeq)
    {
        int output = Math.Clamp(screen, 0, MaxVideoOutputs - 1);
        var w = EnsureVideoOutputWindow(output);
        bool firstOpen = !w.IsVisible;
        _ = w.EnsureVisibleHandle();
        if (firstOpen)
        {
            // ANTES de crear el swapchain: PlaceOnScreen mueve la ventana a
            // su monitor y la pone a pantalla completa, y el swapchain nace
            // con el tamaño que el HWND hijo tenga en ese momento. Al revés
            // nacería con el tamaño provisional y habría que recrearlo en el
            // acto.
            PlaceVideoOutput(output);
            w.SetBlackout(_blackout);   // la que nace durante un blackout, nace en negro
            UpdateVideoOutButton();
        }
        return w.NuevaCapa(layer, slotSeq);
    }

    /// <summary>Opacidad (0..1) del vídeo de un cue, con fundido opcional.
    /// Secuence la implementa POR PRIMERA VEZ aquí: hasta ahora se quedaba
    /// con el cuerpo vacío por defecto de IShowView, así que la opacidad de un
    /// cue de vídeo y los cruces de vídeo no hacían absolutamente nada. Solo
    /// tiene efecto con el compositor encendido — en la ruta clásica por HWND
    /// no hay opacidad posible (airspace), igual que en WinCue.
    ///
    /// Un cue puede verse en varias salidas (modo canvas): cada una funde su
    /// capa si la tiene; donde no haya compositor, no-op.</summary>
    public void SetVideoCueOpacity(int cueId, double opacity, double fadeSeconds = 0.0)
    {
        foreach (var w in _videoOutputs)
            w?.FijarOpacidadCue(cueId, opacity, fadeSeconds);
    }

    /// <summary>El GO sube el cue al frente de su pista. Con la precarga en
    /// STANDBY, la capa entra en el grafo mucho antes de salir al aire y puede
    /// quedar por debajo de vídeos que arrancaron después — mismo motivo (y
    /// mismo remedio) que RaiseCueToFront en WinCue.</summary>
    public void RaiseVideoCueToFront(int cueId)
    {
        foreach (var w in _videoOutputs)
            w?.SubirCueAlFrente(cueId);
    }

    /// <summary>Desengancha el espejo del PGM. Es lo PRIMERO de todo al apagar
    /// el compositor en caliente, ANTES incluso de que el motor dispone las
    /// fuentes: mientras el espejo siga puesto, el hilo del compositor
    /// anfitrión recorre —hasta medio segundo más— instantáneas de nodos de
    /// otras salidas, y esos nodos apuntan a fuentes que
    /// DiscardCompositorPipelines está a punto de liberar. El porqué entero,
    /// en LiveMonitorControl.SoltarEspejo.</summary>
    public void SoltarEspejoPgm() => _pgm.SoltarEspejo();

    /// <summary>Apaga el compositor GPU de TODAS las salidas (Ajustes → Vídeo
    /// lo desmarcó, ver App.xaml.cs). No basta con quitarle el proveedor al
    /// motor: el swapchain seguiría presentando sobre el HWND hijo que la ruta
    /// clásica va a volver a darle a mpv como wid, y dos dueños del mismo HWND
    /// es negro garantizado.</summary>
    public void LiberarCompositoresGpu()
    {
        foreach (var w in _videoOutputs)
            w?.LiberarSalidaGpu();
    }

    /// <summary>true si ESA salida está mostrada. MainController.
    /// PrepareVideoStandby lo consulta para decidir si prepara standby.</summary>
    public bool IsVideoOutputVisible(int screen) =>
        screen >= 0 && screen < MaxVideoOutputs && _videoOutputs[screen] is { IsVisible: true };

    /// <summary>Dimensiones de la salida 1 ya abierta; 1920×1080 (mismo valor
    /// por defecto que FakeView en los tests del controlador) mientras no
    /// exista ventana.</summary>
    public (double Width, double Height) SurfaceDimensions() =>
        _videoOutputs[0] is { IsVisible: true } w ? w.ClientSize : (1920.0, 1080.0);

    /// <summary>Testigo: nombre del vídeo en pantalla, o null = NEGRO. Vive
    /// en la barra de estado de ESTA ventana (el operador), no en la salida
    /// — mismo reparto que TallyLabel en WinCue.App.</summary>
    public void SetOutputContent(string? videoName) =>
        TallyLabel.Text = videoName is null
            ? $"{Icons.BlackFrame} SALIDA: NEGRO"
            : $"{Icons.Video} SALIDA: {videoName}";

    /// <summary>Cierre real de TODAS las salidas (App.xaml.cs, window.Closed).
    /// Antes de esto, VideoOutputWindow.Closing las oculta en vez de
    /// destruirlas: mpv puede seguir apuntando a su HWND hijo.</summary>
    public void CloseVideoOutput()
    {
        foreach (var w in _videoOutputs)
            w?.CloseForShutdown();
    }
}
