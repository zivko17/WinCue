using ProResLab.Gpu;

namespace ProResLab.Tests;

/// <summary>
/// La matemática de color como FUNCIÓN PURA, contra valores de referencia
/// calculados a mano desde las recomendaciones (no desde el propio código).
/// </summary>
public sealed class ColorConvertTests
{
    // Rango limitado a 10 bits: luma 64..940, croma 64..960 con el cero en 512.
    private const int Y10Black = 64;
    private const int Y10White = 940;
    private const int C10Neutral = 512;

    [Fact]
    public void Bt709_NegroLimitado_EsCeroExacto()
    {
        (double r, double g, double b) = ColorConvert.ToRgb(
            Y10Black, C10Neutral, C10Neutral, 10, fullRange: false, ColorMatrix.Bt709);

        Assert.Equal(0.0, r, 12);
        Assert.Equal(0.0, g, 12);
        Assert.Equal(0.0, b, 12);
    }

    [Fact]
    public void Bt709_BlancoLimitado_EsUnoExacto()
    {
        (double r, double g, double b) = ColorConvert.ToRgb(
            Y10White, C10Neutral, C10Neutral, 10, fullRange: false, ColorMatrix.Bt709);

        Assert.Equal(1.0, r, 12);
        Assert.Equal(1.0, g, 12);
        Assert.Equal(1.0, b, 12);
    }

    [Fact]
    public void Bt709_Coeficientes_CoincidenConLaRecomendacion()
    {
        // Valores publicados de BT.709 (Kr=0.2126, Kb=0.0722):
        //   R = Y + 1.5748·Cr ; B = Y + 1.8556·Cb
        //   G = Y − 0.187324·Cb − 0.468124·Cr
        ColorConvert.Coefficients k = ColorConvert.CoefficientsFor(ColorMatrix.Bt709);

        Assert.Equal(1.5748, k.CrToR, 4);
        Assert.Equal(1.8556, k.CbToB, 4);
        Assert.Equal(-0.187324, k.CbToG, 6);
        Assert.Equal(-0.468124, k.CrToG, 6);
    }

    [Fact]
    public void Bt601_Coeficientes_CoincidenConLaRecomendacion()
    {
        // BT.601 (Kr=0.299, Kb=0.114): 1.402 / 1.772 / −0.344136 / −0.714136
        ColorConvert.Coefficients k = ColorConvert.CoefficientsFor(ColorMatrix.Bt601);

        Assert.Equal(1.402, k.CrToR, 4);
        Assert.Equal(1.772, k.CbToB, 4);
        Assert.Equal(-0.344136, k.CbToG, 6);
        Assert.Equal(-0.714136, k.CrToG, 6);
    }

    [Fact]
    public void Bt2020Ncl_Coeficientes_CoincidenConLaRecomendacion()
    {
        // BT.2020 NCL (Kr=0.2627, Kb=0.0593): 1.4746 / 1.8814 / −0.164553 / −0.571353
        ColorConvert.Coefficients k = ColorConvert.CoefficientsFor(ColorMatrix.Bt2020Ncl);

        Assert.Equal(1.4746, k.CrToR, 4);
        Assert.Equal(1.8814, k.CbToB, 4);
        Assert.Equal(-0.164553, k.CbToG, 6);
        Assert.Equal(-0.571353, k.CrToG, 6);
    }

    [Fact]
    public void RojoPrimario_Bt709_DaRojoSaturado()
    {
        // Rojo primario BT.709 en rango limitado a 10 bits:
        //   Y' = Kr = 0,2126        → 64 + 0,2126·876 ≈ 250
        //   Cb = −Kr/(2(1−Kb))      → 512 − 0,11457·896 ≈ 409
        //   Cr = (1−Kr)/(2(1−Kr))   = 0,5 → 512 + 0,5·896 = 960
        (byte r, byte g, byte b) = ColorConvert.ToRgb8(250, 409, 960, 10, false, ColorMatrix.Bt709);

        Assert.Equal(255, r);
        Assert.Equal(0, g);
        Assert.Equal(0, b);
    }

