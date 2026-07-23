using System.Buffers.Binary;
using System.Text;

namespace HapLab;

/// <summary>Posición y tamaño de un sample (frame de vídeo) dentro del fichero .mov.</summary>
public readonly record struct SampleEntry(long Offset, int Size);

/// <summary>Información mínima de una pista de audio (solo se enumera, no se decodifica).</summary>
public sealed class AudioTrackInfo
{
    public AudioTrackInfo(string fourcc, int channels, int sampleSizeBits, double sampleRate)
    {
        Fourcc = fourcc;
        Channels = channels;
        SampleSizeBits = sampleSizeBits;
        SampleRate = sampleRate;
    }

    public string Fourcc { get; }
    public int Channels { get; }
    public int SampleSizeBits { get; }
    public double SampleRate { get; }
}

/// <summary>Resultado de demuxar un .mov: pista de vídeo HAP + tabla de samples.</summary>
public sealed class MovInfo
{
    internal MovInfo(
        string videoFourcc,
        int width,
        int height,
        double fps,
        double durationSeconds,
        IReadOnlyList<SampleEntry> samples,
        AudioTrackInfo? audio)
    {
        VideoFourcc = videoFourcc;
        Width = width;
        Height = height;
        Fps = fps;
        DurationSeconds = durationSeconds;
        Samples = samples;
        Audio = audio;
    }

