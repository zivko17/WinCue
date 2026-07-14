// =============================================================================
// Cue.cs — Unidad atómica de reproducción (port de cue_model.py::Cue)
// =============================================================================
// Clase mutable por diseño: el estado y la latencia se actualizan durante la
// ejecución. El Id es estable y único. Los nombres JSON (snake_case) y el
// ORDEN de declaración de propiedades replican el to_dict() de Python para
// que los shows guardados sean intercambiables entre ambas versiones.
//
// State y LatencyMs son runtime: NO se persisten (igual que en Python).
// =============================================================================

using System.Text.Json.Serialization;

namespace WinCue.Core;

public class Cue
{
    // ── Identidad / acción ─────────────────────────────────────────────
    [JsonPropertyName("id")]        public int     Id       { get; set; }
    [JsonPropertyName("type")]      public CueType Type     { get; set; } = CueType.Memo;
    [JsonPropertyName("name")]      public string  Name     { get; set; } = "";
    [JsonPropertyName("duration")]  public double  Duration { get; set; }
    [JsonPropertyName("file_path")] public string  FilePath { get; set; } = "";

    // ── Numeración / organización ──────────────────────────────────────
    [JsonPropertyName("number")] public string Number { get; set; } = "";
    [JsonPropertyName("color")]  public string Color  { get; set; } = "";
    [JsonPropertyName("notes")]  public string Notes  { get; set; } = "";
    [JsonPropertyName("armed")]  public bool   Armed  { get; set; } = true;

    // ── Temporización / encadenado ─────────────────────────────────────
    [JsonPropertyName("pre_wait")]      public double       PreWait      { get; set; }
    [JsonPropertyName("post_wait")]     public double       PostWait     { get; set; }
    [JsonPropertyName("continue_mode")] public ContinueMode ContinueMode { get; set; } = ContinueMode.None;

    // ── Media ──────────────────────────────────────────────────────────
    [JsonPropertyName("volume")]   public double Volume  { get; set; } = 1.0;
    [JsonPropertyName("fade_in")]  public double FadeIn  { get; set; }
    [JsonPropertyName("fade_out")] public double FadeOut { get; set; }
    [JsonPropertyName("loop")]     public bool   Loop    { get; set; }

    /// <summary>Dispositivo de salida de audio de ESTE cue ("" = principal).
    /// Campo nuevo de la versión C# — la versión Python lo ignora al cargar.</summary>
    [JsonPropertyName("audio_output")] public string AudioOutput { get; set; } = "";

    // ── Cues que apuntan a otro cue (FADE/START/STOP) ──────────────────
    [JsonPropertyName("target_id")]         public int? TargetId       { get; set; }
    [JsonPropertyName("fade_stop_on_done")] public bool FadeStopOnDone { get; set; } = true;

    /// <summary>Modo GO "per_cue": al dispararse detiene el resto de cues activos.</summary>
    [JsonPropertyName("stop_others")] public bool StopOthers { get; set; }

    // ── Grupos (jerarquía) ─────────────────────────────────────────────
    [JsonPropertyName("parent_id")]  public int?      ParentId  { get; set; }
    [JsonPropertyName("group_mode")] public GroupMode GroupMode { get; set; } = GroupMode.Enter;
    [JsonPropertyName("collapsed")]  public bool      Collapsed { get; set; }

    /// <summary>Capa (fila 0–4) del cue en el editor de línea de tiempo de su
    /// grupo TIMELINE. Campo nuevo de la versión C# — Python lo ignora al cargar.</summary>
    [JsonPropertyName("timeline_layer")] public int TimelineLayer { get; set; }

    // ── Inspector: Básicos extra ───────────────────────────────────────
    [JsonPropertyName("flagged")]   public bool Flagged  { get; set; }
    [JsonPropertyName("auto_load")] public bool AutoLoad { get; set; }

    // ── Inspector: Triggers ────────────────────────────────────────────
    [JsonPropertyName("hotkey")]            public string        Hotkey          { get; set; } = "";
    [JsonPropertyName("retrigger")]         public RetriggerMode Retrigger       { get; set; } = RetriggerMode.None;
    [JsonPropertyName("fade_others")]       public bool          FadeOthers      { get; set; }
    [JsonPropertyName("fade_others_time")]  public double        FadeOthersTime  { get; set; } = 3.0;
    [JsonPropertyName("fade_others_scope")] public FadeScope     FadeOthersScope { get; set; } = FadeScope.Peers;

    // ── Geometría (vídeo/imagen) y recorte (audio/vídeo) ───────────────
    [JsonPropertyName("scale")]      public double Scale     { get; set; } = 100.0;  // % (100 = ajuste a la salida)
    [JsonPropertyName("trim_start")] public double TrimStart { get; set; }           // punto de entrada (s)
    [JsonPropertyName("trim_end")]   public double TrimEnd   { get; set; }           // punto de salida (s); 0 = hasta el final

