# =============================================================================
# cue_controller.py — Controlador de reproducción estilo QLab (C en MVC)
# =============================================================================
#
# PARADIGMA
# ─────────
# A diferencia de la versión anterior (mono-activa, secuencial: GO cortaba el
# cue anterior), este controlador implementa el modelo de QLab:
#
#   · PLAYHEAD (standby): índice del cue que GO disparará a continuación.
#   · CUES CONCURRENTES: GO dispara el cue del playhead y AVANZA el playhead,
#     SIN cortar los cues que ya están sonando. Varios cues suenan a la vez.
#   · TEMPORIZACIÓN por cue: pre_wait → acción → (post_wait). El encadenado con
#     el cue siguiente lo decide continue_mode:
#         NONE          — nada automático (GO manual)
#         AUTO_CONTINUE — el siguiente se dispara tras pre_wait+post_wait
#         AUTO_FOLLOW   — el siguiente se dispara cuando ESTE termina su acción
#   · TIPOS: AUDIO/VIDEO (media), WAIT (espera), FADE/START/STOP (apuntan a otro
#     cue), SCRIPT (comando), MEMO (comentario, sin acción).
#   · TRANSPORT: GO, Stop All (Esc), Panic (fade-out global), Pause/Resume.
#
# HILOS
# ─────
# Solo el main thread. Todos los QTimer disparan en el event loop de Qt. Nunca
# sleep()/wait() ni bucles bloqueantes: el hilo principal queda siempre libre.
# =============================================================================

from __future__ import annotations

import random
import subprocess
import sys
from pathlib import Path
from time import perf_counter
from typing import TYPE_CHECKING, Optional

from PySide6.QtCore import QObject, QTimer, Signal
from PySide6.QtWidgets import QFileDialog

from audio_engine import AudioEngine
from video_engine import VideoEngine
from cue_model import Cue, CueModel
from media_import import MediaImporter
from settings_manager import SettingsManager

if TYPE_CHECKING:
    from main_window import MainWindow


# Duración por defecto del fade global de Panic (segundos)
PANIC_FADE: float = 1.5


class _Running:
    """Info de un cue en ejecución, para timers y para el panel de activos."""

    __slots__ = (
        "cue_id", "index", "kind", "name", "number", "color",
        "t_start", "duration", "timers", "manual_stop", "advance_chain",
    )

    def __init__(self, cue: Cue, index: int, advance_chain: bool) -> None:
        self.cue_id: int = cue.id
        self.index: int = index
        self.kind: str = cue.type
        self.name: str = cue.name
        self.number: str = cue.display_number
        self.color: str = cue.color
        self.t_start: float = perf_counter()
        self.duration: float = 0.0     # 0 = indeterminado (fin natural del media)
        self.timers: list[QTimer] = []
        self.manual_stop: bool = False
        self.advance_chain: bool = advance_chain