    /// <summary>Fourcc del códec de vídeo: "Hap1", "Hap5" o "HapY".</summary>
    public string VideoFourcc { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Frames por segundo medios (timescale * frames / duración en unidades).</summary>
    public double Fps { get; }

    public double DurationSeconds { get; }

    /// <summary>Un elemento por frame de vídeo, en orden de decodificación.</summary>
    public IReadOnlyList<SampleEntry> Samples { get; }

    public int FrameCount => Samples.Count;

    /// <summary>Pista de audio si existe; HAP-lab la enumera pero no la decodifica.</summary>
    public AudioTrackInfo? Audio { get; }
}

/// <summary>
/// Demuxer mínimo de contenedor QuickTime/MOV, sin FFmpeg.
/// Parsea moov → trak → mdia → minf → stbl y reconstruye la tabla de
/// (offset, tamaño) de cada sample de vídeo a partir de stsz/stsc/stco(co64).
/// Solo soporta lo que produce ffmpeg al codificar HAP: un trak de vídeo
/// (más, opcionalmente, un trak de audio PCM que se ignora).
/// </summary>
public static class MovDemuxer
{
    /// <summary>Abre y demuxa un fichero .mov con vídeo HAP.</summary>
    /// <exception cref="InvalidDataException">El fichero no es un MOV válido o no tiene pista de vídeo.</exception>
    public static MovInfo Open(string path)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] moov = ReadMoovAtom(fs) ?? throw new InvalidDataException("No se encontró el átomo 'moov' en el fichero.");
        return ParseMoov(moov);
    }

    // ------------------------------------------------------------------
    //  Nivel de fichero: localizar el átomo moov (puede ir antes o después
    //  del mdat; ffmpeg lo escribe al final salvo con +faststart).
    // ------------------------------------------------------------------

    private static byte[]? ReadMoovAtom(FileStream fs)
    {
        long fileLength = fs.Length;
        long pos = 0;
        Span<byte> header = stackalloc byte[16];

        while (pos + 8 <= fileLength)
        {
            fs.Position = pos;
            ReadExactly(fs, header[..8]);
            long atomSize = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            string type = Encoding.ASCII.GetString(header.Slice(4, 4));
            long headerSize = 8;

            if (atomSize == 1)
            {
                // Tamaño extendido de 64 bits a continuación del tipo
                ReadExactly(fs, header.Slice(8, 8));
                atomSize = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8)));
                headerSize = 16;
            }
            else if (atomSize == 0)
            {
                // El átomo se extiende hasta el final del fichero
                atomSize = fileLength - pos;
            }

            if (atomSize < headerSize)
            {
                throw new InvalidDataException($"Átomo '{type}' con tamaño inválido ({atomSize}) en offset {pos}.");
            }

            if (type == "moov")
            {
                long payload = atomSize - headerSize;
                if (payload > int.MaxValue)
                {
                    throw new InvalidDataException("Átomo 'moov' demasiado grande.");
                }

                byte[] buffer = new byte[payload];
                ReadExactly(fs, buffer);
                return buffer;
            }

            pos += atomSize;
        }

        return null;
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        stream.ReadExactly(destination);
    }

    // ------------------------------------------------------------------
    //  Parseo del moov en memoria
    // ------------------------------------------------------------------

    private static MovInfo ParseMoov(byte[] moov)
    {
        TrackTables? video = null;
        AudioTrackInfo? audio = null;

        foreach ((string type, int start, int end) in Children(moov, 0, moov.Length))
        {
            if (type != "trak")
            {
                continue;
            }

            TrackTables track = ParseTrak(moov, start, end);
            if (track.Handler == "vide")
            {
                video ??= track;
            }
            else if (track.Handler == "soun" && audio is null)
            {
                audio = track.AudioInfo;
            }
        }

        if (video is null)
        {
            throw new InvalidDataException("El fichero no contiene ninguna pista de vídeo.");
        }

        IReadOnlyList<SampleEntry> samples = BuildSampleTable(video);

        double durationSeconds = video.TimeScale > 0 ? (double)video.TotalDelta / video.TimeScale : 0;
        double fps = video.TotalDelta > 0 ? (double)video.TimeScale * samples.Count / video.TotalDelta : 0;

        return new MovInfo(video.Fourcc, video.Width, video.Height, fps, durationSeconds, samples, audio);
    }

    /// <summary>Tablas crudas de un trak, tal cual salen del stbl.</summary>
    private sealed class TrackTables
    {
        public string Handler = "";
        public string Fourcc = "";
        public int Width;
        public int Height;
        public uint TimeScale;
        public long TotalDelta;              // suma de stts (unidades de timescale)
        public uint FixedSampleSize;         // stsz.sample_size (0 = tabla)
        public uint[] SampleSizes = [];      // stsz (si FixedSampleSize == 0)
        public int SampleCount;
        public (uint FirstChunk, uint SamplesPerChunk)[] SampleToChunk = []; // stsc
        public long[] ChunkOffsets = [];     // stco o co64
        public AudioTrackInfo? AudioInfo;
    }

    private static TrackTables ParseTrak(byte[] buf, int trakStart, int end)
    {
        TrackTables t = new();

        int mdia = FindChild(buf, AtomBody(buf, trakStart), end, "mdia");
        if (mdia < 0)
        {
            return t;
        }

        int mdiaEnd = AtomEnd(buf, mdia);
        int mdiaBody = AtomBody(buf, mdia);

        int mdhd = FindChild(buf, mdiaBody, mdiaEnd, "mdhd");
        if (mdhd >= 0)
        {
            int b = AtomBody(buf, mdhd);
            byte version = buf[b];
            t.TimeScale = version == 1
                ? BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(b + 4 + 8 + 8, 4))
                : BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(b + 4 + 4 + 4, 4));
        }

        int hdlr = FindChild(buf, mdiaBody, mdiaEnd, "hdlr");
        if (hdlr >= 0)
        {
            int b = AtomBody(buf, hdlr);
            // version/flags(4) + component type(4) + component subtype(4)
            t.Handler = Encoding.ASCII.GetString(buf, b + 8, 4);
        }

        int minf = FindChild(buf, mdiaBody, mdiaEnd, "minf");
        if (minf < 0)
        {
            return t;
        }

        int stbl = FindChild(buf, AtomBody(buf, minf), AtomEnd(buf, minf), "stbl");
        if (stbl < 0)
        {
            return t;
        }

        int stblBody = AtomBody(buf, stbl);
        int stblEnd = AtomEnd(buf, stbl);

        foreach ((string type, int atomStart, int _) in Children(buf, stblBody, stblEnd))
        {
            int b = AtomBody(buf, atomStart);
            switch (type)
            {
                case "stsd":
                    ParseStsd(buf, b, t);
                    break;
                case "stts":
                    ParseStts(buf, b, t);
                    break;
                case "stsc":
                    ParseStsc(buf, b, t);
                    break;
                case "stsz":
                    ParseStsz(buf, b, t);
                    break;
                case "stco":
                    ParseStco(buf, b, t, is64: false);
                    break;
                case "co64":
                    ParseStco(buf, b, t, is64: true);
                    break;
                default:
                    break; // stss, sdtp, etc.: irrelevantes para HAP (todo keyframes)
            }
        }

        return t;
    }

    private static void ParseStsd(byte[] buf, int b, TrackTables t)
    {
        uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(b + 4, 4));
        if (entryCount == 0)
        {
            return;
        }

        // Primera (y en la práctica única) sample description
        int e = b + 8;
        t.Fourcc = Encoding.ASCII.GetString(buf, e + 4, 4);

        if (t.Handler == "vide")
        {
            // size(4) fourcc(4) reserved(6) dataRefIdx(2) version(2) revision(2)
            // vendor(4) temporalQ(4) spatialQ(4) width(2) height(2) ...
            t.Width = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(e + 32, 2));
            t.Height = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(e + 34, 2));
        }
        else if (t.Handler == "soun")
        {
            // size(4) fourcc(4) reserved(6) dataRefIdx(2) version(2) revision(2)
            // vendor(4) channels(2) sampleSize(2) compressionId(2) packetSize(2) sampleRate(4: 16.16)
            int channels = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(e + 24, 2));
            int bits = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(e + 26, 2));
            double rate = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(e + 32, 4)) / 65536.0;
            t.AudioInfo = new AudioTrackInfo(t.Fourcc, channels, bits, rate);
        }
    }

    private static void ParseStts(byte[] buf, int b, TrackTables t)
    {
        uint count = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(b + 4, 4));
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            uint sampleCount = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(b + 8 + (i * 8), 4));
            uint delta = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(b + 12 + (i * 8), 4));
            total += (long)sampleCount * delta;
        }

        t.TotalDelta = total;
    }

    private static void ParseStsc(byte[] buf, int b, TrackTables t)
    {
        uint count = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(b + 4, 4));
        var entries = new (uint, uint)[count];
        for (int i = 0; i < count; i++)
        {
            uint firstChunk = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(b + 8 + (i * 12), 4));
            uint samplesPerChunk = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(b + 12 + (i * 12), 4));
            entries[i] = (firstChunk, samplesPerChunk);
        }

        t.SampleToChunk = entries;
    }

    private static void ParseStsz(byte[] buf, int b, TrackTables t)
    {
        t.FixedSampleSize = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(b + 4, 4));
        uint count = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(b + 8, 4));
        t.SampleCount = checked((int)count);
        if (t.FixedSampleSize == 0)
        {
            var sizes = new uint[count];
            for (int i = 0; i < count; i++)
            {
                sizes[i] = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(b + 12 + (i * 4), 4));
            }

            t.SampleSizes = sizes;
        }
    }

    private static void ParseStco(byte[] buf, int b, TrackTables t, bool is64)
    {
        uint count = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(b + 4, 4));
        var offsets = new long[count];
        for (int i = 0; i < count; i++)
        {
            offsets[i] = is64
                ? checked((long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(b + 8 + (i * 8), 8)))
                : BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(b + 8 + (i * 4), 4));
        }

        t.ChunkOffsets = offsets;
    }

    // ------------------------------------------------------------------
    //  stsz + stsc + stco → offset y tamaño absolutos de cada sample
    // ------------------------------------------------------------------

    private static List<SampleEntry> BuildSampleTable(TrackTables t)
    {
        List<SampleEntry> samples = new(t.SampleCount);
        int sampleIndex = 0;

        for (int chunk = 0; chunk < t.ChunkOffsets.Length && sampleIndex < t.SampleCount; chunk++)
        {
            long offset = t.ChunkOffsets[chunk];
            uint samplesInChunk = SamplesPerChunk(t.SampleToChunk, (uint)(chunk + 1));

            for (uint s = 0; s < samplesInChunk && sampleIndex < t.SampleCount; s++, sampleIndex++)
            {
                uint size = t.FixedSampleSize != 0 ? t.FixedSampleSize : t.SampleSizes[sampleIndex];
                samples.Add(new SampleEntry(offset, checked((int)size)));
                offset += size;
            }
        }

        if (samples.Count != t.SampleCount)
        {
            throw new InvalidDataException(
                $"Tabla de samples incompleta: stsz declara {t.SampleCount} pero stsc/stco solo cubren {samples.Count}.");
        }

        return samples;
    }

    /// <summary>Resuelve cuántos samples tiene el chunk número <paramref name="chunkNumber"/> (base 1) según stsc.</summary>
    private static uint SamplesPerChunk((uint FirstChunk, uint SamplesPerChunk)[] table, uint chunkNumber)
    {
        uint result = 1;
        foreach ((uint firstChunk, uint samplesPerChunk) in table)
        {
            if (firstChunk > chunkNumber)
            {
                break;
            }

            result = samplesPerChunk;
        }

        return result;
    }

    // ------------------------------------------------------------------
    //  Recorrido de átomos dentro de un buffer
    // ------------------------------------------------------------------

    /// <summary>Enumera los átomos hijos en el rango [start, end) del buffer. Devuelve (tipo, inicioDelÁtomo, finDelÁtomo).</summary>
    private static IEnumerable<(string Type, int Start, int End)> Children(byte[] buf, int start, int end)
    {
        int pos = start;
        while (pos + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(pos, 4));
            if (size == 1)
            {
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(pos + 8, 8)));
            }
            else if (size == 0)
            {
                size = end - pos;
            }

            if (size < 8 || pos + size > end)
            {
                throw new InvalidDataException($"Átomo con tamaño inválido ({size}) en offset relativo {pos}.");
            }

            string type = Encoding.ASCII.GetString(buf, pos + 4, 4);
            yield return (type, pos, pos + (int)size);
            pos += (int)size;
        }
    }

    /// <summary>Busca el primer átomo hijo de un tipo dado; devuelve el offset de inicio del átomo o -1.</summary>
    private static int FindChild(byte[] buf, int start, int end, string type)
    {
        foreach ((string t, int s, int _) in Children(buf, start, end))
        {
            if (t == type)
            {
                return s;
            }
        }

        return -1;
    }

    /// <summary>Offset del primer byte del payload de un átomo (asume cabecera de 8; ffmpeg no usa cabeceras extendidas dentro de moov).</summary>
    private static int AtomBody(byte[] buf, int atomStart)
    {
        long size = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(atomStart, 4));
        return size == 1 ? atomStart + 16 : atomStart + 8;
    }

    /// <summary>Offset del primer byte tras el átomo.</summary>
    private static int AtomEnd(byte[] buf, int atomStart)
    {
        long size = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(atomStart, 4));
        if (size == 1)
        {
            size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(atomStart + 8, 8)));
        }

        return atomStart + (int)size;
    }
}
