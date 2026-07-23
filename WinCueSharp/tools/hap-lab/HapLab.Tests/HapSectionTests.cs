namespace HapLab.Tests;

/// <summary>Tests unitarios puros del parser de secciones HAP (sin ficheros).</summary>
public sealed class HapSectionTests
{
    [Fact]
    public void CabeceraDe4Bytes_TamanoEnTresBytesLittleEndian()
    {
        // Tamaño 0x030201 = 197121, tipo 0xBB (Snappy + RGB DXT1)
        byte[] datos = [0x01, 0x02, 0x03, 0xBB];
        (int size, byte type, int dataStart) = HapFrame.ReadSectionHeader(datos, 0);

        Assert.Equal(0x030201, size);
        Assert.Equal(0xBB, type);
        Assert.Equal(4, dataStart);
    }

    [Fact]
    public void CabeceraExtendida_TresBytesACero_TamanoEnUint32Posterior()
    {
        // Los 3 primeros bytes a cero → el tamaño real va en los 4 bytes tras el tipo
        byte[] datos = [0x00, 0x00, 0x00, 0xAB, 0x78, 0x56, 0x34, 0x12];
        (int size, byte type, int dataStart) = HapFrame.ReadSectionHeader(datos, 0);

        Assert.Equal(0x12345678, size);
        Assert.Equal(0xAB, type);
        Assert.Equal(8, dataStart);
    }

    [Fact]
    public void CabeceraTruncada_Lanza()
    {
        byte[] cortos = [0x01, 0x02];
        Assert.Throws<InvalidDataException>(() => HapFrame.ReadSectionHeader(cortos, 0));

        byte[] extendidaCorta = [0x00, 0x00, 0x00, 0xAB, 0x01];
        Assert.Throws<InvalidDataException>(() => HapFrame.ReadSectionHeader(extendidaCorta, 0));
    }

    [Theory]
    [InlineData(1920, 1080, HapTextureFormat.RgbDxt1, 1920 * 1080 / 2)]
    [InlineData(1920, 1080, HapTextureFormat.RgbaDxt5, 1920 * 1080)]
    [InlineData(1920, 1080, HapTextureFormat.YCoCgDxt5, 1920 * 1080)]
    [InlineData(3840, 2160, HapTextureFormat.RgbDxt1, 3840 * 2160 / 2)]
    [InlineData(322, 242, HapTextureFormat.RgbDxt1, 324 * 244 / 2)] // redondeo a múltiplo de 4
    [InlineData(1, 1, HapTextureFormat.RgbaDxt5, 16)]               // un solo bloque
    public void DxtSize_CalculaBloques4x4(int w, int h, HapTextureFormat formato, int esperado)
    {
        Assert.Equal(esperado, HapFrame.DxtSize(w, h, formato));
    }

    [Fact]
    public void FrameSinComprimir_SeCopiaTalCual()
    {
        // Sección top-level 0xAB (None + RGB DXT1) con 8 bytes de "DXT"
        byte[] dxt = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] frame = [8, 0, 0, 0xAB, .. dxt];

        byte[] salida = new byte[8];
        (HapTextureFormat formato, int escritos) = HapFrame.Decode(frame, salida, parallel: false);

        Assert.Equal(HapTextureFormat.RgbDxt1, formato);
        Assert.Equal(8, escritos);
        Assert.Equal(dxt, salida);
    }

    [Fact]
    public void TipoDesconocido_Lanza()
    {
        byte[] frame = [4, 0, 0, 0xD7, 0, 0, 0, 0];
        Assert.Throws<InvalidDataException>(() => HapFrame.Decode(frame, new byte[16], parallel: false));
    }
}
