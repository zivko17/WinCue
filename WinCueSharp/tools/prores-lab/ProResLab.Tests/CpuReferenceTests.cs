using FFmpeg.AutoGen;

namespace ProResLab.Tests;

/// <summary>
/// La referencia de CPU (la que valida al shader) y la métrica de PSNR.
/// </summary>
public sealed class CpuReferenceTests
{
    private static readonly PixelLayout Yuv422P10 = PixelLayout.For(AVPixelFormat.AV_PIX_FMT_YUV422P10LE);
    private static readonly PixelLayout Yuv444P10 = PixelLayout.For(AVPixelFormat.AV_PIX_FMT_YUV444P10LE);

    /// <summary>Planos de un color plano (mismo valor en todos los píxeles).</summary>
    private static ushort[][] Flat(int width, int height, PixelLayout layout, ushort y, ushort cb, ushort cr)
    {
        ushort[][] planes = new ushort[layout.PlaneCount][];
        for (int p = 0; p < layout.PlaneCount; p++)
        {
            int n = layout.PlaneWidth(p, width) * layout.PlaneHeight(p, height);
            planes[p] = new ushort[n];
            Array.Fill(planes[p], p switch { 0 => y, 1 => cb, 2 => cr, _ => (ushort)0 });
        }

        return planes;
    }

    [Fact]
    public void NegroLimitado_SaleNegroOpaco()
    {
        byte[] bgra = CpuReference.ToBgra(
            Flat(8, 4, Yuv422P10, 64, 512, 512), 8, 4, Yuv422P10, fullRange: false, ColorMatrix.Bt709);

        Assert.Equal(8 * 4 * 4, bgra.Length);
        for (int i = 0; i < bgra.Length; i += 4)
        {
            Assert.Equal(0, bgra[i]);
            Assert.Equal(0, bgra[i + 1]);
            Assert.Equal(0, bgra[i + 2]);
            Assert.Equal(255, bgra[i + 3]);
        }
    }

    [Fact]
    public void BlancoLimitado_SaleBlanco()
    {
        byte[] bgra = CpuReference.ToBgra(
            Flat(8, 4, Yuv422P10, 940, 512, 512), 8, 4, Yuv422P10, fullRange: false, ColorMatrix.Bt709);

        for (int i = 0; i < bgra.Length; i += 4)
        {
            Assert.Equal(255, bgra[i]);
            Assert.Equal(255, bgra[i + 1]);
            Assert.Equal(255, bgra[i + 2]);
        }
    }

    [Fact]
    public void EnFormato422_CadaMuestraDeCromaCubreDosPixelesDeLuma()
    {
        // Croma con dos valores distintos: los píxeles pares e impares del par
        // deben compartir el mismo color de croma (replicación, como swscale).
        const int w = 4;
        const int h = 1;
        ushort[][] planes =
        [
            [500, 500, 500, 500],   // luma constante
            [200, 800],             // Cb: dos muestras para cuatro píxeles
            [512, 512],
        ];

        byte[] bgra = CpuReference.ToBgra(planes, w, h, Yuv422P10, false, ColorMatrix.Bt709);

        // píxeles 0 y 1 comparten croma; 2 y 3 comparten otro
        Assert.Equal(bgra[0], bgra[4]);
        Assert.Equal(bgra[8], bgra[12]);
        Assert.NotEqual(bgra[0], bgra[8]);
    }

    [Fact]
    public void EnFormato444_NoHayReplicacionDeCroma()
    {
        const int w = 2;
        const int h = 1;
        ushort[][] planes = [[500, 500], [200, 800], [512, 512]];

        byte[] bgra = CpuReference.ToBgra(planes, w, h, Yuv444P10, false, ColorMatrix.Bt709);

        Assert.NotEqual(bgra[0], bgra[4]);
    }

    [Fact]
    public void ElCanalAlfa_SaleDelCuartoPlanoCuandoLoHay()
    {
        PixelLayout yuva = PixelLayout.For(AVPixelFormat.AV_PIX_FMT_YUVA444P10LE);
        ushort[][] planes = [[940], [512], [512], [64]];   // alfa al negro legal = 0

        byte[] bgra = CpuReference.ToBgra(planes, 1, 1, yuva, false, ColorMatrix.Bt709);

        Assert.Equal(0, bgra[3]);
    }

    // ------------------------------------------------------------------
    //  PSNR
    // ------------------------------------------------------------------

    [Fact]
    public void ImagenesIdenticas_DanPsnrInfinito()
    {
        byte[] a = [10, 20, 30, 255, 40, 50, 60, 255];

        Assert.True(double.IsPositiveInfinity(Psnr.Compute(a, a)));
        Assert.Equal(0, Psnr.MaxAbsDiff(a, a));
    }

    [Fact]
    public void UnErrorDeUnNivelPorCanal_Da48Decibelios()
    {
        // MSE = 1 → PSNR = 10·log10(255²) = 48,13 dB
        byte[] a = [10, 20, 30, 255];
        byte[] b = [11, 21, 31, 255];

        Assert.Equal(48.13, Psnr.Compute(a, b), 2);
        Assert.Equal(1, Psnr.MaxAbsDiff(a, b));
    }

    [Fact]
    public void ElAlfaNoEntraEnElPsnr()
    {
        byte[] a = [10, 20, 30, 0];
        byte[] b = [10, 20, 30, 255];

        Assert.True(double.IsPositiveInfinity(Psnr.Compute(a, b)));
    }

    [Fact]
    public void TamanosDistintos_SeRechazan()
    {
        byte[] a = [1, 2, 3, 4];
        byte[] b = [1, 2, 3, 4, 5, 6, 7, 8];

        Assert.Throws<ArgumentException>(() => Psnr.Compute(a, b));
        Assert.Throws<ArgumentException>(() => Psnr.MaxAbsDiff(a, b));
    }

    // ------------------------------------------------------------------
    //  Layout
    // ------------------------------------------------------------------

    [Fact]
    public void BytesPorFrame_SonLosQueHayQueSubirALaGpu()
    {
        // 1080p 4:2:2 a 10 bits: Y 1920·1080·2 + 2 cromas 960·1080·2
        Assert.Equal((1920L * 1080 * 2) + (2L * 960 * 1080 * 2), Yuv422P10.FrameBytes(1920, 1080));

        // 1080p 4:4:4 con alfa a 12 bits: cuatro planos completos
        PixelLayout yuva12 = PixelLayout.For(AVPixelFormat.AV_PIX_FMT_YUVA444P12LE);
        Assert.Equal(4L * 1920 * 1080 * 2, yuva12.FrameBytes(1920, 1080));
    }

    [Fact]
    public void DimensionesImpares_RedondeanHaciaArribaEnElCroma()
    {
        Assert.Equal(2, Yuv422P10.PlaneWidth(1, 3));
        Assert.Equal(3, Yuv422P10.PlaneHeight(1, 3));
    }

    [Fact]
    public void MaxSample_DependeDeLaProfundidad()
    {
        Assert.Equal(1023, Yuv422P10.MaxSample);
        Assert.Equal(4095, PixelLayout.For(AVPixelFormat.AV_PIX_FMT_YUV444P12LE).MaxSample);
    }
}
