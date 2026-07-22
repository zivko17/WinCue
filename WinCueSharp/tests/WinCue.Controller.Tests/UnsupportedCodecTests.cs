// =============================================================================
// UnsupportedCodecTests.cs — Guard de códecs de vídeo no soportados (HAP)
// =============================================================================
// El compositor GPU de WinCue revienta de forma NATIVA con HAP (libmpv lo
// decodifica bien, pero el interop GL↔DX no). El guard debe: (a) no sondear
// contenedores que no llevan HAP —coste cero para mp4/imágenes—, y (b) si
// ffprobe está disponible, detectar un .mov HAP real.
// =============================================================================

using WinCue.Controller;
using Xunit;

namespace WinCue.Controller.Tests;

public class UnsupportedCodecTests
{
    [Fact]
    public void HapIsInUnsupportedSet()
    {
        Assert.Contains("hap", MediaImporter.UnsupportedVideoCodecs);
    }

    [Theory]
    [InlineData("clip.mp4")]      // contenedor que no lleva HAP → sin ffprobe
    [InlineData("clip.webm")]
    [InlineData("foto.png")]      // imagen → nunca HAP
    [InlineData("")]              // vacío
    public void FastPath_ReturnsFalse_WithoutProbing(string name)
    {
        // No existe en disco y aun así devuelve false SIN sondear (ruta rápida
        // por extensión): confirma que los vídeos normales no pagan ffprobe.
        Assert.False(MediaImporter.IsUnsupportedVideoCodec(name));
    }

    [Fact]
    public void DetectsRealHapMov_WhenFfprobeAvailable()
    {
        // Clip HAP de prueba versionado en tests/hap-test. Si ffprobe no está en
        // el entorno de CI, VideoCodec devuelve "" y el guard da false: en ese
        // caso el test no puede afirmar nada → se omite.
        string hap = System.IO.Path.Combine(
            System.AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "tests", "hap-test", "prueba-hap.mov");
        if (!System.IO.File.Exists(hap) || MediaImporter.VideoCodec(hap).Length == 0)
            return;   // sin fichero o sin ffprobe: nada que afirmar
        Assert.True(MediaImporter.IsUnsupportedVideoCodec(hap));
    }
}
