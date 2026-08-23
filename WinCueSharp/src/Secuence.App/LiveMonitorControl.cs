// =============================================================================
// LiveMonitorControl.cs — Mini-monitor PGM/PVW EN VIVO (mpv embebido), WinUI 3
// =============================================================================
// Equivalente de src/WinCue.App/LiveMonitorControl.cs (WPF) para esta interfaz,
// NO una copia (regla 2 del proyecto). WinUI 3 no tiene WindowsFormsHost: el
// mpv embebido usa el MISMO patrón que VideoOutputWindow.xaml.cs — HwndHijo,
// una ventana hija Win32 a mano —, generalizado para poder vivir en un HUECO
// dentro de MainWindow (el previo PGM o PVW) en vez de llenar toda una
// ventana: Crear/Colocar ahora reciben también su posición (ver HwndHijo.cs).
//
// GEOMETRÍA DEL HUECO — se mide, no se asume (mismo espíritu que el aviso de
// WPF sobre "la barra se MIDE en vez de darla por supuesta"): el HWND hijo va
// bajo la cabecera de color (PgmHeader/PvwHeader), así que su alto se resta
// del hueco medido (PgmInner/PvwInner). Se resincroniza en el primer Loaded,
// en cada SizeChanged del hueco y en cada cambio de XamlRoot (DPI).
//
// SIN ESPEJO DE COMPOSITOR — a propósito, no un olvido. WinCue.App.PGM tiene
// DOS ramas (ver la cabecera de ese archivo): ESPEJA la superficie del
// compositor GPU cuando el cue en pantalla tiene una
// (VideoOutputWindow.HasCompositorSurface), y si no la tiene decodifica su
// PROPIA copia, muda, con su propio mpv embebido — la ruta clásica HWND.
// Secuence.App.VideoOutputWindow.xaml.cs NO tiene todavía superficie de
// compositor GPU (ni HasCompositorSurface ni MirrorVisual: esa fase, 2b,
// sigue escrita y sin conectar aquí — comprobado antes de escribir este
// archivo), así que Secuence está SIEMPRE en la rama clásica que WinCue.App
// ya usa cuando no hay qué espejar: decodificar su propia copia. Portar
// ShowMirror/IsMirroring aquí sería portar una rama que este producto nunca
// toma todavía — si el compositor llega a Secuence, esa rama sube entonces
// (mismo aviso que GeometryCanvas.cs deja sobre TryGrabStillAsync/ShowPaused).
//
// FUERA DE ESTA PASADA (igual que GeometryCanvas.cs, mismo motivo — Secuence
// no tiene todavía un visor de EDICIÓN separado del de SALIDA): ShowPaused,
// Scrub, SetGeometry, TryGrabStillAsync. Solo lo que hace falta para los dos
// previos: reproducir en vivo (PGM/PVW) y el transporte de preparación (PVW).
// =============================================================================

using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinCue.Video;

namespace Secuence.App;

/// <summary>Un previo PGM o PVW: cabecera de color + hueco donde vive el mpv
/// embebido (muted, decode independiente — ver la cabecera del archivo para
/// el porqué de no espejar el compositor). <paramref name="hwndPropio"/> es el
/// HWND de MainWindow (padre del hijo Win32); <paramref name="inner"/>/
/// <paramref name="header"/> son PgmInner/PgmHeader (o Pvw-) del XAML, para
/// medir el hueco; <paramref name="message"/>/<paramref name="status"/> son
/// PgmMessage/PgmStatus (o Pvw-).</summary>
internal sealed class LiveMonitorControl : IDisposable
{
    private readonly nint _hwndPropio;
    private readonly FrameworkElement _inner;
    private readonly FrameworkElement _header;
    private readonly TextBlock _message;
    private readonly TextBlock _status;

    private nint _hijo;
    private MpvPlayer? _player;
    private bool? _pauseState;        // último pause aplicado (null = re-asertar)
    private double _cachedDuration;
    private bool _durationAsked;
    private string _currentPath = "";
    private double _lastElapsed = -1;

    public string CurrentPath => _currentPath;

    public LiveMonitorControl(nint hwndPropio, FrameworkElement inner,
        FrameworkElement header, TextBlock message, TextBlock status)
    {
        _hwndPropio = hwndPropio;
        _inner = inner;
        _header = header;
        _message = message;
        _status = status;

        _inner.SizeChanged += (_, _) => SyncGeometry();
        _inner.Loaded += (_, _) =>
        {
            SyncGeometry();
            if (_inner.XamlRoot is not null)
                _inner.XamlRoot.Changed += (_, _) => SyncGeometry();
        };
    }

    // ------------------------------------------------------------------
    // Geometría del hueco (medida, no asumida — ver la cabecera)
    // ------------------------------------------------------------------

    private void SyncGeometry()
    {
        var root = _inner.XamlRoot;
        if (root is null || _inner.ActualWidth <= 0 || _inner.ActualHeight <= 0)
            return;

        double scale = root.RasterizationScale;
        var origin = _inner.TransformToVisual(root.Content)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        double headerH = _header.ActualHeight;

        int x = (int)Math.Round(origin.X * scale);
        int y = (int)Math.Round((origin.Y + headerH) * scale);
        int w = Math.Max(1, (int)Math.Round(_inner.ActualWidth * scale));
        int h = Math.Max(1, (int)Math.Round((_inner.ActualHeight - headerH) * scale));

        if (_hijo == 0)
            _hijo = HwndHijo.Crear(_hwndPropio, w, h, x, y);
        else
            HwndHijo.Colocar(_hijo, x, y, w, h);
    }

    // ------------------------------------------------------------------
    // API
    // ------------------------------------------------------------------

