// =============================================================================
// WaveformLayout.cs — el CÁLCULO de la barra de trim con onda, sin framework.
// =============================================================================
// Traslado a Win2D (ver el encargo, hitos-0-1): entra el submuestreo de la
// onda a un número fijo de picos (min/max por bucket), su proyección sobre
// las columnas de píxel disponibles, el mapeo tiempo↔píxel, la posición de
// los manejadores de trim y el hit-test/arrastre de esos manejadores. NO
// entra la decodificación del archivo (eso ya vivía, correctamente, en
// WinCue.Audio.AudioDecoder — un pipe a ffmpeg cuando hace falta, con su
// propio mensaje limpio si ffmpeg no está en el PATH) ni la barra lisa de
// vídeo con ffprobe (WaveformControl.ProbeDuration): esta pasada de Secuence
// solo pinta AUDIO — ver el encargo y el informe para el porqué.
//
// Los valores son LOS DE HOY: Buckets, RulerH, HandleHit y el propio bucle de
// min/max proceden literalmente de WaveformControl.ComputePeaks/OnRender
// (WinCue.App). Un cambio aquí se nota en pantalla en los DOS productos y es
// un fallo, no un ajuste — por eso WaveformLayoutTests fija valores concretos
// para el submuestreo, que es la parte que más fácil se rompe sin que salte
// ningún error (solo se ve "distinta" la onda).
//
// POR QUÉ ESTE ARCHIVO NO REFERENCIA WinCue.Audio
// —————————————————————————————————————————————————————————————————
// AudioBuffer (WinCue.Audio) trae NAudio detrás. El submuestreo en sí no
// necesita saber decodificar nada: le basta el array de muestras entrelazadas,
// el número de canales y el número de frames — los mismos tres datos que
// AudioBuffer.Samples/Channels/FrameCount ya exponen. Pedirlos como
// parámetros sueltos evita añadir una referencia de proyecto nueva solo para
// un tipo con tres campos, y deja este archivo tan "motor a secas" como el
// resto de WinCue.Presentation.
// =============================================================================

namespace WinCue.Presentation;

/// <summary>Picos de la onda ya submuestreados: un mínimo y un máximo por
/// bucket. Longitud de ambos arrays = mismo número de buckets.</summary>
public readonly record struct WaveformPeaks(float[] Mins, float[] Maxs)
{
    public int Count => Mins.Length;
}

/// <summary>Qué manejador de trim está más cerca de un punto (o se arrastra).</summary>
public enum WaveformHandle { Start, End }

/// <summary>Geometría y cálculo puro de la barra de trim con onda. Ni un tipo
/// de framework de interfaz — cada producto dibuja esto con lo que sepa
/// (DrawingContext.DrawGeometry, CanvasDrawingSession.DrawLine…).</summary>
public static class WaveformLayout
{
    /// <summary>Buckets de min/max en los que se submuestrea la onda al
    /// cargar el archivo (independiente del ancho en píxeles: eso es la
    /// segunda proyección, ColumnBucketIndex, y se hace en cada pintado).</summary>
    public const int Buckets = 1600;

    /// <summary>Alto de la regla de tiempo, en píxeles.</summary>
    public const double RulerH = 16;

    /// <summary>Radio de acierto (píxeles) para agarrar un manejador de trim.</summary>
    public const double HandleHit = 8;

    /// <summary>Fracciones de la regla de tiempo (0/25/50/75/100 %).</summary>
    public static readonly double[] RulerFractions = { 0.0, 0.25, 0.5, 0.75, 1.0 };

    // ------------------------------------------------------------------
    // Submuestreo: muestras → picos por bucket
    // ------------------------------------------------------------------

