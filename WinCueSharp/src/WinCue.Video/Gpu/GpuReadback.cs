// =============================================================================
// GpuReadback.cs — Lectura de vuelta GPU → CPU (semilla de los sinks de F4)
// =============================================================================
// Hoy lo usan las pruebas para verificar por píxel que un fotograma llegó de
// verdad a la textura (lo mismo que hace tools/mpv-d3d11-lab). Es también la
// pieza base del ReadbackSink de F4 — con una diferencia que hay que respetar
// cuando llegue: esta versión es SÍNCRONA (Map bloquea hasta que la GPU
// termina) y por eso solo vale para pruebas. El sink real debe copiar a
// staging, poner una fence y mapear N fotogramas después; leer síncrono desde
// el hilo de render lo ahoga.
// =============================================================================

using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace WinCue.Video.Gpu;

internal static class GpuReadback
{
    /// <summary>Copia la textura a una staging y devuelve sus bytes compactados
    /// (sin row pitch). SÍNCRONO: no usar desde el hilo de render.</summary>
    public static byte[] Read(GpuDevice gpu, ID3D11Texture2D texture, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(texture);

        int bytesPerPixel = gpu.TextureFormat == Format.R16G16B16A16_Float ? 8 : 4;

        using ID3D11Texture2D staging = gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = gpu.TextureFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });

        gpu.Context.CopyResource(staging, texture);
        MappedSubresource map = gpu.Context.Map(staging, 0, MapMode.Read,
            Vortice.Direct3D11.MapFlags.None);
        try
        {
            int rowBytes = width * bytesPerPixel;
            byte[] result = new byte[rowBytes * height];
            for (int y = 0; y < height; y++)
                Marshal.Copy(map.DataPointer + (int)(y * map.RowPitch), result, y * rowBytes, rowBytes);
            return result;
        }
        finally
        {
            gpu.Context.Unmap(staging, 0);
        }
    }

    /// <summary>Luma media aproximada de un buffer BGRA8. Sirve para afirmar
    /// "esto no es negro" sin depender del contenido exacto del clip.</summary>
    public static double MeanLumaBgra8(byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (bgra.Length == 0)
            return 0;
        double sum = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
            sum += (0.114 * bgra[i]) + (0.587 * bgra[i + 1]) + (0.299 * bgra[i + 2]);
        return sum / (bgra.Length / 4.0);
    }
}
