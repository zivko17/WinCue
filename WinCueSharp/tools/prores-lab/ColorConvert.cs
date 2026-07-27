namespace ProResLab;

/// <summary>Matriz de luminancia/crominancia a aplicar.</summary>
public enum ColorMatrix
{
    /// <summary>ITU-R BT.709 — HD. Es lo que trae el ProRes de 1080p/4K normal.</summary>
    Bt709,

    /// <summary>ITU-R BT.601 — SD heredado (PAL/NTSC reescalado).</summary>
    Bt601,

    /// <summary>ITU-R BT.2020 no constant luminance — UHD/HDR.</summary>
    Bt2020Ncl,
}

/// <summary>
/// Conversión YUV → RGB como FUNCIÓN PURA.
/// </summary>
/// <remarks>
/// <para>
/// Es la MISMA matemática que ejecuta el pixel shader (ver
/// <c>ProResLab.View\Shaders.cs</c>). Vive aquí en CPU por dos motivos:
/// (a) es testeable contra valores de referencia sin GPU, y (b) sirve de
/// referencia independiente para medir el PSNR del camino GPU y así separar
/// "error de matemática" de "error de precisión de textura/filtrado".
/// </para>
/// <para>
/// Rango LIMITADO (el habitual en ProRes) a N bits: la luma vive en
/// [16·2^(N-8), 235·2^(N-8)] y la croma en [16·2^(N-8), 240·2^(N-8)] con el
/// cero en 128·2^(N-8). Rango COMPLETO: luma [0, 2^N−1] y croma centrada en
/// 2^(N−1).
/// </para>
/// </remarks>
public static class ColorConvert
{
    /// <summary>Coeficientes de la matriz inversa (Kr, Kb) → (cr_r, cb_g, cr_g, cb_b).</summary>
    /// <remarks>Derivados de Kr/Kb: R = Y + 2(1−Kr)·Cr, B = Y + 2(1−Kb)·Cb, G compensa.</remarks>
    public readonly record struct Coefficients(double CrToR, double CbToG, double CrToG, double CbToB);

    /// <summary>Coeficientes para una matriz dada.</summary>
    public static Coefficients CoefficientsFor(ColorMatrix matrix)
    {
        // Kr/Kb según la recomendación; el resto se deriva.
        (double kr, double kb) = matrix switch
        {
            ColorMatrix.Bt601 => (0.299, 0.114),
            ColorMatrix.Bt2020Ncl => (0.2627, 0.0593),
            _ => (0.2126, 0.0722), // BT.709
        };

        double kg = 1.0 - kr - kb;
        return new Coefficients(
            CrToR: 2.0 * (1.0 - kr),
            CbToG: -2.0 * kb * (1.0 - kb) / kg,
            CrToG: -2.0 * kr * (1.0 - kr) / kg,
            CbToB: 2.0 * (1.0 - kb));
    }

    /// <summary>
    /// Normaliza una muestra de luma cruda a Y' en [0, 1].
    /// </summary>
    public static double NormalizeLuma(int sample, int bitDepth, bool fullRange)
    {
        double max = (1 << bitDepth) - 1;
        if (fullRange)
        {
            return sample / max;
        }

        double low = 16 << (bitDepth - 8);
        double high = 235 << (bitDepth - 8);
        return (sample - low) / (high - low);
    }

    /// <summary>
    /// Normaliza una muestra de croma cruda a Cb/Cr en [−0,5, 0,5].
    /// </summary>
    public static double NormalizeChroma(int sample, int bitDepth, bool fullRange)
    {
        double mid = 1 << (bitDepth - 1);
        if (fullRange)
        {
            double max = (1 << bitDepth) - 1;
            return (sample - mid) / max;
        }

        double low = 16 << (bitDepth - 8);
        double high = 240 << (bitDepth - 8);
        return (sample - mid) / (high - low);
    }

    /// <summary>
    /// Convierte una terna cruda (Y, Cb, Cr) a RGB lineal-de-código en [0, 1]
    /// (sin recorte). Es la función que replica el shader.
    /// </summary>
    public static (double R, double G, double B) ToRgb(
        int y, int cb, int cr, int bitDepth, bool fullRange, ColorMatrix matrix)
    {
        Coefficients k = CoefficientsFor(matrix);
        double yy = NormalizeLuma(y, bitDepth, fullRange);
        double u = NormalizeChroma(cb, bitDepth, fullRange);
        double v = NormalizeChroma(cr, bitDepth, fullRange);

        return (
            yy + (k.CrToR * v),
            yy + (k.CbToG * u) + (k.CrToG * v),
            yy + (k.CbToB * u));
    }

    /// <summary>Convierte a RGB de 8 bits con recorte y redondeo al más cercano.</summary>
    public static (byte R, byte G, byte B) ToRgb8(
        int y, int cb, int cr, int bitDepth, bool fullRange, ColorMatrix matrix)
    {
        (double r, double g, double b) = ToRgb(y, cb, cr, bitDepth, fullRange, matrix);
        return (Quantize8(r), Quantize8(g), Quantize8(b));
    }

    /// <summary>Recorta a [0,1] y cuantiza a 8 bits.</summary>
    public static byte Quantize8(double value)
        => (byte)Math.Clamp(Math.Round(value * 255.0, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>
    /// Traduce el código de matriz de ISO/IEC 23001-8 (el del átomo <c>colr</c>
    /// y el de <c>AVColorSpace</c>) a la matriz que usa el laboratorio.
    /// </summary>
    /// <remarks>
    /// 1 = BT.709, 5/6 = BT.601, 9 = BT.2020 NCL. Lo demás (0 = identidad,
    /// 2 = unspecified…) cae a BT.709: es lo que hace todo el mundo con HD y
    /// lo que produce ffmpeg al codificar ProRes sin señalizar.
    /// </remarks>
    public static ColorMatrix MatrixFromCode(int code) => code switch
    {
        5 or 6 => ColorMatrix.Bt601,
        9 or 10 => ColorMatrix.Bt2020Ncl,
        _ => ColorMatrix.Bt709,
    };
}
