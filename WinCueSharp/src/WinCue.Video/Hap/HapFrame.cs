// =============================================================================
// HapFrame.cs — Parser/descompresor de frames HAP según la spec de Vidvox
// (promocionado de tools/hap-lab, paso 3 del motor de vídeo propio)
// =============================================================================

using System.Buffers.Binary;

using Snappier;

namespace WinCue.Video.Hap;

/// <summary>Formato de textura DXT contenido en un frame HAP.</summary>
public enum HapTextureFormat
{
    /// <summary>Hap1: RGB DXT1/BC1 (0x_B). 8 bytes por bloque 4x4 → w*h/2.</summary>
    RgbDxt1,

    /// <summary>Hap Alpha (Hap5): RGBA DXT5/BC3 (0x_E). 16 bytes por bloque 4x4 → w*h.</summary>
    RgbaDxt5,

    /// <summary>Hap Q (HapY): YCoCg escalado en DXT5/BC3 (0x_F). 16 bytes por bloque 4x4 → w*h.</summary>
    YCoCgDxt5,
}

/// <summary>Compresor de segunda etapa de un frame HAP.</summary>
public enum HapCompressor
{
    /// <summary>0xA_: datos DXT en crudo.</summary>
    None,

    /// <summary>0xB_: un único bloque Snappy.</summary>
    Snappy,

    /// <summary>0xC_: modo complex/chunked (Decode Instructions), descomprimible por chunks en paralelo.</summary>
    Complex,
}

/// <summary>Resultado de parsear la cabecera de sección de un frame HAP (sin descomprimir).</summary>
public readonly record struct HapFrameInfo(HapTextureFormat Format, HapCompressor Compressor, int ChunkCount);

/// <summary>
/// Parser y descompresor de frames HAP según la especificación pública de Vidvox
/// (hap/documentation/HapVideoDRAFT.md). Soporta los tres formatos que produce
/// ffmpeg (Hap1/Hap5/HapY) con segunda etapa None, Snappy o Complex (chunked).
/// </summary>
public static class HapFrame
{
    // Nibble alto del byte de tipo de la sección de nivel superior
    private const int CompressorNone = 0xA;
    private const int CompressorSnappy = 0xB;
    private const int CompressorComplex = 0xC;

    // Nibble bajo: formato de píxel
    private const int FormatRgbDxt1 = 0xB;
    private const int FormatRgbaDxt5 = 0xE;
    private const int FormatYCoCgDxt5 = 0xF;

    // Tipos de sección dentro del modo complex
    private const byte SectionDecodeInstructions = 0x01;
    private const byte SectionChunkCompressors = 0x02;
    private const byte SectionChunkSizes = 0x03;
    private const byte SectionChunkOffsets = 0x04;

    // Compresores por chunk
    private const byte ChunkUncompressed = 0x0A;
    private const byte ChunkSnappy = 0x0B;

    /// <summary>Bytes de textura DXT que ocupa un frame descomprimido (w y h se redondean a múltiplo de 4).</summary>
    public static int DxtSize(int width, int height, HapTextureFormat format)
    {
        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        int bytesPerBlock = format == HapTextureFormat.RgbDxt1 ? 8 : 16;
        return blocksX * blocksY * bytesPerBlock;
    }

    /// <summary>Parsea solo la cabecera de nivel superior del frame (formato, compresor y nº de chunks) sin descomprimir.</summary>
    public static HapFrameInfo Inspect(ReadOnlyMemory<byte> frame)
    {
        (int sectionSize, byte sectionType, int dataStart) = ReadSectionHeader(frame.Span, 0);
        (HapTextureFormat format, HapCompressor compressor) = DecodeSectionType(sectionType);

        int chunkCount = 1;
        if (compressor == HapCompressor.Complex)
        {
            ComplexLayout layout = ParseComplexLayout(frame, dataStart, sectionSize);
            chunkCount = layout.ChunkCount;
        }

        return new HapFrameInfo(format, compressor, chunkCount);
    }

    /// <summary>
    /// Decodifica un frame HAP completo a datos DXT crudos.
    /// </summary>
    /// <param name="frame">Bytes del sample tal cual vienen del contenedor.</param>
    /// <param name="output">Buffer de destino; debe medir al menos <see cref="DxtSize"/>.</param>
    /// <param name="parallel">Si el frame es chunked, descomprimir los chunks con Parallel.For.</param>
    /// <returns>Formato de textura y bytes escritos en <paramref name="output"/>.</returns>
    /// <exception cref="InvalidDataException">Frame malformado o tipo de sección no soportado.</exception>
    public static (HapTextureFormat Format, int BytesWritten) Decode(ReadOnlyMemory<byte> frame, byte[] output, bool parallel)
    {
        ArgumentNullException.ThrowIfNull(output);

        (int sectionSize, byte sectionType, int dataStart) = ReadSectionHeader(frame.Span, 0);
        (HapTextureFormat format, HapCompressor compressor) = DecodeSectionType(sectionType);

        if (dataStart + sectionSize > frame.Length)
        {
            throw new InvalidDataException(
                $"La sección de nivel superior declara {sectionSize} bytes pero el sample solo tiene {frame.Length - dataStart}.");
        }

        ReadOnlyMemory<byte> data = frame.Slice(dataStart, sectionSize);

        int written = compressor switch
        {
            HapCompressor.None => CopyRaw(data.Span, output),
            HapCompressor.Snappy => Snappy.Decompress(data.Span, output),
            HapCompressor.Complex => DecodeComplex(frame, dataStart, sectionSize, output, parallel),
            _ => throw new InvalidDataException($"Compresor desconocido en tipo de sección 0x{sectionType:X2}."),
        };

        return (format, written);
    }

