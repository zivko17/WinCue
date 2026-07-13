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

# Tipos de cue soportados (estilo QLab).
#   AUDIO / VIDEO  — reproducción de media (con volumen, fades, loop)
#   WAIT           — espera temporizada (útil en cadenas auto-follow)
#   FADE           — modifica el volumen de otro cue a lo largo del tiempo
#   START          — dispara otro cue por id
#   STOP           — detiene otro cue (o todos si no hay target)
#   SCRIPT         — ejecuta un comando/script del sistema
#   MEMO           — comentario, sin acción (organización de la lista)
CUE_TYPES: tuple[str, ...] = (
    "AUDIO", "VIDEO", "WAIT", "FADE", "START", "STOP", "SCRIPT", "MEMO", "GROUP",
)

# Modos de un cue GROUP (estilo QLab):
#   TIMELINE — dispara TODOS los hijos a la vez; playhead salta tras el grupo.
#   ENTER    — dispara el primer hijo y el playhead entra al grupo (organizativo).
#   GO_NEXT  — dispara el primer hijo; el playhead salta tras el grupo (el resto
#              de hijos solo corren si están encadenados con auto-continue/follow).
#   RANDOM   — dispara un hijo aleatorio (round-robin); playhead salta tras el grupo.
GROUP_MODES: tuple[str, ...] = ("TIMELINE", "ENTER", "GO_NEXT", "RANDOM")

# Comportamiento al re-disparar un cue que ya está sonando (Triggers → Second trigger)
RETRIGGER_MODES: tuple[str, ...] = (
    "NONE",          # ignora el segundo disparo (por defecto)
    "PANIC",         # fade-out y parada
    "STOP",          # parada
    "HARD_STOP",     # parada inmediata
    "HARD_RESTART",  # parada y reinicio inmediato
)

# Ámbito de "Fade & stop others over time"
FADE_SCOPES: tuple[str, ...] = ("PEERS", "LIST", "ALL")

# Tipos que apuntan a otro cue (necesitan target_id).
TARGETING_TYPES: frozenset[str] = frozenset({"FADE", "START", "STOP"})

# Tipos con acción de reproducción de media.
MEDIA_TYPES: frozenset[str] = frozenset({"AUDIO", "VIDEO"})

CUE_STATES: tuple[str, ...] = ("IDLE", "LOADED", "PLAYING", "STOPPED", "ERROR")

# Modos de continuación (cómo se encadena con el cue siguiente al dispararse):
#   NONE          — GO manual: el playhead avanza pero nada se auto-dispara
#   AUTO_CONTINUE — el siguiente cue se dispara tras post_wait (desde inicio)
#   AUTO_FOLLOW   — el siguiente cue se dispara cuando ESTE termina su acción
CONTINUE_MODES: tuple[str, ...] = ("NONE", "AUTO_CONTINUE", "AUTO_FOLLOW")

# Colores asociados a cada estado (usados por la vista para renderizar)
STATE_COLORS: dict[str, str] = {
    "IDLE":    "#888888",
    "LOADED":  "#3498db",
    "PLAYING": "#2ecc71",
    "STOPPED": "#ffffff",
    "ERROR":   "#e74c3c",
}

# Paleta de colores de etiqueta para los cues (estilo QLab). Clave vacía = sin color.
CUE_COLORS: dict[str, str] = {
    "":       "",          # sin color
    "red":    "#c0392b",
    "orange": "#d35400",
    "yellow": "#c9a227",
    "green":  "#27865a",
    "blue":   "#2a6fb0",
    "purple": "#7d4b9a",
}


# ---------------------------------------------------------------------------
# Dataclass Cue
# ---------------------------------------------------------------------------

