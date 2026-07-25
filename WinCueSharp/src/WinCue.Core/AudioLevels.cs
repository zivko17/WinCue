// =============================================================================
// AudioLevels.cs — Rango y LEY de los faders de volumen
// =============================================================================
//
// Una sola fuente para tres cosas que antes estaban repartidas a mano por el
// núcleo, el motor y la interfaz:
//
//   · RANGO      — hasta dónde llega un fader. El techo pasa de la unidad
//                  (0 dB) a +10 dB: se puede REFORZAR, no solo atenuar.
//   · UNIDADES   — lo que se guarda sigue siendo GANANCIA LINEAL (1,0 = 0 dB),
//                  igual que antes. Un show viejo con volume=1.0 sigue
//                  significando exactamente 0 dB; nada que migrar.
//   · LEY        — cómo se reparte el recorrido del fader entre esos dB. Un
//                  fader lineal en ganancia es inservible: −20 dB caería al 10 %
//                  del recorrido y −45 dB al 0,5 %, con todo el rango útil
//                  apelotonado en el último milímetro. La ley de abajo reparte
//                  el recorrido por DECIBELIOS, como una mesa de verdad, y de
//                  paso da las posiciones exactas donde rotular la escala.
//
// OJO al orden: la ganancia guardada no cambia de significado, pero la POSICIÓN
// del fader para una ganancia dada sí. Es a propósito — es lo que hace que la
// escala en dB caiga donde tiene que caer.
// =============================================================================

namespace WinCue.Core;

/// <summary>Rango y ley de los faders de volumen (mixer e inspector).</summary>
public static class AudioLevels
{
    /// <summary>Techo del fader en dB: +10 dB de refuerzo sobre la unidad.</summary>
    public const double MaxGainDb = 10.0;

    /// <summary>Suelo ROTULADO de la escala. Por debajo el fader va a silencio.</summary>
    public const double MinGainDb = -60.0;

    /// <summary>El techo como ganancia lineal (≈ 3,162).</summary>
    public static readonly double MaxGain = FromDb(MaxGainDb);

    /// <summary>Ganancia → dB. Silencio devuelve −∞.</summary>
    public static double ToDb(double gain) =>
        gain <= 0.0000001 ? double.NegativeInfinity : 20.0 * Math.Log10(gain);

    /// <summary>dB → ganancia lineal.</summary>
    public static double FromDb(double db) => Math.Pow(10.0, db / 20.0);

    /// <summary>Puntos de la ley: (posición del recorrido 0..1, dB). Entre dos
    /// puntos se interpola en DECIBELIOS, así que el tramo es recto en dB y la
    /// escala se rotula justo en estas posiciones. La unidad (0 dB) cae a tres
    /// cuartos del recorrido, que es donde la busca la mano en una mesa.</summary>
    private static readonly (double Pos, double Db)[] Law =
    {
        (0.03, -60.0),
        (0.10, -45.0),
        (0.20, -30.0),
        (0.33, -20.0),
        (0.52, -10.0),
        (0.63,  -5.0),
        (0.75,   0.0),
        (0.88,   5.0),
        (1.00,  10.0),
    };

    /// <summary>Ganancia del punto más bajo ROTULADO (−60 dB).</summary>
    private static readonly double FloorGain = FromDb(MinGainDb);

    /// <summary>Valores de dB que rotula la escala, de arriba abajo. El −∞ del
    /// fondo lo pinta la escala aparte: no es un punto de la ley.</summary>
    public static readonly double[] ScaleTicks =
        { 10.0, 5.0, 0.0, -5.0, -10.0, -20.0, -30.0, -45.0, -60.0 };

    /// <summary>Posición del recorrido (0..1) → ganancia lineal.
    /// La posición 0 es SILENCIO, no −60 dB: el fader abajo del todo calla.</summary>
    public static double PositionToGain(double pos)
    {
        if (pos <= 0.0)
            return 0.0;
        if (pos >= 1.0)
            return MaxGain;
        // COLA DE SILENCIO: por debajo del −60 dB rotulado no se puede seguir
        // interpolando en dB (el silencio es −∞ y no hay recorrido que le
        // llegue), así que ese último trocito se recorre en GANANCIA hasta 0.
        // Es lo que hace que el fader abajo del todo calle de verdad, y que la
        // marca de −60 caiga donde el fader da −60 y no donde ya no suena.
        if (pos < Law[0].Pos)
            return pos / Law[0].Pos * FloorGain;
        for (int i = 1; i < Law.Length; i++)
        {
            if (pos > Law[i].Pos)
                continue;
            var (p0, d0) = Law[i - 1];
            var (p1, d1) = Law[i];
            double t = (pos - p0) / (p1 - p0);
            return FromDb(d0 + t * (d1 - d0));
        }
        return MaxGain;
    }

    /// <summary>Ganancia lineal → posición del recorrido (0..1). Inversa exacta
    /// de <see cref="PositionToGain"/> en los puntos de la ley.</summary>
    public static double GainToPosition(double gain)
    {
        if (gain <= 0.0)
            return 0.0;
        // La misma cola de silencio, al revés.
        if (gain < FloorGain)
            return gain / FloorGain * Law[0].Pos;
        double db = ToDb(gain);
        if (db >= MaxGainDb)
            return 1.0;
        for (int i = 1; i < Law.Length; i++)
        {
            if (db > Law[i].Db)
                continue;
            var (p0, d0) = Law[i - 1];
            var (p1, d1) = Law[i];
            double t = (db - d0) / (d1 - d0);
            return p0 + t * (p1 - p0);
        }
        return 1.0;
    }

    /// <summary>Posición (0..1) de un dB en la escala, para rotularla.</summary>
    public static double DbToPosition(double db) => GainToPosition(FromDb(db));

    /// <summary>Texto de una ganancia en dB, con el −∞ del silencio.</summary>
    public static string DbLabel(double gain) =>
        gain <= 0.0001 ? "−∞ dB" : $"{ToDb(gain):+0.0;-0.0} dB";
}
