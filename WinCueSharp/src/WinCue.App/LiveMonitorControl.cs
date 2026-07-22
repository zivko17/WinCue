// =============================================================================
// LiveMonitorControl.cs — Mini-monitor PGM/PVW EN VIVO (mpv embebido)
// =============================================================================
// Sustituye al monitor de fotogramas por ffmpeg: una instancia mpv propia,
// SILENCIADA (ao=null), renderiza dentro del panel a frame-rate completo.
//   · PGM: reproduce el mismo archivo que la salida y se re-sincroniza por
//     drift (>0.35 s) con el reloj real del motor; si la salida se pausa,
//     el monitor se pausa (se detecta porque elapsed deja de avanzar).
//   · PVW: reproduce en bucle el cue en standby.
//
// GOTCHA WPF (airspace): no se puede pintar encima de un HWND nativo, así que
// el distintivo va en una barra superior, no superpuesto al vídeo.
// =============================================================================

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using WinCue.Video;

namespace WinCue.App;

// CA1001: control WPF — vive tanto como MainWindow; el mpv embebido se libera
// deterministamente vía Shutdown() (llamado al cerrar la app) y el
// WindowsFormsHost lo desmonta el árbol visual de WPF.
#pragma warning disable CA1001
public sealed class LiveMonitorControl : Border
{
#pragma warning restore CA1001
    private readonly WindowsFormsHost _host;
    private readonly System.Windows.Forms.Panel _panel;
    private readonly TextBlock _msg;
    private readonly TextBlock _status;

    private MpvPlayer? _player;
    private static int _monitorSeq;   // numera los logs de diagnóstico por monitor
    private bool? _pauseState;        // último pause aplicado (null = re-asertar)
    private double _cachedDuration;   // duración del archivo actual (0 = pedirla)
    private string _currentPath = "";
    private double _lastElapsed = -1;

    public string CurrentPath => _currentPath;

