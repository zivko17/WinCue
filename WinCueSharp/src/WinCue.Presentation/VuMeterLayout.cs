// =============================================================================
// VuMeterLayout.cs — el CÁLCULO del VU meter, sin framework.
// =============================================================================
// Piloto del traslado a Win2D (ver el encargo, hitos-0-1): de los siete
// controles que WinCue pinta a mano con DrawingContext, el VU es el más
// pequeño (175 líneas en src/WinCue.App/VuMeterControl.cs). Antes de tocar un
// solo Win2D en Secuence, se separa aquí qué es CÁLCULO —mapeo de nivel a
// posición, ataque/release, retención de pico, testigo de clip, colores por
// zona— de qué es PINTADO (las llamadas a DrawingContext o a CanvasControl).
//
// Los valores son LOS DE HOY, sin tocar ni uno: proceden literalmente de
// VuMeterControl.SetLevel/Decay/OnRender. Un cambio aquí se nota en pantalla
// en los dos productos (WinCue Y Secuence) y es un fallo, no un ajuste.
//
// VuMeterState lleva el ESTADO (nivel, pico, testigo de clip) porque es lo
// que en el original vive como campos mutables de la clase de WPF. Aquí sigue
// siendo mutable por el mismo motivo: un VU recibe reports muchas veces por
// segundo y no hay necesidad de inmutabilidad para eso — hacerlo un record
// inmutable solo obligaría a cada llamador a reasignar su copia sin ganar nada.
//
// VuMeterLayout lleva la GEOMETRÍA: funciones puras (ancho, alto, nivel/pico,
// ¿vertical?) → rectángulos en píxeles. VuMeterPalette lleva los COLORES por
// zona, como tokens de Palette (uint), no como Brush/Color de ningún
// framework — cada interfaz los convierte a lo que sepa pintar.
// =============================================================================

using System.Diagnostics;

namespace WinCue.Presentation;

/// <summary>Rectángulo neutral (sin framework) para la geometría del VU.</summary>
public readonly record struct VuMeterRect(double X, double Y, double Width, double Height);

/// <summary>Un punto de una escala de color (offset 0..1, color de Palette).</summary>
public readonly record struct VuMeterGradientStop(double Offset, uint Color);

/// <summary>Estado de un VU meter: nivel con ataque/release, pico con
/// retención y caída, testigo de clip con retención. Un VuMeterState por
/// canal (igual que hoy una VuMeterControl por canal en WinCue.App).</summary>
public sealed class VuMeterState
{
    // ═════════════════════════ LEY DE NIVEL (dB) ═════════════════════════

    /// <summary>Suelo de la escala mostrada: −60 dBFS.</summary>
    public const double DbFloor = -60.0;

    /// <summary>Techo de la escala mostrada: 0 dBFS.</summary>
    public const double DbCeiling = 0.0;

    /// <summary>Por debajo de este RMS lineal no hay señal medible: se
    /// devuelve 0 directo, sin pasar por log10 (que daría −∞).</summary>
    public const double SilenceFloor = 0.00001;

    /// <summary>RMS lineal a partir del cual se considera saturación
    /// (~0 dBFS): engancha el testigo de clip.</summary>
    public const float ClipThreshold = 0.985f;

    // ═════════════════════════ ATAQUE / RELEASE ═════════════════════════

    /// <summary>Peso del valor NUEVO en el release suave (ataque, en cambio,
    /// es instantáneo: ver <see cref="Report"/>).</summary>
    private const double ReleaseNewWeight = 0.45;

    private const double ReleaseOldWeight = 0.55;

    // ═════════════════════════ DECAIMIENTO (Decay, cada 50 ms) ═════════════

    /// <summary>Tras cuánto sin recibir reports empieza a caer el nivel
    /// (silencio o parada), en milisegundos.</summary>
    public const double LevelIdleMs = 120;

    /// <summary>Factor de caída del nivel por tick de <see cref="Decay"/>.</summary>
    public const double LevelDecayFactor = 0.78;

    /// <summary>Retención del pico antes de empezar a caer, en milisegundos.</summary>
    public const double PeakHoldMs = 1_200;

