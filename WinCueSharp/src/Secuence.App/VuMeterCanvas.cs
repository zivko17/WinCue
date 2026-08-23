// =============================================================================
// VuMeterCanvas.cs — VU meter en Win2D (CanvasControl), piloto del traslado
// de los siete controles pintados a mano de WinCue a Secuence.
// =============================================================================
// El CÁLCULO (mapeo de RMS a posición, ataque/release, retención de pico,
// testigo de clip, geometría en píxeles, colores por zona) es EXACTAMENTE el
// mismo que usa src/WinCue.App/VuMeterControl.cs (WPF): vive en
// WinCue.Presentation.VuMeterLayout, sin un solo tipo de framework de
// interfaz. Este archivo SOLO pinta: pide el estado y la geometría al
// cálculo compartido y los traduce a llamadas de CanvasDrawingSession — el
// "mismo cálculo, otro pincel" del encargo (hitos-0-1).
//
// POR QUÉ ES UN UserControl Y NO UN CanvasControl DIRECTAMENTE
// —————————————————————————————————————————————————————————————
// El plan era heredar de CanvasControl, como VuMeterControl hereda de
// FrameworkElement en WPF. No compila: CanvasControl está SELLADA
// (Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl, error CS0509). Es la
// primera diferencia real de Win2D frente a DrawingContext que no es solo
// de API — en WPF cualquier FrameworkElement se puede especializar
// sobrescribiendo OnRender; en Win2D el patrón es COMPOSICIÓN: se aloja un
// CanvasControl (sin heredar) dentro de algo que sí se puede heredar
// (UserControl) y se engancha su evento Draw. El resto de la traducción es
// directa; ver el bloque siguiente.
//
// TRADUCCIÓN DrawingContext (WPF) → Win2D, control por control:
//   DrawRectangle(brush, pen, rect)      → FillRectangle + DrawRectangle
//   PushClip(RectangleGeometry) / Pop    → CreateLayer(opacidad, rectángulo)
//                                           dentro de un using — Win2D no
//                                           tiene Push/Pop de recorte: la capa
//                                           (con o sin recorte) es un recurso
//                                           IDisposable que se cierra solo.
//   LinearGradientBrush(stops, ángulo)   → CanvasLinearGradientBrush con
//                                           StartPoint/EndPoint explícitos:
//                                           Win2D no tiene un "ángulo de 0/90"
//                                           como el constructor de WPF, así
//                                           que aquí se traduce a los dos
//                                           puntos que dan el mismo efecto.
//   DrawLine(pen, p0, p1)                → DrawLine(p0, p1, color, grosor)
//   FrameworkElement.InvalidateVisual()  → CanvasControl.Invalidate()
//   DispatcherTimer (WPF)                → DispatcherQueueTimer (WinUI 3),
//                                           mismo periodo de 50 ms.
// El resto —fondo, marco, testigo de clip, recorte de la barra, marca de
// pico— es una traducción DIRECTA: los mismos rectángulos, el mismo orden.
// =============================================================================

using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;
using WinCue.Presentation;

namespace Secuence.App;

/// <summary>VU con peak-hold, en Win2D. Equivalente de
/// <c>WinCue.App.VuMeterControl</c> (WPF) para WinUI 3: mismo cálculo
/// (WinCue.Presentation.VuMeterLayout), mismo comportamiento, framework de
/// pintado distinto. UserControl que aloja un CanvasControl —ver la cabecera
/// del archivo: CanvasControl está sellada, no se puede heredar de ella.</summary>
public sealed class VuMeterCanvas : UserControl
{
    private readonly bool _vertical;
    private readonly VuMeterState _state = new();
    private readonly CanvasControl _canvas = new();
    private DispatcherQueueTimer? _decay;

    /// <summary>Constructor sin parámetros (horizontal), simétrico con
    /// VuMeterControl().</summary>
    public VuMeterCanvas() : this(vertical: false) { }

    public VuMeterCanvas(bool vertical)
    {
        _vertical = vertical;
        Content = _canvas;
        _canvas.Draw += OnDraw;

        // El timer de decaimiento necesita un DispatcherQueue ya asociado a
        // una ventana; en el constructor (antes de entrar en el árbol
        // visual) puede no estar listo todavía, así que se arma al cargar.
        Loaded += (_, _) => StartDecayTimer();
        Unloaded += (_, _) => _decay?.Stop();
    }

