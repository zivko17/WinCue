# =============================================================================
# cue_model.py — Capa de Datos Pura (Modelo en MVC)
# =============================================================================
# Responsabilidad: Define la estructura de datos Cue y la clase CueModel que
#   almacena y gestiona la colección de cues.
#
# Hilos permitidos: SOLO el hilo principal (main thread).
#   Esta clase NO tiene conocimiento de Qt ni de la UI. Es Python puro.
#   El controlador (MainController) es el único que debe llamar a sus métodos
#   de escritura (set_state, set_latency). La vista NUNCA toca este modelo
#   directamente.
# =============================================================================

from __future__ import annotations

import json
from dataclasses import dataclass, field
from typing import Optional

from PySide6.QtCore import QModelIndex


# ---------------------------------------------------------------------------
# Constantes de tipo y estado
# ---------------------------------------------------------------------------

CUE_TYPES: tuple[str, ...] = ("AUDIO", "VIDEO", "WAIT", "SCRIPT")

CUE_STATES: tuple[str, ...] = ("IDLE", "LOADED", "PLAYING", "STOPPED", "ERROR")

# Colores asociados a cada estado (usados por la vista para renderizar)
STATE_COLORS: dict[str, str] = {
    "IDLE":    "#888888",
    "LOADED":  "#3498db",
    "PLAYING": "#2ecc71",
    "STOPPED": "#ffffff",
    "ERROR":   "#e74c3c",
}


# ---------------------------------------------------------------------------
# Dataclass Cue
# ---------------------------------------------------------------------------

@dataclass
class Cue:
    """Unidad atómica de reproducción en la cue-list.

    Todos los campos son mutables por diseño: el estado y la latencia
    se actualizan durante la ejecución. El id es estable y único.
    """
    id:         int
    type:       str           # Uno de CUE_TYPES
    name:       str
    duration:   float         # Duración nominal en segundos
    state:      str = "IDLE"  # Uno de CUE_STATES
    latency_ms: Optional[float] = None  # Medida real en ms; None = sin medir
    file_path:  str = ""               # Ruta al archivo de audio/video (Paso 2+)

    def __post_init__(self) -> None:
        if self.type not in CUE_TYPES:
            raise ValueError(f"Tipo de cue inválido: {self.type!r}")
        if self.state not in CUE_STATES:
            raise ValueError(f"Estado de cue inválido: {self.state!r}")
        if self.duration < 0:
            raise ValueError("La duración no puede ser negativa.")

    # ------------------------------------------------------------------
    # Serialización JSON (state y latency_ms son runtime, no se persisten)
    # ------------------------------------------------------------------

    def to_dict(self) -> dict:
        return {
            "id":        self.id,
            "type":      self.type,
            "name":      self.name,
            "duration":  self.duration,
            "file_path": self.file_path,
        }

    @staticmethod
    def from_dict(d: dict) -> "Cue":
        """Reconstruye un Cue desde un dict JSON. Lanza ValueError si hay campos inválidos."""
        return Cue(
            id=int(d["id"]),
            type=str(d["type"]),
            name=str(d["name"]),
            duration=float(d["duration"]),
            file_path=str(d.get("file_path", "")),
            state="IDLE",       # siempre IDLE al cargar — el engine no está listo
            latency_ms=None,    # se medirá en la primera reproducción
        )


# ---------------------------------------------------------------------------
# CueModel
# ---------------------------------------------------------------------------

