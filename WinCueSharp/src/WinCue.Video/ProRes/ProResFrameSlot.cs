// =============================================================================
// ProResFrameSlot.cs — Un frame decodificado RETENIDO, sin copiarlo
// =============================================================================
// El ring del motor HAP guarda arrays gestionados con el DXT: allí el decode
// escribe directamente en el slot y no hay más. Con ProRes el frame sale en
// memoria de FFmpeg (AVFrame con planos alineados), y el decodificador solo
// puede retener UNO a la vez — el siguiente TryReceiveFrame lo desreferencia.
//
// Copiar los planos a un buffer nuestro costaría un memcpy de frame COMPLETO
// por fotograma: 63 MB en 4K 4444, entre 5 y 10 ms, justo en el hilo que menos
// margen tiene. En su lugar cada slot del ring posee su propio AVFrame y el
// decodificador le TRASPASA la referencia con av_frame_move_ref: coste cero,
// el contador de referencias de FFmpeg mantiene vivos los planos y el
// decodificador queda libre para el frame siguiente.
//
// CONTRAPARTIDA, y hay que decirla: cada slot ocupado retiene un frame entero
// de FFmpeg. UN FRAME 4K 4444 SON 63 MB, así que un slot que no se libera no es
// una fuga discreta, es una fuga que se ve en el administrador de tareas. Por
// eso Release() es explícito, Dispose() libera de verdad y el soak lleva
// detector de fuga.
// =============================================================================

using FFmpeg.AutoGen;

namespace WinCue.Video.ProRes;

public sealed unsafe class ProResFrameSlot : IDisposable
{
    private AVFrame* _frame;

    public ProResFrameSlot()
    {
        FFmpegLoader.Load();
        _frame = ffmpeg.av_frame_alloc();
        if (_frame is null)
            throw new InvalidOperationException("av_frame_alloc devolvió null (sin memoria nativa).");
    }

    /// <summary>¿Hay un frame retenido en este slot?</summary>
    public bool HasFrame { get; private set; }

    /// <summary>Ancho del frame retenido.</summary>
    public int Width => HasFrame ? _frame->width : 0;

    /// <summary>Alto del frame retenido.</summary>
    public int Height => HasFrame ? _frame->height : 0;

    /// <summary>Formato de píxel del frame retenido.</summary>
    public AVPixelFormat PixelFormat =>
        HasFrame ? (AVPixelFormat)_frame->format : AVPixelFormat.AV_PIX_FMT_NONE;

    /// <summary>Espacio de color que declara el bitstream de este frame.</summary>
    public AVColorSpace ColorSpace =>
        HasFrame ? _frame->colorspace : AVColorSpace.AVCOL_SPC_UNSPECIFIED;

    /// <summary>Rango de color que declara el bitstream de este frame.</summary>
    public AVColorRange ColorRange =>
        HasFrame ? _frame->color_range : AVColorRange.AVCOL_RANGE_UNSPECIFIED;

    /// <summary>Puntero al plano indicado (válido hasta <see cref="Release"/>).</summary>
    public nint PlanePointer(int plane)
    {
        EnsureFrame();
        ArgumentOutOfRangeException.ThrowIfNegative(plane);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(plane, 4);
        return (nint)_frame->data[(uint)plane];
    }

    /// <summary>Bytes por fila (con padding de alineación) del plano indicado.</summary>
    public int PlaneStride(int plane)
    {
        EnsureFrame();
        ArgumentOutOfRangeException.ThrowIfNegative(plane);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(plane, 4);
        return _frame->linesize[(uint)plane];
    }

    /// <summary>Suelta el frame retenido y devuelve sus búferes al pool de FFmpeg.</summary>
    public void Release()
    {
        if (HasFrame && _frame is not null)
        {
            ffmpeg.av_frame_unref(_frame);
            HasFrame = false;
        }
    }

    internal AVFrame* Raw => _frame;

    internal void MarkFilled() => HasFrame = true;

    private void EnsureFrame()
    {
        if (!HasFrame)
            throw new InvalidOperationException("El slot no tiene ningún frame retenido.");
    }

    public void Dispose()
    {
        if (_frame is not null)
        {
            AVFrame* f = _frame;
            ffmpeg.av_frame_free(&f);   // libera el frame Y desreferencia sus búferes
            _frame = null;
        }

        HasFrame = false;
    }
}
