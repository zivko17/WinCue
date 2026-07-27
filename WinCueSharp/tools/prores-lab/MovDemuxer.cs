using System.Buffers.Binary;
using System.Text;

namespace ProResLab;

/// <summary>Posición y tamaño de un sample (frame de vídeo) dentro del fichero .mov.</summary>
public readonly record struct SampleEntry(long Offset, int Size);

/// <summary>Información mínima de una pista de audio (solo se enumera, no se decodifica).</summary>
public sealed class AudioTrackInfo(string fourcc, int channels, int sampleSizeBits, double sampleRate)
{
    public string Fourcc { get; } = fourcc;

    public int Channels { get; } = channels;

    public int SampleSizeBits { get; } = sampleSizeBits;

    public double SampleRate { get; } = sampleRate;
}

/// <summary>
/// Variante de ProRes según el fourcc del <c>stsd</c>.
/// </summary>
/// <remarks>
/// El fourcc solo dice el PERFIL; el formato de píxel real lo decide el
/// bitstream y lo resuelve el decodificador (ver <see cref="ProResDecoder"/>).
/// Aquí sirve para (a) rechazar pronto lo que no es ProRes y (b) etiquetar
/// las medidas del benchmark.
/// </remarks>
public enum ProResProfile
{
    /// <summary>No es ProRes.</summary>
    None,

    /// <summary><c>apco</c> — ProRes 422 Proxy.</summary>
    Proxy,

    /// <summary><c>apcs</c> — ProRes 422 LT.</summary>
    Lt,

    /// <summary><c>apcn</c> — ProRes 422 (estándar).</summary>
    Standard422,

    /// <summary><c>apch</c> — ProRes 422 HQ.</summary>
    Hq422,

    /// <summary><c>ap4h</c> — ProRes 4444 (con alfa opcional).</summary>
    P4444,

    /// <summary><c>ap4x</c> — ProRes 4444 XQ.</summary>
    P4444Xq,
}

/// <summary>
/// Señalización de color leída del átomo <c>colr</c> del <c>stsd</c>.
/// </summary>
/// <remarks>
/// Es lo que decide la matriz del shader (BT.709 vs BT.2020) y el rango
/// (limitado vs completo). Los códigos son los de ISO/IEC 23001-8, los
/// mismos que usa <c>AVColorSpace</c>/<c>AVColorPrimaries</c> de FFmpeg.
/// </remarks>
public readonly record struct ColrInfo(int Primaries, int Transfer, int Matrix, bool FullRange)
{
    /// <summary>Valor cuando el fichero no trae <c>colr</c> (todo "unspecified").</summary>
    public static ColrInfo Unspecified => new(2, 2, 2, false);

    /// <summary>¿Trae información utilizable de matriz?</summary>
    public bool HasMatrix => Matrix is not (0 or 2);
}

/// <summary>Resultado de demuxar un .mov: pista de vídeo + tabla de samples.</summary>
public sealed class MovInfo
{
    internal MovInfo(
        string videoFourcc,
        int width,
        int height,
        double fps,
        double durationSeconds,
        IReadOnlyList<SampleEntry> samples,
        AudioTrackInfo? audio,
        ColrInfo colr,
        int depth)
    {
        VideoFourcc = videoFourcc;
        Width = width;
        Height = height;
        Fps = fps;
        DurationSeconds = durationSeconds;
        Samples = samples;
        Audio = audio;
        Colr = colr;
        Depth = depth;
    }

    /// <summary>Fourcc del códec de vídeo tal cual aparece en el <c>stsd</c>.</summary>
    public string VideoFourcc { get; }

    /// <summary>Perfil de ProRes, o <see cref="ProResProfile.None"/> si no lo es.</summary>
    public ProResProfile Profile => MovDemuxer.ProfileOf(VideoFourcc);

    public int Width { get; }

    public int Height { get; }

    /// <summary>Frames por segundo medios (timescale * frames / duración en unidades).</summary>
    public double Fps { get; }

    public double DurationSeconds { get; }

    /// <summary>Un elemento por frame de vídeo, en orden de decodificación.</summary>
    public IReadOnlyList<SampleEntry> Samples { get; }

    public int FrameCount => Samples.Count;

    /// <summary>Pista de audio si existe; el laboratorio la enumera pero no la decodifica.</summary>
    public AudioTrackInfo? Audio { get; }

    /// <summary>Señalización de color del <c>colr</c> (o "unspecified" si falta).</summary>
    public ColrInfo Colr { get; }

    /// <summary>Campo <c>depth</c> del sample description (24 = opaco, 32 = con alfa en ProRes 4444).</summary>
    public int Depth { get; }

