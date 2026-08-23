// =============================================================================
// EqCurveLayout.cs — el CÁLCULO del editor de EQ (curva + RTA + manejadores),
// sin framework.
// =============================================================================
// Traslado a Win2D (ver el encargo, hitos-0-1; séptimo y último control
// pintado a mano). Entra el mapeo de ejes del lienzo de la EQ — frecuencia↔
// píxel (LOGARÍTMICO: 100 Hz, 1 kHz y 10 kHz a la misma distancia entre sí) y
// dB↔píxel —, la escala del RTA, el muestreo en píxeles de la curva de
// respuesta (WinCue.Presentation.EqResponse), la balística del analizador
// (ataque directo, release y picos con caída lenta) y el hit-test/arrastre de
// los manejadores (4 bandas + HPF/LPF). NO entra el pintado (DrawingContext/
// CanvasDrawingSession, pinceles, texto) — eso sigue siendo cosa de cada
// interfaz, mismo reparto que GeometryLayout.cs.
//
// Los valores son LOS DE HOY: FMin/FMax, DbRange, RtaFloorDb/RtaTopDb, el
// paso de muestreo de la curva (3 px) y el propio orden de operaciones
// proceden LITERALMENTE de EqCurveCanvas (WinCue.App). Un cambio aquí se nota
// en pantalla en los DOS productos y es un fallo, no un ajuste.
//
// LA TRAMPA DE ESTE CONTROL (ver la cabecera del encargo): el eje de
// frecuencias es logarítmico — XFromFreq/FreqFromX son inversas EXACTAS la
// una de la otra, fijadas con pruebas en los extremos (20 Hz/20 kHz), las
// décadas (100/1k/10k) y un punto intermedio. El eje de dB es lineal pero
// invertido (dB positivo sube en pantalla) y con un margen de 6 px arriba y
// abajo para que la rejilla no toque el borde del lienzo — ese margen NO es
// decorativo: si se omite, YFromDb/DbFromY dejan de ser inversas exactas.
// =============================================================================

using WinCue.Core;

namespace WinCue.Presentation;

/// <summary>Índices de manejador: 0..3 = banda paramétrica, <see
/// cref="Hpf"/>/<see cref="Lpf"/> = filtros, <see cref="None"/> = ninguno.
/// Mismo convenio (enteros negativos para los filtros) que EqCurveCanvas ya
/// usaba en línea — cambiarlo aquí sería tocar comportamiento sin necesidad.</summary>
public static class EqHandle
{
    public const int None = int.MinValue;
    public const int Hpf = -2;
    public const int Lpf = -3;
}

/// <summary>Cálculo puro del lienzo de la EQ (curva + RTA + manejadores). Ni
/// un tipo de framework de interfaz — cada producto dibuja los GeoPoint/
/// GeoRect resultantes con lo que sepa.</summary>
public static class EqCurveLayout
{
    /// <summary>Rango del eje de frecuencias (Hz). Tres décadas exactas
    /// (20 → 20000 = ×1000 = 10³), de ahí el /3.0 en XFromFreq/FreqFromX.</summary>
    public const double FMin = 20.0, FMax = 20000.0;

    /// <summary>Rango del eje de ganancia (±dB).</summary>
    public const double DbRange = 18.0;

    /// <summary>Suelo y techo de la escala del RTA, en dBFS reales (ver la
    /// cabecera de EqCurveCanvas para el porqué de no llevar ningún offset de
    /// compensación: el medidor ya da dBFS honestos).</summary>
    public const double RtaFloorDb = -72.0, RtaTopDb = 6.0;

    /// <summary>Radio de acierto (píxeles) para agarrar un manejador.</summary>
    public const double HandleHitRadius = 10.0;

    /// <summary>Paso de muestreo (píxeles) de la curva de respuesta.</summary>
    public const int CurveStepPx = 3;

    /// <summary>Frecuencias de la rejilla vertical (décadas + subdivisiones).</summary>
    public static readonly double[] GridFrequencies =
        { 20.0, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };

