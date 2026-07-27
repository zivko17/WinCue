using FFmpeg.AutoGen;

namespace ProResLab.Tests;

/// <summary>
/// Decodificación real con las DLL LGPL: qué formato de píxel sale de cada
/// variante, qué hace falta pasarle al decodificador y cómo se porta con
/// entrada hostil.
/// </summary>
public sealed class ProResDecodeTests
{
    private readonly MediaFixture _media = MediaFixture.Instance;

    /// <summary>Decodifica el frame <paramref name="index"/> y ejecuta una comprobación sobre él.</summary>
    private void WithFrame(string file, Action<ProResDecoder, MovInfo> check,
        int index = 0, int threads = 1, ThreadMode mode = ThreadMode.SingleThread, bool passFourcc = true)
    {
        string path = _media.Clip(file);
        MovInfo info = MovDemuxer.Open(path);

        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using PaddedBuffer buffer = new(info.MaxSampleSize);
        using ProResDecoder decoder = new(
            info.Width, info.Height, threads, mode, passFourcc ? info.VideoFourcc : null);

        int size = buffer.ReadSample(fs, info.Samples[index]);
        decoder.SendPacket(buffer.AsPacket(size));
        Assert.True(decoder.TryReceiveFrame(), "el decodificador no entregó el frame");

        check(decoder, info);
        decoder.ReleaseFrame();
    }