    private static int CopyRaw(ReadOnlySpan<byte> data, byte[] output)
    {
        data.CopyTo(output);
        return data.Length;
    }

    // ------------------------------------------------------------------
    //  Modo complex (0xC_): Decode Instructions Container + chunks
    // ------------------------------------------------------------------

    private sealed class ComplexLayout
    {
        public ComplexLayout(byte[] compressors, int[] sizes, int[] offsets, int frameDataStart)
        {
            Compressors = compressors;
            Sizes = sizes;
            Offsets = offsets;
            FrameDataStart = frameDataStart;
        }

        public byte[] Compressors { get; }

        public int[] Sizes { get; }

        /// <summary>Offset de cada chunk relativo al inicio de los datos del frame.</summary>
        public int[] Offsets { get; }

        /// <summary>Offset absoluto (en el sample) del primer byte de datos de frame tras las instrucciones.</summary>
        public int FrameDataStart { get; }

        public int ChunkCount => Sizes.Length;
    }

    private static ComplexLayout ParseComplexLayout(ReadOnlyMemory<byte> frame, int sectionStart, int sectionSize)
    {
        ReadOnlySpan<byte> span = frame.Span;

        // La sección de datos comienza con una única sección de instrucciones,
        // seguida inmediatamente por los datos del frame.
        (int diSize, byte diType, int diBody) = ReadSectionHeader(span, sectionStart);
        if (diType != SectionDecodeInstructions)
        {
            throw new InvalidDataException($"Se esperaba Decode Instructions Container (0x01) y llegó 0x{diType:X2}.");
        }

        byte[]? compressors = null;
        int[]? sizes = null;
        int[]? offsets = null;

        int pos = diBody;
        int diEnd = diBody + diSize;
        while (pos < diEnd)
        {
            (int subSize, byte subType, int subBody) = ReadSectionHeader(span, pos);
            switch (subType)
            {
                case SectionChunkCompressors:
                    compressors = span.Slice(subBody, subSize).ToArray();
                    break;

                case SectionChunkSizes:
                    sizes = ReadUInt32Table(span, subBody, subSize);
                    break;

                case SectionChunkOffsets:
                    offsets = ReadUInt32Table(span, subBody, subSize);
                    break;

                default:
                    break; // sección desconocida: se ignora (la spec pide continuar)
            }

            pos = subBody + subSize;
        }

        if (compressors is null || sizes is null)
        {
            throw new InvalidDataException("Frame chunked sin Chunk Compressor Table (0x02) o Chunk Size Table (0x03).");
        }

        if (compressors.Length != sizes.Length || (offsets is not null && offsets.Length != sizes.Length))
        {
            throw new InvalidDataException("Las tablas de chunks tienen números de entradas distintos.");
        }

        // Sin Chunk Offset Table los offsets se derivan sumando los tamaños previos
        if (offsets is null)
        {
            offsets = new int[sizes.Length];
            int acc = 0;
            for (int i = 0; i < sizes.Length; i++)
            {
                offsets[i] = acc;
                acc += sizes[i];
            }
        }

        int frameDataStart = diEnd;
        int declaredEnd = sectionStart + sectionSize;
        for (int i = 0; i < sizes.Length; i++)
        {
            if (frameDataStart + offsets[i] + sizes[i] > declaredEnd)
            {
                throw new InvalidDataException($"El chunk {i} se sale de la sección de nivel superior.");
            }
        }

        return new ComplexLayout(compressors, sizes, offsets, frameDataStart);
    }