    /// <summary>Líneas de dB de la rejilla horizontal (0 dB se dibuja aparte,
    /// con su propio trazo — ver DbFromY/YFromDb).</summary>
    public static readonly double[] GridDbLines = { -12.0, -6, 6, 12 };

    // ------------------------------------------------------------------
    // Ejes: frecuencia↔píxel (LOGARÍTMICO) y dB↔píxel
    // ------------------------------------------------------------------

    /// <summary>Frecuencia (20 Hz..20 kHz, se acota) → posición X en el
    /// lienzo. Logarítmico en base 10 sobre tres décadas: log10(f/20)/3 da 0
    /// en 20 Hz y 1 en 20000 Hz, con 100/1k/10k cayendo a distancias iguales
    /// entre sí (log10(100/20)=log10(5)≈0.70, log10(1000/20)=log10(50)≈1.70,
    /// log10(10000/20)=log10(500)≈2.70 — un salto de exactamente 1 años-luz
    /// logarítmico por década).</summary>
    public static double XFromFreq(double freq, double width) =>
        Math.Log10(Math.Clamp(freq, FMin, FMax) / FMin) / 3.0 * width;

    /// <summary>Inversa exacta de <see cref="XFromFreq"/>.</summary>
    public static double FreqFromX(double x, double width) =>
        FMin * Math.Pow(10.0, 3.0 * Math.Clamp(x / Math.Max(1, width), 0, 1));

    /// <summary>Ganancia (dB, ±DbRange) → posición Y. Lineal e invertida (dB
    /// positivo sube en pantalla) con 6 px de margen arriba/abajo para que la
    /// rejilla de ±DbRange no toque el borde.</summary>
    public static double YFromDb(double db, double height) =>
        height / 2.0 - db / DbRange * (height / 2.0 - 6);

    /// <summary>Inversa exacta de <see cref="YFromDb"/>.</summary>
    public static double DbFromY(double y, double height) =>
        (height / 2.0 - y) / (height / 2.0 - 6) * DbRange;

    /// <summary>dBFS del RTA (RtaFloorDb..RtaTopDb) → posición Y, 0 = arriba
    /// (a diferencia de YFromDb, que centra en 0 dB: el RTA es un medidor de
    /// nivel, no una curva con signo).</summary>
    public static double YFromRta(double db, double height) =>
        height * (1.0 - Math.Clamp((db - RtaFloorDb) / (RtaTopDb - RtaFloorDb), 0.0, 1.0));

    // ------------------------------------------------------------------
    // Curva de respuesta: muestreo en píxeles (EqResponse hace la matemática
    // del filtro; aquí solo se recorre el eje X y se convierte a Y)
    // ------------------------------------------------------------------

    /// <summary>Puntos de la curva de respuesta, uno cada <see
    /// cref="CurveStepPx"/> píxeles, en Y ya acotada a un margen de 20 px
    /// fuera del lienzo (igual que EqCurveCanvas.OnRender: la curva puede
    /// dispararse fuera de rango con Q muy alta, y no hace falta más
    /// resolución que la que un trazo de 1-2 px puede mostrar).</summary>
    public static IReadOnlyList<GeoPoint> ResponseCurve(AudioEqParams eq, double width, double height)
    {
        var points = new List<GeoPoint>();
        int w = (int)width;
        for (int px = 0; px <= w; px += CurveStepPx)
        {
            double db = EqResponse.ResponseDb(eq, FreqFromX(px, width));
            double y = Math.Clamp(YFromDb(db, height), -20, height + 20);
            points.Add(new GeoPoint(px, y));
        }
        return points;
    }

    /// <summary>EQ del cue en el tipo que EqResponse.ResponseDb necesita, con
    /// GainDb a 0: la curva de este lienzo muestra solo los FILTROS (HPF/LPF
    /// + 4 bandas); la ganancia de clip tiene su propio fader al lado y no
    /// se dibuja aquí — mismo criterio que ParamsFrom tenía en línea.</summary>
    public static AudioEqParams EqParamsFromCue(Cue c) => new(
        GainDb: 0.0,
        EqEnabled: c.EqEnabled,
        HpfOn: c.HpfOn, HpfFreq: c.HpfFreq,
        LpfOn: c.LpfOn, LpfFreq: c.LpfFreq,
        Bands: new[]
        {
            new EqBand(c.Eq1Freq, c.Eq1Gain, c.Eq1Q),
            new EqBand(c.Eq2Freq, c.Eq2Gain, c.Eq2Q),
            new EqBand(c.Eq3Freq, c.Eq3Gain, c.Eq3Q),
            new EqBand(c.Eq4Freq, c.Eq4Gain, c.Eq4Q),
        });

