// =============================================================================
// SettingsManager.cs — Persistencia de configuración (port de settings_manager.py)
// =============================================================================
// Singleton que carga/guarda settings.json junto al ejecutable. Si el archivo
// no existe lo crea con valores por defecto. El formato es compatible con el
// settings.json de la versión Python (mismas claves, mismos tipos).
// =============================================================================

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinCue.Core;

public class SettingsManager
{
    private static SettingsManager? _instance;
    private static readonly object _lock = new();

    private readonly string _settingsPath;
    private JsonObject _data = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ------------------------------------------------------------------
    // Valores de fábrica (espejo de DEFAULTS en settings_manager.py)
    // ------------------------------------------------------------------

    /// <summary>Versión del esquema de settings. Se sube al introducir cambios
    /// de default que requieren migrar archivos antiguos (ver Migrate).</summary>
    private const int CurrentSchema = 3;

    public static JsonObject CreateDefaults() => new()
    {
        ["settings_schema"] = CurrentSchema,

        // ── Audio ────────────────────────────────────────────────────
        ["audio_device_index"] = -1,
        ["audio_device_name"]  = "",
        // Solo "wasapi" | "asio". DirectSound se retiró en el esquema 3: en
        // Windows moderno se EMULA sobre WASAPI (más latencia, cero ventajas)
        // y además nunca estuvo implementado — elegirlo abría un WasapiOut.
        // Ofrecer una opción que miente es peor que no ofrecerla.
        // Con driver "asio", audio_device_name guarda el NOMBRE DEL DRIVER
        // ASIO (las entradas «[ASIO] …» del desplegable de dispositivos).
        ["audio_driver"]       = "wasapi",   // "wasapi" | "asio"
        ["audio_buffer_size"]  = 512,        // samples
        ["audio_sample_rate"]  = 48000,      // 44100 | 48000 | 96000
        ["master_volume"]      = 100,        // 0–100
        ["pre_roll_ms"]        = 0,          // (legado Python; sin efecto en C#)
        ["audition_device_name"] = "",       // salida de la audición local ("" = ppal)
        ["audition_volume"]    = 100,        // trim del fader de audición (0–100)

        // ── Vídeo ────────────────────────────────────────────────────
        ["video_output_screen"]     = 0,     // índice de pantalla (0 = principal)
        // Divergencia deliberada con Python (era false): al elegir un monitor
        // de salida se espera blackout a pantalla completa (estilo QLab).
        ["video_fullscreen_output"] = true,
        ["video_default_volume"]    = 100,
        ["video_loop_default"]      = false,
        ["video_fade_frames"]       = 0,
        // Techo del AUDIO ESPEJO de los vídeos (min). 0 = SIN límite: con el
        // streaming del paso 4 la RAM es constante — solo actúa como opt-out
        // (>0) o como techo clásico de 20 min si WINCUE_MIRROR_STREAMING=0.
        ["video_audio_max_minutes"] = 0,
        // MOTOR ProRes PROPIO (paso 2 de ProRes). Encendido por defecto, pero
        // con interruptor porque su decodificador es NATIVO Y EN PROCESO: si
        // revienta se lleva la aplicación entera (con mpv iba por subproceso y
        // no podía). Apagado → los ProRes van por mpv exactamente como antes.
        // Kill-switch de directo equivalente: WINCUE_PRORES_ENGINE=0.
        ["video_prores_engine"]     = true,
        ["surface_preset"]          = "screen",  // "screen" | "custom" | "WxH"
        ["surface_width"]           = 0,         // tamaño del CANVAS (0 = 1920)
        ["surface_height"]          = 0,         // tamaño del CANVAS (0 = 1080)
        ["blackout_fade"]           = 1,         // s del fundido del blackout (tecla B)
        // Monitores de las salidas 2-4 (C#-only; la 1 usa video_output_screen).
        // -1 = DESHABILITADA: no aparece en el botón de salidas de la UI y no se
        // puede lanzar (evita mandar una salida al monitor principal sin querer).
        ["video_output_screen2"]    = -1,
        ["video_output_screen3"]    = -1,
        ["video_output_screen4"]    = -1,

        // ── Red / Control externo ────────────────────────────────────
        ["osc_enabled"]       = false,
        ["osc_listen_port"]   = 8000,
        ["osc_send_host"]     = "127.0.0.1",
        ["osc_send_port"]     = 9000,
        ["midi_enabled"]      = false,
        ["midi_device_index"] = -1,
        ["midi_device_name"]  = "",
        ["midi_go_note"]      = 60,
        ["midi_stop_note"]    = 61,

        // ── Comportamiento de GO ─────────────────────────────────────
        ["go_mode"] = "concurrent",   // "concurrent" | "sequential" | "per_cue"
        ["go_min_interval_ms"] = 200, // anti doble-tap del GO (0 = desactivado)
        ["fade_all_seconds"] = 5.0,   // duración del FADE ALL manual

        // ── PANIC (emergencia) ───────────────────────────────────────
        ["panic_fade"]        = 1.5,      // s de fundido del PANIC
        ["panic_action"]      = "black",  // "black" | "image" (imagen de respaldo)
        ["panic_image_path"]  = "",       // cartela mostrada en las salidas si "image"
        ["panic_double_hard"] = true,     // 2ª pulsación en <2 s = corte duro

        // ── Interfaz ─────────────────────────────────────────────────
        // Latencia OFF por defecto (útil para depurar, ruido para operar)
        ["show_latency_column"] = false,
        ["show_next_cue_label"] = true,
        ["confirm_last_cue"]    = true,
        ["keyboard_go"]         = "Space",
        ["keyboard_stop"]       = "S",
        ["keyboard_prev"]       = "Left",
        ["theme"]               = "dark",
        ["ui_font_scale"]       = 100,   // % de la tabla/tarjetas (FOH a oscuras)

        // ── Rutas ────────────────────────────────────────────────────
        ["default_show_path"]  = "",
        ["default_media_path"] = "",
        ["ffmpeg_path"]        = "",
    };