    // ── Time & Loops ───────────────────────────────────────────────────
    [JsonPropertyName("play_count")]     public int    PlayCount     { get; set; } = 1;
    [JsonPropertyName("rate")]           public double Rate          { get; set; } = 1.0;
    [JsonPropertyName("preserve_pitch")] public bool   PreservePitch { get; set; } = true;

    // ── Triggers extra (MIDI / Wall-clock / Timecode / Duck / release) ─
    [JsonPropertyName("midi_enabled")]      public bool            MidiEnabled      { get; set; }
    [JsonPropertyName("midi_type")]         public MidiTriggerType MidiType         { get; set; } = MidiTriggerType.NoteOn;
    [JsonPropertyName("midi_channel")]      public int             MidiChannel      { get; set; } = 1;
    [JsonPropertyName("midi_note")]         public int             MidiNote         { get; set; }
    [JsonPropertyName("midi_velocity")]     public int             MidiVelocity     { get; set; } = -1;  // -1 = "any"
    [JsonPropertyName("wallclock_enabled")] public bool            WallclockEnabled { get; set; }
    [JsonPropertyName("wallclock_time")]    public string          WallclockTime    { get; set; } = "";  // "HH:MM:SS" (24h)
    [JsonPropertyName("wallclock_days")]    public int             WallclockDays    { get; set; } = 127; // bitmask L..D
    [JsonPropertyName("timecode_enabled")]  public bool            TimecodeEnabled  { get; set; }
    [JsonPropertyName("timecode_value")]    public string          TimecodeValue    { get; set; } = "";  // "HH:MM:SS:FF" o "HH:MM:SS.mmm"
    [JsonPropertyName("duck_enabled")]      public bool            DuckEnabled      { get; set; }
    [JsonPropertyName("duck_level")]        public double          DuckLevel        { get; set; }        // dB (negativo = duck)
    [JsonPropertyName("duck_time")]         public double          DuckTime         { get; set; } = 1.0;
    [JsonPropertyName("second_trigger_on_release")] public bool SecondTriggerOnRelease { get; set; }

    // ── Display & Geometry (vídeo) ─────────────────────────────────────
    [JsonPropertyName("geo_mode")]        public GeoMode GeoMode        { get; set; } = GeoMode.Full;
    [JsonPropertyName("layer")]           public string  Layer          { get; set; } = "top";   // top | bottom | 1..999
    [JsonPropertyName("opacity")]         public double  Opacity        { get; set; } = 100.0;   // 0..100 (fase 2: compositor)
    [JsonPropertyName("preserve_aspect")] public bool    PreserveAspect { get; set; } = true;
    [JsonPropertyName("translate_x")]     public double  TranslateX     { get; set; }            // px desde el centro (Custom)
    [JsonPropertyName("translate_y")]     public double  TranslateY     { get; set; }
    [JsonPropertyName("scale_x")]         public double  ScaleX         { get; set; } = 1.0;
    [JsonPropertyName("scale_y")]         public double  ScaleY         { get; set; } = 1.0;
    [JsonPropertyName("anchor_x")]        public double  AnchorX        { get; set; }
    [JsonPropertyName("anchor_y")]        public double  AnchorY        { get; set; }
    [JsonPropertyName("rotate_x")]        public double  RotateX        { get; set; }            // grados
    [JsonPropertyName("rotate_y")]        public double  RotateY        { get; set; }
    [JsonPropertyName("rotate_z")]        public double  RotateZ        { get; set; }

    // ── Runtime (NO se persisten) ──────────────────────────────────────
    [JsonIgnore] public CueState State     { get; set; } = CueState.Idle;
    [JsonIgnore] public double?  LatencyMs { get; set; }  // medida real en ms; null = sin medir

    // ------------------------------------------------------------------
    // Utilidades
    // ------------------------------------------------------------------

    /// <summary>Número mostrado en la lista: el explícito o el Id como fallback.</summary>
    [JsonIgnore]
    public string DisplayNumber => string.IsNullOrEmpty(Number) ? Id.ToString() : Number;

    public bool IsMedia() => CueConstants.MediaTypes.Contains(Type);

    public bool TargetsOther() => CueConstants.TargetingTypes.Contains(Type);

    /// <summary>
    /// Validación y clamp defensivo de valores numéricos
    /// (equivale al __post_init__ de la dataclass Python).
    /// Llamar tras construir o deserializar un Cue.
    /// </summary>
    public void Normalize()
    {
        if (Duration < 0)
            throw new ArgumentException("La duración no puede ser negativa.");
        PreWait  = Math.Max(0.0, PreWait);
        PostWait = Math.Max(0.0, PostWait);
        FadeIn   = Math.Max(0.0, FadeIn);
        FadeOut  = Math.Max(0.0, FadeOut);
        Volume   = Math.Clamp(Volume, 0.0, 1.0);
        TimelineLayer = Math.Clamp(TimelineLayer, 0, 4);
    }
}