    // ------------------------------------------------------------------
    // RTA: área del espectro y segmentos de pico, ya en coordenadas de
    // pantalla — el pintado solo tiene que trazar/rellenar estos puntos.
    // ------------------------------------------------------------------

    /// <summary>Contorno del área del RTA (degradado + contorno), cerrada
    /// contra la base del lienzo. Vacío si el espectro tiene menos de 2
    /// bandas (nada que dibujar). Mismo trazado que EqCurveCanvas.OnRender
    /// construía a mano con StreamGeometry.</summary>
    public static IReadOnlyList<GeoPoint> RtaAreaPoints(
        IReadOnlyList<float> smooth, double gainDb, double width, double height)
    {
        if (smooth.Count < 2)
            return Array.Empty<GeoPoint>();
        var pts = new List<GeoPoint>(smooth.Count + 3)
        {
            new(0, height + 1),
            new(0, YFromRta(smooth[0] + gainDb, height)),
        };
        for (int i = 0; i < smooth.Count; i++)
            pts.Add(new GeoPoint((i + 0.5) / smooth.Count * width, YFromRta(smooth[i] + gainDb, height)));
        pts.Add(new GeoPoint(width, YFromRta(smooth[^1] + gainDb, height)));
        pts.Add(new GeoPoint(width, height + 1));
        return pts;
    }

    /// <summary>Un segmento fino por banda de pico (peak-hold), salvo las que
    /// ya caen pegadas al fondo (y &gt;= height - 1) — mismo recorte que el
    /// original, que no dibujaba picos indistinguibles del suelo.</summary>
    public static IReadOnlyList<(GeoPoint A, GeoPoint B)> RtaPeakSegments(
        IReadOnlyList<float> peak, double gainDb, double width, double height)
    {
        var segs = new List<(GeoPoint, GeoPoint)>();
        for (int i = 0; i < peak.Count; i++)
        {
            double y = YFromRta(peak[i] + gainDb, height);
            if (y >= height - 1)
                continue;
            double x0 = (double)i / peak.Count * width + 1;
            double x1 = (double)(i + 1) / peak.Count * width - 1;
            segs.Add((new GeoPoint(x0, y), new GeoPoint(x1, y)));
        }
        return segs;
    }

    // ------------------------------------------------------------------
    // RTA: balística (ataque directo, release ~30 dB/s, picos con caída
    // ~8 dB/s) — mismos coeficientes que SetSpectrum tenía en línea
    // ------------------------------------------------------------------

    public const float RtaReleasePerFrameDb = 2.4f;
    public const float RtaPeakDecayPerFrameDb = 0.65f;

    /// <summary>Un paso de balística del RTA: ataque instantáneo (la traza
    /// sigue al espectro que sube sin retardo), release suave (cae como mucho
    /// <see cref="RtaReleasePerFrameDb"/> dB por fotograma) y picos con caída
    /// lenta (<see cref="RtaPeakDecayPerFrameDb"/> dB por fotograma). Si
    /// <paramref name="prevSmooth"/> no existe o no coincide en longitud con
    /// <paramref name="spectrum"/> (primer espectro, o cambio de resolución de
    /// FFT) arranca clonando el espectro para las dos trazas — mismo
    /// contrato que SetSpectrum tenía en línea, donde _smooth y _peakHold
    /// siempre se reinician juntos y a partir de ahí SIEMPRE tienen la misma
    /// longitud (de ahí que <paramref name="prevPeak"/> se asuma también
    /// válido cuando <paramref name="prevSmooth"/> lo es).</summary>
    public static (float[] Smooth, float[] Peak) RtaStep(
        float[]? prevSmooth, float[]? prevPeak, float[] spectrum)
    {
        if (prevSmooth is null || prevSmooth.Length != spectrum.Length || prevPeak is null)
            return ((float[])spectrum.Clone(), (float[])spectrum.Clone());

        var smooth = new float[spectrum.Length];
        var peak = new float[spectrum.Length];
        for (int i = 0; i < spectrum.Length; i++)
        {
            float v = spectrum[i];
            smooth[i] = v >= prevSmooth[i] ? v : Math.Max(v, prevSmooth[i] - RtaReleasePerFrameDb);
            peak[i] = Math.Max(v, prevPeak[i] - RtaPeakDecayPerFrameDb);
        }
        return (smooth, peak);
    }

