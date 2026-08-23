// =============================================================================
// VideoOutputWindow.xaml.cs — Ventana de salida de vídeo (destino de mpv)
// =============================================================================
// Sustituye al stub de NullVideoEngine: mpv recibe el HWND de la ventana hija
// de ESTA ventana (wid) y pinta ahí. Se coloca SIEMPRE a pantalla completa en
// el monitor que Settings → Vídeo tiene elegido (video_output_screen,
// SettingsManager) — nadie la arrastra, ni en ensayo ni en función (palabras
// de Andrés sobre cómo funciona WinCue de verdad, ver el encargo). Por eso
// esta ventana NO tiene modo "ventana" ni geometría que recordar: eso era
// código extra de WinCue.App.VideoOutputWindow (WPF) para un caso que este
// producto no necesita.
//
// COLOCACIÓN EN PANTALLA — equivalente de WinUI 3 a Screen.AllScreens de WPF:
//   Microsoft.UI.Windowing.DisplayArea. El índice de settings.json sigue
//   siendo "0 = principal, ya lo que sea fuera de rango = principal" (mismo
//   contrato que WPF): se resuelve por DisplayArea.Primary explícitamente en
//   vez de fiarse del orden de DisplayArea.FindAll(), que no tiene por qué
//   coincidir con el de Screen.AllScreens (y las dos APIs comparten
//   settings.json). Los índices >0 caen sobre las NO-principales en el orden
//   que da FindAll(): con dos monitores (el caso real de Andrés) solo hay una
//   posibilidad, así que la ambigüedad de orden no llega a pesar aquí.
//
// PANTALLA COMPLETA: AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen)
// solo fullscreen-ea de forma fiable sobre el monitor donde la ventana YA
// está — por eso PlaceOnScreen mueve/redimensiona la ventana (en modo
// ventana) al monitor de destino ANTES de pedir el presentador FullScreen.
//
// PÍXELES FÍSICOS: a diferencia de WPF (que tiene que esquivar la conversión
// DIP↔px a mano con SetWindowPos, ver el comentario de WinCue.App), el propio
// AppWindow de WinUI 3 ya expone posición y tamaño en píxeles FÍSICOS
// (Position, Size, ClientSize) — nada que convertir aquí.
//
// CAMBIO DE TOPOLOGÍA (aviso del hito 0): un monitor que desaparece a media
// función (cable, no arrastre) es el mismo tipo de evento para Windows que
// arrastrar entre monitores — y eso, en el lab, dejó la ventana pegada, en
// negro un instante y el vídeo cortado y recuperado. No se puede reproducir
// sin un escenario real, así que esta clase se construye a la DEFENSIVA:
// cualquier llamada a DisplayArea/AppWindow que falle se registra (LogAviso)
// y cae a la pantalla principal en vez de tirar la app; OnAppWindowChanged
// vuelve a sincronizar el tamaño del HWND hijo ante CUALQUIER cambio de
// tamaño del AppWindow, sea porque el operador lo pidió o porque Windows
// recolocó la ventana solo al perder un monitor.
// =============================================================================

using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Secuence.App;

public sealed partial class VideoOutputWindow : Window
{
    private readonly nint _hwndPropio;
    private readonly AppWindow _appWindow;
    private nint _hwndHijo;
    private bool _blackout;
    private bool _shutdown;

    public VideoOutputWindow()
    {
        InitializeComponent();
        Title = "Secuence — Salida de vídeo";

        _hwndPropio = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwndPropio);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        // Sin barra de título ni bordes en NINGÚN momento: esta ventana no se
        // ve nunca "en modo ventana" de cara al público — solo existe a
        // pantalla completa o, brevemente, mientras se coloca en su monitor.
        if (_appWindow.Presenter is OverlappedPresenter overlapped)
            overlapped.SetBorderAndTitleBar(false, false);

        _appWindow.Changed += OnAppWindowChanged;

