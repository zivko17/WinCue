namespace ProResLab.Tests;

/// <summary>
/// El demuxer propio (el de hap-lab, ampliado) sobre ficheros ProRes reales.
/// </summary>
public sealed class MovDemuxerTests
{
    private readonly MediaFixture _media = MediaFixture.Instance;

    [Theory]
    [InlineData("t-proxy.mov", "apco", ProResProfile.Proxy)]
    [InlineData("t-lt.mov", "apcs", ProResProfile.Lt)]
    [InlineData("t-422.mov", "apcn", ProResProfile.Standard422)]
    [InlineData("t-422hq.mov", "apch", ProResProfile.Hq422)]
    [InlineData("t-4444.mov", "ap4h", ProResProfile.P4444)]
    public void CadaPerfil_SeReconocePorSuFourcc(string file, string fourcc, ProResProfile profile)
    {
        MovInfo info = MovDemuxer.Open(_media.Clip(file));

        Assert.Equal(fourcc, info.VideoFourcc);
        Assert.Equal(profile, info.Profile);
        Assert.Equal(MediaFixture.Width, info.Width);
        Assert.Equal(MediaFixture.Height, info.Height);
        Assert.Equal(MediaFixture.Frames, info.FrameCount);
        Assert.Equal(30.0, info.Fps, 3);
        Assert.Equal(1.0, info.DurationSeconds, 3);
    }

    [Fact]
    public void TodosLosFourccDeProRes_EstanEnLaTabla()
    {
        Assert.Equal(ProResProfile.Proxy, MovDemuxer.ProfileOf("apco"));
        Assert.Equal(ProResProfile.Lt, MovDemuxer.ProfileOf("apcs"));
        Assert.Equal(ProResProfile.Standard422, MovDemuxer.ProfileOf("apcn"));
        Assert.Equal(ProResProfile.Hq422, MovDemuxer.ProfileOf("apch"));
        Assert.Equal(ProResProfile.P4444, MovDemuxer.ProfileOf("ap4h"));
        Assert.Equal(ProResProfile.P4444Xq, MovDemuxer.ProfileOf("ap4x"));
    }

    [Theory]
    [InlineData("Hap1")]
    [InlineData("avc1")]
    [InlineData("apcz")]
    public void UnFourccQueNoEsProRes_NoSeConfunde(string fourcc)
        => Assert.Equal(ProResProfile.None, MovDemuxer.ProfileOf(fourcc));

    [Fact]
    public void ProRes4444_DeclaraDepth32_QueEsLoQueDelataElAlfa()
    {
        Assert.Equal(32, MovDemuxer.Open(_media.Clip("t-4444.mov")).Depth);
        Assert.Equal(24, MovDemuxer.Open(_media.Clip("t-422hq.mov")).Depth);
    }

    [Fact]
    public void LaPistaDeAudio_SeEnumeraYSeIgnora()
    {
        MovInfo info = MovDemuxer.Open(_media.Clip("t-audio.mov"));

        Assert.NotNull(info.Audio);
        Assert.Equal("sowt", info.Audio!.Fourcc);   // PCM 16 LE de QuickTime
        Assert.Equal(16, info.Audio.SampleSizeBits);
        // La pista de vídeo sigue siendo la que manda
        Assert.Equal("apcn", info.VideoFourcc);
        Assert.Equal(MediaFixture.Frames, info.FrameCount);
    }

    [Fact]
    public void SinPistaDeAudio_ElCampoQuedaANulo()
    {
        Assert.Null(MovDemuxer.Open(_media.Clip("t-422.mov")).Audio);
    }

    [Fact]
    public void ElAtomoColr_SeLeeCuandoElFicheroLoTrae()
    {
        // 9 = BT.2020 no constant luminance, 1 = BT.709 (ISO/IEC 23001-8)
        Assert.Equal(9, MovDemuxer.Open(_media.Clip("t-2020.mov")).Colr.Matrix);
        Assert.Equal(1, MovDemuxer.Open(_media.Clip("t-709colr.mov")).Colr.Matrix);
    }

