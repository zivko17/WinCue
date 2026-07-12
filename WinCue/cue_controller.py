# =============================================================================
# cue_controller.py — Controlador (C en MVC)
# =============================================================================
# Responsabilidad: Orquesta toda la lógica de negocio de la cue-list.
#   Es el ÚNICO punto de escritura sobre CueModel. Recibe señales de la vista
#   (botones GO, STOP, Stress Test) y actualiza modelo + vista mediante
#   llamadas directas a métodos públicos de MainWindow.
#
# Hilos permitidos: SOLO el hilo principal (main thread).
#   Todos los QTimer conectados aquí disparan en el main thread mediante el
#   sistema de señales/slots de Qt. NUNCA se usan sleep(), wait() ni bucles
#   bloqueantes. El hilo principal queda siempre libre para procesar eventos.
# =============================================================================

from __future__ import annotations

from pathlib import Path
from time import perf_counter

from media_import import MediaImporter
from typing import TYPE_CHECKING

from PySide6.QtCore import QObject, QTimer, Signal
from PySide6.QtWidgets import QFileDialog

from audio_engine import AudioEngine  # Paso 2 — línea 1 de 3
from video_engine import VideoEngine  # Paso 3
from cue_model import Cue, CueModel

if TYPE_CHECKING:
    # Importación diferida para evitar circularidad en tiempo de ejecución.
    from main_window import MainWindow