@dataclass
class Cue:
    """Unidad atómica de reproducción en la cue-list (modelo estilo QLab).

    Todos los campos son mutables por diseño: el estado y la latencia
    se actualizan durante la ejecución. El id es estable y único.

    Propiedades de temporización (estilo QLab):
      · pre_wait      — retardo entre disparo y comienzo de la acción
      · duration      — longitud de la acción (audio/vídeo/wait/fade)
      · post_wait     — segundo temporizador usado por AUTO_CONTINUE
      · continue_mode — encadenado con el cue siguiente (ver CONTINUE_MODES)

    Propiedades de media:
      · volume        — ganancia lineal 0.0–1.0 (1.0 = 0 dB)
      · fade_in       — fundido de entrada al arrancar (s)
      · fade_out      — fundido de salida antes del final (s)
      · loop          — repetir en bucle hasta STOP

    Propiedades de cues que apuntan a otro cue (FADE/START/STOP):
      · target_id            — id del cue objetivo
      · fade_stop_on_done    — (FADE) detener el objetivo al terminar en 0
    """
    id:         int
    type:       str           # Uno de CUE_TYPES
    name:       str
    duration:   float         # Duración nominal en segundos
    state:      str = "IDLE"  # Uno de CUE_STATES
    latency_ms: Optional[float] = None  # Medida real en ms; None = sin medir
    file_path:  str = ""               # Ruta al archivo de audio/video

    # ── Numeración / organización ─────────────────────────────────────
    number:     str = ""      # Número visible (QLab). Vacío → se usa str(id)
    color:      str = ""      # Clave en CUE_COLORS ("" = sin color)
    notes:      str = ""      # Nota libre del operador
    armed:      bool = True   # Si False, GO lo salta sin dispararlo

    # ── Temporización / encadenado ────────────────────────────────────
    pre_wait:      float = 0.0
    post_wait:     float = 0.0
    continue_mode: str   = "NONE"

    # ── Media ─────────────────────────────────────────────────────────
    volume:   float = 1.0
    fade_in:  float = 0.0
    fade_out: float = 0.0
    loop:     bool  = False

    # Geometría (vídeo/imagen) y recorte (audio/vídeo), estilo QLab
    scale:      float = 100.0   # escala en % (100 = ajuste a la salida)
    trim_start: float = 0.0     # punto de entrada dentro del archivo (s)
    trim_end:   float = 0.0     # punto de salida (s); 0 = hasta el final

    # ── Time & Loops ──────────────────────────────────────────────────
    play_count:     int   = 1       # nº de repeticiones (1 = una vez)
    rate:           float = 1.0     # velocidad de reproducción (1.0 = normal)
    preserve_pitch: bool  = True    # mantener tono al cambiar la velocidad

    # ── Display & Geometry (vídeo) ────────────────────────────────────
    geo_mode:        str   = "FULL"    # FULL | CUSTOM
    layer:           str   = "top"     # top | bottom | 1..999 (fase 2: compositor)
    opacity:         float = 100.0     # 0..100 (fase 2: necesita compositor)
    preserve_aspect: bool  = True      # keepaspect en modo Full Surface
    translate_x:     float = 0.0       # px desde el centro (Custom)
    translate_y:     float = 0.0
    scale_x:         float = 1.0       # multiplicador (Custom)
    scale_y:         float = 1.0
    anchor_x:        float = 0.0
    anchor_y:        float = 0.0
    rotate_x:        float = 0.0       # grados (fase 2: solo Z ~90° se aplica)
    rotate_y:        float = 0.0
    rotate_z:        float = 0.0

    # ── Triggers extra (MIDI / Wall-clock / Timecode / Duck / release) ─
    midi_enabled:  bool = False
    midi_type:     str  = "note_on"   # note_on | note_off | control_change | program_change
    midi_channel:  int  = 1
    midi_note:     int  = 0
    midi_velocity: int  = -1          # -1 = "any"
    wallclock_enabled: bool = False
    wallclock_time:    str  = ""      # "HH:MM:SS" (24h)
    wallclock_days:    int  = 127     # bitmask L..D (127 = todos los días)
    timecode_enabled:  bool = False
    timecode_value:    str  = ""      # "HH:MM:SS:FF" o "HH:MM:SS.mmm"
    duck_enabled: bool  = False
    duck_level:   float = 0.0         # dB (negativo = duck, positivo = boost)
    duck_time:    float = 1.0         # tiempo del fundido de duck/boost (s)
    second_trigger_on_release: bool = False

    # ── Cues que apuntan a otro cue ───────────────────────────────────
    target_id:         Optional[int] = None
    fade_stop_on_done: bool = True

    # Modo GO "per_cue": si True, al dispararse detiene el resto de cues activos
    stop_others: bool = False

    # ── Grupos (jerarquía) ────────────────────────────────────────────
    parent_id:  Optional[int] = None    # id del GROUP contenedor (None = raíz)
    group_mode: str = "ENTER"           # solo para type == "GROUP" (ver GROUP_MODES)
    collapsed:  bool = False            # solo GROUP: hijos ocultos en la tabla

    # ── Inspector: Básicos extra ──────────────────────────────────────
    flagged:    bool = False            # marcado para revisar (icono ⚑)
    auto_load:  bool = False            # pre-cargar tras reproducir el cue anterior

    # ── Inspector: Triggers ───────────────────────────────────────────
    hotkey:            str = ""         # tecla que dispara el cue (aunque no sea standby)
    retrigger:         str = "NONE"     # comportamiento al re-disparar (RETRIGGER_MODES)
    fade_others:       bool = False     # al dispararse, funde y para otros cues
    fade_others_time:  float = 3.0      # tiempo del fundido (s)
    fade_others_scope: str = "PEERS"    # ámbito (FADE_SCOPES)

    def __post_init__(self) -> None:
        if self.type not in CUE_TYPES:
            raise ValueError(f"Tipo de cue inválido: {self.type!r}")
        if self.state not in CUE_STATES:
            raise ValueError(f"Estado de cue inválido: {self.state!r}")
        if self.duration < 0:
            raise ValueError("La duración no puede ser negativa.")
        if self.continue_mode not in CONTINUE_MODES:
            self.continue_mode = "NONE"
        # Clamp defensivo de valores numéricos
        self.pre_wait  = max(0.0, float(self.pre_wait))
        self.post_wait = max(0.0, float(self.post_wait))
        self.fade_in   = max(0.0, float(self.fade_in))
        self.fade_out  = max(0.0, float(self.fade_out))
        self.volume    = max(0.0, min(1.0, float(self.volume)))

    # ------------------------------------------------------------------
    # Utilidades
    # ------------------------------------------------------------------

    @property
    def display_number(self) -> str:
        """Número mostrado en la lista: el explícito o el id como fallback."""
        return self.number if self.number else str(self.id)

    def is_media(self) -> bool:
        return self.type in MEDIA_TYPES

    def targets_other(self) -> bool:
        return self.type in TARGETING_TYPES

    # ------------------------------------------------------------------
    # Serialización JSON (state y latency_ms son runtime, no se persisten)
    # ------------------------------------------------------------------

    def to_dict(self) -> dict:
        return {
            "id":                self.id,
            "type":              self.type,
            "name":              self.name,
            "duration":          self.duration,
            "file_path":         self.file_path,
            "number":            self.number,
            "color":             self.color,
            "notes":             self.notes,
            "armed":             self.armed,
            "pre_wait":          self.pre_wait,
            "post_wait":         self.post_wait,
            "continue_mode":     self.continue_mode,
            "volume":            self.volume,
            "fade_in":           self.fade_in,
            "fade_out":          self.fade_out,
            "loop":              self.loop,
            "target_id":         self.target_id,
            "fade_stop_on_done": self.fade_stop_on_done,
            "stop_others":       self.stop_others,
            "parent_id":         self.parent_id,
            "group_mode":        self.group_mode,
            "collapsed":         self.collapsed,
            "flagged":           self.flagged,
            "auto_load":         self.auto_load,
            "hotkey":            self.hotkey,
            "retrigger":         self.retrigger,
            "fade_others":       self.fade_others,
            "fade_others_time":  self.fade_others_time,
            "fade_others_scope": self.fade_others_scope,
            "scale":             self.scale,
            "trim_start":        self.trim_start,
            "trim_end":          self.trim_end,
            "play_count":        self.play_count,
            "rate":              self.rate,
            "preserve_pitch":    self.preserve_pitch,
            "midi_enabled":      self.midi_enabled,
            "midi_type":         self.midi_type,
            "midi_channel":      self.midi_channel,
            "midi_note":         self.midi_note,
            "midi_velocity":     self.midi_velocity,
            "wallclock_enabled": self.wallclock_enabled,
            "wallclock_time":    self.wallclock_time,
            "wallclock_days":    self.wallclock_days,
            "timecode_enabled":  self.timecode_enabled,
            "timecode_value":    self.timecode_value,
            "duck_enabled":      self.duck_enabled,
            "duck_level":        self.duck_level,
            "duck_time":         self.duck_time,
            "second_trigger_on_release": self.second_trigger_on_release,
            "geo_mode":          self.geo_mode,
            "layer":             self.layer,
            "opacity":           self.opacity,
            "preserve_aspect":   self.preserve_aspect,
            "translate_x":       self.translate_x,
            "translate_y":       self.translate_y,
            "scale_x":           self.scale_x,
            "scale_y":           self.scale_y,
            "anchor_x":          self.anchor_x,
            "anchor_y":          self.anchor_y,
            "rotate_x":          self.rotate_x,
            "rotate_y":          self.rotate_y,
            "rotate_z":          self.rotate_z,
        }

    @staticmethod
    def from_dict(d: dict) -> "Cue":
        """Reconstruye un Cue desde un dict JSON. Lanza ValueError si hay campos inválidos.

        Retrocompatible: los shows antiguos solo tienen id/type/name/duration/
        file_path; el resto de campos toman sus valores por defecto.
        """
        tgt = d.get("target_id", None)
        return Cue(
            id=int(d["id"]),
            type=str(d["type"]),
            name=str(d["name"]),
            duration=float(d["duration"]),
            file_path=str(d.get("file_path", "")),
            number=str(d.get("number", "")),
            color=str(d.get("color", "")),
            notes=str(d.get("notes", "")),
            armed=bool(d.get("armed", True)),
            pre_wait=float(d.get("pre_wait", 0.0)),
            post_wait=float(d.get("post_wait", 0.0)),
            continue_mode=str(d.get("continue_mode", "NONE")),
            volume=float(d.get("volume", 1.0)),
            fade_in=float(d.get("fade_in", 0.0)),
            fade_out=float(d.get("fade_out", 0.0)),
            loop=bool(d.get("loop", False)),
            target_id=(int(tgt) if tgt is not None else None),
            fade_stop_on_done=bool(d.get("fade_stop_on_done", True)),
            stop_others=bool(d.get("stop_others", False)),
            parent_id=(int(pid) if (pid := d.get("parent_id")) is not None else None),
            group_mode=str(d.get("group_mode", "ENTER")),
            collapsed=bool(d.get("collapsed", False)),
            flagged=bool(d.get("flagged", False)),
            auto_load=bool(d.get("auto_load", False)),
            hotkey=str(d.get("hotkey", "")),
            retrigger=str(d.get("retrigger", "NONE")),
            fade_others=bool(d.get("fade_others", False)),
            fade_others_time=float(d.get("fade_others_time", 3.0)),
            fade_others_scope=str(d.get("fade_others_scope", "PEERS")),
            scale=float(d.get("scale", 100.0)),
            trim_start=float(d.get("trim_start", 0.0)),
            trim_end=float(d.get("trim_end", 0.0)),
            play_count=int(d.get("play_count", 1)),
            rate=float(d.get("rate", 1.0)),
            preserve_pitch=bool(d.get("preserve_pitch", True)),
            midi_enabled=bool(d.get("midi_enabled", False)),
            midi_type=str(d.get("midi_type", "note_on")),
            midi_channel=int(d.get("midi_channel", 1)),
            midi_note=int(d.get("midi_note", 0)),
            midi_velocity=int(d.get("midi_velocity", -1)),
            wallclock_enabled=bool(d.get("wallclock_enabled", False)),
            wallclock_time=str(d.get("wallclock_time", "")),
            wallclock_days=int(d.get("wallclock_days", 127)),
            timecode_enabled=bool(d.get("timecode_enabled", False)),
            timecode_value=str(d.get("timecode_value", "")),
            duck_enabled=bool(d.get("duck_enabled", False)),
            duck_level=float(d.get("duck_level", 0.0)),
            duck_time=float(d.get("duck_time", 1.0)),
            second_trigger_on_release=bool(d.get("second_trigger_on_release", False)),
            geo_mode=str(d.get("geo_mode", "FULL")),
            layer=str(d.get("layer", "top")),
            opacity=float(d.get("opacity", 100.0)),
            preserve_aspect=bool(d.get("preserve_aspect", True)),
            translate_x=float(d.get("translate_x", 0.0)),
            translate_y=float(d.get("translate_y", 0.0)),
            scale_x=float(d.get("scale_x", 1.0)),
            scale_y=float(d.get("scale_y", 1.0)),
            anchor_x=float(d.get("anchor_x", 0.0)),
            anchor_y=float(d.get("anchor_y", 0.0)),
            rotate_x=float(d.get("rotate_x", 0.0)),
            rotate_y=float(d.get("rotate_y", 0.0)),
            rotate_z=float(d.get("rotate_z", 0.0)),
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

    def get_by_id(self, cue_id: int) -> Optional[Cue]:
        """Devuelve el Cue con el id dado, o None si no existe."""
        for c in self._cues:
            if c.id == cue_id:
                return c
        return None

    def index_of_id(self, cue_id: int) -> int:
        """Devuelve el índice de fila del cue con el id dado, o -1."""
        for i, c in enumerate(self._cues):
            if c.id == cue_id:
                return i
        return -1

    def next_id(self) -> int:
        """Devuelve un id nuevo, único, mayor que cualquiera existente."""
        return max((c.id for c in self._cues), default=0) + 1

    def next_number(self) -> str:
        """Sugiere el siguiente número de cue como string incremental simple."""
        nums: list[int] = []
        for c in self._cues:
            token = c.number if c.number else str(c.id)
            try:
                nums.append(int(float(token)))
            except (ValueError, TypeError):
                continue
        return str(max(nums, default=0) + 1)

    # ------------------------------------------------------------------
    # Jerarquía de grupos
    # ------------------------------------------------------------------

    def depth(self, cue: Cue) -> int:
        """Profundidad de anidamiento del cue (0 = raíz)."""
        d = 0
        pid = cue.parent_id
        guard = 0
        while pid is not None and guard < 100:
            parent = self.get_by_id(pid)
            if parent is None:
                break
            d += 1
            pid = parent.parent_id
            guard += 1
        return d

    def is_descendant(self, cue: Cue, group_id: int) -> bool:
        """True si `cue` está dentro del grupo `group_id` (a cualquier nivel)."""
        pid = cue.parent_id
        guard = 0
        while pid is not None and guard < 100:
            if pid == group_id:
                return True
            parent = self.get_by_id(pid)
            if parent is None:
                break
            pid = parent.parent_id
            guard += 1
        return False

    def direct_children(self, group_id: int) -> list[Cue]:
        """Hijos directos del grupo, en orden de lista."""
        return [c for c in self._cues if c.parent_id == group_id]

    def direct_child_indices(self, group_id: int) -> list[int]:
        """Índices de fila de los hijos directos del grupo, en orden."""
        return [i for i, c in enumerate(self._cues) if c.parent_id == group_id]

    def descendants(self, group_id: int) -> list[Cue]:
        """Todos los descendientes (cualquier nivel), en orden de lista."""
        return [c for c in self._cues if self.is_descendant(c, group_id)]

    def group_extent(self, group_index: int) -> int:
        """Índice de la primera fila DESPUÉS del grupo (tras todos sus descendientes).

        Asume contigüidad: los descendientes van inmediatamente tras el grupo.
        """
        if not (0 <= group_index < len(self._cues)):
            return len(self._cues)
        group = self._cues[group_index]
        end = group_index + 1
        for i in range(group_index + 1, len(self._cues)):
            if self.is_descendant(self._cues[i], group.id):
                end = i + 1
            else:
                break
        return end

    def is_ancestor_collapsed(self, cue: Cue) -> bool:
        """True si algún grupo ancestro del cue está colapsado (debe ocultarse)."""
        pid = cue.parent_id
        guard = 0
        while pid is not None and guard < 100:
            parent = self.get_by_id(pid)
            if parent is None:
                break
            if parent.type == "GROUP" and parent.collapsed:
                return True
            pid = parent.parent_id
            guard += 1
        return False

    # ------------------------------------------------------------------
    # Mutación (solo el controlador llama a estos métodos)
    # ------------------------------------------------------------------

    def add_cue(self, cue: Cue) -> None:
        self._cues.append(cue)
        # No hay Qt aquí — la vista se actualiza
        # desde MainWindow con _append_row_to_qt_model

    def insert_cue(self, row: int, cue: Cue) -> None:
        """Inserta un cue en la posición dada (clamp a los límites)."""
        row = max(0, min(row, len(self._cues)))
        self._cues.insert(row, cue)

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
