using System.Runtime.InteropServices;
using System.Text;
using FFmpeg.AutoGen;

namespace ProResLab;

/// <summary>Reparto de trabajo entre hilos del decodificador.</summary>
public enum ThreadMode
{
    /// <summary>Un solo hilo (referencia del benchmark).</summary>
    SingleThread,

    /// <summary>Por SLICES dentro del frame: baja la latencia del frame individual.</summary>
    Slice,

    /// <summary>Por FRAMES: más throughput, pero introduce retardo de salida.</summary>
    Frame,

    /// <summary>Slice + frame (lo que FFmpeg elige por defecto si puede).</summary>
    SliceAndFrame,
}

/// <summary>
/// Decodificador de ProRes: FFmpeg EN PROCESO (enlace dinámico LGPL).
/// </summary>
/// <remarks>
/// <para>
/// ProRes es DCT con codificación entrópica: no hay atajo tipo HAP (donde el
/// "decode" es descomprimir Snappy y darle el DXT a la GPU). Escribir el
/// decodificador desde cero está descartado; el de FFmpeg lleva años
/// optimizado en SIMD. Lo que sigue siendo NUESTRO es el motor alrededor:
/// demuxer, ring de frames, reloj, pacer y presentación GPU.
/// </para>
/// <para>
/// La salida se deja en su formato NATIVO (planar YUV 10/12 bits). NO se
/// convierte a RGB en CPU: eso es trabajo de la GPU
/// (<c>ProResLab.View</c>), y hacerlo aquí tiraría por tierra el
/// presupuesto de directo.
/// </para>
/// <para>
/// Contención: el objeto es de UN solo hilo (el contexto de avcodec no es
/// reentrante). Los errores del decodificador se traducen a excepciones de
/// C# con el texto de <c>av_strerror</c>; lo que NO se puede contener desde
/// aquí es un fallo de memoria dentro del código nativo — de ahí la
/// validación previa del demuxer y el interruptor de apagado del paso 2.
/// </para>
/// </remarks>
public sealed unsafe class ProResDecoder : IDisposable
{
    /// <summary>Valor de <c>AVERROR(EAGAIN)</c>: el decodificador aún no tiene salida.</summary>
    /// <remarks>FFmpeg define AVERROR(e) = -e, y EAGAIN vale 11 tanto en MSVC como en mingw.</remarks>
    public const int AverrorEagain = -11;

    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private bool _frameHeld;

