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
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using WinCue.Controller;
using WinCue.Core;
using WinCue.Presentation;

namespace Secuence.App;

public sealed partial class MainWindow : Window, IShowView
{
    private readonly CueModel _model;
    private readonly ObservableCollection<CueRow> _rows = new();
    private readonly SettingsManager _settings = SettingsManager.Instance;
    private MainController? _controller;
    private int _playheadIndex;

    // ── Salida de vídeo (ver el bloque IShowView de más abajo) ──────────
    private VideoOutputWindow? _videoOutput;
    private bool _blackout;

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

        Title = "Secuence";
        _model = model;

        GoIcon.Text = Icons.Play;
        PauseIcon.Text = Icons.Pause;
        StopAllIcon.Text = Icons.Stop;
        FadeAllIcon.Text = Icons.Fade;
        PanicIcon.Text = Icons.Warning;
        BlackoutIcon.Text = Icons.BlackFrame;

        CueList.ItemsSource = _rows;
        ActiveCuesList.ItemsSource = Array.Empty<ActiveCueRow>();
        EstadoTexto.Text = "Cargando show…";
    }

    /// <summary>Conecta el controlador (llamado por App tras construir motores
    /// y controlador — mismo orden que WinCue.App.App.xaml.cs.Attach).</summary>
    public void Attach(MainController controller)
    {
        _controller = controller;
        RebuildHotkeys();
        UpdateTransport(controller.IsPaused);

        _clockTimer = DispatcherQueue.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromMilliseconds(250);
        _clockTimer.Tick += (_, _) => RefreshShowClock();
        _clockTimer.Start();
    }

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
    }

    public void ReloadTable()
    {
        ReloadRowsFromModel();
        SetPlayhead(_playheadIndex);
        RebuildHotkeys();
    }

    public void AppendCueRow(Cue cue) => _rows.Add(new CueRow(cue, _model));

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
    }

    /// <summary>Sin VU real en esta tarea a propósito (el mezclador es otra
    /// pieza del roadmap, explícitamente fuera de este encargo). El audio SÍ
    /// suena — esto solo es que no hay un medidor que pintar todavía.</summary>
    public void UpdateVuMeter(float left, float right) { }

    /// <summary>Mismo motivo que UpdateVuMeter: sin strip de mezclador ni
    /// inspector, no hay dónde pintar el VU de un cue concreto.</summary>
    public void UpdateCueVuMeter(int cueId, float left, float right) { }

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
