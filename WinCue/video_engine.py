# =============================================================================
# video_engine.py — Motor de Vídeo con python-mpv (Paso 3)
# =============================================================================
#
# ┌─────────────────────────────────────────────────────────────────────────┐
# │  DIAGRAMA: Qt Widget ↔ mpv ↔ Win32                                      │
# │                                                                         │
# │  QMainWindow                                                            │
# │  └─ central QWidget                                                     │
# │     └─ content QWidget (QHBoxLayout)                                    │
# │        └─ VideoContainer (QWidget, WA_NativeWindow=True)                │
# │              │                                                          │
# │              │ winId() → HWND  ← ctypes si falla                       │
# │              ▼                                                          │
# │           Win32 HWND ─────────────────────────────────► mpv.MPV(wid=…) │
# │                                                             │           │
# │                                                         vo='gpu'        │
# │                                                    hwdec='auto'         │
# │                                                   keepaspect='yes'      │
# │                                                    keep-open='yes'      │
# │                                                       │                 │
# │                                                 libmpv (mpv-2.dll)      │
# │                                                GPU decoder → pantalla   │
# └─────────────────────────────────────────────────────────────────────────┘
#
# ORDEN CRÍTICO DE INICIALIZACIÓN
# ───────────────────────────────
# 1. VideoContainer se CONSTRUYE (QWidget existe, pero sin HWND real aún)
# 2. Se añade al layout de MainWindow
# 3. window.show() → Qt asigna HWND nativo a todos los widgets con
#    WA_NativeWindow=True. A partir de aquí winId() devuelve un handle válido.
# 4. En play(): se llama get_hwnd() → HWND válido → MPV(wid=…)
#
# Si pasamos el wid ANTES de show(), winId() puede devolver 0 o el HWND
# del ancestro más cercano, haciendo que mpv abra su propia ventana flotante.
#
# MODELO DE HILOS
# ───────────────
# · Main thread : toda la API pública de VideoEngine, construcción de MPV,
#                 métodos play/stop/preload_next, VideoContainer.
# · QThread (_FileVerifierWorker) : solo verifica que el archivo existe y
#   tiene formato soportado. Sin I/O de vídeo, sin MPV.
# · mpv internal thread : decodificación, renderizado, event loop de mpv.
#   Emite señales Qt desde ahí → queued delivery al main thread.
#   NUNCA tocar widgets Qt desde este hilo.
#
# MODO DEGRADADO
# ──────────────
# Si libmpv-2.dll no está en PATH, MPV_AVAILABLE = False.
# Todos los cues VIDEO pasan a ERROR con mensaje informativo.
# El resto del programa (AUDIO, WAIT, SCRIPT) sigue operativo.
# =============================================================================

from __future__ import annotations

import math
import time
from pathlib import Path
from typing import Optional

from PySide6.QtCore import QObject, QSize, QThread, QTimer, Qt, Signal
from PySide6.QtGui import QColor, QPainter, QResizeEvent
from PySide6.QtWidgets import QSizePolicy, QWidget

# ---------------------------------------------------------------------------
# Importación condicional de mpv — modo degradado si no hay libmpv
# ---------------------------------------------------------------------------

try:
    import mpv as _mpv_module
    MPV_AVAILABLE: bool = True
except (ImportError, OSError) as _mpv_exc:
    _mpv_module = None          # type: ignore[assignment]
    MPV_AVAILABLE = False
    print(
        f"[VIDEO] IMPORT mpv FALLÓ: {type(_mpv_exc).__name__}: {_mpv_exc}",
        flush=True,
    )

_MPV_NOT_FOUND_MSG: str = (
    "libmpv no encontrado. Instala python-mpv y coloca mpv-2.dll en PATH. "
    "El resto del sistema (AUDIO, WAIT, SCRIPT) sigue operativo."
)

# Formatos de imagen (mpv los muestra como fotogramas fijos)
IMAGE_FORMATS: frozenset[str] = frozenset({
    ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp",
})

# Formatos de vídeo soportados (extensiones en minúsculas) + imágenes
SUPPORTED_FORMATS: frozenset[str] = frozenset({
    ".mp4", ".mov", ".avi", ".mkv", ".webm",
    ".prores", ".mxf", ".m4v", ".ts", ".mts",
}) | IMAGE_FORMATS

# Intentos de salida de vídeo en orden de prioridad
_VO_FALLBACKS: tuple[str, ...] = ("gpu", "gpu-next", "null")


# ---------------------------------------------------------------------------
# VideoContainer — QWidget nativo para embedding de mpv
# ---------------------------------------------------------------------------