    public LiveMonitorControl(string badge, Color badgeColor)
    {
        Background = Brushes.Black;
        BorderBrush = new SolidColorBrush(badgeColor);
        BorderThickness = new Thickness(1);
        Height = 152;
        // Alto elástico 16:9 respecto al ancho: al ensanchar el panel
        // izquierdo (separador arrastrable) los monitores crecen con él
        SizeChanged += (_, e) =>
        {
            if (e.WidthChanged && e.NewSize.Width > 0)
                Height = Math.Max(120, e.NewSize.Width * 9.0 / 16.0 + 18.0);
        };

        // Barra superior con el distintivo (fuera del HWND → sin airspace)
        var bar = new DockPanel { Background = new SolidColorBrush(badgeColor) };
        var badgeText = new TextBlock
        {
            Text = badge, FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = Brushes.Black, Margin = new Thickness(6, 1, 6, 1),
        };
        _status = new TextBlock
        {
            FontSize = 10, Foreground = Brushes.Black,
            Margin = new Thickness(0, 1, 6, 1), HorizontalAlignment = HorizontalAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        DockPanel.SetDock(badgeText, Dock.Left);
        bar.Children.Add(badgeText);
        bar.Children.Add(_status);

        _panel = new System.Windows.Forms.Panel
        { BackColor = System.Drawing.Color.Black };
        _host = new WindowsFormsHost { Child = _panel, Visibility = Visibility.Collapsed };

        _msg = new TextBlock
        {
            Text = "—",
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var body = new Grid();
        body.Children.Add(_msg);
        body.Children.Add(_host);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(body);
        Child = root;
    }

    // ------------------------------------------------------------------
    // API
    // ------------------------------------------------------------------

    /// <summary>Reproduce (en silencio) el archivo dado desde startAt.
    /// Si ya es el archivo actual, solo re-sincroniza el reloj.</summary>
    public void ShowVideo(string path, double startAt, bool loop)
    {
        if (!VideoEngine.MpvAvailable || !_host.IsLoaded)
            return;
        if (!EnsurePlayer())
            return;

        if (path != _currentPath)
        {
            _currentPath = path;
            _lastElapsed = -1;
            _cachedDuration = 0.0;
            _pauseState = null;
            try
            {
                _player!.SetProperty("loop-file", loop ? "inf" : "no");
                _player.SetProperty("start", startAt > 0.2
                    ? startAt.ToString("F3", CultureInfo.InvariantCulture) : "none");
                _player.Command("loadfile", path);
            }
            catch (Exception) { return; }
            _host.Visibility = Visibility.Visible;
            _status.Text = System.IO.Path.GetFileName(path);
        }
        else
        {
            SyncClock(startAt);
        }
    }

    /// <summary>Visor de edición: carga el archivo PAUSADO en el instante dado.
    /// Si ya es el archivo actual, solo hace scrub.</summary>
    public void ShowPaused(string path, double at)
    {
        if (!VideoEngine.MpvAvailable)
            return;
        if (!_host.IsLoaded)
        {
            // Aún sin layout (p.ej. arranque): reintentar cuando cargue
            RoutedEventHandler? once = null;
            once = (_, _) => { _host.Loaded -= once; ShowPaused(path, at); };
            _host.Loaded += once;
            return;
        }
        if (!EnsurePlayer())
            return;

        if (path != _currentPath)
        {
            _currentPath = path;
            _cachedDuration = 0.0;
            _pauseState = null;
            try
            {
                _player!.SetProperty("loop-file", "no");
                _player.SetProperty("pause", true);
                _player.SetProperty("start", at > 0.05
                    ? at.ToString("F3", CultureInfo.InvariantCulture) : "none");
                _player.Command("loadfile", path);
            }
            catch (Exception) { return; }
            _host.Visibility = Visibility.Visible;
            _status.Text = System.IO.Path.GetFileName(path);
        }
        else
        {
            Scrub(at);
        }
    }

    /// <summary>Salta (pausado) al segundo dado — fotograma de referencia.</summary>
    public void Scrub(double seconds)
    {
        if (_player is null || _currentPath.Length == 0)
            return;
        try
        {
            _player.SetProperty("pause", true);
            _pauseState = true;
            _player.Command("seek",
                Math.Max(0.0, seconds).ToString("F3", CultureInfo.InvariantCulture),
                "absolute");
        }
        catch (Exception) { }
    }

    /// <summary>Aplica zoom/pan/rotación al visor (misma matemática que la salida).</summary>
    public void SetGeometry(double zoom, double panX, double panY, long rotate, bool keepAspect)
    {
        if (_player is null)
            return;
        try
        {
            _player.SetProperty("keepaspect", keepAspect ? "yes" : "no");
            _player.SetProperty("video-zoom", zoom);
            _player.SetProperty("video-pan-x", panX);
            _player.SetProperty("video-pan-y", panY);
            _player.SetProperty("video-rotate", rotate);
        }
        catch (Exception) { }
    }

    /// <summary>Re-sincroniza el monitor con el reloj real del motor (PGM).
    /// OJO: corre en el hilo de UI cada 500 ms — aquí NO puede haber llamadas
    /// síncronas al core de mpv en caliente (con el core saturado, p. ej.
    /// decode SW de HAP, bloqueaban el hilo de UI y el cursor del timeline iba
    /// a trompicones). Posición = observada (push); duración = cacheada por
    /// archivo; pause = solo al CAMBIAR de estado.</summary>
    public void SyncClock(double elapsed)
    {
        if (_player is null || _currentPath.Length == 0)
            return;
        try
        {
            // Pausa-espejo: si el reloj del motor no avanza, congelar el monitor
            bool advancing = _lastElapsed < 0 || elapsed > _lastElapsed + 0.02;
            bool wantPause = !advancing;
            if (_pauseState != wantPause)
            {
                _player.SetProperty("pause", wantPause);
                _pauseState = wantPause;
            }
            _lastElapsed = elapsed;

            // Duración: consultarla UNA vez por archivo (0 = aún desconocida)
            if (_cachedDuration <= 0.0)
                _cachedDuration = _player.GetPropertyDouble("duration") ?? 0.0;

            // Corrección de drift (solo si el archivo tiene línea de tiempo)
            if (_cachedDuration > 0.5)
            {
                double? pos = _player.ObservedTimePos;
                if (pos.HasValue && Math.Abs(pos.Value - elapsed) > 0.35)
                    _player.Command("seek",
                        elapsed.ToString("F3", CultureInfo.InvariantCulture), "absolute");
            }
        }
        catch (Exception) { /* el monitor nunca debe romper el show */ }
    }

    public void Clear(string message = "—")
    {
        _currentPath = "";
        _lastElapsed = -1;
        _cachedDuration = 0.0;
        _pauseState = null;
        _msg.Text = message;
        _status.Text = "";
        _host.Visibility = Visibility.Collapsed;   // oculta el HWND nativo
        try { _player?.Command("stop"); } catch (Exception) { }
    }

    /// <summary>Libera la instancia mpv (al cerrar la app).</summary>
    public void Shutdown()
    {
        try { _player?.Dispose(); } catch (Exception) { }
        _player = null;
    }

    // ------------------------------------------------------------------
    // mpv embebido
    // ------------------------------------------------------------------

    private bool EnsurePlayer()
    {
        if (_player is not null)
            return true;
        try
        {
            nint hwnd = _panel.Handle;   // fuerza la creación del HWND
            var options = new Dictionary<string, string>
            {
                ["wid"] = hwnd.ToString(),
                ["vo"] = "gpu",
                ["hwdec"] = "auto",
                ["ao"] = "null",              // monitor SIEMPRE mudo
                ["mute"] = "yes",
                ["keep-open"] = "yes",
                ["hr-seek"] = "yes",
                ["keepaspect"] = "yes",
                ["osc"] = "no",
                ["input-default-bindings"] = "no",
                ["input-vo-keyboard"] = "no",
            };
            // Diagnóstico (ver VideoEngine): WINCUE_MPV_LOG=<dir> → log por
            // monitor; el log que se corta en seco delata al player que murió.
            if (Environment.GetEnvironmentVariable("WINCUE_MPV_LOG") is { Length: > 0 } logDir)
            {
                int n = System.Threading.Interlocked.Increment(ref _monitorSeq);
                options["log-file"] = System.IO.Path.Combine(logDir, $"mpv-monitor{n}.log");
                options["msg-level"] = "all=v";
            }
            _player = new MpvPlayer(options);
            return true;
        }
        catch (Exception)
        {
            _msg.Text = "monitor no disponible";
            _player = null;
            return false;
        }
    }
}
