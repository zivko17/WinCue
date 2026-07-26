// =============================================================================
// MonitorTransportBar.cs — Reproducir, pausar y recorrer un monitor de vídeo
// =============================================================================
//
// La usan el visor del inspector y el monitor PVW. Está en un control aparte y
// no copiada en los dos porque dos copias de lo mismo se separan: una gana un
// arreglo y la otra no, y acaban comportándose distinto ante el mismo gesto.
//
// TODO LO QUE HACE ES DEL MONITOR. Nada de aquí toca lo que sale a la sala.
// =============================================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WinCue.Core;

namespace WinCue.App;

/// <summary>Barra de transporte de un <see cref="LiveMonitorControl"/>:
/// reproducir/pausar, barra para saltar y reloj posición/duración.</summary>
public sealed class MonitorTransportBar : DockPanel
{
    private readonly LiveMonitorControl _monitor;
    private readonly Button _playBtn;
    private readonly Slider _seek;
    private readonly TextBlock _time;
    private readonly DispatcherTimer _tick =
        new() { Interval = TimeSpan.FromMilliseconds(120) };

    /// <summary>El operador tiene la barra agarrada: el tic NO la recoloca.
    /// Pelearse con la mano de quien arrastra deja el control inservible.</summary>
    private bool _seeking;

    public MonitorTransportBar(LiveMonitorControl monitor)
    {
        _monitor = monitor;
        Margin = new Thickness(0, 4, 0, 0);

        _playBtn = new Button
        {
            Width = 30, Height = 22, Padding = new Thickness(0),
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = Loc.T("Reproducir / pausar el monitor (no toca la sala)",
                            "Play / pause the monitor (does not touch the room)"),
        };
        _seek = new Slider
        {
            Minimum = 0, Maximum = 1000,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
            IsMoveToPointEnabled = true,   // clic en la barra = saltar ahí
            ToolTip = Loc.T("Arrastra para moverte por el vídeo",
                            "Drag to move through the video"),
        };
        _time = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"), FontSize = 11,
            Foreground = Theme.TextDimBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 92, TextAlignment = TextAlignment.Right,
        };

        SetDock(_playBtn, Dock.Left);
        SetDock(_time, Dock.Right);
        Children.Add(_playBtn);
        Children.Add(_time);
        Children.Add(_seek);

        _playBtn.Click += (_, _) =>
        {
            if (_monitor.PreviewIsPaused)
                _monitor.PreviewPlay();
            else
                _monitor.PreviewPause();
            Refresh();
        };
        _seek.PreviewMouseDown += (_, _) => _seeking = true;
        _seek.PreviewMouseUp += (_, _) => { _seeking = false; SeekToSlider(); };
        _seek.ValueChanged += (_, _) => { if (_seeking) SeekToSlider(); };
        _tick.Tick += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>Arranca el refresco. Solo con la barra A LA VISTA: le pregunta
    /// la posición a mpv ocho veces por segundo y escondida no se paga.</summary>
    public void Start()
    {
        if (!_tick.IsEnabled)
            _tick.Start();
        Refresh();
    }

    public void Stop()
    {
        if (_tick.IsEnabled)
            _tick.Stop();
    }

    private void SeekToSlider()
    {
        double dur = _monitor.PreviewDuration;
        if (dur > 0.05)
            _monitor.PreviewSeek(_seek.Value / _seek.Maximum * dur);
    }

    /// <summary>Pone al día botón, barra y reloj.</summary>
    public void Refresh()
    {
        _playBtn.Content = _monitor.PreviewIsPaused ? Icons.Play : Icons.Pause;

        double dur = _monitor.PreviewDuration;
        double pos = _monitor.PreviewPosition;
        _time.Text = $"{Mmss(pos)} / {Mmss(dur)}";
        if (_seeking || dur <= 0.05)
            return;
        double v = Math.Clamp(pos / dur, 0.0, 1.0) * _seek.Maximum;
        if (Math.Abs(_seek.Value - v) <= 1.0)
            return;
        _seeking = true;      // no re-entrar por ValueChanged
        _seek.Value = v;
        _seeking = false;
    }

    private static string Mmss(double s)
    {
        if (double.IsNaN(s) || s < 0)
            s = 0;
        int m = (int)(s / 60);
        return $"{m}:{s - m * 60:00.0}";
    }
}
