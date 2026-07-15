// =============================================================================
// CanvasModeTests.cs — Modo canvas (estilo Watchout): recortes por salida
// =============================================================================
// Con video_canvas_mode + video_compositor, los cues de vídeo se colocan en
// el canvas global y cada salida habilitada recibe su recorte (CanvasOutputs
// en los parámetros de Play). La exclusividad de pista pasa a ser GLOBAL.
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class CanvasModeTests : ControllerTestBase
{
    private void EnableCanvas(int w = 1920, int h = 1080)
    {
        Settings.Set("video_compositor", true);
        Settings.Set("video_canvas_mode", true);
        Settings.Set("surface_width", w);
        Settings.Set("surface_height", h);
    }

    [Fact]
    public void Go_EnCanvas_PasaLasVistasAlMotor()
    {
        EnableCanvas(3840, 1080);
        Settings.Set("video_output_enabled2", true);
        Settings.Set("video_output_region", "0,0,1920,1080");
        Settings.Set("video_output_region2", "1920,0,1920,1080");
        AddCue(CueType.Video);

        Controller.OnGoPressed();

        var p = Assert.Single(Video.Plays).P;
        Assert.NotNull(p.CanvasOutputs);
        Assert.Equal(2, p.CanvasOutputs!.Count);
        Assert.Equal(new CanvasView(0, 0, 0, 1920, 1080), p.CanvasOutputs[0]);
        Assert.Equal(new CanvasView(1, 1920, 0, 1920, 1080), p.CanvasOutputs[1]);
        // La geometría es relativa al CANVAS, no a la ventana de salida
        Assert.Equal(3840.0, p.Geometry!.SurfW);
        Assert.Equal(1080.0, p.Geometry.SurfH);
    }

    [Fact]
    public void Go_SinModoCanvas_NoPasaVistas()
    {
        AddCue(CueType.Video);
        Controller.OnGoPressed();
        Assert.Null(Assert.Single(Video.Plays).P.CanvasOutputs);
    }

    [Fact]
    public void Go_CanvasSinCompositor_NoPasaVistas()
    {
        Settings.Set("video_canvas_mode", true);   // sin video_compositor
        AddCue(CueType.Video);
        Controller.OnGoPressed();
        Assert.Null(Assert.Single(Video.Plays).P.CanvasOutputs);
    }

    [Fact]
    public void Canvas_ExclusividadDePistaEsGlobal()
    {
        EnableCanvas();
        // En clásico convivirían (salidas distintas); en canvas la pista es
        // global: el segundo GO sustituye al primero.
        var a = AddCue(CueType.Video, setup: c => { c.TimelineLayer = 2; c.VideoOutput = 0; });
        AddCue(CueType.Video, setup: c => { c.TimelineLayer = 2; c.VideoOutput = 1; });

        Controller.OnGoPressed();
        Controller.OnGoPressed();

        Assert.Contains(a.Id, Video.Stops);
    }

    [Fact]
    public void Canvas_SalidasDeshabilitadasNoGeneranVista()
    {
        EnableCanvas();
        Settings.Set("video_output_enabled3", true);
        AddCue(CueType.Video);

        Controller.OnGoPressed();

        var views = Assert.Single(Video.Plays).P.CanvasOutputs!;
        Assert.Equal(2, views.Count);
        Assert.Equal(0, views[0].Screen);
        Assert.Equal(2, views[1].Screen);   // salida 3 habilitada
    }

    [Fact]
    public void ParseRegion_RecortaAlCanvasYToleraBasura()
    {
        // Región válida que se sale del canvas → recortada
        var v = MainController.ParseRegion("1000,500,2000,1000", 1, 1920, 1080);
        Assert.Equal(new CanvasView(1, 1000, 500, 920, 580), v);

        // Basura → canvas completo
        Assert.Equal(new CanvasView(0, 0, 0, 1920, 1080),
            MainController.ParseRegion("patata", 0, 1920, 1080));
        Assert.Equal(new CanvasView(2, 0, 0, 1920, 1080),
            MainController.ParseRegion("", 2, 1920, 1080));
    }
}