    /// <summary>Tamaño del sample más grande: cota superior para dimensionar los búferes de lectura.</summary>
    public int MaxSampleSize
    {
        get
        {
            int max = 0;
            foreach (SampleEntry s in Samples)
            {
                if (s.Size > max)
                {
                    max = s.Size;
                }
            }

            return max;
        }
    }
}

/// <summary>
/// Demuxer mínimo de contenedor QuickTime/MOV, SIN libavformat.
/// </summary>
/// <remarks>
/// <para>
/// Es el mismo demuxer que el laboratorio de HAP (<c>tools\hap-lab</c>), con
/// tres añadidos que ProRes sí necesita y HAP no: reconocimiento de los
/// fourcc <c>apco/apcs/apcn/apch/ap4h/ap4x</c>, lectura del átomo <c>colr</c>
/// (matriz y rango de color, que deciden el shader) y del campo <c>depth</c>
/// (delata el alfa de ProRes 4444).
/// </para>
/// <para>
/// La DECISIÓN de reutilizar este demuxer en vez de libavformat está razonada
/// en <c>INFORME.md</c>: mantiene una sola tubería con HAP, no añade
/// superficie nativa (avformat es la parte del stack de FFmpeg que más
/// contacto tiene con datos hostiles) y deja el .mxf explícitamente fuera.
/// </para>
/// <para>
/// Valida lo suficiente para no pasarle basura al decodificador nativo:
/// tamaños de átomo coherentes, dimensiones dentro de límites y offsets de
/// sample dentro del fichero (<see cref="Validate"/>).
/// </para>
/// </remarks>
public static class MovDemuxer
{
    /// <summary>Cota superior de dimensiones aceptadas (8K con holgura).</summary>
    public const int MaxDimension = 16384;

    /// <summary>Cota superior del tamaño de un frame comprimido (256 MB).</summary>
    public const int MaxSampleBytes = 256 * 1024 * 1024;

    /// <summary>Perfil de ProRes asociado a un fourcc (<see cref="ProResProfile.None"/> si no lo es).</summary>
    public static ProResProfile ProfileOf(string fourcc) => fourcc switch
    {
        "apco" => ProResProfile.Proxy,
        "apcs" => ProResProfile.Lt,
        "apcn" => ProResProfile.Standard422,
        "apch" => ProResProfile.Hq422,
        "ap4h" => ProResProfile.P4444,
        "ap4x" => ProResProfile.P4444Xq,
        _ => ProResProfile.None,
    };

