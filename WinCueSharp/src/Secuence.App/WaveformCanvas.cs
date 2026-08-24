// =============================================================================
// WaveformCanvas.cs — barra de trim (in/out) con onda, en Win2D.
// =============================================================================
// Equivalente de src/WinCue.App/WaveformControl.cs (WPF) para esta interfaz,
// NO una copia (regla 2 del proyecto). El CÁLCULO (submuestreo a buckets,
// proyección sobre columnas de píxel, mapeo tiempo↔píxel, hit-test y arrastre
// de los manejadores) es EXACTAMENTE el mismo que usa WinCue: vive en
// WinCue.Presentation.WaveformLayout, sin un solo tipo de framework de
// interfaz. Este archivo SOLO pinta e interpreta el puntero — mismo reparto
// que TimelineCanvas.cs/VuMeterCanvas.cs.
//
// La decodificación del archivo (pipe a ffmpeg cuando hace falta, con su
// propio mensaje limpio si ffmpeg no está en el PATH) vive en
// WinCue.Audio.AudioDecoder — YA compartida entre los dos productos antes de
// esta tarea (Secuence.App.csproj ya referenciaba WinCue.Audio.csproj para el
// motor de audio real). No hace falta moverla: ya estaba en el sitio correcto.
//
// ALCANCE DE ESTA PASADA (deliberado, no descuido — mismo patrón que el
// timeline/mixer "primera pasada"):
//   · Solo cues de AUDIO. Los de VÍDEO muestran el mensaje "— sin media —":
//     la barra lisa con ffprobe (WaveformControl.ProbeDuration, WinCue.App)
//     no tiene todavía un segundo consumidor en Secuence, y extraerla ahora
//     sin uso real sería la clase de generalización especulativa que el
//     encargo pide evitar. Cuando Secuence soporte vídeo, ProbeDuration sube
//     igual que AudioDecoder ya subió.
//   · SÍ arrastra los manejadores (no se quedó en solo lectura): el hit-test
//     y el clamp de arrastre YA estaban en WaveformLayout por el cálculo
//     compartido, así que conectar el puntero aquí era mucho más barato que
//     en el timeline (que si necesitó quedarse de solo lectura esta pasada).
//
// TRADUCCIÓN DE INTERACCIÓN WPF → WinUI 3:
//   OnMouseDown/Move/Up (MouseButtonEventArgs) → PointerPressed/Moved/
//   Released (PointerRoutedEventArgs); CaptureMouse()/ReleaseMouseCapture()
//   → CapturePointer(e.Pointer)/ReleasePointerCapture(e.Pointer); Cursor de
//   FrameworkElement → ProtectedCursor (ver constructor).
// =============================================================================

using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.UI;
using WinCue.Audio;
using WinCue.Core;
using WinCue.Presentation;

namespace Secuence.App;

/// <summary>Barra de trim con onda, en Win2D. Equivalente de
/// <c>WinCue.App.WaveformControl</c> (WPF) para WinUI 3, limitado en esta
/// pasada a cues de AUDIO (ver la cabecera del archivo). UserControl que
/// aloja un CanvasControl — CanvasControl sigue sellada (ver VuMeterCanvas.cs).</summary>
public sealed class WaveformCanvas : UserControl
{
    private readonly CanvasControl _canvas = new();

    private float[]? _mins, _maxs;
    private double _duration;
    private double _start, _end;
    private string _msg = Loc.T("— sin media —", "— no media —");
    private int _generation;               // cancela cargas viejas
    private WaveformHandle? _drag;

    /// <summary>Manejadores soltados: (startS, endS) — mismo contrato que
    /// WinCue.App.WaveformControl.MarkersChanged.</summary>
    public event Action<double, double>? MarkersChanged;

    public WaveformCanvas()
    {
        MinHeight = 72;
        Content = _canvas;
        _canvas.Draw += OnDraw;
        _canvas.SizeChanged += (_, _) => _canvas.Invalidate();

        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => _drag = null;
    }

    // ------------------------------------------------------------------
    // API
    // ------------------------------------------------------------------