    /// <summary>Abre un contexto del decodificador <c>prores</c>.</summary>
    /// <param name="width">Ancho declarado por el contenedor (pista, no obligatorio pero ayuda).</param>
    /// <param name="height">Alto declarado por el contenedor.</param>
    /// <param name="threadCount">Hilos (0 = que FFmpeg decida por núcleos).</param>
    /// <param name="mode">Reparto del trabajo entre hilos.</param>
    /// <param name="fourcc">
    /// Fourcc del <c>stsd</c> (<c>apcn</c>, <c>apch</c>, <c>ap4h</c>…). Se pasa
    /// como <c>codec_tag</c>: sin él el decodificador avisa
    /// «Unknown prores profile 0» y no rellena <c>avctx-&gt;profile</c>. Es la
    /// única pieza que el decodificador necesita del CONTENEDOR.
    /// </param>
    public ProResDecoder(
        int width, int height, int threadCount = 1, ThreadMode mode = ThreadMode.SingleThread, string? fourcc = null)
    {
        FFmpegLoader.Load();

        if (width is <= 0 or > MovDemuxer.MaxDimension || height is <= 0 or > MovDemuxer.MaxDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"Dimensiones fuera de rango: {width}x{height}.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(threadCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(threadCount, 256);

        AVCodec* codec = ffmpeg.avcodec_find_decoder_by_name("prores");
        if (codec is null)
        {
            throw new NotSupportedException(
                "La build de FFmpeg cargada no trae el decodificador 'prores'.");
        }

        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        if (_ctx is null)
        {
            throw new InvalidOperationException("avcodec_alloc_context3 devolvió null (sin memoria nativa).");
        }

        _ctx->width = width;
        _ctx->height = height;
        if (!string.IsNullOrEmpty(fourcc))
        {
            _ctx->codec_tag = Mktag(fourcc);
        }

        _ctx->thread_count = mode == ThreadMode.SingleThread ? 1 : threadCount;
        _ctx->thread_type = mode switch
        {
            ThreadMode.SingleThread => 0,
            ThreadMode.Slice => ffmpeg.FF_THREAD_SLICE,
            ThreadMode.Frame => ffmpeg.FF_THREAD_FRAME,
            _ => ffmpeg.FF_THREAD_SLICE | ffmpeg.FF_THREAD_FRAME,
        };

        Check(ffmpeg.avcodec_open2(_ctx, codec, null), "avcodec_open2");

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
        if (_frame is null || _packet is null)
        {
            Dispose();
            throw new InvalidOperationException("No se pudo reservar AVFrame/AVPacket (sin memoria nativa).");
        }

        RequestedMode = mode;
        ThreadCount = _ctx->thread_count;
        ActiveThreadType = _ctx->thread_type;
    }

    /// <summary>Empaqueta un fourcc ASCII en el entero little-endian que usa FFmpeg (<c>MKTAG</c>).</summary>
    public static uint Mktag(string fourcc)
    {
        ArgumentNullException.ThrowIfNull(fourcc);
        if (fourcc.Length != 4)
        {
            throw new ArgumentException("Un fourcc son exactamente 4 caracteres.", nameof(fourcc));
        }

        return (uint)fourcc[0] | ((uint)fourcc[1] << 8) | ((uint)fourcc[2] << 16) | ((uint)fourcc[3] << 24);
    }

    /// <summary>Perfil que el decodificador dedujo del <c>codec_tag</c>/bitstream.</summary>
    public int DecoderProfile => _ctx is not null ? _ctx->profile : 0;

    /// <summary>Bits por muestra que declara el bitstream (10 o 12 en ProRes).</summary>
    public int BitsPerRawSample => _ctx is not null ? _ctx->bits_per_raw_sample : 0;

    /// <summary>Modo de hilos pedido.</summary>
    public ThreadMode RequestedMode { get; }

    /// <summary>Hilos que el contexto declara usar (tras <c>avcodec_open2</c>).</summary>
    public int ThreadCount { get; }

    /// <summary>Máscara <c>FF_THREAD_*</c> efectiva.</summary>
    public int ActiveThreadType { get; }

    /// <summary>Ancho del último frame recibido.</summary>
    public int FrameWidth => _frameHeld ? _frame->width : 0;

    /// <summary>Alto del último frame recibido.</summary>
    public int FrameHeight => _frameHeld ? _frame->height : 0;

    /// <summary>Formato de píxel del último frame recibido.</summary>
    public AVPixelFormat PixelFormat => _frameHeld ? (AVPixelFormat)_frame->format : AVPixelFormat.AV_PIX_FMT_NONE;

    /// <summary>Layout (subsampling, profundidad, planos) del último frame.</summary>
    public PixelLayout Layout => PixelLayout.For(PixelFormat);

    /// <summary>Espacio de color que declara el bitstream del último frame.</summary>
    public AVColorSpace ColorSpace => _frameHeld ? _frame->colorspace : AVColorSpace.AVCOL_SPC_UNSPECIFIED;

    /// <summary>Rango (limitado/completo) que declara el bitstream del último frame.</summary>
    public AVColorRange ColorRange => _frameHeld ? _frame->color_range : AVColorRange.AVCOL_RANGE_UNSPECIFIED;

    /// <summary>¿Hay un frame válido retenido ahora mismo?</summary>
    public bool HasFrame => _frameHeld;

    /// <summary>Puntero al plano <paramref name="plane"/> del frame retenido.</summary>
    public nint PlanePointer(int plane)
    {
        EnsureFrame();
        ArgumentOutOfRangeException.ThrowIfNegative(plane);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(plane, 4);
        return (nint)_frame->data[(uint)plane];
    }

    /// <summary>Bytes por fila (con padding) del plano <paramref name="plane"/>.</summary>
    public int PlaneStride(int plane)
    {
        EnsureFrame();
        ArgumentOutOfRangeException.ThrowIfNegative(plane);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(plane, 4);
        return _frame->linesize[(uint)plane];
    }

    /// <summary>
    /// Envía un paquete comprimido. El buffer DEBE llevar
    /// <see cref="ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE"/> bytes a cero al final
    /// (usa <see cref="PaddedBuffer"/>): es requisito de la API de FFmpeg y
    /// saltárselo es lectura fuera de rango dentro del código nativo.
    /// </summary>
    /// <param name="packet">Vista del frame dentro de memoria nativa con padding.</param>
    public void SendPacket(PacketRef packet)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(packet.Size);
        ObjectDisposedException.ThrowIf(_ctx is null, this);

        _packet->data = (byte*)packet.Data;
        _packet->size = packet.Size;
        int rc = ffmpeg.avcodec_send_packet(_ctx, _packet);
        _packet->data = null;
        _packet->size = 0;
        Check(rc, "avcodec_send_packet");
    }