class VideoContainer(QWidget):
    """Widget de renderizado de vídeo.

    · WA_NativeWindow garantiza un HWND propio (no compartido con el padre).
    · WA_OpaquePaintEvent evita que Qt limpie el área antes de que mpv pinte.
    · hasHeightForWidth / heightForWidth mantiene el aspect ratio 16:9 en el
      sistema de layouts de Qt.
    · paintEvent pinta negro cuando mpv no está renderizando (IDLE/STOPPED).

    IMPORTANTE: get_hwnd() solo devuelve un handle válido DESPUÉS de que
    la ventana principal haya recibido show(). Llamarlo antes da HWND=0.
    """

    def __init__(self, parent: Optional[QWidget] = None) -> None:
        super().__init__(parent)

        # WA_NativeWindow: fuerza un HWND propio en Windows — OBLIGATORIO
        # para que mpv no adopte el HWND del ancestro QMainWindow.
        self.setAttribute(Qt.WidgetAttribute.WA_NativeWindow, True)
        self.setAttribute(
            Qt.WidgetAttribute.WA_DontCreateNativeAncestors, True)
        # WA_NoSystemBackground + WA_OpaquePaintEvent: impiden que Qt pinte
        # el fondo del sistema sobre el HWND donde mpv renderiza.
        # En Windows/DWM NO usar WA_PaintOnScreen — causa rayas blancas.
        self.setAttribute(Qt.WidgetAttribute.WA_NoSystemBackground, True)
        self.setAttribute(Qt.WidgetAttribute.WA_OpaquePaintEvent, True)

        self.setMinimumSize(320, 180)

        # Política de tamaño que respeta heightForWidth (16:9)
        sp = QSizePolicy(
            QSizePolicy.Policy.Expanding,
            QSizePolicy.Policy.Expanding,
        )
        sp.setHeightForWidth(True)
        self.setSizePolicy(sp)

    # ------------------------------------------------------------------
    # Aspect ratio 16:9
    # ------------------------------------------------------------------

    def hasHeightForWidth(self) -> bool:
        return True

    def heightForWidth(self, width: int) -> int:
        """Devuelve la altura 16:9 para el ancho dado."""
        return width * 9 // 16

    def sizeHint(self) -> QSize:
        return QSize(640, 360)

    def resizeEvent(self, event: QResizeEvent) -> None:
        super().resizeEvent(event)

    def paintEvent(self, event) -> None:
        """Rellena negro antes de que mpv tome el HWND.
        Una vez que mpv presenta frames vía DXGI/D3D, su swapchain tiene
        prioridad sobre la capa GDI en DWM y los fotogramas son visibles.
        Con WA_OpaquePaintEvent Qt no compuesta el fondo del padre encima."""
        painter = QPainter(self)
        painter.fillRect(event.rect(), QColor(0, 0, 0))

    # ------------------------------------------------------------------
    # HWND — interfaz con Win32
    # ------------------------------------------------------------------

    def get_hwnd(self) -> int:
        """Devuelve el HWND nativo para pasarlo a MPV(wid=…).

        Con WA_NativeWindow + WA_DontCreateNativeAncestors, winId() garantiza
        un HWND propio del VideoContainer. La primera llamada fuerza la
        creación del handle nativo si aún no existía.
        """
        self.winId()              # fuerza creación del HWND nativo
        return int(self.winId())

    def clear(self) -> None:
        """Muestra pantalla negra liberando el estado visual anterior.

        Seguro de llamar aunque no haya player activo.
        """
        self.update()   # Fuerza repintado con paintEvent → negro


# ---------------------------------------------------------------------------
# Worker de verificación de archivo — vive en un QThread
# ---------------------------------------------------------------------------

class _FileVerifierWorker(QObject):
    """Verifica existencia, formato y acceso de lectura en hilo secundario.

    Sin creación de MPV. Sin I/O de vídeo. Solo stat + open mínimo.
    """

    verified: Signal = Signal(int, str)   # (cue_id, filepath)
    failed:   Signal = Signal(int, str)   # (cue_id, error_message)

    def __init__(self, cue_id: int, file_path: str) -> None:
        super().__init__()
        self._cue_id:    int = cue_id
        self._file_path: str = file_path

    def run(self) -> None:
        """Ejecutado en el QThread verificador. Solo operaciones de sistema."""
        print(f"[VIDEO] verifier worker ENTER cue_id={self._cue_id}", flush=True)
        try:
            p = Path(self._file_path)

            if not p.exists():
                raise FileNotFoundError(f"Archivo no encontrado: {self._file_path}")

            if p.suffix.lower() not in SUPPORTED_FORMATS:
                raise ValueError(
                    f"Formato '{p.suffix}' no soportado. "
                    f"Soportados: {', '.join(sorted(SUPPORTED_FORMATS))}"
                )

            # Caso 4 del spec: archivo en uso por otro proceso en Windows
            try:
                with open(self._file_path, "rb") as fh:
                    fh.read(16)  # Lectura mínima para detectar bloqueo
            except PermissionError:
                raise PermissionError(
                    f"Archivo en uso por otro proceso: {self._file_path}"
                )

            print(f"[VIDEO] verifier worker EMIT verified cue_id={self._cue_id}", flush=True)
            self.verified.emit(self._cue_id, self._file_path)

        except Exception as exc:
            print(f"[VIDEO] verifier worker EMIT failed cue_id={self._cue_id} err={exc}", flush=True)
            self.failed.emit(self._cue_id, str(exc))


