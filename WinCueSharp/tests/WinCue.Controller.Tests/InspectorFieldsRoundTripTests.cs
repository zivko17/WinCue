// =============================================================================
// InspectorFieldsRoundTripTests.cs — la prueba que de verdad protege a los
// usuarios del inspector de Secuence (hitos-0-1).
// =============================================================================
// Secuence.App.InspectorPanel no tiene pruebas propias (es UI de WinUI 3, sin
// banco de pruebas de interfaz en este repo — igual que WinCue.App). Lo que
// SÍ se puede probar sin pantalla es el camino que el inspector usa entero:
// InspectorPanel.CommitField → MainController.OnCueEditCommitted (el MISMO
// método, el MISMO CueFields.Apply, la MISMA ruta que WinCue.App.MainWindow.
// xaml.cs.Attach conecta) → SaveShow → JSON en disco.
//
// Este archivo carga el show de pruebas REAL (tests/show-prueba/
// show-prueba.json, el mismo que Andrés abre en pantalla), cambia por ese
// camino TODOS los campos que entran en la primera pasada del inspector
// (identidad, temporización, fundidos, reproducción — ver el encargo y
// Secuence.App/InspectorPanel.cs), guarda, y comprueba que el JSON resultante
// seguiría abriendo en WinCue: parsea con System.Text.Json (validez de
// formato) y con CueModel.FromJson (validez semántica), y que los cambios
// sobreviven a la vuelta completa a disco y otra vez a memoria.
// =============================================================================

using System.Text.Json;
using WinCue.Core;

namespace WinCue.Controller.Tests;

public class InspectorFieldsRoundTripTests : ControllerTestBase
{
    private readonly string _src;

    public InspectorFieldsRoundTripTests()
    {
        string src = _src = ShowPruebaE2ETests.FindShowJson();
        string dst = Path.Combine(TempDir, "show-prueba.json");
        File.Copy(src, dst);
        foreach (var cue in CueModel.FromJson(File.ReadAllText(src)))
            if (cue.FilePath.Length > 0)
            {
                string p = Path.Combine(TempDir, cue.FilePath);
                if (!File.Exists(p))
                    File.WriteAllBytes(p, new byte[] { 0 });
            }
        Controller.LoadShow(dst);
    }

