// =============================================================================
// LiveMonitorControl.cs — Mini-monitor PGM/PVW EN VIVO (mpv embebido)
// =============================================================================
// Dos modos, según lo que haga falta:
//
//   · ESPEJO DEL COMPOSITOR (PGM con compositor GPU activo) — NO decodifica
//     nada: pinta con un VisualBrush la MISMA superficie que va a la sala. Es
//     lo correcto (PGM = lo que sale de verdad: pila de pistas, opacidades,
//     blackout y cartelas incluidas) y además es GRATIS: con un HAP 4K60 el
//     monitor de mpv obligaba a decodificar el archivo DOS veces (motor +
//     monitor) y a leerlo dos veces del disco (~240 MB/s cada uno).
//
//   · mpv EMBEBIDO Y MUDO (ao=null) dentro del panel, a frame-rate completo,
//     para lo que NO se puede espejar: la ruta clásica HWND (airspace: WPF no
//     ve el HWND de mpv), el PVW (cue en STANDBY, que aún no sale) y el visor
//     de EDICIÓN (fotograma pausado con scrub). En PGM clásico se re-sincroniza
//     por drift (>0.35 s) con el reloj real del motor y se pausa en espejo
//     (se detecta porque elapsed deja de avanzar).
//
// GOTCHA WPF (airspace): no se puede pintar encima de un HWND nativo, así que
// el distintivo va en una barra superior, no superpuesto al vídeo.
// =============================================================================

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinCue.Presentation;
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
    private readonly DockPanel _bar;   // barra del distintivo (chrome superior)

    // ESPEJO del compositor: rectángulo pintado con un VisualBrush de la
    // ventana de salida (sin decodificar nada; se refresca solo con ella)
    private readonly System.Windows.Shapes.Rectangle _mirror;
    private readonly VisualBrush _mirrorBrush;

    private MpvPlayer? _player;
    private static int _monitorSeq;   // numera los logs de diagnóstico por monitor
    private bool? _pauseState;        // último pause aplicado (null = re-asertar)
    private double _cachedDuration;   // duración del archivo actual (0 = pedirla)
    private string _currentPath = "";
    private double _lastElapsed = -1;

    public string CurrentPath => _currentPath;

    /// <summary>true si el monitor está ESPEJANDO una salida (sin decodificar).</summary>
    public bool IsMirroring => _mirrorBrush.Visual is not null;

    public LiveMonitorControl(string badge, Color badgeColor)
    {
        Background = Brushes.Black;
        BorderBrush = new SolidColorBrush(badgeColor);
        BorderThickness = new Thickness(1);
        Height = 152;
        // Barra superior con el distintivo (fuera del HWND → sin airspace)
        _bar = new DockPanel { Background = new SolidColorBrush(badgeColor) };
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
        _bar.Children.Add(badgeText);
        _bar.Children.Add(_status);

        _panel = new System.Windows.Forms.Panel
        { BackColor = System.Drawing.Color.Black };
        _host = new WindowsFormsHost { Child = _panel, Visibility = Visibility.Collapsed };

        _msg = new TextBlock
        {
            Text = "—",
            // Mismo gris que los bordes: es un marcador de posición, no texto
            Foreground = Theme.Solid(Theme.Border),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Espejo: el VisualBrush apunta al contenido de la ventana de salida.
        // Stretch Uniform = mismo encuadre que la sala (con bandas si el
        // monitorcito no tiene su misma relación de aspecto).
        _mirrorBrush = new VisualBrush
        {
            Stretch = Stretch.Uniform,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center,
        };
        _mirror = new System.Windows.Shapes.Rectangle
        {
            Fill = _mirrorBrush,
            Visibility = Visibility.Collapsed,
        };

        var body = new Grid();
        body.Children.Add(_msg);
        body.Children.Add(_mirror);
        body.Children.Add(_host);

        var root = new DockPanel();
        DockPanel.SetDock(_bar, Dock.Top);
        root.Children.Add(_bar);
        root.Children.Add(body);
        Child = root;

        // Alto elástico 16:9 respecto al ancho: al ensanchar el panel izquierdo
        // (separador arrastrable) los monitores crecen con él.
        //
        // El 16:9 es del HUECO DEL VÍDEO, no del control: hay que descontar el
        // borde y la barra del distintivo. Antes se sumaba un 18 fijo por la
        // barra y el borde no se contaba en absoluto, así que el hueco no era
        // 16:9 y la imagen se salía un poco del marco — invisible a 280 px de
        // ancho, evidente cuando el visor del inspector pasó a ocupar el alto
        // del panel. La barra se MIDE en vez de darla por supuesta; se suscribe
        // aquí abajo, con _bar ya construido.
        SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged || e.NewSize.Width <= 0)
                return;
            double barH = _bar.ActualHeight > 0
                ? _bar.ActualHeight : MonitorLayout.DefaultBarHeight;
            // La FÓRMULA es cálculo compartido (WinCue.Presentation.MonitorLayout):
            // Secuence la necesitaba igual y repetirla allí habría sido la copia
            // que la regla 2 prohíbe. Mismos números, mismo resultado.
            Height = MonitorLayout.HeightForWidth(
                e.NewSize.Width,
                BorderThickness.Left + BorderThickness.Right,
                BorderThickness.Top + BorderThickness.Bottom + barH);
        };
    }

    // ------------------------------------------------------------------
    // API
    // ------------------------------------------------------------------

    /// <summary>ESPEJO del compositor: el monitor deja de decodificar y pinta
    /// la superficie REAL de la salida (lo que va a la sala). Idempotente —
    /// se puede llamar en cada tick sin coste.</summary>
    public void ShowMirror(Visual source, string label)
    {
        if (ReferenceEquals(_mirrorBrush.Visual, source))
        {
            if (!string.Equals(_status.Text, label, StringComparison.Ordinal))
                _status.Text = label;
            return;
        }
        StopPlayback();                            // suelta el decode del monitor
        _mirrorBrush.Visual = source;
        _mirror.Visibility = Visibility.Visible;
        _host.Visibility = Visibility.Collapsed;   // el HWND taparía el espejo
        _msg.Text = "";
        _status.Text = label;
    }

    /// <summary>Sale del modo espejo (el monitor vuelve a poder reproducir).</summary>
    private void StopMirror()
    {
        if (_mirrorBrush.Visual is null)
            return;
        _mirrorBrush.Visual = null;
        _mirror.Visibility = Visibility.Collapsed;
    }

    /// <summary>Para lo que estuviera reproduciendo el mpv del monitor (sin
    /// destruirlo: crearlo cuesta y el siguiente cue lo reutiliza).</summary>
    private void StopPlayback()
    {
        _currentPath = "";
        _lastElapsed = -1;
        _cachedDuration = 0.0;
        _durationAsked = false;
        _pauseState = null;
        try { _player?.Command("stop"); } catch (Exception) { }
    }

    /// <summary>Reproduce (en silencio) el archivo dado desde startAt.
    /// Si ya es el archivo actual, solo re-sincroniza el reloj.</summary>
    public void ShowVideo(string path, double startAt, bool loop)
    {
        StopMirror();
        if (!VideoEngine.MpvAvailable || !_host.IsLoaded)
            return;
        if (!EnsurePlayer())
            return;

        if (path != _currentPath)
        {
            _currentPath = path;
            _lastElapsed = -1;
            _cachedDuration = 0.0;
            _durationAsked = false;
            // loadfile arranca REPRODUCIENDO: el estado es "sonando", no
            // "desconocido". Dejarlo en null hacía que PreviewIsPaused (que
            // ante la duda dice «pausado») mintiera, y el botón del PVW
            // enseñaba ▶ con el vídeo corriendo: la primera pulsación solo
            // corregía la etiqueta en vez de pausar.
            _pauseState = false;
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
        StopMirror();
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
            _durationAsked = false;
            // Este camino carga PAUSADO (el SetProperty de abajo), así que el
            // estado se conoce: es "pausado".
            _pauseState = true;
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

    // ── Transporte del VISOR (no toca la sala) ─────────────────────────
    //
    // El visor cargaba el archivo en pausa y solo se movía arrastrando el trim:
    // para ver un trozo por el medio había que buscarlo a ciegas con los
    // manejadores. Esto le da lo que tiene cualquier reproductor — reproducir,
    // pausar y saltar donde quieras— sin tocar nada de lo que sale a la sala.

    /// <summary>Reproduce en el visor desde donde esté.</summary>
    public void PreviewPlay()
    {
        if (_player is null || _currentPath.Length == 0)
            return;
        try
        {
            _player.SetProperty("pause", false);
            _pauseState = false;
        }
        catch (Exception) { }
    }

    /// <summary>Pausa el visor dejando el fotograma a la vista.</summary>
    public void PreviewPause()
    {
        if (_player is null || _currentPath.Length == 0)
            return;
        try
        {
            _player.SetProperty("pause", true);
            _pauseState = true;
        }
        catch (Exception) { }
    }

    /// <summary>true si el visor está pausado (o no hay nada cargado).
    /// Propiedad OBSERVADA: no consulta al core.</summary>
    public bool PreviewIsPaused =>
        _player is null || _currentPath.Length == 0 || (_pauseState ?? true);

    /// <summary>Segundo por el que va el visor, o 0 si no hay nada.
    ///
    /// OBSERVADA (push de mpv) + extrapolada con reloj de pared, NUNCA
    /// consultada al core. La primera versión de esto preguntaba «time-pos» de
    /// forma síncrona ocho veces por segundo DESDE EL HILO DE UI: con el core
    /// ocupado la llamada se atasca y bloquea la interfaz — la app llegaba a
    /// arrancar sin pintar la ventana. Está avisado dos métodos más abajo, en
    /// SyncClock, y aun así caí.</summary>
    public double PreviewPosition
    {
        get
        {
            if (_player is null || _currentPath.Length == 0)
                return 0.0;
            return _player.ObservedTimePosSmooth() ?? 0.0;
        }
    }

    /// <summary>Duración del archivo cargado (0 = aún no se sabe).
    ///
    /// Se cachea POR ARCHIVO y se pide UNA vez, no en cada tic: es el único
    /// dato que no llega por push, y consultarlo en bucle era la otra mitad del
    /// bloqueo.</summary>
    public double PreviewDuration
    {
        get
        {
            if (_player is null || _currentPath.Length == 0)
                return 0.0;
            if (_cachedDuration <= 0.0 && !_durationAsked)
            {
                _durationAsked = true;   // una sola vez por archivo
                try { _cachedDuration = _player.GetPropertyDouble("duration") ?? 0.0; }
                catch (Exception) { return 0.0; }
            }
            return _cachedDuration;
        }
    }

    /// <summary>Ya se preguntó la duración de ESTE archivo (se pregunta una vez;
    /// se rearma al cargar otro).</summary>
    private bool _durationAsked;

    /// <summary>Salta a un segundo SIN pausar: es el arrastre de la barra de
    /// progreso, que tiene que poder usarse con el visor en marcha. Scrub()
    /// pausa a propósito porque sirve para fijar un fotograma de trim.</summary>
    public void PreviewSeek(double seconds)
    {
        if (_player is null || _currentPath.Length == 0)
            return;
        try
        {
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
        StopMirror();
        StopPlayback();
        _msg.Text = message;
        _status.Text = "";
        _host.Visibility = Visibility.Collapsed;   // oculta el HWND nativo
    }

    /// <summary>Vuelca el fotograma que se está viendo a un PNG y lo devuelve
    /// como imagen de WPF. null si no se pudo.
    ///
    /// POR QUÉ EXISTE ESTO: el visor es mpv dentro de un HWND nativo, y WPF no
    /// puede pintar encima de un HWND (airspace — ver la cabecera de este
    /// fichero). Por eso el lienzo de geometría no puede superponer el marco del
    /// canvas ni los tiradores sobre el vídeo en vivo: quedarían DEBAJO. Con un
    /// fotograma fijo el problema desaparece, porque pasa a ser un mapa de bits
    /// de WPF como cualquier otro. Para encuadrar no hace falta movimiento: se
    /// encuadra contra una imagen quieta.
    ///
    /// La bandera "video" pide el fotograma DECODIFICADO, no una captura de la
    /// ventana: así no salen ni el OSD ni bordes del HWND, y sigue funcionando
    /// aunque el visor esté oculto.</summary>
    public async Task<BitmapSource?> TryGrabStillAsync()
    {
        if (_player is null || _currentPath.Length == 0)
            return null;

        string file = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"wincue-still-{Guid.NewGuid():N}.png");
        try
        {
            _player.Command("screenshot-to-file", file, "video");
        }
        catch (Exception) { return null; }

        try
        {
            // mpv atiende el comando en SU hilo: el fichero no está listo al
            // volver de Command(). Se espera a que aparezca con contenido, con
            // un tope para no colgar la UI si el vuelco falla en silencio.
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(25).ConfigureAwait(true);
                var info = new System.IO.FileInfo(file);
                if (!info.Exists || info.Length == 0)
                    continue;

                // Un respiro extra: existir no es estar cerrado del todo.
                await Task.Delay(40).ConfigureAwait(true);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                // OnLoad lo carga ENTERO en memoria ahora: sin esto WPF deja el
                // fichero abierto en diferido y el borrado de abajo fallaría.
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(file);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            return null;
        }
        catch (Exception) { return null; }
        finally
        {
            try { System.IO.File.Delete(file); } catch (Exception) { }
        }
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