    /// <summary>Decodifica y somete a submuestreo el archivo en segundo
    /// plano. Si ffmpeg no está disponible (o el formato no tiene soporte),
    /// AudioDecoder lanza con un mensaje entendible — aquí se atrapa y se
    /// muestra "no disponible", igual que hace WinCue.</summary>
    public async void LoadAudio(string path, double trimStart, double trimEnd)
    {
        _mins = _maxs = null;
        _duration = 0.0;
        _start = Math.Max(0.0, trimStart);
        _end = Math.Max(0.0, trimEnd);
        _msg = Loc.T("cargando…", "loading…");
        _canvas.Invalidate();

        int gen = ++_generation;
        try
        {
            var result = await Task.Run(() => ComputePeaksFromFile(path));
            if (gen != _generation) return;
            (_mins, _maxs, _duration) = result;
            if (_end <= 0.0) _end = _duration;
            _msg = "";
        }
        catch (Exception)
        {
            if (gen != _generation) return;
            _msg = Loc.T("no disponible", "not available");
        }
        _canvas.Invalidate();
    }

    /// <summary>Barra de trim de un cue de VÍDEO: se sondea la duración y se
    /// dibuja la barra con sus manejadores, pero SIN onda. No es una carencia:
    /// WinCue hace lo mismo (WaveformControl.LoadVideo). Decodificar el audio
    /// de un vídeo entero solo para pintar un dibujito costaría segundos cada
    /// vez que se selecciona un cue, y lo que se recorta es TIEMPO, que los
    /// manejadores y las cifras ya dan.
    ///
    /// El sondeo es COMPARTIDO (WinCue.Audio.MediaProbe): mismo ffprobe y
    /// mismo parseo que WinCue, que pasó a usarlo también.</summary>
    public async void LoadVideo(string path, double trimStart, double trimEnd)
    {
        _mins = _maxs = null;
        _duration = 0.0;
        _start = Math.Max(0.0, trimStart);
        _end = Math.Max(0.0, trimEnd);
        _msg = Loc.T("leyendo duración…", "reading duration…");
        _canvas.Invalidate();

        int gen = ++_generation;
        try
        {
            double dur = await Task.Run(() => MediaProbe.DurationSeconds(path));
            if (gen != _generation) return;
            _duration = dur;
            if (_end <= 0.0) _end = dur;
            _msg = "";
        }
        catch (Exception)
        {
            if (gen != _generation) return;
            _msg = Loc.T("no disponible", "not available");
        }
        _canvas.Invalidate();
    }

    /// <summary>Sin onda cargada: sin archivo, tipo sin media, o error. Mismo
    /// contrato que WaveformControl.Clear.</summary>
    public void Clear(string? msg = null)
    {
        _generation++;
        _drag = null;
        _mins = _maxs = null;
        _duration = 0.0;
        _msg = msg ?? Loc.T("— sin media —", "— no media —");
        _canvas.Invalidate();
    }

    /// <summary>Reflejar el trim del inspector sin recargar el archivo.</summary>
    public void SetMarkers(double start, double end)
    {
        _start = Math.Max(0.0, start);
        _end = Math.Max(0.0, end);
        _canvas.Invalidate();
    }

    // ------------------------------------------------------------------
    // Cálculo en segundo plano — decodificación (WinCue.Audio, compartida) +
    // submuestreo (WinCue.Presentation.WaveformLayout, compartido)
    // ------------------------------------------------------------------

    private static (float[] Mins, float[] Maxs, double Duration) ComputePeaksFromFile(string path)
    {
        var buf = AudioDecoder.Decode(path);
        var peaks = WaveformLayout.ComputePeaks(buf.Samples, buf.Channels, buf.FrameCount);
        return (peaks.Mins, peaks.Maxs, buf.DurationSeconds);
    }

    // ------------------------------------------------------------------
    // Pintado
    // ------------------------------------------------------------------

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        double w = sender.ActualWidth, h = sender.ActualHeight;
        ds.Clear(ToColor(Palette.WaveBg));
        if (w <= 0 || h <= 0)
            return;

        if (_duration <= 0.0)
        {
            using var msgFormat = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
            {
                FontSize = 11,
                HorizontalAlignment = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center,
                VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Center,
            };
            ds.DrawText(_msg, new Rect(0, 0, w, h), ToColor(Palette.TextFaint), msgFormat);
            return;
        }

        double bodyTop = WaveformLayout.RulerH, bodyH = h - WaveformLayout.RulerH;
        double sX = WaveformLayout.SecToX(_start, _duration, w);
        double eX = WaveformLayout.SecToX(WaveformLayout.EffectiveEnd(_end, _duration), _duration, w);