    /// <summary>Submuestrea audio entrelazado (mono-mix de sus canales) a
    /// como mucho <paramref name="bucketCap"/> buckets de min/max. Mismo
    /// bucle que WaveformControl.ComputePeaks tenía en línea.</summary>
    /// <param name="samples">Muestras entrelazadas (frame0: ch0,ch1…, frame1:…).</param>
    /// <param name="channels">Canales por frame.</param>
    /// <param name="frameCount">Número de frames (muestras por canal).</param>
    /// <exception cref="InvalidOperationException">frameCount es 0 ("sin audio",
    /// mismo mensaje que hoy — así el "no disponible" de la interfaz no cambia).</exception>
    public static WaveformPeaks ComputePeaks(
        float[] samples, int channels, int frameCount, int bucketCap = Buckets)
    {
        if (frameCount == 0)
            throw new InvalidOperationException("sin audio");

        int buckets = Math.Min(bucketCap, frameCount);
        var mins = new float[buckets];
        var maxs = new float[buckets];
        for (int b = 0; b < buckets; b++)
        {
            long f0 = (long)b * frameCount / buckets;
            long f1 = (long)(b + 1) * frameCount / buckets;
            float mn = float.MaxValue, mx = float.MinValue;
            for (long f = f0; f < f1; f++)
            {
                // mezcla mono: media de canales del frame
                float sum = 0;
                long idx = f * channels;
                for (int c = 0; c < channels; c++) sum += samples[idx + c];
                float v = sum / channels;
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
            mins[b] = mn == float.MaxValue ? 0 : mn;
            maxs[b] = mx == float.MinValue ? 0 : mx;
        }
        return new WaveformPeaks(mins, maxs);
    }

    /// <summary>Bucket que le toca dibujar a la columna de píxel <paramref
    /// name="x"/>, de un total de <paramref name="width"/> columnas, sobre
    /// <paramref name="bucketCount"/> buckets disponibles. Mismo vecino-más-
    /// cercano que OnRender usaba en línea (no promedia un rango: si hay más
    /// buckets que columnas, el resto simplemente no se pinta esa columna —
    /// comportamiento de HOY, no una mejora).</summary>
    public static int ColumnBucketIndex(int x, double width, int bucketCount) =>
        Math.Min(bucketCount - 1, (int)(x / Math.Max(1.0, width) * bucketCount));

    // ------------------------------------------------------------------
    // Mapeo tiempo ↔ píxel
    // ------------------------------------------------------------------

    public static double SecToX(double sec, double duration, double width) =>
        duration <= 0 ? 0.0 : Math.Clamp(sec / duration, 0.0, 1.0) * width;

    public static double XToSec(double x, double duration, double width) =>
        duration <= 0 ? 0.0 : Math.Clamp(x / Math.Max(1.0, width), 0.0, 1.0) * duration;

    /// <summary>Fin efectivo del trim: 0 (o negativo) significa "hasta el
    /// final", igual que Cue.TrimEnd en el motor.</summary>
    public static double EffectiveEnd(double end, double duration) => end > 0 ? end : duration;

    /// <summary>Formato de una marca de tiempo de la regla ("m:ss.s").
    /// Mismo formato que WaveformControl.Fmt tenía en línea — DISTINTO del de
    /// TimelineLayout.FormatTime (esa regla llega a horas; esta, pensada para
    /// un solo clip, no lo necesita).</summary>
    public static string FormatTime(double s)
    {
        int m = (int)s / 60;
        return $"{m}:{s - m * 60:00.0}";
    }

    // ------------------------------------------------------------------
    // Manejadores de trim: hit-test y arrastre
    // ------------------------------------------------------------------

    /// <summary>Qué manejador agarra un clic/toque en <paramref name="x"/>.
    /// Mismo criterio que OnMouseDown tenía en línea: dentro del radio de
    /// acierto gana el más cercano de los dos; empatado o fuera del radio,
    /// gana el manejador más cercano sin más.</summary>
    public static WaveformHandle PickHandle(double x, double startX, double endX, double hitRadius = HandleHit)
    {
        double dStart = Math.Abs(x - startX);
        double dEnd = Math.Abs(x - endX);
        if (dStart <= hitRadius && dStart <= dEnd)
            return WaveformHandle.Start;
        if (dEnd <= hitRadius)
            return WaveformHandle.End;
        return dStart <= dEnd ? WaveformHandle.Start : WaveformHandle.End;
    }

    /// <summary>Nuevo inicio al arrastrar el manejador de entrada: no puede
    /// pasar del fin actual (o de la duración total, si el fin es "hasta el
    /// final"). Mismo clamp que UpdateDrag tenía en línea.</summary>
    public static double DragStart(double sec, double currentEnd, double duration) =>
        Math.Min(sec, EffectiveEnd(currentEnd, duration));

    /// <summary>Nuevo fin al arrastrar el manejador de salida: no puede
    /// quedar antes del inicio actual. Mismo clamp que UpdateDrag tenía en línea.</summary>
    public static double DragEnd(double sec, double currentStart) =>
        Math.Max(sec, currentStart);
}