    /// <summary>Cambia, por el camino real del inspector, TODOS los campos de
    /// la primera pasada sobre un cue de AUDIO ya cargado del show de
    /// pruebas, guarda, y recarga desde disco — el JSON tiene que seguir
    /// siendo válido y traer los cambios.</summary>
    [Fact]
    public void EditarGuardarYRecargar_ConservaLosCambiosDeLaPrimeraPasadaDelInspector()
    {
        // Cue 41: AUDIO, con media real en el show de pruebas (sección 4,
        // crossfade de audio) — candidato natural para tocar volumen/loop/
        // trim sin que el tipo bloquee ningún campo.
        int row = Model.IndexOfId(41);
        Assert.True(row >= 0, "el show de pruebas cambió: falta el cue 41");
        var before = Model.GetCue(row)!;
        string originalName = before.Name;

        // Exactamente el conjunto de claves que Secuence.App.InspectorPanel
        // comete en esta pasada (ver su cabecera): identidad, temporización,
        // fundidos, reproducción. Cada Commit del inspector manda UNA sola
        // clave por llamada (CommitField(cueId, key, value)); aquí se agrupan
        // en un solo OnCueEditCommitted porque es lo que permite la firma del
        // controlador, y el resultado es idéntico — CueFields.Apply procesa
        // el diccionario clave a clave igual que si llegaran una a una.
        var edits = new Dictionary<string, object?>
        {
            ["name"] = "Tono editado desde el inspector",
            ["number"] = "4.1b",
            ["pre_wait"] = 1.25,
            ["duration"] = 12.5,
            ["post_wait"] = 0.75,
            ["continue_mode"] = "AUTO_CONTINUE",
            ["fade_in"] = 2.5,
            ["fade_out"] = 1.5,
            ["loop"] = true,
            ["play_count"] = 3,
            ["trim_start"] = 0.5,
            ["trim_end"] = 9.0,
            ["volume"] = 0.5,
        };
        Controller.OnCueEditCommitted(row, edits);

        // Se refleja YA en el modelo en memoria (mismo objeto Cue: es lo que
        // permite que el inspector no tenga que repoblarse tras cada commit
        // propio — ver InspectorPanel.cs, Commit()).
        var edited = Model.GetCue(row)!;
        Assert.Equal("Tono editado desde el inspector", edited.Name);
        Assert.Equal(ContinueMode.AutoContinue, edited.ContinueMode);
        Assert.True(edited.Loop);

        string outPath = Path.Combine(TempDir, "editado.json");
        Controller.SaveShow(outPath);
        string json = File.ReadAllText(outPath);

        // 1) El JSON sigue siendo JSON válido — el requisito literal del
        // encargo antes de mirar ningún campo.
        using (var doc = JsonDocument.Parse(json))
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        // 2) CueModel.FromJson lo relee sin excepciones ni cues perdidos —
        // validez SEMÁNTICA, no solo sintáctica: es el mismo parser que usa
        // WinCue (Python y C#), así que si esto revienta, un show guardado
        // desde Secuence dejaría de abrir en WinCue.
        var reloaded = CueModel.FromJson(json);
        Assert.Equal(Model.Count, reloaded.Count);

        // 3) Los cambios están, tal cual, tras la vuelta completa a disco.
        var back = reloaded.Single(c => c.Id == 41);
        Assert.Equal("Tono editado desde el inspector", back.Name);
        Assert.Equal("4.1b", back.Number);
        Assert.Equal(1.25, back.PreWait);
        Assert.Equal(12.5, back.Duration);
        Assert.Equal(0.75, back.PostWait);
        Assert.Equal(ContinueMode.AutoContinue, back.ContinueMode);
        Assert.Equal(2.5, back.FadeIn);
        Assert.Equal(1.5, back.FadeOut);
        Assert.True(back.Loop);
        Assert.Equal(3, back.PlayCount);
        Assert.Equal(0.5, back.TrimStart);
        Assert.Equal(9.0, back.TrimEnd);
        Assert.Equal(0.5, back.Volume);

        // 4) Los cues NO tocados no se movieron — el commit fue quirúrgico,
        // no una reescritura completa del show (comparación contra el
        // original, no contra el editado en memoria).
        var untouched = CueModel.FromJson(File.ReadAllText(_src)).Single(c => c.Id == 40);
        var untouchedReloaded = reloaded.Single(c => c.Id == 40);
        Assert.Equal(untouched.Name, untouchedReloaded.Name);
        Assert.Equal(untouched.Duration, untouchedReloaded.Duration);
        Assert.NotEqual(originalName, back.Name);   // por si el cue 41 no cambió de verdad
    }

    /// <summary>El campo "archivo" del inspector es el único con un
    /// comportamiento que no es un Apply directo: al comprometerse SIN la
    /// clave "type" a la vez (que es exactamente lo que manda InspectorPanel.
    /// FileRow), MainController deja que MediaImporter detecte el tipo por
    /// extensión — mismo camino que arrastrar un archivo a la tabla. Prueba
    /// aparte porque es la trampa más fácil de no ver: cambiar el archivo de
    /// un cue de AUDIO a un .png tiene que convertirlo a un cue de tipo
    /// distinto, no dejarlo como AUDIO con un archivo que no le pega.</summary>
    [Fact]
    public void CommitDeArchivoSinType_DejaQueMediaImporterDetecteElTipo()
    {
        int row = Model.IndexOfId(41);
        string imagen = Path.Combine(TempDir, "imagen-roja.png");
        Assert.True(File.Exists(imagen), "el show de pruebas trae imagen-roja.png");

        Controller.OnCueEditCommitted(row,
            new Dictionary<string, object?> { ["file_path"] = imagen });

        var edited = Model.GetCue(row)!;
        Assert.Equal(CueType.Video, edited.Type);   // una imagen es un cue de vídeo
        Assert.Equal(imagen, edited.FilePath, ignoreCase: true);

        string outPath = Path.Combine(TempDir, "archivo-cambiado.json");
        Controller.SaveShow(outPath);
        var reloaded = CueModel.FromJson(File.ReadAllText(outPath));
        var back = reloaded.Single(c => c.Id == 41);
        Assert.Equal(CueType.Video, back.Type);
    }
}