    /// <summary>Cierra la entrada (paquete nulo) para drenar el retardo del modo FRAME.</summary>
    /// <remarks>
    /// TRAMPA de la API: tras el paquete nulo el contexto queda en modo
    /// DRENAJE para siempre; el siguiente <see cref="SendPacket"/> devuelve
    /// <c>AVERROR_EOF</c>. Para volver a usarlo hay que llamar a
    /// <see cref="Reset"/>. En producción esto es exactamente lo que pasa al
    /// terminar una pasada de LOOP o al hacer un seek.
    /// </remarks>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_ctx is null, this);
        _ = ffmpeg.avcodec_send_packet(_ctx, null);
    }

    /// <summary>
    /// Vacía el estado interno y saca al contexto del modo drenaje: es lo que
    /// hay que hacer tras un <see cref="Flush"/>, un seek o una pasada de loop.
    /// </summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_ctx is null, this);
        ReleaseFrame();
        ffmpeg.avcodec_flush_buffers(_ctx);
    }

    /// <summary>
    /// Intenta recoger un frame. Devuelve false con EAGAIN/EOF (normal en modo
    /// FRAME, donde la salida va con retardo de <c>thread_count-1</c> paquetes).
    /// El frame retenido vale hasta la siguiente llamada.
    /// </summary>
    public bool TryReceiveFrame()
    {
        ObjectDisposedException.ThrowIf(_ctx is null, this);

        if (_frameHeld)
        {
            ffmpeg.av_frame_unref(_frame);
            _frameHeld = false;
        }

        int rc = ffmpeg.avcodec_receive_frame(_ctx, _frame);
        if (rc == AverrorEagain || rc == ffmpeg.AVERROR_EOF)
        {
            return false;
        }

        Check(rc, "avcodec_receive_frame");
        _frameHeld = true;
        return true;
    }

    /// <summary>Suelta el frame retenido (devuelve el buffer al pool de FFmpeg).</summary>
    public void ReleaseFrame()
    {
        if (_frameHeld)
        {
            ffmpeg.av_frame_unref(_frame);
            _frameHeld = false;
        }
    }

    /// <summary>Copia el frame retenido a planos gestionados, SIN padding de stride.</summary>
    /// <remarks>Fuera del camino caliente: solo para tests y verificación de imagen.</remarks>
    public ushort[][] CopyPlanes()
    {
        EnsureFrame();
        PixelLayout layout = Layout;
        int w = FrameWidth;
        int h = FrameHeight;
        ushort[][] planes = new ushort[layout.PlaneCount][];

        for (int p = 0; p < layout.PlaneCount; p++)
        {
            int pw = layout.PlaneWidth(p, w);
            int ph = layout.PlaneHeight(p, h);
            ushort[] dst = new ushort[pw * ph];
            byte* src = _frame->data[(uint)p];
            int stride = _frame->linesize[(uint)p];
            for (int y = 0; y < ph; y++)
            {
                new ReadOnlySpan<ushort>(src + ((long)y * stride), pw).CopyTo(dst.AsSpan(y * pw, pw));
            }

            planes[p] = dst;
        }

        return planes;
    }

    private void EnsureFrame()
    {
        if (!_frameHeld)
        {
            throw new InvalidOperationException("No hay ningún frame retenido (llama antes a TryReceiveFrame).");
        }
    }

    /// <summary>Traduce un código de error de FFmpeg a texto legible.</summary>
    public static string Describe(int error)
    {
        const int size = 256;
        byte* buffer = stackalloc byte[size];
        if (ffmpeg.av_strerror(error, buffer, size) < 0)
        {
            return $"error {error} (sin descripción)";
        }

        return $"{Marshal.PtrToStringAnsi((nint)buffer)} ({error})";
    }

    private static void Check(int rc, string what)
    {
        if (rc < 0)
        {
            throw new InvalidDataException($"{what} falló: {Describe(rc)}");
        }
    }

    public void Dispose()
    {
        if (_frame is not null)
        {
            AVFrame* f = _frame;
            ffmpeg.av_frame_free(&f);
            _frame = null;
        }

        if (_packet is not null)
        {
            AVPacket* p = _packet;
            ffmpeg.av_packet_free(&p);
            _packet = null;
        }

        if (_ctx is not null)
        {
            AVCodecContext* c = _ctx;
            ffmpeg.avcodec_free_context(&c);
            _ctx = null;
        }

        _frameHeld = false;
    }
}

