// =============================================================================
// FrameCadence.cs — Selección de fotograma por PTS (capa L2, parte pura)
// =============================================================================
// El compositor NO pregunta "¿qué frame hay?" sino "¿qué frame corresponde al
// instante T?". Esta es esa regla, aislada y sin GPU para poder probarla.
//
// REGLA: el último fotograma cuyo PTS no sea POSTERIOR a T. Nunca se enseña un
// fotograma antes de su hora, y no hay empates que romper (elegir "el más
// cercano" sí los tiene: con 30p en una salida de 60 Hz, la mitad de los
// instantes caen exactamente entre dos fotogramas y el desempate decidiría la
// cadencia).
//
// De esa regla salen SOLAS las cadencias correctas:
//   30p → 60 Hz : 2:2   (cada fotograma dos veces, sin judder)
//   24p → 60 Hz : 3:2   (el pulldown clásico, alternando 3 y 2)
//   60p → 60 Hz : 1:1
// =============================================================================

namespace WinCue.Video.Gpu;

public static class FrameCadence
{
    /// <summary>Índice del último PTS que no supera <paramref name="target"/>,
    /// o -1 si todos son posteriores (la fuente aún no ha llegado a ese
    /// instante). <paramref name="pts"/> NO necesita estar ordenado: las
    /// fuentes publican en un anillo circular.</summary>
    public static int SelectLatestNotAfter(ReadOnlySpan<double> pts, double target)
    {
        int best = -1;
        double bestPts = double.NegativeInfinity;
        for (int i = 0; i < pts.Length; i++)
        {
            double p = pts[i];
            if (double.IsNaN(p) || p > target)
                continue;
            if (p > bestPts)
            {
                bestPts = p;
                best = i;
            }
        }
        return best;
    }
}
