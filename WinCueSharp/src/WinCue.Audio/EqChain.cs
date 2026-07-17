// =============================================================================
// EqChain.cs — Cadena de procesado por clip: ganancia (dB) → HPF → 4 bandas
// paramétricas → LPF
// =============================================================================
// Biquads del Audio EQ Cookbook (RBJ), forma directa II transpuesta, estado
// por canal. Configure() se llama desde el main thread; publica el array de
// etapas por intercambio de referencia, así el hilo RT nunca ve coeficientes
// a medio escribir. Los estados se conservan por índice al reconfigurar
// (edición en vivo sin clics). ResponseDb() da la curva teórica para la UI.
// =============================================================================

using WinCue.Core;

namespace WinCue.Audio;

public sealed class EqChain
{
    private sealed class Stage
    {
        public double B0, B1, B2, A1, A2;    // normalizados (a0 = 1)
        public double[] Z1 = Array.Empty<double>();
        public double[] Z2 = Array.Empty<double>();
    }

    /// <summary>Compresor feed-forward (detector de pico, enlazado en estéreo).</summary>
    private sealed class Comp
    {
        public double ThLin, InvRatioComp;   // (1 - 1/ratio)
        public double AttCoef, RelCoef;      // suavizado exponencial
        public double MakeupLin;
        public double Env;                   // envolvente (estado del RT)
    }

    private readonly int _sampleRate;
    private readonly int _channels;
    private Stage[] _stages = Array.Empty<Stage>();   // swap atómico de referencia
    private volatile float _gainLin = 1.0f;
    private Comp? _comp;                              // swap atómico de referencia

    public EqChain(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = Math.Max(1, channels);
    }

    /// <summary>Reconstruye la cadena (main thread). null = bypass.</summary>
    public void Configure(AudioEqParams? eq)
    {
        if (eq is null)
        {
            _gainLin = 1.0f;
            _stages = Array.Empty<Stage>();
            _comp = null;
            return;
        }
        _gainLin = (float)DbToLin(eq.GainDb);

        if (eq.Comp is { Enabled: true } c)
        {
            _comp = new Comp
            {
                ThLin = DbToLin(c.ThresholdDb),
                InvRatioComp = 1.0 - 1.0 / Math.Max(1.0, c.Ratio),
                AttCoef = Math.Exp(-1.0 / (Math.Max(0.1, c.AttackMs) / 1000.0 * _sampleRate)),
                RelCoef = Math.Exp(-1.0 / (Math.Max(5.0, c.ReleaseMs) / 1000.0 * _sampleRate)),
                MakeupLin = DbToLin(c.MakeupDb),
                Env = _comp?.Env ?? 0.0,   // continuidad al editar en vivo
            };
        }
        else
        {
            _comp = null;
        }

        var old = _stages;
        var list = new List<Stage>();
        foreach (var (b0, b1, b2, a1, a2) in Coefficients(eq, _sampleRate))
        {
            var s = new Stage { B0 = b0, B1 = b1, B2 = b2, A1 = a1, A2 = a2 };
            int idx = list.Count;
            if (idx < old.Length && old[idx].Z1.Length == _channels)
            {
                s.Z1 = old[idx].Z1;   // conservar estado → sin clic al editar
                s.Z2 = old[idx].Z2;
            }
            else
            {
                s.Z1 = new double[_channels];
                s.Z2 = new double[_channels];
            }
            list.Add(s);
        }
        _stages = list.ToArray();
    }

    /// <summary>Procesa in-place (hilo RT). Sin allocations.</summary>
    public void Process(float[] buffer, int offset, int samples)
    {
        float g = _gainLin;
        if (g != 1.0f)
            for (int i = 0; i < samples; i++)
                buffer[offset + i] *= g;

        var stages = _stages;
        int ch = _channels;
        foreach (var s in stages)
        {
            for (int i = 0; i + ch <= samples; i += ch)
            {
                for (int c = 0; c < ch; c++)
                {
                    int idx = offset + i + c;
                    double x = buffer[idx];
                    double y = s.B0 * x + s.Z1[c];
                    s.Z1[c] = s.B1 * x - s.A1 * y + s.Z2[c];
                    s.Z2[c] = s.B2 * x - s.A2 * y;
                    buffer[idx] = (float)y;
                }
            }
        }

        // Compresor (post-EQ), detector de pico enlazado entre canales
        var comp = _comp;
        if (comp is null)
        {
            CurrentGainReductionDb = 0f;
            return;
        }
        double env = comp.Env;
        double maxRedDb = 0.0;   // GR máxima del buffer (medidor de la UI)
        for (int i = 0; i + ch <= samples; i += ch)
        {
            double peak = 0;
            for (int c = 0; c < ch; c++)
            {
                double a = Math.Abs(buffer[offset + i + c]);
                if (a > peak) peak = a;
            }
            double coef = peak > env ? comp.AttCoef : comp.RelCoef;
            env = coef * env + (1.0 - coef) * peak;

            double gain = comp.MakeupLin;
            if (env > comp.ThLin)
            {
                double overDb = 20.0 * Math.Log10(env / comp.ThLin);
                double redDb = overDb * comp.InvRatioComp;
                if (redDb > maxRedDb)
                    maxRedDb = redDb;
                gain *= Math.Pow(10.0, -redDb / 20.0);
            }
            for (int c = 0; c < ch; c++)
                buffer[offset + i + c] = (float)(buffer[offset + i + c] * gain);
        }
        comp.Env = env;
        CurrentGainReductionDb = (float)maxRedDb;
    }

    /// <summary>Reducción de ganancia ACTUAL del compresor en dB (≥ 0; 0 =
    /// sin comprimir). La escribe el hilo RT por buffer; la lee la UI para
    /// el medidor GR. float: la lectura/escritura es atómica.</summary>
    public float CurrentGainReductionDb { get; private set; }

    // ------------------------------------------------------------------
    // Coeficientes (RBJ cookbook) — compartidos con la curva de la UI
    // ------------------------------------------------------------------

    private static IEnumerable<(double B0, double B1, double B2, double A1, double A2)>
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

    public static double DbToLin(double db) => Math.Pow(10.0, db / 20.0);

    /// <summary>Respuesta teórica de la cadena en dB a una frecuencia dada
    /// (para pintar la curva del editor; incluye la ganancia del clip).</summary>
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
