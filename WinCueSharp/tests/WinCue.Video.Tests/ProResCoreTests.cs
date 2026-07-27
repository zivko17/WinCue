// =============================================================================
// ProResCoreTests.cs — Núcleo ProRes promocionado (demux + decode + color)
// =============================================================================
// Los tests del laboratorio (tools/prores-lab) que SIGUEN VALIENDO en producción,
// traídos al repo como regresión permanente. Ejercitan WinCue.Video.ProRes sin
// GPU: MovDemuxer sobre .mov ProRes reales, ProResDecoder con las dos trampas
// caras del paso 1 (el codec_tag de los 12 bits y el Reset() tras drenar), y la
// matemática de color como función pura.
//
// Si ffmpeg no está en el PATH o no están las DLL LGPL, los tests que las
// necesitan NO AFIRMAN NADA (mismo patrón que HapCoreTests y VideoEngineTests):
// la máquina de otro puede no tenerlas y no por eso el suite debe romperse.
// =============================================================================

using System.Diagnostics;
using FFmpeg.AutoGen;
using WinCue.Video.Containers;
using WinCue.Video.ProRes;

namespace WinCue.Video.Tests;

/// <summary>Media ProRes diminuta (320x240, 1 s) generada con el ffmpeg del PATH.</summary>
public sealed class ProResMediaFixture : IDisposable
{
    public string Dir { get; }
    public string P422 { get; }        // apcn — ProRes 422
    public string P422Hq { get; }      // apch — ProRes 422 HQ
    public string P4444 { get; }       // ap4h — ProRes 4444 con alfa
    public string ConAudio { get; }    // apcn + seno PCM 16-bit
    public string Bt2020 { get; }      // apch señalizado BT.2020 con colr escrito
    public bool FFmpegOk { get; }

