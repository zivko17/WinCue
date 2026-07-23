namespace HapLab.Tests;

/// <summary>Tests del demuxer MOV contra clips reales generados por ffmpeg.</summary>
[Collection("media")]
public sealed class MovDemuxerTests
{
    private readonly MediaFixture _media;

    public MovDemuxerTests(MediaFixture media) => _media = media;

    [Theory]
    [InlineData("lab-hap1.mov", "Hap1")]
    [InlineData("lab-hap5.mov", "Hap5")]
    [InlineData("lab-hapy.mov", "HapY")]
    public void Demuxa_FourccDimensionesFpsYFrames(string fichero, string fourccEsperado)
    {
        MovInfo info = MovDemuxer.Open(_media.PathOf(fichero));

        Assert.Equal(fourccEsperado, info.VideoFourcc);
        Assert.Equal(320, info.Width);
        Assert.Equal(240, info.Height);
        Assert.Equal(30.0, info.Fps, precision: 3);
        Assert.Equal(30, info.FrameCount);
        Assert.Equal(1.0, info.DurationSeconds, precision: 3);
        Assert.Null(info.Audio);
    }

    [Fact]
    public void TablaDeSamples_OffsetsYTamanosDentroDelFichero()
    {
        string path = _media.PathOf("lab-hap1.mov");
        long fileLength = new FileInfo(path).Length;
        MovInfo info = MovDemuxer.Open(path);

        Assert.Equal(info.FrameCount, info.Samples.Count);
        foreach (SampleEntry s in info.Samples)
        {
            Assert.True(s.Size > 0, "Un sample no puede medir 0 bytes");
            Assert.True(s.Offset >= 0 && s.Offset + s.Size <= fileLength, $"Sample fuera del fichero: {s}");
        }

        // Los samples de un mismo fichero no deben solaparse
        var ordenados = info.Samples.OrderBy(s => s.Offset).ToList();
        for (int i = 1; i < ordenados.Count; i++)
        {
            Assert.True(ordenados[i].Offset >= ordenados[i - 1].Offset + ordenados[i - 1].Size, "Samples solapados");
        }
    }

    [Fact]
    public void PistaDeAudio_SeEnumeraPeroNoSeDecodifica()
    {
        MovInfo info = MovDemuxer.Open(_media.PathOf("lab-audio.mov"));

        Assert.Equal("Hap1", info.VideoFourcc);
        Assert.Equal(30, info.FrameCount);
        Assert.NotNull(info.Audio);
        Assert.True(info.Audio!.Channels >= 1);
        Assert.True(info.Audio.SampleRate > 8000);
        Assert.Equal(16, info.Audio.SampleSizeBits);
    }

    [Fact]
    public void FicheroSinMoov_Lanza()
    {
        string tmp = Path.Combine(_media.MediaDir, "no-es-un-mov.bin");
        File.WriteAllBytes(tmp, new byte[64]);
        try
        {
            Assert.Throws<InvalidDataException>(() => MovDemuxer.Open(tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