    /// <summary>Reproduce (en silencio) el archivo dado desde startAt.
    /// Si ya es el archivo actual, solo re-sincroniza el reloj.</summary>
    public void ShowVideo(string path, double startAt, bool loop)
    {
        if (!VideoEngine.MpvAvailable)
            return;
        if (!EnsurePlayer())
            return;

        if (path != _currentPath)
        {
            _currentPath = path;
            _lastElapsed = -1;
            _cachedDuration = 0.0;
            _durationAsked = false;
            // loadfile arranca REPRODUCIENDO — mismo motivo que WinCue.App
            // (ver su comentario en ShowVideo): el estado es "sonando", no
            // "desconocido".
            _pauseState = false;
            try
            {
                _player!.SetProperty("loop-file", loop ? "inf" : "no");
                _player.SetProperty("start", startAt > 0.2
                    ? startAt.ToString("F3", CultureInfo.InvariantCulture) : "none");
                _player.Command("loadfile", path);
            }
            catch (Exception) { return; }
            HwndHijo.Mostrar(_hijo);
            _message.Visibility = Visibility.Collapsed;
            _status.Text = System.IO.Path.GetFileName(path);
        }
        else
        {
            SyncClock(startAt);
        }
    }

    // ── Transporte del PVW (no toca la sala) ───────────────────────────

    public void PreviewPlay()
    {
        if (_player is null || _currentPath.Length == 0)
            return;
        try { _player.SetProperty("pause", false); _pauseState = false; }
        catch (Exception) { }
    }

    public void PreviewPause()
    {
        if (_player is null || _currentPath.Length == 0)
            return;
        try { _player.SetProperty("pause", true); _pauseState = true; }
        catch (Exception) { }
    }

    /// <summary>true si el previo está pausado (o no hay nada cargado).
    /// Propiedad OBSERVADA: no consulta al core.</summary>
    public bool PreviewIsPaused =>
        _player is null || _currentPath.Length == 0 || (_pauseState ?? true);

    /// <summary>Segundo por el que va el previo, o 0 si no hay nada.
    /// OBSERVADA (push de mpv) + extrapolada, NUNCA consultada al core en el
    /// hilo de UI — mismo motivo que WinCue.App (ver su ObservedTimePosSmooth):
    /// una llamada síncrona con el core ocupado bloquea la interfaz.</summary>
    public double PreviewPosition =>
        _player is null || _currentPath.Length == 0
            ? 0.0 : _player.ObservedTimePosSmooth() ?? 0.0;

    /// <summary>Duración del archivo cargado (0 = aún no se sabe). Cacheada
    /// POR ARCHIVO y pedida UNA vez — mismo motivo que WinCue.App.</summary>
    public double PreviewDuration
    {
        get
        {
            if (_player is null || _currentPath.Length == 0)
                return 0.0;
            if (_cachedDuration <= 0.0 && !_durationAsked)
            {
                _durationAsked = true;
                try { _cachedDuration = _player.GetPropertyDouble("duration") ?? 0.0; }
                catch (Exception) { return 0.0; }
            }
            return _cachedDuration;
        }
    }

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

    /// <summary>Re-sincroniza el monitor con el reloj real del motor (PGM).
    /// Posición = observada (push); duración = cacheada por archivo; pause =
    /// solo al CAMBIAR de estado — mismo contrato que WinCue.App.SyncClock
    /// (ver su comentario sobre por qué esto NUNCA puede llamar al core de
    /// forma síncrona desde el hilo de UI).</summary>
    public void SyncClock(double elapsed)
    {
        if (_player is null || _currentPath.Length == 0)
            return;
        try
        {
            bool advancing = _lastElapsed < 0 || elapsed > _lastElapsed + 0.02;
            bool wantPause = !advancing;
            if (_pauseState != wantPause)
            {
                _player.SetProperty("pause", wantPause);
                _pauseState = wantPause;
            }
            _lastElapsed = elapsed;

            if (_cachedDuration <= 0.0)
                _cachedDuration = _player.GetPropertyDouble("duration") ?? 0.0;

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

    public void Clear(string message)
    {
        StopPlayback();
        _message.Text = message;
        _message.Visibility = Visibility.Visible;
        _status.Text = "";
        if (_hijo != 0)
            HwndHijo.Ocultar(_hijo);
    }

    /// <summary>Para lo que estuviera reproduciendo (sin destruir el HWND ni
    /// el player: crearlos cuesta y el siguiente cue los reutiliza).</summary>
    private void StopPlayback()
    {
        _currentPath = "";
        _lastElapsed = -1;
        _cachedDuration = 0.0;
        _durationAsked = false;
        _pauseState = null;
        try { _player?.Command("stop"); } catch (Exception) { }
    }

    /// <summary>Libera la instancia mpv y destruye el HWND hijo (al cerrar la
    /// app — mismo momento que VideoOutputWindow.CloseForShutdown).</summary>
    public void Dispose()
    {
        try { _player?.Dispose(); } catch (Exception) { }
        _player = null;
        if (_hijo != 0)
        {
            try { HwndHijo.Destruir(_hijo); } catch (Exception) { }
            _hijo = 0;
        }
    }

    // ------------------------------------------------------------------
    // mpv embebido
    // ------------------------------------------------------------------

    private bool EnsurePlayer()
    {
        if (_player is not null)
            return true;
        SyncGeometry();
        if (_hijo == 0)
            return false;   // aún sin layout: reintentará el siguiente tick
        try
        {
            var options = new Dictionary<string, string>
            {
                ["wid"] = _hijo.ToString(),
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
            _player = new MpvPlayer(options);
            return true;
        }
        catch (Exception)
        {
            _message.Text = "monitor no disponible";
            _message.Visibility = Visibility.Visible;
            _player = null;
            return false;
        }
    }
}
