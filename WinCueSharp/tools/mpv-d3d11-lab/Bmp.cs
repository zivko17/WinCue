// =============================================================================
// Bmp.cs — Volcado de un buffer BGRA a .bmp (sin dependencias)
// =============================================================================
// Se escribe con ALTURA NEGATIVA (BMP top-down): así la fila 0 del archivo es
// la fila 0 de la textura D3D. Sin esto, el volteo propio del BMP se sumaría
// al de OpenGL y sería imposible saber si el flip_y de mpv está bien puesto.
// =============================================================================

namespace MpvD3D11Lab;

internal static class Bmp
{
    public static void Save(string path, byte[] bgra, int w, int h)
    {
        const int headerSize = 54;
        int dataSize = w * h * 4;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        bw.Write((byte)'B'); bw.Write((byte)'M');
        bw.Write(headerSize + dataSize);
        bw.Write(0);                    // reservado
        bw.Write(headerSize);           // offset a los datos

        bw.Write(40);                   // BITMAPINFOHEADER
        bw.Write(w);
        bw.Write(-h);                   // negativo = top-down
        bw.Write((short)1);             // planos
        bw.Write((short)32);            // bpp
        bw.Write(0);                    // BI_RGB
        bw.Write(dataSize);
        bw.Write(2835); bw.Write(2835); // ~72 dpi
        bw.Write(0); bw.Write(0);

        bw.Write(bgra, 0, dataSize);
    }
}
