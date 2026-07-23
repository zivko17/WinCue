namespace HapLab;

/// <summary>
/// Referencia CPU de la conversión Scaled YCoCg → RGB de Hap Q (HapY), la
/// misma matemática que ejecuta el pixel shader de HapLab.View. Sirve para
/// validar el shader conceptualmente desde los tests (no se usa en el camino
/// caliente). Layout del DXT5-YCoCg según el shader de referencia de Vidvox
/// (hap-in-avfoundation, ScaledCoCgYToRGBA): Co = R, Cg = G, escala = B, Y = A.
/// </summary>
public static class YCoCgReference
{
    /// <summary>Offset de crominancia del shader de referencia: 128/255.</summary>
    public const double ChromaOffset = 128.0 / 255.0;

    /// <summary>
    /// Convierte un texel Scaled YCoCg (componentes normalizadas 0..1 tal cual
    /// salen del sampler sobre el DXT5) a RGB lineal 0..1 (sin clamp).
    /// </summary>
    /// <param name="co">Canal R del texel: crominancia naranja escalada.</param>
    /// <param name="cg">Canal G del texel: crominancia verde escalada.</param>
    /// <param name="scale">Canal B del texel: factor de escala cuantizado.</param>
    /// <param name="y">Canal A del texel: luminancia.</param>
    public static (double R, double G, double B) ToRgb(double co, double cg, double scale, double y)
    {
        // scale ∈ {1..~32.9}: el canal B guarda (s-1)*8/255
        double s = (scale * (255.0 / 8.0)) + 1.0;
        double coC = (co - ChromaOffset) / s;
        double cgC = (cg - ChromaOffset) / s;
        return (y + coC - cgC, y + cgC, y - coC - cgC);
    }
}
