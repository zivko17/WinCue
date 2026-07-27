// =============================================================================
// ColorParams.cs — Constantes de conversión que consume el pixel shader
// =============================================================================
// Promocionado de tools/prores-lab (paso 1). Se calculan en CPU con la MISMA
// aritmética que ColorConvert (testeada como función pura): así el shader y la
// referencia no pueden divergir por descuido.
// =============================================================================

using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using WinCue.Video.Containers;

namespace WinCue.Video.ProRes;

[StructLayout(LayoutKind.Sequential)]
public struct ColorParams : IEquatable<ColorParams>
{
    /// <summary>Escala de luma.</summary>
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
    /// <c>código/65535</c>. Queremos <c>Y' = (código − bajo)/(alto − bajo)</c>:
    /// <c>escala = 65535/(alto − bajo)</c> y <c>offset = −bajo/(alto − bajo)</c>.
    /// UNA multiplicación y una suma resuelven a la vez la profundidad de bits Y
    /// el rango limitado/completo. Idem para croma con el cero en el punto medio.
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

    /// <summary>
    /// Constantes para un frame concreto, con la regla de precedencia del
    /// laboratorio: MANDA EL BITSTREAM (<c>AVFrame.colorspace</c>), el
    /// <c>colr</c> del contenedor es RESPALDO y BT.709 el valor por defecto.
    /// </summary>
    /// <remarks>
    /// ffmpeg NO escribe el átomo <c>colr</c> al codificar ProRes salvo que se
    /// le pida <c>-movflags +write_colr</c>; el color viaja en la cabecera del
    /// propio frame. Las herramientas de Apple sí lo escriben, de ahí que el
    /// respaldo siga valiendo la pena.
    /// </remarks>
    public static ColorParams ForFrame(PixelLayout layout, AVColorSpace space, AVColorRange range, ColrInfo colr)
    {
        ColorMatrix matrix = space is not (AVColorSpace.AVCOL_SPC_UNSPECIFIED or AVColorSpace.AVCOL_SPC_RGB)
            ? ColorConvert.MatrixFromCode((int)space)
            : colr.HasMatrix
                ? ColorConvert.MatrixFromCode(colr.Matrix)
                : ColorMatrix.Bt709;

        bool fullRange = range == AVColorRange.AVCOL_RANGE_JPEG
            || (range == AVColorRange.AVCOL_RANGE_UNSPECIFIED && colr.FullRange);

        return Create(layout.BitDepth, fullRange, matrix, layout.HasAlpha);
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
