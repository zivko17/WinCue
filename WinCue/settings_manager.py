# =============================================================================
# settings_manager.py — Persistencia de configuración de la aplicación
# =============================================================================
#
# Singleton que carga/guarda settings.json en la misma carpeta que este
# archivo. Si el archivo no existe lo crea con valores por defecto.
# Todos los accesos se hacen a través de SettingsManager.instance().
# =============================================================================

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

# Ruta del archivo de configuración junto a este módulo
_SETTINGS_PATH: Path = Path(__file__).parent / "settings.json"

# ---------------------------------------------------------------------------
# Valores de fábrica
# ---------------------------------------------------------------------------

DEFAULTS: dict[str, Any] = {
    # ── Audio ────────────────────────────────────────────────────────────────
    "audio_device_index":   -1,
    "audio_device_name":    "",
    "audio_driver":         "wasapi",   # "wasapi" | "asio" | "directsound"
    "audio_buffer_size":    512,        # samples
    "audio_sample_rate":    48000,      # 44100 | 48000 | 96000
    "master_volume":        100,        # 0–100
    "pre_roll_ms":          0,          # ms de silencio antes de reproducir

    # ── Vídeo ────────────────────────────────────────────────────────────────
    "video_output_screen":      0,      # índice de pantalla (0 = principal)
    "video_fullscreen_output":  False,
    "video_default_volume":     100,
    "video_loop_default":       False,
    "video_fade_frames":        0,      # frames de fade in/out

    # ── Red / Control externo ────────────────────────────────────────────────
    "osc_enabled":      False,
    "osc_listen_port":  8000,
    "osc_send_host":    "127.0.0.1",
    "osc_send_port":    9000,
    "midi_enabled":     False,
    "midi_device_index": -1,
    "midi_device_name": "",
    "midi_go_note":     60,             # nota MIDI para GO
    "midi_stop_note":   61,             # nota MIDI para STOP

    # ── Interfaz ─────────────────────────────────────────────────────────────
    "show_latency_column":  True,
    "show_next_cue_label":  True,
    "confirm_last_cue":     True,       # diálogo al llegar al último cue
    "keyboard_go":          "Space",
    "keyboard_stop":        "S",
    "keyboard_prev":        "Left",
    "theme":                "dark",     # solo "dark" por ahora

    # ── Rutas ────────────────────────────────────────────────────────────────
    "default_show_path":    "",         # carpeta por defecto al abrir shows
    "default_media_path":   "",         # carpeta por defecto al asignar media
    "ffmpeg_path":          "",         # vacío = buscar en PATH del sistema
}


# ---------------------------------------------------------------------------
# SettingsManager
# ---------------------------------------------------------------------------

class SettingsManager:
    """Singleton de configuración. Acceder siempre via SettingsManager.instance()."""

    _instance: SettingsManager | None = None

    # ------------------------------------------------------------------
    # Singleton
    # ------------------------------------------------------------------

    @classmethod
    def instance(cls) -> SettingsManager:
        """Devuelve (o crea) la instancia única."""
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    # ------------------------------------------------------------------
    # Construcción privada
    # ------------------------------------------------------------------

    def __init__(self) -> None:
        self._data: dict[str, Any] = {}
        self._load()

    # ------------------------------------------------------------------
    # API pública
    # ------------------------------------------------------------------

    def get(self, key: str, default: Any = None) -> Any:
        """Devuelve el valor para key, o default si no existe."""
        return self._data.get(key, default)

    def set(self, key: str, value: Any) -> None:
        """Guarda el valor y persiste inmediatamente en disco."""
        self._data[key] = value
        self._save()

    def set_many(self, updates: dict[str, Any]) -> None:
        """Aplica múltiples cambios y guarda una sola vez."""
        self._data.update(updates)
        self._save()

    def get_all(self) -> dict[str, Any]:
        """Devuelve una copia del dict completo de configuración."""
        return dict(self._data)

    def reset_defaults(self) -> None:
        """Restaura todos los valores de fábrica y guarda."""
        self._data = dict(DEFAULTS)
        self._save()

    # ------------------------------------------------------------------
    # Persistencia interna
    # ------------------------------------------------------------------

    def _load(self) -> None:
        """Carga settings.json. Si no existe crea uno con los defaults."""
        if _SETTINGS_PATH.exists():
            try:
                with _SETTINGS_PATH.open("r", encoding="utf-8") as fh:
                    loaded = json.load(fh)
                # Combinar: defaults como base, luego valores guardados encima.
                # Así las claves nuevas (versiones futuras) tienen su default.
                self._data = {**DEFAULTS, **loaded}
            except (json.JSONDecodeError, OSError):
                # Archivo corrupto: usar defaults y reescribir
                self._data = dict(DEFAULTS)
                self._save()
        else:
            self._data = dict(DEFAULTS)
            self._save()

    def _save(self) -> None:
        """Escribe settings.json de forma atómica (escritura + rename)."""
        tmp = _SETTINGS_PATH.with_suffix(".json.tmp")
        try:
            with tmp.open("w", encoding="utf-8") as fh:
                json.dump(self._data, fh, indent=2, ensure_ascii=False)
            tmp.replace(_SETTINGS_PATH)
        except OSError:
            # Guardar sin atomicidad como fallback
            try:
                with _SETTINGS_PATH.open("w", encoding="utf-8") as fh:
                    json.dump(self._data, fh, indent=2, ensure_ascii=False)
            except OSError:
                pass  # Sin acceso a disco: la sesión continúa en memoria
