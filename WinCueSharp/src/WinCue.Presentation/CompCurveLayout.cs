// =============================================================================
// CompCurveLayout.cs — el CÁLCULO de la gráfica del compresor, sin framework.
// =============================================================================
// Traslado a Win2D (ver el encargo, hitos-0-1). Entra el mapeo de ejes
// entrada/salida en dB↔píxel, la función de transferencia (idéntica a la que
// WinCue.Audio.EqChain aplica de verdad: por debajo del umbral 1:1, por
// encima umbral + exceso/ratio, y el makeup se suma siempre al final), el
// muestreo en píxeles de esa curva, el hueco reservado para el medidor GR y
// el arrastre del umbral (X) / rueda del ratio. NO entra el pintado — mismo
// reparto que EqCurveLayout.cs/GeometryLayout.cs.
//
// Los valores son LOS DE HOY: DbMin, GrScaleDb, MeterW y el propio orden de
// operaciones proceden LITERALMENTE de CompCurveCanvas (WinCue.App).
//
// OJO: esta función de transferencia es la fórmula del EDITOR (ganancia
// estática umbral/ratio/makeup), NO la del compresor en tiempo real de
// EqChain.Process (que además tiene ataque/release y detector de envolvente
// — no representable en una curva estática entrada→salida). Es la misma
// relación que EqChain.Configure usa para calcular InvRatioComp
// (1 − 1/ratio): a régimen permanente, con la envolvente ya asentada, la
// reducción de EqChain.Process converge a lo que esta curva predice — de ahí
// que sea la curva correcta para el editor y no una aproximación.
// =============================================================================

namespace WinCue.Presentation;

/// <summary>Cálculo puro de la gráfica del compresor (transferencia entrada→
/// salida, medidor GR aparte, arrastre del umbral y rueda del ratio).</summary>
public static class CompCurveLayout
{
    /// <summary>Suelo del eje (dB); el techo es 0 dB de entrada (la salida sí
    /// puede pasar de 0 con makeup, ver <see cref="TransferCurve"/>).</summary>
    public const double DbMin = -60.0;

    /// <summary>Fondo de escala del medidor GR, en dB de reducción.</summary>
    public const double GrScaleDb = 24.0;

    /// <summary>Ancho reservado al medidor GR, a la derecha de la gráfica.</summary>
    public const double MeterW = 16;

    /// <summary>Marcas del medidor GR (dB de reducción).</summary>
    public static readonly double[] GrMarks = { 6.0, 12, 18 };

    /// <summary>Líneas de la rejilla (dB, cada 12).</summary>
    public static readonly double[] GridDbLines = { -48.0, -36, -24, -12 };

    /// <summary>Ancho de la zona de la curva: el ancho total menos el hueco
    /// del medidor GR y su separación.</summary>
    public static double PlotWidth(double actualWidth, double meterW = MeterW) =>
        Math.Max(1, actualWidth - meterW - 4);

    // ------------------------------------------------------------------
    // Ejes: dB↔píxel (lineal, sobre PlotWidth)
    // ------------------------------------------------------------------

    public static double XFromDb(double db, double plotWidth, double dbMin = DbMin) =>
        (db - dbMin) / -dbMin * plotWidth;

    public static double DbFromX(double x, double plotWidth, double dbMin = DbMin) =>
        dbMin + Math.Clamp(x / plotWidth, 0, 1) * -dbMin;

    /// <summary>Igual de eje que la entrada, pero para la SALIDA — mismo
    /// origen (dB = 0 en Y = 0), así entrada y salida comparten regla.</summary>
    public static double YFromDb(double db, double height, double dbMin = DbMin) =>
        db / dbMin * height;

    // ------------------------------------------------------------------
    // Función de transferencia (ver la cabecera del archivo)
    // ------------------------------------------------------------------

    /// <summary>Salida en dB para una entrada en dB, con el umbral/ratio/
    /// makeup dados. Por debajo (o en) el umbral, 1:1; por encima, el exceso
    /// se divide por el ratio. El makeup se suma SIEMPRE, dentro y fuera de
    /// compresión — mismo comportamiento que OutDb tenía en línea.</summary>
    public static double OutDb(double inDb, double thresholdDb, double ratio, double makeupDb)
    {
        double outDb = inDb <= thresholdDb
            ? inDb
            : thresholdDb + (inDb - thresholdDb) / Math.Max(1.0, ratio);
        return outDb + makeupDb;
    }

    /// <summary>Puntos de la curva de transferencia, uno cada 2 px de la zona
    /// de la gráfica (sin el medidor), en Y acotada a un margen de 10 px
    /// fuera del lienzo — mismo paso y mismo margen que CompCurveCanvas.
    /// OnRender tenía en línea. La salida se acota a [DbMin, 6] antes de
    /// mapear a Y: con makeup alto la curva puede subir de 0 dB, y 6 dB de
    /// margen es lo que el original dejaba antes de recortar.</summary>
    public static IReadOnlyList<GeoPoint> TransferCurve(
        double thresholdDb, double ratio, double makeupDb, double plotWidth, double height)
    {
        var points = new List<GeoPoint>();
        int pw = (int)plotWidth;
        for (int px = 0; px <= pw; px += 2)
        {
            double outDb = Math.Clamp(OutDb(DbFromX(px, plotWidth), thresholdDb, ratio, makeupDb), DbMin, 6.0);
            double y = Math.Clamp(YFromDb(outDb, height), -10, height + 10);
            points.Add(new GeoPoint(px, y));
        }
        return points;
    }

    /// <summary>Posición del manejador de umbral sobre la curva (su propia
    /// salida, acotada a [DbMin, 0] — nunca por encima de 0 dB de salida
    /// aunque el makeup suba el resto de la curva).</summary>
    public static GeoPoint ThresholdPoint(double thresholdDb, double ratio, double makeupDb,
        double plotWidth, double height)
    {
        double tx = XFromDb(thresholdDb, plotWidth);
        double ty = YFromDb(Math.Clamp(OutDb(thresholdDb, thresholdDb, ratio, makeupDb), DbMin, 0), height);
        return new GeoPoint(tx, ty);
    }

    // ------------------------------------------------------------------
    // Interacción: arrastre del umbral, rueda del ratio
    // ------------------------------------------------------------------

    /// <summary>Nuevo umbral al arrastrar en X (redondeado a 0.1 dB, acotado
    /// a [-60, 0]).</summary>
    public static double ThresholdFromDragX(double x, double plotWidth) =>
        Math.Round(Math.Clamp(DbFromX(x, plotWidth), -60.0, 0.0), 1);

    /// <summary>Nuevo ratio al girar la rueda: ×1.15 por muesca (÷1.15 hacia
    /// abajo), acotado a 1..20, redondeado a 2 decimales.</summary>
    public static double RatioFromWheel(double currentRatio, bool wheelUp) =>
        Math.Round(Math.Clamp(currentRatio * (wheelUp ? 1.15 : 1.0 / 1.15), 1.0, 20.0), 2);

    /// <summary>Fracción [0, 1] de la escala del medidor GR que ocupa una
    /// reducción dada, para el relleno del medidor (crece de arriba abajo,
    /// eso es cosa del pintado — aquí solo la fracción).</summary>
    public static double GrFraction(double grDb, double grScaleDb = GrScaleDb) =>
        Math.Clamp(grDb / grScaleDb, 0, 1);
}
