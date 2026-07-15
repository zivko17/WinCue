// =============================================================================
// RelocateMediaTests.cs — Relocalización de medios faltantes por nombre
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class RelocateMediaTests : ControllerTestBase
{
    [Fact]
    public void RelocateMedia_RecuperaPorNombreDeArchivo()
    {
        // Media "perdido" (ruta vieja inexistente) cuyo archivo real vive
        // en otra carpeta (subcarpeta incluida: búsqueda recursiva)
        string mediaDir = Path.Combine(TempDir, "medios", "audio");
        Directory.CreateDirectory(mediaDir);
        string real = Path.Combine(mediaDir, "intro.wav");
        File.WriteAllBytes(real, new byte[] { 1, 2, 3 });

        var cue = AddCue(CueType.Audio, "intro",
            setup: c => c.FilePath = @"D:\maquina_vieja\intro.wav");
        var ok = AddCue(CueType.Audio, "ok",
            setup: c => c.FilePath = real);   // este ya existe: no se toca

        Assert.Equal(1, Controller.MissingMediaCount());

        int recovered = Controller.RelocateMedia(Path.Combine(TempDir, "medios"));

        Assert.Equal(1, recovered);
        Assert.Equal(real, cue.FilePath);
        Assert.Equal(real, ok.FilePath);
        Assert.Equal(0, Controller.MissingMediaCount());
        // El caché de audio del cue recuperado se invalida
        Assert.Contains(cue.Id, Audio.Invalidated);
    }

    [Fact]
    public void RelocateMedia_SinCoincidencias_NoTocaNada()
    {
        string emptyDir = Path.Combine(TempDir, "vacia");
        Directory.CreateDirectory(emptyDir);
        var cue = AddCue(CueType.Audio, "perdido",
            setup: c => c.FilePath = @"D:\no\existe.wav");

        Assert.Equal(0, Controller.RelocateMedia(emptyDir));
        Assert.Equal(@"D:\no\existe.wav", cue.FilePath);
        Assert.Equal(1, Controller.MissingMediaCount());
    }
}