        // Cerrarla (Alt+F4, o el propio Windows durante un cambio de
        // topología) no debe destruirla: mpv puede seguir apuntando a su
        // HWND hijo. Se oculta, igual que WinCue.App.VideoOutputWindow (WPF)
        // hace en su OnClosing.
        _appWindow.Closing += (_, args) =>
        {
            if (_shutdown)
                return;
            args.Cancel = true;
            try { _appWindow.Hide(); }
            catch (Exception ex) { LogAviso($"AppWindow.Hide() en Closing falló: {ex.Message}"); }
        };
    }

    /// <summary>Aviso a disco (no a consola: esta ventana puede vivir sin
    /// nadie mirando una terminal). Mismo espíritu que DumpCrash de
    /// App.xaml.cs — un fallo de colocación de pantalla sin rastro es
    /// imposible de diagnosticar después de la función.</summary>
    private static void LogAviso(string mensaje)
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "video-output.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {mensaje}\r\n");
        }
        catch (Exception) { /* si ni loguear se puede, no hay más que hacer */ }
    }

    /// <summary>HWND de la ventana hija (0 si aún no se ha creado). Es lo que
    /// IShowView.GetVideoOutputHandle() acaba devolviendo a MainController.</summary>
    public nint ChildHandle => _hwndHijo;

    /// <summary>true si la ventana de salida ya está mostrada. MainWindow lo
    /// usa para saber si es la PRIMERA vez que se pide el HWND (momento en el
    /// que hay que colocarla en su monitor) o una posterior.</summary>
    public bool IsVisible => _appWindow.IsVisible;

    /// <summary>Tamaño del área cliente en píxeles físicos (para
    /// IShowView.SurfaceDimensions — la geometría CUSTOM de un cue).</summary>
    public (double Width, double Height) ClientSize
    {
        get
        {
            var s = _appWindow.ClientSize;
            return (s.Width, s.Height);
        }
    }

    /// <summary>Garantiza que la ventana está visible y su HWND hijo existe;
    /// devuelve el HWND hijo (el wid que se le pasa a mpv). Idempotente: en
    /// llamadas posteriores solo devuelve el handle ya creado.</summary>
    public nint EnsureVisibleHandle()
    {
        if (!_appWindow.IsVisible)
        {
            try { _appWindow.Show(); }
            catch (Exception ex) { LogAviso($"AppWindow.Show() falló: {ex.Message}"); }
        }

        if (_hwndHijo == 0)
        {
            var size = _appWindow.ClientSize;
            _hwndHijo = HwndHijo.Crear(_hwndPropio, Math.Max(1, size.Width), Math.Max(1, size.Height));
            if (_blackout)
                HwndHijo.Ocultar(_hwndHijo);
        }

        return _hwndHijo;
    }

    /// <summary>Resuelve el índice de settings.json a un DisplayArea real.
    /// 0 (o cualquier valor fuera de rango) → SIEMPRE la principal, explícito
    /// y no por casualidad de orden — ver la cabecera del archivo.</summary>
    private static DisplayArea ResolveDisplayArea(int screenIndex)
    {
        var primary = DisplayArea.Primary;
        if (screenIndex <= 0)
            return primary;

        try
        {
            var otras = DisplayArea.FindAll()
                .Where(a => a.DisplayId.Value != primary.DisplayId.Value)
                .ToList();
            int i = screenIndex - 1;
            return i >= 0 && i < otras.Count ? otras[i] : primary;
        }
        catch (Exception ex)
        {
            LogAviso($"DisplayArea.FindAll() falló al resolver el monitor {screenIndex}: {ex.Message} — se usa la principal.");
            return primary;
        }
    }

    /// <summary>Coloca la salida a pantalla completa en el monitor indicado
    /// (índice de settings.json, mismo contrato que WPF: 0 = principal).
    /// Nunca lanza: un fallo aquí (topología cambiada a media función, un
    /// monitor que ya no está) se registra y la salida se queda donde pueda,
    /// en vez de tirar la app — ver el aviso del hito 0 en la cabecera.</summary>
    public void PlaceOnScreen(int screenIndex)
    {
        try
        {
            var area = ResolveDisplayArea(screenIndex);
            var bounds = area.OuterBounds;   // píxeles físicos, borde a borde del monitor

            // FullScreen solo fullscreen-ea de forma fiable sobre el monitor
            // en el que la ventana YA está: hay que volver a modo ventana,
            // mover/redimensionar allí, y DESPUÉS pedir FullScreen.
            if (_appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
                _appWindow.SetPresenter(AppWindowPresenterKind.Default);

            _appWindow.MoveAndResize(bounds);
            _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        catch (Exception ex)
        {
            LogAviso($"PlaceOnScreen({screenIndex}) falló: {ex.Message} — la salida se queda donde estaba.");
        }
    }

    /// <summary>Oculta la salida sin destruirla (botón «Salida vídeo» de
    /// MainWindow, para apagarla entre cues sin perder el HWND hijo que mpv
    /// ya tiene — igual que Closing, pero pedido a propósito en vez de por
    /// un cierre de ventana).</summary>
    public void Hide()
    {
        try { _appWindow.Hide(); }
        catch (Exception ex) { LogAviso($"AppWindow.Hide() (botón Salida vídeo) falló: {ex.Message}"); }
    }

    /// <summary>Blackout: oculta/muestra el HWND hijo. Corte instantáneo, no
    /// un fundido — ver el porqué (airspace) en HwndHijo.Ocultar.</summary>
    public void SetBlackout(bool on)
    {
        _blackout = on;
        if (_hwndHijo == 0)
            return;
        if (on) HwndHijo.Ocultar(_hwndHijo);
        else HwndHijo.Mostrar(_hwndHijo);
    }

    public bool Blackout => _blackout;

    /// <summary>Cualquier cambio del AppWindow que pueda alterar el tamaño
    /// del área cliente (el operador lo pidió, o Windows recolocó la ventana
    /// solo porque un monitor desapareció) resincroniza el HWND hijo — punto
    /// 4 del encargo, y la mitad "no dejar la salida en un estado
    /// inconsistente" del aviso de cambio de topología.</summary>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_hwndHijo == 0)
            return;
        if (!args.DidSizeChange && !args.DidPresenterChange)
            return;
        try
        {
            var size = sender.ClientSize;
            HwndHijo.Redimensionar(_hwndHijo, Math.Max(1, size.Width), Math.Max(1, size.Height));
        }
        catch (Exception ex)
        {
            LogAviso($"Redimensionar el HWND hijo tras un cambio de ventana falló: {ex.Message}");
        }
    }

    /// <summary>Cierre real, solo al salir de la aplicación (ver App.xaml.cs,
    /// window.Closed). Antes de eso, Closing la oculta en vez de destruirla.</summary>
    public void CloseForShutdown()
    {
        _shutdown = true;
        try
        {
            if (_hwndHijo != 0)
                HwndHijo.Destruir(_hwndHijo);
        }
        catch (Exception ex) { LogAviso($"Destruir el HWND hijo al salir falló: {ex.Message}"); }
        _hwndHijo = 0;

        try { Close(); }
        catch (Exception ex) { LogAviso($"Close() de la ventana de salida falló: {ex.Message}"); }
    }
}