    [Fact]
    public void SinAtomoColr_QuedaSinEspecificarYNoInventaNada()
    {
        // ffmpeg NO escribe 'colr' al codificar ProRes salvo que se le pida:
        // el laboratorio no puede fingir que sí.
        ColrInfo colr = MovDemuxer.Open(_media.Clip("t-422hq.mov")).Colr;

        Assert.Equal(2, colr.Matrix);
        Assert.False(colr.HasMatrix);
    }

    [Fact]
    public void ColrSinEspecificar_NoSeConsideraUtilizable()
    {
        Assert.False(ColrInfo.Unspecified.HasMatrix);
        Assert.False(new ColrInfo(1, 1, 0, false).HasMatrix);  // 0 = identidad, tampoco
        Assert.True(new ColrInfo(1, 1, 1, false).HasMatrix);
    }

    [Fact]
    public void LaTablaDeSamples_CubreElFicheroYNadaSeSale()
    {
        string path = _media.Clip("t-422hq.mov");
        MovInfo info = MovDemuxer.Open(path);
        long length = new FileInfo(path).Length;

        Assert.Equal(MediaFixture.Frames, info.Samples.Count);
        foreach (SampleEntry s in info.Samples)
        {
            Assert.True(s.Size > 0);
            Assert.True(s.Offset >= 0 && s.Offset + s.Size <= length);
        }

        // Los samples van en orden creciente de offset (un solo mdat)
        for (int i = 1; i < info.Samples.Count; i++)
        {
            Assert.True(info.Samples[i].Offset >= info.Samples[i - 1].Offset);
        }
    }

    [Fact]
    public void MaxSampleSize_EsElMayorDeVerdad()
    {
        MovInfo info = MovDemuxer.Open(_media.Clip("t-422hq.mov"));
        int expected = info.Samples.Max(s => s.Size);

        Assert.Equal(expected, info.MaxSampleSize);
    }

    [Fact]
    public void UnMp4DeH264_SeDemuxaPeroNoEsProRes()
    {
        // El demuxer es del CONTENEDOR, no del códec: lee el mp4 sin problema,
        // y quien decide que no es asunto suyo es el perfil.
        MovInfo info = MovDemuxer.Open(_media.Clip("t-h264.mp4"));

        Assert.Equal(ProResProfile.None, info.Profile);
    }

    [Fact]
    public void UnFicheroQueNoEsMov_SeRechazaConUnErrorClaro()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"prores-lab-{Guid.NewGuid():N}.mov");
        File.WriteAllBytes(tmp, new byte[512]);   // todo ceros: ni ftyp ni moov
        try
        {
            Assert.Throws<InvalidDataException>(() => MovDemuxer.Open(tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // ------------------------------------------------------------------
    //  Validación: la primera línea de defensa antes del decodificador nativo
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_RechazaUnSampleQueSeSaleDelFichero()
    {
        string path = _media.Clip("t-422.mov");
        MovInfo real = MovDemuxer.Open(path);
        MovInfo trucado = Clone(real, [new SampleEntry(real.Samples[0].Offset, int.MaxValue / 2)]);

        Assert.Throws<InvalidDataException>(() => MovDemuxer.Validate(trucado, new FileInfo(path).Length));
    }

    [Fact]
    public void Validate_RechazaUnSampleDeTamanoCero()
    {
        string path = _media.Clip("t-422.mov");
        MovInfo real = MovDemuxer.Open(path);
        MovInfo trucado = Clone(real, [new SampleEntry(0, 0)]);

        Assert.Throws<InvalidDataException>(() => MovDemuxer.Validate(trucado, new FileInfo(path).Length));
    }

    [Fact]
    public void Validate_RechazaDimensionesImposibles()
    {
        string path = _media.Clip("t-422.mov");
        MovInfo real = MovDemuxer.Open(path);
        MovInfo trucado = new(
            real.VideoFourcc, MovDemuxer.MaxDimension + 1, real.Height, real.Fps, real.DurationSeconds,
            real.Samples, real.Audio, real.Colr, real.Depth);

        Assert.Throws<InvalidDataException>(() => MovDemuxer.Validate(trucado, new FileInfo(path).Length));
    }

    private static MovInfo Clone(MovInfo source, IReadOnlyList<SampleEntry> samples)
        => new(source.VideoFourcc, source.Width, source.Height, source.Fps, source.DurationSeconds,
            samples, source.Audio, source.Colr, source.Depth);
}
