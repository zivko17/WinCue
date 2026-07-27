// =============================================================================
// ProResFormat.cs — Perfiles de ProRes por fourcc y layout de píxel resultante
// =============================================================================
// Promocionado de tools/prores-lab (paso 1). Dos piezas pequeñas pero con dos
// hallazgos caros detrás:
//
//  · El FOURCC no es decorativo. El decodificador de FFmpeg deduce la
//    PROFUNDIDAD DE BITS del perfil, y el perfil sale de avctx->codec_tag. Sin
//    pasárselo, un ProRes 4444 sale como yuva444p10le y SE PIERDEN 2 BITS POR
//    MUESTRA (con el tag sale yuva444p12le). Es lo único imprescindible que el
//    contenedor le aporta al decodificador. Regresión cubierta por
//    ProResDecoderTests.
//
//  · PixelLayout es una LISTA CERRADA, y eso es una puerta de seguridad además
//    de una descripción: si el decodificador devolviera cualquier otra cosa
//    (fichero raro, otra versión de FFmpeg, ProRes RAW), preferimos fallar en
//    C# con un mensaje claro antes que interpretar punteros nativos con el
//    layout equivocado.
// =============================================================================

using FFmpeg.AutoGen;

namespace WinCue.Video.ProRes;

/// <summary>Variante de ProRes según el fourcc del <c>stsd</c>.</summary>
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

/// <summary>Mapa fourcc → perfil de ProRes.</summary>
public static class ProResFormat
{
    /// <summary>Perfil asociado a un fourcc (<see cref="ProResProfile.None"/> si no es ProRes).</summary>
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

    /// <summary>¿Este fourcc del <c>stsd</c> es ProRes?</summary>
    public static bool IsProRes(string fourcc) => ProfileOf(fourcc) != ProResProfile.None;
}

/// <summary>
/// Descripción de los formatos de píxel planares que puede producir el
/// decodificador de ProRes.
/// </summary>
/// <remarks>
/// Todos son little-endian con las muestras en <c>ushort</c> y los bits ÚTILES
/// alineados a la DERECHA (0..1023 en 10 bits, 0..4095 en 12): es lo que obliga
/// a escalar al subir a una textura <c>R16_UNORM</c>.
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
            total += (long)PlaneWidth(p, width) * PlaneHeight(p, height) * 2;

        return total;
    }

    /// <summary>Valor máximo de una muestra (1023 a 10 bits, 4095 a 12).</summary>
    public int MaxSample => (1 << BitDepth) - 1;

    /// <summary>
    /// FIRMA del layout para la clave del pool de slots GPU. La spec de una
    /// textura de plano depende del formato de píxel (3 planos a media anchura
    /// en 422 vs 4 planos a anchura completa en 4444), así que dos cues con la
    /// misma superficie pero distinto perfil NO pueden compartir sesión.
    /// </summary>
    public string Signature => Name;

    /// <summary>Lista CERRADA de formatos aceptados.</summary>
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
            $"Formato de píxel inesperado para ProRes: {format}. Solo se aceptan "
            + "yuv422p10le / yuv444p10le / yuva444p10le / yuv422p12le / yuv444p12le / yuva444p12le."),
    };
}