class CueModel:
    """Colección ordenada de Cue. Sin Qt, sin UI.

    Proporciona acceso de lectura (propiedad .cues) y métodos de escritura
    acotados que el controlador usa para actualizar el estado del sistema.
    """

    def __init__(self) -> None:
        self._cues: list[Cue] = []

    # ------------------------------------------------------------------
    # Acceso de lectura
    # ------------------------------------------------------------------

    @property
    def cues(self) -> list[Cue]:
        """Lista de cues en orden de ejecución. Solo lectura desde la vista."""
        return self._cues

    def count(self) -> int:
        return len(self._cues)

    def get_cue(self, index: int) -> Optional[Cue]:
        """Devuelve el Cue en la posición dada o None si está fuera de rango."""
        if 0 <= index < len(self._cues):
            return self._cues[index]
        return None

    # ------------------------------------------------------------------
    # Mutación (solo el controlador llama a estos métodos)
    # ------------------------------------------------------------------

    def add_cue(self, cue: Cue) -> None:
        self._cues.append(cue)
        # No hay Qt aquí — la vista se actualiza
        # desde MainWindow con _append_row_to_qt_model

    def set_state(self, index: int, state: str) -> None:
        """Actualiza el estado de un cue. Valida el valor antes de asignar."""
        if state not in CUE_STATES:
            raise ValueError(f"Estado inválido: {state!r}")
        if 0 <= index < len(self._cues):
            self._cues[index].state = state

    def set_latency(self, index: int, latency_ms: float) -> None:
        """Registra la latencia medida de un cue (se llenará en Paso 2)."""
        if 0 <= index < len(self._cues):
            self._cues[index].latency_ms = latency_ms

    # ------------------------------------------------------------------
    # Datos de ejemplo (hardcodeados para el arranque inicial)
    # ------------------------------------------------------------------

    def remove_cue(self, row: int) -> None:
        """Elimina el cue en la posición dada. No-op si el índice está fuera de rango."""
        if 0 <= row < len(self._cues):
            self._cues.pop(row)
        # Nota: la notificación Qt (removeRow) la emite MainWindow._on_cues_removed
        # sobre self._qt_model (QStandardItemModel), no aquí.

    def update_cue(self, row: int, cue: Cue) -> None:
        if 0 <= row < len(self._cues):
            self._cues[row] = cue

    def move_cue(self, from_row: int, to_row: int) -> None:
        """Mueve el cue de from_row a to_row reordenando la lista."""
        count = len(self._cues)
        if from_row == to_row or not (0 <= from_row < count and 0 <= to_row < count):
            return
        cue = self._cues.pop(from_row)
        self._cues.insert(to_row, cue)

    def replace_all(self, cues: list[Cue]) -> None:
        """Reemplaza toda la lista en la instancia existente.

        Las referencias externas al objeto CueModel siguen siendo válidas.
        Llamar SOLO desde el controlador, nunca desde la vista.
        """
        self._cues.clear()
        self._cues.extend(cues)

    def to_json(self) -> str:
        """Serializa la lista de cues a JSON con sangría legible."""
        payload = {
            "version": 1,
            "cues": [c.to_dict() for c in self._cues],
        }
        return json.dumps(payload, indent=2, ensure_ascii=False)

    @staticmethod
    def from_json(text: str) -> list[Cue]:
        """Deserializa JSON y devuelve la lista de cues validados.

        Lanza ValueError con mensaje descriptivo si el formato es inválido,
        hay IDs duplicados o algún campo tiene un valor fuera de rango.
        Los IDs duplicados son detectados explícitamente para evitar corrupción
        silenciosa al mezclar shows de distintas sesiones.
        """
        try:
            payload = json.loads(text)
        except json.JSONDecodeError as exc:
            raise ValueError(f"JSON inválido: {exc}") from exc

        # Soportar tanto lista directa como wrapper {"version":1, "cues":[...]}
        raw_list = payload if isinstance(payload, list) else payload.get("cues", [])
        if not isinstance(raw_list, list):
            raise ValueError("El archivo JSON debe contener una lista de cues.")

        seen_ids: set[int] = set()
        cues: list[Cue] = []
        for i, item in enumerate(raw_list):
            try:
                c = Cue.from_dict(item)
            except (KeyError, TypeError, ValueError) as exc:
                raise ValueError(f"Cue #{i+1}: {exc}") from exc
            if c.id in seen_ids:
                raise ValueError(f"ID duplicado en el archivo: {c.id}")
            seen_ids.add(c.id)
            cues.append(c)
        return cues

    @staticmethod
    def create_sample_data() -> list[Cue]:
        """Genera 10 cues ficticios con mezcla de tipos y estados."""
        return [
            Cue(id=1,  type="AUDIO",  name="Intro Music — Fade In",         duration=15.0,  state="IDLE"),
            Cue(id=2,  type="VIDEO",  name="Opening Title Card",             duration=5.0,   state="LOADED"),
            Cue(id=3,  type="WAIT",   name="Espera entrada actores",         duration=3.0,   state="IDLE"),
            Cue(id=4,  type="AUDIO",  name="Ambiente escena 1 — Loop",       duration=30.0,  state="IDLE"),
            Cue(id=5,  type="SCRIPT", name="Trigger lighting cue #1",        duration=0.1,   state="IDLE"),
            Cue(id=6,  type="VIDEO",  name="Transición acto I → II",         duration=2.5,   state="IDLE"),
            Cue(id=7,  type="AUDIO",  name="Música clímax — Act II",         duration=45.0,  state="IDLE"),
            Cue(id=8,  type="WAIT",   name="Blackout pause",                 duration=1.0,   state="IDLE"),
            Cue(id=9,  type="SCRIPT", name="Reset all fixtures",             duration=0.5,   state="ERROR"),
            Cue(id=10, type="AUDIO",  name="Salida — Créditos finales",      duration=120.0, state="IDLE"),
        ]