class MainController(QObject):
    """Controlador principal: playhead + cues concurrentes (modelo QLab)."""

    active_index_changed: Signal = Signal(int)   # emite el índice del playhead

    # ------------------------------------------------------------------
    # Construcción
    # ------------------------------------------------------------------

    def __init__(self, model: CueModel, view: "MainWindow") -> None:
        super().__init__()
        self._model: CueModel = model
        self._view: "MainWindow" = view

        self._playhead: int = 0
        self._paused: bool = False

        # Motores de reproducción
        self._audio: AudioEngine = AudioEngine()
        self._audio.latency_report.connect(self._on_latency_report)
        self._audio.error_occurred.connect(self._on_audio_error)
        self._audio.state_changed.connect(self._on_audio_state_changed)
        self._audio.level_report.connect(self._on_level_report)

        self._video: VideoEngine = VideoEngine(view.video_container)
        self._video.latency_report.connect(self._on_latency_report)
        self._video.error_occurred.connect(self._on_video_error)
        self._video.state_changed.connect(self._on_video_state_changed)

        # Cues en ejecución (cue_id → _Running)
        self._running: dict[int, _Running] = {}

        # Estado round-robin de grupos RANDOM: group_id → set de hijos ya disparados
        self._rr_state: dict[int, set] = {}

        # Timers de continuación (AUTO_CONTINUE): NO se cancelan al terminar el
        # cue de origen (deben disparar aunque la acción acabe antes del post_wait);
        # solo los cancela Stop All / Panic.
        self._chain_timers: list[QTimer] = []

        # Tick de 100 ms para el panel de activos y el countdown
        self._tick: QTimer = QTimer(self)
        self._tick.setInterval(100)
        self._tick.timeout.connect(self._on_tick)
        self._tick.start()

        # Timer del monitor de PROGRAMA (frame del vídeo en salida ~cada 1.2 s)
        self._pgm_timer: QTimer = QTimer(self)
        self._pgm_timer.setInterval(1200)
        self._pgm_timer.timeout.connect(self._update_program_monitor)
        self._pgm_timer.start()

        # Señales de la vista → slots del controlador
        self._view.cue_add_requested.connect(self.add_new_cue)
        self._view.cues_from_files_requested.connect(self.add_cues_from_files)

        self._set_playhead(0)

    # ==================================================================
    # TRANSPORT — API pública conectada a la vista
    # ==================================================================

    def on_go_pressed(self) -> None:
        """GO: dispara el cue del playhead y avanza, sin cortar los activos."""
        idx = self._playhead
        cue = self._model.get_cue(idx)
        if cue is None:
            return
        self._go_cue(idx)

    def on_stop_pressed(self) -> None:
        """Stop All (Esc/S): detiene todos los cues en ejecución.

        No mueve el playhead. Cancela timers y silencia los motores.
        """
        self._stop_everything(fade=0.0)
        self._view.flash_status("■ STOP ALL — todos los cues detenidos")

    def on_panic(self) -> None:
        """Panic: fundido de salida global y parada de todo."""
        self._stop_everything(fade=PANIC_FADE)
        self._view.flash_status(f"⚠ PANIC — fundido de {PANIC_FADE:.0f}s y parada")

    def on_master_volume(self, value: int) -> None:
        """Volumen master global (0-100) desde el slider del top bar."""
        m = max(0.0, min(1.0, value / 100.0))
        self._audio.set_master_volume(m)
        self._video.set_master_volume(m)

    def on_pause_toggle(self) -> None:
        """Pausa/reanuda todos los motores de reproducción."""
        if not self._running:
            return
        if self._paused:
            self._audio.resume_all()
            self._video.resume_all()
            # reajustar t_start para que el countdown no salte
            dt = perf_counter() - self._pause_t0
            for r in self._running.values():
                r.t_start += dt
            self._paused = False
        else:
            self._audio.pause_all()
            self._video.pause_all()
            self._pause_t0 = perf_counter()
            self._paused = True
        self._view.update_transport(self._paused)

    def on_stop_selected(self, row: int) -> None:
        """Detiene el cue de la fila dada, si está en ejecución."""
        cue = self._model.get_cue(row)
        if cue is None:
            return
        if cue.id in self._running:
            self._end_cue(cue.id, mark_stopped=True)

    def on_prev_pressed(self) -> None:
        """Mueve el playhead al cue anterior (sin disparar)."""
        if self._model.count() == 0:
            return
        self._set_playhead(max(0, self._playhead - 1))

    def on_next_pressed(self) -> None:
        """Mueve el playhead al cue siguiente (sin disparar)."""
        if self._model.count() == 0:
            return
        self._set_playhead(min(self._model.count() - 1, self._playhead + 1))

    def on_jump_to_cue(self, cue_number: int) -> None:
        """Salta el playhead a un cue por posición (1-based). No dispara."""
        index = cue_number - 1
        if 0 <= index < self._model.count():
            self._set_playhead(index)

    def on_set_playhead(self, row: int) -> None:
        """Fija el playhead a la fila dada (p.ej. clic del usuario)."""
        if 0 <= row < self._model.count():
            self._set_playhead(row)

    # ==================================================================
    # NÚCLEO DE DISPARO
    # ==================================================================

    def _go_cue(self, idx: int) -> None:
        """Dispara el cue en idx y avanza el playhead (GO y cadenas)."""
        cue = self._model.get_cue(idx)
        if cue is None:
            return

        if cue.type == "GROUP":
            self._go_group(idx, cue)
            return

        self._set_playhead(idx + 1)
        self._trigger(idx, advance_chain=True)

    def _go_group(self, idx: int, cue: Cue) -> None:
        """GO sobre un cue GROUP: avanza el playhead según el modo y dispara."""
        mode = cue.group_mode
        extent = self._model.group_extent(idx)

        if mode == "ENTER":
            # Grupo transparente: dispara el primer hijo como un GO normal
            # (el playhead entra al grupo y avanza al siguiente hijo).
            child_indices = self._model.direct_child_indices(cue.id)
            if child_indices:
                self._go_cue(child_indices[0])
            else:
                self._set_playhead(extent)
            return

        # TIMELINE / GO_NEXT / RANDOM: el playhead salta tras el grupo y el
        # grupo dispara sus hijos (tras su propio pre_wait) en _start_action.
        self._set_playhead(extent)
        self._trigger(idx, advance_chain=True)

    def _fire_group_children(self, idx: int) -> None:
        """Dispara los hijos de un grupo según su modo. Los hijos corren de forma
        independiente (advance_chain=False): no mueven el playhead principal."""
        group = self._model.get_cue(idx)
        if group is None:
            return
        child_indices = self._model.direct_child_indices(group.id)
        mode = group.group_mode

        if mode == "TIMELINE":
            for ci in child_indices:
                self._trigger(ci, advance_chain=False)

        elif mode == "GO_NEXT":
            if child_indices:
                self._trigger(child_indices[0], advance_chain=False)

        elif mode == "RANDOM":
            self._fire_random_child(group, child_indices)

        # ENTER no llega aquí (se gestiona en _go_group como grupo transparente)

    def _fire_random_child(self, group: Cue, child_indices: list) -> None:
        """Round-robin: dispara un hijo armado y no sonando; cada uno una vez por
        ronda antes de repetir."""
        fired = self._rr_state.setdefault(group.id, set())

        def _eligible(require_unfired: bool) -> list:
            out = []
            for ci in child_indices:
                c = self._model.get_cue(ci)
                if c is None or not c.armed or c.id in self._running:
                    continue
                if require_unfired and c.id in fired:
                    continue
                out.append(ci)
            return out

        candidates = _eligible(require_unfired=True)
        if not candidates:
            fired.clear()
            candidates = _eligible(require_unfired=False)
        if not candidates:
            return
        ci = random.choice(candidates)
        c = self._model.get_cue(ci)
        if c is not None:
            fired.add(c.id)
        self._trigger(ci, advance_chain=False)

    def _trigger(self, idx: int, advance_chain: bool) -> None:
        """Dispara el cue en idx: gestiona pre_wait, la acción y el encadenado.

        advance_chain: si True, las continuaciones (auto-continue/follow) mueven
        el playhead (GO normal). Si False, es una sub-secuencia lanzada por un
        cue START que no debe mover el playhead principal.
        """
        cue = self._model.get_cue(idx)
        if cue is None:
            return

        # ── Second trigger: el cue ya está sonando ──────────────────────
        if cue.id in self._running:
            mode = cue.retrigger
            if mode == "NONE":
                return                                  # ignorar el 2º disparo
            if mode in ("STOP", "HARD_STOP"):
                self._end_cue(cue.id, mark_stopped=True)
                return
            if mode == "PANIC":
                self._end_cue(cue.id, mark_stopped=True, fade=PANIC_FADE)
                return
            # HARD_RESTART: continúa abajo (para + reinicia)

        # Registrar el cue como "en curso" (aunque tenga pre_wait)
        run = _Running(cue, idx, advance_chain)
        # Cancelar una instancia previa del mismo cue (retrigger HARD_RESTART)
        if cue.id in self._running:
            self._end_cue(cue.id, mark_stopped=False)
        self._running[cue.id] = run

        # AUTO_CONTINUE: disparar el siguiente tras pre_wait + post_wait.
        # Usa un timer de cadena independiente del ciclo de vida del cue: debe
        # dispararse aunque la acción de este cue termine antes del post_wait.
        if cue.continue_mode == "AUTO_CONTINUE":
            delay = int((cue.pre_wait + cue.post_wait) * 1000)
            self._schedule_chain(delay, lambda: self._continue_next(idx, advance_chain))

        # Acción: inmediata o tras pre_wait
        if cue.pre_wait > 0.0:
            self._schedule(run, int(cue.pre_wait * 1000),
                           lambda: self._start_action(idx))
        else:
            self._start_action(idx)

    def _start_action(self, idx: int) -> None:
        """Ejecuta la acción del cue (ya pasado el pre_wait)."""
        cue = self._model.get_cue(idx)
        if cue is None:
            return
        run = self._running.get(cue.id)
        if run is None:
            return
        run.t_start = perf_counter()

        if not cue.armed:
            # Cue desarmado: sin acción, pero respeta el encadenado.
            self._on_action_end(cue.id)
            return

        kind = cue.type

        # "Fade & stop others over time" (Triggers): funde y para otros cues.
        self._apply_fade_others(cue)

        # Exclusividad de GO (configurable en Settings): al disparar un media
        # puede cortar los demás según el modo elegido.
        if kind in ("AUDIO", "VIDEO"):
            self._apply_go_exclusivity(cue)

        if kind == "AUDIO":
            self._model_state(idx, "PLAYING")
            self._audio.play(cue.id, cue.file_path, volume=cue.volume,
                             fade_in=cue.fade_in, fade_out=cue.fade_out,
                             loop=cue.loop, trim_start=cue.trim_start,
                             trim_end=cue.trim_end, play_count=cue.play_count)
            run.duration = cue.duration
            if cue.duration > 0.0 and not cue.loop:
                self._schedule(run, int(cue.duration * 1000),
                               lambda: self._duration_elapsed(cue.id))
            self._preload_upcoming()

        elif kind == "VIDEO":
            self._model_state(idx, "PLAYING")
            self._resolve_video_output()   # dirigir mpv al monitor de salida
            self._video.play(cue.id, cue.file_path, volume=cue.volume,
                             loop=cue.loop, scale=cue.scale,
                             trim_start=cue.trim_start, trim_end=cue.trim_end,
                             rate=cue.rate, play_count=cue.play_count,
                             preserve_pitch=cue.preserve_pitch,
                             geometry=self._geo_dict(cue))
            self._refresh_video_tally()
            run.duration = cue.duration
            if cue.duration > 0.0 and not cue.loop:
                self._schedule(run, int(cue.duration * 1000),
                               lambda: self._duration_elapsed(cue.id))

        elif kind == "WAIT":
            self._model_state(idx, "PLAYING")
            run.duration = max(0.0, cue.duration)
            self._schedule(run, max(50, int(cue.duration * 1000)),
                           lambda: self._on_action_end(cue.id))

        elif kind == "FADE":
            self._model_state(idx, "PLAYING")
            run.duration = max(0.0, cue.duration)
            self._exec_fade(cue)
            self._schedule(run, max(50, int(cue.duration * 1000)),
                           lambda: self._on_action_end(cue.id))

        elif kind == "START":
            self._exec_start(cue)
            self._on_action_end(cue.id)

        elif kind == "STOP":
            self._exec_stop(cue)
            self._on_action_end(cue.id)

        elif kind == "SCRIPT":
            self._exec_script(cue)
            self._on_action_end(cue.id)

        elif kind == "GROUP":
            self._fire_group_children(idx)
            self._on_action_end(cue.id)

        else:   # MEMO u otros: sin acción
            self._on_action_end(cue.id)

    def _apply_go_exclusivity(self, cue: Cue) -> None:
        """Al disparar un cue de media, corta otros cues según el modo de GO.

        Modos (Settings → Interfaz → Comportamiento de GO):
          · concurrent — audios se solapan; el VÍDEO es exclusivo (una sola salida).
          · sequential — corta TODOS los demás media al disparar uno nuevo.
          · per_cue    — se solapa salvo que este cue tenga stop_others=True.
        El vídeo es SIEMPRE exclusivo (dos vídeos no caben en un único contenedor).
        """
        mode = SettingsManager.instance().get("go_mode", "concurrent")
        stop_all = (mode == "sequential") or (mode == "per_cue" and cue.stop_others)
        for other_id in list(self._running.keys()):
            if other_id == cue.id:
                continue
            other = self._running.get(other_id)
            if other is None:
                continue
            if stop_all or (cue.type == "VIDEO" and other.kind == "VIDEO"):
                self._end_cue(other_id, mark_stopped=True, fade=0.0)

    def _apply_fade_others(self, cue: Cue) -> None:
        """Funde y detiene otros cues activos según el ámbito del cue (Triggers)."""
        if not cue.fade_others:
            return
        scope = cue.fade_others_scope
        t = max(0.0, cue.fade_others_time)
        for other_id in list(self._running.keys()):
            if other_id == cue.id:
                continue
            other = self._model.get_by_id(other_id)
            if other is None:
                continue
            in_scope = (
                scope in ("ALL", "LIST")
                or (scope == "PEERS" and other.parent_id == cue.parent_id)
            )
            if in_scope:
                self._end_cue(other_id, mark_stopped=True, fade=t)

    def trigger_cue_id(self, cue_id: int) -> None:
        """Dispara un cue por id sin mover el playhead (hotkey/START externo)."""
        idx = self._model.index_of_id(cue_id)
        if idx >= 0:
            self._trigger(idx, advance_chain=False)

    def _continue_next(self, idx: int, advance_chain: bool) -> None:
        """Dispara el cue siguiente en una cadena AUTO_CONTINUE/AUTO_FOLLOW."""
        nxt = idx + 1
        if nxt >= self._model.count():
            return
        if advance_chain:
            self._go_cue(nxt)
        else:
            self._trigger(nxt, advance_chain=False)

    def _on_action_end(self, cue_id: int, fade: float = 0.0) -> None:
        """La acción del cue terminó (fin de media/wait/fade o cue instantáneo).

        Marca el cue como terminado y, si es AUTO_FOLLOW, dispara el siguiente.
        `fade` se propaga a _end_cue para respetar el fade_out del media.
        """
        run = self._running.get(cue_id)
        if run is None:
            return
        idx = self._model.index_of_id(cue_id)
        cue = self._model.get_cue(idx)

        # Encadenado AUTO_FOLLOW: el siguiente se dispara al terminar la acción
        if cue is not None and cue.continue_mode == "AUTO_FOLLOW":
            self._continue_next(idx, run.advance_chain)

        self._end_cue(cue_id, mark_stopped=True, fade=fade)

    def _duration_elapsed(self, cue_id: int) -> None:
        """La duración fijada de un media expiró antes del fin natural: parar
        aplicando su fade_out (el corte lo hace _end_cue con ese fundido)."""
        cue = self._model.get_by_id(cue_id)
        if cue is None:
            return
        run = self._running.get(cue_id)
        if run is not None:
            run.manual_stop = True   # evita doble action-end desde el engine
        fade = cue.fade_out if cue.type in ("AUDIO", "VIDEO") else 0.0
        self._on_action_end(cue_id, fade=fade)

    # ==================================================================
    # EJECUCIÓN DE CUES QUE APUNTAN A OTRO CUE
    # ==================================================================

    def _exec_fade(self, cue: Cue) -> None:
        """FADE: funde el volumen del cue objetivo hacia cue.volume."""
        target = self._model.get_by_id(cue.target_id) if cue.target_id else None
        if target is None:
            self._view.flash_status("FADE sin objetivo válido — ignorado")
            return
        stop_after = cue.fade_stop_on_done and cue.volume <= 0.0001
        if self._audio.is_playing(target.id):
            self._audio.fade(target.id, cue.volume, cue.duration, stop_after)
        elif self._video.is_playing(target.id):
            self._video.fade_volume(target.id, cue.volume, cue.duration, stop_after)
        else:
            self._view.flash_status(
                f"FADE → cue {target.display_number} no está sonando")

    def _exec_start(self, cue: Cue) -> None:
        """START: dispara el cue objetivo (como una sub-secuencia)."""
        if cue.target_id is None:
            self._view.flash_status("START sin objetivo — ignorado")
            return
        tgt_idx = self._model.index_of_id(cue.target_id)
        if tgt_idx < 0:
            self._view.flash_status("START: objetivo inexistente — ignorado")
            return
        self._trigger(tgt_idx, advance_chain=False)

    def _exec_stop(self, cue: Cue) -> None:
        """STOP: detiene el cue objetivo, o todos si no hay objetivo."""
        if cue.target_id is None:
            self._stop_everything(fade=cue.fade_out)
            return
        tgt = self._model.get_by_id(cue.target_id)
        if tgt is None:
            return
        if tgt.id in self._running:
            self._end_cue(tgt.id, mark_stopped=True, fade=cue.fade_out)

    def _exec_script(self, cue: Cue) -> None:
        """SCRIPT: lanza el archivo/comando en un proceso independiente."""
        path = cue.file_path.strip()
        if not path:
            self._view.flash_status("SCRIPT sin archivo — ignorado")
            return
        try:
            p = Path(path)
            ext = p.suffix.lower()
            if ext == ".py":
                subprocess.Popen([sys.executable, str(p)])
            elif ext in (".bat", ".cmd"):
                subprocess.Popen(["cmd", "/c", str(p)])
            elif ext in (".ps1",):
                subprocess.Popen(
                    ["powershell", "-ExecutionPolicy", "Bypass", "-File", str(p)])
            else:
                subprocess.Popen(str(p), shell=True)
            self._view.flash_status(f"SCRIPT lanzado: {p.name}")
        except Exception as exc:
            self._view.flash_status(f"SCRIPT falló: {exc}")

    # ==================================================================
    # FIN DE CUE / STOP GLOBAL
    # ==================================================================

    def _end_cue(self, cue_id: int, mark_stopped: bool, fade: float = 0.0) -> None:
        """Retira un cue de la ejecución: cancela timers y para su motor."""
        run = self._running.pop(cue_id, None)
        if run is None:
            return
        for t in run.timers:
            t.stop()
            t.deleteLater()
        run.timers.clear()

        if run.kind == "AUDIO" and self._audio.is_playing(cue_id):
            self._audio.stop(cue_id, fade=fade)
        elif run.kind == "VIDEO":
            # Llamar stop() aunque el vídeo ya haya hecho end-file: termina el
            # player y limpia a negro para que su último frame no quede pegado.
            if fade > 0.0 and self._video.is_playing(cue_id):
                self._video.fade_volume(cue_id, 0.0, fade, stop_after=True)
            else:
                self._video.stop(cue_id)

        if mark_stopped:
            idx = self._model.index_of_id(cue_id)
            if idx >= 0:
                self._model_state(idx, "STOPPED")

    def _stop_everything(self, fade: float) -> None:
        """Detiene TODOS los cues activos y cancela todas las cadenas/timers."""
        # Cancelar continuaciones pendientes (AUTO_CONTINUE) para que Stop/Panic
        # detengan la secuencia de verdad.
        for t in self._chain_timers:
            t.stop()
            t.deleteLater()
        self._chain_timers.clear()

        # Detener cada cue en ejecución (con fundido si Panic). _end_cue funde
        # audio y vídeo por cue vía sus motores.
        for cue_id in list(self._running.keys()):
            self._end_cue(cue_id, mark_stopped=True, fade=fade)

        if fade <= 0.0:
            # Parada inmediata: barrido de seguridad de ambos motores.
            self._audio.stop_all(fade=0.0)
            self._video.stop_all()
        else:
            # Con fundido: barrer solo el audio que hubiera quedado suelto (idempotente);
            # los vídeos ya se están fundiendo por cue en _end_cue — NO cortarlos.
            self._audio.stop_all(fade=fade)

        self._paused = False
        self._view.update_transport(False)
        self._view.update_countdown(None)
        self._view.update_active_cues([])
        self._view.update_vu_meter(0.0)
        self._refresh_video_tally()

    # ==================================================================
    # SEÑALES DE LOS MOTORES
    # ==================================================================

    def _on_audio_state_changed(self, cue_id: int, state: str) -> None:
        if state == "STOPPED":
            run = self._running.get(cue_id)
            if run is not None and not run.manual_stop:
                # Fin natural del audio → terminar la acción (y encadenar)
                self._on_action_end(cue_id)

    def _on_video_state_changed(self, cue_id: int, state: str) -> None:
        if state == "STOPPED":
            run = self._running.get(cue_id)
            if run is not None and not run.manual_stop:
                self._on_action_end(cue_id)
            self._refresh_video_tally()

    def _on_latency_report(self, cue_id: int, ms: float) -> None:
        idx = self._model.index_of_id(cue_id)
        if idx >= 0:
            self._model.set_latency(idx, ms)
            self._view.refresh_row(idx)

    def _on_audio_error(self, cue_id: int, message: str) -> None:
        idx = self._model.index_of_id(cue_id)
        if idx >= 0:
            self._model.set_state(idx, "ERROR")
            self._view.refresh_row(idx)
        self._running.pop(cue_id, None)
        self._view.flash_status(f"Audio: {message}")

    def _on_video_error(self, cue_id: int, message: str) -> None:
        if message.startswith("[AVISO]"):
            self._view.flash_status(message)
            return
        idx = self._model.index_of_id(cue_id)
        if idx >= 0:
            self._model.set_state(idx, "ERROR")
            self._view.refresh_row(idx)
        self._running.pop(cue_id, None)
        self._view.flash_status(f"Vídeo: {message}")

    def _on_level_report(self, rms: float) -> None:
        self._view.update_vu_meter(rms)

    # ==================================================================
    # TICK — panel de activos + countdown
    # ==================================================================

    def _on_tick(self) -> None:
        if not self._running:
            if self._last_had_running:
                self._view.update_active_cues([])
                self._view.update_countdown(None)
                self._last_had_running = False
            return
        self._last_had_running = True

        now = perf_counter()
        rows: list[dict] = []
        primary_remaining: Optional[float] = None
        for run in self._running.values():
            # Rellenar la duración real de un media (cue.duration==0 → longitud
            # natural del archivo) en cuanto el motor la conozca, para poder
            # mostrar una cuenta atrás en vez de un contador ascendente.
            if run.duration <= 0.0 and run.kind in ("AUDIO", "VIDEO"):
                d = (self._audio.get_duration(run.cue_id) if run.kind == "AUDIO"
                     else self._video.get_duration(run.cue_id))
                if d and d > 0.0:
                    run.duration = d

            # Tiempo transcurrido: preferir la posición real del motor (más
            # exacta que el reloj de pared, sobre todo tras un preload).
            elapsed = now - run.t_start if not self._paused else \
                (self._pause_t0 - run.t_start)
            if run.kind == "AUDIO":
                e = self._audio.get_elapsed(run.cue_id)
                if e > 0.0:
                    elapsed = e
            elif run.kind == "VIDEO":
                e = self._video.get_elapsed(run.cue_id)
                if e > 0.0:
                    elapsed = e

            if run.duration > 0.0:
                remaining = max(0.0, run.duration - elapsed)
                fraction = min(1.0, elapsed / run.duration) if run.duration else None
            else:
                remaining = None
                fraction = None
            rows.append({
                "id": run.cue_id,
                "number": run.number,
                "name": run.name,
                "type": run.kind,
                "color": run.color,
                "elapsed": elapsed,
                "remaining": remaining,
                "fraction": fraction,
            })
            if remaining is not None:
                if primary_remaining is None or remaining < primary_remaining:
                    primary_remaining = remaining

        self._view.update_active_cues(rows)
        self._view.update_countdown(primary_remaining)

    # Estado para el tick
    _last_had_running: bool = False
    _pause_t0: float = 0.0

    # ==================================================================
    # PLAYHEAD / MODELO / VISTA
    # ==================================================================

    def _set_playhead(self, index: int) -> None:
        count = self._model.count()
        if count == 0:
            self._playhead = 0
        else:
            self._playhead = max(0, min(index, count))  # count = "más allá del final"
        self.active_index_changed.emit(self._playhead)
        self._view.set_playhead(self._playhead)

    def _model_state(self, index: int, state: str) -> None:
        self._model.set_state(index, state)
        self._view.refresh_row(index)

    def _resolve_video_output(self) -> None:
        """Dirige el motor de vídeo al contenedor de salida (monitor o local)."""
        container = self._view.video_output_container()
        if container is not None:
            self._video.set_container(container)

    def _update_program_monitor(self) -> None:
        """Envía a la vista el frame del vídeo que está en la salida (monitor PGM)."""
        vids = [cid for cid, r in self._running.items()
                if r.kind == "VIDEO" and self._video.is_playing(cid)]
        if not vids:
            self._view.update_program("", 0.0)
            return
        cid = vids[-1]
        cue = self._model.get_by_id(cid)
        if cue:
            self._view.update_program(cue.file_path, self._video.get_elapsed(cid))

    def _refresh_video_tally(self) -> None:
        """Actualiza el testigo con el vídeo actualmente en pantalla (o NEGRO)."""
        vids = [self._model.get_by_id(cid) for cid, r in self._running.items()
                if r.kind == "VIDEO"]
        vids = [v for v in vids if v is not None]
        self._view.set_output_content(vids[-1].name if vids else None)

    def _preload_upcoming(self) -> None:
        """Pre-carga en RAM el siguiente cue de audio para latencia mínima."""
        nxt = self._model.get_cue(self._playhead)
        if nxt and nxt.type == "AUDIO" and nxt.file_path:
            self._audio.preload(nxt.id, nxt.file_path)

    @staticmethod
    def _schedule(run: _Running, ms: int, fn) -> None:
        """Crea un QTimer singleShot asociado a un cue en curso.

        Se cancela cuando el cue termina o se detiene (está en run.timers).
        """
        t = QTimer()
        t.setSingleShot(True)
        t.timeout.connect(fn)
        run.timers.append(t)
        t.start(max(0, ms))

    def _schedule_chain(self, ms: int, fn) -> None:
        """Timer de continuación independiente del ciclo de vida de un cue.

        Sobrevive al fin de la acción del cue de origen; solo lo cancela un
        Stop All / Panic. Se auto-retira de la lista al dispararse.
        """
        t = QTimer(self)
        t.setSingleShot(True)

        def _fire() -> None:
            if t in self._chain_timers:
                self._chain_timers.remove(t)
            t.deleteLater()
            fn()

        t.timeout.connect(_fire)
        self._chain_timers.append(t)
        t.start(max(0, ms))

    # ==================================================================
    # GESTIÓN DE LA LISTA (añadir / importar / editar / guardar)
    # ==================================================================

    def add_new_cue(self, cue_type: str = "AUDIO") -> None:
        new_id = self._model.next_id()
        cue = Cue(
            id=new_id,
            type=cue_type,
            name=f"{cue_type.capitalize()} cue",
            duration=(3.0 if cue_type in ("WAIT", "FADE") else 0.0),
            number=self._model.next_number(),
        )
        self._model.add_cue(cue)
        self._view.append_cue_row(cue)
        self._set_playhead(self._model.count() - 1)

    def on_insert_cue(self, cue_type: str, row: int) -> None:
        """Inserta un cue nuevo del tipo dado justo debajo de la fila `row`."""
        insert_at = (row + 1) if row >= 0 else self._model.count()
        # Heredar el grupo del cue de referencia (mantiene contigüidad)
        ref = self._model.get_cue(row) if row >= 0 else None
        parent = ref.parent_id if ref is not None else None
        cue = Cue(
            id=self._model.next_id(),
            type=cue_type,
            name=f"{cue_type.capitalize()} cue",
            duration=(3.0 if cue_type in ("WAIT", "FADE") else 0.0),
            number=self._model.next_number(),
            parent_id=parent,
        )
        self._model.insert_cue(insert_at, cue)
        self._view.reload_table()
        self._set_playhead(min(insert_at, self._model.count() - 1))

    # ------------------------------------------------------------------
    # Grupos: agrupar / desagrupar / colapsar
    # ------------------------------------------------------------------

    def on_group_selected(self, rows: list) -> None:
        """Crea un GROUP que contiene los cues seleccionados (y sus descendientes)."""
        valid = sorted(set(r for r in rows if 0 <= r < self._model.count()))
        if not valid:
            return
        cues = self._model.cues
        sel_cues = [cues[r] for r in valid]

        # Conjunto a mover = seleccionados + todos sus descendientes
        move_ids: set[int] = set()
        for c in sel_cues:
            move_ids.add(c.id)
            for d in self._model.descendants(c.id):
                move_ids.add(d.id)
        block = [c for c in cues if c.id in move_ids]   # conserva el orden

        gid = self._model.next_id()
        group = Cue(
            id=gid, type="GROUP", name="Grupo", duration=0.0,
            number=self._model.next_number(),
            parent_id=sel_cues[0].parent_id, group_mode="ENTER",
        )
        # Solo los seleccionados de nivel superior cuelgan del nuevo grupo
        for c in sel_cues:
            c.parent_id = gid

        first = valid[0]
        new: list[Cue] = []
        for i, c in enumerate(cues):
            if i == first:
                new.append(group)
                new.extend(block)
            if c.id in move_ids:
                continue
            new.append(c)
        self._model.replace_all(new)
        self._view.reload_table()
        gi = self._model.index_of_id(gid)
        self._set_playhead(gi if gi >= 0 else 0)
        self._view.flash_status(f"{len(sel_cues)} cue(s) agrupados")

    def on_ungroup(self, row: int) -> None:
        """Deshace un grupo: promueve sus hijos directos y elimina el GROUP."""
        group = self._model.get_cue(row)
        if group is None or group.type != "GROUP":
            return
        for c in self._model.direct_children(group.id):
            c.parent_id = group.parent_id
        self._rr_state.pop(group.id, None)
        self._model.remove_cue(row)
        self._view.reload_table()
        count = self._model.count()
        self._set_playhead(min(row, count - 1) if count else 0)
        self._view.flash_status("Grupo deshecho")

    def on_toggle_collapse(self, row: int) -> None:
        """Contrae/expande un grupo (oculta/muestra sus descendientes)."""
        cue = self._model.get_cue(row)
        if cue is None or cue.type != "GROUP":
            return
        cue.collapsed = not cue.collapsed
        self._view.refresh_row(row)
        self._view.apply_group_visibility()

    def add_cues_from_files(self, target_row: int, paths: list) -> None:
        insert_at = target_row if target_row >= 0 else self._model.count()
        created = 0
        for path in paths:
            result = MediaImporter.validate_file(path)
            if not result["valid"]:
                continue
            ctype = result["media_type"] if result["media_type"] in ("AUDIO", "VIDEO") else "AUDIO"
            cue = Cue(
                id=self._model.next_id(),
                type=ctype,
                name=Path(path).stem,
                duration=0.0,
                number=self._model.next_number(),
                file_path=result["filepath"],
            )
            self._model.insert_cue(insert_at + created, cue)
            created += 1
        if created:
            self._view.reload_table()
            self._set_playhead(min(insert_at, self._model.count() - 1))
            self._view.flash_status(f"{created} cue(s) creado(s) desde archivo.")

    def on_cue_edit_committed(self, row: int, fields: dict) -> None:
        cue = self._model.get_cue(row)
        if cue is None:
            return

        file_changed = "file_path" in fields and fields["file_path"] != cue.file_path

        if "file_path" in fields and "type" not in fields:
            # Drag & drop: dejar que MediaImporter detecte el tipo
            MediaImporter.assign_to_cue(cue, fields["file_path"])
            self._model.update_cue(row, cue)
            self._view.refresh_row(row)
            if row == self._playhead:
                self._view.set_playhead(self._playhead)
            return

        # Asignación campo a campo
        str_fields = ("name", "type", "number", "color", "notes", "continue_mode",
                      "file_path", "group_mode", "hotkey", "retrigger",
                      "fade_others_scope", "midi_type", "wallclock_time",
                      "timecode_value", "geo_mode", "layer")
        for k in str_fields:
            if k in fields:
                setattr(cue, k, fields[k])
        float_fields = ("duration", "pre_wait", "post_wait", "volume", "fade_in",
                        "fade_out", "fade_others_time", "scale", "trim_start",
                        "trim_end", "rate", "duck_level", "duck_time",
                        "opacity", "translate_x", "translate_y", "scale_x",
                        "scale_y", "anchor_x", "anchor_y", "rotate_x", "rotate_y",
                        "rotate_z")
        for k in float_fields:
            if k in fields:
                setattr(cue, k, float(fields[k]))
        int_fields = ("play_count", "midi_channel", "midi_note", "midi_velocity",
                      "wallclock_days")
        for k in int_fields:
            if k in fields:
                setattr(cue, k, int(fields[k]))
        bool_fields = ("loop", "armed", "fade_stop_on_done", "stop_others",
                       "flagged", "auto_load", "fade_others", "preserve_pitch",
                       "midi_enabled", "wallclock_enabled", "timecode_enabled",
                       "duck_enabled", "second_trigger_on_release", "preserve_aspect")
        for k in bool_fields:
            if k in fields:
                setattr(cue, k, bool(fields[k]))
        if "target_id" in fields:
            tv = fields["target_id"]
            cue.target_id = int(tv) if tv not in (None, "", -1) else None

        if file_changed:
            cue.latency_ms = None
            self._audio._buffers.pop(cue.id, None)   # invalidar caché de audio

        if "hotkey" in fields:
            self._view.rebuild_hotkeys()

        # Aplicar cambios EN VIVO si el cue está sonando (volumen / geometría)
        self._apply_live_edits(cue, fields)

        self._view.refresh_row(row)
        if row == self._playhead:
            self._view.set_playhead(self._playhead)

    def _geo_dict(self, cue: Cue) -> dict:
        """Construye el dict de geometría para el motor (incluye tamaño de canvas)."""
        sw, sh = self._view.surface_dimensions()
        return {
            "mode": cue.geo_mode, "preserve_aspect": cue.preserve_aspect,
            "translate_x": cue.translate_x, "translate_y": cue.translate_y,
            "scale_x": cue.scale_x, "scale_y": cue.scale_y, "rotate_z": cue.rotate_z,
            "surf_w": sw, "surf_h": sh,
        }

    def _apply_live_edits(self, cue: Cue, fields: dict) -> None:
        """Refleja al instante en el motor los cambios del inspector si el cue suena."""
        if cue.id not in self._running:
            return
        if "volume" in fields:
            if self._audio.is_playing(cue.id):
                self._audio.set_volume(cue.id, cue.volume)
            if self._video.is_playing(cue.id):
                self._video.set_volume(cue.id, cue.volume)
        geo_keys = {"geo_mode", "preserve_aspect", "translate_x", "translate_y",
                    "scale_x", "scale_y", "rotate_z"}
        if (cue.type == "VIDEO" and self._video.is_playing(cue.id)
                and geo_keys & set(fields.keys())):
            self._video.apply_geometry(cue.id, self._geo_dict(cue))

    # ------------------------------------------------------------------
    # Guardar / cargar show en JSON
    # ------------------------------------------------------------------

    def on_save_requested(self) -> None:
        path, _ = QFileDialog.getSaveFileName(
            self._view, "Guardar cue list", "",
            "WinCue Show (*.json);;Todos los archivos (*)")
        if not path:
            return
        if not path.lower().endswith(".json"):
            path += ".json"
        try:
            with open(path, "w", encoding="utf-8") as fh:
                fh.write(self._model.to_json())
            self._view.flash_status(f"Show guardado: {Path(path).name}")
        except OSError as exc:
            self._view.show_error("Error al guardar", str(exc))

    def on_load_requested(self) -> None:
        path, _ = QFileDialog.getOpenFileName(
            self._view, "Abrir cue list", "",
            "WinCue Show (*.json);;Todos los archivos (*)")
        if not path:
            return
        try:
            with open(path, "r", encoding="utf-8") as fh:
                new_cues = CueModel.from_json(fh.read())
        except (OSError, ValueError) as exc:
            self._view.show_error("Error al cargar", str(exc))
            return
        if not new_cues:
            self._view.show_error("Show vacío", "El archivo no contiene ningún cue.")
            return

        self._stop_everything(fade=0.0)
        self._model.replace_all(new_cues)
        self._view.reload_table()
        self._set_playhead(0)
        self._view.flash_status(f"Show cargado: {Path(path).name}")

    # ------------------------------------------------------------------
    # Cierre limpio
    # ------------------------------------------------------------------

    def shutdown(self) -> None:
        """Detiene todo y libera los motores (llamar al cerrar la app)."""
        self._tick.stop()
        self._pgm_timer.stop()
        for t in self._chain_timers:
            t.stop()
        self._chain_timers.clear()
        for cue_id in list(self._running.keys()):
            self._end_cue(cue_id, mark_stopped=False)
        self._audio.cleanup()
        self._video.cleanup()
        try:
            self._view.close_video_output()
        except Exception:
            pass
