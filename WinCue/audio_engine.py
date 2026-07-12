# =============================================================================
# audio_engine.py — Motor de Audio (Paso 2)
# =============================================================================
#
# RESPONSABILIDAD
# ───────────────
# Gestiona la carga, el pre-decode en RAM y la reproducción de cues de audio.
# No conoce nada de la UI ni del CueModel; solo emite señales Qt.
#
# MODELO DE HILOS
# ───────────────
#
#  ┌─────────────────────────────────────────────────────────────────────┐
#  │  MAIN THREAD (Qt event loop)                                        │
#  │  · Toda la API pública de AudioEngine vive aquí                     │
#  │  · load(), play(), stop(), preload(), list_devices()                │
#  │  · Recibe señales de los LoaderWorkers (queued delivery Qt)         │
#  │  · Recibe señales del stream terminado (queued delivery Qt)         │
#  └─────────────────┬───────────────────────────────┬───────────────────┘
#                    │  QThread                       │  sd audio thread
#                    ▼                                ▼
#  ┌────────────────────────────┐    ┌──────────────────────────────────┐
#  │  _LoaderWorker (por cue)   │    │  sounddevice OutputStream        │
#  │  · sf.read() → ndarray     │    │  · Callback en hilo RT de PA     │
#  │  · Emite finished / failed │    │  · Solo lee el buffer en RAM     │
#  │  · Sin acceso a UI         │    │  · Sin disco, sin GIL pesado     │
#  └────────────────────────────┘    │  · Emite latency_report en 1er  │
#                                    │    frame (queued → main thread)  │
#                                    └──────────────────────────────────┘
#
# MÁQUINA DE ESTADOS POR CUE
# ──────────────────────────
#   IDLE ──► LOADING ──► LOADED ──► PLAYING ──► STOPPED
#              │                       │
#              └──────────► ERROR ◄────┘
#   Desde cualquier estado, un nuevo load() puede volver a LOADING.
#   ERROR es terminal hasta que el operador reintenta con play().
#
# GARANTÍA DE DIRECTO
# ───────────────────
#   Durante PLAYING el audio sale íntegramente desde un ndarray en RAM.
#   Un fallo de disco posterior NO interrumpe la reproducción en curso.
# =============================================================================

from __future__ import annotations

import threading
import time
from dataclasses import dataclass, field
from typing import Callable, Optional

import numpy as np
import sounddevice as sd
import soundfile as sf
from PySide6.QtCore import QObject, QThread, Signal
from audio_decoder import AudioDecoder, AudioDecoderError


# ---------------------------------------------------------------------------
# Worker de carga — vive en un QThread dedicado por cue
# ---------------------------------------------------------------------------

class _LoaderWorker(QObject):
    """Lee un archivo de audio completo a un ndarray en el hilo del loader.

    Nunca toca la UI. Emite finished o failed al terminar; ambas señales
    se entregan en el main thread mediante Qt queued connection automática.
    """

    # (cue_id, data_array, samplerate)
    finished: Signal = Signal(int, object, int)
    # (cue_id, error_message)
    failed: Signal = Signal(int, str)

    def __init__(self, cue_id: int, file_path: str) -> None:
        super().__init__()
        self._cue_id:    int = cue_id
        self._file_path: str = file_path

    def run(self) -> None:
        """Ejecutado en el QThread del loader. Solo I/O y conversión numpy."""
        try:
            data, samplerate = AudioDecoder.decode(self._file_path)
            self.finished.emit(self._cue_id, data, samplerate)
        except (FileNotFoundError, AudioDecoderError, Exception) as exc:
            self.failed.emit(self._cue_id, str(exc))


# ---------------------------------------------------------------------------
# Contexto de reproducción activa — transferido entre hilos via cierre
# ---------------------------------------------------------------------------

