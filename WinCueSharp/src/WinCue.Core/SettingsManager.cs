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

    public static JsonObject CreateDefaults() => new()
    {
        // ── Audio ────────────────────────────────────────────────────
        ["audio_device_index"] = -1,
        ["audio_device_name"]  = "",
        ["audio_driver"]       = "wasapi",   // "wasapi" | "asio" | "directsound"
        ["audio_buffer_size"]  = 512,        // samples
        ["audio_sample_rate"]  = 48000,      // 44100 | 48000 | 96000
        ["master_volume"]      = 100,        // 0–100
        ["pre_roll_ms"]        = 0,          // (legado Python; sin efecto en C#)
        ["audition_device_name"] = "",       // salida de la audición local ("" = ppal)

        // ── Vídeo ────────────────────────────────────────────────────
        ["video_output_screen"]     = 0,     // índice de pantalla (0 = principal)
        // Divergencia deliberada con Python (era false): al elegir un monitor
        // de salida se espera blackout a pantalla completa (estilo QLab).
        ["video_fullscreen_output"] = true,
        ["video_default_volume"]    = 100,
        ["video_loop_default"]      = false,
        ["video_fade_frames"]       = 0,
        ["surface_preset"]          = "screen",  // "screen" | "custom" | "WxH"
        ["surface_width"]           = 0,
        ["surface_height"]          = 0,

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

        // ── Interfaz ─────────────────────────────────────────────────
        // Latencia OFF por defecto (útil para depurar, ruido para operar)
        ["show_latency_column"] = false,
        ["show_next_cue_label"] = true,
        ["confirm_last_cue"]    = true,
        ["keyboard_go"]         = "Space",
        ["keyboard_stop"]       = "S",
        ["keyboard_prev"]       = "Left",
        ["theme"]               = "dark",

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
                // Combinar: defaults como base, luego valores guardados encima.
                // Así las claves nuevas (versiones futuras) tienen su default.
                _data = CreateDefaults();
                foreach (var (key, value) in loaded)
                    _data[key] = value?.DeepClone();
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