    // ------------------------------------------------------------------
    // Singleton
    // ------------------------------------------------------------------

    /// <summary>Devuelve (o crea) la instancia única.</summary>
    public static SettingsManager Instance
    {
        get
        {
            lock (_lock)
            {
                _instance ??= new SettingsManager(
                    Path.Combine(AppContext.BaseDirectory, "settings.json"));
                return _instance;
            }
        }
    }

    /// <summary>Constructor con ruta explícita (para tests). Para la app, usar Instance.</summary>
    public SettingsManager(string settingsPath)
    {
        _settingsPath = settingsPath;
        Load();
    }

    // ------------------------------------------------------------------
    // API pública
    // ------------------------------------------------------------------

    /// <summary>Devuelve el valor tipado para key, o default si no existe o no convierte.</summary>
    public T Get<T>(string key, T defaultValue = default!)
    {
        if (_data[key] is not JsonNode node)
            return defaultValue;
        try
        {
            return node.Deserialize<T>() ?? defaultValue;
        }
        catch (JsonException)
        {
            return defaultValue;
        }
    }

    /// <summary>Guarda el valor y persiste inmediatamente en disco.</summary>
    public void Set<T>(string key, T value)
    {
        _data[key] = JsonSerializer.SerializeToNode(value);
        Save();
    }

    /// <summary>Aplica múltiples cambios y guarda una sola vez.</summary>
    public void SetMany(IReadOnlyDictionary<string, object?> updates)
    {
        foreach (var (key, value) in updates)
            _data[key] = JsonSerializer.SerializeToNode(value);
        Save();
    }

    /// <summary>Devuelve una copia profunda del objeto completo de configuración.</summary>
    public JsonObject GetAll() => (JsonObject)_data.DeepClone();

    /// <summary>Restaura todos los valores de fábrica y guarda.</summary>
    public void ResetDefaults()
    {
        _data = CreateDefaults();
        Save();
    }

    // ------------------------------------------------------------------
    // Persistencia interna
    // ------------------------------------------------------------------

    private void Load()
    {
        if (File.Exists(_settingsPath))
        {
            try
            {
                var loaded = JsonNode.Parse(File.ReadAllText(_settingsPath)) as JsonObject
                    ?? throw new JsonException("settings.json no es un objeto JSON");
                // Versión del archivo ANTES de fusionar (los archivos previos a
                // la introducción del esquema no tienen la clave → v1).
                int fileSchema = 1;
                if (loaded["settings_schema"] is JsonValue sv && sv.TryGetValue(out int parsed))
                    fileSchema = parsed;
                // Combinar: defaults como base, luego valores guardados encima.
                // Así las claves nuevas (versiones futuras) tienen su default.
                _data = CreateDefaults();
                foreach (var (key, value) in loaded)
                    _data[key] = value?.DeepClone();
                if (fileSchema < CurrentSchema)
                    Migrate(fileSchema);
            }
            catch (Exception exc) when (exc is JsonException or IOException)
            {
                // Archivo corrupto: usar defaults y reescribir
                _data = CreateDefaults();
                Save();
            }
        }
        else
        {
            _data = CreateDefaults();
            Save();
        }
    }

    /// <summary>Migra settings de esquemas antiguos. Se llama en Load cuando el
    /// archivo tiene una versión inferior a CurrentSchema.
    /// v1→v2: las salidas de vídeo 2-4 pasan del default histórico «0 = monitor
    /// principal» (sin sentido para una salida secundaria y causa de lanzarlas
    /// sin querer sobre el operador) a «-1 = deshabilitada». Solo se tocan las
    /// que siguen en 0; si ya tienen un monitor asignado, se respetan.
    /// v2→v3: se retira el driver "directsound". Nunca estuvo implementado
    /// (AudioEngine.CreateOutput solo distingue "asio"; todo lo demás acababa
    /// en WasapiOut) y en Windows moderno DirectSound se emula sobre WASAPI.
    /// Los settings que lo tuvieran pasan a "wasapi", que es lo que ya estaban
    /// usando de facto — así el desplegable deja de mentir.</summary>
    private void Migrate(int fileSchema)
    {
        if (fileSchema < 2)
            foreach (var key in new[]
                { "video_output_screen2", "video_output_screen3", "video_output_screen4" })
                if (Get<int>(key, -1) == 0)
                    _data[key] = -1;

        if (fileSchema < 3 &&
            !string.Equals(Get("audio_driver", "wasapi"), "asio", StringComparison.OrdinalIgnoreCase))
            _data["audio_driver"] = "wasapi";

        _data["settings_schema"] = CurrentSchema;
        Save();
    }

    /// <summary>Escribe settings.json de forma atómica (escritura + rename).</summary>
    private void Save()
    {
        string json = _data.ToJsonString(JsonOptions);
        string tmp = _settingsPath + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, _settingsPath, overwrite: true);
        }
        catch (IOException)
        {
            // Guardar sin atomicidad como fallback
            try { File.WriteAllText(_settingsPath, json); }
            catch (IOException) { /* Sin acceso a disco: la sesión continúa en memoria */ }
        }
    }
}