    /// <summary>Factor de caída del pico por tick de <see cref="Decay"/>
    /// (nunca cae por debajo del nivel actual: ver <see cref="Decay"/>).</summary>
    public const double PeakDecayFactor = 0.90;

    /// <summary>Cuánto queda enganchado el testigo de clip tras saturar, en
    /// milisegundos.</summary>
    public const double ClipHoldMs = 3_000;

    /// <summary>Por debajo de este valor se considera "apagado" y se
    /// redondea a 0 en vez de dejarlo decayendo indefinidamente.</summary>
    private const double SnapToZero = 0.001;

    /// <summary>Nivel RMS instantáneo, ya normalizado a la escala dB (0..1).</summary>
    public double Level { get; private set; }

    /// <summary>Pico con retención, normalizado a la escala dB (0..1).</summary>
    public double Peak { get; private set; }

    private long _peakTicks;
    private long _lastUpdateTicks;
    private long _clipTicks;

    /// <summary>RMS lineal (0..1) → posición normalizada (0..1) en escala dB
    /// (−60…0 dBFS). Es como se lee un VU de verdad: un tono a −15 dB luce al
    /// ~75 %, no al ~17,8 % que daría una escala lineal.</summary>
    public static double Normalize(float rms)
    {
        if (rms <= SilenceFloor)
            return 0.0;
        double db = 20.0 * Math.Log10(rms);
        return Math.Clamp((db - DbFloor) / (DbCeiling - DbFloor), 0.0, 1.0);
    }

    /// <summary>Un report de nivel, tal como llega del motor de audio varias
    /// veces por segundo. Ataque instantáneo, release suave POR CADA report:
    /// la barra "baila" con la señal en vez de solo subir y clavarse en el
    /// máximo visto.</summary>
    /// <param name="rms">Nivel RMS instantáneo, lineal (0..1).</param>
    /// <param name="nowTicks">Instante actual (<see cref="Stopwatch.GetTimestamp"/>).</param>
    public void Report(float rms, long nowTicks)
    {
        double norm = Normalize(rms);

        Level = norm >= Level ? norm : Level * ReleaseOldWeight + norm * ReleaseNewWeight;

        _lastUpdateTicks = nowTicks;
        if (norm >= Peak)
        {
            Peak = norm;
            _peakTicks = nowTicks;
        }
        if (rms >= ClipThreshold)
            _clipTicks = nowTicks;
    }

    /// <summary>Tick periódico de caída (cada 50 ms en el pintado original).
    /// Devuelve true si algo cambió, para que quien pinte sepa si hace falta
    /// invalidar.</summary>
    public bool Decay(long nowTicks)
    {
        bool dirty = false;

        // Nivel: caída suave cuando dejan de llegar reports (silencio/parada)
        if (Level > SnapToZero &&
            Stopwatch.GetElapsedTime(_lastUpdateTicks, nowTicks).TotalMilliseconds > LevelIdleMs)
        {
            Level *= LevelDecayFactor;
            if (Level < SnapToZero) Level = 0;
            dirty = true;
        }

        // Pico: retención y después caída, nunca por debajo del nivel actual
        if (Peak > SnapToZero &&
            Stopwatch.GetElapsedTime(_peakTicks, nowTicks).TotalMilliseconds > PeakHoldMs)
        {
            Peak = Math.Max(Peak * PeakDecayFactor, Level);
            if (Peak < SnapToZero) Peak = 0;
            dirty = true;
        }

        return dirty;
    }

    /// <summary>Testigo de clip: enganchado <see cref="ClipHoldMs"/> tras
    /// tocar ~0 dBFS. false si nunca ha saturado.</summary>
    public bool IsClipped(long nowTicks) =>
        _clipTicks != 0 &&
        Stopwatch.GetElapsedTime(_clipTicks, nowTicks).TotalMilliseconds < ClipHoldMs;
}

/// <summary>Geometría del VU: funciones puras que traducen estado (nivel,
/// pico, clip) y tamaño del control a rectángulos en píxeles. Ni un tipo de
/// framework — cada interfaz dibuja estos <see cref="VuMeterRect"/> con lo
/// que sepa (DrawingContext.DrawRectangle, CanvasDrawingSession.FillRectangle…).</summary>
public static class VuMeterLayout
{
    /// <summary>Marcas de escala del modo vertical, en fracción de alto (0..1).</summary>
    public static readonly double[] VerticalTickFractions = { 0.25, 0.5, 0.75 };

