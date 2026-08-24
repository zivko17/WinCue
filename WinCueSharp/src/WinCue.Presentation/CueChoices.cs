// =============================================================================
// CueChoices.cs — las opciones que ofrece el inspector, sin framework.
// =============================================================================
// Segunda pasada del inspector de Secuence (ver el encargo, hitos-0-1). Los
// desplegables del inspector y la fila de muestras de color son la clase de
// cosa que el compilador NO vigila: cada producto los escribía en línea con
// su propia lista de pares (valor, etiqueta), así que añadir un valor a un
// enum de WinCue.Core dejaba al otro producto sin esa opción — en silencio, y
// sin que ninguna prueba se enterara. Aquí viven una sola vez, y las pruebas
// exigen que CADA valor de cada enum tenga su etiqueta.
//
// Los textos son LOS DE HOY, literales de WinCue.App/InspectorPanel.cs: un
// cambio aquí se lee en pantalla en los DOS productos.
//
// Por qué en WinCue.Presentation y no en WinCue.Core: son textos de INTERFAZ
// (lo que lee el operador), no parte del modelo ni del JSON del show — Core
// define los enums y su serialización, esto solo decide cómo se PRESENTAN.
// Loc.T() vive en Core y no es framework de interfaz, así que se puede usar
// desde aquí igual que ya lo hace el resto de este proyecto.
//
// POR QUÉ CADA LISTA SE CONSTRUYE AL PEDIRLA y no se cachea en un
// `static readonly`: Loc.Current se fija al arrancar (SetFromCode desde
// settings), y un campo estático se inicializa la PRIMERA vez que alguien
// toca esta clase — si eso pasara antes de fijar el idioma, las etiquetas se
// quedarían en castellano para toda la sesión, y el fallo dependería del
// orden de arranque. Evaluando Loc.T() en cada acceso, la semántica es
// exactamente la de hoy (WinCue las escribe en línea al CONSTRUIR el
// inspector, después de fijar el idioma). Son listas de cinco elementos que
// se piden una vez por inspector: no hay nada que optimizar aquí.
//
// OJO — el valor de cada opción es enum.ToString() (PascalCase: "AutoContinue"),
// NO la forma del JSON (UPPER_SNAKE: "AUTO_CONTINUE"). Es lo que los dos
// inspectores comparan contra el cue cargado y lo que cometen; la traducción
// al JSON la hace CueFields/UpperSnakeEnumConverter, del lado del modelo.
// =============================================================================

using WinCue.Core;

namespace WinCue.Presentation;

/// <summary>Una opción de un desplegable del inspector: el valor que se comete
/// (enum.ToString()) y el texto que lee el operador.</summary>
public readonly record struct CueChoice(string Value, string Label);

/// <summary>Una muestra de color de cue: su clave en el JSON ("" = sin color),
/// la etiqueta traducida y el color ya resuelto (0xRRGGBB; 0 cuando la clave
/// vacía significa "ningún color", que cada interfaz pinta como hueco).</summary>
public readonly record struct CueColorChoice(string Key, string Label, uint Rgb, bool HasColor);

/// <summary>Opciones de los campos del inspector — ÚNICA definición, la
/// comparten WinCue.App (WPF) y Secuence.App (WinUI 3).</summary>
public static class CueChoices
{
    /// <summary>Qué hacer al terminar el cue (continue_mode).</summary>
    public static IReadOnlyList<CueChoice> ContinueModes => new[]
    {
        new CueChoice(nameof(ContinueMode.None), Loc.T("Nada (GO manual)", "None (manual GO)")),
        new CueChoice(nameof(ContinueMode.AutoContinue), Loc.T("Auto-continue (tras post-wait)", "Auto-continue (after post-wait)")),
        new CueChoice(nameof(ContinueMode.AutoFollow), Loc.T("Auto-follow (al acabar la acción)", "Auto-follow (when the action ends)")),
    };

    /// <summary>Modo de un cue GROUP (group_mode).</summary>
    public static IReadOnlyList<CueChoice> GroupModes => new[]
    {
        new CueChoice(nameof(GroupMode.Enter), Loc.T("ENTER — el playhead entra", "ENTER — the playhead enters")),
        new CueChoice(nameof(GroupMode.Timeline), Loc.T("TIMELINE — todos a la vez", "TIMELINE — all at once")),
        new CueChoice(nameof(GroupMode.GoNext), Loc.T("GO NEXT — primer hijo", "GO NEXT — first child")),
        new CueChoice(nameof(GroupMode.Random), Loc.T("RANDOM — aleatorio round-robin", "RANDOM — round-robin random")),
    };

    /// <summary>Qué hacer si el cue se dispara estando ya sonando (retrigger).</summary>
    public static IReadOnlyList<CueChoice> RetriggerModes => new[]
    {
        new CueChoice(nameof(RetriggerMode.None), Loc.T("Ignorar", "Ignore")),
        new CueChoice(nameof(RetriggerMode.Panic), Loc.T("Panic (fade y parar)", "Panic (fade and stop)")),
        new CueChoice(nameof(RetriggerMode.Stop), Loc.T("Parar", "Stop")),
        new CueChoice(nameof(RetriggerMode.HardStop), Loc.T("Parada dura", "Hard stop")),
        new CueChoice(nameof(RetriggerMode.HardRestart), Loc.T("Reiniciar", "Restart")),
    };

    /// <summary>A quién alcanza «fundir y parar otros» (fade_others_scope).</summary>
    public static IReadOnlyList<CueChoice> FadeScopes => new[]
    {
        new CueChoice(nameof(FadeScope.Peers), Loc.T("PEERS — mismo nivel/grupo", "PEERS — same level/group")),
        new CueChoice(nameof(FadeScope.List), Loc.T("LIST — toda la lista", "LIST — the whole list")),
        new CueChoice(nameof(FadeScope.All), Loc.T("ALL — todo", "ALL — everything")),
    };

    /// <summary>Tipo de mensaje MIDI que dispara el cue (midi_type). Sin
    /// traducir: «Note On» se llama igual en cualquier mesa del mundo.</summary>
    public static IReadOnlyList<CueChoice> MidiTypes => new[]
    {
        new CueChoice(nameof(MidiTriggerType.NoteOn), "Note On"),
        new CueChoice(nameof(MidiTriggerType.NoteOff), "Note Off"),
        new CueChoice(nameof(MidiTriggerType.ControlChange), "Control Change"),
        new CueChoice(nameof(MidiTriggerType.ProgramChange), "Program Change"),
    };

    /// <summary>Muestras de color de etiqueta, en el orden en que se pintan.
    /// La primera es el hueco de «sin color» (clave vacía).</summary>
    public static IReadOnlyList<CueColorChoice> Colors =>
        CueConstants.CueColors
            .Select(kv => new CueColorChoice(
                kv.Key, ColorLabel(kv.Key),
                kv.Value.Length > 0 ? ParseHex(kv.Value) : 0u,
                kv.Value.Length > 0))
            .ToList();

    /// <summary>Etiqueta de una clave de color ("" = sin color).</summary>
    public static string ColorLabel(string key) => key switch
    {
        "red" => Loc.T("Rojo", "Red"),
        "orange" => Loc.T("Naranja", "Orange"),
        "yellow" => Loc.T("Amarillo", "Yellow"),
        "green" => Loc.T("Verde", "Green"),
        "blue" => Loc.T("Azul", "Blue"),
        "purple" => Loc.T("Morado", "Purple"),
        _ => Loc.T("Sin color", "No color"),
    };

    private static uint ParseHex(string hex) => Convert.ToUInt32(hex.TrimStart('#'), 16);
}
