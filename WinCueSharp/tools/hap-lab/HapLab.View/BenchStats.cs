namespace HapLab.View;

/// <summary>
/// Acumuladores de métricas de la sesión de presentación: latencias por fase
/// (subida, render, copia de presentación) y contadores de drops/lates.
/// </summary>
internal sealed class BenchStats
{
    private readonly object _gate = new();
    private readonly List<double> _uploadMs = new(4096);
    private readonly List<double> _renderMs = new(4096);
    private readonly List<double> _copyMs = new(4096);
    private int _latePresents;

    /// <summary>Frames que el reloj pedía y aún no estaban decodificados.</summary>
    public int LatePresents => _latePresents;

    public void AddLatePresent() => Interlocked.Increment(ref _latePresents);

    public void AddUpload(double ms)
    {
        lock (_gate)
        {
            _uploadMs.Add(ms);
        }
    }

    public void AddRender(double ms)
    {
        lock (_gate)
        {
            _renderMs.Add(ms);
        }
    }

    public void AddCopy(double ms)
    {
        lock (_gate)
        {
            _copyMs.Add(ms);
        }
    }

    public (double P50, double P95, double Max) Upload
    {
        get
        {
            lock (_gate)
            {
                return Summarize(_uploadMs);
            }
        }
    }

    public (double P50, double P95, double Max) Render
    {
        get
        {
            lock (_gate)
            {
                return Summarize(_renderMs);
            }
        }
    }

    public (double P50, double P95, double Max) Copy
    {
        get
        {
            lock (_gate)
            {
                return Summarize(_copyMs);
            }
        }
    }

    /// <summary>p50/p95/max de una serie de latencias (sin mutar la original).</summary>
    public static (double P50, double P95, double Max) Summarize(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return (0, 0, 0);
        }

        double[] sorted = [.. values];
        Array.Sort(sorted);
        return (Percentile(sorted, 0.50), Percentile(sorted, 0.95), sorted[^1]);
    }

    private static double Percentile(double[] sorted, double p)
    {
        double rank = p * (sorted.Length - 1);
        int lo = (int)rank;
        int hi = Math.Min(lo + 1, sorted.Length - 1);
        double frac = rank - lo;
        return sorted[lo] + ((sorted[hi] - sorted[lo]) * frac);
    }
}