/// <summary>
/// Buffer nativo con el relleno que exige la API de decodificación de FFmpeg.
/// </summary>
/// <remarks>
/// <c>avcodec_send_packet</c> requiere
/// <c>AV_INPUT_BUFFER_PADDING_SIZE</c> bytes A CERO detrás de los datos: los
/// lectores de bits de FFmpeg leen de más por diseño. Con memoria gestionada
/// esto es un accidente esperando a pasar, así que el laboratorio reserva
/// memoria NATIVA (<c>av_malloc</c>, que además alinea para SIMD) y la
/// reutiliza para todos los frames — cero allocaciones en régimen.
/// </remarks>
public sealed unsafe class PaddedBuffer : IDisposable
{
    private byte* _ptr;

    /// <summary>Reserva un buffer para frames de hasta <paramref name="capacity"/> bytes.</summary>
    public PaddedBuffer(int capacity)
    {
        FFmpegLoader.Load();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, MovDemuxer.MaxSampleBytes);

        Capacity = capacity;
        int total = capacity + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE;
        _ptr = (byte*)ffmpeg.av_malloc((ulong)total);
        if (_ptr is null)
        {
            throw new InvalidOperationException($"av_malloc({total}) devolvió null (sin memoria nativa).");
        }

        // El padding SIEMPRE a cero (los datos se sobrescriben en cada frame)
        new Span<byte>(_ptr, total).Clear();
    }

    /// <summary>Bytes útiles (sin contar el padding).</summary>
    public int Capacity { get; }

    /// <summary>Vista escribible de los <see cref="Capacity"/> bytes útiles.</summary>
    public Span<byte> Span => new(_ptr, Capacity);

    /// <summary>Vista de paquete de los primeros <paramref name="length"/> bytes.</summary>
    public PacketRef AsPacket(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, Capacity);
        return new PacketRef((nint)_ptr, length);
    }

    /// <summary>Lee <paramref name="entry"/> del stream directamente al buffer.</summary>
    public int ReadSample(Stream stream, SampleEntry entry)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (entry.Size > Capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(entry),
                $"El sample ({entry.Size} B) no cabe en el buffer ({Capacity} B).");
        }

        stream.Position = entry.Offset;
        stream.ReadExactly(new Span<byte>(_ptr, entry.Size));
        return entry.Size;
    }

    public void Dispose()
    {
        if (_ptr is not null)
        {
            void* p = _ptr;
            ffmpeg.av_free(p);
            _ptr = null;
        }
    }
}