@dataclass
class _PlaybackCtx:
    """Estado mutable de UNA sesión de reproducción activa.

    Instanciado en el main thread; leído/modificado por el callback de audio.
    Los campos se escriben desde el hilo de audio (first_cb_done, latency_ms,
    position), pero en momentos no solapados con lecturas del main thread.
    Python's GIL protege las asignaciones escalares simples.
    """
    cue_id:        int
    data:          np.ndarray       # Buffer en RAM (lectura pura en callback)
    samplerate:    int
    position:      int  = 0         # Frame actual; solo escribe el callback
    t_start:       float = field(default_factory=time.perf_counter)
    first_cb_done: bool  = False    # Flag one-shot para medir latencia
    latency_ms:    Optional[float] = None
    level_counter: int   = 0        # Contador de callbacks para throttle de RMS


# ---------------------------------------------------------------------------
# AudioEngine — API pública
# ---------------------------------------------------------------------------

class AudioEngine(QObject):
    """Motor de audio con pre-decode en RAM y pre-load del cue siguiente.

    Todos los métodos públicos deben llamarse desde el main thread.
    Las señales se entregan siempre en el main thread (queued si vienen
    de un loader QThread o del hilo de audio de sounddevice).
    """

    # ------------------------------------------------------------------
    # Señales Qt
    # ------------------------------------------------------------------

    #: Emitida en cada transición de estado de un cue de audio.
    state_changed:  Signal = Signal(int, str)    # (cue_id, new_state)

    #: Latencia medida entre play() y el primer frame de audio real.
    latency_report: Signal = Signal(int, float)  # (cue_id, ms)

    #: Error no fatal; el cue pasa a ERROR, el sistema sigue operativo.
    error_occurred: Signal = Signal(int, str)    # (cue_id, message)

    #: Nivel RMS instantáneo (0.0–1.0 linear). Emitida ~20 Hz durante PLAYING.
    #: Sin cue_id: solo hay un stream activo a la vez.
    level_report:   Signal = Signal(float)        # rms

    # Transiciones válidas de la máquina de estados interna del engine.
    # (El CueModel del Paso 1 tiene sus propios estados; estos son paralelos.)
    _VALID_NEXT: dict[str, frozenset[str]] = {
        "IDLE":    frozenset({"LOADING"}),
        "LOADING": frozenset({"LOADED", "ERROR"}),
        "LOADED":  frozenset({"PLAYING", "LOADING", "IDLE"}),
        "PLAYING": frozenset({"STOPPED", "ERROR"}),
        "STOPPED": frozenset({"LOADING", "IDLE"}),
        "ERROR":   frozenset({"LOADING", "IDLE"}),
    }

    # ------------------------------------------------------------------
    # Construcción
    # ------------------------------------------------------------------

    def __init__(self, device_index: Optional[int] = None) -> None:
        super().__init__()
        self._device_index: Optional[int] = device_index

        # cue_id → (ndarray, samplerate) — RAM buffer
        self._buffers: dict[int, tuple[np.ndarray, int]] = {}

        # cue_id → estado interno del engine
        self._states: dict[int, str] = {}

        # cue_id → (QThread, _LoaderWorker) en vuelo
        self._loaders: dict[int, tuple[QThread, _LoaderWorker]] = {}

        # Stream activo de sounddevice
        self._active_stream: Optional[sd.OutputStream] = None

        # Contexto de reproducción activa; compartido con el callback de audio.
        # Protegido con lock para el intercambio main-thread ↔ hilo de audio.
        self._ctx:      Optional[_PlaybackCtx] = None
        self._ctx_lock: threading.Lock = threading.Lock()

    # ------------------------------------------------------------------
    # API pública — llamar SOLO desde el main thread
    # ------------------------------------------------------------------

    def load(self, cue_id: int, file_path: str) -> None:
        """Inicia la carga asíncrona del archivo en RAM (estado LOADING).

        Si el cue ya está en LOADING o LOADED no hace nada.
        Si file_path está vacío el cue pasa directamente a ERROR.
        """
        if not file_path:
            self._emit_error(cue_id, "Ruta de archivo vacía; asigna un archivo al cue.")
            return

        current = self._states.get(cue_id, "IDLE")
        if current in ("LOADING", "LOADED", "PLAYING"):
            return  # Ya cargado o en curso, no recargar

        self._set_state(cue_id, "LOADING")
        self._launch_loader(cue_id, file_path, play_when_ready=False)

    def play(self, cue_id: int, file_path: str) -> None:
        """Reproduce el cue. Carga desde disco si aún no está en RAM.

        Flujo:
          · LOADED   → inicia reproducción directamente desde RAM
          · IDLE / STOPPED / ERROR  → carga primero, luego reproduce
          · LOADING  → ya está cargando; cuando termine reproducirá
          · PLAYING  → no hace nada (ya está corriendo)
        """
        if not file_path:
            self._emit_error(cue_id, "No hay archivo asignado a este cue de audio.")
            return

        state = self._states.get(cue_id, "IDLE")

        if state == "LOADED":
            self._start_playback(cue_id)
        elif state == "PLAYING":
            pass  # Ya reproduciendo
        elif state == "LOADING":
            pass  # El loader marcará play_when_ready en la siguiente llamada;
                  # si se vuelve a llamar play() antes de que termine la carga
                  # simplemente esperamos — no lanzamos un loader duplicado.
        else:
            # IDLE / STOPPED / ERROR → cargar y reproducir al terminar
            self._set_state(cue_id, "LOADING")
            self._launch_loader(cue_id, file_path, play_when_ready=True)

    def stop(self) -> None:
        """Detiene la reproducción activa inmediatamente."""
        with self._ctx_lock:
            ctx = self._ctx
            self._ctx = None

        self._close_active_stream()

        if ctx is not None:
            self._set_state(ctx.cue_id, "STOPPED")

    def preload(self, cue_id: int, file_path: str) -> None:
        """Pre-carga el cue N+1 en un QThread independiente mientras N reproduce.

        Llamar desde el controlador justo después de play(N, ...).
        """
        self.load(cue_id, file_path)

    @staticmethod
    def list_devices() -> list[dict]:
        """Devuelve los dispositivos de salida disponibles en el sistema.

        Cada entrada incluye:
          index               — índice para pasar a AudioEngine(device_index=...)
          name                — nombre legible del dispositivo
          max_output_channels — canales de salida máximos
          default_samplerate  — frecuencia de muestreo por defecto
          hostapi_name        — nombre del Host API (WASAPI, ASIO, MME…)
          is_asio             — True si el host API es ASIO
          is_wasapi           — True si el host API es WASAPI

        Solo devuelve dispositivos con al menos 1 canal de salida.
        En caso de error (sin dispositivos, driver caído) devuelve [].
        """
        result: list[dict] = []
        try:
            all_devs = sd.query_devices()
            # query_devices() devuelve dict si hay un solo dispositivo
            if isinstance(all_devs, dict):
                all_devs = [all_devs]

            for idx, dev in enumerate(all_devs):
                if dev.get("max_output_channels", 0) <= 0:
                    continue
                try:
                    hostapi_info = sd.query_hostapis(dev["hostapi"])
                    hostapi_name: str = hostapi_info.get("name", "")
                except Exception:
                    hostapi_name = ""

                result.append({
                    "index":               idx,
                    "name":                dev.get("name", ""),
                    "max_output_channels": dev.get("max_output_channels", 0),
                    "default_samplerate":  int(dev.get("default_samplerate", 44100)),
                    "hostapi_name":        hostapi_name,
                    "is_asio":             "asio"   in hostapi_name.lower(),
                    "is_wasapi":           "wasapi" in hostapi_name.lower(),
                })
        except Exception:
            pass  # Sin dispositivos de audio disponibles
        return result

    # ------------------------------------------------------------------
    # Gestión de loaders
    # ------------------------------------------------------------------

    def _launch_loader(
        self, cue_id: int, file_path: str, *, play_when_ready: bool
    ) -> None:
        """Crea un QThread + _LoaderWorker para cargar file_path en RAM."""
        self._cleanup_loader(cue_id)  # Matar loader previo si existía

        worker = _LoaderWorker(cue_id, file_path)
        thread = QThread(self)
        worker.moveToThread(thread)

        # Conectar señales según si hay que reproducir tras cargar
        if play_when_ready:
            worker.finished.connect(self._on_loaded_play)
        else:
            worker.finished.connect(self._on_loaded)

        worker.failed.connect(self._on_load_failed)

        # Ciclo de vida del thread: termina cuando el worker emite cualquier señal
        thread.started.connect(worker.run)
        worker.finished.connect(thread.quit)
        worker.failed.connect(thread.quit)
        thread.finished.connect(thread.deleteLater)

        self._loaders[cue_id] = (thread, worker)
        thread.start()

    def _cleanup_loader(self, cue_id: int) -> None:
        """Detiene y limpia el loader de un cue si estaba en vuelo."""
        entry = self._loaders.pop(cue_id, None)
        if entry is not None:
            thread, _ = entry
            if thread.isRunning():
                thread.quit()
                thread.wait(2_000)  # Máximo 2 s de espera; no bloquea el loop

    # ------------------------------------------------------------------
    # Slots privados de loaders — entregados en el main thread por Qt
    # ------------------------------------------------------------------

    def _on_loaded(self, cue_id: int, data: object, samplerate: int) -> None:
        """Carga completada. Guarda buffer, no inicia reproducción."""
        self._buffers[cue_id] = (data, samplerate)  # type: ignore[assignment]
        self._loaders.pop(cue_id, None)
        self._set_state(cue_id, "LOADED")

    def _on_loaded_play(self, cue_id: int, data: object, samplerate: int) -> None:
        """Carga completada con play_when_ready=True. Arranca reproducción."""
        self._buffers[cue_id] = (data, samplerate)  # type: ignore[assignment]
        self._loaders.pop(cue_id, None)
        self._set_state(cue_id, "LOADED")
        self._start_playback(cue_id)

    def _on_load_failed(self, cue_id: int, message: str) -> None:
        """Error de carga (archivo no existe, corrupto, permisos…)."""
        self._loaders.pop(cue_id, None)
        self._emit_error(cue_id, f"Error al cargar '{cue_id}': {message}")

    # ------------------------------------------------------------------
    # Reproducción — sounddevice OutputStream con callback
    # ------------------------------------------------------------------

    def _start_playback(self, cue_id: int) -> None:
        """Abre el stream de audio y comienza la reproducción desde RAM."""
        buf = self._buffers.get(cue_id)
        if buf is None:
            self._emit_error(cue_id, "Buffer en RAM no disponible para reproducción.")
            return

        data, samplerate = buf
        channels: int = data.shape[1] if data.ndim > 1 else 1

        # Cerrar stream anterior si existiera (no debería, pero defensivo)
        self._close_active_stream()

        # Crear contexto ANTES de abrir el stream para que el callback
        # tenga acceso desde el primer frame.
        ctx = _PlaybackCtx(cue_id=cue_id, data=data, samplerate=samplerate)
        ctx.t_start = time.perf_counter()  # Marca T0 de latencia

        with self._ctx_lock:
            self._ctx = ctx

        try:
            stream = sd.OutputStream(
                samplerate=samplerate,
                channels=channels,
                dtype="float32",
                device=self._device_index,
                callback=self._make_audio_callback(ctx),
                finished_callback=self._on_stream_finished,
            )
            self._active_stream = stream
            stream.start()
            self._set_state(cue_id, "PLAYING")

        except Exception as exc:
            with self._ctx_lock:
                self._ctx = None
            self._active_stream = None
            self._emit_error(cue_id, f"No se pudo abrir el dispositivo de audio: {exc}")

    def _make_audio_callback(self, ctx: _PlaybackCtx) -> Callable:
        """Genera el closure de callback para sounddevice.

        ┌──────────────────────────────────────────────────────────────┐
        │  HILO DE AUDIO (PortAudio real-time thread)                  │
        │  REGLAS: sin I/O de disco, sin malloc pesado, sin GIL largo  │
        │  Solo lectura de RAM (ctx.data) y contabilidad de posición   │
        └──────────────────────────────────────────────────────────────┘
        """
        def callback(
            outdata:   np.ndarray,
            frames:    int,
            time_info: object,          # pa_stream_callback_time_info
            status:    sd.CallbackFlags,
        ) -> None:
            # ── Medición de latencia (una sola vez, en el primer frame) ──
            if not ctx.first_cb_done:
                ctx.latency_ms    = (time.perf_counter() - ctx.t_start) * 1_000
                ctx.first_cb_done = True
                # emit() desde el hilo de audio es seguro: Qt detecta
                # el cruce de hilos y encola el evento al main thread.
                self.latency_report.emit(ctx.cue_id, ctx.latency_ms)

            # ── Copia de frames desde el buffer en RAM ──────────────────
            remaining: int = len(ctx.data) - ctx.position
            if remaining <= 0:
                outdata[:] = 0.0
                raise sd.CallbackStop()

            chunk: int = min(frames, remaining)
            outdata[:chunk] = ctx.data[ctx.position : ctx.position + chunk]
            if chunk < frames:
                outdata[chunk:] = 0.0  # Rellenar silencio al final

            ctx.position += chunk

            # ── RMS para VU meter (throttle: 1 emit cada 4 callbacks ≈ 20 Hz) ──
            # np.dot sobre outdata[:chunk] es O(N) puro, sin alloc. Seguro en RT.
            ctx.level_counter += 1
            if ctx.level_counter >= 4:
                ctx.level_counter = 0
                flat = outdata[:chunk].ravel()
                n    = len(flat)
                rms  = float(np.dot(flat, flat) / n) ** 0.5 if n else 0.0
                self.level_report.emit(rms)

            if ctx.position >= len(ctx.data):
                raise sd.CallbackStop()

        return callback

    def _on_stream_finished(self) -> None:
        """Callback de fin de stream — llamado por sounddevice (hilo de audio).

        IMPORTANTE: Este método se ejecuta en el hilo de audio de PortAudio,
        NO en el main thread. Las emisiones de señal Qt serán encoladas
        automáticamente y entregadas en el main thread de forma segura.
        """
        with self._ctx_lock:
            ctx      = self._ctx
            self._ctx = None

        if ctx is not None:
            # Actualizar el dict de estados directamente; GIL protege la
            # asignación simple. La señal llegará al main thread encolada.
            self._states[ctx.cue_id] = "STOPPED"
            self.state_changed.emit(ctx.cue_id, "STOPPED")

    def _close_active_stream(self) -> None:
        """Cierra el stream activo de forma segura, ignorando errores."""
        stream = self._active_stream
        self._active_stream = None
        if stream is not None:
            try:
                stream.stop(ignore_errors=True)
                stream.close(ignore_errors=True)
            except Exception:
                pass

    # ------------------------------------------------------------------
    # Helpers de estado
    # ------------------------------------------------------------------

    def _set_state(self, cue_id: int, new_state: str) -> None:
        """Actualiza el estado interno y emite state_changed."""
        self._states[cue_id] = new_state
        self.state_changed.emit(cue_id, new_state)

    def _emit_error(self, cue_id: int, message: str) -> None:
        """Transiciona el cue a ERROR y emite error_occurred."""
        self._states[cue_id] = "ERROR"
        self.state_changed.emit(cue_id, "ERROR")
        self.error_occurred.emit(cue_id, message)