    private void StartDecayTimer()
    {
        if (_decay is not null)
            return;
        _decay = DispatcherQueue.CreateTimer();
        _decay.Interval = TimeSpan.FromMilliseconds(50); // mismo periodo que WinCue.App.VuMeterControl
        _decay.Tick += (_, _) =>
        {
            if (_state.Decay(System.Diagnostics.Stopwatch.GetTimestamp()))
                _canvas.Invalidate();
        };
        _decay.Start();
    }

    /// <summary>Nivel RMS instantáneo (0..1 lineal). Misma ley que
    /// VuMeterControl.SetLevel — ver WinCue.Presentation.VuMeterState.Normalize.</summary>
    public void SetLevel(float rms)
    {
        _state.Report(rms, System.Diagnostics.Stopwatch.GetTimestamp());
        _canvas.Invalidate();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        double w = sender.ActualWidth, h = sender.ActualHeight;
        if (w <= 0 || h <= 0)
            return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();

        // Fondo + marco: el medidor se ve SIEMPRE, también en silencio
        var frame = ToRect(VuMeterLayout.FrameRect(w, h));
        ds.FillRectangle(frame, ToColor(VuMeterPalette.Background));
        ds.DrawRectangle(frame, ToColor(VuMeterPalette.Frame), 1f);

        // Clip-hold: testigo ROJO enganchado 3 s tras tocar 0 dBFS
        var led = VuMeterLayout.ClipLedRect(w, h, _vertical, _state.IsClipped(now));
        if (led is { } ledRect)
            ds.FillRectangle(ToRect(ledRect), ToColor(VuMeterPalette.Clip));

        if (_vertical)
        {
            // Marcas de escala (25/50/75 %)
            foreach (double f in VuMeterLayout.VerticalTickFractions)
            {
                float y = (float)(h * f);
                ds.DrawLine(new Vector2(1, y), new Vector2((float)(w - 1), y),
                    ToColor(VuMeterPalette.Tick, VuMeterPalette.TickAlpha), 1f);
            }

            // Barra de abajo hacia arriba (verde abajo, rojo arriba)
            var bar = VuMeterLayout.LevelBarClipRect(w, h, _vertical, _state.Level);
            if (bar is { } barRect)
            {
                using var layer = ds.CreateLayer(1f, ToRect(barRect));
                using var brush = MakeGradient(sender, VuMeterPalette.VerticalBar,
                    new Vector2(0, 0), new Vector2(0, (float)h));
                ds.FillRectangle(new Rect(0, 0, w, h), brush);
            }
            var peak = VuMeterLayout.PeakRect(w, h, _vertical, _state.Peak);
            if (peak is { } peakRect)
                ds.FillRectangle(ToRect(peakRect), ToColor(VuMeterPalette.Peak));
            return;
        }

        var hBar = VuMeterLayout.LevelBarClipRect(w, h, _vertical, _state.Level);
        if (hBar is { } hBarRect)
        {
            using var layer = ds.CreateLayer(1f, ToRect(hBarRect));
            using var brush = MakeGradient(sender, VuMeterPalette.HorizontalBar,
                new Vector2(0, 0), new Vector2((float)w, 0));
            ds.FillRectangle(new Rect(0, 0, w, h), brush);
        }
        var hPeak = VuMeterLayout.PeakRect(w, h, _vertical, _state.Peak);
        if (hPeak is { } hPeakRect)
            ds.FillRectangle(ToRect(hPeakRect), ToColor(VuMeterPalette.Peak));
    }

    /// <summary>Traduce una escala de color de VuMeterPalette (offset, uint)
    /// a un CanvasLinearGradientBrush con los puntos de inicio/fin explícitos
    /// que pide Win2D — WPF resolvía esto solo con un ángulo (0°/90°).</summary>
    private static CanvasLinearGradientBrush MakeGradient(
        ICanvasResourceCreator creator, VuMeterGradientStop[] stops, Vector2 start, Vector2 end)
    {
        var canvasStops = new CanvasGradientStop[stops.Length];
        for (int i = 0; i < stops.Length; i++)
            canvasStops[i] = new CanvasGradientStop
            {
                Position = (float)stops[i].Offset,
                Color = ToColor(stops[i].Color),
            };
        return new CanvasLinearGradientBrush(creator, canvasStops) { StartPoint = start, EndPoint = end };
    }

    private static Rect ToRect(VuMeterRect r) => new(r.X, r.Y, r.Width, r.Height);

    private static Color ToColor(uint rgb, byte alpha = 0xff) => Color.FromArgb(
        alpha, (byte)((rgb >> 16) & 0xff), (byte)((rgb >> 8) & 0xff), (byte)(rgb & 0xff));
}