# ---------------------------------------------------------------------------
# VideoEngine — motor principal
# ---------------------------------------------------------------------------

class VideoEngine(QObject):
    """Motor de vídeo con máquina de estados por cue y embedding mpv/Qt.

    Máquina de estados por cue:
      IDLE → LOADING → LOADED → PLAYING → STOPPED → ERROR
      LOADING (con play_when_ready) = internamente "WAITING_PLAY"
      → cuando termina la carga, el engine hace play() automáticamente.

    Estado WAITING_PLAY es interno: el engine emite "LOADING" al modelo
    (estado válido en CueModel) y ejecuta play() en cuanto LOADING termina.
    """

    # ------------------------------------------------------------------
    # Señales Qt — entregadas siempre en el main thread (queued si vienen
    # de mpv internal thread o del QThread verificador)
    # ------------------------------------------------------------------

    state_changed:  Signal = Signal(int, str)    # (cue_id, new_state)
    latency_report: Signal = Signal(int, float)  # (cue_id, ms)
    error_occurred: Signal = Signal(int, str)    # (cue_id, message)
    frame_ready:    Signal = Signal(int)          # (cue_id) primer frame visible

    # ------------------------------------------------------------------
    # Construcción
    # ------------------------------------------------------------------

    def __init__(self, container: VideoContainer) -> None:
        super().__init__()
        self._container: VideoContainer = container

        # cue_id → estado del engine (string interno)
        self._states:  dict[int, str]       = {}

        # cue_id → filepath verificado y listo para play
        self._files:   dict[int, str]       = {}

        # cue_id → instancia mpv.MPV activa
        self._players: dict[int, object]    = {}   # type: ignore[type-arg]

        # cue_id → True si debe reproducir al terminar LOADING
        self._play_when_ready: dict[int, bool] = {}

        # cue_id → (QThread, _FileVerifierWorker) en vuelo
        self._loaders: dict[int, tuple[QThread, _FileVerifierWorker]] = {}

        # cue_id → parámetros de reproducción (volume 0..1, loop) pendientes
        self._play_params: dict[int, dict] = {}

        # cue_id → volumen actual (0..1) para gestionar fades
        self._volumes: dict[int, float] = {}

        # cue_id → (QTimer, dict de estado) de un fade de volumen en curso
        self._fade_timers: dict[int, tuple[QTimer, dict]] = {}

        # Volumen master global (multiplicador sobre el volumen de cada player)
        self._master: float = 1.0

    # ------------------------------------------------------------------
    # API pública — llamar SOLO desde el main thread
    # ------------------------------------------------------------------

    def load(self, cue_id: int, file_path: str) -> None:
        print(f"[VIDEO] load() cue_id={cue_id} filepath={file_path}")
        """Inicia verificación asíncrona del archivo (transición → LOADING).

        Si libmpv no está disponible, transiciona a ERROR inmediatamente.
        Si el cue ya está en LOADING o LOADED, no hace nada.
        """
        if not MPV_AVAILABLE:
            print(f"[VIDEO] load() → estado ERROR (mpv no disponible)")
            self._emit_error(cue_id, _MPV_NOT_FOUND_MSG)
            return

        if not file_path:
            print(f"[VIDEO] load() → estado ERROR (filepath vacío)")
            self._emit_error(cue_id, "Ruta de archivo vacía.")
            return

        state = self._states.get(cue_id, "IDLE")
        if state in ("LOADING", "LOADED", "PLAYING"):
            print(f"[VIDEO] load() omitido, estado actual={state}")
            return

        print(f"[VIDEO] load() verificando archivo...")
        self._set_state(cue_id, "LOADING")
        self._play_when_ready[cue_id] = False
        self._launch_verifier(cue_id, file_path)

    def get_state(self, cue_id: int) -> str:
        """Devuelve el estado interno del cue (IDLE si no conocido)."""
        state = self._states.get(cue_id, "IDLE")
        print(f"[VIDEO] get_state() cue_id={cue_id} → {state}")
        return state

    def set_container(self, container: "VideoContainer") -> None:
        """Cambia el contenedor de renderizado (p.ej. ventana de salida en otro
        monitor). Solo afecta a los vídeos que se inicien a partir de ahora."""
        if container is not None and container is not self._container:
            print(f"[VIDEO] set_container → nuevo HWND objetivo", flush=True)
            self._container = container

    def play(
        self,
        cue_id: int,
        file_path: str,
        volume: float = 1.0,
        loop: bool = False,
        scale: float = 100.0,
        trim_start: float = 0.0,
        trim_end: float = 0.0,
        rate: float = 1.0,
        play_count: int = 1,
        preserve_pitch: bool = True,
        geometry: dict = None,
    ) -> None:
        """Reproduce el cue. Autosuficiente: gestiona verificación interna si hace falta.

        Flujo por estado:
          PLAYING         → no-op (ya reproduciendo)
          LOADING         → activa play_when_ready; el verifier ya está en vuelo
          LOADED+caché    → reproducción directa sin I/O (caso pre-carga)
          IDLE/STOPPED/ERROR/LOADED-sin-caché → cancela verifier colgado,
                            re-verifica el archivo y reproduce al terminar
        """
        import traceback as _tb

        # Guardar parámetros de reproducción para _start_playback
        self._play_params[cue_id] = {
            "volume":     max(0.0, min(1.0, float(volume))),
            "loop":       bool(loop),
            "scale":      max(1.0, float(scale)),
            "trim_start": max(0.0, float(trim_start)),
            "trim_end":   max(0.0, float(trim_end)),
            "rate":       max(0.1, float(rate)),
            "play_count": max(1, int(play_count)),
            "preserve_pitch": bool(preserve_pitch),
            "geometry":   geometry or {},
        }

        try:
            state = self._states.get(cue_id, "IDLE")
            print(
                f"[VIDEO] play() → cue_id={cue_id}  state={state!r}"
                f"  type={type(state).__name__}",
                flush=True,
            )

            # ── Guards previos al árbol de estados ──────────────────────
            if not MPV_AVAILABLE:
                print(f"[VIDEO] play() rama=MPV_NO_DISPONIBLE cue_id={cue_id}", flush=True)
                self._emit_error(cue_id, _MPV_NOT_FOUND_MSG)
                return

            print(f"[VIDEO] play() MPV_AVAILABLE=True  file_path={file_path!r}", flush=True)

            if not file_path:
                print(f"[VIDEO] play() rama=FILE_PATH_VACIO cue_id={cue_id}", flush=True)
                self._emit_error(cue_id, "No hay archivo de vídeo asignado a este cue.")
                return

            # ── Árbol de estados ─────────────────────────────────────────
            if state == "PLAYING":
                print(f"[VIDEO] play() rama=PLAYING cue_id={cue_id}", flush=True)
                return

            if state == "LOADING":
                print(f"[VIDEO] play() rama=LOADING cue_id={cue_id}", flush=True)
                self._play_when_ready[cue_id] = True
                return

            if state == "LOADED":
                cached = cue_id in self._files
                print(f"[VIDEO] play() rama=LOADED cue_id={cue_id} cached={cached}", flush=True)
                if cached:
                    self._start_playback(cue_id)
                    return
                # LOADED pero sin archivo en caché → cae al bloque de re-verificación

            # IDLE / STOPPED / ERROR / LOADED-sin-caché
            print(
                f"[VIDEO] play() rama=ELSE (IDLE/STOPPED/ERROR) cue_id={cue_id}"
                f"  state_repr={state!r}",
                flush=True,
            )
            self._cleanup_verifier(cue_id)
            self._set_state(cue_id, "LOADING")
            print(f"[VIDEO] state set to LOADING, llamando _launch_verifier", flush=True)
            self._play_when_ready[cue_id] = True
            self._launch_verifier(cue_id, file_path)

        except Exception:
            print(f"[VIDEO] play() EXCEPCIÓN cue_id={cue_id}", flush=True)
            _tb.print_exc()
            raise

    def stop(self, cue_id: int) -> None:
        """Detiene la reproducción del cue y congela el último frame.

        Usa player.command('stop') — el único método garantizado en todas las
        versiones de python-mpv para detener el playback sin destruir el player.
        Con keep-open=yes el último frame queda visible en el widget.
        Si el cue no está en PLAYING, la llamada es inocua.
        """
        print(
            f"[VIDEO] stop() ENTER cue_id={cue_id}"
            f"  known_players={list(self._players.keys())}",
            flush=True,
        )
        self._cancel_fade(cue_id)
        had_player = cue_id in self._players
        # TERMINAR el player (no solo command('stop')): con keep-open el último
        # frame quedaba congelado y superpuesto al siguiente vídeo. Al terminar
        # el player y limpiar el contenedor a negro, la salida queda limpia y
        # se garantiza un único vídeo activo por contenedor.
        self._terminate_player(cue_id)
        if had_player:
            self._container.clear()   # pinta negro
        self._set_state(cue_id, "STOPPED")
        print(f"[VIDEO] stop() OK cue_id={cue_id} (player terminado, negro)", flush=True)

    def preload_next(self, cue_id: int, file_path: str) -> None:
        """Pre-verifica el siguiente cue mientras el actual está en PLAYING.

        Solo hace la verificación de archivo en QThread (sin crear MPV aún).
        Cuando el operador pulse GO, play() encontrará el estado LOADED y
        creará la instancia MPV sin demora de I/O.
        """
        self.load(cue_id, file_path)

    @staticmethod
    def list_supported_formats() -> list[str]:
        """Devuelve las extensiones de vídeo soportadas."""
        return sorted(SUPPORTED_FORMATS)

    # ------------------------------------------------------------------
    # Volumen / fade / pausa / stop — control estilo QLab
    # ------------------------------------------------------------------

    def is_playing(self, cue_id: int) -> bool:
        return self._states.get(cue_id) == "PLAYING" and cue_id in self._players

    def get_duration(self, cue_id: int) -> float:
        """Duración total del vídeo en segundos (0 si mpv aún no la conoce)."""
        player = self._players.get(cue_id)
        if player is None:
            return 0.0
        try:
            d = player.duration            # type: ignore[union-attr]
            return float(d) if d else 0.0
        except Exception:
            return 0.0

    def get_elapsed(self, cue_id: int) -> float:
        """Segundos reproducidos del vídeo (time-pos de mpv)."""
        player = self._players.get(cue_id)
        if player is None:
            return 0.0
        try:
            t = player.time_pos            # type: ignore[union-attr]
            return float(t) if t else 0.0
        except Exception:
            return 0.0

    def any_playing(self) -> bool:
        return any(s == "PLAYING" for s in self._states.values())

    def apply_geometry(self, cue_id: int, geo: dict) -> None:
        """Aplica la geometría a un vídeo EN REPRODUCCIÓN (en vivo)."""
        player = self._players.get(cue_id)
        if player is not None:
            self._apply_geo(player, geo)

    def _apply_geo(self, player, geo: dict) -> None:
        """Fija en mpv la parte factible de Display & Geometry."""
        try:
            mode = geo.get("mode", "FULL")
            if mode == "CUSTOM":
                sx = float(geo.get("scale_x", 1.0))
                half_w = max(1.0, float(geo.get("surf_w", 1920)) / 2.0)
                half_h = max(1.0, float(geo.get("surf_h", 1080)) / 2.0)
                player["keepaspect"] = "yes"                    # type: ignore[index]
                player["video-zoom"] = math.log2(max(0.01, sx)) # type: ignore[index]
                player["video-pan-x"] = float(geo.get("translate_x", 0.0)) / half_w   # type: ignore[index]
                player["video-pan-y"] = -float(geo.get("translate_y", 0.0)) / half_h  # type: ignore[index]
                player["video-rotate"] = int(round(float(geo.get("rotate_z", 0.0)) / 90.0) * 90) % 360  # type: ignore[index]
            else:  # FULL surface
                player["keepaspect"] = "yes" if geo.get("preserve_aspect", True) else "no"  # type: ignore[index]
                player["video-zoom"] = 0.0                      # type: ignore[index]
                player["video-pan-x"] = 0.0                     # type: ignore[index]
                player["video-pan-y"] = 0.0                     # type: ignore[index]
                player["video-rotate"] = 0                      # type: ignore[index]
        except Exception as exc:
            print(f"[VIDEO] _apply_geo error: {exc}", flush=True)

    def set_master_volume(self, volume: float) -> None:
        """Volumen master global (0..1); reajusta todos los players activos."""
        self._master = max(0.0, min(1.0, float(volume)))
        for cue_id, player in self._players.items():
            try:
                player.volume = self._volumes.get(cue_id, 1.0) * 100.0 * self._master  # type: ignore[union-attr]
            except Exception:
                pass

    def set_volume(self, cue_id: int, volume: float) -> None:
        """Fija el volumen (0..1) de un vídeo en reproducción, si existe."""
        v = max(0.0, min(1.0, float(volume)))
        self._volumes[cue_id] = v
        player = self._players.get(cue_id)
        if player is not None:
            try:
                player.volume = v * 100.0 * self._master   # type: ignore[union-attr]
            except Exception:
                pass

    def fade_volume(
        self,
        cue_id: int,
        target: float,
        duration: float,
        stop_after: bool = False,
    ) -> bool:
        """Funde el volumen del vídeo hacia `target` (0..1) en `duration` s.

        Implementado con un QTimer que actualiza la propiedad `volume` de mpv.
        Devuelve True si el cue estaba en reproducción.
        """
        if cue_id not in self._players:
            return False

        self._cancel_fade(cue_id)

        target = max(0.0, min(1.0, float(target)))
        start = self._volumes.get(cue_id, 1.0)

        if duration <= 0.0:
            self.set_volume(cue_id, target)
            if stop_after:
                self.stop(cue_id)
            return True

        interval_ms = 30
        steps = max(1, int((duration * 1000) / interval_ms))
        state = {"i": 0, "steps": steps, "start": start, "target": target,
                 "stop_after": stop_after}

        timer = QTimer(self)
        timer.setInterval(interval_ms)

        def _step() -> None:
            state["i"] += 1
            frac = min(1.0, state["i"] / state["steps"])
            vol = state["start"] + (state["target"] - state["start"]) * frac
            self.set_volume(cue_id, vol)
            if state["i"] >= state["steps"]:
                self._cancel_fade(cue_id)
                if state["stop_after"]:
                    self.stop(cue_id)

        timer.timeout.connect(_step)
        self._fade_timers[cue_id] = (timer, state)
        timer.start()
        return True

    def _cancel_fade(self, cue_id: int) -> None:
        entry = self._fade_timers.pop(cue_id, None)
        if entry is not None:
            timer, _ = entry
            timer.stop()
            timer.deleteLater()

    def pause_all(self) -> None:
        """Pausa todos los vídeos en reproducción."""
        for player in self._players.values():
            try:
                player.pause = True   # type: ignore[union-attr]
            except Exception:
                pass

    def resume_all(self) -> None:
        """Reanuda todos los vídeos pausados."""
        for cue_id, player in self._players.items():
            if self._states.get(cue_id) == "PLAYING":
                try:
                    player.pause = False   # type: ignore[union-attr]
                except Exception:
                    pass

    def stop_all(self) -> None:
        """Detiene todos los vídeos en reproducción."""
        for cue_id in list(self._players.keys()):
            if self._states.get(cue_id) == "PLAYING":
                self.stop(cue_id)

    def cleanup(self) -> None:
        """Termina todos los players y loaders (al salir de la aplicación)."""
        for cue_id in list(self._fade_timers.keys()):
            self._cancel_fade(cue_id)
        for cue_id in list(self._players.keys()):
            self._terminate_player(cue_id)
        for cue_id in list(self._loaders.keys()):
            self._cleanup_verifier(cue_id)

    # ------------------------------------------------------------------
    # Lanzamiento y limpieza de verificadores
    # ------------------------------------------------------------------

    def _launch_verifier(self, cue_id: int, file_path: str) -> None:
        """Crea QThread + _FileVerifierWorker para verificar el archivo."""
        print(f"[VIDEO] _launch_verifier ENTER cue_id={cue_id} path={file_path}", flush=True)
        self._cleanup_verifier(cue_id)

        worker = _FileVerifierWorker(cue_id, file_path)
        thread = QThread(self)
        worker.moveToThread(thread)

        worker.verified.connect(self._on_file_verified)
        worker.failed.connect(self._on_file_failed)

        thread.started.connect(worker.run)
        worker.verified.connect(thread.quit)
        worker.failed.connect(thread.quit)
        thread.finished.connect(thread.deleteLater)

        self._loaders[cue_id] = (thread, worker)
        thread.start()
        print(f"[VIDEO] verifier thread arrancado cue_id={cue_id}", flush=True)

    def _cleanup_verifier(self, cue_id: int) -> None:
        entry = self._loaders.pop(cue_id, None)
        if entry is not None:
            thread, _ = entry
            if thread.isRunning():
                thread.quit()
                thread.wait(2_000)

    # ------------------------------------------------------------------
    # Slots privados — verificador (entregados en main thread)
    # ------------------------------------------------------------------

    def _on_file_verified(self, cue_id: int, file_path: str) -> None:
        """Archivo verificado OK. Guarda ruta y transiciona a LOADED."""
        print(f"[VIDEO] _on_file_verified RECIBIDO cue_id={cue_id} path={file_path}", flush=True)
        self._loaders.pop(cue_id, None)
        self._files[cue_id] = file_path
        self._set_state(cue_id, "LOADED")

        # Caso 5 del spec: si estaba marcado WAITING_PLAY, reproducir ahora
        will_play = self._play_when_ready.pop(cue_id, False)
        print(f"[VIDEO] _on_file_verified play_when_ready={will_play}", flush=True)
        if will_play:
            print(f"[VIDEO] llamando _start_playback cue_id={cue_id}", flush=True)
            self._start_playback(cue_id)

    def _on_file_failed(self, cue_id: int, message: str) -> None:
        """Error de verificación. Transiciona a ERROR."""
        print(f"[VIDEO] _on_file_failed RECIBIDO cue_id={cue_id} err={message}", flush=True)
        self._loaders.pop(cue_id, None)
        self._play_when_ready.pop(cue_id, None)
        self._emit_error(cue_id, f"Error al verificar vídeo: {message}")

    # ------------------------------------------------------------------
    # Reproducción — creación de MPV e inicio (main thread OBLIGATORIO)
    # ------------------------------------------------------------------

    def _start_playback(self, cue_id: int) -> None:
        """Crea instancia MPV, asigna HWND y arranca reproducción.

        Llamar SOLO desde el main thread y SOLO después de window.show().
        """
        print(f"[VIDEO] _start_playback ENTER cue_id={cue_id}", flush=True)
        file_path = self._files.get(cue_id)
        if not file_path:
            self._emit_error(cue_id, "Archivo no está en caché. Llama a load() primero.")
            return

        # ── Validación del HWND ──────────────────────────────────────────
        # Caso 1 del spec: HWND inválido
        hwnd: int = self._container.get_hwnd()
        if hwnd == 0:
            self._emit_error(
                cue_id,
                "VideoContainer handle no disponible. "
                "Asegúrate de que el widget es visible.",
            )
            return

        # ── Salida única: terminar CUALQUIER otro player ────────────────
        # Un solo contenedor de vídeo = un solo mpv activo. Si quedara otro
        # player (p.ej. congelado), terminarlo evita la superposición de dos
        # vídeos sobre el mismo HWND.
        for other_id in list(self._players.keys()):
            if other_id != cue_id:
                print(f"[VIDEO] salida única → terminando player huérfano cue_id={other_id}", flush=True)
                self._terminate_player(other_id)

        # ── Cerrar player anterior de este cue si existía ───────────────
        self._terminate_player(cue_id)

        # ── Crear instancia MPV con vo fallback ──────────────────────────
        # Caso 3 del spec: GPU no disponible → vo fallback chain
        wid_str: str = str(hwnd)
        print(f"[DEBUG] mpv wid = {wid_str}", flush=True)

        player = None
        used_vo: str = "gpu"
        for vo_candidate in _VO_FALLBACKS:
            try:
                print(f"[VIDEO] Intentando mpv vo={vo_candidate} wid={wid_str}", flush=True)
                player = _mpv_module.MPV(               # type: ignore[union-attr]
                    wid=wid_str,
                    vo=vo_candidate,
                    hwdec="auto",
                    keep_open="yes",
                    hr_seek="yes",
                    keepaspect="yes",
                    osc="no",
                    input_default_bindings="no",
                    input_vo_keyboard="no",
                    log_handler=lambda level, prefix, text: print(
                        f"[mpv {level}] {prefix}: {text}", flush=True
                    ),
                    loglevel="debug",
                )
                used_vo = vo_candidate
                print(f"[VIDEO] mpv instanciado OK con vo={used_vo}", flush=True)
                break
            except Exception as exc:
                print(f"[VIDEO] MPV falló con vo={vo_candidate}: {exc}", flush=True)
                player = None
                continue

        if player is None:
            self._emit_error(cue_id, "No se pudo inicializar ningún video output (gpu/gpu-next/null).")
            return

        # ── Readback de propiedades clave tras instanciar ────────────────
        try:
            print(f"[DEBUG] mpv vo (readback)    = {player.vo}", flush=True)          # type: ignore[union-attr]
            print(f"[DEBUG] mpv hwdec (readback) = {player.hwdec}", flush=True)       # type: ignore[union-attr]
            print(f"[DEBUG] mpv wid (readback)   = {player.wid}", flush=True)         # type: ignore[union-attr]
        except Exception as exc:
            print(f"[DEBUG] readback de propiedades falló: {exc}", flush=True)

        if used_vo != "gpu":
            _aviso = f"[AVISO] vo=gpu no disponible. Usando vo={used_vo}."
            print(f"[VIDEO] {_aviso}", flush=True)
            self.error_occurred.emit(cue_id, _aviso)

        # ── Registrar callbacks de eventos mpv ──────────────────────────
        # Los callbacks de mpv se ejecutan en el hilo interno de mpv.
        # emit() de señales Qt desde ahí es seguro (queued delivery).

        t_start: float = 0.0                          # se asigna justo antes de play()
        _latency_done: list[bool] = [False]           # one-shot flag thread-safe via GIL

        @player.event_callback("file-loaded")         # type: ignore[misc]
        def _on_file_loaded(event: object) -> None:
            print(f"[DEBUG] mpv file-loaded recibido (cue_id={cue_id})", flush=True)

        @player.event_callback("playback-restart")    # type: ignore[misc]
        def _on_first_frame(event: object) -> None:
            """Primer frame visible → medir latencia desde T0 (play())."""
            print(f"[DEBUG] mpv playback-restart recibido (primer frame)", flush=True)
            if not _latency_done[0]:
                _latency_done[0] = True
                latency_ms = (time.perf_counter() - t_start) * 1_000
                self.latency_report.emit(cue_id, latency_ms)
                self.frame_ready.emit(cue_id)
                _on_first_frame.unregister_mpv_events()     # type: ignore[attr-defined]

        @player.event_callback("end-file")             # type: ignore[misc]
        def _on_end_file(event: object) -> None:
            """Reproducción terminada (fin natural de archivo)."""
            print(f"[DEBUG] mpv end-file recibido (cue_id={cue_id})", flush=True)
            current = self._states.get(cue_id, "IDLE")
            if current == "PLAYING":
                self._states[cue_id] = "STOPPED"
                self.state_changed.emit(cue_id, "STOPPED")

        # ── Observer one-shot de time-pos para confirmar avance ──────────
        _time_pos_logged: list[bool] = [False]

        def _on_time_pos(name: str, value: object) -> None:
            if not _time_pos_logged[0] and isinstance(value, (int, float)) and value > 0:
                _time_pos_logged[0] = True
                print(f"[DEBUG] mpv time-pos = {value:.3f}s (mpv está avanzando)", flush=True)
                try:
                    player.unobserve_property(_on_time_pos)   # type: ignore[union-attr]
                except Exception:
                    pass

        player.observe_property("time-pos", _on_time_pos)    # type: ignore[union-attr]

        # ── Aplicar volumen, loop, escala, trim e imagen del cue ─────────
        params = self._play_params.get(cue_id, {})
        vol = float(params.get("volume", 1.0))
        loop = bool(params.get("loop", False))
        scale = float(params.get("scale", 100.0))
        trim_start = float(params.get("trim_start", 0.0))
        trim_end = float(params.get("trim_end", 0.0))
        rate = float(params.get("rate", 1.0))
        play_count = int(params.get("play_count", 1))
        preserve_pitch = bool(params.get("preserve_pitch", True))
        is_image = Path(file_path).suffix.lower() in IMAGE_FORMATS
        self._volumes[cue_id] = vol
        try:
            player.volume = max(0.0, min(100.0, vol * 100.0)) * self._master   # type: ignore[union-attr]
            # Loop infinito, o play_count repeticiones, o una sola vez
            if loop:
                player["loop-file"] = "inf"                     # type: ignore[index]
            elif play_count > 1:
                player["loop-file"] = play_count - 1            # type: ignore[index]
            else:
                player["loop-file"] = "no"                      # type: ignore[index]
            # Velocidad + corrección de tono
            if abs(rate - 1.0) > 0.001:
                player["speed"] = rate                          # type: ignore[index]
            player["audio-pitch-correction"] = preserve_pitch   # type: ignore[index]
            # Escala: mpv video-zoom es log2 (0 = nativo, 1 = 2×, -1 = ½×)
            if abs(scale - 100.0) > 0.01:
                player["video-zoom"] = math.log2(max(1.0, scale) / 100.0)  # type: ignore[index]
            # Recorte (in/out) dentro del archivo
            if trim_start > 0.0:
                player["start"] = trim_start                    # type: ignore[index]
            if trim_end > 0.0:
                player["end"] = trim_end                        # type: ignore[index]
            # Imagen fija: mantenerla en pantalla hasta que se pare/cambie
            if is_image:
                player["image-display-duration"] = "inf"        # type: ignore[index]

        except Exception as exc:
            print(f"[VIDEO] no se pudo fijar volume/loop/scale/trim: {exc}", flush=True)

        # ── Display & Geometry (se aplica también en vivo) ───────────────
        self._apply_geo(player, params.get("geometry") or {})

        # ── Iniciar reproducción — T0 justo antes de play() ─────────────
        self._players[cue_id] = player
        print(f"[DEBUG] container winId justo antes de play() = {int(self._container.winId())}", flush=True)
        t_start = time.perf_counter()                # T0 para latencia
        player.play(file_path)                       # type: ignore[union-attr]
        print(f"[VIDEO] player.play() llamado con {file_path}", flush=True)

        self._set_state(cue_id, "PLAYING")

    def _terminate_player(self, cue_id: int) -> None:
        """Cierra y libera el player de un cue de forma segura.

        Llamar command('stop') antes de terminate() para vaciar la playlist
        y evitar que mpv siga decodificando en background durante el shutdown.
        """
        player = self._players.pop(cue_id, None)
        if player is None:
            return
        try:
            player.command("stop")           # type: ignore[union-attr]
        except Exception:
            pass
        try:
            player.terminate()               # type: ignore[union-attr]
        except Exception:
            pass

    # ------------------------------------------------------------------
    # Helpers de estado
    # ------------------------------------------------------------------

    def _set_state(self, cue_id: int, new_state: str) -> None:
        self._states[cue_id] = new_state
        self.state_changed.emit(cue_id, new_state)

    def _emit_error(self, cue_id: int, message: str) -> None:
        self._states[cue_id] = "ERROR"
        self.state_changed.emit(cue_id, "ERROR")
        self.error_occurred.emit(cue_id, message)