    // ------------------------------------------------------------------
    // Manejadores: posición, hit-test y arrastre (4 bandas + HPF/LPF)
    // ------------------------------------------------------------------

    /// <summary>Posición en pantalla de una banda paramétrica.</summary>
    public static GeoPoint BandPoint(double freq, double gainDb, double width, double height) =>
        new(XFromFreq(freq, width), YFromDb(gainDb, height));

    /// <summary>Posición en pantalla de un manejador de filtro (siempre sobre
    /// la línea de 0 dB: HPF/LPF solo tienen frecuencia, no ganancia).</summary>
    public static GeoPoint FilterPoint(double freq, double width, double height) =>
        new(XFromFreq(freq, width), YFromDb(0, height));

    /// <summary>Manejador bajo el punto dado, o <see cref="EqHandle.None"/>.
    /// Se comprueban las bandas ANTES que los filtros — mismo orden que
    /// HitHandle tenía en línea (con las 4 bandas y los 2 filtros nunca
    /// solapados en la práctica, el orden no cambia el resultado, pero
    /// conservarlo evita cualquier sorpresa si algún día se solapan).</summary>
    public static int HitHandle(GeoPoint p, IReadOnlyList<(double Freq, double Gain)> bands,
        double hpfFreq, double lpfFreq, double width, double height, double radius = HandleHitRadius)
    {
        for (int i = 0; i < bands.Count; i++)
            if (GeometryLayout.Dist(BandPoint(bands[i].Freq, bands[i].Gain, width, height), p) <= radius)
                return i;
        if (GeometryLayout.Dist(FilterPoint(hpfFreq, width, height), p) <= radius)
            return EqHandle.Hpf;
        if (GeometryLayout.Dist(FilterPoint(lpfFreq, width, height), p) <= radius)
            return EqHandle.Lpf;
        return EqHandle.None;
    }

    /// <summary>Frecuencia al arrastrar (redondeada al Hz, sin acotar todavía
    /// — el llamador decide el rango según sea banda/HPF/LPF).</summary>
    public static double FreqFromDragX(double x, double width) =>
        Math.Round(FreqFromX(x, width));

    /// <summary>Ganancia al arrastrar una banda (redondeada a 0.1 dB, acotada
    /// a ±DbRange).</summary>
    public static double GainFromDragY(double y, double height) =>
        Math.Round(Math.Clamp(DbFromY(y, height), -DbRange, DbRange), 1);

    /// <summary>Rango del HPF: 20..2000 Hz (mismo tope que la caja numérica de
    /// hpf_freq en el inspector).</summary>
    public static double ClampHpfFreq(double freq) => Math.Clamp(freq, 20.0, 2000.0);

    /// <summary>Rango del LPF: 200..20000 Hz.</summary>
    public static double ClampLpfFreq(double freq) => Math.Clamp(freq, 200.0, 20000.0);

    /// <summary>Nueva Q al girar la rueda sobre una banda: ×1.12 por muesca
    /// (÷1.12 hacia abajo), acotada a 0.1..12, redondeada a 2 decimales.</summary>
    public static double QFromWheel(double currentQ, bool wheelUp) =>
        Math.Round(Math.Clamp(currentQ * (wheelUp ? 1.12 : 1.0 / 1.12), 0.1, 12.0), 2);
}
