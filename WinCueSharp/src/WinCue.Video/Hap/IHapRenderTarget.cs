// =============================================================================
// IHapRenderTarget.cs — A dónde dibuja el motor HAP
// =============================================================================
// HapCuePlayer (decodificación + reloj + hilo de presentación) es idéntico en
// las dos rutas; lo único que cambia es el DESTINO del dibujo:
//
//   · HapGpuPipeline  (ruta clásica) → RT compartida → D3D9Ex → D3DImage → DWM
//   · HapRingTarget   (motor nuevo)  → slot del anillo → lo muestrea el
//                                       compositor propio
//
// Extraer este contrato es lo que permite que el motor nuevo reutilice TODA la
// lógica de reproducción HAP —que lleva meses de casos reales encima— en vez
// de reescribirla.
// =============================================================================

namespace WinCue.Video.Hap;

internal interface IHapRenderTarget
{
    /// <summary>Tamaño de la superficie de destino: el player lo usa para
    /// calcular el letterbox del vídeo dentro de ella.</summary>
    int Width { get; }

    int Height { get; }

    HapVideoSession CreateSession(int videoWidth, int videoHeight, HapTextureFormat format);

    void Upload(HapVideoSession session, int slot, ReadOnlySpan<byte> dxt);

    void Render(HapVideoSession session, int slot, int destX, int destY, int destW, int destH);

    /// <summary>El fotograma está dibujado y listo.</summary>
    /// <param name="pts">Instante del fotograma en el medio, en segundos. La
    /// ruta clásica lo ignora (el DWM no sincroniza por tiempo); el motor
    /// nuevo lo necesita para publicar en el anillo — es la marca con la que
    /// el compositor pedirá "el fotograma del instante T".</param>
    void NotifyFrame(double pts);
}