    // ------------------------------------------------------------------
    //  Formato de píxel por variante
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("t-proxy.mov")]
    [InlineData("t-lt.mov")]
    [InlineData("t-422.mov")]
    [InlineData("t-422hq.mov")]
    public void LasVariantes422_SalenComoYuv422p10le(string file)
        => WithFrame(file, (d, _) =>
        {
            Assert.Equal(AVPixelFormat.AV_PIX_FMT_YUV422P10LE, d.PixelFormat);

            PixelLayout layout = d.Layout;
            Assert.Equal(10, layout.BitDepth);
            Assert.Equal(3, layout.PlaneCount);
            Assert.False(layout.HasAlpha);

            // 4:2:2 = croma a media anchura, altura completa
            Assert.Equal(MediaFixture.Width, layout.PlaneWidth(0, d.FrameWidth));
            Assert.Equal(MediaFixture.Width / 2, layout.PlaneWidth(1, d.FrameWidth));
            Assert.Equal(MediaFixture.Height, layout.PlaneHeight(1, d.FrameHeight));
        });

    [Theory]
    [InlineData("t-4444.mov")]
    [InlineData("t-4444xq.mov")]
    public void LasVariantes4444_SalenComoYuva444p12leConAlfa(string file)
        => WithFrame(file, (d, _) =>
        {
            Assert.Equal(AVPixelFormat.AV_PIX_FMT_YUVA444P12LE, d.PixelFormat);

            PixelLayout layout = d.Layout;
            Assert.Equal(12, layout.BitDepth);
            Assert.Equal(4, layout.PlaneCount);
            Assert.True(layout.HasAlpha);

            // 4:4:4 = todos los planos a resolución completa
            for (int p = 0; p < layout.PlaneCount; p++)
            {
                Assert.Equal(MediaFixture.Width, layout.PlaneWidth(p, d.FrameWidth));
                Assert.Equal(MediaFixture.Height, layout.PlaneHeight(p, d.FrameHeight));
            }
        });

    [Fact]
    public void SinElFourccDelContenedor_ElDecodificadorSeQuedaA10BitsEnUn4444()
    {
        // TRAMPA CARA: el bitstream de ProRes 4444 es de 12 bits, pero FFmpeg
        // deduce la profundidad del PERFIL, y el perfil sale del codec_tag.
        // Sin pasárselo, entrega yuva444p10le y se pierden 2 bits por muestra.
        WithFrame("t-4444.mov", (d, _) =>
        {
            Assert.Equal(AVPixelFormat.AV_PIX_FMT_YUVA444P10LE, d.PixelFormat);
            Assert.Equal(10, d.Layout.BitDepth);
        }, passFourcc: false);

        // Con el fourcc, 12 bits (comprobado arriba en LasVariantes4444_*)
        WithFrame("t-4444.mov", (d, _) => Assert.Equal(12, d.Layout.BitDepth));
    }

    [Fact]
    public void ElFourcc_LlegaAlDecodificadorComoPerfil()
    {
        // Perfiles de FFmpeg: 0 proxy, 1 LT, 2 422, 3 HQ, 4 4444, 5 4444 XQ
        WithFrame("t-proxy.mov", (d, _) => Assert.Equal(0, d.DecoderProfile));
        WithFrame("t-422hq.mov", (d, _) => Assert.Equal(3, d.DecoderProfile));
        WithFrame("t-4444xq.mov", (d, _) => Assert.Equal(5, d.DecoderProfile));
    }

    [Fact]
    public void Mktag_EmpaquetaElFourccComoFFmpeg()
    {
        // 'apch' = 0x68637061 en little-endian
        Assert.Equal(0x68637061u, ProResDecoder.Mktag("apch"));
        Assert.Throws<ArgumentException>(() => ProResDecoder.Mktag("apc"));
    }

    // ------------------------------------------------------------------
    //  Color: lo que manda es el BITSTREAM, no el contenedor
    // ------------------------------------------------------------------

    [Fact]
    public void ElBitstreamDeclaraSuMatrizAunqueElContenedorNoTraigaColr()
    {
        WithFrame("t-422hq.mov", (d, info) =>
        {
            Assert.False(info.Colr.HasMatrix);                     // el .mov no dice nada
            Assert.Equal(AVColorSpace.AVCOL_SPC_BT709, d.ColorSpace);   // el frame sí
            Assert.Equal(AVColorRange.AVCOL_RANGE_MPEG, d.ColorRange);  // ProRes es rango limitado
        });
    }

    [Fact]
    public void Bt2020_LlegaHastaLaMatrizQueUsaraElShader()
    {
        WithFrame("t-2020.mov", (d, _) =>
        {
            Assert.Equal(AVColorSpace.AVCOL_SPC_BT2020_NCL, d.ColorSpace);
            Assert.Equal(ColorMatrix.Bt2020Ncl, ColorConvert.MatrixFromCode((int)d.ColorSpace));
        });
    }

    // ------------------------------------------------------------------
    //  Hilos
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(ThreadMode.SingleThread, 1)]
    [InlineData(ThreadMode.Slice, 4)]
    [InlineData(ThreadMode.Frame, 4)]
    [InlineData(ThreadMode.SliceAndFrame, 4)]
    public void TodosLosModosDeHilos_EntreganLosMismosFrames(ThreadMode mode, int threads)
    {
        string path = _media.Clip("t-422hq.mov");
        MovInfo info = MovDemuxer.Open(path);

        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using PaddedBuffer buffer = new(info.MaxSampleSize);
        using ProResDecoder decoder = new(info.Width, info.Height, threads, mode, info.VideoFourcc);

        int received = 0;
        foreach (SampleEntry entry in info.Samples)
        {
            int size = buffer.ReadSample(fs, entry);
            decoder.SendPacket(buffer.AsPacket(size));
            if (decoder.TryReceiveFrame())
            {
                received++;
                decoder.ReleaseFrame();
            }
        }

        // El modo FRAME retiene frames en la tubería: el drenaje los suelta.
        decoder.Flush();
        while (decoder.TryReceiveFrame())
        {
            received++;
            decoder.ReleaseFrame();
        }

        Assert.Equal(info.FrameCount, received);
    }

    [Fact]
    public void ModoFrame_RetieneFramesHastaElDrenaje_YModoSliceNo()
    {
        Assert.True(EntregaElPrimerPaquete(ThreadMode.Slice, 4));
        Assert.False(EntregaElPrimerPaquete(ThreadMode.Frame, 4));

        bool EntregaElPrimerPaquete(ThreadMode mode, int threads)
        {
            string path = _media.Clip("t-422hq.mov");
            MovInfo info = MovDemuxer.Open(path);
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using PaddedBuffer buffer = new(info.MaxSampleSize);
            using ProResDecoder decoder = new(info.Width, info.Height, threads, mode, info.VideoFourcc);

            int size = buffer.ReadSample(fs, info.Samples[0]);
            decoder.SendPacket(buffer.AsPacket(size));
            bool got = decoder.TryReceiveFrame();
            decoder.ReleaseFrame();
            return got;
        }
    }

    [Fact]
    public void ElMismoFrame_SaleIgualConUnHiloQueConDoce()
    {
        ushort[][]? single = null;
        ushort[][]? multi = null;

        WithFrame("t-422hq.mov", (d, _) => single = d.CopyPlanes(), index: 7);
        WithFrame("t-422hq.mov", (d, _) => multi = d.CopyPlanes(), index: 7,
            threads: 12, mode: ThreadMode.Slice);

        Assert.NotNull(single);
        Assert.NotNull(multi);
        Assert.Equal(single!.Length, multi!.Length);
        for (int p = 0; p < single.Length; p++)
        {
            Assert.Equal(single[p], multi[p]);
        }
    }

    // ------------------------------------------------------------------
    //  Ciclo de vida (loop y seek dependen de esto)
    // ------------------------------------------------------------------

    [Fact]
    public void TrasElDrenaje_HayQueResetearParaVolverAUsarlo()
    {
        string path = _media.Clip("t-422.mov");
        MovInfo info = MovDemuxer.Open(path);

        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using PaddedBuffer buffer = new(info.MaxSampleSize);
        using ProResDecoder decoder = new(info.Width, info.Height, 1, ThreadMode.SingleThread, info.VideoFourcc);

        int size = buffer.ReadSample(fs, info.Samples[0]);
        decoder.SendPacket(buffer.AsPacket(size));
        Assert.True(decoder.TryReceiveFrame());
        decoder.ReleaseFrame();

        decoder.Flush();
        while (decoder.TryReceiveFrame())
        {
            decoder.ReleaseFrame();
        }

        // Sin Reset el contexto sigue en drenaje y devuelve AVERROR_EOF
        Assert.Throws<InvalidDataException>(() => decoder.SendPacket(buffer.AsPacket(size)));

        decoder.Reset();
        decoder.SendPacket(buffer.AsPacket(size));
        Assert.True(decoder.TryReceiveFrame());
        decoder.ReleaseFrame();
    }

    [Fact]
    public void ProResEsTodoIntra_CualquierFrameSeDecodificaSolo()
    {
        // Es lo que permite el seek instantáneo y el trim por índices.
        ushort[][]? secuencial = null;
        ushort[][]? directo = null;

        string path = _media.Clip("t-422.mov");
        MovInfo info = MovDemuxer.Open(path);
        const int target = 20;

        using (FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (PaddedBuffer buffer = new(info.MaxSampleSize))
        using (ProResDecoder decoder = new(info.Width, info.Height, 1, ThreadMode.SingleThread, info.VideoFourcc))
        {
            for (int i = 0; i <= target; i++)
            {
                int size = buffer.ReadSample(fs, info.Samples[i]);
                decoder.SendPacket(buffer.AsPacket(size));
                Assert.True(decoder.TryReceiveFrame());
                if (i == target)
                {
                    secuencial = decoder.CopyPlanes();
                }

                decoder.ReleaseFrame();
            }
        }

        WithFrame("t-422.mov", (d, _) => directo = d.CopyPlanes(), index: target);

        Assert.NotNull(secuencial);
        Assert.NotNull(directo);
        Assert.Equal(secuencial![0], directo![0]);
    }

    // ------------------------------------------------------------------
    //  Cordura de la entrada
    // ------------------------------------------------------------------

    [Fact]
    public void UnPaqueteQueNoEsProRes_DevuelveErrorEnVezDeCaerse()
    {
        MovInfo info = MovDemuxer.Open(_media.Clip("t-422.mov"));
        using PaddedBuffer buffer = new(4096);
        using ProResDecoder decoder = new(info.Width, info.Height, 1, ThreadMode.SingleThread, info.VideoFourcc);

        buffer.Span[..4096].Fill(0xA5);

        // O rechaza el paquete, o lo traga sin entregar frame: lo que NO puede
        // es llevarse el proceso por delante.
        try
        {
            decoder.SendPacket(buffer.AsPacket(4096));
            Assert.False(decoder.TryReceiveFrame());
        }
        catch (InvalidDataException)
        {
            // También vale: el error viaja como excepción de C#
        }
    }

    [Fact]
    public void DimensionesImposibles_SeRechazanAntesDeTocarNadaNativo()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProResDecoder(0, 1080).Dispose());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProResDecoder(MovDemuxer.MaxDimension + 1, 1080).Dispose());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProResDecoder(1920, 1080, threadCount: 9999).Dispose());
    }

    [Fact]
    public void ElBufferConPadding_NoAceptaMasDeSuCapacidad()
    {
        using PaddedBuffer buffer = new(1024);

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.AsPacket(1025));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.AsPacket(0));
    }

    [Fact]
    public void UnFormatoDePixelInesperado_SeRechazaConNombreYRazon()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => PixelLayout.For(AVPixelFormat.AV_PIX_FMT_YUV420P));

        Assert.Contains("yuv422p10le", ex.Message, StringComparison.Ordinal);
    }
}
