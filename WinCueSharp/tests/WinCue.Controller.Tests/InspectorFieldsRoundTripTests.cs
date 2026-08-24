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
// camino TODOS los campos del inspector — la primera pasada (identidad,
// temporización, fundidos, reproducción) y la SEGUNDA (color/armado/flag/
// notas, destino y grupo, velocidad, y los disparadores: hotkey, retrigger,
// «fundir y parar otros», wall-clock y MIDI) — guarda, y comprueba que el
// JSON resultante seguiría abriendo en WinCue: parsea con System.Text.Json
// (validez de formato) y con CueModel.FromJson (validez semántica), y que los
// cambios sobreviven a la vuelta completa a disco y otra vez a memoria.
//
// Por qué importa especialmente en la segunda pasada: son campos que el
// operador rellena una vez y no vuelve a mirar hasta la función. Un
// wallclock_time que no sobrevive al guardado no se nota editando — se nota
// el día del estreno.
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

    /// <summary>La SEGUNDA pasada del inspector: color/armado/flag/notas,
    /// destino y grupo, velocidad y los disparadores remotos. Mismo camino
    /// real (OnCueEditCommitted → CueFields.Apply → SaveShow) y misma
    /// exigencia: el JSON tiene que seguir abriendo y traer los valores.
    ///
    /// CueFields resuelve los campos por reflexión sobre [JsonPropertyName],
    /// así que estos campos "ya funcionaban" sin tocar el controlador — esta
    /// prueba fija justamente eso: que la clave que el inspector comete es la
    /// del JSON, y que renombrar un JsonPropertyName rompe AQUÍ (en rojo) en
    /// vez de en un inspector que calla y no guarda nada.</summary>
    [Fact]
    public void EditarGuardarYRecargar_ConservaLosCambiosDeLaSegundaPasadaDelInspector()
    {
        int row = Model.IndexOfId(41);
        Assert.True(row >= 0, "el show de pruebas cambió: falta el cue 41");

        var edits = new Dictionary<string, object?>
        {
            // Identidad
            ["color"] = "purple",
            ["armed"] = false,
            ["flagged"] = true,
            ["notes"] = "Ojo: entra con el actor ya en escena.\nSegunda línea.",
            // Destino y grupo
            ["stop_others"] = true,
            // Reproducción
            ["rate"] = 0.75,
            ["preserve_pitch"] = false,
            // Disparadores
            ["hotkey"] = "q",
            ["retrigger"] = "HARD_RESTART",
            ["fade_others"] = true,
            ["fade_others_time"] = 4.5,
            ["fade_others_scope"] = "LIST",
            ["wallclock_enabled"] = true,
            ["wallclock_time"] = "20:30:00",
            ["midi_enabled"] = true,
            ["midi_type"] = "CONTROL_CHANGE",
            ["midi_channel"] = 3,
            ["midi_note"] = 64,
            ["midi_velocity"] = 127,
        };
        Controller.OnCueEditCommitted(row, edits);

        string outPath = Path.Combine(TempDir, "editado-2.json");
        Controller.SaveShow(outPath);
        string json = File.ReadAllText(outPath);

        using (var doc = JsonDocument.Parse(json))
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        var reloaded = CueModel.FromJson(json);
        Assert.Equal(Model.Count, reloaded.Count);

        var back = reloaded.Single(c => c.Id == 41);
        Assert.Equal("purple", back.Color);
        Assert.False(back.Armed);
        Assert.True(back.Flagged);
        Assert.Contains("Segunda línea.", back.Notes);
        Assert.True(back.StopOthers);
        Assert.Equal(0.75, back.Rate);
        Assert.False(back.PreservePitch);
        Assert.Equal("q", back.Hotkey);
        Assert.Equal(RetriggerMode.HardRestart, back.Retrigger);
        Assert.True(back.FadeOthers);
        Assert.Equal(4.5, back.FadeOthersTime);
        Assert.Equal(FadeScope.List, back.FadeOthersScope);
        Assert.True(back.WallclockEnabled);
        Assert.Equal("20:30:00", back.WallclockTime);
        Assert.True(back.MidiEnabled);
        Assert.Equal(MidiTriggerType.ControlChange, back.MidiType);
        Assert.Equal(3, back.MidiChannel);
        Assert.Equal(64, back.MidiNote);
        Assert.Equal(127, back.MidiVelocity);
    }

    /// <summary>El cue OBJETIVO de un START/STOP/FADE: el desplegable del
    /// inspector comete el id, o null cuando se elige «(sin objetivo)» — y
    /// CueFields trata null/-1/"" como "quitar el objetivo" (ver su Apply).
    /// Se prueba sobre un cue que de verdad apunta a otro.</summary>
    [Fact]
    public void CommitDeCueObjetivo_PoneYQuitaElDestino()
    {
        var targeting = Model.Cues.FirstOrDefault(c => c.TargetsOther());
        Assert.NotNull(targeting);
        int row = Model.IndexOfId(targeting!.Id);
        var otro = Model.Cues.First(c => c.Id != targeting.Id && c.IsMedia());

        Controller.OnCueEditCommitted(row,
            new Dictionary<string, object?> { ["target_id"] = otro.Id });
        Assert.Equal(otro.Id, Model.GetCue(row)!.TargetId);

        // «(sin objetivo)» → el inspector comete null
        Controller.OnCueEditCommitted(row,
            new Dictionary<string, object?> { ["target_id"] = null });
        Assert.Null(Model.GetCue(row)!.TargetId);

        string outPath = Path.Combine(TempDir, "sin-objetivo.json");
        Controller.SaveShow(outPath);
        Assert.Null(CueModel.FromJson(File.ReadAllText(outPath))
            .Single(c => c.Id == targeting.Id).TargetId);
    }

    /// <summary>Cada clave que el inspector comete tiene que existir en
    /// CueFields: si no, el campo se edita en pantalla y NO PASA NADA (Apply
    /// devuelve false en silencio). Es la comprobación más barata que protege
    /// de un renombrado de [JsonPropertyName].</summary>
    [Fact]
    public void TodasLasClavesDelInspectorExistenEnCueFields()
    {
        string[] claves =
        {
            // Primera pasada
            "number", "name", "file_path", "continue_mode", "pre_wait", "duration",
            "post_wait", "fade_in", "fade_out", "loop", "play_count",
            "trim_start", "trim_end", "volume",
            // Segunda pasada
            "color", "armed", "flagged", "notes", "target_id", "fade_stop_on_done",
            "group_mode", "stop_others", "rate", "preserve_pitch",
            "hotkey", "retrigger", "fade_others", "fade_others_time",
            "fade_others_scope", "wallclock_enabled", "wallclock_time",
            "midi_enabled", "midi_type", "midi_channel", "midi_note", "midi_velocity",
            // Rack de audio y geometría (pasadas anteriores)
            "gain_db", "eq_enabled", "hpf_on", "hpf_freq", "lpf_on", "lpf_freq",
            "comp_enabled", "comp_threshold", "comp_ratio", "comp_attack",
            "comp_release", "comp_makeup",
            "geo_mode", "preserve_aspect", "scale", "scale_x", "translate_x",
            "translate_y", "rotate_z",
        };
        var faltan = claves.Where(k => !CueFields.Names.Contains(k)).ToList();
        Assert.Empty(faltan);
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