    /// <summary>Abre y demuxa un fichero .mov.</summary>
    /// <exception cref="InvalidDataException">El fichero no es un MOV válido o no tiene pista de vídeo.</exception>
    public static MovInfo Open(string path)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] moov = ReadMoovAtom(fs) ?? throw new InvalidDataException("No se encontró el átomo 'moov' en el fichero.");
        MovInfo info = ParseMoov(moov);
        Validate(info, fs.Length);
        return info;
    }

    /// <summary>
    /// Comprobaciones de cordura ANTES de alimentar al decodificador nativo.
    /// </summary>
    /// <remarks>
    /// Un decodificador en proceso se lleva la aplicación entera por delante si
    /// se le da algo imposible. Esta es la primera línea: dimensiones acotadas,
    /// frames con tamaño positivo y acotado, y offsets que caben en el fichero.
    /// </remarks>
    /// <exception cref="InvalidDataException">Alguna cota no se cumple.</exception>
    public static void Validate(MovInfo info, long fileLength)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (info.Width is <= 0 or > MaxDimension || info.Height is <= 0 or > MaxDimension)
        {
            throw new InvalidDataException($"Dimensiones fuera de rango: {info.Width}x{info.Height}.");
        }

        for (int i = 0; i < info.Samples.Count; i++)
        {
            SampleEntry s = info.Samples[i];
            if (s.Size <= 0 || s.Size > MaxSampleBytes)
            {
                throw new InvalidDataException($"Sample {i} con tamaño imposible ({s.Size} bytes).");
            }

            if (s.Offset < 0 || s.Offset + s.Size > fileLength)
            {
                throw new InvalidDataException(
                    $"Sample {i} se sale del fichero (offset {s.Offset}, tamaño {s.Size}, fichero {fileLength}).");
            }
        }
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
            fs.ReadExactly(header[..8]);
            long atomSize = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            string type = Encoding.ASCII.GetString(header.Slice(4, 4));
            long headerSize = 8;

            if (atomSize == 1)
            {
                fs.ReadExactly(header.Slice(8, 8));
                atomSize = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8)));
                headerSize = 16;
            }
            else if (atomSize == 0)
            {
                atomSize = fileLength - pos;
            }

            if (atomSize < headerSize)
            {
                throw new InvalidDataException($"Átomo '{type}' con tamaño inválido ({atomSize}) en offset {pos}.");
            }

            if (type == "moov")
            {
                long payload = atomSize - headerSize;
                if (payload is > int.MaxValue or <= 0)
                {
                    throw new InvalidDataException("Átomo 'moov' con tamaño imposible.");
                }

                byte[] buffer = new byte[payload];
                fs.ReadExactly(buffer);
                return buffer;
            }

            pos += atomSize;
        }

        return null;
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

        return new MovInfo(
            video.Fourcc, video.Width, video.Height, fps, durationSeconds, samples, audio, video.Colr, video.Depth);
    }

    /// <summary>Tablas crudas de un trak, tal cual salen del stbl.</summary>
    private sealed class TrackTables
    {
        public string Handler = "";
        public string Fourcc = "";
        public int Width;
        public int Height;
        public int Depth;
        public ColrInfo Colr = ColrInfo.Unspecified;
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

        foreach ((string type, int atomStart, int atomEnd) in Children(buf, stblBody, stblEnd))
        {
            int b = AtomBody(buf, atomStart);
            switch (type)
            {
                case "stsd":
                    ParseStsd(buf, b, atomEnd, t);
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
                    break; // stss, sdtp, etc.: irrelevantes (ProRes es todo intra)
            }
        }

        return t;
    }

    private static void ParseStsd(byte[] buf, int b, int stsdEnd, TrackTables t)
    {
        uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(b + 4, 4));
        if (entryCount == 0)
        {
            return;
        }

        // Primera (y en la práctica única) sample description
        int e = b + 8;
        long entrySize = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(e, 4));
        t.Fourcc = Encoding.ASCII.GetString(buf, e + 4, 4);

        if (t.Handler == "vide")
        {
            // Cabecera fija de 86 bytes de VisualSampleEntry (QuickTime):
            // size(4) fourcc(4) reserved(6) dri(2) version(2) revision(2)
            // vendor(4) temporalQ(4) spatialQ(4) width(2) height(2)
            // hres(4) vres(4) dataSize(4) frameCount(2) compressorName(32)
            // depth(2) colorTableID(2)  → las extensiones empiezan en +86
            t.Width = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(e + 32, 2));
            t.Height = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(e + 34, 2));
            t.Depth = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(e + 82, 2));

            int extStart = e + 86;
            int extEnd = (int)Math.Min(stsdEnd, e + entrySize);
            if (extStart < extEnd)
            {
                ParseVideoExtensions(buf, extStart, extEnd, t);
            }
        }
        else if (t.Handler == "soun")
        {
            int channels = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(e + 24, 2));
            int bits = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(e + 26, 2));
            double rate = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(e + 32, 4)) / 65536.0;
            t.AudioInfo = new AudioTrackInfo(t.Fourcc, channels, bits, rate);
        }
    }

    /// <summary>Extensiones del sample description de vídeo; solo interesa <c>colr</c>.</summary>
    private static void ParseVideoExtensions(byte[] buf, int start, int end, TrackTables t)
    {
        foreach ((string type, int atomStart, int atomEnd) in Children(buf, start, end))
        {
            if (type != "colr")
            {
                continue;
            }

            int b = AtomBody(buf, atomStart);
            if (b + 4 > atomEnd)
            {
                continue;
            }

            string kind = Encoding.ASCII.GetString(buf, b, 4);
            if (kind is not ("nclc" or "nclx"))
            {
                continue; // 'prof' (perfil ICC) queda fuera del laboratorio
            }

            if (b + 10 > atomEnd)
            {
                continue;
            }

            int primaries = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(b + 4, 2));
            int transfer = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(b + 6, 2));
            int matrix = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(b + 8, 2));

            // 'nclx' añade un byte con el bit de rango completo en el bit alto
            bool full = kind == "nclx" && b + 11 <= atomEnd && (buf[b + 10] & 0x80) != 0;

            t.Colr = new ColrInfo(primaries, transfer, matrix, full);
            return;
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

    /// <summary>Resuelve cuántos samples tiene el chunk <paramref name="chunkNumber"/> (base 1) según stsc.</summary>
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

    /// <summary>Enumera los átomos hijos en [start, end). Devuelve (tipo, inicio, fin).</summary>
    internal static IEnumerable<(string Type, int Start, int End)> Children(byte[] buf, int start, int end)
    {
        int pos = start;
        while (pos + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(pos, 4));
            if (size == 1)
            {
                if (pos + 16 > end)
                {
                    throw new InvalidDataException($"Átomo con cabecera extendida truncada en offset relativo {pos}.");
                }

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

    /// <summary>Busca el primer átomo hijo de un tipo dado; devuelve su offset de inicio o -1.</summary>
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

    /// <summary>Offset del primer byte del payload de un átomo.</summary>
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