    [Fact]
    public void RangoCompleto_NegroYBlancoUsanTodoElRecorrido()
    {
        Assert.Equal(0.0, ColorConvert.NormalizeLuma(0, 10, fullRange: true), 12);
        Assert.Equal(1.0, ColorConvert.NormalizeLuma(1023, 10, fullRange: true), 12);

        // En rango limitado, el 0 crudo queda POR DEBAJO del negro legal.
        Assert.True(ColorConvert.NormalizeLuma(0, 10, fullRange: false) < 0);
    }

    [Fact]
    public void DoceBits_EscalaIgualQueDiez()
    {
        // El negro y el blanco legales a 12 bits son 4× los de 10.
        Assert.Equal(0.0, ColorConvert.NormalizeLuma(64 * 4, 12, false), 12);
        Assert.Equal(1.0, ColorConvert.NormalizeLuma(940 * 4, 12, false), 12);
        Assert.Equal(0.0, ColorConvert.NormalizeChroma(512 * 4, 12, false), 12);
    }

    [Theory]
    [InlineData(1, ColorMatrix.Bt709)]
    [InlineData(5, ColorMatrix.Bt601)]
    [InlineData(6, ColorMatrix.Bt601)]
    [InlineData(9, ColorMatrix.Bt2020Ncl)]
    [InlineData(2, ColorMatrix.Bt709)]   // unspecified → BT.709
    [InlineData(0, ColorMatrix.Bt709)]   // identity → BT.709
    public void CodigoDeMatriz_SeTraduceComoTocaYNuncaFallaConDesconocidos(int code, ColorMatrix expected)
        => Assert.Equal(expected, ColorConvert.MatrixFromCode(code));

    [Fact]
    public void Cuantizacion_RedondeaAlMasCercanoYRecorta()
    {
        Assert.Equal(0, ColorConvert.Quantize8(-0.5));
        Assert.Equal(255, ColorConvert.Quantize8(1.5));
        Assert.Equal(128, ColorConvert.Quantize8(128 / 255.0));
        Assert.Equal(1, ColorConvert.Quantize8(0.5 / 255.0));   // .5 hacia arriba
    }

    // ------------------------------------------------------------------
    //  Las constantes que consume el SHADER tienen que reproducir EXACTAMENTE
    //  la normalización de la referencia de CPU: es la única garantía de que
    //  GPU y CPU no divergen por descuido.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(10, false)]
    [InlineData(10, true)]
    [InlineData(12, false)]
    [InlineData(12, true)]
    public void ConstantesDelShader_ReproducenLaNormalizacionDeCpu(int bitDepth, bool fullRange)
    {
        ColorParams p = ColorParams.Create(bitDepth, fullRange, ColorMatrix.Bt709, hasAlpha: false);
        int max = (1 << bitDepth) - 1;

        // Las constantes viajan a la GPU como float32: el margen es el de esa
        // precisión (~1e-7 relativo), no el de un double.
        const double tolerance = 1e-5;

        foreach (int code in new[] { 0, 64 << (bitDepth - 8), 512 << (bitDepth - 8), max })
        {
            // Lo que hace la GPU: muestra R16_UNORM = code/65535, luego escala+offset
            double gpuLuma = ((code / 65535.0) * p.YScale) + p.YOffset;
            double cpuLuma = ColorConvert.NormalizeLuma(code, bitDepth, fullRange);
            Assert.True(Math.Abs(cpuLuma - gpuLuma) < tolerance,
                $"luma {code}: CPU {cpuLuma} vs GPU {gpuLuma}");

            double gpuChroma = ((code / 65535.0) * p.CScale) + p.COffset;
            double cpuChroma = ColorConvert.NormalizeChroma(code, bitDepth, fullRange);
            Assert.True(Math.Abs(cpuChroma - gpuChroma) < tolerance,
                $"croma {code}: CPU {cpuChroma} vs GPU {gpuChroma}");
        }
    }

    [Fact]
    public void ConstantesDelShader_LlevanLosCoeficientesDeLaMatrizPedida()
    {
        ColorParams p = ColorParams.Create(10, false, ColorMatrix.Bt2020Ncl, hasAlpha: true);

        Assert.Equal(1.4746f, p.CrToR, 4);
        Assert.Equal(1.8814f, p.CbToB, 4);
        Assert.Equal(1f, p.HasAlpha);
    }
}
