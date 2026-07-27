namespace ProResLab;

/// <summary>
/// Conversión YUV→BGRA en CPU: la REFERENCIA independiente del shader.
/// </summary>
/// <remarks>
/// <para>
/// No entra en el camino caliente (convertir en CPU es justo lo que este
/// laboratorio evita). Existe para dos cosas: comparar el resultado de la GPU
/// contra la MISMA matemática ejecutada en doble precisión —lo que aísla el
/// error de precisión de textura y del filtrado del que pueda haber en la
/// fórmula— y para tener algo testeable sin GPU.
/// </para>
/// <para>
/// El croma se amplía por REPLICACIÓN, igual que hace swscale de FFmpeg al
/// convertir 4:2:2 a RGB (dos píxeles de luma por muestra de croma). Con 4:4:4
/// no hay ampliación que hacer.
/// </para>
/// </remarks>
public static class CpuReference
{
    /// <summary>Convierte los planos de un frame a BGRA de 8 bits, fila a fila de arriba abajo.</summary>
    public static byte[] ToBgra(
        ushort[][] planes, int width, int height, PixelLayout layout, bool fullRange, ColorMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        int chromaWidth = layout.PlaneWidth(1, width);
        int chromaHeight = layout.PlaneHeight(1, height);
        byte[] output = new byte[(long)width * height * 4];

        for (int y = 0; y < height; y++)
        {
            int cy = Math.Min(y >> layout.ChromaShiftY, chromaHeight - 1);
            int lumaRow = y * width;
            int chromaRow = cy * chromaWidth;
            int outRow = y * width * 4;

            for (int x = 0; x < width; x++)
            {
                int cx = Math.Min(x >> layout.ChromaShiftX, chromaWidth - 1);
                int yy = planes[0][lumaRow + x];
                int cb = planes[1][chromaRow + cx];
                int cr = planes[2][chromaRow + cx];

                (byte r, byte g, byte b) = ColorConvert.ToRgb8(yy, cb, cr, layout.BitDepth, fullRange, matrix);
                int o = outRow + (x * 4);
                output[o] = b;
                output[o + 1] = g;
                output[o + 2] = r;
                output[o + 3] = layout.HasAlpha && planes.Length > 3
                    ? ColorConvert.Quantize8(ColorConvert.NormalizeLuma(planes[3][lumaRow + x], layout.BitDepth, fullRange))
                    : (byte)255;
            }
        }

        return output;
    }
}

/// <summary>Comparación objetiva de dos imágenes BGRA del mismo tamaño.</summary>
public static class Psnr
{
    /// <summary>
    /// PSNR en dB sobre los canales R, G y B (el alfa NO entra: el laboratorio
    /// presenta opaco y ffmpeg no lo produce igual).
    /// </summary>
    /// <returns><see cref="double.PositiveInfinity"/> si son idénticas.</returns>
    public static double Compute(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length || a.Length == 0 || a.Length % 4 != 0)
        {
            throw new ArgumentException("Las dos imágenes BGRA deben medir lo mismo y no estar vacías.", nameof(b));
        }

        double sum = 0;
        long n = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            for (int c = 0; c < 3; c++)
            {
                double d = a[i + c] - b[i + c];
                sum += d * d;
                n++;
            }
        }

        if (sum == 0)
        {
            return double.PositiveInfinity;
        }

        double mse = sum / n;
        return 10.0 * Math.Log10(255.0 * 255.0 / mse);
    }

    /// <summary>Diferencia absoluta máxima por canal (R, G, B): delata errores puntuales.</summary>
    public static int MaxAbsDiff(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("Las dos imágenes deben medir lo mismo.", nameof(b));
        }

        int max = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            for (int c = 0; c < 3; c++)
            {
                int d = Math.Abs(a[i + c] - b[i + c]);
                if (d > max)
                {
                    max = d;
                }
            }
        }

        return max;
    }
}