    public ProResMediaFixture()
    {
        Dir = Path.Combine(Path.GetTempPath(), "wincue_prores_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        P422 = Path.Combine(Dir, "p422.mov");
        P422Hq = Path.Combine(Dir, "p422hq.mov");
        P4444 = Path.Combine(Dir, "p4444.mov");
        ConAudio = Path.Combine(Dir, "p422-audio.mov");
        Bt2020 = Path.Combine(Dir, "p2020.mov");

        const string Src = "testsrc2=size=320x240:rate=30:duration=1";
        FFmpegOk =
            Run("-f", "lavfi", "-i", Src,
                "-c:v", "prores_ks", "-profile:v", "2", "-colorspace", "bt709", "-color_range", "tv", P422)
            && Run("-f", "lavfi", "-i", Src,
                "-c:v", "prores_ks", "-profile:v", "3", "-colorspace", "bt709", "-color_range", "tv", P422Hq)
            && Run("-f", "lavfi", "-i", Src,
                "-f", "lavfi", "-i", "gradients=size=320x240:rate=30:duration=1",
                "-filter_complex", "[0][1]alphamerge[v]", "-map", "[v]",
                "-c:v", "prores_ks", "-profile:v", "4", "-pix_fmt", "yuva444p10le", P4444)
            && Run("-f", "lavfi", "-i", Src,
                "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
                "-c:v", "prores_ks", "-profile:v", "2", "-c:a", "pcm_s16le", "-shortest", ConAudio)
            && Run("-f", "lavfi", "-i", Src,
                "-c:v", "prores_ks", "-profile:v", "3",
                "-colorspace", "bt2020nc", "-color_range", "tv", "-movflags", "+write_colr", Bt2020);
    }

    /// <summary>¿Están las DLL LGPL de FFmpeg? Sin ellas no hay decode que probar.</summary>
    public static bool FFmpegDllsOk => FFmpegLoader.IsAvailable;

    private static bool Run(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-y");
            foreach (string a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(60_000);
            return p.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

public class ProResCoreTests : IClassFixture<ProResMediaFixture>
{
    private readonly ProResMediaFixture _media;

    public ProResCoreTests(ProResMediaFixture media) => _media = media;

    private bool Skip => !_media.FFmpegOk || !ProResMediaFixture.FFmpegDllsOk;

    // ------------------------------------------------------------------
    // Demuxer: el contenedor visto desde ProRes
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("apco", ProResProfile.Proxy)]
    [InlineData("apcs", ProResProfile.Lt)]
    [InlineData("apcn", ProResProfile.Standard422)]
    [InlineData("apch", ProResProfile.Hq422)]
    [InlineData("ap4h", ProResProfile.P4444)]
    [InlineData("ap4x", ProResProfile.P4444Xq)]
    [InlineData("Hap1", ProResProfile.None)]
    [InlineData("avc1", ProResProfile.None)]
    public void PerfilPorFourcc(string fourcc, ProResProfile expected)
    {
        Assert.Equal(expected, ProResFormat.ProfileOf(fourcc));
        Assert.Equal(expected != ProResProfile.None, ProResFormat.IsProRes(fourcc));
    }

    [Fact]
    public void Demux_LeeElFourccYLasDimensiones()
    {
        if (!_media.FFmpegOk) return;
        var info = MovDemuxer.Open(_media.P422Hq);

        Assert.Equal("apch", info.VideoFourcc);
        Assert.Equal(320, info.Width);
        Assert.Equal(240, info.Height);
        Assert.Equal(30, info.FrameCount);
        Assert.Equal(30.0, info.Fps, 1);
        Assert.True(info.MaxSampleSize > 0);
    }

    [Fact]
    public void Demux_Depth32EnUn4444ConAlfa_Y24EnUn422()
    {
        if (!_media.FFmpegOk) return;
        Assert.Equal(24, MovDemuxer.Open(_media.P422).Depth);
        Assert.Equal(32, MovDemuxer.Open(_media.P4444).Depth);
    }

    [Fact]
    public void Demux_EnumeraLaPistaDeAudioSinDecodificarla()
    {
        if (!_media.FFmpegOk) return;
        var info = MovDemuxer.Open(_media.ConAudio);

        Assert.NotNull(info.Audio);
        Assert.True(info.Audio!.SampleRate >= 8000.0);
        Assert.Equal(16, info.Audio.SampleSizeBits);
        Assert.True(info.Audio.Channels >= 1);
    }

    [Fact]
    public void Demux_ColrSeLeeCuandoEstaYNoSeInventaCuandoNoEsta()
    {
        if (!_media.FFmpegOk) return;

        // +write_colr → matriz 9 (BT.2020 NCL)
        Assert.Equal(9, MovDemuxer.Open(_media.Bt2020).Colr.Matrix);

        // Sin +write_colr ffmpeg NO escribe el átomo: "unspecified", no inventado
        var sinColr = MovDemuxer.Open(_media.P422Hq).Colr;
        Assert.False(sinColr.HasMatrix);
    }

    [Fact]
    public void Validate_RechazaSamplesQueSeSalenDelFichero()
    {
        if (!_media.FFmpegOk) return;
        var info = MovDemuxer.Open(_media.P422);

        // Mismo MovInfo, longitud de fichero mentida a la mitad
        long real = new FileInfo(_media.P422).Length;
        _ = Assert.Throws<InvalidDataException>(() => MovDemuxer.Validate(info, real / 2));
    }

    // ------------------------------------------------------------------
    // Decodificador: las dos trampas que costaron caro en el paso 1
    // ------------------------------------------------------------------

    [Fact]
    public void SinElFourccDelContenedor_ElDecodificadorSeQuedaA10BitsEnUn4444()
    {
        if (Skip) return;
        var info = MovDemuxer.Open(_media.P4444);

        // CON el fourcc: 12 bits (yuva444p12le)
        using (var conTag = Decode(_media.P4444, info, info.VideoFourcc))
        {
            Assert.Equal(AVPixelFormat.AV_PIX_FMT_YUVA444P12LE, conTag.PixelFormat);
            Assert.Equal(12, conTag.Layout.BitDepth);
            Assert.True(conTag.Layout.HasAlpha);
            Assert.Equal(4, conTag.Layout.PlaneCount);
        }

        // SIN el fourcc: el decodificador no sabe el perfil y se queda a 10.
        // Dos bits por muestra perdidos por no pasar 4 caracteres.
        using var sinTag = Decode(_media.P4444, info, fourcc: null);
        Assert.Equal(AVPixelFormat.AV_PIX_FMT_YUVA444P10LE, sinTag.PixelFormat);
        Assert.Equal(10, sinTag.Layout.BitDepth);
    }

    [Fact]
    public void Un422SaleComoYuv422p10le_ConTresPlanosYSinAlfa()
    {
        if (Skip) return;
        var info = MovDemuxer.Open(_media.P422Hq);
        using var d = Decode(_media.P422Hq, info, info.VideoFourcc);

        Assert.Equal(AVPixelFormat.AV_PIX_FMT_YUV422P10LE, d.PixelFormat);
        PixelLayout layout = d.Layout;
        Assert.Equal(3, layout.PlaneCount);
        Assert.False(layout.HasAlpha);
        Assert.Equal(320, layout.PlaneWidth(0, 320));
        Assert.Equal(160, layout.PlaneWidth(1, 320));   // croma a media anchura
        Assert.Equal(240, layout.PlaneHeight(1, 240));  // …y altura completa
    }

    [Fact]
    public void TrasElDrenaje_HayQueResetearParaVolverAUsarlo()
    {
        if (Skip) return;
        var info = MovDemuxer.Open(_media.P422);
        using var buffer = new PaddedBuffer(info.MaxSampleSize);
        using var d = new ProResDecoder(info.Width, info.Height, 2, ThreadMode.Slice, info.VideoFourcc);
        using FileStream fs = File.OpenRead(_media.P422);

        Send(d, buffer, fs, info.Samples[0]);
        Assert.True(d.TryReceiveFrame());

        // Drenaje: el contexto queda en modo EOF PARA SIEMPRE
        d.Flush();
        while (d.TryReceiveFrame()) { }
        _ = Assert.Throws<InvalidDataException>(() => Send(d, buffer, fs, info.Samples[1]));

        // Reset lo devuelve a la vida — esto pasa en CADA pasada de loop
        d.Reset();
        Send(d, buffer, fs, info.Samples[1]);
        Assert.True(d.TryReceiveFrame());
    }

    [Fact]
    public void EsTodoIntra_ElFrame20SueltoEsIgualQueDecodificadoEnSecuencia()
    {
        if (Skip) return;
        var info = MovDemuxer.Open(_media.P422);
        const int Target = 20;

        ushort[][] secuencial;
        using (var d = new ProResDecoder(info.Width, info.Height, 2, ThreadMode.Slice, info.VideoFourcc))
        using (var buffer = new PaddedBuffer(info.MaxSampleSize))
        using (FileStream fs = File.OpenRead(_media.P422))
        {
            for (int i = 0; i <= Target; i++)
            {
                Send(d, buffer, fs, info.Samples[i]);
                Assert.True(d.TryReceiveFrame());
            }

            secuencial = d.CopyPlanes();
        }

        ushort[][] suelto;
        using (var d = new ProResDecoder(info.Width, info.Height, 2, ThreadMode.Slice, info.VideoFourcc))
        using (var buffer = new PaddedBuffer(info.MaxSampleSize))
        using (FileStream fs = File.OpenRead(_media.P422))
        {
            Send(d, buffer, fs, info.Samples[Target]);
            Assert.True(d.TryReceiveFrame());
            suelto = d.CopyPlanes();
        }

        for (int p = 0; p < secuencial.Length; p++)
            Assert.Equal(secuencial[p], suelto[p]);
    }

    [Fact]
    public void ElMismoFrameEsIdenticoConUnHiloYConVarios()
    {
        if (Skip) return;
        var info = MovDemuxer.Open(_media.P422Hq);

        ushort[][] uno = DecodeFirstPlanes(info, _media.P422Hq, 1, ThreadMode.SingleThread);
        ushort[][] varios = DecodeFirstPlanes(info, _media.P422Hq, 8, ThreadMode.Slice);

        for (int p = 0; p < uno.Length; p++)
            Assert.Equal(uno[p], varios[p]);
    }

    [Fact]
    public void ElModoDeProduccionEsSliceYNuncaFrame()
    {
        if (Skip) return;
        var info = MovDemuxer.Open(_media.P422);
        using var d = new ProResDecoder(info.Width, info.Height, 6, ThreadMode.Slice, info.VideoFourcc);

        Assert.Equal(ThreadMode.Slice, d.RequestedMode);
        Assert.Equal(ffmpeg.FF_THREAD_SLICE, d.ActiveThreadType & ffmpeg.FF_THREAD_SLICE);
        Assert.Equal(0, d.ActiveThreadType & ffmpeg.FF_THREAD_FRAME);
    }

    [Fact]
    public void ElBitstreamMandaSobreElContenedor_EnLaMatrizDeColor()
    {
        if (Skip) return;
        var info = MovDemuxer.Open(_media.Bt2020);
        using var d = Decode(_media.Bt2020, info, info.VideoFourcc);

        Assert.Equal(AVColorSpace.AVCOL_SPC_BT2020_NCL, d.ColorSpace);
        ColorParams p = ColorParams.ForFrame(d.Layout, d.ColorSpace, d.ColorRange, info.Colr);
        ColorConvert.Coefficients k = ColorConvert.CoefficientsFor(ColorMatrix.Bt2020Ncl);
        Assert.Equal((float)k.CrToR, p.CrToR, 5);
    }

    [Fact]
    public void SinSeñalizacionNinguna_CaeABt709()
    {
        if (Skip) return;
        var info = MovDemuxer.Open(_media.P4444);   // sin colorspace ni colr
        using var d = Decode(_media.P4444, info, info.VideoFourcc);

        ColorParams p = ColorParams.ForFrame(d.Layout, d.ColorSpace, d.ColorRange, info.Colr);
        ColorConvert.Coefficients k709 = ColorConvert.CoefficientsFor(ColorMatrix.Bt709);
        Assert.Equal((float)k709.CrToR, p.CrToR, 5);
    }

    [Fact]
    public void DimensionesImposibles_SeRechazanAntesDeTocarNadaNativo()
    {
        if (!ProResMediaFixture.FFmpegDllsOk) return;
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProResDecoder(0, 240, 1, ThreadMode.Slice, "apcn"));
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProResDecoder(320, MovDemuxer.MaxDimension + 1, 1, ThreadMode.Slice, "apcn"));
    }

    [Fact]
    public void FormatoDePixelInesperado_SeRechazaConMensajeClaro()
    {
        // Puerta de seguridad: mejor una excepción que interpretar punteros
        // nativos con el layout equivocado.
        _ = Assert.Throws<NotSupportedException>(
            () => PixelLayout.For(AVPixelFormat.AV_PIX_FMT_YUV420P));
    }

    [Fact]
    public void MktagEmpaquetaElFourccComoFFmpeg()
    {
        Assert.Equal(0x6E637061u, ProResDecoder.Mktag("apcn"));   // 'n''c''p''a'
        _ = Assert.Throws<ArgumentException>(() => ProResDecoder.Mktag("apc"));
    }

    // ------------------------------------------------------------------
    // Presupuesto de hilos (§ 4 del INFORME: reparto, no ProcessorCount)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1, 24, 8)]     // una capa: techo, no 24
    [InlineData(2, 24, 8)]
    [InlineData(3, 24, 8)]     // 3 × 8 = 24 lógicos, el reparto medido
    [InlineData(4, 24, 6)]
    [InlineData(8, 24, 3)]
    [InlineData(24, 24, 2)]    // suelo
    [InlineData(3, 4, 2)]      // máquina pequeña: suelo
    [InlineData(0, 24, 8)]     // capas 0 se trata como 1
    public void PresupuestoDeHilosPorCapa(int layers, int logical, int expected) =>
        Assert.Equal(expected, ThreadBudget.For(layers, logical));

    [Fact]
    public void PresupuestoDeHilosSiempreDentroDeLosLimites()
    {
        for (int layers = 1; layers <= 16; layers++)
        {
            int t = ThreadBudget.For(layers);
            Assert.InRange(t, ThreadBudget.Min, ThreadBudget.Max);
        }
    }

    // ------------------------------------------------------------------
    // Color: función pura contra valores de referencia
    // ------------------------------------------------------------------

    [Fact]
    public void NegroYBlancoLimitadosSonExactos()
    {
        // Rango limitado a 10 bits: negro = 64, blanco = 940
        Assert.Equal(0.0, ColorConvert.NormalizeLuma(64, 10, fullRange: false), 6);
        Assert.Equal(1.0, ColorConvert.NormalizeLuma(940, 10, fullRange: false), 6);
        // A 12 bits: negro = 256, blanco = 3760
        Assert.Equal(0.0, ColorConvert.NormalizeLuma(256, 12, fullRange: false), 6);
        Assert.Equal(1.0, ColorConvert.NormalizeLuma(3760, 12, fullRange: false), 6);
    }

    [Fact]
    public void CoeficientesDeLasRecomendaciones()
    {
        var k709 = ColorConvert.CoefficientsFor(ColorMatrix.Bt709);
        Assert.Equal(1.5748, k709.CrToR, 6);
        Assert.Equal(1.8556, k709.CbToB, 6);

        var k601 = ColorConvert.CoefficientsFor(ColorMatrix.Bt601);
        Assert.Equal(1.402, k601.CrToR, 6);
        Assert.Equal(1.772, k601.CbToB, 6);

        var k2020 = ColorConvert.CoefficientsFor(ColorMatrix.Bt2020Ncl);
        Assert.Equal(1.4746, k2020.CrToR, 6);
        Assert.Equal(1.8814, k2020.CbToB, 6);
    }

    [Theory]
    [InlineData(0, ColorMatrix.Bt709)]
    [InlineData(1, ColorMatrix.Bt709)]
    [InlineData(2, ColorMatrix.Bt709)]
    [InlineData(5, ColorMatrix.Bt601)]
    [InlineData(6, ColorMatrix.Bt601)]
    [InlineData(9, ColorMatrix.Bt2020Ncl)]
    [InlineData(10, ColorMatrix.Bt2020Ncl)]
    public void TraduccionDeCodigosIso23001_8(int code, ColorMatrix expected) =>
        Assert.Equal(expected, ColorConvert.MatrixFromCode(code));

    [Theory]
    [InlineData(10, false)]
    [InlineData(10, true)]
    [InlineData(12, false)]
    [InlineData(12, true)]
    public void LasConstantesDelShaderReproducenLaNormalizacionDeCpu(int bitDepth, bool fullRange)
    {
        ColorParams p = ColorParams.Create(bitDepth, fullRange, ColorMatrix.Bt709, hasAlpha: false);
        int max = (1 << bitDepth) - 1;

        foreach (int sample in new[] { 0, 16 << (bitDepth - 8), max / 3, max / 2, max })
        {
            // Lo que hace la GPU: código/65535 · yScale + yOffset
            double gpu = (sample / 65535.0 * p.YScale) + p.YOffset;
            double cpu = ColorConvert.NormalizeLuma(sample, bitDepth, fullRange);
            Assert.Equal(cpu, gpu, 4);

            double gpuC = (sample / 65535.0 * p.CScale) + p.COffset;
            double cpuC = ColorConvert.NormalizeChroma(sample, bitDepth, fullRange);
            Assert.Equal(cpuC, gpuC, 4);
        }
    }

    [Fact]
    public void FirmaDelLayout_DistingueLosPerfiles()
    {
        // La clave del pool de slots GPU necesita esto: 422 a 10 bits y 4444 a
        // 12 no pueden compartir sesión (3 planos a media anchura vs 4 enteros).
        string s422 = PixelLayout.For(AVPixelFormat.AV_PIX_FMT_YUV422P10LE).Signature;
        string s4444 = PixelLayout.For(AVPixelFormat.AV_PIX_FMT_YUVA444P12LE).Signature;
        Assert.NotEqual(s422, s4444);
    }

    [Fact]
    public void BytesPorFrame_CuadranConLoMedidoEnElLaboratorio()
    {
        // 1080p 422 10 bits = 7,91 MB · 1080p 4444 12 bits = 15,82 MB
        var l422 = PixelLayout.For(AVPixelFormat.AV_PIX_FMT_YUV422P10LE);
        Assert.Equal(1920L * 1080 * 2 * 2, l422.FrameBytes(1920, 1080));

        var l4444 = PixelLayout.For(AVPixelFormat.AV_PIX_FMT_YUVA444P12LE);
        Assert.Equal(1920L * 1080 * 2 * 4, l4444.FrameBytes(1920, 1080));

        // 4K 4444: 63 MB por frame. Un frame sin liberar es una fuga enorme.
        Assert.Equal(3840L * 2160 * 2 * 4, l4444.FrameBytes(3840, 2160));
    }

    // ------------------------------------------------------------------
    // Licencia (función pura: sin FFmpeg cargado)
    // ------------------------------------------------------------------

    [Fact]
    public void Licencia_LgplLimpiaEsApta()
    {
        var r = LicenseCheck.Evaluate("--enable-version3 --enable-shared",
            new Dictionary<string, string> { ["avcodec"] = "LGPL version 3 or later" });
        Assert.Equal(LicenseVerdict.Lgpl, r.Verdict);
    }

    [Fact]
    public void Licencia_LgplContieneGpl_YAunAsiEsApta()
    {
        // La trampa obvia: "LGPL".Contains("GPL") es true.
        var r = LicenseCheck.Evaluate("--enable-shared",
            new Dictionary<string, string>
            {
                ["avutil"] = "LGPL version 2.1 or later",
                ["avcodec"] = "LGPL version 2.1 or later",
            });
        Assert.Equal(LicenseVerdict.Lgpl, r.Verdict);
    }

    [Fact]
    public void Licencia_ConfigureConGpl_SeRechaza()
    {
        var r = LicenseCheck.Evaluate("--enable-gpl --enable-version3",
            new Dictionary<string, string> { ["avcodec"] = "LGPL version 3 or later" });
        Assert.Equal(LicenseVerdict.Gpl, r.Verdict);
    }

    [Fact]
    public void Licencia_LibreriaQueDeclaraGpl_SeRechazaAunqueConfigureParezcaLimpio()
    {
        var r = LicenseCheck.Evaluate("--enable-shared",
            new Dictionary<string, string> { ["avcodec"] = "GPL version 2 or later" });
        Assert.Equal(LicenseVerdict.Gpl, r.Verdict);
    }

    [Fact]
    public void Licencia_NonfreeGanaAGpl()
    {
        var r = LicenseCheck.Evaluate("--enable-gpl --enable-nonfree",
            new Dictionary<string, string> { ["avcodec"] = "nonfree and unredistributable" });
        Assert.Equal(LicenseVerdict.NonFree, r.Verdict);
    }

    [Fact]
    public void Licencia_SinDatos_EsIndeterminadoYNoApto()
    {
        var r = LicenseCheck.Evaluate("--enable-shared", new Dictionary<string, string>());
        Assert.Equal(LicenseVerdict.Unknown, r.Verdict);
        Assert.NotEqual(LicenseVerdict.Lgpl, r.Verdict);
    }

    [Fact]
    public void Licencia_LasDllQueSeVanADistribuirSonLgpl()
    {
        // Integración: si están las DLL, el veredicto REAL tiene que ser LGPL.
        // Es el mismo criterio que aplica el smoke de publicación.
        if (!ProResMediaFixture.FFmpegDllsOk) return;
        Assert.Equal(LicenseVerdict.Lgpl, LicenseCheck.Inspect().Verdict);
    }

    // ------------------------------------------------------------------
    // Utilidades
    // ------------------------------------------------------------------

    private static void Send(ProResDecoder d, PaddedBuffer buffer, FileStream fs, SampleEntry sample)
    {
        _ = buffer.ReadSample(fs, sample);
        d.SendPacket(buffer.AsPacket(sample.Size));
    }

    /// <summary>Decodifica el primer frame y devuelve el decodificador con el
    /// frame RETENIDO (el llamante lo libera con el using).</summary>
    private static ProResDecoder Decode(string path, MovInfo info, string? fourcc)
    {
        var d = new ProResDecoder(info.Width, info.Height, 2, ThreadMode.Slice, fourcc);
        try
        {
            using var buffer = new PaddedBuffer(info.MaxSampleSize);
            using FileStream fs = File.OpenRead(path);
            Send(d, buffer, fs, info.Samples[0]);
            Assert.True(d.TryReceiveFrame());
            return d;
        }
        catch
        {
            d.Dispose();
            throw;
        }
    }

    private static ushort[][] DecodeFirstPlanes(MovInfo info, string path, int threads, ThreadMode mode)
    {
        using var d = new ProResDecoder(info.Width, info.Height, threads, mode, info.VideoFourcc);
        using var buffer = new PaddedBuffer(info.MaxSampleSize);
        using FileStream fs = File.OpenRead(path);
        Send(d, buffer, fs, info.Samples[0]);
        if (mode == ThreadMode.Frame)
        {
            d.Flush();
        }

        Assert.True(d.TryReceiveFrame());
        return d.CopyPlanes();
    }
}
