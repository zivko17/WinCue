// =============================================================================
// MonitorControl.cs — Mini-monitor PGM/PVW (port de monitor_widget.py)
// =============================================================================
// Muestra un fotograma extraído con ffmpeg: monitor de PROGRAMA (frame al
// tiempo actual del vídeo en la salida) o de PREVIEW (primer frame del cue en
// standby). Distintivo PGM/PVW en la esquina.
//
// Guardas heredadas del Python (resueltas allí a base de sustos):
//   · _busy: si hay una extracción en curso se descarta la nueva petición
//     (evita acumular procesos ffmpeg y que el monitor vaya a tirones).
//   · _token: un frame que llega tarde de una petición vieja se ignora.
// =============================================================================

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinCue.App;

public sealed class MonitorControl : Border
{
    private readonly Image _image = new()
    {
        Stretch = Stretch.Uniform,   // letterbox preservando aspecto
    };

    private readonly TextBlock _empty = new()
    {
        Text = "—",
        Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly TextBlock _badge = new()
    {
        FontSize = 10,
        FontWeight = FontWeights.Bold,
        Foreground = Brushes.Black,
        Padding = new Thickness(6, 1, 6, 1),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private int _token;
    private bool _busy;
    private string _lastPath = "";

    public MonitorControl(string badge, Color badgeColor)
    {
        Background = Brushes.Black;
        BorderBrush = new SolidColorBrush(badgeColor);
        BorderThickness = new Thickness(1);
        Height = 130;                        // ~16:9 dentro del panel de 260 px

        _badge.Text = badge;
        _badge.Background = new SolidColorBrush(badgeColor);

        var grid = new Grid();
        grid.Children.Add(_image);
        grid.Children.Add(_empty);
        grid.Children.Add(_badge);
        Child = grid;
    }

    /// <summary>Ruta del último frame solicitado (para no re-extraer PVW).</summary>
    public string LastPath => _lastPath;

    /// <summary>Extrae y muestra el fotograma de path al segundo t.</summary>
    public async void ShowFrame(string path, double t, bool force = false)
    {
        if (_busy && !force)
            return;
        _busy = true;
        int token = ++_token;
        _lastPath = path;
        try
        {
            var frame = await Task.Run(() => ExtractFrame(path, t));
            if (token != _token)
                return;   // llegó un frame viejo
            if (frame is not null)
            {
                _image.Source = frame;
                _empty.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _busy = false;   // libera el guard
        }
    }

    public void Clear(string msg = "—")
    {
        _token++;            // invalida extracciones en vuelo
        _lastPath = "";
        _image.Source = null;
        _empty.Text = msg;
        _empty.Visibility = Visibility.Visible;
    }

    /// <summary>ffmpeg -ss t -i path → 1 frame PNG por pipe → BitmapImage congelado.</summary>
    private static BitmapImage? ExtractFrame(string path, double t)
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(Math.Max(0.0, t).ToString("F3",
                System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add("scale=-1:180");
            psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("png");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("image2pipe");
            psi.ArgumentList.Add("-");

            using var proc = Process.Start(psi);
            if (proc is null) return null;
            using var mem = new MemoryStream();
            proc.StandardOutput.BaseStream.CopyTo(mem);
            if (!proc.WaitForExit(8_000))
            {
                try { proc.Kill(); } catch (Exception) { }
                return null;
            }
            if (mem.Length == 0)
                return null;

            mem.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = mem;
            bmp.EndInit();
            bmp.Freeze();   // usable desde el hilo de UI
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
