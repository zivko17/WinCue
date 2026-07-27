using FFmpeg.AutoGen;

namespace ProResLab;

/// <summary>
/// Descripción de los formatos de píxel planares que puede producir el
/// decodificador de ProRes.
/// </summary>
/// <remarks>
/// <para>
/// Es también una PUERTA DE SEGURIDAD: el laboratorio solo acepta esta lista
/// cerrada. Si el decodificador devolviera cualquier otra cosa (fichero raro,
/// versión de FFmpeg distinta, ProRes RAW), preferimos fallar en C# con un
/// mensaje claro antes que interpretar punteros con el layout equivocado.
/// </para>
/// <para>
/// Todos son little-endian con las muestras en <c>ushort</c> y los bits ÚTILES
/// alineados a la DERECHA (valor 0..1023 en 10 bits, 0..4095 en 12): es lo que
/// obliga a escalar al subir a una textura R16_UNORM.
/// </para>
/// </remarks>
public readonly record struct PixelLayout(
    AVPixelFormat Format,
    string Name,
    int BitDepth,
    int PlaneCount,
    int ChromaShiftX,
    int ChromaShiftY,
    bool HasAlpha)
{
    /// <summary>Muestras por fila del plano <paramref name="plane"/> a un ancho dado.</summary>
    public int PlaneWidth(int plane, int width) => plane is 1 or 2
        ? (width + (1 << ChromaShiftX) - 1) >> ChromaShiftX
        : width;

    /// <summary>Filas del plano <paramref name="plane"/> a un alto dado.</summary>
    public int PlaneHeight(int plane, int height) => plane is 1 or 2
        ? (height + (1 << ChromaShiftY) - 1) >> ChromaShiftY
        : height;

    /// <summary>Bytes útiles (sin padding de stride) de un frame completo.</summary>
    public long FrameBytes(int width, int height)
    {
        long total = 0;
        for (int p = 0; p < PlaneCount; p++)
        {
            total += (long)PlaneWidth(p, width) * PlaneHeight(p, height) * 2;
        }

        return total;
    }

    /// <summary>Valor máximo de una muestra (1023 a 10 bits, 4095 a 12).</summary>
    public int MaxSample => (1 << BitDepth) - 1;

    /// <summary>Lista cerrada de formatos aceptados.</summary>
    /// <exception cref="NotSupportedException">El formato no es uno de los de ProRes.</exception>
    public static PixelLayout For(AVPixelFormat format) => format switch
    {
        AVPixelFormat.AV_PIX_FMT_YUV422P10LE =>
            new(format, "yuv422p10le", 10, 3, 1, 0, false),
        AVPixelFormat.AV_PIX_FMT_YUV444P10LE =>
            new(format, "yuv444p10le", 10, 3, 0, 0, false),
        AVPixelFormat.AV_PIX_FMT_YUVA444P10LE =>
            new(format, "yuva444p10le", 10, 4, 0, 0, true),
        AVPixelFormat.AV_PIX_FMT_YUV422P12LE =>
            new(format, "yuv422p12le", 12, 3, 1, 0, false),
        AVPixelFormat.AV_PIX_FMT_YUV444P12LE =>
            new(format, "yuv444p12le", 12, 3, 0, 0, false),
        AVPixelFormat.AV_PIX_FMT_YUVA444P12LE =>
            new(format, "yuva444p12le", 12, 4, 0, 0, true),
        _ => throw new NotSupportedException(
            $"Formato de píxel inesperado para ProRes: {format}. El laboratorio solo acepta "
            + "yuv422p10le / yuv444p10le / yuva444p10le / yuv422p12le / yuv444p12le / yuva444p12le."),
    };
}