    /// <summary>Grosor del testigo de clip (LED), en píxeles.</summary>
    public const double ClipLedThickness = 5.0;

    /// <summary>Grosor de la marca de pico, en píxeles.</summary>
    public const double PeakThickness = 2.0;

    /// <summary>Por debajo de este pico no se dibuja la marca (demasiado
    /// fina para significar algo).</summary>
    public const double PeakDrawThreshold = 0.01;

    /// <summary>Marco exterior del medidor (fondo + borde), inset medio
    /// píxel para un trazo de 1 px nítido.</summary>
    public static VuMeterRect FrameRect(double width, double height) =>
        new(0.5, 0.5, width - 1, height - 1);

    /// <summary>Testigo de clip: null si no está enganchado.</summary>
    public static VuMeterRect? ClipLedRect(double width, double height, bool vertical, bool clipped)
    {
        if (!clipped)
            return null;
        return vertical
            ? new VuMeterRect(0.5, 0.5, width - 1, ClipLedThickness)
            : new VuMeterRect(width - ClipLedThickness - 0.5, 0.5, ClipLedThickness, height - 1);
    }

    /// <summary>Rectángulo de recorte de la barra de nivel: la barra se
    /// pinta a tamaño completo y se recorta a esto (mismo patrón que
    /// DrawingContext.PushClip en el original). null si no hay nivel.</summary>
    public static VuMeterRect? LevelBarClipRect(double width, double height, bool vertical, double level)
    {
        if (level <= 0)
            return null;
        return vertical
            ? new VuMeterRect(0, height * (1 - level), width, height * level)
            : new VuMeterRect(0, 0, level * width, height);
    }

    /// <summary>Marca de pico. null si el pico está por debajo de
    /// <see cref="PeakDrawThreshold"/> (no se dibuja).</summary>
    public static VuMeterRect? PeakRect(double width, double height, bool vertical, double peak)
    {
        if (peak <= PeakDrawThreshold)
            return null;
        if (vertical)
        {
            double y = Math.Max(height * (1 - peak), 0);
            return new VuMeterRect(0, y, width, PeakThickness);
        }
        double x = Math.Min(peak * width, width - PeakThickness);
        return new VuMeterRect(x, 0, PeakThickness, height);
    }
}

/// <summary>Colores del VU, como tokens de <see cref="Palette"/> (uint), no
/// como Brush/Color de ningún framework.</summary>
public static class VuMeterPalette
{
    /// <summary>Fondo del medidor.</summary>
    public const uint Background = Palette.MeterBg;

    /// <summary>Marco (1 px).</summary>
    public const uint Frame = Palette.BorderMeter;

    /// <summary>Marcas de escala (modo vertical). Va con <see cref="TickAlpha"/>:
    /// el original las pinta semitransparentes (Theme.Alpha(TextDimmer, 90)).</summary>
    public const uint Tick = Palette.TextDimmer;

    /// <summary>Alfa (0..255) de las marcas de escala.</summary>
    public const byte TickAlpha = 90;

    /// <summary>Marca de pico.</summary>
    public const uint Peak = Palette.White;

    /// <summary>Testigo de clip.</summary>
    public const uint Clip = Palette.Clip;

    /// <summary>Degradado de la barra horizontal: verde hasta el 62 %, ámbar
    /// al 80 %, rojo al 100 % — izquierda a derecha.</summary>
    public static readonly VuMeterGradientStop[] HorizontalBar =
    {
        new(0.00, Palette.Go),
        new(0.62, Palette.Go),
        new(0.80, Palette.Standby),
        new(1.00, Palette.Pgm),
    };

    /// <summary>Degradado de la barra vertical: rojo arriba, ámbar al 20 %
    /// (desde arriba), verde desde el 38 % hasta abajo — la barra crece desde
    /// abajo, así que el verde queda donde vive el nivel bajo.</summary>
    public static readonly VuMeterGradientStop[] VerticalBar =
    {
        new(0.00, Palette.Pgm),
        new(0.20, Palette.Standby),
        new(0.38, Palette.Go),
        new(1.00, Palette.Go),
    };
}
