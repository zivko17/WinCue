using System.Runtime.InteropServices;

namespace ProResLab.Gpu;

/// <summary>
/// Constantes de conversión que consume el pixel shader.
/// </summary>
/// <remarks>
/// Se calculan en CPU con la MISMA aritmética que <see cref="ColorConvert"/>
/// (que está testeada como función pura): así el shader y la referencia de CPU
/// no pueden divergir por descuido.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct ColorParams : IEquatable<ColorParams>
{
    /// <summary>x = escala de luma, y = offset de luma (z, w sin usar).</summary>
    public float YScale;

    /// <summary>Offset de luma.</summary>
    public float YOffset;

    private readonly float _padY0;
    private readonly float _padY1;

    /// <summary>Escala de croma.</summary>
    public float CScale;

    /// <summary>Offset de croma.</summary>
    public float COffset;

    private readonly float _padC0;
    private readonly float _padC1;

    /// <summary>Coeficiente Cr→R.</summary>
    public float CrToR;

    /// <summary>Coeficiente Cb→G.</summary>
    public float CbToG;

    /// <summary>Coeficiente Cr→G.</summary>
    public float CrToG;

    /// <summary>Coeficiente Cb→B.</summary>
    public float CbToB;

    /// <summary>1 si hay plano alfa.</summary>
    public float HasAlpha;

    private readonly float _padF0;
    private readonly float _padF1;
    private readonly float _padF2;

    /// <summary>
    /// Deriva las constantes de la profundidad de bits, el rango y la matriz.
    /// </summary>
    /// <remarks>
    /// La textura es <c>R16_UNORM</c>, así que el shader recibe
    /// <c>código/65535</c>. Queremos
    /// <c>Y' = (código − bajo)/(alto − bajo)</c>, luego
    /// <c>escala = 65535/(alto − bajo)</c> y <c>offset = −bajo/(alto − bajo)</c>.
    /// Idem para croma con su propio rango y el cero en el punto medio.
    /// </remarks>
    public static ColorParams Create(int bitDepth, bool fullRange, ColorMatrix matrix, bool hasAlpha)
    {
        double max = (1 << bitDepth) - 1;
        double mid = 1 << (bitDepth - 1);

        double yLow = fullRange ? 0 : 16 << (bitDepth - 8);
        double ySpan = fullRange ? max : (235 << (bitDepth - 8)) - yLow;
        double cSpan = fullRange ? max : (240 << (bitDepth - 8)) - (16 << (bitDepth - 8));

        ColorConvert.Coefficients k = ColorConvert.CoefficientsFor(matrix);

        return new ColorParams
        {
            YScale = (float)(65535.0 / ySpan),
            YOffset = (float)(-yLow / ySpan),
            CScale = (float)(65535.0 / cSpan),
            COffset = (float)(-mid / cSpan),
            CrToR = (float)k.CrToR,
            CbToG = (float)k.CbToG,
            CrToG = (float)k.CrToG,
            CbToB = (float)k.CbToB,
            HasAlpha = hasAlpha ? 1f : 0f,
        };
    }

    /// <summary>Tamaño del constant buffer (múltiplo de 16 que exige D3D11).</summary>
    public static int SizeInBytes => Marshal.SizeOf<ColorParams>();

    /// <inheritdoc/>
    public readonly bool Equals(ColorParams other)
        => YScale.Equals(other.YScale) && YOffset.Equals(other.YOffset)
        && CScale.Equals(other.CScale) && COffset.Equals(other.COffset)
        && CrToR.Equals(other.CrToR) && CbToG.Equals(other.CbToG)
        && CrToG.Equals(other.CrToG) && CbToB.Equals(other.CbToB)
        && HasAlpha.Equals(other.HasAlpha);

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is ColorParams p && Equals(p);

    /// <inheritdoc/>
    public override readonly int GetHashCode()
        => HashCode.Combine(YScale, YOffset, CScale, COffset, CrToR, CbToG, CrToG, CbToB);

    /// <summary>Igualdad por valor.</summary>
    public static bool operator ==(ColorParams left, ColorParams right) => left.Equals(right);

    /// <summary>Desigualdad por valor.</summary>
    public static bool operator !=(ColorParams left, ColorParams right) => !left.Equals(right);
}