/// <summary>Vista de un frame comprimido en memoria nativa CON su padding detrás.</summary>
/// <param name="Data">Puntero al primer byte útil.</param>
/// <param name="Size">Bytes útiles (el padding no cuenta).</param>
/// <remarks>
/// No es dueña de la memoria: vale mientras viva el <see cref="PaddedBuffer"/>
/// o el <see cref="NativeFrameStore"/> que la aloja.
/// </remarks>
public readonly record struct PacketRef(nint Data, int Size);

/// <summary>
/// Almacén de N frames comprimidos en UN SOLO bloque nativo, cada uno seguido
/// del padding que exige FFmpeg.
/// </summary>
/// <remarks>
/// Una allocación en vez de una por frame: menos fragmentación, mejor
/// localidad y —lo importante para el benchmark— cero ruido de memoria dentro
/// de la medición.
/// </remarks>
public sealed unsafe class NativeFrameStore : IDisposable
{
    private readonly List<(nint Offset, int Size)> _entries = [];
    private byte* _block;

    /// <summary>Reserva sitio para <paramref name="totalBytes"/> útiles repartidos en <paramref name="frames"/> frames.</summary>
    public NativeFrameStore(long totalBytes, int frames)
    {
        FFmpegLoader.Load();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);

        long total = totalBytes + ((long)frames * ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
        _block = (byte*)ffmpeg.av_malloc((ulong)total);
        if (_block is null)
        {
            throw new InvalidOperationException($"av_malloc({total}) devolvió null (sin memoria nativa).");
        }

        new Span<byte>(_block, checked((int)Math.Min(total, int.MaxValue))).Clear();
        CapacityBytes = total;
    }

    /// <summary>Bytes reservados en total (útiles + padding).</summary>
    public long CapacityBytes { get; }

    /// <summary>Frames almacenados.</summary>
    public int Count => _entries.Count;

    private long _used;

    /// <summary>Lee un sample del stream y lo añade al almacén.</summary>
    public void Append(Stream stream, SampleEntry entry)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ObjectDisposedException.ThrowIf(_block is null, this);

        long need = entry.Size + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE;
        if (_used + need > CapacityBytes)
        {
            throw new InvalidOperationException("El almacén nativo se ha quedado corto.");
        }

        stream.Position = entry.Offset;
        stream.ReadExactly(new Span<byte>(_block + _used, entry.Size));
        _entries.Add(((nint)_used, entry.Size));
        _used += need;
    }

    /// <summary>Vista del frame <paramref name="index"/>.</summary>
    public PacketRef this[int index]
    {
        get
        {
            ObjectDisposedException.ThrowIf(_block is null, this);
            (nint offset, int size) = _entries[index];
            return new PacketRef((nint)(_block + offset), size);
        }
    }

    public void Dispose()
    {
        if (_block is not null)
        {
            void* p = _block;
            ffmpeg.av_free(p);
            _block = null;
        }

        _entries.Clear();
    }
}

/// <summary>Utilidades de texto para el informe del laboratorio.</summary>
internal static class FourccText
{
    public static string Ascii(uint tag)
    {
        Span<byte> b = [(byte)tag, (byte)(tag >> 8), (byte)(tag >> 16), (byte)(tag >> 24)];
        return Encoding.ASCII.GetString(b);
    }
}
