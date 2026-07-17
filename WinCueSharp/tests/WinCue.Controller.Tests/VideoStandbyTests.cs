// =============================================================================
// VideoStandbyTests.cs — GO instantáneo: precarga en PAUSA del cue en standby
// =============================================================================
// Con la salida visible, el vídeo del playhead se prepara con el player
// pausado (opacidad 0 hasta el GO); sin salida abierta solo se verifica (la
// precarga no debe ABRIR ventanas); editar el cue en standby lo re-prepara.
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class VideoStandbyTests : ControllerTestBase
{
    [Fact]
    public void StandbyConSalidaVisible_PreparaEnPausaYOcultaSuSuperficie()
    {
        AddCue(CueType.Audio, "antes", duration: 10.0);
        var v = AddCue(CueType.Video, "siguiente", duration: 10.0,
            setup: c => c.FadeIn = 1.5);
        View.VisibleOutputs.Add(0);

        Controller.OnGoPressed();   // dispara el audio → el vídeo queda en standby

        var prep = Assert.Single(Video.Prepared);
        Assert.Equal(v.Id, prep.CueId);
        Assert.Equal(1.5, prep.P.HandoverSeconds, 2);   // mismos parámetros que el GO
        Assert.Equal(0.0, View.CueOpacities[v.Id]);      // preparado ≠ en pantalla
    }

    [Fact]
    public void StandbySinSalidaAbierta_SoloVerifica()
    {
        AddCue(CueType.Audio, "antes", duration: 10.0);
        var v = AddCue(CueType.Video, "siguiente", duration: 10.0);
        // sin View.VisibleOutputs: la salida está cerrada (modo EDIT)

        Controller.OnGoPressed();

        Assert.Empty(Video.Prepared);
        Assert.Contains(v.Id, Video.Preloaded);   // verificación clásica
    }

    [Fact]
    public void EditarElCueEnStandby_LoRePrepara()
    {
        AddCue(CueType.Audio, "antes", duration: 10.0);
        var v = AddCue(CueType.Video, "siguiente", duration: 10.0);
        View.VisibleOutputs.Add(0);
        Controller.OnGoPressed();
        Video.Prepared.Clear();
        Video.Discarded.Clear();

        Controller.OnCueEditCommitted(1, new Dictionary<string, object?>
        { ["fade_in"] = 2.0 });

        Assert.Contains(v.Id, Video.Discarded);
        var prep = Assert.Single(Video.Prepared);
        Assert.Equal(2.0, prep.P.HandoverSeconds, 2);   // preparado con lo NUEVO
    }

    [Fact]
    public void PlayheadSaleDelVideo_DescartaElStandby()
    {
        AddCue(CueType.Video, "video", duration: 10.0);
        AddCue(CueType.Memo, "memo");
        View.VisibleOutputs.Add(0);

        Controller.OnGoPressed();   // dispara el vídeo → standby pasa al MEMO

        Assert.Contains(-1, Video.Discarded);   // el standby ya no es un vídeo
    }

    [Fact]
    public void CargarShow_DescartaElStandby()
    {
        AddCue(CueType.Video, "video", duration: 10.0);
        string path = Path.Combine(TempDir, "otro.json");
        var other = new CueModel();
        other.AddCue(new Cue { Id = 1, Type = CueType.Memo, Name = "nuevo" });
        File.WriteAllText(path, other.ToJson());

        Controller.LoadShow(path);

        Assert.Contains(-1, Video.Discarded);
    }
}
