// =============================================================================
// CueEnums.cs — Tipos, estados y constantes del modelo (port de cue_model.py)
// =============================================================================
// Los valores serializados en JSON deben coincidir EXACTAMENTE con los strings
// que escribe/lee la versión Python (SCREAMING_SNAKE para tipos/modos,
// snake_case minúscula para midi_type). Eso garantiza que un show guardado
// por WinCue (Python) se abre aquí y viceversa.
// =============================================================================

using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinCue.Core;

/// <summary>Convierte enums a/desde SCREAMING_SNAKE_CASE ("AUTO_CONTINUE").</summary>
public sealed class UpperSnakeEnumConverter : JsonStringEnumConverter
{
    public UpperSnakeEnumConverter()
        : base(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false) { }
}

/// <summary>Convierte enums a/desde snake_case minúscula ("note_on").</summary>
public sealed class LowerSnakeEnumConverter : JsonStringEnumConverter
{
    public LowerSnakeEnumConverter()
        : base(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) { }
}

/// <summary>Tipos de cue soportados (estilo QLab).</summary>
[JsonConverter(typeof(UpperSnakeEnumConverter))]
public enum CueType
{
    Audio,   // reproducción de media (volumen, fades, loop)
    Video,   // vídeo o imagen estática
    Wait,    // espera temporizada (útil en cadenas auto-follow)
    Fade,    // modifica el volumen de otro cue a lo largo del tiempo
    Start,   // dispara otro cue por id
    Stop,    // detiene otro cue (o todos si no hay target)
    Script,  // ejecuta un comando/script del sistema
    Memo,    // comentario, sin acción
    Group,   // contenedor jerárquico
}

/// <summary>Estado de ejecución (runtime, NO se persiste en JSON).</summary>
[JsonConverter(typeof(UpperSnakeEnumConverter))]
public enum CueState
{
    Idle,
    Loaded,
    Playing,
    Stopped,
    Error,
}

/// <summary>Cómo se encadena con el cue siguiente al dispararse.</summary>
[JsonConverter(typeof(UpperSnakeEnumConverter))]
public enum ContinueMode
{
    None,          // GO manual: el playhead avanza pero nada se auto-dispara
    AutoContinue,  // el siguiente cue se dispara tras post_wait (desde inicio)
    AutoFollow,    // el siguiente cue se dispara cuando ESTE termina su acción
}

/// <summary>Modos de un cue GROUP (estilo QLab).</summary>
[JsonConverter(typeof(UpperSnakeEnumConverter))]
public enum GroupMode
{
    Timeline,  // dispara TODOS los hijos a la vez; playhead salta tras el grupo
    Enter,     // dispara el primer hijo y el playhead entra al grupo
    GoNext,    // dispara el primer hijo; el playhead salta tras el grupo
    Random,    // dispara un hijo aleatorio (round-robin)
}

/// <summary>Comportamiento al re-disparar un cue que ya está sonando.</summary>
[JsonConverter(typeof(UpperSnakeEnumConverter))]
public enum RetriggerMode
{
    None,         // ignora el segundo disparo (por defecto)
    Panic,        // fade-out y parada
    Stop,         // parada
    HardStop,     // parada inmediata
    HardRestart,  // parada y reinicio inmediato
}

/// <summary>Ámbito de "Fade &amp; stop others over time".</summary>
[JsonConverter(typeof(UpperSnakeEnumConverter))]
public enum FadeScope
{
    Peers,
    List,
    All,
}

/// <summary>Modo de geometría de vídeo.</summary>
[JsonConverter(typeof(UpperSnakeEnumConverter))]
public enum GeoMode
{
    Full,
    Custom,
}

/// <summary>Tipo de mensaje MIDI que dispara el cue.</summary>
[JsonConverter(typeof(LowerSnakeEnumConverter))]
public enum MidiTriggerType
{
    NoteOn,
    NoteOff,
    ControlChange,
    ProgramChange,
}

/// <summary>Constantes compartidas del modelo.</summary>
public static class CueConstants
{
    /// <summary>Tipos que apuntan a otro cue (necesitan TargetId).</summary>
    public static readonly IReadOnlySet<CueType> TargetingTypes =
        new HashSet<CueType> { CueType.Fade, CueType.Start, CueType.Stop };

    /// <summary>Tipos con acción de reproducción de media.</summary>
    public static readonly IReadOnlySet<CueType> MediaTypes =
        new HashSet<CueType> { CueType.Audio, CueType.Video };

    /// <summary>Colores asociados a cada estado (usados por la vista).</summary>
    public static readonly IReadOnlyDictionary<CueState, string> StateColors =
        new Dictionary<CueState, string>
        {
            [CueState.Idle]    = "#888888",
            [CueState.Loaded]  = "#3498db",
            [CueState.Playing] = "#2ecc71",
            [CueState.Stopped] = "#ffffff",
            [CueState.Error]   = "#e74c3c",
        };

    /// <summary>Paleta de colores de etiqueta (estilo QLab). Clave vacía = sin color.</summary>
    public static readonly IReadOnlyDictionary<string, string> CueColors =
        new Dictionary<string, string>
        {
            [""]       = "",
            ["red"]    = "#c0392b",
            ["orange"] = "#d35400",
            ["yellow"] = "#c9a227",
            ["green"]  = "#27865a",
            ["blue"]   = "#2a6fb0",
            ["purple"] = "#7d4b9a",
        };
}
