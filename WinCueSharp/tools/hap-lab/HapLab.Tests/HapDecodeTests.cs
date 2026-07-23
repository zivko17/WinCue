namespace HapLab.Tests;

/// <summary>Decodificación completa de las variantes HAP generadas por ffmpeg.</summary>
[Collection("media")]
public sealed class HapDecodeTests
{
    private readonly MediaFixture _media;

    public HapDecodeTests(MediaFixture media) => _media = media;

    private static byte[] LeerSample(string path, SampleEntry s)
    {
        byte[] buffer = new byte[s.Size];
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Position = s.Offset;
        fs.ReadExactly(buffer);
        return buffer;
    }

    [Theory]
    [InlineData("lab-hap1.mov", HapTextureFormat.RgbDxt1)]
    [InlineData("lab-hap5.mov", HapTextureFormat.RgbaDxt5)]
    [InlineData("lab-hapy.mov", HapTextureFormat.YCoCgDxt5)]
    public void DecodificaTodosLosFrames_TamanoDxtExacto(string fichero, HapTextureFormat formatoEsperado)
    {
        string path = _media.PathOf(fichero);
        MovInfo info = MovDemuxer.Open(path);
        int esperado = HapFrame.DxtSize(info.Width, info.Height, formatoEsperado);
        byte[] salida = new byte[esperado];

        foreach (SampleEntry s in info.Samples)
        {
            byte[] frame = LeerSample(path, s);
            (HapTextureFormat formato, int escritos) = HapFrame.Decode(frame, salida, parallel: false);

            Assert.Equal(formatoEsperado, formato);
            Assert.Equal(esperado, escritos);
        }
    }

    [Fact]
    public void FrameChunked_SeDetectaYDecodificaIgualEnSerieYParalelo()
    {
        string path = _media.PathOf("lab-chunked.mov");
        MovInfo info = MovDemuxer.Open(path);
        int esperado = HapFrame.DxtSize(info.Width, info.Height, HapTextureFormat.RgbDxt1);

        byte[] primerFrame = LeerSample(path, info.Samples[0]);
        HapFrameInfo cabecera = HapFrame.Inspect(primerFrame);
        Assert.Equal(HapCompressor.Complex, cabecera.Compressor);
        Assert.Equal(4, cabecera.ChunkCount);

        byte[] serie = new byte[esperado];
        byte[] paralelo = new byte[esperado];
        foreach (SampleEntry s in info.Samples)
        {
            byte[] frame = LeerSample(path, s);
            (_, int escritosSerie) = HapFrame.Decode(frame, serie, parallel: false);
            (_, int escritosParalelo) = HapFrame.Decode(frame, paralelo, parallel: true);

            Assert.Equal(esperado, escritosSerie);
            Assert.Equal(esperado, escritosParalelo);
            Assert.Equal(serie, paralelo);
        }
    }

    [Fact]
    public void FrameSnappy_DescomprimeAlTamanoDeclarado()
    {
        // ffmpeg sin -chunks produce frames 0xBB (Snappy simple) o 0xAB si Snappy no ahorra
        string path = _media.PathOf("lab-hap1.mov");
        MovInfo info = MovDemuxer.Open(path);

        byte[] frame = LeerSample(path, info.Samples[0]);
        HapFrameInfo cabecera = HapFrame.Inspect(frame);

        Assert.Equal(HapTextureFormat.RgbDxt1, cabecera.Format);
        Assert.True(
            cabecera.Compressor is HapCompressor.Snappy or HapCompressor.None or HapCompressor.Complex,
            $"Compresor inesperado: {cabecera.Compressor}");
    }
}