    /// <summary>Lee una tabla de uint32 little-endian (tamaños u offsets de chunks).</summary>
    private static int[] ReadUInt32Table(ReadOnlySpan<byte> span, int start, int byteLength)
    {
        if (byteLength % 4 != 0)
        {
            throw new InvalidDataException($"Tabla de chunks con longitud no múltiplo de 4: {byteLength} bytes.");
        }

        int count = byteLength / 4;
        int[] table = new int[count];
        for (int i = 0; i < count; i++)
        {
            table[i] = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(start + (i * 4), 4)));
        }

        return table;
    }

    private static int DecodeComplex(ReadOnlyMemory<byte> frame, int sectionStart, int sectionSize, byte[] output, bool parallel)
    {
        ComplexLayout layout = ParseComplexLayout(frame, sectionStart, sectionSize);

        // Offset de escritura de cada chunk en el buffer DXT de salida:
        // los chunks descomprimidos son contiguos, así que hay que conocer el
        // tamaño descomprimido de los anteriores.
        int chunkCount = layout.ChunkCount;
        int[] outOffsets = new int[chunkCount];
        int[] outSizes = new int[chunkCount];
        int totalOut = 0;
        for (int i = 0; i < chunkCount; i++)
        {
            ReadOnlySpan<byte> chunk = frame.Span.Slice(layout.FrameDataStart + layout.Offsets[i], layout.Sizes[i]);
            int decompressed = layout.Compressors[i] switch
            {
                ChunkUncompressed => chunk.Length,
                ChunkSnappy => Snappy.GetUncompressedLength(chunk),
                _ => throw new InvalidDataException($"Compresor de chunk desconocido: 0x{layout.Compressors[i]:X2}."),
            };
            outOffsets[i] = totalOut;
            outSizes[i] = decompressed;
            totalOut += decompressed;
        }

        if (totalOut > output.Length)
        {
            throw new InvalidDataException($"El frame descomprime a {totalOut} bytes pero el buffer mide {output.Length}.");
        }

        if (parallel && chunkCount > 1)
        {
            Parallel.For(0, chunkCount, i => DecodeChunk(frame, layout, i, output, outOffsets[i], outSizes[i]));
        }
        else
        {
            for (int i = 0; i < chunkCount; i++)
            {
                DecodeChunk(frame, layout, i, output, outOffsets[i], outSizes[i]);
            }
        }

        return totalOut;
    }

    private static void DecodeChunk(ReadOnlyMemory<byte> frame, ComplexLayout layout, int index, byte[] output, int outOffset, int outSize)
    {
        ReadOnlySpan<byte> chunk = frame.Span.Slice(layout.FrameDataStart + layout.Offsets[index], layout.Sizes[index]);
        Span<byte> dest = output.AsSpan(outOffset, outSize);

        if (layout.Compressors[index] == ChunkUncompressed)
        {
            chunk.CopyTo(dest);
        }
        else
        {
            int written = Snappy.Decompress(chunk, dest);
            if (written != outSize)
            {
                throw new InvalidDataException($"El chunk {index} descomprimió {written} bytes; se esperaban {outSize}.");
            }
        }
    }

    // ------------------------------------------------------------------
    //  Cabeceras de sección HAP
    // ------------------------------------------------------------------

    /// <summary>
    /// Lee una cabecera de sección HAP en <paramref name="offset"/>.
    /// 4 bytes: tamaño en 3 bytes little-endian + 1 byte de tipo. Si los tres
    /// primeros bytes son cero, la cabecera es de 8 bytes y el tamaño real va
    /// en los 4 bytes siguientes (uint32 little-endian).
    /// </summary>
    /// <returns>(tamaño de la sección, tipo, offset del primer byte de datos).</returns>
    internal static (int Size, byte Type, int DataStart) ReadSectionHeader(ReadOnlySpan<byte> span, int offset)
    {
        if (offset + 4 > span.Length)
        {
            throw new InvalidDataException("Sample demasiado corto para contener una cabecera de sección HAP.");
        }

        int size24 = span[offset] | (span[offset + 1] << 8) | (span[offset + 2] << 16);
        byte type = span[offset + 3];

        if (size24 != 0)
        {
            return (size24, type, offset + 4);
        }

        if (offset + 8 > span.Length)
        {
            throw new InvalidDataException("Cabecera de sección extendida truncada.");
        }

        uint size32 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 4, 4));
        return (checked((int)size32), type, offset + 8);
    }

    private static (HapTextureFormat Format, HapCompressor Compressor) DecodeSectionType(byte sectionType)
    {
        HapCompressor compressor = (sectionType >> 4) switch
        {
            CompressorNone => HapCompressor.None,
            CompressorSnappy => HapCompressor.Snappy,
            CompressorComplex => HapCompressor.Complex,
            _ => throw new InvalidDataException($"Tipo de sección de nivel superior no soportado: 0x{sectionType:X2}."),
        };

        HapTextureFormat format = (sectionType & 0x0F) switch
        {
            FormatRgbDxt1 => HapTextureFormat.RgbDxt1,
            FormatRgbaDxt5 => HapTextureFormat.RgbaDxt5,
            FormatYCoCgDxt5 => HapTextureFormat.YCoCgDxt5,
            _ => throw new InvalidDataException($"Formato de píxel no soportado en tipo 0x{sectionType:X2} (¿HapM/Hap7/HapH?)."),
        };

        return (format, compressor);
    }
}
