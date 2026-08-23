// =============================================================================
// EqResponse.cs — respuesta en frecuencia de la cadena de EQ, sin framework.
// =============================================================================
// Traslado a Win2D (séptimo y último control pintado a mano, ver el encargo
// hitos-0-1): la curva que se dibuja en el editor de EQ tiene que ser la
// MISMA que de verdad aplica el motor de audio — no una aproximación "que se
// parece". Por eso este archivo es un traslado LITERAL, coeficiente a
// coeficiente, de los biquads del Audio EQ Cookbook (RBJ) que WinCue.Audio.
// EqChain ya tenía: Peaking/Highpass/Lowpass, su combinación en Coefficients()
// según qué esté activo (HPF → bandas con ganancia ≠ 0 → LPF, el mismo orden
// que procesa el motor) y ResponseDb(), que evalúa H(e^jw) en una frecuencia
// dada sumando en dB la respuesta de cada etapa.
//
// EqChain.cs (WinCue.Audio) ahora DELEGA aquí en vez de tener su propia copia
// — Coefficients() alimenta tanto el procesado en tiempo real (Stage.B0.._2
// en EqChain.Configure) como la curva de pantalla, así que solo puede haber
// una fórmula. Si algún día divergieran, el fallo sería justo el que este
// control existe para evitar: una curva que miente sobre lo que suena.
//
// Por qué aquí y no en WinCue.Audio: estas fórmulas son matemática pura sobre
// tipos de WinCue.Core (AudioEqParams/EqBand), sin una sola línea de NAudio ni
// de estado en tiempo real (eso — Stage, Z1/Z2, el propio EqChain con su
// Process() de audio — SÍ es cosa de WinCue.Audio y se queda allí). Nació en
// WinCue.Presentation, pero eso obligaba a WinCue.Audio a referenciar la capa
// de presentación solo para llegar a una fórmula; en Core la ven Audio y
// Presentation sin que ninguno dependa del otro.
// =============================================================================

namespace WinCue.Core;

/// <summary>Respuesta en frecuencia de la cadena de EQ por clip (ganancia +
/// HPF + hasta 4 bandas paramétricas + LPF). Coeficientes RBJ, forma directa
/// II transpuesta — EXACTAMENTE los que usa WinCue.Audio.EqChain para
/// procesar audio de verdad.</summary>
public static class EqResponse
{
    /// <summary>Coeficientes normalizados (a0 = 1) de cada etapa activa, en el
    /// MISMO orden en que la cadena real las aplica: HPF → bandas con
    /// ganancia audible → LPF. Una banda con |ganancia| ≤ 0.01 dB ni se
    /// instancia (es, a efectos prácticos, transparente) — mismo umbral que
    /// EqChain.Configure usaba para no gastar una etapa de biquad en nada.</summary>
    public static IEnumerable<(double B0, double B1, double B2, double A1, double A2)>
        Coefficients(AudioEqParams eq, int fs)
    {
        if (eq.HpfOn)
            yield return Highpass(eq.HpfFreq, 0.7071, fs);
        if (eq.EqEnabled && eq.Bands is not null)
            foreach (var b in eq.Bands)
                if (Math.Abs(b.GainDb) > 0.01)
                    yield return Peaking(b.Freq, b.GainDb, b.Q, fs);
        if (eq.LpfOn)
            yield return Lowpass(eq.LpfFreq, 0.7071, fs);
    }

    private static (double, double, double, double, double) Peaking(
        double freq, double gainDb, double q, int fs)
    {
        double a = Math.Pow(10.0, gainDb / 40.0);
        double w0 = 2.0 * Math.PI * Clamp(freq, fs) / fs;
        double alpha = Math.Sin(w0) / (2.0 * Math.Max(0.05, q));
        double cosw = Math.Cos(w0);
        double a0 = 1.0 + alpha / a;
        return ((1.0 + alpha * a) / a0, -2.0 * cosw / a0, (1.0 - alpha * a) / a0,
                -2.0 * cosw / a0, (1.0 - alpha / a) / a0);
    }

    private static (double, double, double, double, double) Highpass(
        double freq, double q, int fs)
    {
        double w0 = 2.0 * Math.PI * Clamp(freq, fs) / fs;
        double alpha = Math.Sin(w0) / (2.0 * q);
        double cosw = Math.Cos(w0);
        double a0 = 1.0 + alpha;
        return ((1.0 + cosw) / 2.0 / a0, -(1.0 + cosw) / a0, (1.0 + cosw) / 2.0 / a0,
                -2.0 * cosw / a0, (1.0 - alpha) / a0);
    }

    private static (double, double, double, double, double) Lowpass(
        double freq, double q, int fs)
    {
        double w0 = 2.0 * Math.PI * Clamp(freq, fs) / fs;
        double alpha = Math.Sin(w0) / (2.0 * q);
        double cosw = Math.Cos(w0);
        double a0 = 1.0 + alpha;
        return ((1.0 - cosw) / 2.0 / a0, (1.0 - cosw) / a0, (1.0 - cosw) / 2.0 / a0,
                -2.0 * cosw / a0, (1.0 - alpha) / a0);
    }

    private static double Clamp(double freq, int fs) =>
        Math.Clamp(freq, 10.0, fs * 0.49);

    /// <summary>Respuesta teórica de la cadena en dB a una frecuencia dada
    /// (incluye la ganancia de clip, que no pasa por ningún biquad). Es la
    /// curva que se dibuja en el editor de EQ — de los dos productos.</summary>
    public static double ResponseDb(AudioEqParams eq, double freq, int sampleRate = 48000)
    {
        double db = eq.GainDb;
        double w = 2.0 * Math.PI * freq / sampleRate;
        double cosw = Math.Cos(w), sinw = Math.Sin(w);
        double cos2 = Math.Cos(2 * w), sin2 = Math.Sin(2 * w);
        foreach (var (b0, b1, b2, a1, a2) in Coefficients(eq, sampleRate))
        {
            // |H(e^jw)| con z^-1 = cos(w) - j·sin(w)
            double numRe = b0 + b1 * cosw + b2 * cos2;
            double numIm = -(b1 * sinw + b2 * sin2);
            double denRe = 1.0 + a1 * cosw + a2 * cos2;
            double denIm = -(a1 * sinw + a2 * sin2);
            double mag2 = (numRe * numRe + numIm * numIm)
                        / Math.Max(1e-18, denRe * denRe + denIm * denIm);
            db += 10.0 * Math.Log10(Math.Max(1e-12, mag2));
        }
        return db;
    }
}