        // Onda: min/max por columna, proyectados desde los buckets cargados
        if (_mins is not null && _maxs is not null)
        {
            double mid = bodyTop + bodyH / 2;
            double half = bodyH / 2 - 2;
            int n = _mins.Length;
            var waveColor = ToColor(Palette.Wave);
            var dimColor = ToColor(Palette.WaveDim);
            for (int x = 0; x < (int)w; x++)
            {
                int i = WaveformLayout.ColumnBucketIndex(x, w, n);
                float y0 = (float)(mid - _maxs[i] * half);
                float y1 = (float)(mid - _mins[i] * half);
                var color = x >= sX && x <= eX ? waveColor : dimColor;
                ds.DrawLine(x + 0.5f, y0, x + 0.5f, y1, color);
            }
        }

        // Región seleccionada + zonas recortadas atenuadas
        ds.FillRectangle(new Rect(sX, bodyTop, Math.Max(0, eX - sX), bodyH), ToColor(Palette.Standby, 34));
        if (sX > 0)
            ds.FillRectangle(new Rect(0, bodyTop, sX, bodyH), ToColor(Palette.Black, 150));
        if (eX < w)
            ds.FillRectangle(new Rect(eX, bodyTop, w - eX, bodyH), ToColor(Palette.Black, 150));

        // Regla de tiempo (0/25/50/75/100 %)
        using var tickFormat = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat { FontSize = 10 };
        foreach (double frac in WaveformLayout.RulerFractions)
        {
            float x = (float)Math.Clamp(frac * w, 1, w - 1);
            ds.DrawLine(x, 0, x, (float)(WaveformLayout.RulerH - 4), ToColor(Palette.TextDimmer));
            string label = WaveformLayout.FormatTime(frac * _duration);
            using var layout = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(ds, label, tickFormat, 200, 12);
            float tx = frac == 0 ? x + 2
                     : frac == 1 ? x - (float)layout.LayoutBounds.Width - 2
                     : x - (float)layout.LayoutBounds.Width / 2;
            ds.DrawTextLayout(layout, tx, 0, ToColor(Palette.TextDimmer));
        }

        // Manejadores in/out
        DrawHandle(ds, sX, h);
        DrawHandle(ds, eX, h);
    }

    private static void DrawHandle(CanvasDrawingSession ds, double x, double h)
    {
        ds.DrawLine((float)x, (float)WaveformLayout.RulerH, (float)x, (float)h, ToColor(Palette.Standby), 2f);
        const float gw = 5;
        ds.FillRectangle(new Rect(x - gw, WaveformLayout.RulerH, gw * 2, 10), ToColor(Palette.Standby));
    }

    // ------------------------------------------------------------------
    // Interacción: arrastre de los manejadores (cálculo compartido, ver la
    // cabecera del archivo)
    // ------------------------------------------------------------------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_duration <= 0.0)
            return;
        double x = e.GetCurrentPoint(this).Position.X;
        double sX = WaveformLayout.SecToX(_start, _duration, ActualWidth);
        double eX = WaveformLayout.SecToX(WaveformLayout.EffectiveEnd(_end, _duration), _duration, ActualWidth);
        _drag = WaveformLayout.PickHandle(x, sX, eX);
        CapturePointer(e.Pointer);
        UpdateDrag(x);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not null)
            UpdateDrag(e.GetCurrentPoint(this).Position.X);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not null)
        {
            _drag = null;
            ReleasePointerCapture(e.Pointer);
            MarkersChanged?.Invoke(_start, WaveformLayout.EffectiveEnd(_end, _duration));
        }
    }

    private void UpdateDrag(double x)
    {
        double sec = WaveformLayout.XToSec(x, _duration, ActualWidth);
        if (_drag == WaveformHandle.Start)
            _start = WaveformLayout.DragStart(sec, _end, _duration);
        else
            _end = WaveformLayout.DragEnd(sec, _start);
        _canvas.Invalidate();
    }

    private static Color ToColor(uint rgb, byte alpha = 0xff) => Color.FromArgb(
        alpha, (byte)((rgb >> 16) & 0xff), (byte)((rgb >> 8) & 0xff), (byte)(rgb & 0xff));
}