class MainController(QObject):
    """Controlador principal de la cue-list.

    Gestiona el índice activo, las transiciones de estado y los timers de
    simulación de reproducción. No construye ni manipula widgets directamente:
    delega toda actualización visual en métodos públicos de MainWindow.
    """

    # Señal emitida cuando el índice activo cambia (usada opcionalmente por
    # la vista para sincronizaciones futuras).
    active_index_changed: Signal = Signal(int)

    # ------------------------------------------------------------------
    # Construcción
    # ------------------------------------------------------------------

    def __init__(self, model: CueModel, view: MainWindow) -> None:
        super().__init__()
        self._model: CueModel = model
        self._view: MainWindow = view
        self._active_index: int = 0

        # Timer de simulación de reproducción (singleShot: se dispara UNA vez)
        self._play_timer: QTimer = QTimer(self)
        self._play_timer.setSingleShot(True)
        self._play_timer.timeout.connect(self._on_play_timer_fired)

        # Paso 2 — línea 2 de 3: motor de audio + conexión de señales
        self._audio_engine: AudioEngine = AudioEngine()
        self._audio_engine.latency_report.connect(self._on_latency_report)
        self._audio_engine.error_occurred.connect(self._on_audio_error)

        # Paso 3 — motor de vídeo
        self._video_engine: VideoEngine = VideoEngine(view.video_container)
        self._video_engine.latency_report.connect(self._on_latency_report)
        self._video_engine.error_occurred.connect(self._on_video_error)
        self._video_engine.state_changed.connect(self._on_video_state_changed)

        # Paso 5 — VU meter: recibir niveles del engine de audio
        self._audio_engine.level_report.connect(self._on_level_report)

        # Paso 5 — countdown: timer de 100 ms para actualizar el tiempo restante
        self._tick_timer: QTimer = QTimer(self)
        self._tick_timer.setInterval(100)
        self._tick_timer.timeout.connect(self._on_tick)
        self._tick_start_time: float = 0.0
        self._tick_duration: float = 0.0

        # Flag para distinguir STOP manual de fin natural de archivo.
        # Se activa justo antes de llamar a video_engine.stop() y se desactiva
        # en _on_video_state_changed, evitando que ese handler avance el índice
        # cuando el operador pulsa STOP (el avance solo debe ocurrir en end-file).
        self._video_stop_by_user: bool = False

        # Timer del stress test (dispara GO repetidamente)
        self._stress_timer: QTimer = QTimer(self)
        self._stress_timer.setInterval(40)       # 50 disparos en 2 s → 40 ms/tick
        self._stress_timer.timeout.connect(self._on_stress_tick)
        self._stress_count: int = 0
        self._stress_max: int = 50

        # Añadir cue nuevo desde el botón ＋ CUE
        self._view.cue_add_requested.connect(self.add_new_cue)

    # ------------------------------------------------------------------
    # API pública — conectada a señales de MainWindow
    # ------------------------------------------------------------------

    def on_go_pressed(self) -> None:
        """Procesa la pulsación del botón GO (o barra espaciadora).

        Si hay un cue en PLAYING con timer activo, detiene el engine de ese
        cue, lo finaliza y avanza antes de iniciar el nuevo. Esto garantiza
        que mpv/audio no sigan corriendo en background cuando el índice avanza.
        """
        if self._play_timer.isActive():
            self._play_timer.stop()
            self._stop_active_engine()     # detiene VIDEO/AUDIO antes de avanzar
            self._finalize_current_cue()   # STOPPED en modelo + avance de índice

        # Iniciar reproducción del cue ahora activo
        cue: Cue | None = self._model.get_cue(self._active_index)
        if cue is None:
            return

        print(f"[CUE] GO pressed, cue type={cue.type}")
        self._model.set_state(self._active_index, "PLAYING")
        self._view.refresh_row(self._active_index)
        self._view.highlight_active_row(self._active_index)
        self._view.update_status_bar(self._active_index, "RUNNING")

        # Paso 2 — línea 3 de 3: delegar al motor de audio si el cue es AUDIO
        if cue.type == "AUDIO":
            self._audio_engine.play(cue.id, cue.file_path)
            # Pre-cargar el siguiente cue en segundo plano
            next_cue = self._model.get_cue(
                (self._active_index + 1) % self._model.count()
            )
            if next_cue and next_cue.type == "AUDIO" and next_cue.file_path:
                self._audio_engine.preload(next_cue.id, next_cue.file_path)

        elif cue.type == "VIDEO":
            print(f"[CUE] VIDEO branch — file_path={repr(cue.file_path)}", flush=True)
            if cue.file_path:
                self._video_engine.play(cue.id, cue.file_path)
            else:
                print(f"[CUE] VIDEO — file_path vacío, play() no llamado", flush=True)

            # Pre-cargar el siguiente cue VIDEO (latencia cero en GO consecutivos)
            next_cue = self._model.get_cue(
                (self._active_index + 1) % self._model.count()
            )
            if (next_cue and next_cue.type == "VIDEO" and next_cue.file_path
                    and self._video_engine.get_state(next_cue.id) == "IDLE"):
                self._video_engine.load(next_cue.id, next_cue.file_path)

        # Iniciar countdown con la duración real del cue
        self._tick_duration = max(0.0, cue.duration)
        self._tick_start_time = perf_counter()
        self._tick_timer.start()
        self._view.update_countdown(self._tick_duration)

        # Avanzar automáticamente cuando expire la duración del cue.
        # Para VIDEO sin duración definida (0 s) NO iniciamos el timer:
        # el avance lo gestiona _on_video_state_changed cuando end-file dispara.
        if cue.type == "VIDEO" and cue.duration == 0.0:
            print(f"[CUE] VIDEO sin duración — auto-avance desactivado, "
                  f"esperando end-file del engine", flush=True)
        else:
            self._play_timer.start(max(50, int(cue.duration * 1000)))

    def on_stop_pressed(self) -> None:
        """Detiene el cue activo inmediatamente y cancela el timer."""
        cue: Cue | None = self._model.get_cue(self._active_index)
        print(
            f"[CUE] STOP pressed  active_index={self._active_index}"
            f"  cue_id={cue.id if cue else None}"
            f"  type={cue.type if cue else None}",
            flush=True,
        )

        if self._play_timer.isActive():
            self._play_timer.stop()
        if self._tick_timer.isActive():
            self._tick_timer.stop()
        self._view.update_countdown(None)

        if cue is None:
            return

        self._model.set_state(self._active_index, "STOPPED")
        if cue.type in ("VIDEO", "AUDIO"):
            if cue.type == "VIDEO":
                print(f"[CUE] STOP → video_engine.stop(cue_id={cue.id})", flush=True)
                self._video_stop_by_user = True
                self._video_engine.stop(cue.id)
                # _video_stop_by_user se limpia en _on_video_state_changed
            else:
                self._audio_engine.stop()
        self._view.refresh_row(self._active_index)
        self._view.highlight_active_row(self._active_index)
        self._view.update_status_bar(self._active_index, "STOPPED")

    def on_stress_test_pressed(self) -> None:
        """Inicia o detiene el stress test (50 GO en 2 segundos).

        Si el test ya está corriendo, lo cancela para evitar dobles arranques.
        """
        if self._stress_timer.isActive():
            self._stress_timer.stop()
            self._stress_count = 0
            self._view.set_stress_button_state(active=False)
            return

        self._stress_count = 0
        self._stress_timer.start()
        self._view.set_stress_button_state(active=True)

    # ------------------------------------------------------------------
    # Slots privados
    # ------------------------------------------------------------------

    def _on_play_timer_fired(self) -> None:
        """Callback del timer de simulación: transiciona PLAYING → STOPPED."""
        if self._tick_timer.isActive():
            self._tick_timer.stop()
        self._view.update_countdown(None)
        self._finalize_current_cue()
        self._view.update_status_bar(self._active_index, "READY")

    def _on_stress_tick(self) -> None:
        """Un tick del stress test: dispara GO y cuenta hasta _stress_max."""
        self._stress_count += 1
        self.on_go_pressed()

        if self._stress_count >= self._stress_max:
            self._stress_timer.stop()
            self._stress_count = 0
            self._view.set_stress_button_state(active=False)

    def _on_tick(self) -> None:
        """Tick de 100 ms: actualiza el countdown con el tiempo restante."""
        elapsed = perf_counter() - self._tick_start_time
        remaining = max(0.0, self._tick_duration - elapsed)
        self._view.update_countdown(remaining)

    def _on_level_report(self, rms: float) -> None:
        """Recibe el nivel RMS del engine de audio y lo envía al VU meter."""
        self._view.update_vu_meter(rms)

    def on_prev_pressed(self) -> None:
        """Retrocede al cue anterior (circular) sin iniciar reproducción."""
        if self._play_timer.isActive():
            return   # No navegar mientras hay un cue en PLAYING
        self._active_index = (self._active_index - 1) % self._model.count()
        self.active_index_changed.emit(self._active_index)
        self._view.highlight_active_row(self._active_index)

    def add_new_cue(self, cue_type: str = "AUDIO") -> None:
        """Crea un cue vacío del tipo dado y lo añade al final de la lista."""
        new_id = len(self._model.cues) + 1
        cue = Cue(
            id=new_id,
            type=cue_type,
            name=f"Cue {new_id}",
            duration=0.0,
            state="IDLE",
            latency_ms=None,
            file_path="",
        )
        self._model.add_cue(cue)
        self._view._append_row_to_qt_model(cue)
        last_row = self._model.count() - 1
        self._view._table_view.selectRow(last_row)

    def on_jump_to_cue(self, cue_number: int) -> None:
        """Salta directamente a un cue por número (1-based).

        Si el número está fuera de rango se ignora silenciosamente.
        No inicia la reproducción: solo mueve el foco activo.
        """
        if self._play_timer.isActive():
            return   # No navegar mientras hay un cue en PLAYING
        index = cue_number - 1
        if 0 <= index < self._model.count():
            self._active_index = index
            self.active_index_changed.emit(self._active_index)
            self._view.highlight_active_row(self._active_index)

    # ------------------------------------------------------------------
    # Helpers internos
    # ------------------------------------------------------------------

    def _on_latency_report(self, cue_id: int, ms: float) -> None:
        """Recibe la latencia medida del engine y la persiste en el modelo."""
        for i, cue in enumerate(self._model.cues):
            if cue.id == cue_id:
                self._model.set_latency(i, ms)
                self._view.refresh_row(i)
                break

    def _on_audio_error(self, cue_id: int, message: str) -> None:
        """Propaga un error del engine al modelo (cue → ERROR) y a la vista."""
        for i, cue in enumerate(self._model.cues):
            if cue.id == cue_id:
                self._model.set_state(i, "ERROR")
                self._view.refresh_row(i)
                break

    def _on_video_error(self, cue_id: int, message: str) -> None:
        """Propaga un error del VideoEngine. [AVISO] no transiciona a ERROR."""
        for i, cue in enumerate(self._model.cues):
            if cue.id == cue_id:
                if not message.startswith("[AVISO]"):
                    self._model.set_state(i, "ERROR")
                self._view.refresh_row(i)
                break

    def _on_video_state_changed(self, cue_id: int, new_state: str) -> None:
        """Avanza automáticamente cuando un vídeo termina de reproducirse (end-file natural).

        Solo actúa si:
          - el estado nuevo es STOPPED
          - el cue que terminó es el cue activo
          - no hay _play_timer en vuelo (duración definida)
          - el STOP no fue iniciado por el operador (_video_stop_by_user=False)
        """
        print(
            f"[CUE] _on_video_state_changed cue_id={cue_id} new_state={new_state!r}"
            f"  stop_by_user={self._video_stop_by_user}",
            flush=True,
        )

        if new_state != "STOPPED":
            return

        # STOP manual del operador: limpiar flag y salir sin avanzar índice
        if self._video_stop_by_user:
            print(f"[CUE] STOP manual — no avanzar índice", flush=True)
            self._video_stop_by_user = False
            return

        active_cue = self._model.get_cue(self._active_index)
        if active_cue is None or active_cue.id != cue_id:
            print(
                f"[CUE] cue {cue_id} terminó pero no es el activo"
                f" (activo={active_cue.id if active_cue else None}) — ignorando",
                flush=True,
            )
            return  # el cue que terminó no es el activo, no tocar índice

        if self._play_timer.isActive():
            print(f"[CUE] _play_timer activo — el timer gestionará el avance", flush=True)
            return  # el timer ya gestionará el avance

        print(f"[CUE] VIDEO end-file natural para cue activo {cue_id} — avanzando índice", flush=True)
        if self._tick_timer.isActive():
            self._tick_timer.stop()
        self._view.update_countdown(None)
        self._finalize_current_cue()
        self._view.update_status_bar(self._active_index, "READY")

    def _stop_active_engine(self) -> None:
        """Detiene el engine (VIDEO/AUDIO) del cue activo sin modificar el índice.

        Llamar ANTES de _finalize_current_cue() cuando un GO interrumpe un cue
        en reproducción (timer activo). Evita que mpv o el decoder de audio sigan
        corriendo en segundo plano después de que _active_index avance al siguiente.

        Para VIDEO activa _video_stop_by_user para que _on_video_state_changed
        no interprete el STOPPED como fin natural y no avance el índice de nuevo.
        """
        cue = self._model.get_cue(self._active_index)
        if cue is None:
            return
        if cue.type == "VIDEO":
            vid_state = self._video_engine.get_state(cue.id)
            if vid_state in ("PLAYING", "LOADING"):
                print(
                    f"[CUE] _stop_active_engine → VIDEO cue_id={cue.id}"
                    f" vid_state={vid_state!r}",
                    flush=True,
                )
                self._video_stop_by_user = True
                self._video_engine.stop(cue.id)
        elif cue.type == "AUDIO":
            print(f"[CUE] _stop_active_engine → AUDIO cue_id={cue.id}", flush=True)
            self._audio_engine.stop()

    def _finalize_current_cue(self) -> None:
        """Marca el cue activo como STOPPED y avanza el índice."""
        self._model.set_state(self._active_index, "STOPPED")
        self._view.refresh_row(self._active_index)

        # Avance circular
        self._active_index = (self._active_index + 1) % self._model.count()
        self.active_index_changed.emit(self._active_index)
        self._view.highlight_active_row(self._active_index)

    # ------------------------------------------------------------------
    # Paso 4: guardar / cargar show en JSON
    # ------------------------------------------------------------------

    def on_save_requested(self) -> None:
        """Guarda la cue list en un archivo JSON elegido por el usuario."""
        path, _ = QFileDialog.getSaveFileName(
            self._view,
            "Guardar cue list",
            "",
            "CUE Show (*.json);;Todos los archivos (*)",
        )
        if not path:
            return
        if not path.lower().endswith(".json"):
            path += ".json"
        try:
            with open(path, "w", encoding="utf-8") as fh:
                fh.write(self._model.to_json())
        except OSError as exc:
            self._view.show_error("Error al guardar", str(exc))

    def on_load_requested(self) -> None:
        """Carga una cue list desde JSON, reemplazando la actual.

        Detiene cualquier reproducción activa antes de reemplazar el modelo
        para evitar referencias colgantes en los engines.
        """
        path, _ = QFileDialog.getOpenFileName(
            self._view,
            "Abrir cue list",
            "",
            "CUE Show (*.json);;Todos los archivos (*)",
        )
        if not path:
            return

        try:
            with open(path, "r", encoding="utf-8") as fh:
                text = fh.read()
            new_cues = CueModel.from_json(text)
        except (OSError, ValueError) as exc:
            self._view.show_error("Error al cargar", str(exc))
            return

        if not new_cues:
            self._view.show_error(
                "Show vacío", "El archivo no contiene ningún cue."
            )
            return

        # Detener reproducción antes de tocar el modelo
        if self._play_timer.isActive():
            self._play_timer.stop()
        self._audio_engine.stop()

        self._model.replace_all(new_cues)
        self._active_index = 0
        self.active_index_changed.emit(0)
        self._view.reload_table()

    # ------------------------------------------------------------------
    # Paso 4: editar cue (doble clic o drag & drop)
    # ------------------------------------------------------------------

    def on_cue_edit_committed(self, row: int, fields: dict) -> None:
        """Aplica un conjunto parcial de cambios a un cue del modelo.

        Recibe un dict con solo los campos que cambiaron (el diálogo de edición
        envía todos los campos; el drag & drop envía solo {"file_path": …}).
        El estado del cue NO se modifica aquí — lo gestiona el engine al
        reproducir. Solo se limpia latency_ms si cambia el archivo.
        """
        cue = self._model.get_cue(row)
        if cue is None:
            return

        file_changed = "file_path" in fields and fields["file_path"] != cue.file_path

        if "name"      in fields:
            cue.name      = fields["name"]
        if "duration"  in fields:
            cue.duration  = float(fields["duration"])
        if "type"      in fields:
            cue.type      = fields["type"]
        if "file_path" in fields:
            if "type" not in fields:
                # Drag & drop o asignación directa: dejar que MediaImporter
                # detecte el tipo y gestione latency_ms automáticamente.
                MediaImporter.assign_to_cue(cue, fields["file_path"])
                self._model.update_cue(row, cue)   # notifica el cambio al modelo
                file_changed = False   # assign_to_cue ya resetea latency_ms
            else:
                cue.file_path = fields["file_path"]

        if file_changed:
            # El archivo cambió con tipo explícito: invalidar latencia
            cue.latency_ms = None

        self._view.refresh_row(row)
        # Si es la fila activa, refrescar también las etiquetas
        if row == self._active_index:
            self._view.highlight_active_row(self._active_index)
